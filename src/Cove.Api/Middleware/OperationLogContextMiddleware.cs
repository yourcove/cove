namespace Cove.Api.Middleware;

/// <summary>
/// Adds request correlation to application events without emitting a log event
/// for every HTTP request.
/// </summary>
public sealed class OperationLogContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ILogger<OperationLogContextMiddleware> logger)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["OperationId"] = context.TraceIdentifier,
        });
        await next(context);
    }
}
