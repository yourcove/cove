namespace Cove.Core.Entities;

public static class InteractionValueMapper
{
    public static bool TryParseHostType(string? hostType, out InteractionHostType parsed)
    {
        parsed = default;
        return Normalize(hostType) switch
        {
            "video" => Assign(InteractionHostType.Video, out parsed),
            "image" => Assign(InteractionHostType.Image, out parsed),
            "audio" => Assign(InteractionHostType.Audio, out parsed),
            "text" => Assign(InteractionHostType.Text, out parsed),
            "performer" => Assign(InteractionHostType.Performer, out parsed),
            "tag" => Assign(InteractionHostType.Tag, out parsed),
            "face" => Assign(InteractionHostType.Face, out parsed),
            "segment" => Assign(InteractionHostType.Segment, out parsed),
            "studio" => Assign(InteractionHostType.Studio, out parsed),
            "gallery" => Assign(InteractionHostType.Gallery, out parsed),
            "group" => Assign(InteractionHostType.Group, out parsed),
            "search" => Assign(InteractionHostType.Search, out parsed),
            "collection" => Assign(InteractionHostType.Collection, out parsed),
            _ => false,
        };
    }

    public static string ToName(InteractionHostType hostType) => hostType switch
    {
        InteractionHostType.Video => "video",
        InteractionHostType.Image => "image",
        InteractionHostType.Audio => "audio",
        InteractionHostType.Text => "text",
        InteractionHostType.Performer => "performer",
        InteractionHostType.Tag => "tag",
        InteractionHostType.Face => "face",
        InteractionHostType.Segment => "segment",
        InteractionHostType.Studio => "studio",
        InteractionHostType.Gallery => "gallery",
        InteractionHostType.Group => "group",
        InteractionHostType.Search => "search",
        InteractionHostType.Collection => "collection",
        _ => hostType.ToString(),
    };

    public static bool TryParseKind(string? kind, out InteractionKind parsed)
    {
        parsed = default;
        return Normalize(kind) switch
        {
            "pause" => Assign(InteractionKind.Pause, out parsed),
            "seek" => Assign(InteractionKind.Seek, out parsed),
            "likecount" => Assign(InteractionKind.LikeCount, out parsed),
            "opendetail" => Assign(InteractionKind.OpenDetail, out parsed),
            "openlightbox" => Assign(InteractionKind.OpenLightbox, out parsed),
            "closelightbox" => Assign(InteractionKind.CloseLightbox, out parsed),
            "navigate" => Assign(InteractionKind.Navigate, out parsed),
            "zoom" => Assign(InteractionKind.Zoom, out parsed),
            "searchquery" => Assign(InteractionKind.SearchQuery, out parsed),
            "searchselect" => Assign(InteractionKind.SearchSelect, out parsed),
            "filterapply" => Assign(InteractionKind.FilterApply, out parsed),
            "filterclear" => Assign(InteractionKind.FilterClear, out parsed),
            "pagevisit" => Assign(InteractionKind.PageVisit, out parsed),
            "derivedlike" => Assign(InteractionKind.DerivedLike, out parsed),
            "fullscreen" => Assign(InteractionKind.Fullscreen, out parsed),
            "slideshowdelay" => Assign(InteractionKind.SlideshowDelay, out parsed),
            _ => false,
        };
    }

    public static string ToName(InteractionKind kind) => kind switch
    {
        InteractionKind.Pause => "pause",
        InteractionKind.Seek => "seek",
        InteractionKind.LikeCount => "likeCount",
        InteractionKind.OpenDetail => "openDetail",
        InteractionKind.OpenLightbox => "openLightbox",
        InteractionKind.CloseLightbox => "closeLightbox",
        InteractionKind.Navigate => "navigate",
        InteractionKind.Zoom => "zoom",
        InteractionKind.SearchQuery => "searchQuery",
        InteractionKind.SearchSelect => "searchSelect",
        InteractionKind.FilterApply => "filterApply",
        InteractionKind.FilterClear => "filterClear",
        InteractionKind.PageVisit => "pageVisit",
        InteractionKind.DerivedLike => "derivedLike",
        InteractionKind.Fullscreen => "fullscreen",
        InteractionKind.SlideshowDelay => "slideshowDelay",
        _ => kind.ToString(),
    };

    public static bool RequiresConcreteHost(InteractionHostType hostType)
        => hostType is not InteractionHostType.Search and not InteractionHostType.Collection;

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool Assign(InteractionHostType hostType, out InteractionHostType parsed)
    {
        parsed = hostType;
        return true;
    }

    private static bool Assign(InteractionKind kind, out InteractionKind parsed)
    {
        parsed = kind;
        return true;
    }
}
