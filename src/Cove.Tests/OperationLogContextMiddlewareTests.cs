using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Cove.Api.Middleware;

namespace Cove.Tests;

public sealed class OperationLogContextMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AddsRequestTraceIdentifierToScope()
    {
        var logger = new ScopeRecordingLogger<OperationLogContextMiddleware>();
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "operation-123",
        };
        IReadOnlyDictionary<string, object?>? observedScope = null;
        var middleware = new OperationLogContextMiddleware(_ =>
        {
            observedScope = logger.CurrentScope;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, logger);

        Assert.NotNull(observedScope);
        Assert.Equal("operation-123", observedScope["OperationId"]);
        Assert.Empty(logger.CurrentScope);
    }

    private sealed class ScopeRecordingLogger<T> : ILogger<T>
    {
        private readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> _scope = new();

        public IReadOnlyDictionary<string, object?> CurrentScope =>
            _scope.Value ?? new Dictionary<string, object?>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var previous = _scope.Value;
            _scope.Value = state as IReadOnlyDictionary<string, object?>
                ?? (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value)
                ?? new Dictionary<string, object?>();
            return new ScopeLease(() => _scope.Value = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class ScopeLease(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
