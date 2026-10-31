using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Babelfin.Api;

// Expected values are transport metadata on an otherwise unchanged flat settings body.
// Decode into the concrete storage type, so this metadata cannot enter configuration or exports.
internal static class SettingsUpdateRequest
{
    internal const int MaxExpectedBytes = 8 * 1024 * 1024;

    internal static bool TryRead<T>(JsonElement body, string? fields, out T? settings, out JsonElement? expected, out ApiProblem? error, HttpContext? context = null)
        where T : class
    {
        settings = null;
        expected = null;
        error = null;
        try
        {
            if (body.ValueKind != JsonValueKind.Object) throw new JsonException();
            var supplied = body.EnumerateObject().Where(p => string.Equals(p.Name, "expectedValues", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (supplied.Length > 1 || (supplied.Length == 1 && fields is null))
            {
                error = InvalidExpected();
                return false;
            }
            if (supplied.Length == 1)
            {
                expected = supplied[0].Value;
                if (expected.Value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(expected.Value.GetRawText()) > MaxExpectedBytes)
                {
                    error = InvalidExpected();
                    return false;
                }
            }
            // Preserve the host's existing PUT binding, including Jellyfin's compact GUID
            // converter and its casing policy. Storage JSON has a different GUID convention.
            var options = context?.RequestServices?.GetService<IOptions<JsonOptions>>()?.Value.JsonSerializerOptions ?? BabelfinJson.Options;
            settings = body.Deserialize<T>(options);
            if (settings is null) throw new JsonException();
            return true;
        }
        catch (JsonException)
        {
            error = new ApiProblem("The settings are not valid.", Array.Empty<ValidationError>());
            return false;
        }
    }

    internal static ApiProblem InvalidExpected() => new("The expected field values are not valid. Reload the settings and retry.",
        new[] { new ValidationError("expectedValues", "Supply one object with the original value of every selected field, using either the request body or the legacy header.") });
}
