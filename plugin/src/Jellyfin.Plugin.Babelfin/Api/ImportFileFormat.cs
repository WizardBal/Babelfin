using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Api;

// Import files have their own stable JSON contract, independent of the host MVC naming policy.
internal static class ImportFileFormat
{
    internal static readonly JsonSerializerOptions ReadOptions = CreateReadOptions();

    private static JsonSerializerOptions CreateReadOptions()
    {
        var options = new JsonSerializerOptions(BabelfinJson.Options);
        options.Converters.Insert(0, new ImportGuidConverter());
        return options;
    }

    // Jellyfin GET responses use compact GUIDs; downloaded files use canonical GUIDs.
    // File imports and expected-value comparisons accept both without changing storage serialization.
    private sealed class ImportGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadGuid(ref reader);

        public override Guid ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadGuid(ref reader);

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString("D"));

        private static Guid ReadGuid(ref Utf8JsonReader reader)
        {
            var text = reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName ? reader.GetString() : null;
            if (Guid.TryParseExact(text, "N", out var value) || Guid.TryParseExact(text, "D", out value)) return value;
            throw new JsonException("Expected a GUID in N or D format.");
        }
    }

    internal static bool TryRead<T>(JsonElement json, bool personal, out T? document, out ApiProblem? error)
    {
        document = default; error = null;
        var fields = personal
            ? new[] { "everyday", "enabled", "perLibrary", "handleNextEpisode", "libraries", "smartRules", "collectionOrder", "overrides", "burnedIn", "seed", "useEverydayEverywhere" }
            : new[] { "admin", "users" };
        if (json.ValueKind != JsonValueKind.Object || !json.EnumerateObject().Any(p => fields.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
        {
            error = new ApiProblem("The file contains no importable settings.", Array.Empty<ValidationError>());
            return false;
        }
        try
        {
            document = json.Deserialize<T>(ReadOptions);
            if (document is not null) return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            error = new ApiProblem("The import file does not match the supported settings format.", new[] { new ValidationError("file", "Check the JSON field types and settings values, then retry.") });
            return false;
        }
        error = new ApiProblem("The import file is empty.", Array.Empty<ValidationError>());
        return false;
    }
}
