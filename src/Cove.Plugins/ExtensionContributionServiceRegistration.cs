using Microsoft.Extensions.DependencyInjection;

namespace Cove.Plugins;

internal static class ExtensionContributionServiceRegistration
{
    /// <summary>
    /// Convert contribution-provider registrations added by one extension to owner-keyed services.
    /// Built-in extensions share the root container, while runtime extensions have isolated overlays;
    /// using the same convention for both prevents local contribution IDs from crossing owners.
    /// </summary>
    public static void KeyProvidersAddedSince(
        IServiceCollection services,
        int startIndex,
        string extensionId)
    {
        var endIndex = services.Count;
        for (var index = Math.Max(0, startIndex); index < endIndex; index++)
        {
            var descriptor = services[index];
            if (!descriptor.ServiceType.IsInterface
                || !typeof(IExtensionContributionProvider).IsAssignableFrom(descriptor.ServiceType))
                continue;

            services[index] = AsOwnerKeyed(descriptor, extensionId);
        }
    }

    private static ServiceDescriptor AsOwnerKeyed(ServiceDescriptor descriptor, string extensionId)
    {
        if (descriptor.IsKeyedService)
        {
            if (descriptor.KeyedImplementationType is { } keyedImplementationType)
            {
                return ServiceDescriptor.DescribeKeyed(
                    descriptor.ServiceType,
                    extensionId,
                    keyedImplementationType,
                    descriptor.Lifetime);
            }

            if (descriptor.KeyedImplementationFactory is { } keyedImplementationFactory)
            {
                return ServiceDescriptor.DescribeKeyed(
                    descriptor.ServiceType,
                    extensionId,
                    (services, key) => keyedImplementationFactory(services, key),
                    descriptor.Lifetime);
            }

            if (descriptor.KeyedImplementationInstance is { } keyedImplementationInstance)
            {
                return ServiceDescriptor.KeyedSingleton(
                    descriptor.ServiceType,
                    extensionId,
                    keyedImplementationInstance);
            }
        }

        if (descriptor.ImplementationType is { } implementationType)
        {
            return ServiceDescriptor.DescribeKeyed(
                descriptor.ServiceType,
                extensionId,
                implementationType,
                descriptor.Lifetime);
        }

        if (descriptor.ImplementationFactory is { } implementationFactory)
        {
            return ServiceDescriptor.DescribeKeyed(
                descriptor.ServiceType,
                extensionId,
                (services, _) => implementationFactory(services),
                descriptor.Lifetime);
        }

        if (descriptor.ImplementationInstance is { } implementationInstance)
        {
            return ServiceDescriptor.KeyedSingleton(
                descriptor.ServiceType,
                extensionId,
                implementationInstance);
        }

        throw new InvalidOperationException(
            $"Contribution provider '{descriptor.ServiceType}' has no implementation registration.");
    }
}
