using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class AudioBuilder
{
    private readonly List<VideoGroupInputDto> _groups = [];
    private readonly List<int> _performerIds = [];
    private readonly List<int> _tagIds = [];
    private readonly List<string> _urls = [];
    private string? _code;
    private string? _date;
    private string? _details;
    private bool _organized;
    private int? _studioId;
    private string _title = $"API test audio {Guid.NewGuid():N}";

    public AudioBuilder WithTitle(string title) { _title = title; return this; }
    public AudioBuilder WithCode(string code) { _code = code; return this; }
    public AudioBuilder WithDate(string date) { _date = date; return this; }
    public AudioBuilder WithDetails(string details) { _details = details; return this; }
    public AudioBuilder WithStudio(StudioDto studio) { _studioId = studio.Id; return this; }
    public AudioBuilder WithUrl(string url) { _urls.Add(url); return this; }
    public AudioBuilder WithTag(TagDetailDto tag) { _tagIds.Add(tag.Id); return this; }
    public AudioBuilder WithPerformer(PerformerDto performer) { _performerIds.Add(performer.Id); return this; }
    public AudioBuilder WithGroup(GroupDto group) { _groups.Add(new VideoGroupInputDto(group.Id, 0)); return this; }
    public AudioBuilder AsOrganized() { _organized = true; return this; }

    public AudioCreateDto Build() => new(
        Title: _title,
        Code: _code,
        Details: _details,
        Organized: _organized,
        StudioId: _studioId,
        Date: _date,
        Urls: [.. _urls],
        TagIds: [.. _tagIds],
        PerformerIds: [.. _performerIds],
        GroupIds: [.. _groups]);
}
