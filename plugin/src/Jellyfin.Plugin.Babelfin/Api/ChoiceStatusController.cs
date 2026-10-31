using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Api;

public sealed record ChoiceStatus([property: JsonPropertyName("HasChoices")] bool HasChoices);

/// <summary>Read-only status; does not create or seed a person document.</summary>
[ApiController]
[Route("Babelfin")]
[Authorize]
public sealed class ChoiceStatusController : ControllerBase
{
    private readonly IPersonStore _store;
    private readonly IUserManager _users;
    private readonly ILibraryManager _libraries;
    private readonly Func<PluginConfiguration> _configuration;

    public ChoiceStatusController(IPersonStore store, IUserManager users, ILibraryManager libraries, Func<PluginConfiguration>? configuration = null)
    {
        _store = store; _users = users; _libraries = libraries;
        _configuration = configuration ?? (() => Plugin.Instance?.Configuration ?? new PluginConfiguration());
    }

    [HttpGet("Me/ChoiceStatus")]
    [ProducesResponseType(503)]
    public ActionResult<ChoiceStatus> Me()
    {
        var id = ClaimsHelper.GetUserId(User);
        var user = id is { } value ? _users.GetUserById(value) : null;
        if (user is null) return Unauthorized();
        IReadOnlyCollection<Guid> visible;
        try { visible = PersonDocuments.VisibleLibraryIds(_libraries, user); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (HttpContext?.RequestServices?.GetService(typeof(ILogger<ChoiceStatusController>)) as ILogger<ChoiceStatusController>)
                ?.LogWarning(ex, "[Babelfin] Saved-choice diagnostic temporarily unavailable; no settings were changed");
            return StatusCode(503, new ApiProblem("The library is temporarily unavailable. Saved-choice status could not be checked; retry later.", Array.Empty<ValidationError>()));
        }
        return Ok(new ChoiceStatus(ChoiceStatusLogic.ForPerson(_store.TryGet(user.Id), _configuration(), visible)));
    }

    [HttpGet("Admin/ChoiceStatus")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<ChoiceStatus> Admin()
    {
        var config = _configuration();
        var existingLibraries = _libraries.GetVirtualFolders().Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToHashSet();
        if (ChoiceStatusLogic.AdminDefaults(config, existingLibraries)) return Ok(new ChoiceStatus(true));
        foreach (var id in _store.UserIds())
        {
            var user = _users.GetUserById(id);
            if (user is not null && ChoiceStatusLogic.ForPerson(_store.TryGet(id), config, PersonDocuments.VisibleLibraryIds(_libraries, user)))
                return Ok(new ChoiceStatus(true));
        }
        return Ok(new ChoiceStatus(false));
    }
}

/// <summary>Reports saved choices in the selected table mode, including while playback is disabled.
/// Built-in commentary/audio-description avoidance can still affect a file when HasChoices is false.</summary>
public static class ChoiceStatusLogic
{
    private static bool AudioChoice(IReadOnlyList<AudioStep> chain) => chain.Count != 1 || chain[0].Kind != AudioStepKind.VideoDefault;
    private static bool ProfileChoices(ResolvedProfile p) => AudioChoice(p.AudioChain)
        || p.WhenAudioUnderstood != SubtitleWant.VideoDefault || p.WhenAudioNotUnderstood != SubtitleWant.VideoDefault;

    private static bool LayerChoices(SettingsLayer? layer) => layer is not null &&
        ((layer.AudioChain is { } chain && (chain.Length != 1 || chain[0]?.Kind != AudioStepKind.VideoDefault))
        || (layer.AudioChain is null && layer.ForeignTitles.HasValue)
        || (layer.WhenAudioUnderstood.HasValue && layer.WhenAudioUnderstood != SubtitleWant.VideoDefault)
        || (layer.WhenAudioNotUnderstood.HasValue && layer.WhenAudioNotUnderstood != SubtitleWant.VideoDefault)
        || Advanced(layer));

    // Explicit Advanced fields can affect filtering/ranking, even when the table says Video default.
    // Spoken/written/subtitle language lists alone and seed provenance are preferences, not track choices.
    private static bool Advanced(SettingsLayer? layer) => layer?.SetFields().Any(f => !TableModes.Fields.Contains(f)
        && f != SettingsFields.Understood && f != SettingsFields.Reads) == true;

    private static IEnumerable<SmartRule> Genres(IEnumerable<SmartRule>? rows) => (rows ?? Array.Empty<SmartRule>())
        .Where(r => r is not null && r.Enabled && PersonMigrations.IsGenreRow(r));

    public static bool ForPerson(PersonSettings? person, PluginConfiguration admin, IReadOnlyCollection<Guid> visibleLibraries)
    {
        person ??= new PersonSettings();
        if (person.BurnedIn?.Any(b => b is not null && b.Scope != OverrideScope.Library
            && Guid.TryParse(b.ItemId, out var id) && id != Guid.Empty && b.Languages?.Any(l => !string.IsNullOrWhiteSpace(l)) == true) == true) return true;
        if (Advanced(person.Everyday) || (person.Overrides?.Any(o => o is not null && o.Scope != OverrideScope.Collection
            && (LayerChoices(o.Settings) || o.Manual is { } m && (m.Audio is not null || m.Subtitle is not null || m.SubtitlesOff))) ?? false)) return true;
        if (person.Libraries?.Any(l => l is not null && Guid.TryParse(l.LibraryId, out var id) && visibleLibraries.Contains(id) && Advanced(l.Settings)) == true) return true;
        var genreNames = Genres(person.SmartRules).Concat(Genres(admin.SmartRules))
            .SelectMany(r => r.Conditions[0].Values).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Include the ordinary case and each configured genre; first matching row still resolves field by field.
        var genres = new[] { Array.Empty<string>() }.Concat(genreNames.Select(g => new[] { g }));
        var libraries = visibleLibraries.Count == 0 ? new[] { Guid.Empty } : visibleLibraries.ToArray();
        foreach (var library in libraries)
        foreach (var names in genres)
        {
            var context = ItemContext.None with { LibraryIds = library == Guid.Empty ? Array.Empty<Guid>() : new[] { library }, Genres = names };
            var applicableAdmin = visibleLibraries.Count == 0 ? new PluginConfiguration() : admin;
            var resolved = ProfileResolver.Resolve(person, applicableAdmin, context, LanguageTable.Default);
            if (ProfileChoices(resolved.Profile)) return true;
            if (visibleLibraries.Count > 0 && (Advanced(admin.Everyday) || admin.Libraries?.Any(l => l is not null
                && PersonSettings.SameId(l.LibraryId, library) && Advanced(l.Settings)) == true)) return true;
        }
        return false;
    }

    public static bool AdminDefaults(PluginConfiguration admin, IReadOnlyCollection<Guid> existingLibraries)
    {
        if (Advanced(admin.Everyday)) return true;
        if (Genres(admin.SmartRules).Any(r => LayerChoices(TableModes.TableOnly(r.Settings)))) return true;
        return TableModes.AdminPerLibrary(admin)
            ? admin.Libraries?.Any(l => l is not null && Guid.TryParse(l.LibraryId, out var id) && existingLibraries.Contains(id) && LayerChoices(l.Settings)) == true
            : LayerChoices(admin.Everyday) || admin.Libraries?.Any(l => l is not null && Guid.TryParse(l.LibraryId, out var id) && existingLibraries.Contains(id) && Advanced(l.Settings)) == true;
    }
}