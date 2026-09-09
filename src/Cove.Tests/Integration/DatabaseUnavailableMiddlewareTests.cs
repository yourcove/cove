using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using Cove.Api.Middleware;
using Cove.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

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

    private sealed class ConnectionRefusedDbException : DbException
    {
        public ConnectionRefusedDbException()
            : base("Database connection refused.", new SocketException((int)SocketError.ConnectionRefused))
        {
        }
    }
}