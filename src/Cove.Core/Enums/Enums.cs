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

/// <summary>How a VR video's frame maps onto the sphere around the viewer.</summary>
public enum VrProjection
{
    // Stored as integers. Append only.
    Equirectangular,
    Fisheye,
    Mkx200
}

/// <summary>How the two eyes are packed into a VR video's frame.</summary>
public enum VrStereoMode
{
    // Stored as integers. Append only.
    Mono,
    SideBySide,
    TopBottom
}
