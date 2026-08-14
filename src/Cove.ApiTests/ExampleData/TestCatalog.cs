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

public sealed record CatalogMetadataService(string Name, string ApiUrl);

public static class TestCatalog
{
    public static class MetadataServices
    {
        public static CatalogMetadataService PulpMovieDb { get; } = new(
            "Pulp Movie DB",
            "https://api.pulpmoviedb.example/graphql");

        public static CatalogMetadataService TheBacklotIndex { get; } = new(
            "The Backlot Index",
            "https://api.thebacklotindex.example/graphql");
    }

    public static CatalogStudio Studio => Studios.BarelyDressedPictures;

    public static class Studios
    {
        public static CatalogStudio BarelyDressedPictures { get; } = Studio(
            "Barely Dressed Pictures",
            "barely-dressed-pictures",
            "A proudly low-budget company where every entrance is overproduced and every plot is negotiable.");

        public static CatalogStudio SecondTakeFeatures { get; } = Studio(
            "Second Take Features",
            "second-take-features",
            "An actor-driven independent film company founded during the early 1980s.");

        public static CatalogStudio ElectricMarquee { get; } = Studio(
            "Electric Marquee",
            "electric-marquee",
            "A 1990s producer of genre pictures and romantic comedies.");

        public static CatalogStudio OpenSecretFilms { get; } = Studio(
            "Open Secret Films",
            "open-secret-films",
            "A 2000s independent production company focused on writer-led ensemble work.");

        public static CatalogStudio FourthWallPictures { get; } = Studio(
            "Fourth Wall Pictures",
            "fourth-wall-pictures",
            "Bella Bloom's production company for theatre adaptations and director-led films.");

        public static CatalogStudio TheLanternRoom { get; } = Studio(
            "The Lantern Room",
            "the-lantern-room",
            "A theatre company associated with Bella Bloom's early plays and contemporary revivals.");

        public static CatalogStudio CherryPoppinsStudio { get; } = Studio(
            "Cherry Poppins Studio",
            "cherry-poppins-studio",
            "A photography studio founded in the late 1980s for portraiture, editorial fashion, unit stills, and theatrical publicity.");

        public static CatalogStudio AvailableLightCooperative { get; } = Studio(
            "Available Light Cooperative",
            "available-light-cooperative",
            "A contemporary studio representing portrait, documentary, and production photographers.");

        public static CatalogStudio SilverContactArchive { get; } = Studio(
            "Silver Contact Archive",
            "silver-contact-archive",
            "A photographic archive maintaining negatives, contact sheets, publicity stills, and licensed retrospectives.");

        public static CatalogStudio StageDoorEditions { get; } = Studio(
            "Stage Door Editions",
            "stage-door-editions",
            "A publisher of plays, screenplays, annotated shooting scripts, and production histories.");

        public static CatalogStudio MarginAndMeasurePress { get; } = Studio(
            "Margin & Measure Press",
            "margin-and-measure-press",
            "A publisher of fiction, essays, professional memoirs, and photography books.");

        public static CatalogStudio PromptCopyLibrary { get; } = Studio(
            "Prompt Copy Library",
            "prompt-copy-library",
            "An archival collection of screenplays, theatrical scripts, revisions, and production documents.");

        public static CatalogStudio SignalHouseAudio { get; } = Studio(
            "Signal House Audio",
            "signal-house-audio",
            "A producer of radio plays, cast recordings, archival interviews, and spoken-word work.");

        public static CatalogStudio NightWindowAudio { get; } = Studio(
            "Night Window Audio",
            "night-window-audio",
            "A contemporary producer of audiobooks, podcasts, oral histories, and serialized fiction.");

        public static CatalogStudio TheLongInterview { get; } = Studio(
            "The Long Interview",
            "the-long-interview",
            "A recurring radio program featuring extended professional conversations with performers and filmmakers.");

        public static IReadOnlyList<CatalogStudio> All { get; } =
        [
            BarelyDressedPictures,
            SecondTakeFeatures,
            ElectricMarquee,
            OpenSecretFilms,
            FourthWallPictures,
            TheLanternRoom,
            CherryPoppinsStudio,
            AvailableLightCooperative,
            SilverContactArchive,
            StageDoorEditions,
            MarginAndMeasurePress,
            PromptCopyLibrary,
            SignalHouseAudio,
            NightWindowAudio,
            TheLongInterview
        ];

        private static CatalogStudio Studio(string name, string slug, string description) => new(name, slug, description);
    }

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

        public static CatalogPerformer GideonSlate { get; } = Performer(
            "Gideon Slate",
            "gideon-slate",
            "An actor and director known for restrained delivery, physical confidence, and more than five decades portraying intelligence officer Sebastian Rook.");

        public static CatalogPerformer SimoneVale { get; } = Performer(
            "Simone Vale",
            "simone-vale",
            "A Black American stage actor and producer known for cerebral comedy, legal thrillers, and characters whose authority comes from preparation rather than intimidation.");

        public static CatalogPerformer MarisolVega { get; } = Performer(
            "Marisol Vega",
            "marisol-vega",
            "A Cuban-American choreographer and actor who moves from dance-centered supporting roles into physical comedy and direction of musical sequences.");

        public static CatalogPerformer KenjiWatanabe { get; } = Performer(
            "Kenji Watanabe",
            "kenji-watanabe",
            "A Japanese-American character actor and commercial photographer specializing in quiet observers who eventually reveal that they understand the entire plot.");

        public static CatalogPerformer JulianMarch { get; } = Performer(
            "Julian March",
            "julian-march",
            "A British actor known for deadpan romantic antagonists and elegant professional failures who later becomes a dependable television director.");

        public static CatalogPerformer AminaShaw { get; } = Performer(
            "Amina Shaw",
            "amina-shaw",
            "A British Pakistani actor and screenwriter who excels at fast dialogue, workplace farce, and characters attempting to impose procedure on chaos.");

        public static CatalogPerformer DariusKing { get; } = Performer(
            "Darius King",
            "darius-king",
            "A Black American actor and director known as an urbane ensemble lead whose characters are persuasive without always being correct.");

        public static CatalogPerformer RafaelSato { get; } = Performer(
            "Rafael Sato",
            "rafael-sato",
            "A Japanese-Brazilian actor and musician known for romantic comedy, musical performance, and an unusually relaxed screen presence.");

        public static CatalogPerformer TessNorth { get; } = Performer(
            "Tess North",
            "tess-north",
            "A Canadian stunt performer, actor, and action director whose characters are practical professionals surrounded by theatrical amateurs.");

        public static CatalogPerformer NiaHart { get; } = Performer(
            "Nia Hart",
            "nia-hart",
            "A Black British actor and producer who moves between intimate drama and workplace comedy before developing projects for ensemble casts.");

        public static CatalogPerformer DevMalik { get; } = Performer(
            "Dev Malik",
            "dev-malik",
            "An Indian-Canadian comic actor and composer known for verbal precision and characters who remain enthusiastic long after a plan has failed.");

        public static CatalogPerformer SofiaCalderon { get; } = Performer(
            "Sofia Calderón",
            "sofia-calderon",
            "A Mexican-American actor and documentary producer who begins in romantic comedy before moving into investigative and historical work.");

        public static CatalogPerformer EllisWard { get; } = Performer(
            "Ellis Ward",
            "ellis-ward",
            "An Irish actor with extensive radio experience, known for voice work, restrained comedy, and later audiobook narration.");

        public static CatalogPerformer BellaBloom { get; } = Performer(
            "Bella Bloom",
            "bella-bloom",
            "A playwright and actor-director known for controlled staging, exact dialogue, and ensemble performances.");

        public static CatalogPerformer ImaniCole { get; } = Performer(
            "Imani Cole",
            "imani-cole",
            "A Black American actor and producer known for intimate ensemble performances and careful project development.");

        public static CatalogPerformer ArunSen { get; } = Performer(
            "Arun Sen",
            "arun-sen",
            "An Indian-American actor and composer working across theatrical comedy, independent film, and music-driven drama.");

        public static CatalogPerformer LuciaFerrer { get; } = Performer(
            "Lucía Ferrer",
            "lucia-ferrer",
            "A Chilean-Spanish actor, stunt coordinator, and second-unit director known for practical action and dry comedy.");

        public static CatalogPerformer AmaraOkoye { get; } = Performer(
            "Amara Okoye",
            "amara-okoye",
            "A Nigerian-British actor, radio dramatist, and audio director who moves naturally between screen productions, audio plays, and literary adaptations.");

        public static CatalogPerformer JunePark { get; } = Performer(
            "June Park",
            "june-park",
            "A Korean-American actor and director specializing in elegant genre films with restrained humor.");

        public static CatalogPerformer EliasGrant { get; } = Performer(
            "Elias Grant",
            "elias-grant",
            "A Black British playwright and actor who becomes the new lead of the Sebastian Rook franchise without imitating Gideon Slate's performance.");

        public static CatalogPerformer NoorHaddad { get; } = Performer(
            "Noor Haddad",
            "noor-haddad",
            "An Arab-American actor and narrator with extensive audiobook and audio-drama work, known for controlled suspense and understated comedy.");

        public static IReadOnlyList<CatalogPerformer> All { get; } =
        [
            CherryPoppins,
            VelvetThunder,
            BeaHaven,
            RandyDandy,
            GideonSlate,
            SimoneVale,
            MarisolVega,
            KenjiWatanabe,
            JulianMarch,
            AminaShaw,
            DariusKing,
            RafaelSato,
            TessNorth,
            NiaHart,
            DevMalik,
            SofiaCalderon,
            EllisWard,
            BellaBloom,
            ImaniCole,
            ArunSen,
            LuciaFerrer,
            AmaraOkoye,
            JunePark,
            EliasGrant,
            NoorHaddad
        ];

        private static CatalogPerformer Performer(string name, string slug, string description) => new(name, slug, description);
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
