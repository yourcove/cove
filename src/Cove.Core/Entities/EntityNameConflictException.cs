namespace Cove.Core.Entities;

public sealed class EntityNameConflictException : InvalidOperationException
{
    public EntityNameConflictException(string entityType, Exception? innerException = null)
        : this(entityType, null, null, innerException, detailed: false)
    {
    }

    private EntityNameConflictException(
        string entityType,
        string? name,
        string? disambiguation,
        Exception? innerException,
        bool detailed)
        : base(BuildMessage(entityType, name, disambiguation), innerException)
    {
        EntityType = entityType;
        ConflictingName = name;
        ConflictingDisambiguation = disambiguation;
    }

    public string EntityType { get; }
    public string? ConflictingName { get; }
    public string? ConflictingDisambiguation { get; }

    public static EntityNameConflictException ForExistingIdentity(
        string entityType,
        string name,
        string? disambiguation = null,
        Exception? innerException = null)
        => new(entityType, name, disambiguation, innerException, detailed: true);

    private static string BuildMessage(string entityType, string? name, string? disambiguation)
    {
        if (name != null)
        {
            return entityType switch
            {
                NameConflictEntityTypes.Performer when disambiguation == null =>
                    $"A performer with name \"{name}\" and no disambiguation already exists. Performer name and disambiguation combinations must be unique.",
                NameConflictEntityTypes.Performer =>
                    $"A performer with name \"{name}\" and disambiguation \"{disambiguation}\" already exists. Performer name and disambiguation combinations must be unique.",
                NameConflictEntityTypes.Studio =>
                    $"A studio with name \"{name}\" already exists. Studio names must be unique.",
                _ => $"An entity with name \"{name}\" already exists. Entity names must be unique.",
            };
        }

        return entityType switch
        {
            NameConflictEntityTypes.Performer =>
                "A performer with that name and disambiguation already exists. Performer name and disambiguation combinations must be unique.",
            NameConflictEntityTypes.Studio =>
                "A studio with that name already exists. Studio names must be unique.",
            _ => "An entity with that name already exists. Entity names must be unique.",
        };
    }
}
