using Microsoft.AspNetCore.Routing;

namespace Cove.Api.Http;

/// <summary>Lowercases attribute-route tokens such as <c>[controller]</c>.</summary>
public sealed class LowercaseRouteTokenTransformer : IOutboundParameterTransformer
{
    public string? TransformOutbound(object? value) => value?.ToString()?.ToLowerInvariant();
}
