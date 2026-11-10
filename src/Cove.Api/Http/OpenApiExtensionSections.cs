using System.Text.Json.Nodes;
using Cove.Plugins;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Cove.Api.Http;

/// <summary>
/// Groups each extension's routes into one Swagger UI section named after the extension, and lists those
/// sections after Cove's own.
/// </summary>
/// <remarks>
/// Without this, an extension route's tag is whatever the generator infers: the handler's class name, or
/// the application name ("Cove") for a lambda, which mixes it into the host's section. Ownership comes
/// from <see cref="ExtensionEndpointMetadata"/>, which the host stamps on every extension endpoint, so an
/// extension cannot place its routes in a core section. Tags an extension sets itself are replaced, because
/// Swagger UI lists an operation under every tag it carries.
/// </remarks>
public static class OpenApiExtensionSections
{
    /// <summary>Operation extension naming the Cove extension that registered the route.</summary>
    public const string ExtensionIdProperty = "x-cove-extension";

    /// <summary>Section for host routes that no controller owns, such as /health.</summary>
    public const string HostTag = "Cove";

    public const string ExtensionTagPrefix = "Extension: ";

    /// <summary>Records which extension registered the operation's route, if any.</summary>
    public sealed class OperationTransformer : IOpenApiOperationTransformer
    {
        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
        {
            var owner = context.Description.ActionDescriptor.EndpointMetadata.OfType<ExtensionEndpointMetadata>().LastOrDefault();
            if (owner is not null)
            {
                operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                operation.Extensions[ExtensionIdProperty] = new JsonNodeExtension(JsonValue.Create(owner.ExtensionId));
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>Retags extension operations by extension and orders the document's sections.</summary>
    public static void Apply(OpenApiDocument document, ExtensionManager? extensions)
    {
        var operations = (document.Paths ?? [])
            .SelectMany(path => path.Value.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
            .ToList();

        // One section per extension ID, named after the extension. Two extensions with the same display name
        // keep separate sections by adding the ID.
        var extensionSections = new Dictionary<string, OpenApiTag>(StringComparer.Ordinal);
        var takenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            if (GetExtensionId(operation) is not { } extensionId)
                continue;
            if (!extensionSections.TryGetValue(extensionId, out var section))
            {
                var extension = extensions?.GetExtension(extensionId);
                var name = ExtensionTagPrefix + (string.IsNullOrWhiteSpace(extension?.Name) ? extensionId : extension.Name.Trim());
                if (!takenNames.Add(name))
                {
                    name = $"{name} ({extensionId})";
                    takenNames.Add(name);
                }
                section = new OpenApiTag
                {
                    Name = name,
                    Description = extension is null
                        ? $"Routes registered by the `{extensionId}` extension."
                        : $"Routes registered by the `{extensionId}` extension, version {extension.Version}.",
                };
                extensionSections[extensionId] = section;
            }
            operation.Tags = new HashSet<OpenApiTagReference> { new(section.Name!, document) };
        }

        // Cove's own sections first, alphabetically, then host routes without a controller, then extensions.
        var existing = (document.Tags ?? Enumerable.Empty<OpenApiTag>())
            .Where(tag => tag.Name is not null)
            .GroupBy(tag => tag.Name!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var extensionNames = extensionSections.Values.Select(tag => tag.Name!).ToHashSet(StringComparer.Ordinal);
        var used = operations
            .SelectMany(operation => operation.Tags ?? Enumerable.Empty<OpenApiTagReference>())
            .Select(tag => tag.Name)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var ordered = used
            .OrderBy(name => extensionNames.Contains(name) ? 2 : name == HostTag ? 1 : 0)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => extensionSections.Values.FirstOrDefault(tag => tag.Name == name)
                ?? existing.GetValueOrDefault(name)
                ?? new OpenApiTag { Name = name });

        // Swagger UI shows sections in the order of this list; a HashSet that only receives additions
        // enumerates in insertion order, which the serializer preserves.
        document.Tags = new HashSet<OpenApiTag>(ordered);
    }

    private static string? GetExtensionId(OpenApiOperation operation)
        => operation.Extensions?.TryGetValue(ExtensionIdProperty, out var value) == true
            && value is JsonNodeExtension { Node: JsonValue node }
            && node.TryGetValue<string>(out var id)
                ? id
                : null;
}
