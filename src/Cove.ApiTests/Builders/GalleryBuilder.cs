using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class GalleryBuilder
{
    private readonly Dictionary<string, object> _customFields = [];
    private readonly List<int> _performerIds = [];
    private readonly List<int> _tagIds = [];
    private readonly List<string> _urls = [];
    private readonly List<int> _videoIds = [];
    private string? _code;
    private string? _date;
    private string? _details;
    private bool _organized;
    private string? _photographer;
    private int? _rating;
    private int? _studioId;
    private string _title = $"API test gallery {Guid.NewGuid():N}";

    public GalleryBuilder WithTitle(string title) { _title = title; return this; }
    public GalleryBuilder WithCode(string code) { _code = code; return this; }
    public GalleryBuilder WithDate(string date) { _date = date; return this; }
    public GalleryBuilder WithDetails(string details) { _details = details; return this; }
    public GalleryBuilder WithPhotographer(string photographer) { _photographer = photographer; return this; }
    public GalleryBuilder WithRating(int rating) { _rating = rating; return this; }
    public GalleryBuilder WithStudio(StudioDto studio) { _studioId = studio.Id; return this; }
    public GalleryBuilder WithUrl(string url) { _urls.Add(url); return this; }
    public GalleryBuilder WithTag(TagDetailDto tag) { _tagIds.Add(tag.Id); return this; }
    public GalleryBuilder WithPerformer(PerformerDto performer) { _performerIds.Add(performer.Id); return this; }
    public GalleryBuilder WithVideo(VideoDto video) { _videoIds.Add(video.Id); return this; }
    public GalleryBuilder WithCustomField(string key, object value) { _customFields[key] = value; return this; }
    public GalleryBuilder AsOrganized() { _organized = true; return this; }

    public GalleryCreateDto Build() => new(
        Title: _title,
        Code: _code,
        Date: _date,
        Details: _details,
        Photographer: _photographer,
        Rating: _rating,
        Organized: _organized,
        StudioId: _studioId,
        Urls: [.. _urls],
        TagIds: [.. _tagIds],
        PerformerIds: [.. _performerIds],
        VideoIds: [.. _videoIds],
        CustomFields: new Dictionary<string, object>(_customFields));
}
