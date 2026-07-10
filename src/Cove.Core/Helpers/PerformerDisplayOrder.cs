using Cove.Core.Entities;
using Cove.Core.Enums;

namespace Cove.Core.Helpers;

public static class PerformerDisplayOrder
{
    public static IOrderedEnumerable<Performer> OrderForDisplay(this IEnumerable<Performer> performers) =>
        performers
            .OrderBy(performer => GenderBucket(performer.Gender))
            .ThenBy(performer => performer.Name, StringComparer.CurrentCulture);

    private static int GenderBucket(GenderEnum? gender) => gender switch
    {
        GenderEnum.Female => 0,
        GenderEnum.TransgenderFemale => 1,
        GenderEnum.Male => 2,
        GenderEnum.TransgenderMale => 3,
        GenderEnum.Intersex => 4,
        GenderEnum.NonBinary => 5,
        _ => 6,
    };
}
