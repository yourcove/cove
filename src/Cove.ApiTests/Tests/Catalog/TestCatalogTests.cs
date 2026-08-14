using Cove.ApiTests.ExampleData;

namespace Cove.ApiTests.Tests.Catalog;

public sealed class TestCatalogTests
{
    [Fact]
    public void PerformersContainCompleteInUniverseRoster()
    {
        (string Name, string Slug)[] expected =
        [
            ("Cherry Poppins", "cherry-poppins"), ("Velvet Thunder", "velvet-thunder"),
            ("Bea Haven", "bea-haven"), ("Randy Dandy", "randy-dandy"), ("Gideon Slate", "gideon-slate"),
            ("Simone Vale", "simone-vale"), ("Marisol Vega", "marisol-vega"),
            ("Kenji Watanabe", "kenji-watanabe"), ("Julian March", "julian-march"),
            ("Amina Shaw", "amina-shaw"), ("Darius King", "darius-king"),
            ("Rafael Sato", "rafael-sato"), ("Tess North", "tess-north"),
            ("Nia Hart", "nia-hart"), ("Dev Malik", "dev-malik"),
            ("Sofia Calderón", "sofia-calderon"), ("Ellis Ward", "ellis-ward"),
            ("Bella Bloom", "bella-bloom"), ("Imani Cole", "imani-cole"),
            ("Arun Sen", "arun-sen"), ("Lucía Ferrer", "lucia-ferrer"),
            ("Amara Okoye", "amara-okoye"), ("June Park", "june-park"),
            ("Elias Grant", "elias-grant"), ("Noor Haddad", "noor-haddad")
        ];

        TestCatalog.Performers.All.Select(performer => (performer.Name, performer.Slug)).Should().Equal(expected);
        TestCatalog.Performers.All.Should().OnlyContain(performer => !string.IsNullOrWhiteSpace(performer.Description));
    }

    [Fact]
    public void StudiosContainCompleteInUniverseOrganizationRoster()
    {
        (string Name, string Slug)[] expected =
        [
            ("Barely Dressed Pictures", "barely-dressed-pictures"),
            ("Second Take Features", "second-take-features"), ("Electric Marquee", "electric-marquee"),
            ("Open Secret Films", "open-secret-films"), ("Fourth Wall Pictures", "fourth-wall-pictures"),
            ("The Lantern Room", "the-lantern-room"), ("Cherry Poppins Studio", "cherry-poppins-studio"),
            ("Available Light Cooperative", "available-light-cooperative"),
            ("Silver Contact Archive", "silver-contact-archive"), ("Stage Door Editions", "stage-door-editions"),
            ("Margin & Measure Press", "margin-and-measure-press"), ("Prompt Copy Library", "prompt-copy-library"),
            ("Signal House Audio", "signal-house-audio"), ("Night Window Audio", "night-window-audio"),
            ("The Long Interview", "the-long-interview")
        ];

        TestCatalog.Studios.All.Select(studio => (studio.Name, studio.Slug)).Should().Equal(expected);
        TestCatalog.Studios.All.Should().OnlyContain(studio => !string.IsNullOrWhiteSpace(studio.Description));
        TestCatalog.Studio.Should().BeSameAs(TestCatalog.Studios.BarelyDressedPictures);
    }
}
