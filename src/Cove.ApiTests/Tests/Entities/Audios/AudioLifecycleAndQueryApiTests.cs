using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Audios;

public sealed class AudioLifecycleAndQueryApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/audios")]
    [CoversEndpoint("GET", "/api/audios/{id:int}")]
    public async Task GivenAudioMetadata_WhenMemberCreatesAndReadsIt_ThenRelationshipsRoundTrip()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync($"Audio studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var tag = await AsUser().CreateTagAsync($"Audio tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Audio performer {Guid.NewGuid():N}")
            .Build(), TestContext.Current.CancellationToken);
        var group = await AsUser().CreateGroupAsync($"Audio group {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var request = new AudioBuilder()
            .WithTitle("  Audio lifecycle title  ")
            .WithCode("  AUDIO-CODE  ")
            .WithDetails("  Audio details  ")
            .WithDate("2026-08-01")
            .WithStudio(studio)
            .WithUrl("  https://audio.example/item  ")
            .WithTag(tag)
            .WithPerformer(performer)
            .WithGroup(group)
            .AsOrganized()
            .Build();

        // Act
        var created = await AsUser(ApiTestUsers.Eva).CreateAudioAsync(request, TestContext.Current.CancellationToken);
        var retrieved = await AsUser(ApiTestUsers.Eva).GetAudioByIdAsync(created.Id, TestContext.Current.CancellationToken);

        // Assert
        retrieved.Title.Should().Be("Audio lifecycle title");
        retrieved.Code.Should().Be("AUDIO-CODE");
        retrieved.Details.Should().Be("Audio details");
        retrieved.Date.Should().Be("2026-08-01");
        retrieved.Organized.Should().BeTrue();
        retrieved.StudioId.Should().Be(studio.Id);
        retrieved.Urls.Should().Equal("https://audio.example/item");
        retrieved.Tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
        retrieved.Performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
        retrieved.Groups.Should().ContainSingle(candidate => candidate.Id == group.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/audios/from-file")]
    [CoversEndpoint("GET", "/api/audios/{id:int}/stream")]
    [CoversEndpoint("POST", "/api/audios/{id:int}/rescan")]
    public async Task GivenPcmWaveFile_WhenMemberImportsStreamsAndRequestsRescan_ThenFileIdentityAndStreamsRemainConsistent()
    {
        // Arrange
        const string fileName = "audio-file-lifecycle.wav";
        var fileSystem = AsTestFileSystem();
        var path = fileSystem.CreatePcmWaveFile(fileName, sampleFrames: 8_000);
        var expectedStream = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // Act
        var created = await AsUser(ApiTestUsers.Eva).CreateAudioFromFileAsync(path, TestContext.Current.CancellationToken);
        var read = await AsUser(ApiTestUsers.Eva).GetAudioByIdAsync(created.Id, TestContext.Current.CancellationToken);
        var streamed = await AsUser(ApiTestUsers.Eva).GetAudioStreamAsync(created.Id, TestContext.Current.CancellationToken);
        var createdFile = created.Files.Should().ContainSingle().Which;
        var originalFile = read.Files.Should().ContainSingle().Which;
        fileSystem.ReplacePcmWaveFile(path, sampleFrames: 16_000);
        var expectedRescannedStream = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var jobId = await AsUser(ApiTestUsers.Eva).RescanAudioAsync(created.Id, TestContext.Current.CancellationToken);
        var job = await AsUser(ApiTestUsers.Eva).WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);
        var rescanned = await AsUser(ApiTestUsers.Eva).GetAudioByIdAsync(created.Id, TestContext.Current.CancellationToken);
        var rescannedFile = rescanned.Files.Should().ContainSingle().Which;
        var rescannedStream = await AsUser(ApiTestUsers.Eva).GetAudioStreamAsync(created.Id, TestContext.Current.CancellationToken);

        // Assert
        created.Title.Should().Be(Path.GetFileNameWithoutExtension(path));
        created.FileCount.Should().Be(1);
        createdFile.Path.Should().Be(path);
        createdFile.Basename.Should().Be(fileName);
        createdFile.Format.Should().Be("wav");
        createdFile.Size.Should().Be(expectedStream.Length);
        read.Id.Should().Be(created.Id);
        read.Title.Should().Be(Path.GetFileNameWithoutExtension(path));
        read.FileCount.Should().Be(1);
        originalFile.Id.Should().Be(createdFile.Id);
        originalFile.Path.Should().Be(path);
        originalFile.Basename.Should().Be(fileName);
        originalFile.Format.Should().Be("wav");
        originalFile.Size.Should().Be(expectedStream.Length);
        originalFile.Duration.Should().Be(createdFile.Duration);
        streamed.MediaType.Should().Be("audio/wav");
        streamed.Content.Should().Equal(expectedStream);
        job.Status.Should().Be(JobStatus.Completed);
        rescanned.Id.Should().Be(created.Id);
        rescanned.FileCount.Should().Be(1);
        rescannedFile.Id.Should().Be(originalFile.Id);
        rescannedFile.Path.Should().Be(path);
        rescannedStream.MediaType.Should().Be("audio/wav");
        rescannedStream.Content.Should().Equal(expectedRescannedStream);
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/audios/{id:int}")]
    public async Task GivenAudioMetadata_WhenMemberPartiallyUpdatesIt_ThenResponseAndReadPreserveRelationships()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync($"Original audio studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var tag = await AsUser().CreateTagAsync($"Original audio tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Original audio performer {Guid.NewGuid():N}")
            .Build(), TestContext.Current.CancellationToken);
        var group = await AsUser().CreateGroupAsync($"Original audio group {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var audio = await AsUser().CreateAudioAsync(new AudioBuilder()
            .WithTitle($"Original audio {Guid.NewGuid():N}")
            .WithCode("ORIGINAL-CODE")
            .WithDetails("Original details")
            .WithDate("2026-08-02")
            .WithStudio(studio)
            .WithUrl("https://audio.example/original")
            .WithTag(tag)
            .WithPerformer(performer)
            .WithGroup(group)
            .Build(), TestContext.Current.CancellationToken);

        // Act
        var updated = await AsUser(ApiTestUsers.Eva).UpdateAudioAsync(audio.Id, new
        {
            title = "Updated audio title",
            details = "Updated details",
            urls = new[] { "https://audio.example/updated" },
            clearFields = new[] { "studioId" },
        }, TestContext.Current.CancellationToken);
        var retrieved = await AsUser().GetAudioByIdAsync(audio.Id, TestContext.Current.CancellationToken);

        // Assert
        updated.Title.Should().Be("Updated audio title");
        updated.Urls.Should().Equal("https://audio.example/updated");
        retrieved.Urls.Should().Equal(updated.Urls);
        retrieved.Details.Should().Be("Updated details");
        retrieved.Code.Should().Be("ORIGINAL-CODE");
        retrieved.Date.Should().Be("2026-08-02");
        retrieved.StudioId.Should().BeNull();
        retrieved.Tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
        retrieved.Performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
        retrieved.Groups.Should().ContainSingle(candidate => candidate.Id == group.Id);
    }

    [Fact]
    public async Task GivenMissingAudio_WhenReadOrUpdated_ThenNotFoundIsReturned()
    {
        const int missingId = int.MaxValue;

        var read = () => AsUser().GetAudioByIdAsync(missingId);
        var update = () => AsUser().UpdateAudioAsync(missingId, new { details = "Missing" });

        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await update.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/audios/find")]
    public async Task GivenMatchingAudios_WhenFilteredAndPaged_ThenOnlyTheRequestedPageIsReturned()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var first = await AsUser().CreateAudioAsync(new AudioBuilder().WithTitle($"A filtered audio {suffix}").AsOrganized().Build(), TestContext.Current.CancellationToken);
        var second = await AsUser().CreateAudioAsync(new AudioBuilder().WithTitle($"B filtered audio {suffix}").AsOrganized().Build(), TestContext.Current.CancellationToken);
        await AsUser().CreateAudioAsync(new AudioBuilder().WithTitle($"Excluded audio {suffix}").Build(), TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<AudioFilter>
        {
            ObjectFilter = new AudioFilter { OrganizedCriterion = new BoolCriterion { Value = true } },
            FindFilter = new FindFilter { Q = suffix, Page = 2, PerPage = 1, Sort = "title" },
        };

        // Act
        var result = await AsUser(ApiTestUsers.Eva).FindAudiosAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.PerPage.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        result.Items.Should().NotContain(candidate => candidate.Id == first.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/audios/aggregate")]
    public async Task GivenSelectedAudios_WhenAggregated_ThenNonzeroTotalsAreScopedToSelection()
    {
        // Arrange
        var first = await AsUser().CreateAudioAsync($"Aggregate audio first {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var second = await AsUser().CreateAudioAsync($"Aggregate audio second {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var excluded = await AsUser().CreateAudioAsync($"Aggregate audio excluded {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await AsDbUser().AttachAudioFileAsync(first.Id, duration: 12.5, size: 1_000, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachAudioFileAsync(second.Id, duration: 7.25, size: 2_500, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachAudioFileAsync(excluded.Id, duration: 90, size: 9_999, cancellationToken: TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<AudioFilter> { Ids = [first.Id, second.Id] };

        // Act
        var aggregate = await AsUser(ApiTestUsers.Eva).AggregateAudiosAsync(request, TestContext.Current.CancellationToken);

        // Assert
        aggregate.Should().Be(new AudioAggregate(Count: 2, Duration: 19.75, FileSize: 3_500));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/audios/bulk")]
    public async Task GivenAudiosWithRelationships_WhenMemberBulkSetsValues_ThenOnlySelectedAudiosChange()
    {
        // Arrange
        var originalStudio = await AsUser().CreateStudioAsync($"Original bulk audio studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var originalTag = await AsUser().CreateTagAsync($"Original bulk audio tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var originalPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Original bulk audio performer {Guid.NewGuid():N}")
            .Build(), TestContext.Current.CancellationToken);
        var replacementTag = await AsUser().CreateTagAsync($"Replacement bulk audio tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Replacement bulk audio performer {Guid.NewGuid():N}")
            .Build(), TestContext.Current.CancellationToken);
        var selected = await Task.WhenAll(Enumerable.Range(1, 2).Select(index => AsUser().CreateAudioAsync(new AudioBuilder()
            .WithTitle($"Selected bulk audio {index} {Guid.NewGuid():N}")
            .WithCode($"ORIGINAL-{index}")
            .WithDetails($"Original details {index}")
            .WithDate("2026-08-03")
            .WithStudio(originalStudio)
            .WithTag(originalTag)
            .WithPerformer(originalPerformer)
            .Build())));
        var unselected = await AsUser().CreateAudioAsync(new AudioBuilder()
            .WithTitle($"Unselected bulk audio {Guid.NewGuid():N}")
            .WithCode("CONTROL-CODE")
            .WithDetails("Control details")
            .WithDate("2026-08-04")
            .WithStudio(originalStudio)
            .WithTag(originalTag)
            .WithPerformer(originalPerformer)
            .Build(), TestContext.Current.CancellationToken);
        var request = new BulkAudioUpdateDto
        {
            Ids = selected.Select(audio => audio.Id).ToList(),
            ClearFields = ["studioId", "date"],
            Organized = true,
            Code = "BULK-CODE",
            Details = "Bulk details",
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Set,
            PerformerIds = [replacementPerformer.Id],
            PerformerMode = BulkUpdateMode.Set,
        };

        // Act
        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateAudiosAsync(request, TestContext.Current.CancellationToken);
        var updated = await Task.WhenAll(selected.Select(audio => AsUser().GetAudioByIdAsync(audio.Id)));
        var control = await AsUser().GetAudioByIdAsync(unselected.Id, TestContext.Current.CancellationToken);

        // Assert
        updatedCount.Should().Be(2);
        updated.Should().AllSatisfy(audio =>
        {
            audio.Code.Should().Be("BULK-CODE");
            audio.Details.Should().Be("Bulk details");
            audio.Organized.Should().BeTrue();
            audio.StudioId.Should().BeNull();
            audio.Date.Should().BeNull();
            audio.Tags.Should().ContainSingle(candidate => candidate.Id == replacementTag.Id);
            audio.Performers.Should().ContainSingle(candidate => candidate.Id == replacementPerformer.Id);
        });
        control.Code.Should().Be("CONTROL-CODE");
        control.Details.Should().Be("Control details");
        control.Organized.Should().BeFalse();
        control.StudioId.Should().Be(originalStudio.Id);
        control.Date.Should().Be("2026-08-04");
        control.Tags.Should().ContainSingle(candidate => candidate.Id == originalTag.Id);
        control.Performers.Should().ContainSingle(candidate => candidate.Id == originalPerformer.Id);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/audios/{id:int}")]
    public async Task GivenAudio_WhenOwnerDeletesIt_ThenItCanNoLongerBeRead()
    {
        var audio = await AsUser().CreateAudioAsync($"Single delete audio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        await AsUser().DeleteAudioAsync(audio.Id, cancellationToken: TestContext.Current.CancellationToken);

        var read = () => AsUser().GetAudioByIdAsync(audio.Id);
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenAudio_WhenMemberDeletesIt_ThenForbiddenIsReturnedWithoutRemovingIt()
    {
        var audio = await AsUser().CreateAudioAsync($"Protected delete audio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        var deletion = () => AsUser(ApiTestUsers.Eva).DeleteAudioAsync(audio.Id);

        await deletion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetAudioByIdAsync(audio.Id, TestContext.Current.CancellationToken)).Id.Should().Be(audio.Id);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/audios/bulk")]
    public async Task GivenAudiosAndMissingId_WhenBulkDeleteRuns_ThenPermissionAndSelectionAreEnforced()
    {
        // Arrange
        var first = await AsUser().CreateAudioAsync($"Bulk delete audio first {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var second = await AsUser().CreateAudioAsync($"Bulk delete audio second {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var retained = await AsUser().CreateAudioAsync($"Bulk delete audio retained {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var request = new BatchDeleteDto([first.Id, int.MaxValue, second.Id]);

        // Act
        var forbidden = () => AsUser(ApiTestUsers.Eva).BulkDeleteAudiosAsync(request);
        await forbidden.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        await AsUser().BulkDeleteAudiosAsync(request, TestContext.Current.CancellationToken);

        // Assert
        foreach (var deleted in new[] { first, second })
        {
            var read = () => AsUser().GetAudioByIdAsync(deleted.Id);
            await read.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 404 (NotFound)*");
        }
        (await AsUser().GetAudioByIdAsync(retained.Id, TestContext.Current.CancellationToken)).Id.Should().Be(retained.Id);
    }

}
