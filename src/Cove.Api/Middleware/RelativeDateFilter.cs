using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cove.Api.Middleware;

public sealed class RelativeDateFilter(TimeProvider clock) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        using var evaluation = RelativeDateEvaluation.Begin(clock);
        var result = await next();
        if (result.Exception is RelativeDateFilterException error)
        {
            result.Result = new BadRequestObjectResult(new { message = error.Message });
            result.ExceptionHandled = true;
        }
    }
}
