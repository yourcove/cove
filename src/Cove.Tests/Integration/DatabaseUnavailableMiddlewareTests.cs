using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using Cove.Api.Middleware;
using Cove.Api.Services;
using Cove.Data.Services;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cove.Tests.Integration;

public sealed class DatabaseUnavailableMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_MapsTransientDatabaseConnectionFailure_ToServiceUnavailable()
    {
        var failure = new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.",
            new ConnectionRefusedDbException());
        var middleware = new DatabaseUnavailableMiddleware(_ => Task.FromException(failure));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, new MaintenanceState(), NullLogger<DatabaseUnavailableMiddleware>.Instance);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("DATABASE_UNAVAILABLE", body);
    }

    [Fact]
    public async Task InvokeAsync_MapsCommandTimeout_ToGatewayTimeoutWithoutRetryAfter()
    {
        // What EF's retrying execution strategy throws once every attempt hit the command timeout.
        var failure = new RetryLimitExceededException("The maximum number of retries (3) was exceeded.", NpgsqlCommandTimeout());
        var middleware = new DatabaseUnavailableMiddleware(_ => Task.FromException(failure));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, new MaintenanceState(), NullLogger<DatabaseUnavailableMiddleware>.Instance);

        Assert.Equal((int)HttpStatusCode.GatewayTimeout, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("QUERY_TIMEOUT", context.Response.Headers[DatabaseUnavailableMiddleware.ErrorCodeHeader]);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("QUERY_TIMEOUT", body);
    }

    [Fact]
    public async Task InvokeAsync_MapsATimeoutThatBrokeTheConnection_ToServiceUnavailable()
    {
        // A server that stops answering mid-command gives the same exception as a slow query, but breaks the
        // connection, which DatabaseConnectionFailureInterceptor records on the exception.
        var timeout = NpgsqlCommandTimeout();
        DatabaseConnectionFailure.MarkConnectionBroken(timeout);
        var failure = new RetryLimitExceededException("The maximum number of retries (3) was exceeded.", timeout);
        var middleware = new DatabaseUnavailableMiddleware(_ => Task.FromException(failure));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, new MaintenanceState(), NullLogger<DatabaseUnavailableMiddleware>.Instance);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter);
        Assert.False(context.Response.Headers.ContainsKey(DatabaseUnavailableMiddleware.ErrorCodeHeader));
    }

    [Fact]
    public async Task InvokeAsync_CommandTimeoutAfterTheResponseStarted_RethrowsAndLeavesTheResponseAlone()
    {
        var failure = NpgsqlCommandTimeout();
        var middleware = new DatabaseUnavailableMiddleware(_ => Task.FromException(failure));
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Response.StatusCode = StatusCodes.Status200OK;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => middleware.InvokeAsync(context, new MaintenanceState(), NullLogger<DatabaseUnavailableMiddleware>.Instance));

        Assert.Same(failure, thrown);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey(DatabaseUnavailableMiddleware.ErrorCodeHeader));
    }

    [Fact]
    public async Task InvokeAsync_CommandTimeout_LogsTheRouteTemplateRatherThanTheRequestPath()
    {
        var logger = new RecordingLogger();
        var middleware = new DatabaseUnavailableMiddleware(_ => Task.FromException(NpgsqlCommandTimeout()));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = "POST";
        context.Request.Path = "/api/videos/42\r\nforged line";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("api/videos/{id}"), 0, EndpointMetadataCollection.Empty, "videos"));

        await middleware.InvokeAsync(context, new MaintenanceState(), logger);

        var message = Assert.Single(logger.Messages, entry => entry.Level == LogLevel.Warning).Message;
        Assert.Contains("POST api/videos/{id}", message);
        Assert.DoesNotContain("forged", message);
    }

    [Fact]
    public void DescribeEndpoint_WithoutARoute_EscapesThePath()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/unknown\r\nforged line";

        var described = DatabaseUnavailableMiddleware.DescribeEndpoint(context);

        Assert.DoesNotContain('\r', described);
        Assert.DoesNotContain('\n', described);
        Assert.StartsWith("/api/unknown%0D%0A", described);
    }

    [Fact]
    public async Task InvokeAsync_LetsQueryCancellationPropagate()
    {
        var failure = new OperationCanceledException("Query was cancelled", new TimeoutException("Timeout during reading attempt"));
        var middleware = new DatabaseUnavailableMiddleware(_ => Task.FromException(failure));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => middleware.InvokeAsync(context, new MaintenanceState(), NullLogger<DatabaseUnavailableMiddleware>.Instance));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task InvokeAsync_WhileRestoreInProgress_ShortCircuitsToServiceUnavailableWithoutCallingNext()
    {
        var nextCalled = false;
        var middleware = new DatabaseUnavailableMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var maintenance = new MaintenanceState();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        using (maintenance.BeginRestore())
        {
            await middleware.InvokeAsync(context, maintenance, NullLogger<DatabaseUnavailableMiddleware>.Instance);
        }

        Assert.False(nextCalled); // the request must not reach DB-backed handlers mid-restore
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("DATABASE_RESTORE_IN_PROGRESS", body);
    }

    [Fact]
    public async Task InvokeAsync_WhileInitializing_ShortCircuitsToServiceUnavailableWithoutCallingNext()
    {
        var nextCalled = false;
        var middleware = new DatabaseUnavailableMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var maintenance = new MaintenanceState();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        using (maintenance.BeginInitialization())
        {
            await middleware.InvokeAsync(context, maintenance, NullLogger<DatabaseUnavailableMiddleware>.Instance);
        }

        Assert.False(nextCalled); // the request must not reach DB-backed handlers before the schema exists
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("DATABASE_INITIALIZING", body);
    }

    [Fact]
    public void MaintenanceState_BeginRestore_FlagClearsAfterDispose()
    {
        var maintenance = new MaintenanceState();
        Assert.False(maintenance.IsRestoreInProgress);

        var scope = maintenance.BeginRestore();
        Assert.True(maintenance.IsRestoreInProgress);

        scope.Dispose();
        Assert.False(maintenance.IsRestoreInProgress);
    }

    [Fact]
    public void MaintenanceState_BeginInitialization_FlagClearsAfterDispose()
    {
        var maintenance = new MaintenanceState();
        Assert.False(maintenance.IsInitializing);
        Assert.False(maintenance.IsSchemaUnavailable);

        var scope = maintenance.BeginInitialization();
        Assert.True(maintenance.IsInitializing);
        Assert.True(maintenance.IsSchemaUnavailable);

        scope.Dispose();
        Assert.False(maintenance.IsInitializing);
        Assert.False(maintenance.IsSchemaUnavailable);
    }

    [Fact]
    public void IsTransientDatabaseConnectionFailure_DoesNotClassifyPlainSocketFailure()
    {
        var exception = new SocketException((int)SocketError.ConnectionRefused);

        Assert.False(DatabaseUnavailableExceptionClassifier.IsTransientDatabaseConnectionFailure(exception));
    }

    [Fact]
    public void Classifier_TreatsCommandTimeoutAsTimeoutRatherThanUnavailable()
    {
        var exception = NpgsqlCommandTimeout();

        Assert.True(DatabaseUnavailableExceptionClassifier.IsCommandTimeout(exception));
        Assert.False(DatabaseUnavailableExceptionClassifier.IsTransientDatabaseConnectionFailure(exception));
    }

    public static TheoryData<Exception> ConnectionFailures() => new()
    {
        new NpgsqlException("Failed to connect to 127.0.0.1:5432", new SocketException((int)SocketError.ConnectionRefused)),
        new NpgsqlException("Failed to connect to 127.0.0.1:5432", new TimeoutException("Timeout during connection attempt")),
        // A server that accepts the TCP connection but never answers the startup handshake.
        new NpgsqlException("The operation has timed out", new TimeoutException("The operation has timed out.")),
        new NpgsqlException("The connection pool has been exhausted, either raise 'Max Pool Size' (currently 100) or 'Timeout' (currently 15 seconds) in your connection string.", new TimeoutException()),
        new RetryLimitExceededException("The maximum number of retries (3) was exceeded.",
            new NpgsqlException("Failed to connect to 127.0.0.1:5432", new SocketException((int)SocketError.ConnectionRefused))),
    };

    [Theory]
    [MemberData(nameof(ConnectionFailures))]
    public void Classifier_KeepsConnectionFailuresUnavailable(Exception exception)
    {
        Assert.True(DatabaseUnavailableExceptionClassifier.IsTransientDatabaseConnectionFailure(exception));
        Assert.False(DatabaseUnavailableExceptionClassifier.IsCommandTimeout(exception));
    }

    public static TheoryData<Exception> QueryCancellations() => new()
    {
        new OperationCanceledException("Query was cancelled",
            new PostgresException("canceling statement due to user request", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled)),
        new OperationCanceledException("Query was cancelled", new TimeoutException("Timeout during reading attempt")),
    };

    [Theory]
    [MemberData(nameof(QueryCancellations))]
    public void Classifier_LeavesQueryCancellationUnclassified(Exception exception)
    {
        Assert.False(DatabaseUnavailableExceptionClassifier.IsTransientDatabaseConnectionFailure(exception));
        Assert.False(DatabaseUnavailableExceptionClassifier.IsCommandTimeout(exception));
    }

    // Pins the classification to what the referenced Npgsql actually throws, since it keys off a message.
    [Fact]
    [RequiresPostgres]
    public async Task Classifier_RecognizesARealNpgsqlCommandTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { CommandTimeout = 1 }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_sleep(10)", connection);

        var exception = await Assert.ThrowsAnyAsync<NpgsqlException>(() => command.ExecuteNonQueryAsync(ct));

        Assert.True(DatabaseUnavailableExceptionClassifier.IsCommandTimeout(exception));
        Assert.False(DatabaseUnavailableExceptionClassifier.IsTransientDatabaseConnectionFailure(exception));
    }

    [Fact]
    [RequiresPostgres]
    public async Task ConnectionFailureInterceptor_RecordsABrokenConnectionButNotACommandTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);

        await using (var slow = new Cove.Data.CoveContext(database.CreateOptions(options => options.AddInterceptors(DatabaseConnectionFailureInterceptor.Instance))))
        {
            slow.Database.SetCommandTimeout(1);
            var timeout = await Assert.ThrowsAnyAsync<Exception>(() => slow.Database.ExecuteSqlRawAsync("SELECT pg_sleep(10)", ct));

            Assert.False(DatabaseConnectionFailure.ConnectionBroke(timeout));
            Assert.True(DatabaseUnavailableExceptionClassifier.IsCommandTimeout(timeout));
        }

        await using var broken = new Cove.Data.CoveContext(database.CreateOptions(options => options.AddInterceptors(DatabaseConnectionFailureInterceptor.Instance)));
        await broken.Database.OpenConnectionAsync(ct);
        var backendPid = ((NpgsqlConnection)broken.Database.GetDbConnection()).ProcessID;
        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin);
            terminate.Parameters.AddWithValue("pid", backendPid);
            await terminate.ExecuteScalarAsync(ct);
        }

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => broken.Database.ExecuteSqlRawAsync("SELECT 1", ct));

        Assert.True(DatabaseConnectionFailure.ConnectionBroke(failure));
        Assert.False(DatabaseUnavailableExceptionClassifier.IsCommandTimeout(failure));
    }

    private static NpgsqlException NpgsqlCommandTimeout()
        => new("Exception while reading from stream", new TimeoutException("Timeout during reading attempt"));

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class RecordingLogger : ILogger<DatabaseUnavailableMiddleware>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ConnectionRefusedDbException : DbException
    {
        public ConnectionRefusedDbException()
            : base("Database connection refused.", new SocketException((int)SocketError.ConnectionRefused))
        {
        }
    }
}