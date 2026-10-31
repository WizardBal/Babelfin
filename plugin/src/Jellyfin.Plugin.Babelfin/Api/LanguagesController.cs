using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>
/// The normalised language list, under {BaseUrl}/Babelfin/Languages, for any signed-in person.
/// </summary>
/// <remarks>
/// Names are the English source strings, localized by the UI; the list follows the table's
/// order; a code the table can't name is listed under its code. The ETag is a hash of the list, computed once per language table (the table is rebuilt
/// when the admin changes aliases, which changes the ETag); responses carry <c>Cache-Control: private, no-cache</c> so clients revalidate cheaply with
/// <c>If-None-Match</c> and get a 304.
/// </remarks>
[ApiController]
[Route("Babelfin")]
[Authorize]
public class LanguagesController : ControllerBase
{
    private static readonly object SyncRoot = new();
    private static Snapshot? _snapshot;

    /// <summary>Gets every language Babelfin knows.</summary>
    /// <returns>The languages, or 304 when the client's copy is current.</returns>
    [HttpGet("Languages")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public ActionResult<IReadOnlyList<LanguageDto>> GetLanguages()
    {
        var snapshot = Current(EngineServices.Current.Languages);
        Response.Headers[HeaderNames.ETag] = snapshot.ETag;
        Response.Headers[HeaderNames.CacheControl] = "private, no-cache";
        var ifNoneMatch = Request.Headers[HeaderNames.IfNoneMatch].ToString();
        if (ifNoneMatch.Length > 0 && ifNoneMatch.Split(',').Any(t => string.Equals(t.Trim(), snapshot.ETag, StringComparison.Ordinal) || t.Trim() == "*"))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(snapshot.Languages);
    }

    /// <summary>Builds the language list from a table.</summary>
    /// <param name="table">The language table.</param>
    /// <returns>The list, in table order.</returns>
    [NonAction]
    public static IReadOnlyList<LanguageDto> Build(LanguageTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var list = new List<LanguageDto>(table.Codes.Count);
        foreach (var code in table.Codes)
        {
            var tag = table.Resolve(code);
            if (tag is null)
            {
                continue;
            }

            list.Add(new LanguageDto(tag.Code, table.EnglishName(tag) ?? tag.Code, tag.Base, tag.Variety, tag.Script, tag.Region));
        }

        return list;
    }

    private static Snapshot Current(LanguageTable table)
    {
        lock (SyncRoot)
        {
            if (_snapshot is not null && ReferenceEquals(_snapshot.Table, table))
            {
                return _snapshot;
            }

            var languages = Build(table);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(languages)));
            var etag = "\"bf-lang-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + "\"";
            _snapshot = new Snapshot(table, languages, etag);
            return _snapshot;
        }
    }

    private sealed record Snapshot(LanguageTable Table, IReadOnlyList<LanguageDto> Languages, string ETag);
}
