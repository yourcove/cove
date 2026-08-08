namespace Cove.Core.Enums;

public enum GenderEnum
{
    Male,
    Female,
    TransgenderMale,
    TransgenderFemale,
    Intersex,
    NonBinary
}

public enum CircumcisedEnum
{
    Cut,
    Uncut
}

public enum FilterMode
{
    Videos,
    Performers,
    Studios,
    Galleries,
    Groups,
    Tags,
    Images,
    // Appended only — historical migrations map the original integer values to their string names,
    // so preserving the existing order keeps fresh and upgraded databases equivalent.
    Audios,
    Faces,
    Texts,
    Segments,
    RawSegments,
    GroupItems
}

public enum SortDirection
{
    Asc,
    Desc
}
