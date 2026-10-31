using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>
/// Babelfin's HTTP API, under {BaseUrl}/Babelfin: the web client assets, Ping, Explain and the admin status.
/// The settings endpoints live in <see cref="MeController"/>, <see cref="AdminController"/> and <see cref="LanguagesController"/>.
/// </summary>
[ApiController]
[StorageApiErrors]
[Route("Babelfin")]
public class BabelfinController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly DecoratorStats _stats;
    private readonly HostSettings _host;
    private readonly IConfiguration _configuration;
    private readonly IPersonStore _store;
    private readonly ILibraryFacts _facts;

    /// <summary>
    /// Initializes a new instance of the <see cref="BabelfinController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="mediaSourceManager">Media source manager (Babelfin's decorator when wrapped).</param>
    /// <param name="stats">Babelfin's counters and recent decisions.</param>
    /// <param name="host">Server settings read at startup.</param>
    /// <param name="configuration">The app configuration (for Jellyfin's "hostwebclient" switch).</param>
    /// <param name="store">The per-person store (admin status).</param>
    /// <param name="facts">Library facts (admin status).</param>
    public BabelfinController(ILibraryManager libraryManager, IUserManager userManager, IMediaSourceManager mediaSourceManager, DecoratorStats stats, HostSettings host, IConfiguration configuration, IPersonStore store, ILibraryFacts facts)
    {
        _store = store;
        _facts = facts;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _stats = stats;
        _host = host;
        _configuration = configuration;
    }

    /// <summary>
    /// Gets Babelfin's web client script.
    /// </summary>
    /// <param name="v">The version the page asked for; when current, the script may be cached for a year.</param>
    /// <returns>The script.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    public async Task<ActionResult> GetClientScript([FromQuery] string? v)
    {
        var version = Plugin.VersionString;
        var payload = ResponseWriter.ReadResource("Babelfin.client.js", "text/javascript; charset=utf-8", true);
        var cacheControl = string.Equals(v, version, StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-cache";
        await ResponseWriter.WriteAsync(HttpContext, payload, "bf-" + version, cacheControl).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>
    /// Gets Babelfin's own page.
    /// </summary>
    /// <returns>The page.</returns>
    [HttpGet("app")]
    [AllowAnonymous]
    public async Task<ActionResult> GetApp()
    {
        var payload = ResponseWriter.ReadResource("Babelfin.app.html", "text/html; charset=utf-8", true);
        await ResponseWriter.WriteAsync(HttpContext, payload, "bf-app-" + Plugin.VersionString, "no-cache").ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>
    /// Tells a signed-in person that Babelfin is running.
    /// </summary>
    /// <returns>Version and state.</returns>
    [HttpGet("Ping")]
    [Authorize]
    [Produces("application/json")]
    public ActionResult Ping()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            version = Plugin.VersionString,
            enabled = config?.Enabled ?? false,
            dryRun = config?.DryRun ?? false,
            wrapped = _stats.Wrapped,
            innerType = _stats.InnerTypeName,
        });
    }

    /// <summary>
    /// Explains the tracks Babelfin chose for the signed-in person on one item: the trace sentences, the engine's one-paragraph
    /// explanation and its structured trace. The engine's classified track list is never sent (it holds external file names).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="mediaSourceId">Optional media source id.</param>
    /// <returns>One entry per media source.</returns>
    [HttpGet("Me/Explain")]
    [Authorize]
    [Produces("application/json")]
    public ActionResult Explain([FromQuery] Guid itemId, [FromQuery] string? mediaSourceId)
    {
        var userId = ClaimsHelper.GetUserId(User);
        var user = userId is null ? null : _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return Unauthorized();
        }

        // The access check: an item this person can't see is simply not found.
        var item = itemId == Guid.Empty ? null : LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(itemId, user));
        if (item is null)
        {
            return NotFound();
        }

        var sources = _mediaSourceManager.GetStaticMediaSources(item, true, user) ?? Array.Empty<MediaBrowser.Model.Dto.MediaSourceInfo>();
        var result = sources
            .Where(s => string.IsNullOrEmpty(mediaSourceId) || string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase))
            .Select(s =>
            {
                var record = _stats.GetDecision(user.Id, item.Id, s.Id);
                var decision = record?.Decision;
                return new
                {
                    id = s.Id,
                    name = s.Name,
                    jellyfinAudioIndex = record is null ? s.DefaultAudioStreamIndex : record.JellyfinAudioIndex,
                    jellyfinSubtitleIndex = record is null ? s.DefaultSubtitleStreamIndex : record.JellyfinSubtitleIndex,
                    babelfinAudioIndex = decision?.AudioIndex,
                    babelfinSubtitleIndex = decision?.SubtitleIndex,
                    applied = record?.Outcome == DecisionOutcome.Applied,
                    outcome = record?.Outcome.ToString() ?? "None",
                    trace = decision?.Trace.ToArray()
                        ?? new[] { record is null ? "No decision recorded" : "Babelfin had nothing to say; Jellyfin's choice stands" },
                    explanation = decision?.Explanation,
                    engineTrace = decision?.Engine?.Trace
                        .Select(l => new EngineTraceLineDto(l.Code, l.Text, l.TrackIndex))
                        .ToArray(),
                    streams = (s.MediaStreams ?? new System.Collections.Generic.List<MediaStream>())
                        .Where(m => m.Type is MediaStreamType.Audio or MediaStreamType.Subtitle or MediaStreamType.Video)
                        .Select(m => new
                        {
                            index = m.Index,
                            type = m.Type.ToString(),
                            language = m.Language,
                            title = m.Title,
                            isDefault = m.IsDefault,
                            isForced = m.IsForced,
                            isHearingImpaired = m.IsHearingImpaired,
                            isExternal = m.IsExternal,
                            isOriginal = m.IsOriginal,
                            codec = m.Codec,
                        })
                        .ToArray(),
                };
            })
            .ToArray();

        return Ok(result);
    }

    /// <summary>
    /// The light version of Explain for the item chip: per media source, the chosen audio and subtitle with their
    /// language codes, English names and kind, and the one-paragraph explanation. Names are translated client-side from the codes.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>One entry per media source, or 404 when the person can't see the item.</returns>
    [HttpGet("Me/Choice")]
    [ProducesResponseType(503)]
    [Authorize]
    [Produces("application/json")]
    public ActionResult Choice([FromQuery] Guid itemId)
    {
        var userId = ClaimsHelper.GetUserId(User);
        var user = userId is null ? null : _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return Unauthorized();
        }

        try { return ChoiceForItem(itemId, user); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (HttpContext?.RequestServices?.GetService(typeof(ILogger<BabelfinController>)) as ILogger<BabelfinController>)
                ?.LogWarning(ex, "[Babelfin] Choice diagnostic temporarily unavailable; no settings were changed");
            return StatusCode(503, new ApiProblem("The library is temporarily unavailable. Retry checking this title later; no settings were changed.", Array.Empty<ValidationError>()));
        }
    }

    private ActionResult ChoiceForItem(Guid itemId, Jellyfin.Database.Implementations.Entities.User user)
    {
        var item = itemId == Guid.Empty ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        // A series or season has no media of its own: decide for its first episode, so the chip can show (and the dialog
        // set) what plays for the show or season. Anything else without video media gets no chip.
        var forItem = item;
        if (item is MediaBrowser.Controller.Entities.TV.Series or MediaBrowser.Controller.Entities.TV.Season)
        {
            forItem = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                AncestorIds = new[] { item.Id },
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false,
                OrderBy = new[] { (Jellyfin.Data.Enums.ItemSortBy.ParentIndexNumber, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending), (Jellyfin.Data.Enums.ItemSortBy.IndexNumber, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) },
                Limit = 1,
            }).FirstOrDefault();
        }

        if (forItem is null || forItem.IsFolder || forItem.MediaType != Jellyfin.Data.Enums.MediaType.Video)
        {
            return Ok(Array.Empty<ChoiceDto>());
        }

        // Babelfin switched off for the server or for this person: no chip.
        if (Plugin.Instance?.Configuration is { Enabled: false } || _store.TryGet(user.Id) is { Enabled: false })
        {
            return Ok(Array.Empty<ChoiceDto>());
        }

        var languages = Jellyfin.Plugin.Babelfin.Decisions.EngineServices.Current.Languages;
        IReadOnlyList<MediaBrowser.Model.Dto.MediaSourceInfo> sources;
        try
        {
            sources = _mediaSourceManager.GetStaticMediaSources(forItem, true, user) ?? Array.Empty<MediaBrowser.Model.Dto.MediaSourceInfo>();
        }
        catch (ArgumentException)
        {
            // Jellyfin refuses items it considers unplayable; no chip for them.
            return Ok(Array.Empty<ChoiceDto>());
        }

        item = forItem;
        var result = sources.Select(s =>
        {
            var record = _stats.GetDecision(user.Id, item.Id, s.Id);
            var engine = record?.Decision?.Engine;
            if (engine is null && record is { Outcome: DecisionOutcome.PassedThrough })
            {
                // Nothing chosen: the chip still shows what plays (the video's own tracks), so the dialog stays one click away.
                var tracks = Jellyfin.Plugin.Babelfin.Decisions.StreamMapper.Map(s.MediaStreams, s.Path)
                    .Select(t => Jellyfin.Plugin.Babelfin.Decisions.EngineServices.Current.Classifier.Classify(t))
                    .ToList();
                var plays = tracks.FirstOrDefault(t => t.Track.Type == Jellyfin.Plugin.Babelfin.Engine.Classification.TrackType.Audio && t.Track.Index == record.JellyfinAudioIndex);
                var shows = record.JellyfinSubtitleIndex is int js && js >= 0
                    ? tracks.FirstOrDefault(t => t.Track.Type == Jellyfin.Plugin.Babelfin.Engine.Classification.TrackType.Subtitle && t.Track.Index == js)
                    : null;
                return new ChoiceDto(
                    s.Id,
                    record.Outcome.ToString(),
                    plays?.Track.Index,
                    plays?.Language?.Code,
                    plays?.Language is null ? null : languages.EnglishName(plays.Language),
                    shows?.Track.Index ?? -1,
                    shows?.Language?.Code,
                    shows?.Language is null ? null : languages.EnglishName(shows.Language),
                    shows is null ? null : Jellyfin.Plugin.Babelfin.Engine.Selection.TrackDescriptor.KindOf(shows),
                    "Audio: the video's default. Subtitles: the video's default.",
                    item.Id.ToString("N"));
            }

            var audio = engine?.Tracks.FirstOrDefault(t => t.Track.Index == engine.AudioIndex);
            var subtitle = engine?.Tracks.FirstOrDefault(t => t.Track.Index == engine.SubtitleIndex);
            return new ChoiceDto(
                s.Id,
                record?.Outcome.ToString() ?? "None",
                engine?.AudioIndex,
                audio?.Language?.Code,
                audio?.Language is null ? null : languages.EnglishName(audio.Language),
                engine?.SubtitleIndex,
                subtitle?.Language?.Code,
                subtitle?.Language is null ? null : languages.EnglishName(subtitle.Language),
                subtitle is null ? null : Jellyfin.Plugin.Babelfin.Engine.Selection.TrackDescriptor.KindOf(subtitle),
                record?.Decision?.Explanation,
                item.Id.ToString("N"));
        }).ToArray();
        return Ok(result);
    }

    /// <summary>
    /// Gets Babelfin's status for administrators.
    /// </summary>
    /// <returns>Counters and settings.</returns>
    [HttpGet("Admin/Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces("application/json")]
    public ActionResult AdminStatus()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            version = Plugin.VersionString,
            wrapped = _stats.Wrapped,
            innerType = _stats.InnerTypeName,
            staticCalls = _stats.StaticCalls,
            playbackCalls = _stats.PlaybackCalls,
            setDefaultsCalls = _stats.SetDefaultsCalls,
            applied = _stats.Applied,
            dryRunDecisions = _stats.DryRun,
            passedThrough = _stats.PassedThrough,
            errors = _stats.Errors,
            decisionsCached = _stats.DecisionCount,
            enabled = config?.Enabled ?? false,
            dryRun = config?.DryRun ?? false,
            unknownAppMode = (config?.UnknownAppMode ?? AppModeKind.Trust).ToString(),
            appModes = config?.AppModes.Count ?? 0,
            correctAfterStart = (config?.CorrectAfterStart ?? CorrectAfterStartMode.Auto).ToString(),
            compatibilityMode = config?.CompatibilityMode ?? false,
            dataFolder = _store.DataFolder,
            personFiles = _store.UserIds().Count,
            libraryFactsComputedAt = _facts.ComputedAt,
            baseUrl = _host.BaseUrl,
            hostsWebClient = _configuration.GetValue<bool>("hostwebclient"),
            hostSettingsRead = _host.Resolved,
        });
    }
}
