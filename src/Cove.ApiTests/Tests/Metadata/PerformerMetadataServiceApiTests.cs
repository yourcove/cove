using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Metadata;

public sealed class PerformerMetadataServiceApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/performers/{id:int}/metadata-server/search")]
    [CoversEndpoint("POST", "/api/performers/{id:int}/metadata-server/import")]
    public async Task GivenPulpMovieDbPerformer_WhenCherryPoppinsIsSearchedByName_ThenMetadataCanBeImported()
    {
        // Arrange
        const string performerProfileUrl = "https://pulpmoviedb.example/performers/cherry-poppins";
        var owner = AsUser();
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
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder()
                .WithName(catalogPerformer.Name)
                .Build(), TestContext.Current.CancellationToken);

        // Act & Assert
        var fallbackMatches = await owner.SearchPerformerMetadataServiceAsync(performer, string.Empty, metadataPerformer, TestContext.Current.CancellationToken);
        fallbackMatches.Should().ContainSingle().Which.Id.Should().Be(metadataPerformer.Id);

        var matches = await owner.SearchPerformerMetadataServiceAsync(performer, catalogPerformer.Name, metadataPerformer, TestContext.Current.CancellationToken);
        var match = matches.Should().ContainSingle().Which;
        match.MetadataServerName.Should().Be(pulpMovieDb.Name);
        match.Id.Should().Be(metadataPerformer.Id);
        match.Name.Should().Be(catalogPerformer.Name);
        match.Urls.Should().ContainSingle().Which.Should().Be(performerProfileUrl);

        var imported = await owner.ImportPerformerFromMetadataServiceAsync(performer, match, TestContext.Current.CancellationToken);

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

        var persisted = await owner.GetPerformerByIdAsync(imported.Id, TestContext.Current.CancellationToken);
        persisted.Name.Should().Be(catalogPerformer.Name);
        persisted.Disambiguation.Should().Be("the theatrical entrance specialist");
        persisted.Aliases.Should().ContainSingle().Which.Should().Be("Cherry Pop-In");
        persisted.Gender.Should().Be("Female");
        persisted.Birthdate.Should().Be("1992-04-01");
        persisted.Ethnicity.Should().Be("Caucasian");
        persisted.Country.Should().Be("United Kingdom");
        persisted.EyeColor.Should().Be("Hazel");
        persisted.HairColor.Should().Be("Auburn");
        persisted.HeightCm.Should().Be(168);
        persisted.CareerStart.Should().Be("2014-01-01");
        persisted.Urls.Should().Contain(performerProfileUrl);
        persisted.RemoteIds.Should().ContainSingle(remoteId =>
            remoteId.Endpoint == metadataPerformer.Endpoint.AbsoluteUri
            && remoteId.RemoteId == metadataPerformer.Id);

        var existingRemoteMatches = await owner.SearchPerformerMetadataServiceAsync(persisted, string.Empty, metadataPerformer, TestContext.Current.CancellationToken);
        existingRemoteMatches.Should().ContainSingle().Which.Id.Should().Be(metadataPerformer.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/performers/metadata-server/find-by-ids")]
    [CoversEndpoint("POST", "/api/performers/{id:int}/metadata-server/submit-draft")]
    [CoversEndpoint("POST", "/api/performers/metadata-server/batch-tag")]
    public async Task GivenFixtureMetadataPerformers_WhenFindDraftAndBatchTagRun_ThenAuthorizationSubmissionsAndPersistenceAreExact()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var metadataPerformer = AsMetadataService().CreatePerformer(
            new MetadataServicePerformerBuilder()
                .WithId($"remote-performer-{suffix}")
                .WithName($"Remote metadata performer {suffix}")
                .WithDisambiguation("draft and import")
                .WithAlias($"Remote alias {suffix}")
                .WithGender("FEMALE")
                .WithBirthDate("1992-04-01")
                .WithEthnicity("Caucasian")
                .WithCountry("United Kingdom")
                .WithEyeColor("Hazel")
                .WithHairColor("Auburn")
                .WithHeightCm(168)
                .WithCareerStartYear(2014)
                .WithUrl($"https://metadata.example/performers/{suffix}")
                .Build());
        var batchMetadataPerformer = AsMetadataService().CreatePerformer(
            new MetadataServicePerformerBuilder()
                .WithId($"remote-batch-performer-{suffix}")
                .WithName($"Remote batch performer {suffix}")
                .WithDisambiguation("batch import")
                .WithAlias($"Batch alias {suffix}")
                .WithGender("MALE")
                .WithUrl($"https://metadata.example/performers/batch-{suffix}")
                .Build());
        var preservedBatchAlias = $"Preserved batch alias {suffix}";
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder()
                .WithName(metadataPerformer.Performer.Name)
                .WithDetails($"Local metadata details {suffix}")
                .Build(), TestContext.Current.CancellationToken);
        var batchPerformer = await owner.CreatePerformerAsync(new PerformerBuilder()
                .WithName(batchMetadataPerformer.Performer.Name)
                .WithDisambiguation(batchMetadataPerformer.Performer.Disambiguation!)
                .WithDetails($"Local batch details {suffix}")
                .WithAlias(preservedBatchAlias)
                .Build(), TestContext.Current.CancellationToken);
        var unmatchedPerformer = await owner.CreatePerformerAsync(new PerformerBuilder()
                .WithName($"Unmatched batch performer {suffix}")
                .WithDetails($"Unmatched batch details {suffix}")
                .WithAlias($"Unmatched batch alias {suffix}")
                .Build(), TestContext.Current.CancellationToken);
        var beforeForbiddenPerformer = await owner.GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken);
        var beforeForbiddenBatchPerformer = await owner.GetPerformerByIdAsync(batchPerformer.Id, TestContext.Current.CancellationToken);
        var beforeUnmatchedPerformer = await owner.GetPerformerByIdAsync(unmatchedPerformer.Id, TestContext.Current.CancellationToken);

        var noRoleUsername = $"performer-metadata-no-role-{suffix}";
        var viewerUsername = $"performer-metadata-viewer-{suffix}";
        const string password = "Performer metadata permissions 123!";
        await owner.CreateUserAsync(new CreateUserRequest(noRoleUsername, password, Roles: []), TestContext.Current.CancellationToken);
        await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, password, Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var noRoleSession = await owner.CreateAuthSessionAsync(noRoleUsername, password, TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, password, TestContext.Current.CancellationToken);
        var noRole = noRoleSession.Client;
        var viewer = viewerSession.Client;
        var viewerBatchRequest = new MetadataServerPerformerBatchTagRequestDto
        {
            Endpoint = batchMetadataPerformer.Endpoint.AbsoluteUri,
            Ids = [batchPerformer.Id],
        };

        var forbiddenFind = () => noRole.FindPerformerMetadataServiceByIdsAsync(metadataPerformer, [metadataPerformer.Id]);
        await forbiddenFind.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");

        var viewerMatches = await viewer.FindPerformerMetadataServiceByIdsAsync(metadataPerformer, [metadataPerformer.Id, $"missing-{suffix}", metadataPerformer.Id], TestContext.Current.CancellationToken);
        AssertPerformerMatch(viewerMatches.Should().ContainSingle().Which, metadataPerformer);

        var forbiddenWrites = new Func<Task>[]
        {
            async () => _ = await viewer.ImportPerformerFromMetadataServiceAsync(performer, viewerMatches.Single()),
            async () => _ = await viewer.SubmitPerformerDraftToMetadataServiceAsync(performer, metadataPerformer),
            async () => _ = await viewer.StartPerformerMetadataBatchTagAsync(viewerBatchRequest),
        };
        foreach (var forbiddenWrite in forbiddenWrites)
            await forbiddenWrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");

        AsMetadataService().PerformerDraftSubmissions.Should().BeEmpty();
        AssertUnchanged(await owner.GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken), beforeForbiddenPerformer);
        AssertUnchanged(await owner.GetPerformerByIdAsync(batchPerformer.Id, TestContext.Current.CancellationToken), beforeForbiddenBatchPerformer);
        AssertUnchanged(await owner.GetPerformerByIdAsync(unmatchedPerformer.Id, TestContext.Current.CancellationToken), beforeUnmatchedPerformer);

        var foundByIds = await owner.FindPerformerMetadataServiceByIdsAsync(metadataPerformer, [metadataPerformer.Id, $"missing-{suffix}", metadataPerformer.Id], TestContext.Current.CancellationToken);
        AssertPerformerMatch(foundByIds.Should().ContainSingle().Which, metadataPerformer);
        (await owner.FindPerformerMetadataServiceByIdsAsync(metadataPerformer, [], TestContext.Current.CancellationToken)).Should().BeEmpty();

        var imported = await owner.ImportPerformerFromMetadataServiceAsync(performer, foundByIds.Single(), TestContext.Current.CancellationToken);
        AssertImportedPerformer(imported, metadataPerformer);

        var draftId = await owner.SubmitPerformerDraftToMetadataServiceAsync(imported, metadataPerformer, TestContext.Current.CancellationToken);
        draftId.Should().Be("draft-1");
        var draft = AsMetadataService().PerformerDraftSubmissions.Should().ContainSingle().Which;
        draft.DraftId.Should().Be(draftId);
        draft.Input.GetProperty("id").GetString().Should().Be(metadataPerformer.Id);
        draft.Input.GetProperty("name").GetString().Should().Be(metadataPerformer.Performer.Name);
        draft.Input.GetProperty("disambiguation").GetString().Should().Be(metadataPerformer.Performer.Disambiguation);
        draft.Input.GetProperty("aliases").GetString().Should().Be(metadataPerformer.Performer.Aliases.Single());
        draft.Input.GetProperty("gender").GetString().Should().Be("FEMALE");
        draft.Input.GetProperty("birthdate").GetString().Should().Be(metadataPerformer.Performer.BirthDate);
        draft.Input.GetProperty("ethnicity").GetString().Should().Be(metadataPerformer.Performer.Ethnicity);
        draft.Input.GetProperty("country").GetString().Should().Be(metadataPerformer.Performer.Country);
        draft.Input.GetProperty("eye_color").GetString().Should().Be(metadataPerformer.Performer.EyeColor);
        draft.Input.GetProperty("hair_color").GetString().Should().Be(metadataPerformer.Performer.HairColor);
        draft.Input.GetProperty("height").GetString().Should().Be("168");
        draft.Input.GetProperty("career_start_year").GetInt32().Should().Be(2014);
        draft.Input.GetProperty("urls").EnumerateArray().Select(url => url.GetString())
            .Should().Equal(metadataPerformer.Performer.Urls);

        var batchRequest = new MetadataServerPerformerBatchTagRequestDto
        {
            Endpoint = batchMetadataPerformer.Endpoint.AbsoluteUri,
            Ids = [batchPerformer.Id, unmatchedPerformer.Id],
            RefreshAlreadyTagged = true,
            ExcludeFields = ["aliases"],
        };
        var batchStart = await owner.StartPerformerMetadataBatchTagAsync(batchRequest, TestContext.Current.CancellationToken);
        batchStart.ItemCount.Should().Be(2);
        var batchJob = await owner.WaitForTerminalJobAsync(batchStart.JobId, TestContext.Current.CancellationToken);
        batchJob.Status.Should().Be(JobStatus.Completed);
        batchJob.Type.Should().Be("metadata-server:performers");
        batchJob.Error.Should().BeNull();
        var batchUpdated = await owner.GetPerformerByIdAsync(batchPerformer.Id, TestContext.Current.CancellationToken);
        batchUpdated.Name.Should().Be(batchMetadataPerformer.Performer.Name);
        batchUpdated.Disambiguation.Should().Be(batchMetadataPerformer.Performer.Disambiguation);
        batchUpdated.Gender.Should().Be("Male");
        batchUpdated.Urls.Should().Equal(batchMetadataPerformer.Performer.Urls);
        batchUpdated.Aliases.Should().Equal(preservedBatchAlias);
        batchUpdated.RemoteIds.Should().ContainSingle(remoteId =>
            remoteId.Endpoint == batchMetadataPerformer.Endpoint.AbsoluteUri
            && remoteId.RemoteId == batchMetadataPerformer.Id);
        AssertUnchanged(await owner.GetPerformerByIdAsync(unmatchedPerformer.Id, TestContext.Current.CancellationToken), beforeUnmatchedPerformer);

        var driftedBatchPerformer = await owner.UpdatePerformerAsync(batchPerformer.Id, new { gender = "Female" }, TestContext.Current.CancellationToken);
        driftedBatchPerformer.Gender.Should().Be("Female");
        driftedBatchPerformer.Aliases.Should().Equal(preservedBatchAlias);

        var filteredBatchRequest = new MetadataServerPerformerBatchTagRequestDto
        {
            Endpoint = batchMetadataPerformer.Endpoint.AbsoluteUri,
            SelectAll = true,
            Filter = new PerformerFilter { Name = batchMetadataPerformer.Performer.Name },
            RefreshAlreadyTagged = true,
            ExcludeFields = ["aliases"],
        };
        var filteredBatchStart = await owner.StartPerformerMetadataBatchTagAsync(filteredBatchRequest, TestContext.Current.CancellationToken);
        filteredBatchStart.ItemCount.Should().Be(1);
        var filteredBatchJob = await owner.WaitForTerminalJobAsync(filteredBatchStart.JobId, TestContext.Current.CancellationToken);
        filteredBatchJob.Status.Should().Be(JobStatus.Completed);
        filteredBatchJob.Type.Should().Be("metadata-server:performers");
        filteredBatchJob.Error.Should().BeNull();
        var filteredBatchUpdated = await owner.GetPerformerByIdAsync(batchPerformer.Id, TestContext.Current.CancellationToken);
        filteredBatchUpdated.Gender.Should().Be("Male");
        filteredBatchUpdated.Aliases.Should().Equal(preservedBatchAlias);
        filteredBatchUpdated.RemoteIds.Should().Equal(batchUpdated.RemoteIds);
        AssertUnchanged(await owner.GetPerformerByIdAsync(unmatchedPerformer.Id, TestContext.Current.CancellationToken), beforeUnmatchedPerformer);
    }

    private static void AssertPerformerMatch(
        MetadataServerPerformerMatchDto match,
        MetadataServicePerformerHandle metadataPerformer)
    {
        match.Endpoint.Should().Be(metadataPerformer.Endpoint.AbsoluteUri);
        match.Id.Should().Be(metadataPerformer.Id);
        match.Name.Should().Be(metadataPerformer.Performer.Name);
        match.Disambiguation.Should().Be(metadataPerformer.Performer.Disambiguation);
        match.Aliases.Should().Equal(metadataPerformer.Performer.Aliases);
        match.Urls.Should().Equal(metadataPerformer.Performer.Urls);
    }

    private static void AssertImportedPerformer(
        PerformerDto performer,
        MetadataServicePerformerHandle metadataPerformer)
    {
        performer.Name.Should().Be(metadataPerformer.Performer.Name);
        performer.Disambiguation.Should().Be(metadataPerformer.Performer.Disambiguation);
        performer.Aliases.Should().Equal(metadataPerformer.Performer.Aliases);
        performer.Gender.Should().Be(ToCoveGender(metadataPerformer.Performer.Gender));
        performer.Birthdate.Should().Be(metadataPerformer.Performer.BirthDate);
        performer.Ethnicity.Should().Be(metadataPerformer.Performer.Ethnicity);
        performer.Country.Should().Be(metadataPerformer.Performer.Country);
        performer.EyeColor.Should().Be(metadataPerformer.Performer.EyeColor);
        performer.HairColor.Should().Be(metadataPerformer.Performer.HairColor);
        performer.HeightCm.Should().Be(metadataPerformer.Performer.HeightCm);
        performer.CareerStart.Should().Be(metadataPerformer.Performer.CareerStartYear is int year
            ? $"{year:0000}-01-01"
            : null);
        performer.Urls.Should().Equal(metadataPerformer.Performer.Urls);
        performer.RemoteIds.Should().ContainSingle(remoteId =>
            remoteId.Endpoint == metadataPerformer.Endpoint.AbsoluteUri
            && remoteId.RemoteId == metadataPerformer.Id);
    }

    private static string? ToCoveGender(string? gender)
        => string.IsNullOrWhiteSpace(gender)
            ? null
            : $"{char.ToUpperInvariant(gender[0])}{gender[1..].ToLowerInvariant()}";

    private static void AssertUnchanged(PerformerDto actual, PerformerDto before)
    {
        actual.Id.Should().Be(before.Id);
        actual.Name.Should().Be(before.Name);
        actual.Details.Should().Be(before.Details);
        actual.Disambiguation.Should().Be(before.Disambiguation);
        actual.Aliases.Should().Equal(before.Aliases);
        actual.Urls.Should().Equal(before.Urls);
        actual.RemoteIds.Should().Equal(before.RemoteIds);
    }
}
