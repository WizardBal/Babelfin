using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>
/// The admin's Babelfin API, under {BaseUrl}/Babelfin/Admin. Requires elevation. Endpoints that act on one person check
/// item ids as that person.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item><c>PUT Admin/Settings</c> validates with a language table built from the body's own aliases (so a new alias can be used in the same save),
/// keeps <see cref="PluginConfiguration.ConfigVersion"/> current and drops the retired probe fields; <see cref="PluginConfiguration.LastUpgradeNote"/>
/// is taken from the body (sending it empty dismisses the note).</item>
/// <item><c>PUT Admin/Users/{userId}/Settings</c> takes the editable layers and the locks from the body; the id, name, seed facts (except "confirmed")
/// and remembered manual changes for unchanged audio/subtitle choices stay as stored, exactly as for the person's own save. A library-scope flag in a person's document is a 400:
/// whole-library flags belong in the admin configuration. The body is merged into the document the store holds when its lock is taken
/// (<see cref="IPersonStore.Update"/>), not a copy read earlier, so an override removed, a "remove all" or a track change recorded meanwhile is neither undone nor lost.</item>
/// <item>Approving a burned-in flag merges its languages into an existing shared flag for the same item and scope; the shared flag records the person
/// who set it in <see cref="BurnedInFlag.SetBy"/>. The person's own flag is kept (nothing is deleted). Without <c>scope</c>, the person's first flag on
/// the item is approved.</item>
/// <item><c>Admin/Libraries</c> lists every library (no visibility filter) with its computed facts; folder locations are not included.</item>
/// <item>The export carries each person's current Jellyfin name, so a file moved to another server can be matched by name.</item>
/// <item>every admin save writes a <c>settings-changed</c> activity entry: server-wide changes with no person, changes to one person's document for that
/// person (worded so it names no other person). <c>PUT Admin/Users/{userId}/Settings</c> applies or restores the person's Jellyfin remember flags when
/// "let Babelfin handle next-episode tracks" changes, like the person's own save. After an applied import, each imported person keeps the record of Jellyfin
/// changes this server had (a file never brings one in), and the remember flags follow the imported switch.</item>
/// <item>Turning compatibility mode off in <c>PUT Admin/Settings</c> (or through an applied <c>Admin/Import</c>) starts <see cref="ICompatibilityWriter.UndoAllAsync"/> in the background; the answer doesn't wait.</item>
/// <item><c>POST Admin/Users/{userId}/CopyTo/{targetUserId}</c> copies the everyday layer, "use my everyday settings everywhere", libraries, smart rules, the
/// explicit settings of overrides and the collection order. The target keeps their locks, remembered track changes, burned-in flags, seed, on/off switch and
/// "handle next-episode tracks". Entries naming items or libraries the target can't see are left out and listed in the answer; copying to oneself is a 400.</item>
/// <item><c>GET Admin/Users/{userId}/Overrides</c> lists the person's overrides like <c>GET Me/Overrides</c> (items looked up as that person; none for a person with no
/// file, and no file is created). <c>DELETE Admin/Users/{userId}/Overrides/All</c> removes them all and <c>DELETE Admin/Users/{userId}/Overrides/{itemId}</c> one, by the
/// stored id (the item need not exist; <c>scope</c> is accepted and not needed). Each answers 204 whether or not something was there, and writes an activity entry for
/// the person only when something was removed: "An admin removed your override for {name}" (the name as the person sees it, else the stored one, else "a title") and
/// "An admin removed all your show and movie overrides". Each reads and writes the person's document as one step under the store's lock
/// (<see cref="IPersonStore.Update"/>), so a track change recorded meanwhile isn't overwritten.</item>
/// <item><c>PUT Admin/Users/{userId}/Settings?keepOverrides=true</c> keeps the person's stored overrides exactly and does not validate or access-check the body's;
/// without it nothing changes.</item>
/// <item><c>GET Admin/Activity</c>: 100 entries by default, at most <see cref="ActivityLog.RingSize"/>. <c>GET Admin/Playback</c> is empty when no play registry is running.</item>
/// <item><c>POST Admin/ResetAll</c> first writes the <c>GET Admin/Export</c> document to <c>{DataPath}/babelfin/backups/babelfin-settings-before-reset-{UTC
/// yyyyMMdd-HHmmss}.json</c> (never over an existing file: <c>-2</c>, <c>-3</c>… are added); when that fails it answers 500 and changes nothing. Each person
/// whose Jellyfin user still exists then gets what a new person gets (their locks go too); the record of Jellyfin changes is kept and "handle next-episode
/// tracks" is off, so the remember flags Babelfin turned off are put back. Documents of users who no longer exist are left alone (the backup holds them),
/// and one person's failure is logged without stopping the others. Last, the admin settings become the shipped defaults, so turning compatibility mode off
/// puts back the remembered selections it wrote.</item>
/// </list>
/// </remarks>
[ApiController]
[StorageApiErrors]
[Route("Babelfin/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public class AdminController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPersonStore _store;
    private readonly ILibraryFacts _facts;
    private readonly IProfileResolver _resolver;
    private readonly IServerConfigurationManager? _serverConfiguration;
    private readonly IActivityLog? _activity;
    private readonly IRememberFlags? _rememberFlags;
    private readonly ICompatibilityWriter? _compatibilityWriter;
    private readonly IPlayRegistry? _playRegistry;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="AdminController"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="store">The per-person store.</param>
    /// <param name="facts">Library facts.</param>
    /// <param name="resolver">The profile resolver (its cache is dropped after a save).</param>
    /// <param name="serverConfiguration">Server configuration (the metadata language used when seeding), when available.</param>
    /// <param name="activity">Babelfin's activity log, when available.</param>
    /// <param name="rememberFlags">The people's remember-flags switch, when available.</param>
    /// <param name="compatibilityWriter">The compatibility-mode writer, when available.</param>
    /// <param name="playRegistry">The live play registry, when available.</param>
    /// <param name="logger">Logger, when available.</param>
    public AdminController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPersonStore store,
        ILibraryFacts facts,
        IProfileResolver resolver,
        IServerConfigurationManager? serverConfiguration = null,
        IActivityLog? activity = null,
        IRememberFlags? rememberFlags = null,
        ICompatibilityWriter? compatibilityWriter = null,
        IPlayRegistry? playRegistry = null,
        ILogger<AdminController>? logger = null)
    {
        _activity = activity;
        _rememberFlags = rememberFlags;
        _compatibilityWriter = compatibilityWriter;
        _playRegistry = playRegistry;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _store = store;
        _facts = facts;
        _resolver = resolver;
        _serverConfiguration = serverConfiguration;
    }

    /// <summary>Gets the admin configuration.</summary>
    /// <returns>The configuration.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PluginConfiguration> GetSettings()
    {
        var plugin = Plugin.Instance;
        return plugin is null ? NotRunning() : Ok(plugin.Configuration);
    }

    /// <summary>Gets the shipped settings (a fresh configuration): what the admin page shows when nothing is stored and what
    /// "Restore defaults" puts back.</summary>
    /// <returns>The shipped configuration.</returns>
    [HttpGet("Defaults")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration> GetShippedDefaults() => Ok(ShippedDefaults());

    /// <summary>Validates and saves the admin configuration (admin regex patterns must compile with the non-backtracking engine).</summary>
    /// <param name="body">The flat configuration body and optional expected field values.</param>
    /// <returns>The saved configuration.</returns>
    /// <param name="fields">Optional comma-separated editable fields; everyday requires leaf paths. Omitted preserves full-save behavior.</param>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PluginConfiguration> PutSettingsRequest([FromBody] JsonElement body, [FromQuery] string? fields = null)
        => SettingsUpdateRequest.TryRead<PluginConfiguration>(body, fields, out var config, out var expected, out var error, ControllerContext.HttpContext)
            ? PutSettingsCore(config!, fields, expected)
            : BadRequest(error);

    /// <summary>Saves an already decoded configuration.</summary>
    [NonAction]
    public ActionResult<PluginConfiguration> PutSettings(PluginConfiguration config, string? fields = null)
        => PutSettingsCore(config, fields, null);

    private ActionResult<PluginConfiguration> PutSettingsCore(PluginConfiguration config, string? fields, JsonElement? expectedValues)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotRunning();
        }

        if (config is null)
        {
            return BadRequest(new ApiProblem("The settings are missing.", Array.Empty<ValidationError>()));
        }

        lock (plugin)
        {
            if (fields is null && ControllerContext.HttpContext?.Request.Query.ContainsKey("fields") == true) fields = string.Empty;
            if (!SettingsIntent.TryParse(fields, admin: true, server: true, out var intent, out var intentError))
                return BadRequest(intentError);
            intent?.ReadExpected(ControllerContext.HttpContext?.Request, expectedValues);
            intent?.CheckExpected(plugin.Configuration);
            if (intent is not null) config = intent.Apply(plugin.Configuration, config);
            SettingsValidator.PrepareAdmin(config);
            var languages = new EngineServices(config).Languages;
            var errors = intent is null ? SettingsValidator.ValidateAdmin(config, languages) : intent.Validate(config, languages, plugin.Configuration);
            if (errors.Count > 0)
            {
                return BadRequest(new ApiProblem("The settings are not valid.", errors));
            }

            config.ConfigVersion = PluginConfiguration.CurrentVersion;
            config.ProbeAudioLanguage = null;
            config.ProbeSubtitleLanguage = null;
            config.ProbeReplaceRequestIndices = null;
            config.ReplaceRequestIndices = null;
            var compatibilityWasOn = plugin.Configuration.CompatibilityMode;
            plugin.UpdateConfiguration(config);
            _resolver.Invalidate(null);
            Record(null, "An admin changed Babelfin's server settings");
            UndoCompatibilityIfTurnedOff(compatibilityWasOn, plugin.Configuration.CompatibilityMode);
            return Ok(plugin.Configuration);
        }
    }

    /// <summary>Lists the per-app mode table: every app with its mode, and whether and when it was seen.</summary>
    /// <returns>The rows.</returns>
    [HttpGet("Apps")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IReadOnlyList<AppModeDto>> GetApps()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotRunning();
        }

        return Ok(AppRows(plugin.Configuration));
    }

    internal static IReadOnlyList<AppModeDto> AppRows(PluginConfiguration config)
        => (config.AppModes ?? new List<AppMode>())
            .Where(a => a is not null)
            .Select(a => new AppModeDto(a.Client ?? string.Empty, a.Mode, a.Seen, a.LastSeen, config.RemembersPrePlayChoices(a.Client), a.SeenOnly))
            .ToList();

    /// <summary>Reads Babelfin's activity log, newest first.</summary>
    /// <param name="limit">At most this many (default 100).</param>
    /// <param name="kind">Only this kind ("override-created", "correction-sent", "settings-changed"…).</param>
    /// <param name="userId">Only this person.</param>
    /// <returns>The entries.</returns>
    [HttpGet("Activity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ActivityEntry>> GetActivity([FromQuery] int? limit, [FromQuery] string? kind, [FromQuery] Guid? userId)
    {
        var count = Math.Clamp(limit ?? 100, 1, ActivityLog.RingSize);
        var filterKind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
        var filterUser = userId is { } id && id != Guid.Empty ? id : (Guid?)null;
        IReadOnlyList<ActivityEntry> entries = _activity?.Query(count, filterKind, filterUser) ?? Array.Empty<ActivityEntry>();
        return Ok(entries);
    }

    /// <summary>Lists the plays Babelfin is following now (the dashboard's "what's playing" card).</summary>
    /// <returns>The live entries.</returns>
    [HttpGet("Playback")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PlayEntry>> GetPlayback()
    {
        IReadOnlyList<PlayEntry> entries = _playRegistry?.Snapshot() ?? Array.Empty<PlayEntry>();
        return Ok(entries);
    }

    /// <summary>
    /// Copies one person's settings to another: everyday, "use my everyday settings everywhere", libraries, smart rules, overrides' explicit settings and
    /// collection order. The target keeps their locks, remembered track changes, burned-in flags and switches.
    /// </summary>
    /// <param name="userId">The person to copy from.</param>
    /// <param name="targetUserId">The person to copy to.</param>
    /// <returns>The target's document and the entries left out.</returns>
    [HttpPost("Users/{userId}/CopyTo/{targetUserId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<CopySettingsResult> CopyTo([FromRoute] Guid userId, [FromRoute] Guid targetUserId)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        var source = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        var target = targetUserId == Guid.Empty ? null : _userManager.GetUserById(targetUserId);
        if (source is null || target is null)
        {
            return NotFound();
        }

        if (source.Id == target.Id)
        {
            return BadRequest(new ApiProblem("Choose two different people.", Array.Empty<ValidationError>()));
        }

        PersonStorageAccess.Require(_store, source.Id, writing: true);
        PersonStorageAccess.Require(_store, target.Id, writing: true);

        var stored = GetOrSeed(target);
        var targetObserved = JsonSerializer.Serialize(stored, BabelfinJson.Options);
        var incoming = PersonDocuments.ForCopy(GetOrSeed(source), stored);
        SettingsValidator.PreparePerson(incoming);
        var skipped = PersonDocuments.DropInaccessible(_libraryManager, incoming, target);
        var visibleTarget = stored.Clone();
        var hiddenTarget = PersonDocuments.DropInaccessible(_libraryManager, visibleTarget, target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PersonSettings MergeCopy(PersonSettings latest)
        {
            var result = PersonDocuments.Merge(latest, PersonDocuments.ForCopy(incoming, latest), takeLocks: false, target);
            foreach (var row in latest.Overrides.Where(row => hiddenTarget.Contains(row.ItemId)))
            {
                result.Overrides.RemoveAll(candidate => PersonSettings.SameId(candidate.ItemId, Guid.Parse(row.ItemId)));
                result.Overrides.Add(row.Clone());
            }
            return result;
        }
        var merged = MergeCopy(stored);
        SettingsValidator.PreparePerson(merged);
        var errors = SettingsValidator.ValidatePerson(merged, EngineServices.Current.Languages, includeLocks: false);
        if (errors.Count > 0)
        {
            return BadRequest(new ApiProblem("The copied settings are not valid for this person.", errors));
        }

        string? backupFile = null;
        // Back up exactly the document replaced, under its own store lock, never the configuration lock.
        // Rebuild the copy body from the latest target so its switches, seed and burned-in flags stay local.
        PersonStorageAccess.Update(_store, target.Id, current =>
        {
            PersonStorageAccess.RequireSnapshot(stored, current);
            var latest = current ?? stored;
            if (targetObserved != JsonSerializer.Serialize(latest, BabelfinJson.Options))
                throw new PersonStorageConflictException("The target settings changed while the copy was prepared. Reload the settings and retry; nothing was copied.");
            var document = new ExportDocument { BabelfinVersion = Plugin.VersionString, ExportedAt = DateTime.UtcNow, Users = new List<PersonSettings> { latest.Clone() } };
            var name = "copy-" + target.Id.ToString("N") + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            backupFile = Path.GetFileName(AtomicJsonFile.WriteNew(Path.Combine(_store.DataFolder, "backups"), name, document, BabelfinJson.Options));
            merged = MergeCopy(latest);
            return merged;
        });
        _resolver.Invalidate(target.Id);
        Record(target.Id, "An admin copied another person's Babelfin settings to yours");
        Record(null, $"An admin copied {source.Username}'s Babelfin settings to {target.Username}");
        return Ok(new CopySettingsResult(source.Id, target.Id, _store.TryGet(target.Id) ?? merged, skipped) { BackupFile = backupFile });
    }

    /// <summary>Lists every Jellyfin user with their Babelfin state (the first-run card).</summary>
    /// <returns>The people.</returns>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AdminUserSummary>> GetUsers()
    {
        var rows = _userManager.GetUsers()
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u =>
            {
                var doc = _store.TryGet(u.Id);
                IReadOnlyList<AssumedLanguage> assumed = doc?.Seed is { Confirmed: false } seed ? seed.Assumed : Array.Empty<AssumedLanguage>();
                return new AdminUserSummary(
                    u.Id,
                    u.Username,
                    doc is not null,
                    doc?.Enabled ?? true,
                    doc?.UseEverydayEverywhere ?? false,
                    assumed,
                    doc?.Seed?.Confirmed ?? false,
                    doc?.UpdatedAt,
                    doc is not null && TableModes.PersonPerLibrary(doc),
                    doc is null ? null : TableModes.TableOnly(doc.Everyday),
                    doc?.Libraries?.Count(l => TableModes.HasTableFields(l?.Settings)) ?? 0);
            })
            .ToList();
        return Ok(rows);
    }

    /// <summary>Gets one person's document (seeded and saved when they have none yet).</summary>
    /// <param name="userId">The person.</param>
    /// <returns>The document.</returns>
    [HttpGet("Users/{userId}/Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PersonSettings> GetUserSettings([FromRoute] Guid userId)
    {
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        return Ok(GetOrSeed(user));
    }

    /// <summary>Gets what each row of one person's "Audio and subtitles" table resolves to when left on Default.</summary>
    /// <param name="userId">The person.</param>
    /// <returns>The defaults.</returns>
    [HttpGet("Users/{userId}/Defaults")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TableDefaultsDto> GetUserDefaults([FromRoute] Guid userId)
    {
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        var libraries = PersonDocuments.VisibleLibraries(_libraryManager, _facts, user);
        return Ok(TableDefaults.Build(GetOrSeed(user), Plugin.Instance?.Configuration ?? new PluginConfiguration(), libraries, _facts, EngineServices.Current.Languages));
    }

    /// <summary>Saves one person's document: their own layers and the admin's locks (item ids are checked as that person).</summary>
    /// <param name="userId">The person.</param>
    /// <param name="body">The flat settings body and optional expected field values.</param>
    /// <param name="keepOverrides">True to keep the person's stored overrides (with their remembered track changes) and ignore the body's: a page that doesn't edit overrides saves with this, so it never brings back a removed override or drops one added meanwhile.</param>
    /// <returns>The saved document.</returns>
    /// <param name="fields">Optional comma-separated field intent applied under the person update lock.</param>
    [HttpPut("Users/{userId}/Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<PersonSettings>> PutUserSettingsRequest([FromRoute] Guid userId, [FromBody] JsonElement body, [FromQuery] bool keepOverrides = false, [FromQuery] string? fields = null)
        => SettingsUpdateRequest.TryRead<PersonSettings>(body, fields, out var settings, out var expected, out var error, ControllerContext.HttpContext)
            ? PutUserSettingsCore(userId, settings!, keepOverrides, fields, expected, body)
            : Task.FromResult<ActionResult<PersonSettings>>(BadRequest(error));

    /// <summary>Saves an already decoded person's document.</summary>
    [NonAction]
    public Task<ActionResult<PersonSettings>> PutUserSettings(Guid userId, PersonSettings settings, bool keepOverrides = false, string? fields = null)
        => PutUserSettingsCore(userId, settings, keepOverrides, fields, null);

    private async Task<ActionResult<PersonSettings>> PutUserSettingsCore(Guid userId, PersonSettings settings, bool keepOverrides, string? fields, JsonElement? expectedValues, JsonElement? requestBody = null)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        PersonStorageAccess.Require(_store, user.Id, writing: true);

        if (settings is null)
        {
            return BadRequest(new ApiProblem("The settings are missing.", Array.Empty<ValidationError>()));
        }

        if (fields is null && ControllerContext.HttpContext?.Request.Query.ContainsKey("fields") == true) fields = string.Empty;
        if (!SettingsIntent.TryParse(fields, admin: true, server: false, out var intent, out var intentError))
            return BadRequest(intentError);
        intent?.ReadExpected(ControllerContext.HttpContext?.Request, expectedValues);
        var intentSnapshot = intent is null ? null : GetOrSeed(user);
        if (intent is not null)
        {
            settings = intent.Apply(intentSnapshot!, settings);
            keepOverrides = true; // Neither reference list is part of field intent; keep it exactly as stored.
        }

        SettingsValidator.PreparePerson(settings);
        if (keepOverrides)
        {
            // This page saves neither overrides nor burned-in flags. Ignore both before validation and access checks.
            settings.Overrides = new List<ItemOverride>();
            settings.BurnedIn = new List<BurnedInFlag>();
        }

        var errors = intent is null ? SettingsValidator.ValidatePerson(settings, EngineServices.Current.Languages, includeLocks: true)
            : intent.Validate(settings, EngineServices.Current.Languages, includeLocks: true, intentSnapshot);
        if (errors.Count > 0)
        {
            return BadRequest(new ApiProblem("The settings are not valid.", errors));
        }

        var stored = intentSnapshot ?? GetOrSeed(user);
        var hidden = PersonDocuments.InaccessibleIds(_libraryManager, intent?.ReferencesToCheck(settings) ?? settings, user, stored);
        if (hidden.Count > 0)
        {
            return BadRequest(new ApiProblem("Some items can't be found for this person.", Array.Empty<ValidationError>(), hidden));
        }

        // Merged into the document the store holds when its lock is taken, not into the copy read above: an override removed, a "remove all" or a recorded track
        // change landing in between is neither undone nor lost.
        var saved = PersonDocuments.SaveMerged(_store, stored, settings, takeLocks: true, user, keepOverrides, intent, preserveAbsentLists: true, requestBody: requestBody);
        _resolver.Invalidate(user.Id);
        Record(user.Id, "An admin changed your Babelfin settings");
        await PersonDocuments.SyncRememberFlagsAsync(_rememberFlags, user, saved.Before, saved.Merged).ConfigureAwait(false);
        return Ok(_store.TryGet(user.Id) ?? saved.Merged);
    }

    /// <summary>Lists one person's show and movie overrides (explicit settings and remembered track changes), with their items' current names and libraries as that person sees them.</summary>
    /// <param name="userId">The person.</param>
    /// <returns>The overrides, in the order they are stored (none for a person with no file).</returns>
    [HttpGet("Users/{userId}/Overrides")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<OverrideSummary>> GetUserOverrides([FromRoute] Guid userId)
    {
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        PersonStorageAccess.Require(_store, user.Id, writing: false);
        var settings = _store.TryGet(user.Id);
        return Ok(LibraryQueries.Read(() => OverrideSummaries.Build(_libraryManager, user, settings)));
    }

    /// <summary>Reports a bounded page of one person's missing item references without changing settings or recovery files.</summary>
    /// <param name="userId">The existing Jellyfin person.</param>
    /// <param name="after">The previous report's continuation id, or null for the first page.</param>
    /// <returns>Report-only counts, readiness or skipped reason, and the next continuation id, if any.</returns>
    [HttpPost("Users/{userId}/Overrides/Prune")]
    public ActionResult PruneUserOverrides([FromRoute] Guid userId, [FromQuery] Guid? after = null)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        if (userId == Guid.Empty || _userManager.GetUserById(userId) is null) return NotFound();
        var report = OverridePruner.Prune(_store, _libraryManager, userId, after,
            HttpContext?.RequestAborted ?? default, services: HttpContext?.RequestServices);
        return Ok(report);
    }

    /// <summary>Removes every override of one person, explicit settings and remembered track changes alike.</summary>
    /// <param name="userId">The person.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Users/{userId}/Overrides/All")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteUserOverrides([FromRoute] Guid userId)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        // Read, changed and written as one step under the store's lock, so a recorded track change can't be overwritten by this.
        if (PersonStorageAccess.Update(_store, user.Id, PersonDocuments.WithoutOverrides))
        {
            _resolver.Invalidate(user.Id);
            Record(user.Id, "An admin removed all your show and movie overrides");
        }

        return NoContent();
    }

    /// <summary>Removes one override of one person, with its remembered track change. The item need not exist any more.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="itemId">The id the override is stored under (<c>ItemId</c> in the list).</param>
    /// <param name="scope">The override's scope as the list shows it. Accepted so the same call works for the person and the admin; the id alone names the entry, so it is not needed.</param>
    /// <returns>No content, whether or not the person had that override.</returns>
    [HttpDelete("Users/{userId}/Overrides/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteUserOverride([FromRoute] Guid userId, [FromRoute] Guid itemId, [FromQuery] OverrideScope? scope)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        if (itemId == Guid.Empty)
            return BadRequest(new ApiProblem("An item id is required. To remove all overrides, use DELETE Admin/Users/{userId}/Overrides/All.", Array.Empty<ValidationError>()));
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        var currentName = LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(itemId, user)?.Name);
        ItemOverride? removed = null;
        if (PersonStorageAccess.Update(_store, user.Id, current => PersonDocuments.WithoutOverride(current, itemId, out removed)))
        {
            _resolver.Invalidate(user.Id);

            // The name as the person sees it, else the one stored with the override, else "a title".
            Record(user.Id, $"An admin removed your override for {OverrideSummaries.ActivityNameOf(currentName, removed?.Name)}", itemId);
        }

        return NoContent();
    }

    /// <summary>Approves a person's burned-in flag: it is copied into the admin configuration and applies to everyone.</summary>
    /// <param name="userId">The person who set the flag.</param>
    /// <param name="itemId">The flagged item.</param>
    /// <param name="scope">The flag's scope (optional; the person's first flag on the item otherwise).</param>
    /// <returns>The shared flag.</returns>
    [HttpPost("Users/{userId}/BurnedIn/{itemId}/Approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<BurnedInFlag> ApproveBurnedIn([FromRoute] Guid userId, [FromRoute] Guid itemId, [FromQuery] OverrideScope? scope)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotRunning();
        }

        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null || itemId == Guid.Empty || LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(itemId, user)) is null)
        {
            return NotFound();
        }

        PersonStorageAccess.Require(_store, user.Id, writing: true);
        var flag = _store.TryGet(user.Id)?.BurnedIn
            .FirstOrDefault(f => PersonSettings.SameId(f.ItemId, itemId) && (scope is null || f.Scope == scope));
        if (flag is null)
        {
            return NotFound();
        }

        lock (plugin)
        {
            var config = ImportPlanner.CloneAdmin(plugin.Configuration);
            SettingsValidator.PrepareAdmin(config);
            var shared = config.BurnedIn.FirstOrDefault(f => PersonSettings.SameId(f.ItemId, itemId) && f.Scope == flag.Scope);
            if (shared is null)
            {
                if (config.BurnedIn.Count >= SettingsValidator.MaxBurnedIn)
                {
                    return BadRequest(new ApiProblem($"At most {SettingsValidator.MaxBurnedIn} shared burned-in flags are allowed.", Array.Empty<ValidationError>()));
                }

                shared = new BurnedInFlag { ItemId = itemId.ToString("N"), Scope = flag.Scope };
                config.BurnedIn.Add(shared);
            }

            shared.Name = flag.Name ?? shared.Name;
            shared.Languages = shared.Languages.Concat(flag.Languages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            shared.SetAt = DateTime.UtcNow;
            shared.SetBy = user.Id.ToString("N");
            plugin.UpdateConfiguration(config);
            _resolver.Invalidate(null);
            Record(user.Id, $"An admin shared your burned-in subtitles flag on {shared.Name ?? "a title"} with everyone", itemId);
            return Ok(shared);
        }
    }

    /// <summary>Lists every library with its computed facts and the admin's original-audio choice.</summary>
    /// <returns>The libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AdminLibraryDto>> GetLibraries()
    {
        var config = Plugin.Instance?.Configuration;
        var list = new List<AdminLibraryDto>();
        foreach (var folder in LibraryQueries.Read(() => _libraryManager.GetVirtualFolders()))
        {
            if (!Guid.TryParse(folder.ItemId, out var id) || id == Guid.Empty)
            {
                continue;
            }

            var fact = _facts.Get(id);
            var choice = config?.FindLibrary(id)?.OriginalAudio ?? OriginalAudioChoice.Auto;
            list.Add(new AdminLibraryDto(
                id.ToString("N"),
                folder.Name ?? string.Empty,
                folder.CollectionType?.ToString(),
                choice,
                LibraryView.IsOriginalAudio(choice, fact, folder.Name, config?.Starter),
                fact?.UsualOriginalLanguage,
                fact));
        }

        return Ok(list);
    }

    /// <summary>Asks for the library facts to be recomputed soon (in the background).</summary>
    /// <returns>Accepted.</returns>
    [HttpPost("Libraries/Refresh")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult RefreshLibraries()
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        _facts.RequestRefresh();
        return Accepted();
    }

    /// <summary>Downloads every Babelfin setting (admin and every person) as one JSON file.</summary>
    /// <returns>The file.</returns>
    /// <param name="allowPartial">True to export readable accounts and explicitly name omitted accounts; false retains the full-backup storage preflight.</param>
    [HttpGet("Export")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Export([FromQuery] bool allowPartial = false)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotRunning();
        }

        var now = DateTime.UtcNow;
        var skipped = new List<SkippedAccount>();
        var document = ImportPlanner.BuildExport(plugin.Configuration, StoredPeople(allowPartial ? skipped : null, writing: true), Plugin.VersionString, now);
        document.SkippedUsers = skipped;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, BabelfinJson.Options);
        return File(bytes, "application/json", "babelfin-settings-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json");
    }

    /// <summary>
    /// Resets every Babelfin setting: the admin settings become the shipped defaults (what <c>GET Admin/Defaults</c> returns), and every person whose
    /// Jellyfin user still exists starts again from a fresh seed of their Jellyfin preferences, without the admin's locks. Everything is first saved
    /// to a new file in Babelfin's backups folder, in the <c>GET Admin/Export</c> format, so <c>POST Admin/Import</c> can restore it; when that file
    /// can't be written, nothing is reset.
    /// </summary>
    /// <returns>The backup file and how many people were reset.</returns>
    /// <param name="allowPartial">Explicitly allow resetting readable accounts while reporting skipped accounts. False preserves the all-or-nothing storage preflight.</param>
    [HttpPost("ResetAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ResetAllReport>> ResetAll([FromQuery] bool allowPartial = false)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotRunning();
        }

        // A stable durable inventory survives read-side quarantine and cannot silently lose omitted accounts.
        var candidates = PersonStoreInventory.KnownIds(_store).ToArray();
        if (!allowPartial)
            foreach (var id in candidates) PersonStorageAccess.Require(_store, id, writing: true);

        string backupPath;
        var skipped = new List<SkippedAccount>();
        List<PersonSettings> backedUp;
        string adminObserved;
        try
        {
            var now = DateTime.UtcNow;
            backedUp = StoredPeople(allowPartial ? skipped : null, writing: true, candidates);
            PluginConfiguration adminSnapshot;
            lock (plugin)
            {
                adminObserved = JsonSerializer.Serialize(plugin.Configuration, BabelfinJson.Options);
                adminSnapshot = ImportPlanner.CloneAdmin(plugin.Configuration);
            }
            var document = ImportPlanner.BuildExport(adminSnapshot, backedUp, Plugin.VersionString, now);
            document.SkippedUsers = skipped;
            var name = "babelfin-settings-before-reset-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            backupPath = AtomicJsonFile.WriteNew(plugin.BackupsFolder, name, document, BabelfinJson.Options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't back up the settings before resetting them; nothing was reset");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ApiProblem("The current settings couldn't be backed up, so nothing was reset. The server log has the details.", Array.Empty<ValidationError>()) { Code = "backup-failed" });
        }

        _logger.LogInformation("[Babelfin] Resetting every Babelfin setting; the previous settings were saved to {Backup}", backupPath);
        var report = await ResetPeopleAsync(backupPath, backedUp.ToDictionary(p => p.UserId), skipped).ConfigureAwait(false);

        lock (plugin)
        {
            if (adminObserved == JsonSerializer.Serialize(plugin.Configuration, BabelfinJson.Options))
            {
                var compatibilityWasOn = plugin.Configuration.CompatibilityMode;
                plugin.UpdateConfiguration(ShippedDefaults());
                report = report with { AdminReset = true };
                UndoCompatibilityIfTurnedOff(compatibilityWasOn, plugin.Configuration.CompatibilityMode);
            }
            else
            {
                report = report with
                {
                    FailedNames = report.FailedNames.Append("Babelfin server settings").ToArray(),
                    AdminResetReason = "Server settings changed after the recovery backup and were not reset. Retry to create a current backup.",
                };
            }
        }
        _resolver.Invalidate(null);
        Record(null, report.FailedNames.Count == 0 ? "An admin reset all Babelfin settings" : "An admin reset " + report.People + " people's settings; server reset: " + report.AdminReset + "; problems: " + string.Join(", ", report.FailedNames));
        return ResetResult(report);
    }

    /// <summary>Imports a settings file: a preview by default; with <c>dryRun=false</c> the admin settings and the matched people's documents are replaced.</summary>
    /// <param name="document">The file's content.</param>
    /// <param name="dryRun">True (the default) to preview only.</param>
    /// <returns>The preview (or report).</returns>
    [HttpPost("Import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ImportPreview>> ImportFile([FromBody] JsonElement document, [FromQuery] bool dryRun = true)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        if (!ImportFileFormat.TryRead<ExportDocument>(document, personal: false, out var decoded, out var error)) return BadRequest(error! with { Applied = false });
        return await Import(decoded!, dryRun).ConfigureAwait(false);
    }

    /// <summary>Validates and imports an already decoded file. The HTTP boundary uses the plugin file format explicitly.</summary>
    [NonAction]
    public async Task<ActionResult<ImportPreview>> Import(ExportDocument document, bool dryRun = true)
    {
        using var writeActor = PersonWriteActor.Admin(ControllerContext.HttpContext?.User);
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotRunning();
        }

        if (document is null)
        {
            return BadRequest(new ApiProblem("The file is missing.", Array.Empty<ValidationError>(), Applied: false));
        }

        var structureErrors = ImportStructure.Validate(document);
        if (structureErrors.Count > 0)
            return BadRequest(new ApiProblem("The import file is not valid.", structureErrors, Applied: false));

        if (document.Admin is null && document.Users.Count == 0)
            return BadRequest(new ApiProblem("The file contains no importable settings.", Array.Empty<ValidationError>(), Applied: false));

        var users = _userManager.GetUsers().Select(u => new ImportTargetUser(u.Id, u.Username)).ToList();
        var imported = new Dictionary<Guid, (PersonSettings? Before, PersonSettings After)>();

        PluginConfiguration adminSnapshot;
        string adminObserved;
        lock (plugin)
        {
            adminObserved = JsonSerializer.Serialize(plugin.Configuration, BabelfinJson.Options);
            adminSnapshot = ImportPlanner.CloneAdmin(plugin.Configuration);
        }
        var compatibilityWasOn = adminSnapshot.CompatibilityMode;
        // Reading, preserving and writing person documents must not hold the configuration lock.
        var preview = ImportPlanner.Run(
            document,
            adminSnapshot,
            users,
            _store,
            new EngineServices(adminSnapshot).Languages,
            config =>
            {
                lock (plugin)
                {
                    if (adminObserved != JsonSerializer.Serialize(plugin.Configuration, BabelfinJson.Options))
                        throw new PersonStorageConflictException("Server settings changed during the import. Reload the preview and retry.");
                    plugin.UpdateConfiguration(config);
                }
            },
            dryRun,
            (before, after) => imported[after.UserId] = (before, after));
        if (preview.Applied)
        {
            _resolver.Invalidate(null);
            Record(null, "An admin imported Babelfin settings");
            UndoCompatibilityIfTurnedOff(compatibilityWasOn, plugin.Configuration.CompatibilityMode);
            foreach (var row in preview.Users.Where(r => r.Action != ImportUserPreview.Skip))
            {
                if (imported.TryGetValue(row.UserId, out var saved)
                    && !await AfterImportAsync(row.UserId, saved.Before, saved.After).ConfigureAwait(false))
                {
                    preview.Warnings.Add($"{row.UserName}: The settings were imported, but the Jellyfin remember flags could not be updated. Check the server log and retry.");
                }
            }
        }

        if (!dryRun && preview.StorageBlocked)
            return Conflict(new ApiProblem("Some existing settings use an unsupported format or belong to an archived account. Upgrade Babelfin or restore the Jellyfin account and restart, then preview the import again. "
                + (preview.Applied ? "Some settings were imported; review the failed accounts before retrying." : "Nothing was imported."),
                preview.Users.Where(row => row.Action == ImportUserPreview.Skip).Select(row => new ValidationError("users." + row.UserId.ToString("N"), row.Reason ?? "The settings could not be imported.")).ToArray(), Applied: preview.Applied, Import: preview) { Code = "import-blocked" });

        if (!dryRun && !preview.Applied)
            return BadRequest(new ApiProblem("No settings were applied from this file. Review the preview and account matching before retrying.",
                preview.Users.Where(row => row.Action == ImportUserPreview.Skip).Select(row => new ValidationError("users." + row.SourceUserId.ToString("N"), row.Reason ?? "This account could not be imported.")).ToArray(), Applied: false, Import: preview) { Code = "import-not-applied" });

        return Ok(preview);
    }

    private static PluginConfiguration ShippedDefaults()
    {
        var shipped = new PluginConfiguration();
        shipped.Upgrade();
        return shipped;
    }

    private PersonSettings GetOrSeed(JellyfinUser user)
    {
        PersonStorageAccess.Require(_store, user.Id, writing: false);
        return _store.GetOrCreate(user.Id, () => PersonDocuments.Seed(user, _serverConfiguration));
    }

    /// <summary>Every stored person document, copied, with each person's current Jellyfin name (the people of an export).</summary>
    private List<PersonSettings> StoredPeople(List<SkippedAccount>? skipped = null, bool writing = true, IReadOnlyList<Guid>? candidates = null)
    {
        var people = new List<PersonSettings>();
        foreach (var id in candidates ?? PersonStoreInventory.KnownIds(_store))
        {
            var name = id.ToString("N");
            try
            {
                var user = _userManager.GetUserById(id);
                name = user?.Username ?? name;
                PersonStorageAccess.Require(_store, id, writing);
                var doc = _store.TryGet(id);
                if (doc is null) throw new IOException("The stored settings could not be read.");
                var copy = doc.Clone();
                copy.UserName = user?.Username ?? copy.UserName;
                people.Add(copy);
            }
            catch (Exception ex) when (skipped is not null)
            {
                _logger.LogWarning(ex, "[Babelfin] Skipped {UserId} in admin backup/export; original settings were retained", id);
                skipped.Add(new SkippedAccount(id, name, ex is PersonStorageConflictException ? ex.Message : "The existing settings could not be read safely. Check server storage and retry."));
            }
        }
        return people;
    }

    /// <summary>Resets stored documents of existing Jellyfin people; missing users are skipped, failures are named.</summary>
    internal async Task<ResetAllReport> ResetPeopleAsync(string backupPath, IReadOnlyDictionary<Guid, PersonSettings>? backedUp = null, IReadOnlyList<SkippedAccount>? skipped = null)
    {
        var people = 0;
        var failed = skipped?.Select(p => p.UserName).ToList() ?? new List<string>();
        var problems = skipped?.ToList() ?? new List<SkippedAccount>();
        foreach (var userId in backedUp?.Keys.ToArray() ?? _store.UserIds())
        {
            var name = userId.ToString("N");
            try
            {
                var user = _userManager.GetUserById(userId);
                if (user is null)
                {
                    continue;
                }

                name = user.Username;
                var fresh = PersonDocuments.Seed(user, _serverConfiguration);
                PersonSettings? before = null;
                if (!PersonStorageAccess.Update(_store, userId, current =>
                {
                    if (current is null)
                    {
                        return null;
                    }

                    if (backedUp is not null)
                    {
                        var snapshot = backedUp[userId];
                        PersonStorageAccess.RequireSnapshot(snapshot, current);
                        var comparable = current.Clone(); comparable.UserName = user.Username;
                        if (JsonSerializer.Serialize(comparable, BabelfinJson.Options) != JsonSerializer.Serialize(snapshot, BabelfinJson.Options))
                            throw new PersonStorageConflictException("These settings changed after the reset backup. They were not reset; retry to create a current backup.");
                    }
                    before = current;
                    fresh.StorageEpoch = current.StorageEpoch;
                    fresh.JellyfinChanges = current.JellyfinChanges?.Clone();
                    fresh.HandleNextEpisode = false;
                    return fresh;
                }))
                {
                    throw new IOException("The stored settings were unavailable and were not reset.");
                }

                _resolver.Invalidate(userId);
                Record(userId, "An admin reset your Babelfin settings");
                if (!await PersonDocuments.SyncRememberFlagsAsync(_rememberFlags, user, before, fresh, _store).ConfigureAwait(false))
                {
                    failed.Add(name);
                    problems.Add(new SkippedAccount(userId, name, "The settings were reset, but Jellyfin remember flags could not be restored. Check the server log and retry restoration."));
                    continue;
                }

                people++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Babelfin] Couldn't reset the settings of {UserId}", userId);
                failed.Add(name);
                problems.Add(new SkippedAccount(userId, name, ex is PersonStorageConflictException ? ex.Message : "The settings could not be reset safely. Their previous data was retained; check server storage and retry."));
            }
        }

        return new ResetAllReport(Path.GetFileName(backupPath), "backups/" + Path.GetFileName(backupPath), people, failed) { SkippedUsers = skipped ?? Array.Empty<SkippedAccount>(), FailedUsers = problems };
    }

    internal ObjectResult ResetResult(ResetAllReport report)
        => report.People == 0 && report.FailedNames.Count > 0
            ? StatusCode(StatusCodes.Status500InternalServerError, report)
            : Ok(report);

    private void UndoCompatibilityIfTurnedOff(bool wasOn, bool isOn)
    {
        if (!wasOn || isOn || _compatibilityWriter is not { } writer)
        {
            return;
        }

        _logger.LogInformation("[Babelfin] Compatibility mode was turned off; restoring the remembered selections it wrote");
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await writer.UndoAllAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Babelfin] Undoing compatibility mode failed");
            }
        });
    }

    private void Record(Guid? userId, string text, Guid? itemId = null)
    {
        _activity?.Add(new ActivityEntry(DateTime.UtcNow, ActivityKinds.SettingsChanged, userId, itemId, text));
        _logger.LogInformation("[Babelfin] Settings changed for {Person}: {Text}", userId?.ToString() ?? "the server", text);
    }

    private async Task<bool> AfterImportAsync(Guid userId, PersonSettings? before, PersonSettings imported)
    {
        try
        {
            Record(userId, "An admin imported your Babelfin settings");
            var user = _userManager.GetUserById(userId);
            return user is null || await PersonDocuments.SyncRememberFlagsAsync(_rememberFlags, user, before, imported, _store).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't finish importing the settings of {UserId}", userId);
            return false;
        }
    }

    private ObjectResult NotRunning()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiProblem("Babelfin is not running.", Array.Empty<ValidationError>()) { Code = "not-running" });
}

/// <summary>The reset backup, successful person count, and names whose settings or remember flags could not be reset.</summary>
public sealed record ResetAllReport(string BackupFile, string BackupPath, int People, IReadOnlyList<string> FailedNames)
{
    /// <summary>Gets whether the server configuration was reset as part of this operation.</summary>
    public bool AdminReset { get; init; }

    /// <summary>Gets why the server configuration was left intact, when it changed after its backup.</summary>
    public string? AdminResetReason { get; init; }

    /// <summary>Gets every failed account with a reason, including incomplete Jellyfin flag restoration after a document reset.</summary>
    public IReadOnlyList<SkippedAccount> FailedUsers { get; init; } = Array.Empty<SkippedAccount>();

    /// <summary>Gets the accounts skipped during backup preflight; their files were never reset.</summary>
    public IReadOnlyList<SkippedAccount> SkippedUsers { get; init; } = Array.Empty<SkippedAccount>();
    /// <summary>Gets the result summary, including failures for API clients.</summary>
    public string Message => FailedNames.Count == 0 ? "All settings were reset." : "Reset with problems: " + string.Join(", ", FailedNames);
}
