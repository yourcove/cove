using Cove.Core.DTOs;

namespace Cove.ApiTests.Assertions;

public static class PerformerAssertions
{
    public static void ShouldHaveTag(this PerformerDto performer, TagDetailDto expectedTag)
    {
        var actualTag = performer.Tags.SingleOrDefault(tag => tag.Id == expectedTag.Id);
        actualTag.Should().NotBeNull();
        actualTag.Name.Should().Be(expectedTag.Name);
    }
}
