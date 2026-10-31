using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Seeding;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>
/// The signed-in person's own Babelfin settings, under {BaseUrl}/Babelfin/Me. Every endpoint acts on the person
/// named by the request's user-id claim only; every item id is checked with <c>GetItemById(id, user)</c> as that person.
/// No answer contains a file path.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item><c>PUT Me/Settings</c> checks every id the body names: override and burned-in item ids, collection-order ids and library-layer ids
/// (libraries are items too). Smart-rule condition values are not access-checked: they are only compared against the item being played,
/// which the person can already see.</item>
/// <item>Remembered manual changes (<see cref="ItemOverride.Manual"/>) are never taken from a body; the stored ones are kept for the same ids,
/// and memory survives only for the audio or subtitle settings that did not change. A stored memory-only entry survives when the body leaves it out.</item>
/// <item>Burned-in flags from a person are stored with <see cref="BurnedInFlag.SetBy"/> = the person; the languages are stored as canonical codes
/// when set through <c>Me/BurnedIn</c>.</item>
/// <item><c>Me/Overrides</c> with scope library is a 400 (library settings live in <c>Me/Settings</c>'s libraries list); a collection override for an
/// episode or season also accepts a collection that holds its series. <c>PUT</c> counts only entries with explicit settings against the cap of
/// <see cref="SettingsValidator.MaxOverrides"/> (remembered track changes are not capped), so a person whose remembered changes fill it can still save one.
/// <c>DELETE</c> removes an entry stored under exactly the id and scope named, by that id alone and with no item lookup: the overrides list shows entries of
/// items that are gone or hidden, of collections (their rows carry no <c>collectionId</c>) and of a type the scope no longer fits. Only when nothing is stored
/// under that id and scope is the item looked up and the scope resolved from it, as for <c>PUT</c> (an item that can't be found, with nothing stored, is a 404; one that is found with nothing stored is a 204).
/// Either way the whole entry goes (with <c>manual=true</c> only the remembered track change), read and written as one step under the store's lock, so it can't
/// take back what a "remove all" or a recorded track change stored meanwhile; the activity entry names the item as the person sees it, else as stored, else "a title".</item>
/// <item><c>GET Me/Overrides</c> lists every stored override of every scope in the order stored (the items are looked up as the person, in batches; one that is
/// gone keeps its stored name and has no type or library; a library the person can't see is never named). An entry with no usable item id gets no row.
/// <c>DELETE Me/Overrides/All</c> removes them all, explicit settings and remembered track changes, and writes one activity
/// entry only when something was removed.</item>
/// <item><c>GET Me/Overrides/Target/{itemId}</c> answers for movies, videos, shows, seasons and episodes (400 for any other type, or a season or episode with no
/// show). <c>Inherited</c> resolves the target's item context from the person's document minus the entry stored under the target id, so the admin's title defaults
/// and everything else still apply, and a show is resolved as its episodes are played (the content type "episode"); <c>HasNarrower</c> looks only at the person's
/// season and title overrides, in batched queries.</item>
/// <item><c>PUT Me/Settings?keepOverrides=true</c> keeps the stored overrides exactly (remembered track changes and times too) and does not validate or access-check
/// the body's; without it nothing changes.</item>
/// <item><c>PUT Me/Settings</c>, <c>POST Me/Import</c> and <c>PUT Me/Overrides/{itemId}</c> merge into (or set the override on) the document the store holds when its
/// lock is taken (<see cref="IPersonStore.Update"/>), not a copy read earlier, so an override removed, a "remove all" or a track change recorded meanwhile is neither undone nor
/// lost. The change is pure; the item and id checks (library lookups), the activity entry, the remember flags and the resolver's cache are dealt with outside the lock.</item>
/// <item><c>POST Me/Reset</c> seeds afresh from Jellyfin (the new-user template is not re-applied) and keeps the admin's locks. It also keeps the record
/// of Jellyfin settings Babelfin changed. The fresh document has "handle next-episode tracks" on, its default, so the remember flags stay off (or are
/// turned off when they were on).</item>
/// <item>a save that turns "let Babelfin handle next-episode tracks" on applies it (<see cref="IRememberFlags.ApplyAsync"/>); one that turns it off restores
/// the person's Jellyfin flags. A save that leaves the switch on while no record exists (an earlier apply failed) applies again. Every save writes a
/// <c>settings-changed</c> activity entry for the person (no paths; item names only for items the person can see).</item>
/// <item><c>DELETE Me/Overrides/{itemId}?manual=true</c> removes only the remembered track change; the entry goes when no explicit setting is left. Without
/// <c>manual=true</c> the whole entry goes, as before.</item>
/// <item><c>GET Me/Export</c> is the person's document without the admin's locks, the seed facts, the record of Jellyfin changes and the remembered track
/// changes (entries holding only a remembered change are left out). <c>POST Me/Import</c> runs the same preparation, validation and access checks as
/// <c>PUT Me/Settings</c>; the locks, seed, record of Jellyfin changes and remembered changes are never taken from the body (an export from another server
/// whose ids don't exist here is a 400 listing them, as for a PUT).</item>
/// <item><c>GET Me/Activity</c> returns the person's own entries, newest first: 50 by default, at most 200.</item>
/// </list>
/// </remarks>
[ApiController]
[StorageApiErrors]
[Route("Babelfin/Me")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public class MeController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IPersonStore _store;
    private readonly IProfileResolver _resolver;
    private readonly ILibraryFacts _facts;
    private readonly IItemContextBuilder _contextBuilder;
    private readonly IServerConfigurationManager? _serverConfiguration;
    private readonly IActivityLog? _activity;
    private readonly IRememberFlags? _rememberFlags;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="MeController"/> class.</summary>
    /// <param name="libraryManager">Library manager (access checks, libraries).</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="collectionManager">Collection manager (collection overrides).</param>
    /// <param name="store">The per-person store.</param>
    /// <param name="resolver">The profile resolver.</param>
    /// <param name="facts">Library facts.</param>
    /// <param name="contextBuilder">Builds the item context (the library and the rules' facts) for <c>Me/Overrides/Target</c>.</param>
    /// <param name="serverConfiguration">Server configuration (the metadata language used when seeding), when available.</param>
    /// <param name="activity">Babelfin's activity log, when available (test hosts may leave it out).</param>
    /// <param name="rememberFlags">The person's remember-flags switch, when available (test hosts may leave it out).</param>
    /// <param name="logger">Jellyfin logger.</param>
    public MeController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ICollectionManager collectionManager,
        IPersonStore store,
        IProfileResolver resolver,
        ILibraryFacts facts,
        IItemContextBuilder contextBuilder,
        IServerConfigurationManager? serverConfiguration = null,
        IActivityLog? activity = null,
        IRememberFlags? rememberFlags = null,
        ILogger<MeController>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _activity = activity;
        _rememberFlags = rememberFlags;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _collectionManager = collectionManager;
        _store = store;
        _resolver = resolver;
        _facts = facts;
        _contextBuilder = contextBuilder;
        _serverConfiguration = serverConfiguration;
    }

    /// <summary>Gets the person's settings, what they inherit, the libraries they can see and the languages Babelfin assumed.</summary>
    /// <returns>The settings view.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<MeSettingsResponse> GetSettings()
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(BuildView(user));
    }

    /// <summary>Saves the person's own settings (the admin's locks and the seed facts are kept from storage).</summary>
    /// <param name="body">The flat settings body and optional expected field values.</param>
    /// <param name="keepOverrides">True to keep the stored overrides (with their remembered track changes) and ignore the body's: a page that doesn't edit overrides saves with this, so it never brings back a removed override or drops one added meanwhile.</param>
    /// <returns>The settings view after saving.</returns>
    /// <param name="fields">Optional comma-separated editable leaf paths; omitted keeps the legacy full-save contract.</param>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<MeSettingsResponse>> PutSettingsRequest([FromBody] JsonElement body, [FromQuery] bool keepOverrides = false, [FromQuery] string? fields = null)
        => SettingsUpdateRequest.TryRead<PersonSettings>(body, fields, out var settings, out var expected, out var error, ControllerContext.HttpContext)
            ? SaveBodyAsync(settings!, import: false, keepOverrides, fields, expected, body)
            : Task.FromResult<ActionResult<MeSettingsResponse>>(BadRequest(error));

    /// <summary>Saves an already decoded person's document.</summary>
    [NonAction]
    public Task<ActionResult<MeSettingsResponse>> PutSettings(PersonSettings settings, bool keepOverrides = false, string? fields = null)
        => SaveBodyAsync(settings, import: false, keepOverrides, fields);

    /// <summary>Imports a settings file the person exported (their own document): checked exactly like <c>PUT Me/Settings</c>; the admin's locks,
    /// the seed facts, the record of Jellyfin changes and remembered track changes are never taken from the file.</summary>
    /// <param name="settings">The file's content.</param>
    /// <returns>The settings view after saving.</returns>
    [HttpPost("Import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<MeSettingsResponse>> ImportFile([FromBody] JsonElement settings)
        => ImportFileFormat.TryRead<PersonSettings>(settings, personal: true, out var decoded, out var error)
            ? Import(decoded!)
            : Task.FromResult<ActionResult<MeSettingsResponse>>(BadRequest(error));

    /// <summary>Imports an already decoded personal file; HTTP decoding is independent of the host's JSON convention.</summary>
    [NonAction]
    public Task<ActionResult<MeSettingsResponse>> Import(PersonSettings settings) => SaveBodyAsync(settings, import: true);

    /// <summary>Downloads the person's own settings as a file (without the admin's locks, the seed facts, the record of Jellyfin changes and remembered track changes).</summary>
    /// <returns>The file.</returns>
    [HttpGet("Export")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult Export()
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var copy = ForPerson(PersonDocuments.ForExport(GetOrSeed(user)));
        copy.UserName = user.Username;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(copy, BabelfinJson.Options);
        return File(bytes, "application/json", "babelfin-my-settings-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json");
    }

    /// <summary>Gets the person's own activity entries, newest first.</summary>
    /// <param name="limit">At most this many (default 50, at most 200).</param>
    /// <returns>The entries.</returns>
    [HttpGet("Activity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<ActivityEntry>> GetActivity([FromQuery] int? limit)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var count = Math.Clamp(limit ?? 50, 1, 200);
        IReadOnlyList<ActivityEntry> entries = _activity?.Query(count, null, user.Id) ?? Array.Empty<ActivityEntry>();
        return Ok(entries);
    }

    /// <summary>Reports a bounded page of missing item references without changing overrides, burned-in flags or recovery files.</summary>
    /// <param name="after">The previous report's continuation id, or null for the first page.</param>
    /// <returns>Report-only counts, readiness or skipped reason, and the next continuation id, if any.</returns>
    [HttpPost("Overrides/Prune")]
    public ActionResult PruneOverrides([FromQuery] Guid? after = null)
    {
        var user = CurrentUser();
        if (user is null) return Unauthorized();
        var report = OverridePruner.Prune(_store, _libraryManager, user.Id, after,
            HttpContext?.RequestAborted ?? default, services: HttpContext?.RequestServices);
        return Ok(report);
    }

    /// <summary>Resets the person's settings to a fresh seed from their Jellyfin preferences (the admin's locks are kept).</summary>
    /// <returns>The new document.</returns>
    [HttpPost("Reset")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PersonSettings>> Reset()
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var observed = _store.TryGet(user.Id);
        var fresh = PersonDocuments.Seed(user, _serverConfiguration);
        PersonSettings? stored = null;
        Update(user, current =>
        {
            PersonStorageAccess.RequireSnapshot(observed, current);
            stored = current;
            fresh.Locks = current?.Locks?.Clone() ?? new SettingsLayer();
            fresh.JellyfinChanges = current?.JellyfinChanges?.Clone();
            return fresh;
        });
        Record(user, "You reset your Babelfin settings");
        await PersonDocuments.SyncRememberFlagsAsync(_rememberFlags, user, stored, fresh).ConfigureAwait(false);
        return Ok(ForPerson(_store.TryGet(user.Id) ?? fresh));
    }

    /// <summary>Creates or replaces the person's explicit settings for a title, its season, its series or one of its collections.</summary>
    /// <param name="itemId">The item the person is looking at.</param>
    /// <param name="layer">The settings.</param>
    /// <param name="scope">title (default), season, series or collection.</param>
    /// <param name="collectionId">The collection, for scope collection.</param>
    /// <returns>The stored override.</returns>
    [HttpPut("Overrides/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ItemOverride> PutOverride([FromRoute] Guid itemId, [FromBody] SettingsLayer layer, [FromQuery] OverrideScope? scope, [FromQuery] Guid? collectionId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var effectiveScope = scope ?? OverrideScope.Title;
        if (effectiveScope == OverrideScope.Library)
        {
            return BadRequest(new ApiProblem("Library settings are saved with Me/Settings (libraries), not as an override.", Array.Empty<ValidationError>()));
        }

        var target = ResolveTarget(user, itemId, effectiveScope, collectionId);
        if (target.Error is not null)
        {
            return target.Error;
        }

        var errors = SettingsValidator.ValidateLayer(layer, EngineServices.Current.Languages);
        if (errors.Count > 0)
        {
            return BadRequest(new ApiProblem("The settings are not valid.", errors));
        }

        // Changed on the stored document as it is when the store's lock is taken, not on a copy read before: a removal, a "remove all" or a recorded track change
        // landing in between is neither undone nor lost. The document exists first (seeded when the person has none); it is the fallback only if the file is gone
        // by the time the update runs.
        var seeded = GetOrSeed(user);
        ItemOverride? stored = null;

        // Only explicit settings count against the cap (WithOverride refuses with null): remembered track changes are not capped, so they must not use it up.
        var saved = Update(user, current =>
        {
            PersonStorageAccess.RequireSnapshot(seeded, current);
            return PersonDocuments.WithOverride(current ?? seeded, target.Id, effectiveScope, target.Name, layer, DateTime.UtcNow, out stored);
        });
        if (!saved)
        {
            return BadRequest(new ApiProblem($"At most {SettingsValidator.MaxOverrides} overrides are allowed.", Array.Empty<ValidationError>()));
        }

        Record(user, $"You changed your settings for {target.Name}", target.Id, Scope(effectiveScope));
        return Ok(stored);
    }

    /// <summary>Removes the person's override for a title, its season, its series or one of its collections.</summary>
    /// <param name="itemId">The item the person is looking at.</param>
    /// <param name="scope">title (default), season, series or collection.</param>
    /// <param name="collectionId">The collection, for scope collection.</param>
    /// <param name="manual">True to remove only the remembered track change (explicit settings stay).</param>
    /// <returns>No content.</returns>
    [HttpDelete("Overrides/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteOverride([FromRoute] Guid itemId, [FromQuery] OverrideScope? scope, [FromQuery] Guid? collectionId, [FromQuery] bool manual = false)
    {
        if (itemId == Guid.Empty)
            return BadRequest(new ApiProblem("An item id is required. To remove all overrides, use DELETE Me/Overrides/All.", Array.Empty<ValidationError>()));
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var effectiveScope = scope ?? OverrideScope.Title;
        if (effectiveScope == OverrideScope.Library)
        {
            return BadRequest(new ApiProblem("Library settings are saved with Me/Settings (libraries), not as an override.", Array.Empty<ValidationError>()));
        }

        Target target;
        var stored = _store.TryGet(user.Id);
        if (stored?.FindOverride(itemId) is { } own && own.Scope == effectiveScope)
        {
            // What the list shows is removed by the id it is stored under, with no item lookup: a collection row (the page sends no collectionId),
            // an item that is gone or hidden and an entry whose scope doesn't fit the item's type all work.
            target = new Target(itemId, CurrentName(user, itemId), null);
        }
        else
        {
            target = ResolveTarget(user, itemId, effectiveScope, collectionId);
            if (target.Error is NotFoundResult && effectiveScope == OverrideScope.Collection && collectionId is { } held && stored?.FindOverride(held) is { Scope: OverrideScope.Collection })
            {
                // The item is gone (or the person can't see it any more): what they stored for its collection can still be removed, by its own id.
                target = new Target(held, CurrentName(user, held), null);
            }
        }

        if (target.Error is not null)
        {
            return target.Error;
        }

        // Read again under the store's lock, so a "remove all" or a recorded track change meeting this call can't be overwritten by it.
        ItemOverride? found = null;
        var removed = Update(user, current =>
        {
            PersonStorageAccess.RequireSnapshot(stored, current);
            found = current?.FindOverride(target.Id);
            if (current is null || found is null || (manual && found.Manual is null))
            {
                return null;
            }

            var doc = current.Clone();
            if (manual)
            {
                PersonDocuments.RemoveManual(doc, target.Id, DateTime.UtcNow);
            }
            else
            {
                doc.Overrides.RemoveAll(o => PersonSettings.SameId(o.ItemId, target.Id));
            }

            return doc;
        });
        if (removed)
        {
            var name = OverrideSummaries.ActivityNameOf(target.Name, found?.Name);
            Record(user, manual ? $"You removed a remembered track change for {name}" : $"You removed your settings for {name}", target.Id, Scope(effectiveScope));
        }

        return NoContent();
    }

    /// <summary>Lists the person's show and movie overrides (explicit settings and remembered track changes), with their items' current names and libraries.</summary>
    /// <returns>The overrides, in the order they are stored.</returns>
    [HttpGet("Overrides")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<OverrideSummary>> GetOverrides()
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        PersonStorageAccess.Require(_store, user.Id, writing: false);
        var settings = _store.TryGet(user.Id);
        return Ok(LibraryQueries.Read(() => OverrideSummaries.Build(_libraryManager, user, settings)));
    }

    /// <summary>Removes every override of the person, explicit settings and remembered track changes alike.</summary>
    /// <returns>No content.</returns>
    [HttpDelete("Overrides/All")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult DeleteOverrides()
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        if (Update(user, PersonDocuments.WithoutOverrides))
        {
            Record(user, "You removed all your show and movie overrides");
        }

        return NoContent();
    }

    /// <summary>
    /// Gets what the "Audio and subtitles (Babelfin)" dialog needs for an item: the movie, video or show an override for it is stored under (a season or an
    /// episode stands for its show), its library, the stored override, what applies without that override, and whether a season or episode override sits under the show.
    /// </summary>
    /// <param name="itemId">The item the person opened the menu for.</param>
    /// <returns>The target.</returns>
    [HttpGet("Overrides/Target/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OverrideTarget> GetOverrideTarget([FromRoute] Guid itemId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var item = itemId == Guid.Empty ? null : LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(itemId, user));
        if (item is null)
        {
            return NotFound();
        }

        var kind = item.GetBaseItemKind();
        var seriesId = item switch
        {
            Episode episode => episode.SeriesId,
            Season season => season.SeriesId,
            _ => Guid.Empty,
        };
        if (OverrideTargets.For(kind, item.Id, seriesId) is not { } key)
        {
            var message = OverrideTargets.IsSupported(kind) ? "This item has no series." : "Overrides are for shows and movies only.";
            return BadRequest(new ApiProblem(message, Array.Empty<ValidationError>()));
        }

        var target = key.Id == item.Id ? item : LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(key.Id, user));
        if (target is null)
        {
            return NotFound();
        }

        // What applies without the person's override on the target: the same resolution as for playback, from their document minus that entry.
        var stored = GetOrSeed(user);
        var context = LibraryQueries.Read(() => _contextBuilder.Build(target, user, ClaimsHelper.GetClient(User)));
        var admin = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var inherited = OverrideTargets.Inherited(stored, admin, context, key, EngineServices.Current.Languages);
        var lookups = new PersonLookups(_libraryManager, user);
        var hasNarrower = key.Scope == OverrideScope.Series
            && LibraryQueries.Read(() => OverrideTargets.HasNarrower(stored.Overrides, key.Id, ids => OverrideTargets.AnyInShow(lookups, ids, key.Id)));

        // Only a library the person can see is named, even for an item that also sits in one they can't.
        var (libraryIds, libraryNames) = LibraryQueries.Read(() => lookups.LibrariesOf(target));
        return Ok(new OverrideTarget(
            key.Id.ToString("N"),
            key.Scope,
            target.Name ?? string.Empty,
            target.GetBaseItemKind().ToString(),
            libraryIds.Length > 0 ? libraryIds[0].ToString("N") : null,
            libraryNames.Length > 0 ? libraryNames[0] : null,
            stored.FindOverride(key.Id)?.Clone(),
            inherited,
            hasNarrower));
    }

    /// <summary>Flags burned-in subtitles on a title, its season, its series or one of its collections (for this person only, until an admin approves).</summary>
    /// <param name="itemId">The item the person is looking at.</param>
    /// <param name="request">The languages and scope.</param>
    /// <param name="collectionId">The collection, for scope collection (the body's collectionId also works).</param>
    /// <returns>The stored flag.</returns>
    [HttpPost("BurnedIn/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BurnedInFlag> PostBurnedIn([FromRoute] Guid itemId, [FromBody] BurnedInRequest request, [FromQuery] Guid? collectionId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        if (request is null)
        {
            return BadRequest(new ApiProblem("The languages are missing.", Array.Empty<ValidationError>()));
        }

        if (request.Scope == OverrideScope.Library)
        {
            return LibraryScopeForbidden();
        }

        var target = ResolveTarget(user, itemId, request.Scope, collectionId ?? request.CollectionId);
        if (target.Error is not null)
        {
            return target.Error;
        }

        var languages = EngineServices.Current.Languages;
        var errors = SettingsValidator.ValidateLanguages(request.Languages, languages);
        if (errors.Count > 0)
        {
            return BadRequest(new ApiProblem("The languages are not valid.", errors));
        }

        var codes = request.Languages
            .Select(l => languages.Resolve(l)!.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seeded = GetOrSeed(user);
        BurnedInFlag? flag = null;
        if (!Update(user, current =>
        {
            PersonStorageAccess.RequireSnapshot(seeded, current);
            var doc = (current ?? seeded).Clone();
            flag = doc.BurnedIn.FirstOrDefault(f => PersonSettings.SameId(f.ItemId, target.Id) && f.Scope == request.Scope);
            if (flag is null)
            {
                if (doc.BurnedIn.Count >= SettingsValidator.MaxBurnedIn)
                {
                    return null;
                }

                flag = new BurnedInFlag { ItemId = target.Id.ToString("N"), Scope = request.Scope };
                doc.BurnedIn.Add(flag);
            }

            flag.Name = target.Name;
            flag.Languages = codes;
            flag.SetAt = DateTime.UtcNow;
            flag.SetBy = user.Id.ToString("N");
            return doc;
        }))
        {
            return BadRequest(new ApiProblem($"At most {SettingsValidator.MaxBurnedIn} burned-in flags are allowed.", Array.Empty<ValidationError>()));
        }
        Record(user, $"You flagged burned-in subtitles on {target.Name}", target.Id, Scope(request.Scope));
        return Ok(flag);
    }

    /// <summary>Removes the person's burned-in flag on a title, its season, its series or one of its collections.</summary>
    /// <param name="itemId">The item the person is looking at.</param>
    /// <param name="scope">title (default), season, series or collection.</param>
    /// <param name="collectionId">The collection, for scope collection.</param>
    /// <returns>No content.</returns>
    [HttpDelete("BurnedIn/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteBurnedIn([FromRoute] Guid itemId, [FromQuery] OverrideScope? scope, [FromQuery] Guid? collectionId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var effectiveScope = scope ?? OverrideScope.Title;
        if (effectiveScope == OverrideScope.Library)
        {
            return LibraryScopeForbidden();
        }

        var target = ResolveTarget(user, itemId, effectiveScope, collectionId);
        if (target.Error is not null)
        {
            return target.Error;
        }

        if (Update(user, current =>
        {
            if (current is null || !current.BurnedIn.Any(f => PersonSettings.SameId(f.ItemId, target.Id) && f.Scope == effectiveScope))
            {
                return null;
            }

            var doc = current.Clone();
            doc.BurnedIn.RemoveAll(f => PersonSettings.SameId(f.ItemId, target.Id) && f.Scope == effectiveScope);
            return doc;
        }))
        {
            Record(user, $"You removed the burned-in subtitles flag on {target.Name}", target.Id, Scope(effectiveScope));
        }

        return NoContent();
    }

    /// <summary>Gets the person's resolved settings for one item, with where each field came from.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The resolved profile.</returns>
    [HttpGet("Effective")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<EffectiveProfileDto> GetEffective([FromQuery] Guid itemId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var item = itemId == Guid.Empty ? null : LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(itemId, user));
        if (item is null)
        {
            return NotFound();
        }

        PersonStorageAccess.Require(_store, user.Id, writing: false);
        var result = _resolver.Resolve(user, item, ClaimsHelper.GetClient(User));
        return Ok(EffectiveProfileDto.From(result));
    }

    /// <summary>Gets the libraries the person can see, with Babelfin's view of each (no folder locations).</summary>
    /// <returns>The libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<LibraryInfoDto>> GetLibraries()
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(PersonDocuments.VisibleLibraries(_libraryManager, _facts, user));
    }

    private JellyfinUser? CurrentUser()
    {
        var userId = ClaimsHelper.GetUserId(User);
        return userId is null ? null : _userManager.GetUserById(userId.Value);
    }

    private PersonSettings GetOrSeed(JellyfinUser user)
    {
        PersonStorageAccess.Require(_store, user.Id, writing: false);
        return _store.GetOrCreate(user.Id, () => PersonDocuments.Seed(user, _serverConfiguration));
    }

    private bool Update(JellyfinUser user, Func<PersonSettings?, PersonSettings?> change)
    {
        var saved = PersonStorageAccess.Update(_store, user.Id, change);
        if (saved)
        {
            _resolver.Invalidate(user.Id);
        }

        return saved;
    }

    private string? CurrentName(JellyfinUser user, Guid itemId)
        => itemId == Guid.Empty ? null : LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(itemId, user))?.Name;

    private static Dictionary<string, string> Scope(OverrideScope scope)
        => new() { ["scope"] = scope.ToString().ToLowerInvariant() };

    private void Record(JellyfinUser user, string text, Guid? itemId = null, IReadOnlyDictionary<string, string>? details = null)
    {
        _activity?.Add(new ActivityEntry(DateTime.UtcNow, ActivityKinds.SettingsChanged, user.Id, itemId, text, details));
        _logger.LogInformation("[Babelfin] Settings changed for {Person}: {Text}", user.Id, text);
    }

    private async Task<ActionResult<MeSettingsResponse>> SaveBodyAsync(PersonSettings settings, bool import, bool keepOverrides = false, string? fields = null, JsonElement? expectedValues = null, JsonElement? requestBody = null)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        PersonStorageAccess.Require(_store, user.Id, writing: true);

        if (settings is null)
        {
            return BadRequest(new ApiProblem(import ? "The file is empty." : "The settings are missing.", Array.Empty<ValidationError>()));
        }

        if (fields is null && ControllerContext.HttpContext?.Request.Query.ContainsKey("fields") == true) fields = string.Empty;
        if (!SettingsIntent.TryParse(fields, admin: false, server: false, out var intent, out var intentError))
            return BadRequest(intentError);
        intent?.ReadExpected(ControllerContext.HttpContext?.Request, expectedValues);
        var intentSnapshot = intent is null ? null : GetOrSeed(user);
        if (intent is not null)
        {
            settings = intent.Apply(intentSnapshot!, settings);
            keepOverrides = true; // Neither reference list is part of field intent; keep it exactly as stored.
        }

        if (!keepOverrides && settings.BurnedIn?.Any(b => b is not null && b.Scope == OverrideScope.Library) == true)
        {
            return LibraryScopeForbidden();
        }

        if (import)
        {
            var structureErrors = ImportStructure.Validate(settings);
            if (structureErrors.Count > 0)
                return BadRequest(new ApiProblem("The import file is not valid.", structureErrors));
        }

        SettingsValidator.PreparePerson(settings);
        if (import)
        {
            PersonDocuments.StripForImport(settings);
            if (settings.SchemaVersion < PersonSettings.CurrentSchemaVersion)
            {
                // The local archives stay server-owned; legacy fields from a personal import must never become active again.
                PersonStore.Migrate(settings);
            }
        }

        if (keepOverrides)
        {
            // This page saves neither overrides nor burned-in flags. Ignore both before validation and access checks.
            settings.Overrides = new List<ItemOverride>();
            settings.BurnedIn = new List<BurnedInFlag>();
        }

        var errors = intent is null ? SettingsValidator.ValidatePerson(settings, EngineServices.Current.Languages, includeLocks: false)
            : intent.Validate(settings, EngineServices.Current.Languages, includeLocks: false, intentSnapshot);
        if (errors.Count > 0)
        {
            return BadRequest(new ApiProblem("The settings are not valid.", errors));
        }

        // The ids already stored are not checked again, so the access check can use the copy read here; it is slow (library lookups) and stays outside the store's lock.
        var stored = intentSnapshot ?? GetOrSeed(user);
        var hidden = PersonDocuments.InaccessibleIds(_libraryManager, intent?.ReferencesToCheck(settings) ?? settings, user, stored);
        if (hidden.Count > 0)
        {
            return BadRequest(new ApiProblem("Some items can't be found.", Array.Empty<ValidationError>(), hidden));
        }

        // Merged into the document the store holds when its lock is taken, not into the copy read above: an override removed, a "remove all" or a recorded track
        // change landing in between is neither undone nor lost.
        MergeResult saved;
        try
        {
            saved = PersonDocuments.SaveMerged(_store, stored, settings, takeLocks: false, user, keepOverrides, intent, preserveAbsentLists: !import, requestBody: requestBody);
        }
        catch (SettingsConflictException conflict) when (IsAdminActor(conflict.Problem.LastChangedBy))
        {
            throw new SettingsConflictException(conflict.Problem with { LastChangedBy = "admin", LastChangedByName = null });
        }
        _resolver.Invalidate(user.Id);
        Record(user, import ? "Your Babelfin settings were imported" : "You changed your Babelfin settings");
        await PersonDocuments.SyncRememberFlagsAsync(_rememberFlags, user, saved.Before, saved.Merged).ConfigureAwait(false);
        return Ok(BuildView(user));
    }

    private MeSettingsResponse BuildView(JellyfinUser user)
    {
        var settings = ForPerson(GetOrSeed(user));
        var inherited = EffectiveProfileDto.From(_resolver.Resolve(user, ItemContext.None));
        var libraries = PersonDocuments.VisibleLibraries(_libraryManager, _facts, user);
        IReadOnlyList<AssumedLanguage> assumed = settings.Seed is { Confirmed: false } seed ? seed.Assumed : Array.Empty<AssumedLanguage>();
        var admin = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var defaults = TableDefaults.Build(settings, admin, libraries, _facts, EngineServices.Current.Languages);

        // The Advanced options' values when the person sets nothing: their own everyday fields left out, their languages kept.
        var bare = settings.Clone();
        bare.Everyday = new SettingsLayer { Understood = settings.Everyday?.Understood, Reads = settings.Everyday?.Reads };
        var advanced = EffectiveProfileDto.From(Jellyfin.Plugin.Babelfin.Resolution.ProfileResolver.Resolve(bare, admin, ItemContext.None, EngineServices.Current.Languages));
        return new MeSettingsResponse(settings, inherited, libraries, assumed, defaults, advanced);
    }

    private static PersonSettings ForPerson(PersonSettings source)
    {
        var copy = source.Clone();
        if (IsAdminActor(copy.LastChangedBy))
        {
            copy.LastChangedBy = "admin";
            copy.LastChangedByName = null;
        }
        return copy;
    }

    private static bool IsAdminActor(string? actor) => actor?.StartsWith("admin:", StringComparison.OrdinalIgnoreCase) == true;

    private ObjectResult LibraryScopeForbidden()
        => StatusCode(StatusCodes.Status403Forbidden, new ApiProblem("Only an admin can flag a whole library.", Array.Empty<ValidationError>()) { Code = "forbidden" });

    private Target ResolveTarget(JellyfinUser user, Guid itemId, OverrideScope scope, Guid? collectionId)
    {
        var item = itemId == Guid.Empty ? null : LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(itemId, user));
        if (item is null)
        {
            return new Target(Guid.Empty, null, NotFound());
        }

        Guid targetId;
        switch (scope)
        {
            case OverrideScope.Title:
                return new Target(item.Id, item.Name, null);
            case OverrideScope.Season:
                targetId = item switch
                {
                    Episode episode => episode.SeasonId,
                    Season => item.Id,
                    _ => Guid.Empty,
                };
                if (targetId == Guid.Empty)
                {
                    return new Target(Guid.Empty, null, BadRequest(new ApiProblem("This item has no season.", Array.Empty<ValidationError>())));
                }

                break;
            case OverrideScope.Series:
                targetId = item switch
                {
                    Episode episode => episode.SeriesId,
                    Season season => season.SeriesId,
                    Series => item.Id,
                    _ => Guid.Empty,
                };
                if (targetId == Guid.Empty)
                {
                    return new Target(Guid.Empty, null, BadRequest(new ApiProblem("This item has no series.", Array.Empty<ValidationError>())));
                }

                break;
            case OverrideScope.Collection:
                if (collectionId is not { } wanted || wanted == Guid.Empty)
                {
                    return new Target(Guid.Empty, null, BadRequest(new ApiProblem("Name the collection with collectionId.", Array.Empty<ValidationError>())));
                }

                if (!(item is BoxSet && item.Id == wanted) && !CollectionsHolding(user, item).Contains(wanted))
                {
                    return new Target(Guid.Empty, null, BadRequest(new ApiProblem("This item is not in that collection.", Array.Empty<ValidationError>())));
                }

                targetId = wanted;
                break;
            default:
                return new Target(Guid.Empty, null, LibraryScopeForbidden());
        }

        var targetItem = LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(targetId, user));
        return targetItem is null ? new Target(Guid.Empty, null, NotFound()) : new Target(targetItem.Id, targetItem.Name, null);
    }

    private HashSet<Guid> CollectionsHolding(JellyfinUser user, BaseItem item)
    {
        var ids = new HashSet<Guid>(_collectionManager.GetCollectionsContainingItem(user, item.Id).Select(c => c.Id));
        var seriesId = item switch
        {
            Episode episode => episode.SeriesId,
            Season season => season.SeriesId,
            _ => Guid.Empty,
        };
        if (seriesId != Guid.Empty)
        {
            ids.UnionWith(_collectionManager.GetCollectionsContainingItem(user, seriesId).Select(c => c.Id));
        }

        return ids;
    }

    private sealed record Target(Guid Id, string? Name, ActionResult? Error);
}

/// <summary>What <see cref="PersonDocuments.SaveMerged"/> did.</summary>
/// <param name="Before">The document the store held when the update ran (what the save replaced).</param>
/// <param name="Merged">The document that was stored.</param>
internal sealed record MergeResult(PersonSettings Before, PersonSettings Merged);

/// <summary>Helpers shared by the person and admin endpoints: seeding, merging a body into the stored document, access checks, library views.</summary>
internal static class PersonDocuments
{
    /// <summary>Seeds a document from the person's Jellyfin preferences (the same seed the resolver uses).</summary>
    /// <param name="user">The person.</param>
    /// <param name="serverConfiguration">The server configuration, when available.</param>
    /// <returns>The seeded document.</returns>
    internal static PersonSettings Seed(JellyfinUser user, IServerConfigurationManager? serverConfiguration)
        => PersonSeeder.Seed(SeedInputs.FromUser(user, serverConfiguration?.Configuration.PreferredMetadataLanguage), EngineServices.Current.Languages);

    /// <summary>
    /// Builds the document to store from the stored one and a prepared, validated body: the editable parts come from the body;
    /// the id, name, schema, seed facts and remembered manual changes from storage (and the locks too unless <paramref name="takeLocks"/>).
    /// With <paramref name="keepOverrides"/> the overrides and burned-in flags are the stored ones too, exactly as stored: the body's are not taken.
    /// </summary>
    /// <param name="stored">The stored document.</param>
    /// <param name="incoming">The body.</param>
    /// <param name="takeLocks">True when the admin edits the person (locks come from the body).</param>
    /// <param name="user">The person.</param>
    /// <param name="keepOverrides">True when a page saves its settings without touching overrides (<c>?keepOverrides=true</c>): the stored overrides, with their remembered track changes, are kept as they are.</param>
    /// <returns>The document to save.</returns>
    /// <param name="preserveAbsentLists">True for legacy PUT: omitted reference lists remain exactly as stored.</param>
    internal static PersonSettings Merge(PersonSettings stored, PersonSettings incoming, bool takeLocks, JellyfinUser user, bool keepOverrides = false, bool preserveAbsentLists = false, JsonElement? requestBody = null)
    {
        bool Has(string name) => requestBody is null || requestBody.Value.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var now = DateTime.UtcNow;
        // Server-owned fields, including retired snapshots and migration notes, stay on the stored clone.
        var result = stored.Clone();
        result.SchemaVersion = PersonSettings.CurrentSchemaVersion;
        result.UserId = user.Id;
        result.UserName = user.Username;
        if (Has("Enabled")) result.Enabled = incoming.Enabled;
        if (Has("UseEverydayEverywhere")) result.UseEverydayEverywhere = incoming.UseEverydayEverywhere;
        if (Has("PerLibrary")) result.PerLibrary = incoming.PerLibrary;
        if (Has("HandleNextEpisode")) result.HandleNextEpisode = incoming.HandleNextEpisode;
        if (Has("Everyday")) result.Everyday = incoming.Everyday.Clone();
        if (Has("Libraries")) result.Libraries = incoming.Libraries.Select(l => l.Clone()).ToList();
        if (Has("SmartRules")) result.SmartRules = incoming.SmartRules.Select(r => r.Clone()).ToList();
        if (Has("CollectionOrder")) result.CollectionOrder = incoming.CollectionOrder.ToList();
        if (takeLocks && Has("Locks"))
        {
            result.Locks = incoming.Locks.Clone();
        }

        if (!keepOverrides && (!preserveAbsentLists || incoming.OverridesPresent))
        {
            var overrides = new List<ItemOverride>();
            foreach (var o in incoming.Overrides)
            {
                var copy = o.Clone();
                var id = Guid.Parse(o.ItemId);
                copy.ItemId = id.ToString("N");
                var old = stored.FindOverride(id);
                copy.Manual = OverrideEdits.AfterSettingsChange(old?.Settings, copy.Settings, old?.Manual);
                copy.UpdatedAt = old is not null && SameJson(old.Settings, o.Settings) && old.Scope == o.Scope ? old.UpdatedAt : now;
                overrides.Add(copy);
            }

            foreach (var old in stored.Overrides.Where(o => o.Manual is not null))
            {
                if (Guid.TryParse(old.ItemId, out var id) && !overrides.Any(o => PersonSettings.SameId(o.ItemId, id)))
                {
                    var kept = old.Clone();
                    kept.Settings = new SettingsLayer();
                    kept.Manual = OverrideEdits.AfterSettingsChange(old.Settings, kept.Settings, old.Manual);
                    if (kept.Manual is not null)
                    {
                        overrides.Add(kept);
                    }
                }
            }

            result.Overrides = overrides;
        }

        result.BurnedIn = keepOverrides || (preserveAbsentLists && !incoming.BurnedInPresent) ? stored.BurnedIn.Select(b => b.Clone()).ToList() : incoming.BurnedIn.Select(b =>
        {
            var copy = b.Clone();
            var id = Guid.Parse(b.ItemId);
            copy.ItemId = id.ToString("N");
            var old = stored.BurnedIn.FirstOrDefault(x => PersonSettings.SameId(x.ItemId, id) && x.Scope == b.Scope);
            copy.SetAt = old?.SetAt ?? now;
            copy.SetBy = user.Id.ToString("N");
            return copy;
        }).ToList();

        result.Seed ??= new SeedRecord { SeededAt = now };
        if (incoming.Seed is { } seed)
        {
            result.Seed.Confirmed = seed.Confirmed;
        }

        return result;
    }

    /// <summary>
    /// Saves a prepared, validated body as one step under the store's lock (<see cref="IPersonStore.Update"/>): <see cref="Merge"/> runs on the document the store
    /// holds when the update runs, not on a copy read before the body was checked, so an override removed, a "remove all" or a track change recorded in between
    /// is neither undone nor lost by this save. The merge is pure (it reads the body and the stored document and calls nothing else); the activity entry, the
    /// remember flags and the resolver's cache are the caller's to deal with afterwards, outside the lock.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="readEarlier">The document read before the body was checked; used only when the person's document is gone when the update runs.</param>
    /// <param name="incoming">The body.</param>
    /// <param name="takeLocks">True when the admin edits the person (locks come from the body).</param>
    /// <param name="user">The person.</param>
    /// <param name="keepOverrides">True to keep the stored overrides exactly (see <see cref="Merge"/>).</param>
    /// <returns>The document the update replaced and the one it stored.</returns>
    /// <param name="intent">Optional explicit field intent applied to the latest document under the update lock.</param>
    /// <param name="preserveAbsentLists">True for legacy PUT: omitted reference lists remain exactly as stored.</param>
    internal static MergeResult SaveMerged(IPersonStore store, PersonSettings readEarlier, PersonSettings incoming, bool takeLocks, JellyfinUser user, bool keepOverrides, SettingsIntent? intent = null, bool preserveAbsentLists = false, JsonElement? requestBody = null)
    {
        var before = readEarlier;
        PersonSettings? merged = null;
        PersonStorageAccess.Update(store, user.Id, current =>
        {
            PersonStorageAccess.RequireSnapshot(readEarlier, current);
            before = current ?? readEarlier;
            intent?.CheckExpected(before);
            var body = intent?.Apply(before, incoming) ?? incoming;
            merged = Merge(before, body, takeLocks, user, keepOverrides || intent is not null, preserveAbsentLists, intent is null ? requestBody : null);
            if (intent is not null)
            {
                var errors = intent.Validate(merged, EngineServices.Current.Languages, takeLocks, before);
                if (errors.Count > 0) throw new SettingsValidationException(new ApiProblem("The settings are not valid.", errors));
            }
            // Preserve flags added, removed or changed after validation began. Dedicated flag endpoints are independent writers.
            foreach (var flag in readEarlier.BurnedIn.Concat(before.BurnedIn))
            {
                bool SameKey(BurnedInFlag b) => b.Scope == flag.Scope && (Guid.TryParse(flag.ItemId, out var id) ? PersonSettings.SameId(b.ItemId, id) : b.ItemId == flag.ItemId);
                var earlier = readEarlier.BurnedIn.FirstOrDefault(SameKey);
                var latest = before.BurnedIn.FirstOrDefault(SameKey);
                if (JsonSerializer.Serialize(earlier, BabelfinJson.Options) == JsonSerializer.Serialize(latest, BabelfinJson.Options)) continue;
                merged.BurnedIn.RemoveAll(b => SameKey(b));
                if (latest is not null) merged.BurnedIn.Add(latest.Clone());
            }
            if (merged.BurnedIn.Count > SettingsValidator.MaxBurnedIn && merged.BurnedIn.Count > before.BurnedIn.Count)
                throw new PersonStorageConflictException("Burned-in flags changed while these settings were being saved. Reload the settings and retry.", "burned-in-conflict");
            return merged;
        }, explicitly: true);
        return new MergeResult(before, merged ?? throw new InvalidOperationException("The store did not run the change"));
    }

    /// <summary>Applies the next-episode preference only while the server and person are enabled; disabling restores Jellyfin's flags.</summary>
    /// <returns>False when the flags service throws or a requested restoration leaves its undo record, so multi-person operations can report partial failure.</returns>
    internal static async Task<bool> SyncRememberFlagsAsync(IRememberFlags? flags, JellyfinUser user, PersonSettings? before, PersonSettings after, IPersonStore? store = null)
    {
        var wasOn = before is { Enabled: true, HandleNextEpisode: true };
        var recorded = before?.JellyfinChanges is not null;
        var isOn = Plugin.Instance?.Configuration.Enabled != false && after.Enabled && after.HandleNextEpisode;
        if (flags is null)
        {
            return true;
        }

        try
        {
            if (isOn && (!wasOn || !recorded))
            {
                await flags.ApplyAsync(user, CancellationToken.None).ConfigureAwait(false);
            }
            else if (!isOn && (wasOn || recorded))
            {
                await flags.RestoreAsync(user, CancellationToken.None).ConfigureAwait(false);
                // The production service logs failures and keeps the undo record instead of throwing.
                if (store?.TryGet(user.Id)?.JellyfinChanges is not null)
                {
                    return false;
                }
            }
        }
        catch (Exception)
        {
            // The service logs its own failures; a save never fails over Jellyfin's flags.
            return false;
        }

        return true;
    }

    /// <summary>
    /// The body of a copy from one person to another: the source's everyday layer, "everyday everywhere", libraries, smart rules, overrides' explicit
    /// settings and collection order; the target's own switches, burned-in flags and seed. Remembered changes and locks never travel.
    /// </summary>
    /// <param name="source">The source document.</param>
    /// <param name="target">The target's stored document.</param>
    /// <returns>A body for <see cref="Merge"/>.</returns>
    internal static PersonSettings ForCopy(PersonSettings source, PersonSettings target)
    {
        var body = source.Clone();
        body.UserId = target.UserId;
        body.UserName = target.UserName;
        body.Enabled = target.Enabled;
        body.HandleNextEpisode = target.HandleNextEpisode;
        body.JellyfinChanges = null;
        body.Seed = null;
        body.Locks = new SettingsLayer();
        body.BurnedIn = target.BurnedIn.Select(b => b.Clone()).ToList();
        body.Overrides = body.Overrides.Where(o => o.Settings is not null && !o.Settings.IsEmpty).ToList();
        foreach (var o in body.Overrides)
        {
            o.Manual = null;
        }

        return body;
    }

    /// <summary>Removes the overrides, collection-order entries and library layers a person can't see (or whose ids aren't ids) from a body.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="body">The prepared body (changed in place).</param>
    /// <param name="user">The person the check runs as.</param>
    /// <returns>The ids removed.</returns>
    internal static IReadOnlyList<string> DropInaccessible(ILibraryManager libraryManager, PersonSettings body, JellyfinUser user)
    {
        var cache = new Dictionary<Guid, bool>();
        var dropped = new List<string>();
        bool Visible(string? value)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            {
                return false;
            }

            if (!cache.TryGetValue(id, out var visible))
            {
                visible = LibraryQueries.Read(() => libraryManager.GetItemById<BaseItem>(id, user) is not null);
                cache[id] = visible;
            }

            return visible;
        }

        void Note(string? value)
        {
            if (value is not null && !dropped.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                dropped.Add(value);
            }
        }

        foreach (var o in body.Overrides.Where(o => !Visible(o.ItemId)).ToList())
        {
            Note(o.ItemId);
            body.Overrides.Remove(o);
        }

        foreach (var c in body.CollectionOrder.Where(c => !Visible(c)).ToList())
        {
            Note(c);
            body.CollectionOrder.Remove(c);
        }

        foreach (var l in body.Libraries.Where(l => !Visible(l.LibraryId)).ToList())
        {
            Note(l.LibraryId);
            body.Libraries.Remove(l);
        }

        return dropped;
    }

    /// <summary>A person's document as their own export shows it: no locks, no seed facts, no record of Jellyfin changes, no remembered track changes.</summary>
    /// <param name="stored">The stored document.</param>
    /// <returns>A copy for export.</returns>
    internal static PersonSettings ForExport(PersonSettings stored)
    {
        var copy = stored.Clone();
        copy.Locks = new SettingsLayer();
        copy.Seed = null;
        copy.JellyfinChanges = null;
        copy.Overrides = copy.Overrides.Where(o => o.Settings is not null && !o.Settings.IsEmpty).ToList();
        foreach (var o in copy.Overrides)
        {
            o.Manual = null;
        }

        return copy;
    }

    /// <summary>Clears what an imported body may never set: the admin's locks, the seed, the record of Jellyfin changes and remembered track changes.</summary>
    /// <param name="settings">The prepared body (changed in place).</param>
    internal static void StripForImport(PersonSettings settings)
    {
        settings.Locks = new SettingsLayer();
        settings.Seed = null;
        settings.JellyfinChanges = null;
        foreach (var o in settings.Overrides)
        {
            o.Manual = null;
        }
    }

    /// <summary>Removes the remembered track change from an override, and the override itself when no explicit setting is left.</summary>
    /// <param name="doc">The document (changed in place).</param>
    /// <param name="itemId">The override's id.</param>
    /// <param name="now">The time (UTC).</param>
    /// <returns>True when something was removed.</returns>
    internal static bool RemoveManual(PersonSettings doc, Guid itemId, DateTime now)
    {
        var entry = doc.FindOverride(itemId);
        if (entry?.Manual is null)
        {
            return false;
        }

        entry.Manual = null;
        if (entry.Settings is null || entry.Settings.IsEmpty)
        {
            doc.Overrides.Remove(entry);
        }
        else
        {
            entry.UpdatedAt = now;
        }

        return true;
    }

    /// <summary>
    /// A copy of the stored document with an explicit override set for one item: the entry stored under that id gets the settings, scope and name (a remembered
    /// track change on unchanged audio/subtitle fields stays), or one is added. Only entries with explicit settings count against <see cref="SettingsValidator.MaxOverrides"/>; remembered
    /// track changes are not capped, so they never use it up.
    /// </summary>
    /// <param name="stored">The stored document (not changed).</param>
    /// <param name="itemId">The id the override is stored under.</param>
    /// <param name="scope">The override's scope.</param>
    /// <param name="name">The item's name as the person sees it.</param>
    /// <param name="layer">The explicit settings (copied).</param>
    /// <param name="now">The time (UTC).</param>
    /// <param name="saved">The entry as it is in the copy, or null when the cap refused it.</param>
    /// <returns>The copy to save, or null when the person already has the most explicit overrides and this one would add another.</returns>
    internal static PersonSettings? WithOverride(PersonSettings stored, Guid itemId, OverrideScope scope, string? name, SettingsLayer layer, DateTime now, out ItemOverride? saved)
    {
        var doc = stored.Clone();
        var existing = doc.FindOverride(itemId);
        var addsExplicit = existing is null || (existing.Settings is not { IsEmpty: false } && !layer.IsEmpty);
        if (addsExplicit && doc.Overrides.Count(o => o.Settings is { IsEmpty: false }) >= SettingsValidator.MaxOverrides)
        {
            saved = null;
            return null;
        }

        if (existing is null)
        {
            existing = new ItemOverride { ItemId = itemId.ToString("N") };
            doc.Overrides.Add(existing);
        }

        existing.Scope = scope;
        existing.Name = name;
        existing.Manual = OverrideEdits.AfterSettingsChange(existing.Settings, layer, existing.Manual);
        existing.Settings = layer.Clone();
        existing.UpdatedAt = now;
        saved = existing;
        return doc;
    }

    /// <summary>A copy of the stored document without any override: no explicit settings and no remembered track changes.</summary>
    /// <param name="stored">The stored document, or null when the person has none.</param>
    /// <returns>The copy to save, or null when there was no override to remove.</returns>
    internal static PersonSettings? WithoutOverrides(PersonSettings? stored)
    {
        if (stored is null || stored.Overrides.Count == 0)
        {
            return null;
        }

        var copy = stored.Clone();
        copy.Overrides.Clear();
        return copy;
    }

    /// <summary>A copy of the stored document without one override (its explicit settings and its remembered track change both go).</summary>
    /// <param name="stored">The stored document, or null when the person has none.</param>
    /// <param name="itemId">The id the override is stored under.</param>
    /// <param name="removed">The override that was removed (the stored one), or null.</param>
    /// <returns>The copy to save, or null when the person has no override under that id.</returns>
    internal static PersonSettings? WithoutOverride(PersonSettings? stored, Guid itemId, out ItemOverride? removed)
    {
        removed = stored?.FindOverride(itemId);
        if (stored is null || removed is null)
        {
            return null;
        }

        var copy = stored.Clone();
        copy.Overrides.RemoveAll(o => PersonSettings.SameId(o.ItemId, itemId));
        return copy;
    }

    /// <summary>
    /// The ids a body names that the person can't see (or that aren't ids): overrides, burned-in flags, collection order, library layers.
    /// Ids the stored document already holds are not checked again, so an item deleted or hidden later never stops the person saving.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="settings">The prepared body.</param>
    /// <param name="user">The person the check runs as.</param>
    /// <param name="stored">The stored document, or null to check every id.</param>
    /// <returns>The failing ids, as sent.</returns>
    internal static IReadOnlyList<string> InaccessibleIds(ILibraryManager libraryManager, PersonSettings settings, JellyfinUser user, PersonSettings? stored = null)
    {
        var raw = settings.Overrides.Select(o => o.ItemId)
            .Concat(settings.BurnedIn.Select(b => b.ItemId))
            .Concat(settings.CollectionOrder)
            .Concat(settings.Libraries.Select(l => l.LibraryId));
        var failed = new List<string>();
        var checkedIds = new Dictionary<Guid, bool>();
        if (stored is not null)
        {
            foreach (var known in stored.Overrides.Select(o => o.ItemId)
                .Concat(stored.BurnedIn.Select(b => b.ItemId))
                .Concat(stored.CollectionOrder)
                .Concat(stored.Libraries.Select(l => l.LibraryId)))
            {
                if (Guid.TryParse(known, out var knownId) && knownId != Guid.Empty)
                {
                    checkedIds[knownId] = true;
                }
            }
        }

        foreach (var value in raw)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            {
                failed.Add(value);
                continue;
            }

            if (!checkedIds.TryGetValue(id, out var visible))
            {
                visible = LibraryQueries.Read(() => libraryManager.GetItemById<BaseItem>(id, user) is not null);
                checkedIds[id] = visible;
            }

            if (!visible && !failed.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                failed.Add(value);
            }
        }

        return failed;
    }

    /// <summary>The libraries a person can see (or every library when <paramref name="user"/> is null), with Babelfin's view of each.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="facts">Library facts.</param>
    /// <param name="user">The person, or null for all.</param>
    /// <returns>The libraries.</returns>
    internal static List<LibraryInfoDto> VisibleLibraries(ILibraryManager libraryManager, ILibraryFacts facts, JellyfinUser? user)
        => LibraryQueries.Read(() => ReadVisibleLibraries(libraryManager, facts, user));

    private static List<LibraryInfoDto> ReadVisibleLibraries(ILibraryManager libraryManager, ILibraryFacts facts, JellyfinUser? user)
    {
        var config = Plugin.Instance?.Configuration;
        var list = new List<LibraryInfoDto>();
        foreach (var folder in libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var id) || id == Guid.Empty)
            {
                continue;
            }

            if (user is not null && !CanSee(libraryManager, user, id))
            {
                continue;
            }

            var fact = facts.Get(id);
            var choice = config?.FindLibrary(id)?.OriginalAudio ?? OriginalAudioChoice.Auto;
            list.Add(new LibraryInfoDto(
                id.ToString("N"),
                folder.Name ?? string.Empty,
                folder.CollectionType?.ToString(),
                LibraryView.IsOriginalAudio(choice, fact, folder.Name, config?.Starter),
                fact?.UsualOriginalLanguage));
        }

        return list;
    }

    /// <summary>The ids of the libraries a person can see.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="user">The person.</param>
    /// <returns>The ids, in the form <see cref="VisibleLibraries"/> lists them and a library layer or an item's libraries use.</returns>
    internal static HashSet<Guid> VisibleLibraryIds(ILibraryManager libraryManager, JellyfinUser user)
        => LibraryQueries.Read(() => ReadVisibleLibraryIds(libraryManager, user));

    private static HashSet<Guid> ReadVisibleLibraryIds(ILibraryManager libraryManager, JellyfinUser user)
    {
        var ids = new HashSet<Guid>();
        foreach (var folder in libraryManager.GetVirtualFolders())
        {
            if (Guid.TryParse(folder.ItemId, out var id) && id != Guid.Empty && CanSee(libraryManager, user, id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static bool CanSee(ILibraryManager libraryManager, JellyfinUser user, Guid libraryId)
        => libraryManager.GetItemById<CollectionFolder>(libraryId) is { } library && library.IsVisibleStandalone(user);

    private static bool SameJson(SettingsLayer a, SettingsLayer b)
        => string.Equals(JsonSerializer.Serialize(a, BabelfinJson.Options), JsonSerializer.Serialize(b, BabelfinJson.Options), StringComparison.Ordinal);
}
