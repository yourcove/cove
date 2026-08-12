using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Cove.Tests;

[Collection("Managed Postgres integration")]
public sealed class Phase12SchemaParityTests
{
    private const string V1BaselineMigrationId = "20260516223910_V1_0";
    private const string NameRuleCompatibilityCheckpointMigrationId = "20260808123000_SavedFilterStringModes";
    private const string NameRuleEnforcementMigrationId = NameRuleEnforcementService.MigrationId;
    [Fact]
    public void NameRuleEnforcementMigration_FollowsTheCompatibilityCheckpoint()
    {
        using var context = CreateContext(5432, "migration-order-fixture");
        AssertNoPendingModelChanges(context);
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.ToArray();

        var compatibilityCheckpoint = Array.IndexOf(migrations, NameRuleCompatibilityCheckpointMigrationId);
        var enforcement = Array.IndexOf(migrations, NameRuleEnforcementMigrationId);
        Assert.True(compatibilityCheckpoint >= 0);
        Assert.True(enforcement > compatibilityCheckpoint);
    }

    [Fact]
    public async Task NameRuleEnforcementMigration_UpgradesAFullyCleanedCheckpoint()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"tag_namespace_clean_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using var context = CreateContext(environment.Port, databaseName);
            await context.GetService<IMigrator>().MigrateAsync(NameRuleCompatibilityCheckpointMigrationId);
            var firstTagId = await InsertTagAsync(environment.Port, databaseName, " \u00a0Alpha\u00a0 ");
            var secondTagId = await InsertTagAsync(environment.Port, databaseName, "Gamma");
            var aliasId = await InsertAliasAsync(environment.Port, databaseName, firstTagId, "\u2003Beta\u2003");
            var longNameComponent = BuildLongNameComponent();
            var longAliasId = await InsertAliasAsync(environment.Port, databaseName, secondTagId, longNameComponent);
            var performerId = await InsertPerformerAsync(environment.Port, databaseName, " Performer ", " Role ");
            var blankDisambiguationPerformerId = await InsertPerformerAsync(environment.Port, databaseName, " Solo ", " \t ");
            var longDisambiguationPerformerId = await InsertPerformerAsync(
                environment.Port,
                databaseName,
                "Long identity performer",
                longNameComponent);
            var studioId = await InsertStudioAsync(environment.Port, databaseName, " Studio ");

            var enforcement = CreateNameRuleEnforcement(context);
            var preparation = await enforcement.PreflightAsync();
            await using (await enforcement.StageAsync(preparation))
                await context.Database.MigrateAsync();

            context.ChangeTracker.Clear();
            var firstTag = await context.Tags.IgnoreQueryFilters().SingleAsync(tag => tag.Id == firstTagId);
            var alias = await context.Set<TagAlias>().IgnoreQueryFilters().SingleAsync(value => value.Id == aliasId);
            var longAlias = await context.Set<TagAlias>().IgnoreQueryFilters().SingleAsync(value => value.Id == longAliasId);
            var performer = await context.Performers.IgnoreQueryFilters().SingleAsync(value => value.Id == performerId);
            var blankDisambiguationPerformer = await context.Performers.IgnoreQueryFilters()
                .SingleAsync(value => value.Id == blankDisambiguationPerformerId);
            var longDisambiguationPerformer = await context.Performers.IgnoreQueryFilters()
                .SingleAsync(value => value.Id == longDisambiguationPerformerId);
            var studio = await context.Studios.IgnoreQueryFilters().SingleAsync(value => value.Id == studioId);
            Assert.Equal("Alpha", firstTag.Name);
            Assert.Equal("alpha", firstTag.NamespaceKey);
            Assert.Equal("Beta", alias.Alias);
            Assert.Equal("beta", alias.NamespaceKey);
            Assert.Equal(longNameComponent, longAlias.Alias);
            Assert.Equal(TagNameRules.NamespaceKey(longNameComponent), longAlias.NamespaceKey);
            Assert.Equal("Performer", performer.Name);
            Assert.Equal("Role", performer.Disambiguation);
            Assert.Equal(EntityNameRules.PerformerIdentityKey("Performer", "Role"), performer.IdentityKey);
            Assert.Equal("Solo", blankDisambiguationPerformer.Name);
            Assert.Null(blankDisambiguationPerformer.Disambiguation);
            Assert.Equal(EntityNameRules.PerformerIdentityKey("Solo", null), blankDisambiguationPerformer.IdentityKey);
            Assert.Equal(longNameComponent, longDisambiguationPerformer.Disambiguation);
            Assert.Equal(
                EntityNameRules.PerformerIdentityKey("Long identity performer", longNameComponent),
                longDisambiguationPerformer.IdentityKey);
            Assert.Equal("Studio", studio.Name);
            Assert.Equal(EntityNameRules.StudioIdentityKey("Studio"), studio.NameKey);
            Assert.Equal(4, await CountTagNameClaimsAsync(environment.Port, databaseName));

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertAliasAsync(environment.Port, databaseName, secondTagId, "Conflicting alias", firstTag.NamespaceKey));
            Assert.Equal(PostgresErrorCodes.ExclusionViolation, exception.SqlState);
            Assert.Equal("UQ_tag_name_claims_namespace", exception.ConstraintName);
            Assert.Equal(4, await CountTagNameClaimsAsync(environment.Port, databaseName));

            var performerException = await Assert.ThrowsAsync<PostgresException>(() => InsertPerformerAsync(
                environment.Port,
                databaseName,
                "PERFORMER",
                "role",
                EntityNameRules.PerformerIdentityKey("PERFORMER", "role")));
            Assert.Equal(PostgresErrorCodes.ExclusionViolation, performerException.SqlState);
            Assert.Equal("UQ_performers_identity", performerException.ConstraintName);

            var studioException = await Assert.ThrowsAsync<PostgresException>(() => InsertStudioAsync(
                environment.Port,
                databaseName,
                "STUDIO",
                EntityNameRules.StudioIdentityKey("STUDIO")));
            Assert.Equal(PostgresErrorCodes.ExclusionViolation, studioException.SqlState);
            Assert.Equal("UQ_studios_name", studioException.ConstraintName);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());

            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE tags CASCADE");
            Assert.Equal(0, await CountTagNameClaimsAsync(environment.Port, databaseName));
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task NameRuleEnforcementPreflight_BlocksAPartiallyCleanedCheckpoint()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"tag_namespace_partial_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using var context = CreateContext(environment.Port, databaseName);
            await context.GetService<IMigrator>().MigrateAsync(NameRuleCompatibilityCheckpointMigrationId);
            await InsertTagAsync(environment.Port, databaseName, " Partial fixture ");
            await InsertTagAsync(environment.Port, databaseName, "partial fixture");

            var enforcement = CreateNameRuleEnforcement(context);
            var exception = await Assert.ThrowsAsync<NameRuleUpgradeBlockedException>(
                () => enforcement.PreflightAsync());

            Assert.Equal(1, exception.UnresolvedGroupCount);
            Assert.Equal(2, exception.UnresolvedClaimCount);
            Assert.Equal(1, exception.TagGroupCount);
            Assert.Equal(0, exception.PerformerGroupCount);
            Assert.Equal(0, exception.StudioGroupCount);
            Assert.Contains("Cove 1.2.x", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Settings → Operations → Name Conflicts", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Partial fixture", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "tags", "NamespaceKey"));
            Assert.Contains(NameRuleEnforcementMigrationId, await context.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task NameRuleEnforcementMigration_RejectsANonemptyCheckpointWithoutStaging()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"name_rules_unstaged_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using var context = CreateContext(environment.Port, databaseName);
            await context.GetService<IMigrator>().MigrateAsync(NameRuleCompatibilityCheckpointMigrationId);
            await InsertStudioAsync(environment.Port, databaseName, "Ready but unstaged studio");

            var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync());

            Assert.True(NameRuleEnforcementService.IsGuardFailure(exception));
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "tags", "NamespaceKey"));
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "performers", "IdentityKey"));
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "studios", "NameKey"));
            Assert.Contains(NameRuleEnforcementMigrationId, await context.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task TagRepository_RetriesAConcurrentSharedNamespaceInsertAfterEnforcement()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"tag_namespace_repository_race_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using (var setup = CreateContext(environment.Port, databaseName))
                await setup.Database.MigrateAsync();

            var barrier = new AsyncTwoPartyBarrier();
            await using var first = CreateContext(
                environment.Port,
                databaseName,
                saveChangesInterceptor: new FirstSaveBarrierInterceptor(barrier));
            await using var second = CreateContext(
                environment.Port,
                databaseName,
                saveChangesInterceptor: new FirstSaveBarrierInterceptor(barrier));

            var results = await Task.WhenAll(
                new TagRepository(first).FindOrCreateByNamesAsync(["Concurrent namespace fixture"]),
                new TagRepository(second).FindOrCreateByNamesAsync(["concurrent namespace fixture"]));

            var firstResult = Assert.Single(results[0]).Value;
            var secondResult = Assert.Single(results[1]).Value;
            Assert.Equal(firstResult.Id, secondResult.Id);

            await using var verify = CreateContext(environment.Port, databaseName);
            var persisted = await verify.Tags
                .IgnoreQueryFilters()
                .Where(tag => tag.NamespaceKey == "concurrent namespace fixture")
                .ToListAsync();
            Assert.Equal(firstResult.Id, Assert.Single(persisted).Id);
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task TagPerformerAndStudioWrites_TranslateConcurrentConstraintConflictsAfterEnforcement()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"name_rule_write_race_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using (var setup = CreateContext(environment.Port, databaseName))
                await setup.Database.MigrateAsync();

            var tagBarrier = new AsyncTwoPartyBarrier();
            await using (var first = CreateContext(
                environment.Port,
                databaseName,
                saveChangesInterceptor: new FirstSaveBarrierInterceptor(tagBarrier)))
            await using (var second = CreateContext(
                environment.Port,
                databaseName,
                saveChangesInterceptor: new FirstSaveBarrierInterceptor(tagBarrier)))
            {
                var errors = await Task.WhenAll(
                    Record.ExceptionAsync(() => new TagRepository(first)
                        .AddAsync(new Tag { Name = "Concurrent tag" })),
                    Record.ExceptionAsync(() => new TagRepository(second)
                        .AddAsync(new Tag { Name = " concurrent TAG " })));
                Assert.Single(errors, error => error == null);
                var conflict = Assert.IsType<TagNameConflictException>(Assert.Single(errors, error => error != null));
                Assert.Equal(
                    "A tag with that name or alias already exists. Tag names and tag aliases must be unique.",
                    conflict.Message);
            }

            var performerBarrier = new AsyncTwoPartyBarrier();
            await using (var first = CreateContext(
                environment.Port,
                databaseName,
                saveChangesInterceptor: new FirstSaveBarrierInterceptor(performerBarrier)))
            await using (var second = CreateContext(
                environment.Port,
                databaseName,
                saveChangesInterceptor: new FirstSaveBarrierInterceptor(performerBarrier)))
            {
                first.Performers.Add(new Performer { Name = "Concurrent performer", Disambiguation = "Role" });
                second.Performers.Add(new Performer { Name = " concurrent PERFORMER ", Disambiguation = " role " });
                var errors = await Task.WhenAll(
                    Record.ExceptionAsync(() => first.SaveChangesAsync()),
                    Record.ExceptionAsync(() => second.SaveChangesAsync()));
                Assert.Single(errors, error => error == null);
                var conflict = Assert.IsType<EntityNameConflictException>(Assert.Single(errors, error => error != null));
                Assert.Equal(NameConflictEntityTypes.Performer, conflict.EntityType);
                Assert.Equal(
                    "A performer with that name and disambiguation already exists. Performer name and disambiguation combinations must be unique.",
                    conflict.Message);
            }

            var studioBarrier = new AsyncTwoPartyBarrier();
            await using (var first = CreateContext(
                environment.Port,
                databaseName,
                saveChangesInterceptor: new FirstSaveBarrierInterceptor(studioBarrier)))
            await using (var second = CreateContext(
                environment.Port,
                databaseName,
                saveChangesInterceptor: new FirstSaveBarrierInterceptor(studioBarrier)))
            {
                first.Studios.Add(new Studio { Name = "Concurrent studio" });
                second.Studios.Add(new Studio { Name = " concurrent STUDIO " });
                var errors = await Task.WhenAll(
                    Record.ExceptionAsync(() => first.SaveChangesAsync()),
                    Record.ExceptionAsync(() => second.SaveChangesAsync()));
                Assert.Single(errors, error => error == null);
                var conflict = Assert.IsType<EntityNameConflictException>(Assert.Single(errors, error => error != null));
                Assert.Equal(NameConflictEntityTypes.Studio, conflict.EntityType);
                Assert.Equal(
                    "A studio with that name already exists. Studio names must be unique.",
                    conflict.Message);
            }

            await using var verify = CreateContext(environment.Port, databaseName);
            Assert.Equal(1, await verify.Tags.CountAsync());
            Assert.Equal(1, await verify.Performers.CountAsync());
            Assert.Equal(1, await verify.Studios.CountAsync());
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task PerformerAndStudioMerges_ContinueToTransferRelationshipsAfterEnforcement()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"name_rules_merge_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            int targetPerformerId;
            int sourcePerformerId;
            int targetStudioId;
            int sourceStudioId;
            int videoId;
            await using (var setup = CreateContext(environment.Port, databaseName))
            {
                await setup.Database.MigrateAsync();
                var targetPerformer = new Performer { Name = "Target performer", Disambiguation = "Role" };
                var sourcePerformer = new Performer { Name = "Source performer", Disambiguation = "Role" };
                var targetStudio = new Studio { Name = "Target studio" };
                var sourceStudio = new Studio { Name = "Source studio" };
                var video = new Video { Title = "Relationship transfer fixture", Studio = sourceStudio };
                setup.AddRange(targetPerformer, sourcePerformer, targetStudio, sourceStudio, video);
                await setup.SaveChangesAsync();
                setup.Set<VideoPerformer>().Add(new VideoPerformer
                {
                    VideoId = video.Id,
                    PerformerId = sourcePerformer.Id,
                });
                await setup.SaveChangesAsync();
                targetPerformerId = targetPerformer.Id;
                sourcePerformerId = sourcePerformer.Id;
                targetStudioId = targetStudio.Id;
                sourceStudioId = sourceStudio.Id;
                videoId = video.Id;
            }

            await using (var performerContext = CreateContext(environment.Port, databaseName))
                await new PerformerMergeService(performerContext).MergeAsync(targetPerformerId, [sourcePerformerId]);
            await using (var studioContext = CreateContext(environment.Port, databaseName))
                await new StudioMergeService(studioContext).MergeAsync(targetStudioId, [sourceStudioId]);

            await using var verify = CreateContext(environment.Port, databaseName);
            Assert.False(await verify.Performers.IgnoreQueryFilters().AnyAsync(entity => entity.Id == sourcePerformerId));
            Assert.False(await verify.Studios.IgnoreQueryFilters().AnyAsync(entity => entity.Id == sourceStudioId));
            Assert.Equal(
                targetPerformerId,
                (await verify.Set<VideoPerformer>().SingleAsync(link => link.VideoId == videoId)).PerformerId);
            Assert.Equal(targetStudioId, (await verify.Videos.SingleAsync(video => video.Id == videoId)).StudioId);
            Assert.Equal(
                EntityNameRules.PerformerIdentityKey("Target performer", "Role"),
                (await verify.Performers.SingleAsync(entity => entity.Id == targetPerformerId)).IdentityKey);
            Assert.Equal(
                EntityNameRules.StudioIdentityKey("Target studio"),
                (await verify.Studios.SingleAsync(entity => entity.Id == targetStudioId)).NameKey);
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task NameRuleEnforcementPreflight_BlocksACheckpointThatSkippedCleanup()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"tag_namespace_skipped_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using var context = CreateContext(environment.Port, databaseName);
            await context.GetService<IMigrator>().MigrateAsync(NameRuleCompatibilityCheckpointMigrationId);
            var firstId = await InsertTagAsync(environment.Port, databaseName, " Case fixture ");
            await InsertTagAsync(environment.Port, databaseName, "case fixture");
            var aliasOwnerId = await InsertTagAsync(environment.Port, databaseName, "Alias owner fixture");
            var otherAliasOwnerId = await InsertTagAsync(environment.Port, databaseName, "Other alias owner fixture");
            await InsertAliasAsync(environment.Port, databaseName, aliasOwnerId, "Shared fixture");
            await InsertAliasAsync(environment.Port, databaseName, otherAliasOwnerId, "shared fixture");
            await InsertAliasAsync(environment.Port, databaseName, aliasOwnerId, "Alias owner fixture");
            await InsertAliasAsync(environment.Port, databaseName, firstId, " \t ");
            await InsertTagAsync(environment.Port, databaseName, "   ");
            await InsertTagAsync(environment.Port, databaseName, TagNameRules.EmptyCanonicalName);
            await InsertPerformerAsync(environment.Port, databaseName, " Shared performer ", " Role ");
            await InsertPerformerAsync(environment.Port, databaseName, "shared PERFORMER", "role");
            await InsertPerformerAsync(environment.Port, databaseName, " No role ", null);
            await InsertPerformerAsync(environment.Port, databaseName, "no role", " \t ");
            await InsertStudioAsync(environment.Port, databaseName, " Shared studio ");
            await InsertStudioAsync(environment.Port, databaseName, "shared STUDIO");

            var enforcement = CreateNameRuleEnforcement(context);
            var exception = await Assert.ThrowsAsync<NameRuleUpgradeBlockedException>(
                () => enforcement.PreflightAsync());

            Assert.Equal(5, exception.TagGroupCount);
            Assert.Equal(2, exception.PerformerGroupCount);
            Assert.Equal(1, exception.StudioGroupCount);
            Assert.Equal(8, exception.UnresolvedGroupCount);
            Assert.Equal(15, exception.UnresolvedClaimCount);
            Assert.DoesNotContain("fixture", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "tag_aliases", "NamespaceKey"));
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "performers", "IdentityKey"));
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "studios", "NameKey"));
            Assert.Contains(NameRuleEnforcementMigrationId, await context.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("performer")]
    [InlineData("studio")]
    public async Task NameRuleEnforcementMigration_AbortsWhenIdentitiesChangeAfterPreflight(string changedEntityType)
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"tag_namespace_concurrent_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using var context = CreateContext(environment.Port, databaseName);
            await context.GetService<IMigrator>().MigrateAsync(NameRuleCompatibilityCheckpointMigrationId);
            var tagId = await InsertTagAsync(environment.Port, databaseName, " Stable fixture ");
            var performerId = await InsertPerformerAsync(environment.Port, databaseName, "Stable performer", "Stable role");
            var studioId = await InsertStudioAsync(environment.Port, databaseName, "Stable studio");
            var enforcement = CreateNameRuleEnforcement(context);
            var preparation = await enforcement.PreflightAsync();

            await using (await enforcement.StageAsync(preparation))
            {
                if (changedEntityType == "tag")
                    await UpdateTagNameAsync(environment.Port, databaseName, tagId, "Changed tag after preflight");
                else if (changedEntityType == "performer")
                    await UpdatePerformerDisambiguationAsync(environment.Port, databaseName, performerId, "Changed role after preflight");
                else
                    await UpdateStudioNameAsync(environment.Port, databaseName, studioId, "Changed studio after preflight");
                var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync());
                Assert.True(NameRuleEnforcementService.IsGuardFailure(exception));
            }

            if (changedEntityType == "tag")
                Assert.Equal("Changed tag after preflight", await ReadTagNameAsync(environment.Port, databaseName, tagId));
            else if (changedEntityType == "performer")
                Assert.Equal("Changed role after preflight", await ReadPerformerDisambiguationAsync(environment.Port, databaseName, performerId));
            else
                Assert.Equal("Changed studio after preflight", await ReadStudioNameAsync(environment.Port, databaseName, studioId));
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "tags", "NamespaceKey"));
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "performers", "IdentityKey"));
            Assert.False(await ColumnExistsAsync(environment.Port, databaseName, "studios", "NameKey"));
            Assert.Contains(NameRuleEnforcementMigrationId, await context.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task V1BaselineMigration_CreatesFreshDatabaseSchema()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"v1_baseline_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using var context = CreateContext(environment.Port, databaseName);
            var expectedMigrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.ToArray();
            AssertNoPendingModelChanges(context);

            await context.Database.MigrateAsync();

            var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
            var pending = (await context.Database.GetPendingMigrationsAsync()).ToArray();

            Assert.Equal(expectedMigrations, applied);
            Assert.Empty(pending);

            await AssertAuthFunctionsCreatedAsync(environment.Port, databaseName);
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task NameRuleEnforcementPreflight_AllowsAFreshEmptyDatabase()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"name_rule_fresh_preflight_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using var context = CreateContext(environment.Port, databaseName);
            var preparation = await CreateNameRuleEnforcement(context).PreflightAsync();
            await using (await CreateNameRuleEnforcement(context).StageAsync(preparation))
                await context.Database.MigrateAsync();

            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.True(await ColumnExistsAsync(environment.Port, databaseName, "tags", "NamespaceKey"));
            Assert.True(await ColumnExistsAsync(environment.Port, databaseName, "performers", "IdentityKey"));
            Assert.True(await ColumnExistsAsync(environment.Port, databaseName, "studios", "NameKey"));
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task TagExternalReferenceInspector_InventoriesAndRepairsForeignKeysOutsideTheCoreMergeContract()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"tag_external_refs_{Guid.NewGuid():N}";
        var restrictedRoleName = $"tag_external_refs_role_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);
        await CreateRoleAsync(environment.AdminConnectionString, restrictedRoleName);

        try
        {
            await using var context = CreateContext(environment.Port, databaseName, enableRetry: false);
            await context.Database.MigrateAsync();
            var target = new Cove.Core.Entities.Tag { Name = "Repair target fixture" };
            var referenced = new Cove.Core.Entities.Tag { Name = "Referenced fixture" };
            var unreferenced = new Cove.Core.Entities.Tag { Name = "Unreferenced fixture" };
            context.Tags.AddRange(target, referenced, unreferenced);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE extension_tag_reference_fixture (
                    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    tag_id integer NOT NULL REFERENCES tags("Id") ON DELETE RESTRICT
                );
                """);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO extension_tag_reference_fixture (tag_id) VALUES ({referenced.Id})");

            var references = await new PostgresTagExternalReferenceInspector(context)
                .InspectAsync([referenced.Id, unreferenced.Id]);

            var reference = Assert.Single(references);
            Assert.Equal(referenced.Id, reference.TagId);
            Assert.Equal("public", reference.SchemaName);
            Assert.Equal("extension_tag_reference_fixture", reference.TableName);
            Assert.Equal("tag_id", reference.ColumnName);
            Assert.Equal("restrict", reference.DeleteBehavior);
            Assert.Equal(1, reference.RowCount);

            var inspector = new PostgresTagExternalReferenceInspector(context);
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await inspector.ApplyResolutionsAsync(
                    target.Id,
                    [new TagExternalReferenceResolutionDto(
                        referenced.Id,
                        reference.ReferenceKey,
                        TagExternalReferenceActions.UpdateToSurvivor)]);
                await transaction.CommitAsync();
            }

            var updatedTagId = await context.Database
                .SqlQueryRaw<int>("SELECT tag_id AS \"Value\" FROM extension_tag_reference_fixture")
                .SingleAsync();
            Assert.Equal(target.Id, updatedTagId);

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO extension_tag_reference_fixture (tag_id) VALUES ({unreferenced.Id}), ({unreferenced.Id})");
            var deleteReference = Assert.Single(await inspector.InspectAsync([unreferenced.Id]));
            Assert.Equal(2, deleteReference.RowCount);
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await inspector.ApplyResolutionsAsync(
                    target.Id,
                    [new TagExternalReferenceResolutionDto(
                        unreferenced.Id,
                        deleteReference.ReferenceKey,
                        TagExternalReferenceActions.DeleteRows)]);
                await transaction.CommitAsync();
            }

            Assert.Empty(await inspector.InspectAsync([unreferenced.Id]));

            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE extension_partitioned_tag_reference_fixture (
                    id integer NOT NULL,
                    tag_id integer NOT NULL REFERENCES tags("Id") ON DELETE RESTRICT
                ) PARTITION BY RANGE (id);
                CREATE TABLE extension_partitioned_tag_reference_fixture_p0
                    PARTITION OF extension_partitioned_tag_reference_fixture
                    FOR VALUES FROM (0) TO (100);
                """);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO extension_partitioned_tag_reference_fixture (id, tag_id) VALUES (1, {unreferenced.Id})");
            var partitionedReferences = await inspector.InspectAsync([unreferenced.Id]);
            var partitionedReference = Assert.Single(partitionedReferences);
            Assert.Equal("extension_partitioned_tag_reference_fixture", partitionedReference.TableName);
            Assert.DoesNotContain(
                partitionedReferences,
                candidate => candidate.TableName == "extension_partitioned_tag_reference_fixture_p0");
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await inspector.ApplyResolutionsAsync(
                    target.Id,
                    [new TagExternalReferenceResolutionDto(
                        unreferenced.Id,
                        partitionedReference.ReferenceKey,
                        TagExternalReferenceActions.UpdateToSurvivor)]);
                await transaction.CommitAsync();
            }
            var partitionedTagId = await context.Database
                .SqlQueryRaw<int>("SELECT tag_id AS \"Value\" FROM extension_partitioned_tag_reference_fixture")
                .SingleAsync();
            Assert.Equal(target.Id, partitionedTagId);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE extension_dual_tag_reference_fixture (
                    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    source_tag_id integer NOT NULL REFERENCES tags("Id") ON DELETE RESTRICT,
                    derived_tag_id integer NOT NULL REFERENCES tags("Id") ON DELETE RESTRICT
                );
                """);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO extension_dual_tag_reference_fixture (source_tag_id, derived_tag_id) VALUES ({referenced.Id}, {referenced.Id})");
            var overlappingReferences = await inspector.InspectAsync([referenced.Id]);
            Assert.Equal(2, overlappingReferences.Count);
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await inspector.ApplyResolutionsAsync(
                    target.Id,
                    overlappingReferences.Select(reference => new TagExternalReferenceResolutionDto(
                        referenced.Id,
                        reference.ReferenceKey,
                        TagExternalReferenceActions.DeleteRows)).ToArray());
                await transaction.CommitAsync();
            }
            var overlappingRows = await context.Database
                .SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM extension_dual_tag_reference_fixture")
                .SingleAsync();
            Assert.Equal(0, overlappingRows);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE extension_unique_tag_reference_fixture (
                    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    tag_id integer NOT NULL UNIQUE REFERENCES tags("Id") ON DELETE RESTRICT
                );
                """);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO extension_unique_tag_reference_fixture (tag_id) VALUES ({target.Id}), ({referenced.Id})");
            var conflictingReference = Assert.Single(await inspector.InspectAsync([referenced.Id]));
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                var exception = await Assert.ThrowsAsync<TagExternalReferenceRepairException>(() =>
                    inspector.ApplyResolutionsAsync(
                        target.Id,
                        [new TagExternalReferenceResolutionDto(
                            referenced.Id,
                            conflictingReference.ReferenceKey,
                            TagExternalReferenceActions.UpdateToSurvivor)]));
                Assert.Contains("database rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
                await transaction.RollbackAsync();
            }

            var preservedRows = await context.Database
                .SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM extension_unique_tag_reference_fixture")
                .SingleAsync();
            Assert.Equal(2, preservedRows);

            var rlsTarget = new Tag { Name = "RLS merge fixture" };
            var rlsSource = new Tag { Name = "RLS source fixture" };
            context.Tags.AddRange(rlsTarget, rlsSource);
            await context.SaveChangesAsync();
            // Simulate a conflicting 1.2 checkpoint row after the enforcement schema is present.
            // Updating only the display value deliberately preserves its old unique claim key.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE tags SET \"Name\" = {' ' + rlsTarget.Name + ' '} WHERE \"Id\" = {rlsSource.Id}");
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE extension_rls_tag_reference_fixture (
                    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    tag_id integer NOT NULL REFERENCES tags("Id") ON DELETE CASCADE
                );
                """);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO extension_rls_tag_reference_fixture (tag_id) VALUES ({rlsSource.Id})");
            await context.Database.ExecuteSqlRawAsync("""
                ALTER TABLE extension_rls_tag_reference_fixture ENABLE ROW LEVEL SECURITY;
                ALTER TABLE extension_rls_tag_reference_fixture FORCE ROW LEVEL SECURITY;
                CREATE POLICY extension_rls_deny_all ON extension_rls_tag_reference_fixture
                    FOR ALL USING (false) WITH CHECK (false);
                """);
            await GrantExtensionRepairPrivilegesAsync(context, restrictedRoleName);

            context.ChangeTracker.Clear();
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await SetLocalRoleAsync(context, restrictedRoleName);
                var restrictedInspector = new PostgresTagExternalReferenceInspector(context);
                var restrictedReferences = (await restrictedInspector.InspectAsync([rlsTarget.Id, rlsSource.Id]))
                    .Where(reference => reference.TableName == "extension_rls_tag_reference_fixture")
                    .ToArray();
                Assert.Equal(2, restrictedReferences.Length);
                Assert.All(restrictedReferences, reference =>
                {
                    Assert.Null(reference.RowCount);
                    Assert.Equal(TagExternalReferenceAccessLimitations.RowLevelSecurity, reference.AccessLimitation);
                });
                var rowSecurity = await ReadRowSecuritySettingAsync(context);
                Assert.Equal("on", rowSecurity);

                await transaction.RollbackAsync();
            }

            context.ChangeTracker.Clear();
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await SetLocalRoleAsync(context, restrictedRoleName);
                var mergeException = await Assert.ThrowsAsync<TagMergeBlockedException>(
                    () => new TagMergeService(
                            context,
                            externalReferenceInspector: new PostgresTagExternalReferenceInspector(context))
                        .MergeAsync(rlsTarget.Id, [rlsSource.Id]));
                Assert.True(mergeException.HasUninspectableReferences);
                await transaction.RollbackAsync();
            }

            context.ChangeTracker.Clear();
            Assert.True(await context.Tags.AnyAsync(tag => tag.Id == rlsSource.Id));
            var hiddenRows = await context.Database
                .SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM extension_rls_tag_reference_fixture")
                .SingleAsync();
            Assert.Equal(1, hiddenRows);
        }
        finally
        {
            try
            {
                await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
            }
            finally
            {
                await DropRoleAsync(environment.AdminConnectionString, restrictedRoleName);
            }
        }
    }

    [Fact]
    public async Task TagMergeService_TransfersRowsHiddenFromTheInitiatingPrincipal()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"tag_merge_filter_bypass_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            int targetId;
            int sourceId;
            int initiatingUserId;
            await using (var setup = CreateContext(environment.Port, databaseName))
            {
                await setup.Database.MigrateAsync();
                var target = new Tag { Name = "Target fixture" };
                var source = new Tag { Name = "Source fixture" };
                var initiatingUser = new User { Username = "initiating-fixture", PasswordHash = "fixture" };
                var otherUser = new User { Username = "other-fixture", PasswordHash = "fixture" };
                setup.AddRange(target, source, initiatingUser, otherUser);
                await setup.SaveChangesAsync();
                setup.Ratings.AddRange(
                    new Rating { UserId = initiatingUser.Id, HostType = RatingHostType.Tag, HostId = source.Id, Value = 60 },
                    new Rating { UserId = otherUser.Id, HostType = RatingHostType.Tag, HostId = source.Id, Value = 80 });
                await setup.SaveChangesAsync();
                targetId = target.Id;
                sourceId = source.Id;
                initiatingUserId = initiatingUser.Id;
            }

            var principalAccessor = new CurrentPrincipalAccessor();
            principalAccessor.Set(new CovePrincipal
            {
                UserId = initiatingUserId,
                Username = "initiating-fixture",
                Kind = PrincipalKind.User,
                Roles = new HashSet<string>(StringComparer.Ordinal),
                Permissions = new HashSet<string>(
                    [Permissions.TagsRead, Permissions.TagsWrite, Permissions.TagsDelete],
                    StringComparer.Ordinal),
            });
            try
            {
                await using var filtered = CreateContext(environment.Port, databaseName, principalAccessor);
                await new TagMergeService(
                        filtered,
                        externalReferenceInspector: new PostgresTagExternalReferenceInspector(filtered))
                    .MergeAsync(targetId, [sourceId]);
            }
            finally
            {
                principalAccessor.Set(null);
            }

            await using var verify = CreateContext(environment.Port, databaseName);
            var ratings = await verify.Ratings
                .IgnoreQueryFilters()
                .OrderBy(rating => rating.UserId)
                .ToListAsync();
            Assert.Equal(2, ratings.Count);
            Assert.All(ratings, rating => Assert.Equal(targetId, rating.HostId));
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ExternalReferenceInspectors_DoNotExcludeExtensionForeignKeysAddedToCoreTables()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"extension_core_table_refs_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            int studioId;
            int tagId;
            int videoId;
            await using (var setup = CreateContext(environment.Port, databaseName))
            {
                await setup.Database.MigrateAsync();
                var studio = new Studio { Name = "Extension studio fixture" };
                var tag = new Tag { Name = "Extension tag fixture" };
                var video = new Video { Title = "Extension reference fixture" };
                setup.AddRange(studio, tag, video);
                await setup.SaveChangesAsync();
                studioId = studio.Id;
                tagId = tag.Id;
                videoId = video.Id;
                await setup.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE videos ADD COLUMN extension_studio_id integer NULL REFERENCES studios("Id") ON DELETE RESTRICT;
                    ALTER TABLE videos ADD COLUMN extension_tag_id integer NULL REFERENCES tags("Id") ON DELETE RESTRICT;
                    """);
                await setup.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE videos SET extension_studio_id = {studioId}, extension_tag_id = {tagId} WHERE \"Id\" = {videoId}");
            }

            await using var context = CreateExtensionModelContext(environment.Port, databaseName);
            var studioReference = Assert.Single(await new PostgresEntityExternalReferenceInspector(context)
                .InspectAsync(NameConflictEntityTypes.Studio, [studioId]));
            Assert.Equal("videos", studioReference.TableName);
            Assert.Equal("extension_studio_id", studioReference.ColumnName);
            Assert.Equal(1, studioReference.RowCount);

            var tagReference = Assert.Single(await new PostgresTagExternalReferenceInspector(context)
                .InspectAsync([tagId]));
            Assert.Equal("videos", tagReference.TableName);
            Assert.Equal("extension_tag_id", tagReference.ColumnName);
            Assert.Equal(1, tagReference.RowCount);
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    private static async Task<int> InsertTagAsync(int port, string databaseName, string name)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tags ("Name", "Favorite", "Organized", "CreatedAt", "UpdatedAt")
            VALUES ($1, false, false, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            RETURNING "Id"
            """;
        command.Parameters.AddWithValue(name);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static string BuildLongNameComponent()
        => string.Concat(Enumerable.Range(0, 128)
            .Select(value => Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(value)))));

    private static async Task<int> InsertAliasAsync(
        int port,
        string databaseName,
        int tagId,
        string alias,
        string? namespaceKey = null)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = namespaceKey == null
            ? """
                INSERT INTO tag_aliases ("TagId", "Alias")
                VALUES ($1, $2)
                RETURNING "Id"
                """
            : """
                INSERT INTO tag_aliases ("TagId", "Alias", "NamespaceKey")
                VALUES ($1, $2, $3)
                RETURNING "Id"
                """;
        command.Parameters.AddWithValue(tagId);
        command.Parameters.AddWithValue(alias);
        if (namespaceKey != null)
            command.Parameters.AddWithValue(namespaceKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> InsertPerformerAsync(
        int port,
        string databaseName,
        string name,
        string? disambiguation,
        string? identityKey = null)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = identityKey == null
            ? """
                INSERT INTO performers ("Name", "Disambiguation", "Favorite", "CreatedAt", "UpdatedAt")
                VALUES ($1, $2, false, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                RETURNING "Id"
                """
            : """
                INSERT INTO performers ("Name", "Disambiguation", "IdentityKey", "Favorite", "CreatedAt", "UpdatedAt")
                VALUES ($1, $2, $3, false, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                RETURNING "Id"
                """;
        command.Parameters.AddWithValue(name);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = disambiguation ?? (object)DBNull.Value,
        });
        if (identityKey != null)
            command.Parameters.AddWithValue(identityKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> InsertStudioAsync(
        int port,
        string databaseName,
        string name,
        string? nameKey = null)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = nameKey == null
            ? """
                INSERT INTO studios ("Name", "Favorite", "Organized", "CreatedAt", "UpdatedAt")
                VALUES ($1, false, false, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                RETURNING "Id"
                """
            : """
                INSERT INTO studios ("Name", "NameKey", "Favorite", "Organized", "CreatedAt", "UpdatedAt")
                VALUES ($1, $2, false, false, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                RETURNING "Id"
                """;
        command.Parameters.AddWithValue(name);
        if (nameKey != null)
            command.Parameters.AddWithValue(nameKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task UpdateTagNameAsync(int port, string databaseName, int tagId, string name)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tags SET \"Name\" = $1 WHERE \"Id\" = $2";
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(tagId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdatePerformerDisambiguationAsync(
        int port,
        string databaseName,
        int performerId,
        string? disambiguation)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE performers SET \"Disambiguation\" = $1 WHERE \"Id\" = $2";
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = disambiguation ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue(performerId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateStudioNameAsync(int port, string databaseName, int studioId, string name)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE studios SET \"Name\" = $1 WHERE \"Id\" = $2";
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(studioId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadTagNameAsync(int port, string databaseName, int tagId)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Name\" FROM tags WHERE \"Id\" = $1";
        command.Parameters.AddWithValue(tagId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadPerformerDisambiguationAsync(
        int port,
        string databaseName,
        int performerId)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Disambiguation\" FROM performers WHERE \"Id\" = $1";
        command.Parameters.AddWithValue(performerId);
        var value = await command.ExecuteScalarAsync();
        return value == DBNull.Value ? null : Assert.IsType<string>(value);
    }

    private static async Task<string> ReadStudioNameAsync(int port, string databaseName, int studioId)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Name\" FROM studios WHERE \"Id\" = $1";
        command.Parameters.AddWithValue(studioId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountTagNameClaimsAsync(int port, string databaseName)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*)::integer FROM tag_name_claims";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> ColumnExistsAsync(
        int port,
        string databaseName,
        string tableName,
        string columnName)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = $1
                  AND column_name = $2)
            """;
        command.Parameters.AddWithValue(tableName);
        command.Parameters.AddWithValue(columnName);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private static CoveContext CreateContext(
        int port,
        string databaseName,
        ICurrentPrincipalAccessor? principalAccessor = null,
        SaveChangesInterceptor? saveChangesInterceptor = null,
        bool enableRetry = true)
    {
        var optionsBuilder = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(BuildConnectionString(port, databaseName), npgsqlOptions =>
            {
                npgsqlOptions.UseVector();
                if (enableRetry)
                    npgsqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
            })
            .ReplaceService<IModelCacheKeyFactory, CoveModelCacheKeyFactory>();
        if (saveChangesInterceptor != null)
            optionsBuilder.AddInterceptors(saveChangesInterceptor);

        return new CoveContext(optionsBuilder.Options, principalAccessor, includeDataExtensionsInModel: false);
    }

    private static NameRuleEnforcementService CreateNameRuleEnforcement(CoveContext context)
        => new(context);

    private static CoveContext CreateExtensionModelContext(int port, string databaseName)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(BuildConnectionString(port, databaseName), npgsqlOptions => npgsqlOptions.UseVector())
            .ReplaceService<IModelCacheKeyFactory, CoveModelCacheKeyFactory>()
            .Options;
        return new ExtensionModelContext(options);
    }

    private static string BuildConnectionString(int port, string databaseName)
        => $"Host=127.0.0.1;Port={port};Database={databaseName};Username=postgres;Trust Server Certificate=true;Timeout=15;Command Timeout=30";

    private static void AssertNoPendingModelChanges(CoveContext context)
    {
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var differ = context.GetService<IMigrationsModelDiffer>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var snapshotModel = initializer.Initialize(snapshot!.Model, designTime: true);
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;
        var operations = differ.GetDifferences(snapshotModel.GetRelationalModel(), designTimeModel.GetRelationalModel());
        if (operations.Count == 0)
            return;

        var details = string.Join(Environment.NewLine, operations.Select(FormatOperation));
        throw new Xunit.Sdk.XunitException($"Pending model changes detected:{Environment.NewLine}{details}");
    }

    private static string FormatOperation(MigrationOperation operation)
        => operation switch
        {
            AddColumnOperation addColumn => $"AddColumn {addColumn.Table}.{addColumn.Name} ({addColumn.ColumnType ?? addColumn.ClrType.Name})",
            AlterColumnOperation alterColumn => $"AlterColumn {alterColumn.Table}.{alterColumn.Name} ({alterColumn.ColumnType ?? alterColumn.ClrType.Name})",
            CreateTableOperation createTable => $"CreateTable {createTable.Name}",
            CreateIndexOperation createIndex => $"CreateIndex {createIndex.Table}.{createIndex.Name}",
            DropColumnOperation dropColumn => $"DropColumn {dropColumn.Table}.{dropColumn.Name}",
            DropIndexOperation dropIndex => $"DropIndex {dropIndex.Table}.{dropIndex.Name}",
            DropTableOperation dropTable => $"DropTable {dropTable.Name}",
            _ => operation.GetType().Name,
        };

    private static async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateRoleAsync(string adminConnectionString, string roleName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE ROLE {QuoteIdentifier(roleName)} NOLOGIN";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropRoleAsync(string adminConnectionString, string roleName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP ROLE IF EXISTS {QuoteIdentifier(roleName)}";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantExtensionRepairPrivilegesAsync(CoveContext context, string roleName)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
            await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            var role = QuoteIdentifier(roleName);
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA public TO {role};
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role};
                GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {role};
                """;
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            if (openedHere)
                await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task SetLocalRoleAsync(CoveContext context, string roleName)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        command.CommandText = $"SET LOCAL ROLE {QuoteIdentifier(roleName)}";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadRowSecuritySettingAsync(CoveContext context)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        command.CommandText = "SELECT current_setting('row_security')";
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync();

        await using (var terminate = conn.CreateCommand())
        {
            terminate.CommandText = $"""
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{databaseName}' AND pid <> pg_backend_pid()
            """;
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = conn.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task AssertAuthFunctionsCreatedAsync(int port, string databaseName)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT public.cove_authz_can_read(
                true,
                false,
                false,
                ARRAY[]::text[],
                NULL::uuid,
                'video',
                1
            )
            """;
        var result = await cmd.ExecuteScalarAsync();

        Assert.True(result is bool value && value);
    }

    private static async Task<PostgresTestEnvironment> CreateEnvironmentAsync(string managedRoot)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = ReserveLoopbackPort();
            var postgresConfig = new PostgresConfig
            {
                Managed = true,
                DataPath = managedRoot,
                Port = port,
                Database = "postgres",
            };

            var manager = new PostgresManagerService(Options.Create(postgresConfig), NullLogger<PostgresManagerService>.Instance);

            try
            {
                await manager.StartAsync(CancellationToken.None);
                return new PostgresTestEnvironment(manager, port, BuildConnectionString(port, "postgres"));
            }
            catch (Exception ex) when (attempt < 4)
            {
                lastError = ex;
                try
                {
                    await manager.StopAsync(CancellationToken.None);
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException("Failed to start managed Postgres for V1 baseline migration tests.", lastError);
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? ResolveManagedPostgresRoot()
    {
        var repoArtifactRoot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "backup-verify-data");
        if (File.Exists(Path.Combine(repoArtifactRoot, "pgsql", "bin", Exe("pg_ctl"))))
            return repoArtifactRoot;

        var localAppDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cove");
        if (File.Exists(Path.Combine(localAppDataRoot, "pgsql", "bin", Exe("pg_ctl"))))
            return localAppDataRoot;

        return null;
    }

    private static string Exe(string toolName)
        => OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;

    private sealed class AsyncTwoPartyBarrier
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivalCount;

        public async Task SignalAndWaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrivalCount) == 2)
                _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
    }

    private sealed class FirstSaveBarrierInterceptor(AsyncTwoPartyBarrier barrier) : SaveChangesInterceptor
    {
        private int _hasSignaled;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _hasSignaled, 1) == 0)
                await barrier.SignalAndWaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class PostgresTestEnvironment(PostgresManagerService manager, int port, string adminConnectionString) : IAsyncDisposable
    {
        public int Port { get; } = port;
        public string AdminConnectionString { get; } = adminConnectionString;

        public async ValueTask DisposeAsync()
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ExtensionModelContext(DbContextOptions<CoveContext> options)
        : CoveContext(options, principalAccessor: null, includeDataExtensionsInModel: false)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Video>()
                .Property<int?>("ExtensionStudioId")
                .HasColumnName("extension_studio_id");
            modelBuilder.Entity<Video>()
                .HasOne<Studio>()
                .WithMany()
                .HasForeignKey("ExtensionStudioId")
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<Video>()
                .Property<int?>("ExtensionTagId")
                .HasColumnName("extension_tag_id");
            modelBuilder.Entity<Video>()
                .HasOne<Tag>()
                .WithMany()
                .HasForeignKey("ExtensionTagId")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
