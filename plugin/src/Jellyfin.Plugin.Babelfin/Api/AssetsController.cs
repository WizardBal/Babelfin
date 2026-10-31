using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>
/// Serves Babelfin's embedded web assets under {BaseUrl}/Babelfin/assets/{path} (scripts, styles, translation files).
/// Anonymous like client.js: the assets hold no data, only the UI. The page itself is at /Babelfin/app.
/// </summary>
[ApiController]
[Route("Babelfin/assets")]
public class AssetsController : ControllerBase
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".woff2"] = "font/woff2",
    };

    private static readonly HashSet<string> Compressible = new(StringComparer.OrdinalIgnoreCase) { ".js", ".mjs", ".css", ".json", ".html", ".svg" };

    /// <summary>Gets one asset.</summary>
    /// <param name="path">The path under Assets, e.g. "app.js", "i18n/en.json".</param>
    /// <param name="v">The version the page asked for; when current, the asset may be cached for a year.</param>
    /// <returns>The asset, or 404.</returns>
    [HttpGet("{**path}")]
    [AllowAnonymous]
    public async Task<ActionResult> Get([FromRoute] string path, [FromQuery] string? v)
    {
        var logical = LogicalName(path);
        if (logical is null)
        {
            return NotFound();
        }

        var extension = System.IO.Path.GetExtension(path);
        if (!ContentTypes.TryGetValue(extension, out var contentType))
        {
            return NotFound();
        }

        Payload payload;
        try
        {
            payload = ResponseWriter.ReadResource(logical, contentType, Compressible.Contains(extension));
        }
        catch (System.IO.FileNotFoundException)
        {
            return NotFound();
        }

        var version = Plugin.VersionString;
        var cacheControl = string.Equals(v, version, StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-cache";
        await ResponseWriter.WriteAsync(HttpContext, payload, "bf-" + version + "-" + Normalize(path).Replace('/', '.'), cacheControl).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>
    /// Maps a request path to the embedded resource's logical name. The csproj embeds Assets/**/* as
    /// "Babelfin.%(RecursiveDir)%(Filename)%(Extension)" with '/' separators. Older builds used '\', so both are accepted.
    /// Paths with ".." or absolute segments are refused.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>The logical name that exists, or null.</returns>
    internal static string? LogicalName(string? path)
    {
        var clean = Normalize(path);
        if (clean.Length == 0 || clean.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
        {
            return null;
        }

        // Case-insensitive: Jellyfin stores UI language tags in lower case ("pt-br"); the files are named canonically ("pt-BR.json").
        var names = typeof(AssetsController).Assembly.GetManifestResourceNames();
        foreach (var candidate in new[] { "Babelfin." + clean, "Babelfin." + clean.Replace('/', '\\') })
        {
            var match = names.FirstOrDefault(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string Normalize(string? path) => (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
}
