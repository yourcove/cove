using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Api.Controllers;

namespace Cove.Tests;

/// <summary>
/// Saved/default filters using random sort must not persist the random seed, so the order
/// re-shuffles on every load instead of reproducing the same "random" sequence forever.
/// </summary>
public class SavedFilterRandomSeedTests
{
    [Fact]
    public void StripRandomSeed_RemovesSeed_WhenSortIsRandom()
    {
        var input = "{\"sort\":\"random\",\"seed\":12345,\"page\":1,\"perPage\":25}";

        var result = SavedFiltersController.StripRandomSeed(input);

        var obj = JsonNode.Parse(result!)!.AsObject();
        Assert.False(obj.ContainsKey("seed"));
        Assert.Equal("random", obj["sort"]!.GetValue<string>());
        Assert.Equal(25, obj["perPage"]!.GetValue<int>());
    }

    [Fact]
    public void StripRandomSeed_IsCaseInsensitiveOnSortValue()
    {
        var input = "{\"sort\":\"RANDOM\",\"seed\":7}";

        var result = SavedFiltersController.StripRandomSeed(input);

        Assert.False(JsonNode.Parse(result!)!.AsObject().ContainsKey("seed"));
    }

    [Fact]
    public void StripRandomSeed_KeepsSeed_WhenSortIsNotRandom()
    {
        // A seed on a non-random sort is meaningless but harmless; leave the payload untouched.
        var input = "{\"sort\":\"date\",\"seed\":999}";

        var result = SavedFiltersController.StripRandomSeed(input);

        Assert.Equal(999, JsonNode.Parse(result!)!.AsObject()["seed"]!.GetValue<int>());
    }

    [Fact]
    public void StripRandomSeed_NoOp_WhenNoSeedPresent()
    {
        var input = "{\"sort\":\"random\",\"page\":1}";

        var result = SavedFiltersController.StripRandomSeed(input);

        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void StripRandomSeed_ReturnsInputUnchanged_ForNonObjectOrInvalidJson(string? input)
    {
        Assert.Equal(input, SavedFiltersController.StripRandomSeed(input));
    }
}
