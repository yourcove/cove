using System.Text.Json;
using Cove.Api.Middleware;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Http;

namespace Cove.Tests;

public class CompoundSortValidationTests
{
    [Fact]
    public void NormalizeRejectsUnsupportedKeysInsteadOfDroppingThem()
    {
        var clauses = new[]
        {
            new SortClause("unsupported", SortDirection.Desc),
            new SortClause("date", SortDirection.Desc),
        };

        var exception = Assert.Throws<UnsupportedCompoundSortException>(() =>
            CompoundSortOrdering.Normalize(clauses, new HashSet<string> { "date" }));

        Assert.Equal("unsupported", exception.Key);
    }

    [Fact]
    public async Task MiddlewareReturnsBadRequestForUnsupportedCompoundSorts()
    {
        var middleware = new CompoundSortValidationMiddleware(_ =>
            throw new UnsupportedCompoundSortException("unsupported"));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Unsupported compound sort.", document.RootElement.GetProperty("title").GetString());
        Assert.Contains("unsupported", document.RootElement.GetProperty("detail").GetString());
    }
}
