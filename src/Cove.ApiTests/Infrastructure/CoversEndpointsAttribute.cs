namespace Cove.ApiTests.Infrastructure;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CoversEndpointsAttribute(params Type[] controllerTypes) : Attribute
{
    public IReadOnlyList<Type> ControllerTypes { get; } = controllerTypes;
}
