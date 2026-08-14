using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Metadata;

[Collection(ApiTestLane1Collection.Name)]
public sealed class PerformerMetadataServiceApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenPulpMovieDbPerformer_WhenCherryPoppinsIsSearchedByName_ThenMetadataCanBeImported()
    {
        const string performerProfileUrl = "https://pulpmoviedb.example/performers/cherry-poppins";
        var catalogPerformer = TestCatalog.Performers.CherryPoppins;
        var pulpMovieDb = TestCatalog.MetadataServices.PulpMovieDb;
        var metadataPerformer = AsMetadataService().CreatePerformer(
            new MetadataServicePerformerBuilder()
                .WithId("cherry-poppins")
                .WithName(catalogPerformer.Name)
                .WithDisambiguation("the theatrical entrance specialist")
                .WithAlias("Cherry Pop-In")
                .WithGender("FEMALE")
                .WithBirthDate("1992-04-01")
                .WithEthnicity("Caucasian")
                .WithCountry("United Kingdom")
                .WithEyeColor("Hazel")
                .WithHairColor("Auburn")
                .WithHeightCm(168)
                .WithCareerStartYear(2014)
                .WithUrl(performerProfileUrl)
                .Build());
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(catalogPerformer.Name)
                .Build());

        var matches = await AsUser().SearchPerformerMetadataServiceAsync(
            performer,
            catalogPerformer.Name,
            metadataPerformer);
        var match = matches.Should().ContainSingle().Which;
        match.MetadataServerName.Should().Be(pulpMovieDb.Name);
        match.Id.Should().Be(metadataPerformer.Id);
        match.Name.Should().Be(catalogPerformer.Name);
        match.Urls.Should().ContainSingle().Which.Should().Be(performerProfileUrl);

        var imported = await AsUser().ImportPerformerFromMetadataServiceAsync(performer, match);

        imported.Name.Should().Be(catalogPerformer.Name);
        imported.Disambiguation.Should().Be("the theatrical entrance specialist");
        imported.Aliases.Should().ContainSingle().Which.Should().Be("Cherry Pop-In");
        imported.Gender.Should().Be("Female");
        imported.Birthdate.Should().Be("1992-04-01");
        imported.Ethnicity.Should().Be("Caucasian");
        imported.Country.Should().Be("United Kingdom");
        imported.EyeColor.Should().Be("Hazel");
        imported.HairColor.Should().Be("Auburn");
        imported.HeightCm.Should().Be(168);
        imported.CareerStart.Should().Be("2014-01-01");
        imported.Urls.Should().Contain(performerProfileUrl);
        imported.RemoteIds.Should().ContainSingle(remoteId =>
            remoteId.Endpoint == metadataPerformer.Endpoint.AbsoluteUri
            && remoteId.RemoteId == metadataPerformer.Id);
    }
}
