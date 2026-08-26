namespace Cove.Core.Entities;

public static class CustomFieldEntityTypes
{
    public const string Video = "video";
    public const string Performer = "performer";
    public const string Tag = "tag";
    public const string Studio = "studio";
    public const string Gallery = "gallery";
    public const string Image = "image";
    public const string Group = "group";
    public const string Face = "face";
    public const string Audio = "audio";
    public const string Text = "text";

    public static readonly string[] All = [Video, Performer, Tag, Studio, Gallery, Image, Group, Face, Audio, Text];
}

public static class CustomFieldTypes
{
    public const string Text = "text";
    public const string LongText = "longText";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string Timestamp = "timestamp";
    public const string Url = "url";
    public const string Enum = "enum";
    public const string Json = "json";
    public const string Duration = "duration";
    public const string Percent = "percent";
    public const string Tag = "tag";
    public const string Performer = "performer";
    public const string Studio = "studio";
    public const string Video = "video";
    public const string Gallery = "gallery";
    public const string Image = "image";
    public const string Group = "group";

    public static readonly string[] All =
    [
        Text,
        LongText,
        Number,
        Boolean,
        Date,
        Timestamp,
        Url,
        Enum,
        Json,
        Duration,
        Percent,
        Tag,
        Performer,
        Studio,
        Video,
        Gallery,
        Image,
        Group,
    ];

    public static bool IsNumberLike(string? type)
    {
        var normalized = Normalize(type);
        return normalized == Number || normalized == Duration || normalized == Percent;
    }

    public static bool IsDateLike(string? type) => Normalize(type) == Date;
    public static bool IsTimestampLike(string? type) => Normalize(type) == Timestamp;
    public static bool IsBoolean(string? type) => Normalize(type) == Boolean;
    public static bool IsJson(string? type) => Normalize(type) == Json;

    public static bool IsReference(string? type)
    {
        var normalized = Normalize(type);
        return normalized == Tag || normalized == Performer || normalized == Studio || normalized == Video || normalized == Gallery || normalized == Image || normalized == Group;
    }

    public static string Normalize(string? type)
    {
        var normalized = type?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return Text;

        return All.FirstOrDefault(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)) ?? Text;
    }
}

public class CustomFieldDefinition : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = CustomFieldTypes.Text;
    public string[] EntityTypes { get; set; } = [];
    public string[] Options { get; set; } = [];
    public bool Filterable { get; set; } = true;
    public bool Sortable { get; set; }
    public bool IsMultiValue { get; set; }
    public int DisplayOrder { get; set; }

    public ICollection<CustomFieldValue> Values { get; set; } = [];
}

public class CustomFieldValue : BaseEntity
{
    public int DefinitionId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public int Position { get; set; }

    public string? TextValue { get; set; }
    public string? JsonValue { get; set; }
    public decimal? NumberValue { get; set; }
    public bool? BoolValue { get; set; }
    public DateOnly? DateValue { get; set; }
    public DateTime? TimestampValue { get; set; }
    public int? IntegerValue { get; set; }

    public CustomFieldDefinition? Definition { get; set; }
}
