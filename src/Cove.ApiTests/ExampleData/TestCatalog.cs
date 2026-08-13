namespace Cove.ApiTests.ExampleData;

public sealed record CatalogStudio(string Name, string Slug, string Description);

public sealed record CatalogPerformer(string Name, string Slug, string Description);

public sealed record CatalogTag(string Name, string Slug, string Description);

public sealed record CatalogMovie(
    string Title,
    string Slug,
    string Premise,
    IReadOnlyList<CatalogPerformer> Cast,
    IReadOnlyList<CatalogTag> Tags);

public static class TestCatalog
{
    public static CatalogStudio Studio { get; } = new(
        "Barely Dressed Pictures",
        "barely-dressed-pictures",
        "A proudly low-budget company where every entrance is overproduced and every plot is negotiable.");

    public static class Performers
    {
        public static CatalogPerformer CherryPoppins { get; } = new(
            "Cherry Poppins",
            "cherry-poppins",
            "A cheerfully chaotic pin-up performer known for suspiciously elaborate entrances. Cherry treats every doorway like opening night and every wardrobe mishap like choreography.");

        public static CatalogPerformer VelvetThunder { get; } = new(
            "Velvet Thunder",
            "velvet-thunder",
            "A magnificently brooding heartthrob whose shirt buttons always seem one dramatic sigh from surrender. His serious pose rarely survives contact with the rest of the cast.");

        public static CatalogPerformer BeaHaven { get; } = new(
            "Bea Haven",
            "bea-haven",
            "A sweet-faced troublemaker with immaculate comic timing and a gift for accidental-sounding double entendres. Her expressions range from mock innocence to delighted scheming.");

        public static CatalogPerformer RandyDandy { get; } = new(
            "Randy Dandy",
            "randy-dandy",
            "An impossibly confident lounge performer who assumes every room has been waiting for his entrance. Randy brings exceptional hair and peacock energy to even the flimsiest plot.");
    }

    public static class Tags
    {
        public static CatalogTag AccidentalDoubleEntendre { get; } = Tag("Accidental Double Entendre", "accidental-double-entendre", "Dialogue that was allegedly innocent when it was written.");
        public static CatalogTag Brooding { get; } = Tag("Brooding", "brooding", "Meaningful staring, ideally toward rain or a badly lit window.");
        public static CatalogTag CandleBudgetExceeded { get; } = Tag("Candle Budget Exceeded", "candle-budget-exceeded", "A scene containing far more mood lighting than fiscal responsibility permits.");
        public static CatalogTag CowboyBoots { get; } = Tag("Cowboy Boots", "cowboy-boots", "Western footwear carrying most of the production value.");
        public static CatalogTag DramaticStandoff { get; } = Tag("Dramatic Standoff", "dramatic-standoff", "Two or more characters pause the plot to glare professionally.");
        public static CatalogTag EnemiesToLovers { get; } = Tag("Enemies to Lovers", "enemies-to-lovers", "Hostility with suspiciously flattering lighting.");
        public static CatalogTag NerdsAfterDark { get; } = Tag("Nerds After Dark", "nerds-after-dark", "Technology, research, or spreadsheets presented with unnecessary allure.");
        public static CatalogTag PeriodCostume { get; } = Tag("Period Costume", "period-costume", "Historical clothing with varying levels of commitment to history.");
        public static CatalogTag PlotOptional { get; } = Tag("Plot Optional", "plot-optional", "The production has a premise, several poses, and no urgent need to connect them.");
        public static CatalogTag QuestionableAlibi { get; } = Tag("Questionable Alibi", "questionable-alibi", "An explanation that becomes less convincing every time it is repeated.");
        public static CatalogTag ShirtButtonsUnderStress { get; } = Tag("Shirt Buttons Under Stress", "shirt-buttons-under-stress", "Menswear subjected to forces beyond its rated capacity.");
        public static CatalogTag SlowMotionHairToss { get; } = Tag("Slow-Motion Hair Toss", "slow-motion-hair-toss", "Hair movement important enough to alter the frame rate.");
        public static CatalogTag SuggestiveEyeContact { get; } = Tag("Suggestive Eye Contact", "suggestive-eye-contact", "A look held approximately two seconds longer than the plot requires.");
        public static CatalogTag TastefulSideboob { get; } = Tag("Tasteful Sideboob", "tasteful-sideboob", "PG-13 framing that remains non-explicit and more comic than revealing.");
        public static CatalogTag TheatricalEntrance { get; } = Tag("Theatrical Entrance", "theatrical-entrance", "Curtains, spotlights, wind machines, or other evidence that a doorway was considered insufficient.");
        public static CatalogTag WardrobeMalfunction { get; } = Tag("Wardrobe Malfunction", "wardrobe-malfunction", "Comic costume trouble without explicit exposure.");

        private static CatalogTag Tag(string name, string slug, string description) => new(name, slug, description);
    }

    public static class Movies
    {
        public static CatalogMovie TheFastAndTheFlirtatious { get; } = Movie(
            "The Fast and the Flirtatious",
            "the-fast-and-the-flirtatious",
            "Two rival getaway drivers discover that neither owns a suitable getaway car, but both have brought excellent lighting.",
            [Performers.CherryPoppins, Performers.VelvetThunder],
            [Tags.SuggestiveEyeContact, Tags.SlowMotionHairToss, Tags.ShirtButtonsUnderStress]);

        public static CatalogMovie RaidersOfTheLostCorset { get; } = Movie(
            "Raiders of the Lost Corset",
            "raiders-of-the-lost-corset",
            "A backstage wardrobe vault, an absurdly valuable golden corset, and two treasure hunters with no clear plan beyond making an entrance.",
            [Performers.CherryPoppins, Performers.BeaHaven],
            [Tags.WardrobeMalfunction, Tags.TheatricalEntrance, Tags.PlotOptional]);

        public static CatalogMovie HotSinglesInYourDatabase { get; } = Movie(
            "Hot Singles in Your Database",
            "hot-singles-in-your-database",
            "A database administrator and an overly polished consultant race to repair a matchmaking service before its candlelit launch party.",
            [Performers.BeaHaven, Performers.RandyDandy],
            [Tags.AccidentalDoubleEntendre, Tags.NerdsAfterDark, Tags.CandleBudgetExceeded]);

        public static CatalogMovie NoShirtNoShoesNoAlibi { get; } = Movie(
            "No Shirt, No Shoes, No Alibi",
            "no-shirt-no-shoes-no-alibi",
            "Two lounge patrons awaken after a suspiciously glamorous party with one missing shirt, several conflicting stories, and a receipt for twelve umbrellas.",
            [Performers.VelvetThunder, Performers.RandyDandy],
            [Tags.TastefulSideboob, Tags.Brooding, Tags.QuestionableAlibi]);

        public static CatalogMovie MuchAdoAboutNothinOn { get; } = Movie(
            "Much Ado About Nothin' On",
            "much-ado-about-nothin-on",
            "A costume comedy in which everyone has rehearsed the romantic misunderstandings and nobody has checked the fasteners.",
            [Performers.CherryPoppins, Performers.VelvetThunder, Performers.BeaHaven, Performers.RandyDandy],
            [Tags.PeriodCostume, Tags.EnemiesToLovers, Tags.WardrobeMalfunction]);

        public static CatalogMovie TheGoodTheBadAndTheShirtless { get; } = Movie(
            "The Good, the Bad, and the Shirtless",
            "the-good-the-bad-and-the-shirtless",
            "A dusty frontier standoff escalates when the saloon runs out of clean glasses and Velvet runs out of structurally sound shirts.",
            [Performers.VelvetThunder, Performers.CherryPoppins],
            [Tags.CowboyBoots, Tags.DramaticStandoff, Tags.ShirtButtonsUnderStress]);

        private static CatalogMovie Movie(
            string title,
            string slug,
            string premise,
            IReadOnlyList<CatalogPerformer> cast,
            IReadOnlyList<CatalogTag> tags) => new(title, slug, premise, cast, tags);
    }
}
