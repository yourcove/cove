namespace Cove.ApiTests.Infrastructure;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class CoversEndpointAttribute : Attribute
{
    public CoversEndpointAttribute(string httpMethod, string routeTemplate)
    {
        Endpoint = ApiEndpointId.Create(httpMethod, routeTemplate);
    }

    public ApiEndpointId Endpoint { get; }
}
