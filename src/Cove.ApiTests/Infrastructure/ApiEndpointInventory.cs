using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.ApiTests.Infrastructure;

public sealed record ApiEndpointDefinition(
    ApiEndpointId Endpoint,
    Type ControllerType,
    MethodInfo ActionMethod);

public static class ApiEndpointInventory
{
    private static readonly Lazy<IReadOnlyList<ApiEndpointDefinition>> Endpoints =
        new(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<ApiEndpointDefinition> All => Endpoints.Value;

    private static IReadOnlyList<ApiEndpointDefinition> Discover()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddControllers()
            .AddApplicationPart(typeof(Program).Assembly);

        using var serviceProvider = services.BuildServiceProvider();
        var actionDescriptors = serviceProvider
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors
            .Items
            .OfType<ControllerActionDescriptor>()
            .Where(descriptor => descriptor.ControllerTypeInfo.AsType() is
            {
                IsAbstract: false,
                IsPublic: true,
            } controllerType && controllerType.Assembly == typeof(Program).Assembly)
            .ToArray();

        var actionsWithoutHttpMethods = actionDescriptors
            .Where(descriptor => GetHttpMethods(descriptor).Count == 0)
            .Select(descriptor => descriptor.DisplayName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (actionsWithoutHttpMethods.Length > 0)
        {
            throw new InvalidOperationException(
                $"Public API actions must declare an explicit HTTP method before they can be inventoried:{Environment.NewLine}{string.Join(Environment.NewLine, actionsWithoutHttpMethods)}");
        }

        return actionDescriptors
            .SelectMany(descriptor => GetHttpMethods(descriptor)
                .Select(httpMethod => new ApiEndpointDefinition(
                    ApiEndpointId.Create(
                        httpMethod,
                        descriptor.AttributeRouteInfo?.Template
                            ?? throw new InvalidOperationException(
                                $"Attributed action {descriptor.DisplayName} has no route template.")),
                    descriptor.ControllerTypeInfo.AsType(),
                    descriptor.MethodInfo)))
            .OrderBy(definition => definition.Endpoint.ToString(), StringComparer.Ordinal)
            .ThenBy(definition => definition.ControllerType.FullName, StringComparer.Ordinal)
            .ThenBy(definition => definition.ActionMethod.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> GetHttpMethods(ControllerActionDescriptor descriptor)
        => descriptor.ActionConstraints?
            .OfType<HttpMethodActionConstraint>()
            .SelectMany(constraint => constraint.HttpMethods)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
}
