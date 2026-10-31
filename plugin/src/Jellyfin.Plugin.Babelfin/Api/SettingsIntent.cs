using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Api;

// Explicit field intent is independent of the default values introduced by typed JSON binding.
// Libraries support stable-id row leaves; other named arrays are atomic replacements. Nullable leaf values include explicit null (inherit/default).
internal sealed class SettingsIntent
{
    private static readonly JsonSerializerOptions CompleteJson = new(BabelfinJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private readonly string[][] _paths;
    private JsonObject? _expected;
    private SettingsIntent(string[][] paths) => _paths = paths;

    internal static bool TryParse(string? fields, bool admin, bool server, out SettingsIntent? intent, out ApiProblem? error)
    {
        intent = null; error = null;
        if (fields is null) return true; // Legacy full PUT.
        var layer = typeof(SettingsLayer).GetProperties().Where(p => p.CanWrite).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet(StringComparer.Ordinal);
        var top = server
            ? ImportPlanner.Sections.SelectMany(s => s.Properties).Select(JsonNamingPolicy.CamelCase.ConvertName).Where(p => p != "retired").ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(new[] { "enabled", "useEverydayEverywhere", "perLibrary", "handleNextEpisode", "libraries", "smartRules", "collectionOrder" }, StringComparer.Ordinal);
        var paths = fields.Split(',').Select(p => p.Trim().Split('.')).ToArray();
        bool Allowed(string[] path)
        {
            if (server && path.SequenceEqual(new[] { "newUserTemplate", "everyday", "understood" })) return true;
            if (path[0] == "libraries" && path.Length > 1)
            {
                if (!Guid.TryParse(path[1], out var id) || id == Guid.Empty) return false;
                path[1] = id.ToString("N");
                return (path.Length == 4 && path[2] == "settings" && layer.Contains(path[3]))
                    || (server && path.Length == 3 && path[2] == "originalAudio");
            }
            if (path.Length == 1) return top.Contains(path[0]) && path[0] != "everyday";
            if (path.Length != 2) return false;
            if (path[0] == "everyday" || (!server && admin && path[0] == "locks")) return layer.Contains(path[1]);
            if (!server && path[0] == "seed") return path[1] == "confirmed";
            var type = path[0] == "starter" ? typeof(StarterSettings) : path[0] == "keywords" ? typeof(KeywordSettings) : null;
            return server && type is not null && type.GetProperties().Any(p => p.CanWrite && p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length == 0 && JsonNamingPolicy.CamelCase.ConvertName(p.Name) == path[1]);
        }
        if (paths.Length > 100 || paths.Any(p => !Allowed(p))
            || paths.Any(a => paths.Any(b => a.Length < b.Length && a.SequenceEqual(b.Take(a.Length)))))
        {
            error = new ApiProblem("The field intent is not valid. Name editable fields explicitly; everyday and locks require leaf paths.", new[] { new ValidationError("fields", "Unknown, empty, or server-owned field path.") });
            return false;
        }
        intent = new SettingsIntent(paths);
        return true;
    }

    internal void ReadExpected(Microsoft.AspNetCore.Http.HttpRequest? request, JsonElement? bodyExpected = null)
    {
        var legacy = request?.Headers.ContainsKey("X-Babelfin-Expected") == true;
        if (!legacy && !bodyExpected.HasValue) return;
        try
        {
            if (legacy && bodyExpected.HasValue) throw new FormatException();
            JsonObject supplied;
            if (bodyExpected.HasValue)
            {
                var body = bodyExpected.Value;
                if (body.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(body.GetRawText()) > SettingsUpdateRequest.MaxExpectedBytes) throw new FormatException();
                // Reject duplicate keys rather than accepting the last value during JSON binding.
                if (body.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != body.EnumerateObject().Count()) throw new FormatException();
                supplied = JsonNode.Parse(body.GetRawText()) as JsonObject ?? throw new FormatException();
            }
            else
            {
                var header = request!.Headers["X-Babelfin-Expected"].ToString();
                if (header.Length == 0 || header.Length > 131072 || header.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw new FormatException();
                var encoded = header.Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                supplied = JsonNode.Parse(Convert.FromBase64String(encoded)) as JsonObject ?? throw new FormatException();
            }
            _expected = new JsonObject();
            foreach (var field in supplied)
            {
                var path = field.Key.Split('.');
                if (path.Length > 1 && path[0] == "libraries" && Guid.TryParse(path[1], out var id)) path[1] = id.ToString("N");
                _expected.Add(string.Join(".", path), field.Value?.DeepClone());
            }
            var paths = _paths.Select(path => string.Join(".", path)).ToHashSet(StringComparer.Ordinal);
            if (_expected.Count != paths.Count || _expected.Any(field => !paths.Contains(field.Key))) throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw InvalidExpected();
        }
    }

    internal void CheckExpected<T>(T latest)
    {
        if (_expected is null) return;
        var document = JsonSerializer.SerializeToNode(latest, CompleteJson)!;
        var conflicts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var path in _paths)
        {
            var key = string.Join(".", path);
            var current = ValueAt(document, path);
            JsonNode? expected;
            try
            {
                // GET responses omit defaults. Bind expected values to the actual field type before
                // completing JSON so unchanged arrays of settings rows compare like stored values.
                var type = FieldType(typeof(T), path);
                var value = _expected[key]?.Deserialize(type, ImportFileFormat.ReadOptions);
                expected = value is null ? null : JsonSerializer.SerializeToNode(value, type, CompleteJson);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                throw InvalidExpected();
            }
            if (!JsonNode.DeepEquals(current, expected)) conflicts[key] = current?.DeepClone();
        }
        if (conflicts.Count == 0) return;
        var person = latest as PersonSettings;
        throw new SettingsConflictException(new ApiProblem("Settings changed while you were editing. Reload these fields or keep your changes.", Array.Empty<ValidationError>())
        {
            Code = "field-conflict", ConflictingPaths = conflicts.Keys.ToArray(), CurrentValues = conflicts,
            LastChangedAt = person?.LastChangedAt, LastChangedBy = person?.LastChangedBy, LastChangedByName = person?.LastChangedByName,
        });
    }

    private static SettingsValidationException InvalidExpected() => new(SettingsUpdateRequest.InvalidExpected());

    private static JsonNode? ValueAt(JsonNode document, string[] path)
    {
        JsonNode? current = document;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == "libraries" && i == 0 && path.Length > 1)
            {
                current = (current?["libraries"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(row => Guid.TryParse(row["libraryId"]?.GetValue<string>(), out var id) && id == Guid.Parse(path[1]));
                i++;
            }
            else current = current?[path[i]];
        }
        return current;
    }

    private static Type FieldType(Type root, string[] path)
    {
        var type = root;
        for (var i = 0; i < path.Length; i++)
        {
            type = type.GetProperties().First(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name) == path[i]).PropertyType;
            if (path[i] == "libraries" && i == 0 && path.Length > 1) { type = type.GetGenericArguments()[0]; i++; }
        }
        return type;
    }

    internal PersonSettings Apply(PersonSettings latest, PersonSettings body)
    {
        var result = ApplyCore(latest, body);
        result.StorageEpoch = latest.StorageEpoch;
        return result;
    }
    internal PluginConfiguration Apply(PluginConfiguration latest, PluginConfiguration body) => ApplyCore(latest, body);

    // Access validation follows the same field intent. Untouched malformed references are neither rechecked nor rewritten.
    internal PersonSettings ReferencesToCheck(PersonSettings document) => Apply(new PersonSettings(), document);

    // Validate a projection of the selected fields, never untouched rows whose language aliases may have changed.
    internal IReadOnlyList<ValidationError> Validate(PersonSettings document, LanguageTable languages, bool includeLocks, PersonSettings? before = null)
    {
        var selected = Apply(new PersonSettings(), document);
        AddLanguageDependencies(selected.Everyday, document.Everyday, new[] { "everyday" });
        if (includeLocks) AddLanguageDependencies(selected.Locks, document.Locks, new[] { "locks" });
        foreach (var row in selected.Libraries)
            if (Guid.TryParse(row.LibraryId, out var id) && document.FindLibrary(id) is { } current)
                AddLanguageDependencies(row.Settings, current.Settings, new[] { "libraries", id.ToString("N"), "settings" });
        var errors = RemapLibraryErrors(SettingsValidator.ValidatePerson(selected, languages, includeLocks),
            selected.Libraries.Select(row => row.LibraryId).ToArray(), document.Libraries.Select(row => row.LibraryId).ToArray());
        if (before is not null && document.Libraries.Count > SettingsValidator.MaxLibraries && document.Libraries.Count > before.Libraries.Count)
            errors.Add(new ValidationError("libraries", "too many libraries"));
        return errors;
    }

    internal IReadOnlyList<ValidationError> Validate(PluginConfiguration document, LanguageTable languages, PluginConfiguration before)
    {
        var selected = Apply(new PluginConfiguration(), document);
        AddLanguageDependencies(selected.Everyday, document.Everyday, new[] { "everyday" });
        foreach (var row in selected.Libraries)
            if (Guid.TryParse(row.LibraryId, out var id) && document.FindLibrary(id) is { } current)
                AddLanguageDependencies(row.Settings, current.Settings, new[] { "libraries", id.ToString("N"), "settings" });
        var errors = RemapLibraryErrors(SettingsValidator.ValidateAdmin(selected, languages),
            selected.Libraries.Select(row => row.LibraryId).ToArray(), document.Libraries.Select(row => row.LibraryId).ToArray());
        if (document.Libraries.Count > SettingsValidator.MaxLibraries && document.Libraries.Count > before.Libraries.Count)
            errors.Add(new ValidationError("libraries", "too many libraries"));
        return errors;
    }

    private List<ValidationError> RemapLibraryErrors(IReadOnlyList<ValidationError> errors, string[] selected, string[] actual)
    {
        if (!_paths.Any(path => path[0] == "libraries" && path.Length > 1)) return errors.ToList();
        return errors.Select(error =>
        {
            var end = error.Path.IndexOf(']');
            if (!error.Path.StartsWith("libraries[", StringComparison.Ordinal) || end < 10
                || !int.TryParse(error.Path.AsSpan(10, end - 10), out var index) || index < 0 || index >= selected.Length
                || !Guid.TryParse(selected[index], out var id)) return error;
            var current = Array.FindIndex(actual, value => PersonSettings.SameId(value, id));
            return current < 0 ? error : new ValidationError($"libraries[{current}]" + error.Path[(end + 1)..], error.Message);
        }).ToList();
    }

    private void AddLanguageDependencies(SettingsLayer selected, SettingsLayer current, string[] prefix)
    {
        bool Edits(string leaf) => _paths.Any(path => path.SequenceEqual(prefix.Append(leaf)));
        if (Edits("unlabelledAudioLanguage")) selected.UnlabelledAudio = current.UnlabelledAudio;
        if (Edits("unlabelledAudio") && current.UnlabelledAudio == UnlabelledAudioRule.SpecificLanguage)
            selected.UnlabelledAudioLanguage = current.UnlabelledAudioLanguage;
    }

    private T ApplyCore<T>(T latest, T body)
    {
        var result = JsonSerializer.SerializeToNode(latest, CompleteJson)!.AsObject();
        var source = JsonSerializer.SerializeToNode(body, CompleteJson)!.AsObject();
        foreach (var path in _paths)
        {
            if (path.SequenceEqual(new[] { "newUserTemplate", "everyday", "understood" }))
            {
                var value = source["newUserTemplate"]?["everyday"]?["understood"];
                var current = result["newUserTemplate"] as JsonObject;
                if (current is null && value is null) continue;
                if (current is null) result["newUserTemplate"] = current = JsonSerializer.SerializeToNode(new PersonSettings(), CompleteJson)!.AsObject();
                if (current["everyday"] is not JsonObject everyday) current["everyday"] = everyday = new JsonObject();
                everyday["understood"] = value?.DeepClone();
                // Re-serialize to recompute read-only fields such as IsEmpty before deciding whether every field is truly default.
                var normalized = JsonSerializer.SerializeToNode(current.Deserialize<PersonSettings>(CompleteJson), CompleteJson);
                if (JsonNode.DeepEquals(normalized, JsonSerializer.SerializeToNode(new PersonSettings(), CompleteJson))) result["newUserTemplate"] = null;
                continue;
            }
            if (path[0] == "libraries" && path.Length > 1)
            {
                ApplyLibraryLeaf(result, source, path);
                continue;
            }
            var to = result; JsonNode? from = source;
            for (var i = 0; i < path.Length - 1; i++)
            {
                to[path[i]] ??= new JsonObject();
                to = to[path[i]]!.AsObject();
                from = from?[path[i]];
            }
            to[path[^1]] = path[0] == "seed" && from is null ? JsonValue.Create(false) : from?[path[^1]]?.DeepClone();
        }
        return result.Deserialize<T>(CompleteJson)!;
    }

    private static void ApplyLibraryLeaf(JsonObject result, JsonObject source, string[] path)
    {
        var id = Guid.Parse(path[1]);
        bool Matches(JsonObject row) => Guid.TryParse(row["libraryId"]?.GetValue<string>(), out var key) && key == id;
        var rows = result["libraries"] as JsonArray;
        if (rows is null) result["libraries"] = rows = new JsonArray();
        var row = rows.OfType<JsonObject>().FirstOrDefault(Matches);
        var from = (source["libraries"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(Matches);
        // An absent body row clears only the named leaf of an existing row. It never deletes another field or row.
        if (row is null && from is null) return;
        if (row is null)
        {
            row = new JsonObject { ["libraryId"] = id.ToString("N"), ["settings"] = new JsonObject() };
            rows.Add(row);
        }
        if (path.Length == 3)
        {
            row["originalAudio"] = from?["originalAudio"]?.DeepClone() ?? JsonValue.Create("Auto");
            return;
        }
        if (row["settings"] is not JsonObject settings) row["settings"] = settings = new JsonObject();
        settings[path[3]] = from?["settings"]?[path[3]]?.DeepClone();
    }

}
