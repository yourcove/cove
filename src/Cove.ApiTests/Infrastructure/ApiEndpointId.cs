namespace Cove.ApiTests.Infrastructure;

public readonly record struct ApiEndpointId
{
    private ApiEndpointId(string httpMethod, string routeTemplate)
    {
        HttpMethod = httpMethod;
        RouteTemplate = routeTemplate;
    }

    public string HttpMethod { get; }

    public string RouteTemplate { get; }

    public static ApiEndpointId Create(string httpMethod, string routeTemplate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(httpMethod);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeTemplate);

        return new ApiEndpointId(
            httpMethod.Trim().ToUpperInvariant(),
            NormalizeRoute(routeTemplate));
    }

    public static ApiEndpointId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var separator = value.IndexOf(' ');
        if (separator <= 0 || separator == value.Length - 1)
            throw new FormatException($"Endpoint identifier '{value}' must use the format 'VERB /route'.");

        return Create(value[..separator], value[(separator + 1)..]);
    }

    public override string ToString() => $"{HttpMethod} {RouteTemplate}";

    private static string NormalizeRoute(string routeTemplate)
    {
        var route = routeTemplate.Trim();
        if (route.StartsWith("~/", StringComparison.Ordinal))
            route = route[1..];
        if (!route.StartsWith('/'))
            route = $"/{route}";

        var segments = route
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Length == 0
            ? "/"
            : $"/{string.Join('/', segments).ToLowerInvariant()}";
    }
}
