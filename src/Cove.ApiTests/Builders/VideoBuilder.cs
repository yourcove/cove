using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class VideoBuilder
{
    private readonly List<int> _performerIds = [];
    private readonly List<int> _tagIds = [];
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
        Code: null,
        Details: null,
        Director: null,
        Date: null,
        Rating: null,
        Organized: false,
        StudioId: _studioId,
        Captions: null,
        Urls: [],
        TagIds: [.. _tagIds],
        PerformerIds: [.. _performerIds],
        GalleryIds: [],
        Groups: []);
}
