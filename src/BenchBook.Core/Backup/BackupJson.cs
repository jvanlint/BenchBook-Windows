using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchBook.Core.Model;

namespace BenchBook.Core.Backup;

/// <summary>JSON settings for library.json and manifest.json.</summary>
internal static class BackupJson
{
    public static readonly JsonSerializerOptions Options = Make();

    public static readonly JsonSerializerOptions IndentedOptions = new(Options) { WriteIndented = true };

    private static JsonSerializerOptions Make()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Unknown fields are ignored, so a newer app can add fields.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            // Readable non-ASCII text such as “ ” and accents.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        };
        options.Converters.Add(new IsoDateConverter());
        return options;
    }

    private sealed class IsoDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            return IsoDate.TryRead(text, out var date) ? date : throw new JsonException($"“{text}” isn't an ISO 8601 date.");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(IsoDate.Write(value));
    }
}

/// <summary>Describes a backup, so a restore can check it before touching the library.</summary>
public sealed class BackupManifest
{
    public const int CurrentFormat = 2;

    public required int Format { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string AppVersion { get; set; }
    /// <summary>"mac" or "windows". For messages only, never for logic.</summary>
    public required string Platform { get; set; }
    public required string SchemaVersion { get; set; }
    public required BackupCounts Counts { get; set; }
    public required List<BackupFile> Files { get; set; }

    [JsonIgnore]
    public long TotalSize => Files.Sum(f => f.Size);
}

public sealed class BackupCounts
{
    public required long Projects { get; set; }
    public required long References { get; set; }
    public required long Paints { get; set; }
}

public sealed class BackupFile
{
    public required string Path { get; set; }
    public required long Size { get; set; }
    /// <summary>Lowercase hex.</summary>
    public required string Sha256 { get; set; }
}
