using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Tests;

/// <summary>
/// Tests that verify all filter criteria properties exist on filter classes,
/// all DTO fields exist, and JSON deserialization works for the full filter criteria set.
/// </summary>
public class FilterCriteriaParityTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // ===== SCENE FILTER CRITERIA EXISTENCE =====

    [Fact]
    public void SceneFilter_HasAllCriteria()
    {
        var filter = new SceneFilter();
        // Original criteria
        Assert.Null(filter.RatingCriterion);
        Assert.Null(filter.OCounterCriterion);
        Assert.Null(filter.DurationCriterion);
        Assert.Null(filter.ResolutionCriterion);
        Assert.Null(filter.TagsCriterion);
        Assert.Null(filter.PerformersCriterion);
        Assert.Null(filter.StudiosCriterion);
        Assert.Null(filter.GroupsCriterion);
        Assert.Null(filter.OrganizedCriterion);
        Assert.Null(filter.HasMarkersCriterion);
        Assert.Null(filter.InteractiveCriterion);
        Assert.Null(filter.PathCriterion);
        Assert.Null(filter.VideoCodecCriterion);
        Assert.Null(filter.AudioCodecCriterion);
        Assert.Null(filter.DateCriterion);
        Assert.Null(filter.PerformerFavoriteCriterion);
        Assert.Null(filter.RemoteIdCriterion);
        Assert.Null(filter.TitleCriterion);
        Assert.Null(filter.CodeCriterion);
        Assert.Null(filter.DetailsCriterion);
        Assert.Null(filter.DirectorCriterion);
        Assert.Null(filter.TagCountCriterion);
        Assert.Null(filter.ResumeTimeCriterion);
        Assert.Null(filter.PlayDurationCriterion);
        Assert.Null(filter.GalleriesCriterion);
        Assert.Null(filter.UrlCriterion);
        Assert.Null(filter.CreatedAtCriterion);
        Assert.Null(filter.UpdatedAtCriterion);
        Assert.Null(filter.LastPlayedAtCriterion);

        // Additional filter criteria
        Assert.Null(filter.PerformerTagsCriterion);
        Assert.Null(filter.PerformerAgeCriterion);
        Assert.Null(filter.CaptionsCriterion);
        Assert.Null(filter.InteractiveSpeedCriterion);
    }

    [Fact]
    public void SceneFilter_PerformerTagsCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "performerTagsCriterion": { "value": [1, 2], "modifier": "includes" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<SceneFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.PerformerTagsCriterion);
        Assert.Equal(2, result.ObjectFilter.PerformerTagsCriterion.Value.Count);
        Assert.Equal(CriterionModifier.Includes, result.ObjectFilter.PerformerTagsCriterion.Modifier);
    }

    [Fact]
    public void SceneFilter_PerformerAgeCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "performerAgeCriterion": { "value": 25, "modifier": "greaterThan" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<SceneFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.PerformerAgeCriterion);
        Assert.Equal(25, result.ObjectFilter.PerformerAgeCriterion.Value);
        Assert.Equal(CriterionModifier.GreaterThan, result.ObjectFilter.PerformerAgeCriterion.Modifier);
    }

    [Fact]
    public void SceneFilter_CaptionsCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "captionsCriterion": { "value": "english", "modifier": "includes" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<SceneFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.CaptionsCriterion);
        Assert.Equal("english", result.ObjectFilter.CaptionsCriterion.Value);
    }

    [Fact]
    public void SceneFilter_InteractiveSpeedCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "interactiveSpeedCriterion": { "value": 50, "modifier": "equals" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<SceneFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.InteractiveSpeedCriterion);
        Assert.Equal(50, result.ObjectFilter.InteractiveSpeedCriterion.Value);
    }

    // ===== PERFORMER FILTER CRITERIA EXISTENCE =====

    [Fact]
    public void PerformerFilter_HasAllNewCriteria()
    {
        var filter = new PerformerFilter();
        Assert.Null(filter.NameCriterion);
        Assert.Null(filter.DisambiguationCriterion);
        Assert.Null(filter.DetailsCriterion);
        Assert.Null(filter.EyeColorCriterion);
        Assert.Null(filter.HairColorCriterion);
        Assert.Null(filter.MeasurementsCriterion);
        Assert.Null(filter.FakeTitsCriterion);
        Assert.Null(filter.PenisLengthCriterion);
        Assert.Null(filter.CircumcisedCriterion);
        Assert.Null(filter.CareerStartCriterion);
        Assert.Null(filter.CareerEndCriterion);
        Assert.Null(filter.CareerLengthCriterion);
        Assert.Null(filter.TattooCriterion);
        Assert.Null(filter.PiercingsCriterion);
        Assert.Null(filter.AliasesCriterion);
        Assert.Null(filter.DeathDateCriterion);
        Assert.Null(filter.MarkerCountCriterion);
        Assert.Null(filter.PlayCountCriterion);
        Assert.Null(filter.OCounterCriterion);
        Assert.Null(filter.GroupsCriterion);
        Assert.Null(filter.IgnoreAutoTagCriterion);
        Assert.Null(filter.TagCountCriterion);
        Assert.Null(filter.StudioCountCriterion);
        Assert.Null(filter.RemoteIdValueCriterion);
    }

    [Fact]
    public void PerformerFilter_NameCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "nameCriterion": { "value": "alice", "modifier": "includes" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<PerformerFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.NameCriterion);
        Assert.Equal("alice", result.ObjectFilter.NameCriterion.Value);
        Assert.Equal(CriterionModifier.Includes, result.ObjectFilter.NameCriterion.Modifier);
    }

    [Fact]
    public void PerformerFilter_StudioCountCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "studioCountCriterion": { "value": 2, "modifier": "equals" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<PerformerFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.StudioCountCriterion);
        Assert.Equal(2, result.ObjectFilter.StudioCountCriterion.Value);
        Assert.Equal(CriterionModifier.Equals, result.ObjectFilter.StudioCountCriterion.Modifier);
    }

    [Fact]
    public void PerformerFilter_CareerLengthCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "careerLengthCriterion": { "value": 5, "modifier": "greaterThan" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<PerformerFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.CareerLengthCriterion);
        Assert.Equal(5, result.ObjectFilter.CareerLengthCriterion.Value);
        Assert.Equal(CriterionModifier.GreaterThan, result.ObjectFilter.CareerLengthCriterion.Modifier);
    }

    [Fact]
    public void PerformerFilter_RemoteIdValueCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "remoteIdValueCriterion": { "value": "pmv-1", "modifier": "includes" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<PerformerFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.RemoteIdValueCriterion);
        Assert.Equal("pmv-1", result.ObjectFilter.RemoteIdValueCriterion.Value);
        Assert.Equal(CriterionModifier.Includes, result.ObjectFilter.RemoteIdValueCriterion.Modifier);
    }

    [Fact]
    public void PerformerFilter_DeathDateCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "deathDateCriterion": { "value": "2020-01-01", "modifier": "greaterThan" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<PerformerFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.DeathDateCriterion);
        Assert.Equal("2020-01-01", result.ObjectFilter.DeathDateCriterion.Value);
    }

    [Fact]
    public void PerformerFilter_GroupsCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "groupsCriterion": { "value": [10, 20], "modifier": "includes" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<PerformerFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.GroupsCriterion);
        Assert.Equal(2, result.ObjectFilter.GroupsCriterion.Value.Count);
    }

    [Fact]
    public void PerformerFilter_IgnoreAutoTagCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "ignoreAutoTagCriterion": { "value": true, "modifier": "equals" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<PerformerFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.IgnoreAutoTagCriterion);
        Assert.True(result.ObjectFilter.IgnoreAutoTagCriterion.Value);
    }

    // ===== TAG FILTER CRITERIA EXISTENCE =====

    [Fact]
    public void TagFilter_HasAllNewCriteria()
    {
        var filter = new TagFilter();
        Assert.Null(filter.NameCriterion);
        Assert.Null(filter.SortNameCriterion);
        Assert.Null(filter.RemoteIdCriterion);
        Assert.Null(filter.RemoteIdValueCriterion);
        Assert.Null(filter.AliasesCriterion);
        Assert.Null(filter.DescriptionCriterion);
        Assert.Null(filter.ImageCountCriterion);
        Assert.Null(filter.GalleryCountCriterion);
        Assert.Null(filter.StudioCountCriterion);
        Assert.Null(filter.GroupCountCriterion);
        Assert.Null(filter.ParentCountCriterion);
        Assert.Null(filter.ChildCountCriterion);
        Assert.Null(filter.IgnoreAutoTagCriterion);
    }

    [Fact]
    public void TagFilter_ImageCountCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "imageCountCriterion": { "value": 5, "modifier": "greaterThan" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<TagFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.ImageCountCriterion);
        Assert.Equal(5, result.ObjectFilter.ImageCountCriterion.Value);
    }

    [Fact]
    public void TagFilter_RemoteIdCriteria_Deserialize()
    {
        var json = """
        {
            "objectFilter": {
                "remoteIdCriterion": { "value": "StashDB", "modifier": "includes" },
                "remoteIdValueCriterion": { "value": "tag-42", "modifier": "equals" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<TagFilter>>(json, Options);

        Assert.NotNull(result?.ObjectFilter?.RemoteIdCriterion);
        Assert.Equal("StashDB", result.ObjectFilter.RemoteIdCriterion.Value);
        Assert.Equal(CriterionModifier.Includes, result.ObjectFilter.RemoteIdCriterion.Modifier);
        Assert.NotNull(result.ObjectFilter.RemoteIdValueCriterion);
        Assert.Equal("tag-42", result.ObjectFilter.RemoteIdValueCriterion.Value);
        Assert.Equal(CriterionModifier.Equals, result.ObjectFilter.RemoteIdValueCriterion.Modifier);
    }

    // ===== STUDIO FILTER CRITERIA EXISTENCE =====

    [Fact]
    public void StudioFilter_HasAllNewCriteria()
    {
        var filter = new StudioFilter();
        Assert.Null(filter.NameCriterion);
        Assert.Null(filter.DetailsCriterion);
        Assert.Null(filter.AliasesCriterion);
        Assert.Null(filter.ParentsCriterion);
        Assert.Null(filter.ChildCountCriterion);
        Assert.Null(filter.TagCountCriterion);
        Assert.Null(filter.GroupCountCriterion);
        Assert.Null(filter.IgnoreAutoTagCriterion);
        Assert.Null(filter.OrganizedCriterion);
    }

    [Fact]
    public void StudioFilter_ParentsCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "parentsCriterion": { "value": [5], "modifier": "includes" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<StudioFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.ParentsCriterion);
        Assert.Single(result.ObjectFilter.ParentsCriterion.Value);
    }

    // ===== GALLERY FILTER CRITERIA EXISTENCE =====

    [Fact]
    public void GalleryFilter_HasAllNewCriteria()
    {
        var filter = new GalleryFilter();
        Assert.Null(filter.TitleCriterion);
        Assert.Null(filter.CodeCriterion);
        Assert.Null(filter.DetailsCriterion);
        Assert.Null(filter.PhotographerCriterion);
        Assert.Null(filter.FileCountCriterion);
        Assert.Null(filter.TagCountCriterion);
        Assert.Null(filter.PerformerCountCriterion);
        Assert.Null(filter.ScenesCriterion);
        Assert.Null(filter.PerformerTagsCriterion);
    }

    [Fact]
    public void GalleryFilter_ScenesCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "scenesCriterion": { "value": [100, 200], "modifier": "includes" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<GalleryFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.ScenesCriterion);
        Assert.Equal(2, result.ObjectFilter.ScenesCriterion.Value.Count);
    }

    // ===== IMAGE FILTER CRITERIA EXISTENCE =====

    [Fact]
    public void ImageFilter_HasAllNewCriteria()
    {
        var filter = new ImageFilter();
        Assert.Null(filter.TitleCriterion);
        Assert.Null(filter.CodeCriterion);
        Assert.Null(filter.DetailsCriterion);
        Assert.Null(filter.PhotographerCriterion);
        Assert.Null(filter.UrlCriterion);
        Assert.Null(filter.DateCriterion);
        Assert.Null(filter.FileCountCriterion);
        Assert.Null(filter.TagCountCriterion);
        Assert.Null(filter.PerformerCountCriterion);
        Assert.Null(filter.PerformerTagsCriterion);
    }

    // ===== GROUP FILTER CRITERIA EXISTENCE =====

    [Fact]
    public void GroupFilter_HasAllNewCriteria()
    {
        var filter = new GroupFilter();
        Assert.Null(filter.NameCriterion);
        Assert.Null(filter.DirectorCriterion);
        Assert.Null(filter.SynopsisCriterion);
        Assert.Null(filter.PerformersCriterion);
        Assert.Null(filter.SceneCountCriterion);
        Assert.Null(filter.TagCountCriterion);
    }

    [Fact]
    public void GroupFilter_PerformersCriterion_Deserializes()
    {
        var json = """
        {
            "objectFilter": {
                "performersCriterion": { "value": [1], "modifier": "includes" }
            }
        }
        """;
        var result = JsonSerializer.Deserialize<FilteredQueryRequest<GroupFilter>>(json, Options);
        Assert.NotNull(result?.ObjectFilter?.PerformersCriterion);
        Assert.Single(result.ObjectFilter.PerformersCriterion.Value);
    }

    // ===== SCENE ENTITY NEW FIELDS =====

    [Fact]
    public void Scene_HasCaptionsAndInteractiveSpeed()
    {
        var scene = new Scene
        {
            Captions = "English subtitles",
            InteractiveSpeed = 75
        };
        Assert.Equal("English subtitles", scene.Captions);
        Assert.Equal(75, scene.InteractiveSpeed);
    }

    [Fact]
    public void Scene_CaptionsAndInteractiveSpeedDefaults()
    {
        var scene = new Scene();
        Assert.Null(scene.Captions);
        Assert.Null(scene.InteractiveSpeed);
    }

    // ===== DTO NEW FIELDS =====

    [Fact]
    public void SceneCreateDto_HasCaptionsAndInteractiveSpeed()
    {
        var dto = new SceneCreateDto("Title", "Code", "Details", "Director", "2024-01-01",
            85, false, null, "English", 50,
            null, null, null, null, null);
        Assert.Equal("English", dto.Captions);
        Assert.Equal(50, dto.InteractiveSpeed);
    }

    [Fact]
    public void SceneUpdateDto_HasCaptionsAndInteractiveSpeed()
    {
        var dto = new SceneUpdateDto("Title", "Code", "Details", "Director", "2024-01-01",
            85, false, null, "English", 50,
            null, null, null, null, null, null);
        Assert.Equal("English", dto.Captions);
        Assert.Equal(50, dto.InteractiveSpeed);
    }

    [Fact]
    public void SceneDto_HasCaptionsAndInteractiveSpeed()
    {
        var dto = new SceneDto(1, "Title", "Code", "Details", "Director", "2024-01-01",
            85, false, null, null, 0, 0, 0, null, 0, "English", 50,
            [], [], [], [], [], [], [], [], null, "2024-01-01", "2024-01-01");
        Assert.Equal("English", dto.Captions);
        Assert.Equal(50, dto.InteractiveSpeed);
    }

    [Fact]
    public void GalleryCreateDto_HasSceneIds()
    {
        var dto = new GalleryCreateDto("Gallery", null, null, null, null, null, false, null, null, null, null, [1, 2]);
        Assert.NotNull(dto.SceneIds);
        Assert.Equal(2, dto.SceneIds.Count);
    }

    [Fact]
    public void ImageCreateDto_HasGalleryIds()
    {
        var dto = new ImageCreateDto("Image", null, null, null, null, false, null, null, null, null, null, [10]);
        Assert.NotNull(dto.GalleryIds);
        Assert.Single(dto.GalleryIds);
    }

    [Fact]
    public void SceneMarkerUpdateDto_HasTagIds()
    {
        var dto = new SceneMarkerUpdateDto("Title", 10.0, null, 1, [5, 6]);
        Assert.NotNull(dto.TagIds);
        Assert.Equal(2, dto.TagIds.Count);
    }

    [Fact]
    public void BulkSceneUpdateDto_HasNewFields()
    {
        var dto = new BulkSceneUpdateDto
        {
            Ids = [1, 2],
            Date = "2024-06-01",
            Code = "ABC",
            Director = "Director",
            GroupIds = [new SceneGroupInputDto(10, 0)],
            GroupMode = BulkUpdateMode.Add
        };
        Assert.Equal("2024-06-01", dto.Date);
        Assert.Equal("ABC", dto.Code);
        Assert.Equal("Director", dto.Director);
        Assert.NotNull(dto.GroupIds);
        Assert.Single(dto.GroupIds);
        Assert.Equal(BulkUpdateMode.Add, dto.GroupMode);
    }

    // ===== COMBINED FILTER ROUND-TRIP TESTS =====

    [Fact]
    public void SceneFilter_AllNewCriteria_RoundTrip()
    {
        var filter = new SceneFilter
        {
            PerformerTagsCriterion = new MultiIdCriterion { Value = [1], Modifier = CriterionModifier.Includes },
            PerformerAgeCriterion = new IntCriterion { Value = 25, Modifier = CriterionModifier.GreaterThan },
            CaptionsCriterion = new StringCriterion { Value = "en", Modifier = CriterionModifier.Includes },
            InteractiveSpeedCriterion = new IntCriterion { Value = 50, Modifier = CriterionModifier.Equals }
        };

        var json = JsonSerializer.Serialize(filter, Options);
        var deserialized = JsonSerializer.Deserialize<SceneFilter>(json, Options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.PerformerTagsCriterion);
        Assert.NotNull(deserialized.PerformerAgeCriterion);
        Assert.NotNull(deserialized.CaptionsCriterion);
        Assert.NotNull(deserialized.InteractiveSpeedCriterion);
        Assert.Equal(CriterionModifier.Includes, deserialized.PerformerTagsCriterion.Modifier);
        Assert.Equal(25, deserialized.PerformerAgeCriterion.Value);
        Assert.Equal("en", deserialized.CaptionsCriterion.Value);
        Assert.Equal(50, deserialized.InteractiveSpeedCriterion.Value);
    }

    [Fact]
    public void PerformerFilter_AllNewCriteria_RoundTrip()
    {
        var filter = new PerformerFilter
        {
            DisambiguationCriterion = new StringCriterion { Value = "test", Modifier = CriterionModifier.Includes },
            DetailsCriterion = new StringCriterion { Value = "bio", Modifier = CriterionModifier.Includes },
            EyeColorCriterion = new StringCriterion { Value = "blue", Modifier = CriterionModifier.Equals },
            HairColorCriterion = new StringCriterion { Value = "blonde", Modifier = CriterionModifier.Equals },
            MeasurementsCriterion = new StringCriterion { Value = "34", Modifier = CriterionModifier.Includes },
            FakeTitsCriterion = new StringCriterion { Value = "yes", Modifier = CriterionModifier.Equals },
            PenisLengthCriterion = new IntCriterion { Value = 6, Modifier = CriterionModifier.GreaterThan },
            CircumcisedCriterion = new StringCriterion { Value = "Cut", Modifier = CriterionModifier.Equals },
            CareerStartCriterion = new DateCriterion { Value = "2010-01-01", Modifier = CriterionModifier.GreaterThan },
            CareerEndCriterion = new DateCriterion { Value = "2020-12-31", Modifier = CriterionModifier.LessThan },
            TattooCriterion = new StringCriterion { Value = "arm", Modifier = CriterionModifier.Includes },
            PiercingsCriterion = new StringCriterion { Value = "ear", Modifier = CriterionModifier.Includes },
            AliasesCriterion = new StringCriterion { Value = "alias1", Modifier = CriterionModifier.Includes },
            DeathDateCriterion = new DateCriterion { Value = "2023-01-01", Modifier = CriterionModifier.IsNull },
            MarkerCountCriterion = new IntCriterion { Value = 0, Modifier = CriterionModifier.GreaterThan },
            PlayCountCriterion = new IntCriterion { Value = 10, Modifier = CriterionModifier.GreaterThan },
            OCounterCriterion = new IntCriterion { Value = 5, Modifier = CriterionModifier.LessThan },
            GroupsCriterion = new MultiIdCriterion { Value = [1], Modifier = CriterionModifier.Includes },
            IgnoreAutoTagCriterion = new BoolCriterion { Value = false },
            TagCountCriterion = new IntCriterion { Value = 3, Modifier = CriterionModifier.Equals }
        };

        var json = JsonSerializer.Serialize(filter, Options);
        var deserialized = JsonSerializer.Deserialize<PerformerFilter>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal("test", deserialized.DisambiguationCriterion?.Value);
        Assert.Equal("blue", deserialized.EyeColorCriterion?.Value);
        Assert.Equal(6, deserialized.PenisLengthCriterion?.Value);
        Assert.Equal("Cut", deserialized.CircumcisedCriterion?.Value);
        Assert.Equal("2010-01-01", deserialized.CareerStartCriterion?.Value);
        Assert.False(deserialized.IgnoreAutoTagCriterion?.Value);
    }

    [Fact]
    public void TagFilter_AllNewCriteria_RoundTrip()
    {
        var filter = new TagFilter
        {
            NameCriterion = new StringCriterion { Value = "test", Modifier = CriterionModifier.Includes },
            SortNameCriterion = new StringCriterion { Value = "sort", Modifier = CriterionModifier.Equals },
            RemoteIdCriterion = new StringCriterion { Value = "stash", Modifier = CriterionModifier.Includes },
            RemoteIdValueCriterion = new StringCriterion { Value = "tag-1", Modifier = CriterionModifier.Equals },
            AliasesCriterion = new StringCriterion { Value = "alias", Modifier = CriterionModifier.Includes },
            DescriptionCriterion = new StringCriterion { Value = "desc", Modifier = CriterionModifier.Includes },
            ImageCountCriterion = new IntCriterion { Value = 1, Modifier = CriterionModifier.GreaterThan },
            GalleryCountCriterion = new IntCriterion { Value = 0, Modifier = CriterionModifier.Equals },
            StudioCountCriterion = new IntCriterion { Value = 2, Modifier = CriterionModifier.LessThan },
            GroupCountCriterion = new IntCriterion { Value = 5, Modifier = CriterionModifier.GreaterThan },
            ParentCountCriterion = new IntCriterion { Value = 1, Modifier = CriterionModifier.Equals },
            ChildCountCriterion = new IntCriterion { Value = 0, Modifier = CriterionModifier.Equals },
            IgnoreAutoTagCriterion = new BoolCriterion { Value = true }
        };

        var json = JsonSerializer.Serialize(filter, Options);
        var deserialized = JsonSerializer.Deserialize<TagFilter>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal("test", deserialized.NameCriterion?.Value);
        Assert.Equal("stash", deserialized.RemoteIdCriterion?.Value);
        Assert.Equal("tag-1", deserialized.RemoteIdValueCriterion?.Value);
        Assert.Equal(1, deserialized.ImageCountCriterion?.Value);
        Assert.True(deserialized.IgnoreAutoTagCriterion?.Value);
    }

    [Fact]
    public void StudioFilter_AllNewCriteria_RoundTrip()
    {
        var filter = new StudioFilter
        {
            NameCriterion = new StringCriterion { Value = "studio", Modifier = CriterionModifier.Includes },
            DetailsCriterion = new StringCriterion { Value = "detail", Modifier = CriterionModifier.Includes },
            AliasesCriterion = new StringCriterion { Value = "alias", Modifier = CriterionModifier.Includes },
            ParentsCriterion = new MultiIdCriterion { Value = [1], Modifier = CriterionModifier.Includes },
            ChildCountCriterion = new IntCriterion { Value = 3, Modifier = CriterionModifier.GreaterThan },
            TagCountCriterion = new IntCriterion { Value = 2, Modifier = CriterionModifier.Equals },
            GroupCountCriterion = new IntCriterion { Value = 1, Modifier = CriterionModifier.GreaterThan },
            IgnoreAutoTagCriterion = new BoolCriterion { Value = false },
            OrganizedCriterion = new BoolCriterion { Value = true }
        };

        var json = JsonSerializer.Serialize(filter, Options);
        var deserialized = JsonSerializer.Deserialize<StudioFilter>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal("studio", deserialized.NameCriterion?.Value);
        Assert.Equal(3, deserialized.ChildCountCriterion?.Value);
        Assert.True(deserialized.OrganizedCriterion?.Value);
    }

    [Fact]
    public void GalleryFilter_AllNewCriteria_RoundTrip()
    {
        var filter = new GalleryFilter
        {
            TitleCriterion = new StringCriterion { Value = "gallery", Modifier = CriterionModifier.Includes },
            CodeCriterion = new StringCriterion { Value = "GAL01", Modifier = CriterionModifier.Equals },
            DetailsCriterion = new StringCriterion { Value = "detail", Modifier = CriterionModifier.Includes },
            PhotographerCriterion = new StringCriterion { Value = "john", Modifier = CriterionModifier.Includes },
            FileCountCriterion = new IntCriterion { Value = 10, Modifier = CriterionModifier.GreaterThan },
            TagCountCriterion = new IntCriterion { Value = 3, Modifier = CriterionModifier.Equals },
            PerformerCountCriterion = new IntCriterion { Value = 1, Modifier = CriterionModifier.GreaterThan },
            ScenesCriterion = new MultiIdCriterion { Value = [1, 2], Modifier = CriterionModifier.Includes },
            PerformerTagsCriterion = new MultiIdCriterion { Value = [5], Modifier = CriterionModifier.Includes }
        };

        var json = JsonSerializer.Serialize(filter, Options);
        var deserialized = JsonSerializer.Deserialize<GalleryFilter>(json, Options);

        Assert.NotNull(deserialized);
    Assert.Equal("gallery", deserialized.TitleCriterion?.Value);
        Assert.Equal("GAL01", deserialized.CodeCriterion?.Value);
        Assert.Equal(10, deserialized.FileCountCriterion?.Value);
        Assert.Equal(2, deserialized.ScenesCriterion?.Value.Count);
    }

    [Fact]
    public void ImageFilter_AllNewCriteria_RoundTrip()
    {
        var filter = new ImageFilter
        {
            TitleCriterion = new StringCriterion { Value = "image", Modifier = CriterionModifier.Includes },
            CodeCriterion = new StringCriterion { Value = "IMG01", Modifier = CriterionModifier.Equals },
            DetailsCriterion = new StringCriterion { Value = "detail", Modifier = CriterionModifier.Includes },
            PhotographerCriterion = new StringCriterion { Value = "jane", Modifier = CriterionModifier.Includes },
            UrlCriterion = new StringCriterion { Value = "https://example.com", Modifier = CriterionModifier.Includes },
            DateCriterion = new DateCriterion { Value = "2024-01-01", Modifier = CriterionModifier.GreaterThan },
            FileCountCriterion = new IntCriterion { Value = 1, Modifier = CriterionModifier.Equals },
            TagCountCriterion = new IntCriterion { Value = 2, Modifier = CriterionModifier.LessThan },
            PerformerCountCriterion = new IntCriterion { Value = 1, Modifier = CriterionModifier.GreaterThan },
            PerformerTagsCriterion = new MultiIdCriterion { Value = [3], Modifier = CriterionModifier.Includes }
        };

        var json = JsonSerializer.Serialize(filter, Options);
        var deserialized = JsonSerializer.Deserialize<ImageFilter>(json, Options);

        Assert.NotNull(deserialized);
    Assert.Equal("image", deserialized.TitleCriterion?.Value);
        Assert.Equal("IMG01", deserialized.CodeCriterion?.Value);
        Assert.Equal("2024-01-01", deserialized.DateCriterion?.Value);
        Assert.Single(deserialized.PerformerTagsCriterion!.Value);
    }

    [Fact]
    public void GroupFilter_AllNewCriteria_RoundTrip()
    {
        var filter = new GroupFilter
        {
            NameCriterion = new StringCriterion { Value = "collection", Modifier = CriterionModifier.Includes },
            DirectorCriterion = new StringCriterion { Value = "spielberg", Modifier = CriterionModifier.Includes },
            SynopsisCriterion = new StringCriterion { Value = "adventure", Modifier = CriterionModifier.Includes },
            PerformersCriterion = new MultiIdCriterion { Value = [1, 2, 3], Modifier = CriterionModifier.IncludesAll },
            SceneCountCriterion = new IntCriterion { Value = 5, Modifier = CriterionModifier.GreaterThan },
            TagCountCriterion = new IntCriterion { Value = 2, Modifier = CriterionModifier.Equals }
        };

        var json = JsonSerializer.Serialize(filter, Options);
        var deserialized = JsonSerializer.Deserialize<GroupFilter>(json, Options);

        Assert.NotNull(deserialized);
    Assert.Equal("collection", deserialized.NameCriterion?.Value);
        Assert.Equal("spielberg", deserialized.DirectorCriterion?.Value);
        Assert.Equal(3, deserialized.PerformersCriterion?.Value.Count);
        Assert.Equal(CriterionModifier.IncludesAll, deserialized.PerformersCriterion?.Modifier);
    }
}
