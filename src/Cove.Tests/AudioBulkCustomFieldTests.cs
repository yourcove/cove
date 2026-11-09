using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Data;
using Cove.Tests.Postgres;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

/// <summary>
/// Audio bulk updates merge custom field values the same way video bulk updates do, so a caller can
/// record one key on one audio without reading and resending the rest of that audio's fields.
/// </summary>
[RequiresPostgres]
public class AudioBulkCustomFieldTests
{
    [Fact]
    public async Task BulkUpdate_AddsAndRemovesOneKeyWithoutTouchingOtherKeysOrAudios()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        var customFields = new CustomFieldService(context);
        await customFields.CreateDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = "confirmed_absent_occurrence_tags",
            Label = "Confirmed absent occurrence tags",
            Type = CustomFieldTypes.Text,
            EntityTypes = [CustomFieldEntityTypes.Audio],
            Filterable = true,
            IsMultiValue = true,
        }, TestContext.Current.CancellationToken);
        await customFields.CreateDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = "unrelated_note",
            Label = "Unrelated note",
            Type = CustomFieldTypes.Text,
            EntityTypes = [CustomFieldEntityTypes.Audio],
        }, TestContext.Current.CancellationToken);

        var target = new Audio { Title = "target" };
        var control = new Audio { Title = "control" };
        context.Audios.AddRange(target, control);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await customFields.SaveValuesAsync(CustomFieldEntityTypes.Audio, target.Id,
            new Dictionary<string, object> { ["unrelated_note"] = "keep me" },
            TestContext.Current.CancellationToken);

        var controller = new AudiosController(context, customFields, null!, null!, null!, null);

        Assert.IsType<OkObjectResult>(await controller.BulkUpdate(new BulkAudioUpdateDto
        {
            Ids = [target.Id],
            CustomFields = new() { ["confirmed_absent_occurrence_tags"] = new[] { "11:21", "11:22" } },
            CustomFieldMode = BulkUpdateMode.Add,
        }, TestContext.Current.CancellationToken));

        Assert.Equal(["11:21", "11:22"], await AbsenceValuesAsync(customFields, target.Id));
        Assert.Equal("keep me", (await customFields.GetValuesAsync(CustomFieldEntityTypes.Audio, target.Id, TestContext.Current.CancellationToken))["unrelated_note"]);
        Assert.Empty(await AbsenceValuesAsync(customFields, control.Id));

        Assert.IsType<OkObjectResult>(await controller.BulkUpdate(new BulkAudioUpdateDto
        {
            Ids = [target.Id],
            CustomFields = new() { ["confirmed_absent_occurrence_tags"] = new[] { "11:21" } },
            CustomFieldMode = BulkUpdateMode.Remove,
        }, TestContext.Current.CancellationToken));

        Assert.Equal(["11:22"], await AbsenceValuesAsync(customFields, target.Id));

        // Only the audio whose values changed is touched.
        var touched = await context.Audios.AsNoTracking().SingleAsync(item => item.Id == target.Id, TestContext.Current.CancellationToken);
        var untouched = await context.Audios.AsNoTracking().SingleAsync(item => item.Id == control.Id, TestContext.Current.CancellationToken);
        Assert.True(touched.UpdatedAt > untouched.UpdatedAt);
    }

    [Fact]
    public async Task BulkUpdate_RejectsAnUnknownCustomFieldKeyWithoutApplyingTheRestOfTheRequest()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        var customFields = new CustomFieldService(context);
        var audio = new Audio { Title = "unchanged", Organized = false };
        context.Audios.Add(audio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new AudiosController(context, customFields, null!, null!, null!, null);
        var response = await controller.BulkUpdate(new BulkAudioUpdateDto
        {
            Ids = [audio.Id],
            Organized = true,
            CustomFields = new() { ["never_defined"] = new[] { "11:21" } },
        }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(response);
        Assert.False((await context.Audios.AsNoTracking().SingleAsync(item => item.Id == audio.Id, TestContext.Current.CancellationToken)).Organized);
    }

    private static async Task<string[]> AbsenceValuesAsync(CustomFieldService customFields, int audioId)
    {
        var values = await customFields.GetValuesAsync(CustomFieldEntityTypes.Audio, audioId, TestContext.Current.CancellationToken);
        return values.TryGetValue("confirmed_absent_occurrence_tags", out var value) && value is IEnumerable<object> items
            ? [.. items.Select(item => item?.ToString() ?? string.Empty)]
            : [];
    }
}
