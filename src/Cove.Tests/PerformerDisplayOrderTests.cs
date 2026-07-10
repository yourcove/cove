using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Helpers;

namespace Cove.Tests;

public class PerformerDisplayOrderTests
{
    [Fact]
    public void OrderForDisplay_SortsByStashGenderBucketThenName()
    {
        Performer[] performers =
        [
            new() { Name = "Zulu unknown" },
            new() { Name = "Zulu male", Gender = GenderEnum.Male },
            new() { Name = "Beta female", Gender = GenderEnum.Female },
            new() { Name = "Alpha nonbinary", Gender = GenderEnum.NonBinary },
            new() { Name = "Alpha male", Gender = GenderEnum.Male },
            new() { Name = "Alpha female", Gender = GenderEnum.Female },
            new() { Name = "Alpha intersex", Gender = GenderEnum.Intersex },
            new() { Name = "Alpha trans man", Gender = GenderEnum.TransgenderMale },
            new() { Name = "Alpha trans woman", Gender = GenderEnum.TransgenderFemale },
            new() { Name = "Alpha unknown" },
        ];

        var orderedNames = performers.OrderForDisplay().Select(performer => performer.Name).ToArray();

        Assert.Equal(
        [
            "Alpha female",
            "Beta female",
            "Alpha trans woman",
            "Alpha male",
            "Zulu male",
            "Alpha trans man",
            "Alpha intersex",
            "Alpha nonbinary",
            "Alpha unknown",
            "Zulu unknown",
        ], orderedNames);
    }
}
