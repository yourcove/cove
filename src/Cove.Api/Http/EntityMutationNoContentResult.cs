using Cove.Core.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Cove.Api.Http;

/// <summary>
/// Preserves a 204 response while exposing the affected entity IDs to in-process action filters.
/// </summary>
public sealed class EntityMutationNoContentResult(IReadOnlyList<int> entityIds)
    : IActionResult, IStatusCodeActionResult, IEntityMutationResult
{
    public IReadOnlyList<int> EntityIds { get; } = entityIds;
    public int? StatusCode => StatusCodes.Status204NoContent;

    public Task ExecuteResultAsync(ActionContext context)
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }
}
