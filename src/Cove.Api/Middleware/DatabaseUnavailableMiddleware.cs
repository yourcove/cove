using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using Cove.Api.Services;
using Cove.Data.Services;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Cove.Api.Middleware;

public sealed class DatabaseUnavailableMiddleware
{
    public const int RetryAfterSeconds = 5;

    /// <summary>
    /// Set on Cove's own 504 for a query timeout, so the UI can tell it from a proxy's 504 without reading
    /// the body: the server answered, so it is reachable.
    /// </summary>
    public const string ErrorCodeHeader = "X-Cove-Error-Code";
    public const string QueryTimeoutCode = "QUERY_TIMEOUT";
    private static readonly TimeSpan WarningLogInterval = TimeSpan.FromSeconds(15);
    private readonly RequestDelegate _next;
    private readonly object _warningLock = new();
    private DateTime _nextWarningAtUtc = DateTime.MinValue;
    private int _suppressedWarnings;

    public DatabaseUnavailableMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, MaintenanceState maintenance, ILogger<DatabaseUnavailableMiddleware> logger)
    {
        // A restore (or the first-boot baseline migration) builds the schema from nothing; don't let
        // requests run queries against the missing/half-built database (they'd 500 with "relation ...
        // does not exist"). Short-circuit to 503 so clients retry once it finishes. The request that
        // triggered a restore is already past this middleware, so it isn't affected.
        if (!context.Response.HasStarted)
        {
            if (maintenance.IsRestoreInProgress)
            {
                await WriteUnavailableAsync(context, CreateRestoreInProgressResponse());
                return;
            }

            if (maintenance.IsInitializing)
            {
                await WriteUnavailableAsync(context, CreateInitializingResponse());
                return;
            }
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (DatabaseUnavailableExceptionClassifier.IsCommandTimeout(ex))
        {
            if (context.Response.HasStarted)
                throw;

            // Not rate limited like the warning below: the point of the line is to name the slow endpoint, and
            // each one costs a full command timeout per attempt (EF retries a timed-out command, see #1026).
            // A server that stopped answering breaks the connection instead and takes the rate-limited path.
            logger.LogWarning("Database query timed out while handling {Method} {Endpoint} after {ElapsedSeconds:0.0} seconds.",
                context.Request.Method,
                DescribeEndpoint(context),
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
            logger.LogDebug(ex, "Database command timeout handled at request boundary.");

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            context.Response.Headers[ErrorCodeHeader] = QueryTimeoutCode;
            await context.Response.WriteAsJsonAsync(CreateQueryTimeoutResponse(), context.RequestAborted);
        }
        catch (Exception ex) when (DatabaseUnavailableExceptionClassifier.IsTransientDatabaseConnectionFailure(ex))
        {
            if (context.Response.HasStarted)
                throw;

            var reason = ex.GetBaseException().Message;
            var (shouldLogWarning, suppressedWarnings) = ShouldLogWarning();
            if (shouldLogWarning)
            {
                logger.LogWarning("Database temporarily unavailable while handling {Method} {Endpoint}: {Reason}. Suppressed {SuppressedCount} similar warning(s) in the last {IntervalSeconds} seconds.",
                    context.Request.Method,
                    DescribeEndpoint(context),
                    reason,
                    suppressedWarnings,
                    (int)WarningLogInterval.TotalSeconds);
            }
            logger.LogDebug(ex, "Transient database connection failure handled at request boundary.");

            await WriteUnavailableAsync(context, CreateResponse());
        }
    }

    /// <summary>
    /// The matched route template, such as <c>api/videos/{id}</c>, which names the endpoint and carries no
    /// request text. Without one, the escaped path: the decoded path could carry line breaks into the log.
    /// </summary>
    internal static string DescribeEndpoint(HttpContext context)
        => (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText is { Length: > 0 } pattern
            ? pattern
            : context.Request.Path.ToUriComponent();

    private static async Task WriteUnavailableAsync(HttpContext context, object payload)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(payload, context.RequestAborted);
    }

    private (bool ShouldLog, int SuppressedWarnings) ShouldLogWarning()
    {
        lock (_warningLock)
        {
            var now = DateTime.UtcNow;
            if (now < _nextWarningAtUtc)
            {
                _suppressedWarnings++;
                return (false, 0);
            }

            var suppressed = _suppressedWarnings;
            _suppressedWarnings = 0;
            _nextWarningAtUtc = now.Add(WarningLogInterval);
            return (true, suppressed);
        }
    }

    public static object CreateResponse() => new
    {
        code = "DATABASE_UNAVAILABLE",
        message = "The database is temporarily unavailable. Try again in a few seconds.",
    };

    public static object CreateQueryTimeoutResponse() => new
    {
        code = QueryTimeoutCode,
        message = "The database took too long to complete this request, so it was stopped.",
    };

    public static object CreateRestoreInProgressResponse() => new
    {
        code = "DATABASE_RESTORE_IN_PROGRESS",
        message = "A database restore is in progress. The server will be available again shortly.",
    };

    public static object CreateInitializingResponse() => new
    {
        code = "DATABASE_INITIALIZING",
        message = "The database is being initialized. The server will be available again shortly.",
    };
}

public static class DatabaseUnavailableExceptionClassifier
{
    // Npgsql has no exception type for a command that outlives its CommandTimeout. It throws an
    // NpgsqlException wrapping a TimeoutException with this message, and marks it transient. On a healthy
    // server it cancels the query and the connection stays usable; when the server stops answering it throws
    // the same exception but breaks the connection, which DatabaseConnectionFailure records. Timeouts while
    // opening a connection or waiting for a pooled one carry "Timeout during connection attempt" or
    // "The operation has timed out" instead.
    private const string NpgsqlCommandTimeoutMessage = "Timeout during reading attempt";

    public static bool IsTransientDatabaseConnectionFailure(Exception exception)
    {
        if (IsCommandTimeout(exception))
            return false;

        var sawDatabaseException = false;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException npgsqlException)
            {
                if (npgsqlException.IsTransient)
                    return true;

                sawDatabaseException = true;
            }
            else if (current is DbException)
            {
                sawDatabaseException = true;
            }

            if (sawDatabaseException && current is SocketException socketException && IsTransientSocketError(socketException.SocketErrorCode))
                return true;

            if (sawDatabaseException && current is TimeoutException)
                return true;

            if (sawDatabaseException && current is EndOfStreamException)
                return true;
        }

        return false;
    }

    public static bool IsCommandTimeout(Exception exception)
    {
        if (DatabaseConnectionFailure.ConnectionBroke(exception))
            return false;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { InnerException: TimeoutException { Message: NpgsqlCommandTimeoutMessage } })
                return true;
        }

        return false;
    }

    private static bool IsTransientSocketError(SocketError socketError) => socketError switch
    {
        SocketError.ConnectionAborted
            or SocketError.ConnectionRefused
            or SocketError.ConnectionReset
            or SocketError.HostDown
            or SocketError.HostNotFound
            or SocketError.NetworkDown
            or SocketError.NetworkUnreachable
            or SocketError.TimedOut
            or SocketError.TryAgain => true,
        _ => false,
    };
}