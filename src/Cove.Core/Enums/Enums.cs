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
    VideoMarkers,
    Groups,
    Tags,
    Images,
    // Appended only — Mode persists as the enum's integer value (saved_filters."Mode"), so new entries
    // must go at the end to keep existing rows' modes stable.
    Audios,
    Faces,
    Texts
}

public enum SortDirection
{
    Asc,
    Desc
}

