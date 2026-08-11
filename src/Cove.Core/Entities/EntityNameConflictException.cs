namespace Cove.Core.Entities;

public sealed class EntityNameConflictException : InvalidOperationException
{
    public EntityNameConflictException(string entityType)
        : base(entityType switch
        {
            NameConflictEntityTypes.Performer => "A performer with the same name and disambiguation already exists after trimming and case folding.",
            NameConflictEntityTypes.Studio => "A studio with the same name already exists after trimming and case folding.",
            _ => "An entity with the same canonical identity already exists after trimming and case folding.",
        })
    {
        EntityType = entityType;
    }

    public string EntityType { get; }
}
