using System.Data.Common;
using System.Net.Sockets;
using Cove.Api.Services;
using Npgsql;

namespace Cove.Api.Middleware;

public sealed class DatabaseUnavailableMiddleware
{
    public const int RetryAfterSeconds = 5;
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

        try
        {
            await _next(context);
        }
        catch (Exception ex) when (DatabaseUnavailableExceptionClassifier.IsTransientDatabaseConnectionFailure(ex))
        {
            if (context.Response.HasStarted)
                throw;

            var reason = ex.GetBaseException().Message;
            var (shouldLogWarning, suppressedWarnings) = ShouldLogWarning();
            if (shouldLogWarning)
            {
                logger.LogWarning("Database temporarily unavailable while handling {Method} {Path}: {Reason}. Suppressed {SuppressedCount} similar warning(s) in the last {IntervalSeconds} seconds.",
                    context.Request.Method,
                    context.Request.Path.Value,
                    reason,
                    suppressedWarnings,
                    (int)WarningLogInterval.TotalSeconds);
            }
            logger.LogDebug(ex, "Transient database connection failure handled at request boundary.");

            await WriteUnavailableAsync(context, CreateResponse());
        }
    }

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
    public static bool IsTransientDatabaseConnectionFailure(Exception exception)
    {
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