using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class StudioBuilder
{
    private readonly List<string> _aliases = [];
    private readonly Dictionary<string, object> _customFields = [];
    private readonly List<StudioRemoteIdDto> _remoteIds = [];
    private readonly List<int> _tagIds = [];
    private readonly List<string> _urls = [];
    private string? _details;
    private bool _favorite;
    private string _name = $"API test studio {Guid.NewGuid():N}";
    private bool _organized;
    private int? _parentId;
    private int? _rating;

    public StudioBuilder WithName(string name) { _name = name; return this; }
    public StudioBuilder WithParent(StudioDto parent) { _parentId = parent.Id; return this; }
    public StudioBuilder WithRating(int rating) { _rating = rating; return this; }
    public StudioBuilder WithDetails(string details) { _details = details; return this; }
    public StudioBuilder WithUrl(string url) { _urls.Add(url); return this; }
    public StudioBuilder WithAlias(string alias) { _aliases.Add(alias); return this; }
    public StudioBuilder WithTag(TagDetailDto tag) { _tagIds.Add(tag.Id); return this; }
    public StudioBuilder WithRemoteId(string endpoint, string remoteId) { _remoteIds.Add(new StudioRemoteIdDto(endpoint, remoteId)); return this; }
    public StudioBuilder WithCustomField(string key, object value) { _customFields[key] = value; return this; }
    public StudioBuilder AsFavorite() { _favorite = true; return this; }
    public StudioBuilder AsOrganized() { _organized = true; return this; }

    public StudioCreateDto Build() => new(
        Name: _name,
        ParentId: _parentId,
        Rating: _rating,
        Favorite: _favorite,
        Details: _details,
        Organized: _organized,
        Urls: [.. _urls],
        Aliases: [.. _aliases],
        TagIds: [.. _tagIds],
        RemoteIds: [.. _remoteIds],
        CustomFields: new Dictionary<string, object>(_customFields));
}
