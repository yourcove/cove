namespace Cove.Core.Entities;

public sealed class TagNameConflictException : InvalidOperationException
{
    public TagNameConflictException(string name)
        : base($"Tag name '{name}' is already claimed by another tag name or alias.")
    {
        ConflictingName = name;
    }

    private TagNameConflictException(string alias, string? tagName)
        : base(tagName == null
            ? $"Tag alias '{alias}' is already claimed by another tag name or alias."
            : $"Tag '{tagName}' has an alias '{alias}' that is already claimed by another tag name or alias.")
    {
        ConflictingName = alias;
        OwningTagName = tagName;
    }

    public string ConflictingName { get; }
    public string? OwningTagName { get; }

    public static TagNameConflictException ForAlias(string alias, string? tagName = null)
        => new(alias, tagName);
}
