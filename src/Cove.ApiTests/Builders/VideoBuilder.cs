using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class VideoBuilder
{
    private readonly Dictionary<string, object> _customFields = [];
    private readonly List<int> _galleryIds = [];
    private readonly List<VideoGroupInputDto> _groups = [];
    private readonly List<int> _performerIds = [];
    private readonly List<VideoRemoteIdDto> _remoteIds = [];
    private readonly List<int> _tagIds = [];
    private readonly List<string> _urls = [];
    private string? _captions;
    private string? _code;
    private string? _date;
    private string? _details;
    private string? _director;
    private bool _isVr;
    private bool _organized;
    private int? _rating;
    private int? _studioId;
    private string _title = $"API test video {Guid.NewGuid():N}";

    public VideoBuilder WithTitle(string title)
    {
        _title = title;
        return this;
    }

    public VideoBuilder WithStudio(StudioDto studio)
    {
        _studioId = studio.Id;
        return this;
    }

    public VideoBuilder WithCode(string code) { _code = code; return this; }
    public VideoBuilder WithDetails(string details) { _details = details; return this; }
    public VideoBuilder WithDirector(string director) { _director = director; return this; }
    public VideoBuilder WithDate(string date) { _date = date; return this; }
    public VideoBuilder WithRating(int rating) { _rating = rating; return this; }
    public VideoBuilder WithCaptions(string captions) { _captions = captions; return this; }
    public VideoBuilder WithUrl(string url) { _urls.Add(url); return this; }
    public VideoBuilder WithGallery(GalleryDto gallery) { _galleryIds.Add(gallery.Id); return this; }
    public VideoBuilder WithGroup(GroupDto group, int videoIndex = 0) { _groups.Add(new VideoGroupInputDto(group.Id, videoIndex)); return this; }
    public VideoBuilder WithRemoteId(string endpoint, string remoteId) { _remoteIds.Add(new VideoRemoteIdDto(endpoint, remoteId)); return this; }
    public VideoBuilder WithCustomField(string key, object value) { _customFields[key] = value; return this; }
    public VideoBuilder AsOrganized() { _organized = true; return this; }
    public VideoBuilder AsVr() { _isVr = true; return this; }

    public VideoBuilder WithPerformers(IEnumerable<PerformerDto> performers)
    {
        _performerIds.AddRange(performers.Select(performer => performer.Id));
        return this;
    }

    public VideoBuilder WithTags(IEnumerable<TagDetailDto> tags)
    {
        _tagIds.AddRange(tags.Select(tag => tag.Id));
        return this;
    }

    public VideoCreateDto Build() => new(
        Title: _title,
        Code: _code,
        Details: _details,
        Director: _director,
        Date: _date,
        Rating: _rating,
        Organized: _organized,
        StudioId: _studioId,
        Captions: _captions,
        Urls: [.. _urls],
        TagIds: [.. _tagIds],
        PerformerIds: [.. _performerIds],
        GalleryIds: [.. _galleryIds],
        Groups: [.. _groups],
        RemoteIds: [.. _remoteIds],
        CustomFields: new Dictionary<string, object>(_customFields),
        IsVr: _isVr);
}
