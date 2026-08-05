using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Api.Middleware;

public sealed class CompoundSortValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (UnsupportedCompoundSortException ex) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Unsupported compound sort.",
                Detail = ex.Message,
            });
        }
    }
}
