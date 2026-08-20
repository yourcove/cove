using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class ImageBuilder
{
    private readonly Dictionary<string, object> _customFields = [];
    private readonly List<int> _galleryIds = [];
    private readonly List<VideoGroupInputDto> _groupIds = [];
    private readonly List<int> _performerIds = [];
    private readonly List<int> _tagIds = [];
    private readonly List<string> _urls = [];
    private string? _code;
    private string? _date;
    private string? _details;
    private bool _organized;
    private string? _photographer;
    private int? _rating;
    private int? _studioId;
    private string _title = $"API test image {Guid.NewGuid():N}";

    public ImageBuilder WithTitle(string title) { _title = title; return this; }
    public ImageBuilder WithCode(string code) { _code = code; return this; }
    public ImageBuilder WithDetails(string details) { _details = details; return this; }
    public ImageBuilder WithPhotographer(string photographer) { _photographer = photographer; return this; }
    public ImageBuilder WithRating(int rating) { _rating = rating; return this; }
    public ImageBuilder WithStudio(StudioDto studio) { _studioId = studio.Id; return this; }
    public ImageBuilder WithDate(string date) { _date = date; return this; }
    public ImageBuilder WithUrl(string url) { _urls.Add(url); return this; }
    public ImageBuilder WithTag(TagDetailDto tag) { _tagIds.Add(tag.Id); return this; }
    public ImageBuilder WithPerformer(PerformerDto performer) { _performerIds.Add(performer.Id); return this; }
    public ImageBuilder WithGallery(GalleryDto gallery) { _galleryIds.Add(gallery.Id); return this; }
    public ImageBuilder WithGroup(GroupDto group) { _groupIds.Add(new VideoGroupInputDto(group.Id)); return this; }
    public ImageBuilder WithCustomField(string key, object value) { _customFields[key] = value; return this; }
    public ImageBuilder AsOrganized() { _organized = true; return this; }

    public ImageCreateDto Build() => new(
        Title: _title,
        Code: _code,
        Details: _details,
        Photographer: _photographer,
        Rating: _rating,
        Organized: _organized,
        StudioId: _studioId,
        Date: _date,
        Urls: [.. _urls],
        TagIds: [.. _tagIds],
        PerformerIds: [.. _performerIds],
        GalleryIds: [.. _galleryIds],
        GroupIds: [.. _groupIds],
        CustomFields: new Dictionary<string, object>(_customFields));
}
