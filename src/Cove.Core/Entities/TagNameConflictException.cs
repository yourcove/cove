namespace Cove.Core.Entities;

public sealed class TagNameConflictException : InvalidOperationException
{
    private const string ConcurrentConflictMessage =
        "A tag with that name or alias already exists. Tag names and tag aliases must be unique.";

    public TagNameConflictException(string name)
        : this(name, isAlias: false, name)
    {
    }

    private TagNameConflictException(
        string existingName,
        bool isAlias,
        string? conflictingName,
        string? owningTagName = null)
        : base(isAlias
            ? $"A tag alias with name \"{existingName}\" already exists. Tag names and tag aliases must be unique."
            : $"A tag with name \"{existingName}\" already exists. Tag names and tag aliases must be unique.")
    {
        ConflictingName = conflictingName ?? existingName;
        ExistingClaimName = existingName;
        ExistingClaimIsAlias = isAlias;
        OwningTagName = owningTagName;
    }

    private TagNameConflictException()
        : base(ConcurrentConflictMessage)
    {
        ConflictingName = string.Empty;
    }

    public string ConflictingName { get; }
    public string? ExistingClaimName { get; }
    public bool? ExistingClaimIsAlias { get; }
    public string? OwningTagName { get; }

    public static TagNameConflictException ForExistingTagName(string name, string? conflictingName = null)
        => new(name, isAlias: false, conflictingName);

    public static TagNameConflictException ForExistingAlias(string alias, string? conflictingName = null)
        => new(alias, isAlias: true, conflictingName);

    public static TagNameConflictException ForAlias(string alias, string? tagName = null)
        => new(alias, isAlias: true, conflictingName: alias, owningTagName: tagName);

    public static TagNameConflictException ForConcurrentWrite()
        => new();

    public static TagNameConflictException ForConcurrentWrite(string attemptedName)
        => new(
            $"A tag with name or alias \"{attemptedName}\" already exists. Tag names and tag aliases must be unique.",
            attemptedName);

    private TagNameConflictException(string message, string conflictingName)
        : base(message)
    {
        ConflictingName = conflictingName;
    }
}
