using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>What Babelfin does with the indices of one PlaybackInfo request.</summary>
public enum PlaybackInfoOutcome
{
    /// <summary>Keep the request as it is (no indices, Babelfin's own indices, or the app's choice trusted).</summary>
    Trust,

    /// <summary>Replace the indices (and, for a single source, the media source id) with Babelfin's choice.</summary>
    Replace,

    /// <summary>Keep the request: it is a manual choice, which is remembered (a change during play once it has settled).</summary>
    Manual,
}

/// <summary>The facts the PlaybackInfo decision table reads.</summary>
/// <param name="HasExplicitIndices">The request carries an audio or a subtitle index.</param>
/// <param name="RepeatDuringPlay">The same session is playing the same item; replace-unless-manual also preserves long pauses.</param>
/// <param name="DiffersFromExpected">The indices differ from the play's expected tracks (only meaningful for a repeat).</param>
/// <param name="DiffersFromServed">Babelfin served this person indices for the item within the served window, and the request's differ.</param>
/// <param name="DiffersFromChoice">The request's indices differ from Babelfin's current choice.</param>
/// <param name="Mode">The app's mode.</param>
/// <param name="AutoplayWindowHit">The same session's previous (other) item stopped within the autoplay window.</param>
/// <param name="RememberPrePlayChoices">Whether this app's differing pre-play picks may be remembered.</param>
/// <param name="FreshServedChoice">A served picker newer than the last stop and at least <see cref="PlaybackInfoFilter.PickerUseTime"/> before the request, with no other item still playing, or a request outside autoplay.</param>
public readonly record struct PlaybackInfoFacts(
    bool HasExplicitIndices,
    bool RepeatDuringPlay,
    bool DiffersFromExpected,
    bool DiffersFromServed,
    bool DiffersFromChoice,
    AppModeKind Mode,
    bool AutoplayWindowHit,
    bool RememberPrePlayChoices = true,
    bool FreshServedChoice = false);

/// <summary>
/// Handles <c>POST /Items/{itemId}/PlaybackInfo</c>: records the play's expected tracks, tells a manual
/// choice from an app's own copy of the last episode's tracks, replaces the indices by the app's mode, remembers manual choices
/// and, in compatibility mode, writes the choice into Jellyfin's remembered selection. Never throws and never blocks the request.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item>"Served" means media sources included in a successful response for this person, item and source. Internal lookups and
/// simulator results never populate that map; entries expire after 30 minutes.</item>
/// <item>A repeat during play is considered first. Apps opted into pre-play memory may preserve a fresh served-picker change
/// before autoplay replacement; during autoplay a picker counts as fresh only when served after the previous item stopped and at
/// least <see cref="PickerUseTime"/> before the request. Other apps only remember changes made during play.</item>
/// <item>The autoplay window counts only when the stopped item is another item (resuming the same item is not autoplay). A request
/// for another item while the session still plays one counts too: jellyfin-web asks for the next item before its stop report lands.</item>
/// <item>In replace-on-autoplay mode, every index inside the autoplay window that differs from Babelfin's choice is replaced, even one
/// naming a track of the same language and kind: the app copies the previous item's track by position, codec or language, which is
/// never a person's choice. A person's picks reach the next item through the changes Babelfin remembered for their memory scope.</item>
/// <item>A manual change during play (a repeat) updates the play's expected tracks at once and is held on the play: the
/// <see cref="PlaybackMonitor"/> remembers it once it has stayed selected for <see cref="PlaybackMonitor.SettlePeriod"/>. A manual
/// pre-play choice is remembered at once.</item>
/// <item>A request that names only one of the two indices is compared on that one; the other stays Babelfin's.</item>
/// <item>No session yet and no device id: the play is not registered (nothing to key it by); everything else still applies.</item>
/// <item>Babelfin had nothing to say (plugin or person switched off, nothing decided): the filter stays out entirely.</item>
/// <item>The per-app row of an unknown client is added once per client per process (with the configured mode for unknown apps);
/// Seen/LastSeen updates are saved at most once an hour across all clients, without raising the configuration event.</item>
/// <item>An app's own choice kept in Trust mode (indices differing from Babelfin's) gets a <see cref="ActivityKinds.ManualTrusted"/>
/// activity entry once per play.</item>
/// </list>
/// </remarks>
public sealed class PlaybackInfoFilter : IAsyncActionFilter
{
    /// <summary>A request within this long of the play's last progress report is a repeat during play.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(2);

    /// <summary>During autoplay a served picker supports a fresh pre-play choice only when it was served after the previous item stopped
    /// and at least this long before the request: jellyfin-web fetches the next item immediately before an autoplay PlaybackInfo
    /// whose indices are ranked from the previous item's tracks.</summary>
    public static readonly TimeSpan PickerUseTime = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SeenInterval = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, DateTime> SeenMarks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ConfigLock = new();
    private static readonly ConditionalWeakTable<PluginConfiguration, DiscoverySave> DiscoverySaves = new();

    private sealed class DiscoverySave
    {
        public DateTime At { get; set; }
    }

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly DecoratorStats _stats;
    private readonly PlayRegistry _registry;
    private readonly TrackChangeRecorder _recorder;
    private readonly ISessionManager _sessionManager;
    private readonly IActivityLog _activity;
    private readonly ICompatibilityWriter _compatibility;
    private readonly ILogger<PlaybackInfoFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackInfoFilter"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager (Babelfin's decorator when wrapped).</param>
    /// <param name="stats">Babelfin's counters and recent decisions (the "served" indices).</param>
    /// <param name="registry">The play registry.</param>
    /// <param name="recorder">Remembers manual choices.</param>
    /// <param name="sessionManager">Jellyfin's sessions (to find the requesting app's session).</param>
    /// <param name="activity">Babelfin's activity log.</param>
    /// <param name="compatibility">Compatibility mode's writer.</param>
    /// <param name="logger">Logger.</param>
    public PlaybackInfoFilter(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        DecoratorStats stats,
        PlayRegistry registry,
        TrackChangeRecorder recorder,
        ISessionManager sessionManager,
        IActivityLog activity,
        ICompatibilityWriter compatibility,
        ILogger<PlaybackInfoFilter> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _stats = stats;
        _registry = registry;
        _recorder = recorder;
        _sessionManager = sessionManager;
        _activity = activity;
        _compatibility = compatibility;
        _logger = logger;
    }

    /// <summary>
    /// No indices are trusted. In-play repeats compare with expected indices. An opted-in app's fresh pre-play choice is
    /// remembered before autoplay replacement. Otherwise indices that differ from Babelfin's choice are replaced in
    /// replace-unless-manual mode and, inside the autoplay window, in replace-on-autoplay mode; all others are trusted.
    /// </summary>
    /// <param name="facts">The facts.</param>
    /// <returns>The outcome.</returns>
    public static PlaybackInfoOutcome Decide(PlaybackInfoFacts facts)
    {
        if (!facts.HasExplicitIndices)
        {
            return PlaybackInfoOutcome.Trust;
        }

        if (facts.RepeatDuringPlay)
        {
            return facts.DiffersFromExpected ? PlaybackInfoOutcome.Manual : PlaybackInfoOutcome.Trust;
        }

        if (facts.RememberPrePlayChoices && facts.DiffersFromServed
            && (facts.FreshServedChoice || (!facts.AutoplayWindowHit && facts.DiffersFromChoice)))
        {
            return PlaybackInfoOutcome.Manual;
        }

        if (!facts.DiffersFromChoice)
        {
            return PlaybackInfoOutcome.Trust;
        }

        return facts.Mode == AppModeKind.ReplaceUnlessManual || (facts.Mode == AppModeKind.ReplaceOnAutoplay && facts.AutoplayWindowHit)
            ? PlaybackInfoOutcome.Replace
            : PlaybackInfoOutcome.Trust;
    }

    /// <summary>Whether requested indices differ from a reference choice; only the indices present in the request are compared (a subtitle below 0 is "off").</summary>
    /// <param name="audio">The requested audio index, or null.</param>
    /// <param name="subtitle">The requested subtitle index, or null.</param>
    /// <param name="refAudio">The reference audio index.</param>
    /// <param name="refSubtitle">The reference subtitle index.</param>
    /// <returns>True when they differ.</returns>
    public static bool Differs(int? audio, int? subtitle, int? refAudio, int? refSubtitle)
        => (audio.HasValue && audio != refAudio) || (subtitle.HasValue && Sub(subtitle) != Sub(refSubtitle));

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        try
        {
            if (context.ActionDescriptor is ControllerActionDescriptor action
                && action.ActionName == "GetPostedPlaybackInfo"
                && action.ControllerName == "MediaInfo")
            {
                Handle(context);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't handle a PlaybackInfo request; the app's request is used unchanged");
        }

        await next().ConfigureAwait(false);
    }

    private static int Sub(int? subtitle) => subtitle is { } s && s >= 0 ? s : -1;

    private static T? Get<T>(IDictionary<string, object?> args, string name)
        => args.TryGetValue(name, out var value) && value is T typed ? typed : default;

    // PlaybackInfoDto lives in Jellyfin.Api, which plugins can't reference, so its properties are read by name.
    private static T? GetDtoValue<T>(object? dto, string property)
    {
        var info = dto?.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        return info?.GetValue(dto) is T typed ? typed : default;
    }

    private static void SetDtoValue(object? dto, string property, object? value)
    {
        var info = dto?.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (info is not null && info.CanWrite)
        {
            info.SetValue(dto, value);
        }
    }

    private void Handle(ActionExecutingContext context)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
        {
            return;
        }

        var args = context.ActionArguments;
        if (!args.TryGetValue("itemId", out var itemIdValue) || itemIdValue is not Guid itemId || itemId == Guid.Empty)
        {
            return;
        }

        var principal = context.HttpContext.User;
        var userId = ClaimsHelper.GetUserId(principal);
        if (userId is null)
        {
            return;
        }

        args.TryGetValue("playbackInfoDto", out var dto);

        // An admin asking on someone else's behalf: Jellyfin answers for that person, so stay out.
        var requestedUser = Get<Guid?>(args, "userId") ?? GetDtoValue<Guid?>(dto, "UserId");
        if (requestedUser.HasValue && requestedUser.Value != Guid.Empty && requestedUser.Value != userId.Value)
        {
            return;
        }

        // Snapshot before source/resolver I/O: a request delayed there must not become a
        // newer manual choice merely because it resumes after a progress report.
        var client = ClaimsHelper.GetClient(principal)?.Trim();
        var deviceId = ClaimsHelper.GetDeviceId(principal)?.Trim();
        var session = FindSession(deviceId, client, userId.Value);
        var now = _registry.Now;
        var order = ManualChangeOrder.Next();
        var sessionSnapshot = session is null ? null : _registry.Get(session.Id);
        var pendingSnapshot = string.IsNullOrEmpty(deviceId) ? null : _registry.GetPending(deviceId, client, itemId);
        var entry = session is null ? pendingSnapshot : sessionSnapshot;
        if (entry is not null && entry.UserId != userId.Value) entry = null;

        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return;
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null || !BabelfinMediaSourceManager.CanChooseTracks(item))
        {
            return;
        }

        var mode = AppModeFor(config, client);

        var askedAudio = Get<int?>(args, "audioStreamIndex") ?? GetDtoValue<int?>(dto, "AudioStreamIndex");
        var askedSubtitle = Get<int?>(args, "subtitleStreamIndex") ?? GetDtoValue<int?>(dto, "SubtitleStreamIndex");
        var observedAudio = askedAudio;
        var observedSubtitle = askedSubtitle;
        var mediaSourceId = Get<string>(args, "mediaSourceId");
        if (string.IsNullOrEmpty(mediaSourceId))
        {
            mediaSourceId = GetDtoValue<string>(dto, "MediaSourceId");
        }

        // This internal lookup changes diagnostic decisions, never the map of actual responses.
        var sources = _mediaSourceManager.GetStaticMediaSources(item, true, user);
        if (sources is null || sources.Count == 0)
        {
            return;
        }

        var setSourceId = false;
        if (string.IsNullOrEmpty(mediaSourceId) && sources.Count == 1)
        {
            mediaSourceId = sources[0].Id;
            setSourceId = true;
        }

        var source = string.IsNullOrEmpty(mediaSourceId)
            ? null
            : sources.FirstOrDefault(s => string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            _logger.LogDebug("[Babelfin] PlaybackInfo for {Item}: no single media source to choose for; request left unchanged", item.Name);
            return;
        }

        var decision = _stats.GetDecision(user.Id, item.Id, source.Id)?.Decision;
        if (decision is null)
        {
            // Babelfin had nothing to say for this person or item: Jellyfin's behaviour stands.
            return;
        }

        // A track left to the video (Babelfin set nothing for it) belongs to the app and Jellyfin: it is never compared, replaced,
        // expected after start, remembered as a manual change or written in compatibility mode.
        var audioLeft = decision.AudioIndex is null;
        var subtitleLeft = decision.SubtitleIndex is null;
        if (audioLeft)
        {
            askedAudio = null;
        }

        if (subtitleLeft)
        {
            askedSubtitle = null;
        }

        // Babelfin's choice: what the wrapper wrote into the source (in a dry run, the planned decision).
        int? choiceAudio = audioLeft ? null : config.DryRun ? decision.AudioIndex ?? source.DefaultAudioStreamIndex : source.DefaultAudioStreamIndex;
        int? choiceSubtitle = subtitleLeft ? null : config.DryRun ? decision.SubtitleIndex ?? source.DefaultSubtitleStreamIndex : source.DefaultSubtitleStreamIndex;

        var served = _stats.GetServed(user.Id, item.Id, source.Id, deviceId, client);
        if (served is not null && served.At < now.AddMinutes(-Math.Max(0, config.ServedWindowMinutes)))
        {
            served = null;
        }

        int? servedAudio = null, servedSubtitle = null;
        if (served is not null)
        {
            var applied = served.Outcome == DecisionOutcome.Applied && served.Decision is not null;
            servedAudio = applied ? served.Decision!.AudioIndex ?? served.JellyfinAudioIndex : served.JellyfinAudioIndex;
            servedSubtitle = applied ? served.Decision!.SubtitleIndex ?? served.JellyfinSubtitleIndex : served.JellyfinSubtitleIndex;
        }

        var repeat = IsRepeat(entry, item.Id, mode, now);
        var sameSource = entry?.MediaSourceId is null || string.Equals(entry.MediaSourceId, source.Id, StringComparison.OrdinalIgnoreCase);
        if (repeat && sameSource)
        {
            // Exclude command echoes and audio-driven subtitle resets from memory comparisons,
            // while leaving the app's actual request untouched. A different stream can still be manual.
            entry!.ObserveAudio(observedAudio, now);
            if (entry.IsAudioCommandEcho(askedAudio, now)) askedAudio = null;
            if (entry.IsSubtitleCommandEcho(askedSubtitle, now) || entry.IsSubtitleReset(askedSubtitle, now)) askedSubtitle = null;
        }
        // Moving to the next item: the previous one stopped within the window, or the session is still playing another item
        // (jellyfin-web asks for the next item's playback info before its "stopped" report for the previous one arrives).
        var switching = IsActivePlay(entry, now) && entry!.ItemId is { } playingItem && playingItem != Guid.Empty && playingItem != item.Id;
        var autoplay = switching
            || (entry?.LastStoppedItemId is { } stoppedItem && stoppedItem != item.Id
                && entry.LastStoppedAt is { } stoppedAt && stoppedAt >= now.AddSeconds(-Math.Max(0, config.AutoplayWindowSeconds)));
        var hasExplicit = askedAudio.HasValue || askedSubtitle.HasValue;
        var differsFromChoice = Differs(askedAudio, askedSubtitle, choiceAudio, choiceSubtitle);
        var facts = new PlaybackInfoFacts(
            hasExplicit,
            repeat,
            repeat && entry!.HasExpected && Differs(askedAudio, askedSubtitle, entry.ExpectedAudio, entry.ExpectedSubtitle),
            served is not null && Differs(askedAudio, askedSubtitle, servedAudio, servedSubtitle),
            differsFromChoice,
            mode,
            autoplay,
            config.RemembersPrePlayChoices(client),
            IsFreshPrePlayChoice(served, entry, autoplay, now, switching));
        var outcome = Decide(facts);

        if (config.DryRun)
        {
            _logger.LogInformation(
                "[Babelfin] Dry run: PlaybackInfo for {Item} from {Client} ({Mode}): asked {OldAudio}/{OldSubtitle}, Babelfin {NewAudio}/{NewSubtitle} → would {Outcome}, mediaSourceId {Id}",
                item.Name,
                client,
                mode,
                askedAudio,
                askedSubtitle,
                choiceAudio,
                choiceSubtitle,
                outcome,
                mediaSourceId);
            return;
        }

        int? expectedAudio, expectedSubtitle;
        if (outcome == PlaybackInfoOutcome.Replace)
        {
            expectedAudio = choiceAudio;
            expectedSubtitle = choiceSubtitle;
            _logger.LogInformation(
                "[Babelfin] PlaybackInfo for {Item} from {Client} ({Mode}): audio {OldAudio}→{NewAudio}, subs {OldSubtitle}→{NewSubtitle}, mediaSourceId {Id}",
                item.Name,
                client,
                mode,
                askedAudio,
                choiceAudio,
                askedSubtitle,
                choiceSubtitle,
                mediaSourceId);
        }
        else
        {
            expectedAudio = audioLeft ? null : askedAudio ?? (repeat ? entry!.ExpectedAudio : choiceAudio);
            expectedSubtitle = subtitleLeft ? null : askedSubtitle ?? (repeat ? entry!.ExpectedSubtitle : choiceSubtitle);
        }

        var trusted = outcome != PlaybackInfoOutcome.Replace && hasExplicit && differsFromChoice;
        var manualAudio = false;
        var manualSubtitle = false;
        int? previousAudio = null, previousSubtitle = null;
        if (outcome == PlaybackInfoOutcome.Manual)
        {
            // A stream the request didn't name didn't change.
            previousAudio = askedAudio is null ? expectedAudio : repeat ? entry!.ExpectedAudio : served is not null ? servedAudio : choiceAudio;
            previousSubtitle = askedSubtitle is null ? expectedSubtitle : repeat ? entry!.ExpectedSubtitle : served is not null ? servedSubtitle : choiceSubtitle;
            if (repeat)
            {
                _logger.LogInformation(
                    "[Babelfin] PlaybackInfo for {Item} from {Client}: a manual choice during play ({Audio}/{Subtitle}, was {OldAudio}/{OldSubtitle}); trusted, and remembered if it stays selected for {Seconds} s",
                    item.Name,
                    client,
                    expectedAudio,
                    expectedSubtitle,
                    previousAudio,
                    previousSubtitle,
                    PlaybackMonitor.SettlePeriod.TotalSeconds);
            }
            else
            {
                _logger.LogInformation(
                    "[Babelfin] PlaybackInfo for {Item} from {Client}: a manual choice ({Audio}/{Subtitle}, was {OldAudio}/{OldSubtitle}); trusted and remembered",
                    item.Name,
                    client,
                    expectedAudio,
                    expectedSubtitle,
                    previousAudio,
                    previousSubtitle);
            }

            manualAudio = askedAudio.HasValue && expectedAudio.HasValue && expectedAudio != previousAudio;
            manualSubtitle = askedSubtitle.HasValue && expectedSubtitle.HasValue
                && (!previousSubtitle.HasValue || Sub(expectedSubtitle) != Sub(previousSubtitle));
        }
        else if (outcome == PlaybackInfoOutcome.Trust && trusted && !repeat)
        {
            _logger.LogInformation("[Babelfin] PlaybackInfo for {Item} from {Client} ({Mode}): the app's own choice {Audio}/{Subtitle} is kept", item.Name, client, mode, askedAudio, askedSubtitle);
            AddTrustedEntry(user, item, client, askedAudio, askedSubtitle, choiceAudio, choiceSubtitle);
        }

        var accepted = Register(session, deviceId, client, user, item, source, sessionSnapshot, pendingSnapshot, repeat, switching, outcome, trusted, manualAudio, manualSubtitle, expectedAudio, expectedSubtitle, previousAudio, previousSubtitle, choiceAudio, choiceSubtitle, observedAudio, observedSubtitle, now, order,
            (audio, subtitle) =>
            {
                if (outcome == PlaybackInfoOutcome.Replace)
                    Replace(args, dto, source, audio && subtitle && setSourceId ? mediaSourceId : null, !audioLeft && audio, !subtitleLeft && subtitle);
            });

        // A change during play is held on the play until it settles (Register); a pre-play choice is remembered now.
        if (outcome == PlaybackInfoOutcome.Manual && !repeat && ((manualAudio && accepted.Audio) || (manualSubtitle && accepted.Subtitle)))
        {
            _recorder.Record(user, item, source,
                manualAudio && accepted.Audio ? expectedAudio : previousAudio,
                manualSubtitle && accepted.Subtitle ? expectedSubtitle : previousSubtitle,
                previousAudio, previousSubtitle, client, order);
        }

        if (config.CompatibilityMode && accepted.Audio && accepted.Subtitle)
        {
            WriteCompatibility(user, item, expectedAudio, expectedSubtitle);
        }
    }

    private static bool IsNewer(ManualChangeOrder order, ManualChangeOrder? previous) =>
        previous is { } value && order.Epoch == value.Epoch && order.Sequence > value.Sequence;

    private (bool Audio, bool Subtitle) Register(
        SessionInfo? session,
        string? deviceId,
        string? client,
        User user,
        BaseItem item,
        MediaSourceInfo source,
        PlayRecord? sessionSnapshot,
        PlayRecord? pendingSnapshot,
        bool repeat,
        bool switching,
        PlaybackInfoOutcome outcome,
        bool trusted,
        bool manualAudio,
        bool manualSubtitle,
        int? expectedAudio,
        int? expectedSubtitle,
        int? previousAudio,
        int? previousSubtitle,
        int? choiceAudio,
        int? choiceSubtitle,
        int? observedAudio,
        int? observedSubtitle,
        DateTime now,
        ManualChangeOrder order,
        Action<bool, bool> applyRequest)
    {
        var pending = session is null || switching;
        var snapshot = pending ? pendingSnapshot : sessionSnapshot;
        var acceptAudio = true;
        var acceptSubtitle = true;
        void Apply(PlayRecord r)
        {
            var supersededSnapshot = snapshot is not null && r.PlayId != snapshot.PlayId;
            if (snapshot is not null && !supersededSnapshot)
            {
                if (r.PlayId != snapshot.PlayId || r.UserId != snapshot.UserId || r.ItemId != snapshot.ItemId
                    || (snapshot.MediaSourceId is not null && !string.Equals(r.MediaSourceId, snapshot.MediaSourceId, StringComparison.OrdinalIgnoreCase)))
                {
                    acceptAudio = false;
                    acceptSubtitle = false;
                    return;
                }
                acceptAudio = outcome == PlaybackInfoOutcome.Manual && manualAudio && r.ManualAudioOrder is not null
                    ? IsNewer(order, r.ManualAudioOrder)
                    : r.ManualAudioRevision == snapshot.ManualAudioRevision;
                acceptSubtitle = outcome == PlaybackInfoOutcome.Manual && manualSubtitle && r.ManualSubtitleOrder is not null
                    ? IsNewer(order, r.ManualSubtitleOrder)
                    : r.ManualSubtitleRevision == snapshot.ManualSubtitleRevision;
            }
            var sourceChanged = r.MediaSourceId is not null && !string.Equals(r.MediaSourceId, source.Id, StringComparison.OrdinalIgnoreCase);
            if ((!repeat && !r.Playing && !((snapshot is null || supersededSnapshot) && r.PreparationOrder is not null)) || sourceChanged)
            {
                r.ResetPlay();
            }

            r.ExpireCommands(now);
            if (repeat && !sourceChanged)
            {
                if (acceptAudio) r.ObserveAudio(observedAudio, now);
                // A PlaybackInfo echo acknowledges that stream once. Advance the report baseline
                // too, so its following progress report cannot turn the same echo into memory.
                if (acceptAudio && r.IsAudioCommandEcho(observedAudio, now))
                {
                    r.ClearAudioCommand();
                    r.LastReportedAudio = observedAudio;
                }
                if (acceptSubtitle && r.IsSubtitleCommandEcho(observedSubtitle, now))
                {
                    r.ClearSubtitleCommand();
                    r.LastReportedSubtitle = observedSubtitle;
                }
            }

            r.ItemId = item.Id;
            r.ItemName = item.Name;
            r.UserId = user.Id;
            r.Client = client ?? r.Client;
            r.DeviceId = deviceId ?? r.DeviceId;
            r.DeviceName = session?.DeviceName ?? r.DeviceName;
            r.MediaSourceId = source.Id;
            r.HasExpected = true;
            if (acceptAudio)
            {
                r.ExpectedAudio = expectedAudio;
                r.ChoiceAudio = choiceAudio;
            }
            if (acceptSubtitle)
            {
                r.ExpectedSubtitle = expectedSubtitle;
                r.ChoiceSubtitle = choiceSubtitle;
            }
            r.Trusted |= trusted;
            if (outcome == PlaybackInfoOutcome.Manual && ((manualAudio && acceptAudio) || (manualSubtitle && acceptSubtitle)))
            {
                r.ManualChanged = true;
                // Explicit PlaybackInfo picks and progress-report picks retire the same
                // per-stream startup acknowledgement. Leave the other stream untouched.
                // A change during play is held until it settles; a pre-play choice, remembered at once, replaces a held one.
                if (manualAudio && acceptAudio)
                {
                    r.ReportChoiceAudio = choiceAudio;
                    r.ManualAudioChanged = true;
                    r.ManualAudioRevision++;
                    r.ManualAudioOrder = order;
                    if (repeat) r.HoldAudioChange(expectedAudio, previousAudio, now, order);
                    else r.PendingAudio = null;
                }
                if (manualSubtitle && acceptSubtitle)
                {
                    r.ReportChoiceSubtitle = choiceSubtitle.HasValue ? Sub(choiceSubtitle) : null;
                    r.ManualSubtitleChanged = true;
                    r.ManualSubtitleRevision++;
                    r.ManualSubtitleOrder = order;
                    r.LastSubtitleChangedAt = now;
                    if (repeat) r.HoldSubtitleChange(expectedSubtitle, previousSubtitle, now, order);
                    else r.PendingSubtitle = null;
                }
                if (repeat)
                {
                    // The progress reports that follow show the new tracks: not a second manual change.
                    if (manualAudio && acceptAudio) r.LastReportedAudio = expectedAudio;
                    if (manualSubtitle && acceptSubtitle) r.LastReportedSubtitle = expectedSubtitle;
                    r.LastProgressAt = now;
                }
            }
            applyRequest(acceptAudio, acceptSubtitle);
        }

        if (session is not null || !string.IsNullOrEmpty(deviceId))
        {
            if (!_registry.TryUpdatePlaybackInfo(session?.Id, deviceId, client, item.Id, user.Id, sessionSnapshot, pendingSnapshot, pending, source.Id, order, Apply))
                return (false, false);
        }
        else
        {
            // Without a session or device there is no tracked identity to compare.
            applyRequest(true, true);
        }
        return (acceptAudio, acceptSubtitle);
    }

    private void Replace(IDictionary<string, object?> args, object? dto, MediaSourceInfo source, string? setSourceId, bool audio, bool subtitle)
    {
        if (setSourceId is not null)
        {
            args["mediaSourceId"] = setSourceId;
            SetDtoValue(dto, "MediaSourceId", setSourceId);
        }

        if (audio && source.DefaultAudioStreamIndex.HasValue)
        {
            args["audioStreamIndex"] = source.DefaultAudioStreamIndex;
            SetDtoValue(dto, "AudioStreamIndex", source.DefaultAudioStreamIndex);
        }

        if (subtitle && source.DefaultSubtitleStreamIndex.HasValue)
        {
            args["subtitleStreamIndex"] = source.DefaultSubtitleStreamIndex;
            SetDtoValue(dto, "SubtitleStreamIndex", source.DefaultSubtitleStreamIndex);
        }
    }

    private SessionInfo? FindSession(string? deviceId, string? client, Guid userId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        foreach (var session in _sessionManager.Sessions ?? Enumerable.Empty<SessionInfo>())
        {
            if (session is not null
                && !string.IsNullOrEmpty(session.Id)
                && string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal)
                && (string.IsNullOrEmpty(client) || string.Equals(session.Client, client, StringComparison.OrdinalIgnoreCase))
                && (session.UserId == userId || session.UserId == Guid.Empty))
            {
                return session;
            }
        }

        return null;
    }

    /// <summary>Whether a play has actually started and still has a recent server-received progress report.</summary>
    /// <param name="entry">The session's play.</param>
    /// <param name="now">Current server time.</param>
    /// <returns>True only for a reported, recent play; preparing PlaybackInfo is not playback.</returns>
    public static bool IsActivePlay(PlayRecord? entry, DateTime now)
        => entry is { Playing: true, StartedAt: { } started, LastProgressAt: { } progress }
            && started <= progress && progress <= now && progress >= now - RepeatWindow;

    /// <summary>A paused play remains a repeat in replace-unless-manual mode, even after the progress window.</summary>
    public static bool IsRepeat(PlayRecord? entry, Guid itemId, AppModeKind mode, DateTime now)
        => entry is not null && entry.Playing && entry.ItemId == itemId
            && ((mode == AppModeKind.ReplaceUnlessManual && entry.StartedAt.HasValue) || entry.LastProgressAt >= now - RepeatWindow);

    /// <summary>Whether a served picker belongs to the new choice rather than the preceding autoplay transition: during autoplay, the item was
    /// served after the last stop and at least <see cref="PickerUseTime"/> before <paramref name="now"/>, by the latest or the previous serve.</summary>
    public static bool IsFreshPrePlayChoice(DecisionRecord? served, PlayRecord? entry, bool autoplay, DateTime now, bool switching = false)
        // While another item still plays, its last stop belongs to an older transition. A response may just be a prefetch.
        => served is not null && !switching && (!autoplay
            || (entry?.LastStoppedAt is { } lastStop && (UsableAfter(served.At, lastStop, now) || UsableAfter(served.PreviousServedAt, lastStop, now))));

    private static bool UsableAfter(DateTime? servedAt, DateTime lastStop, DateTime now)
        => servedAt > lastStop && servedAt <= now - PickerUseTime;

    private AppModeKind AppModeFor(PluginConfiguration config, string? client)
    {
        client = client?.Trim();
        if (string.IsNullOrEmpty(client) || client.Length > SettingsValidator.MaxClientLength)
        {
            return config.UnknownAppMode;
        }

        if (config.ConfigVersion > PluginConfiguration.CurrentVersion)
        {
            // A future schema is read-only until an explicit admin save, including discovery metadata.
            return config.FindAppMode(client)?.Mode ?? config.UnknownAppMode;
        }

        lock (ConfigLock)
        {
            var now = _registry.Now;
            if (!SeenMarks.TryGetValue(client, out var marked) || marked < now - SeenInterval)
            {
                foreach (var key in SeenMarks.Where(p => p.Value < now - SeenInterval).Select(p => p.Key).ToList())
                {
                    SeenMarks.TryRemove(key, out _);
                }

                while (SeenMarks.Count >= SettingsValidator.MaxAppModes)
                {
                    SeenMarks.TryRemove(SeenMarks.MinBy(p => p.Value).Key, out _);
                }

                SeenMarks[client] = now;
                try
                {
                    var save = DiscoverySaves.GetValue(config, _ => new DiscoverySave());
                    if (config.NoteAppSeen(client, now) && now - save.At >= SeenInterval)
                    {
                        // Client names are untrusted: bound writes across names, including failed attempts.
                        save.At = now;
                        // Discovery metadata must not invalidate decision caches or raise ConfigurationChanged.
                        Plugin.Instance?.SaveConfiguration();
                    }
                }
                catch (Exception ex)
                {
                    SeenMarks.TryRemove(client, out _);
                    _logger.LogWarning(ex, "[Babelfin] Couldn't save when the app {Client} was last seen", client);
                }
            }

            return config.FindAppMode(client)?.Mode ?? config.UnknownAppMode;
        }
    }

    private void AddTrustedEntry(User user, BaseItem item, string? client, int? audio, int? subtitle, int? choiceAudio, int? choiceSubtitle)
    {
        try
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AudioIndex"] = audio?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["SubtitleIndex"] = subtitle?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["BabelfinAudioIndex"] = choiceAudio?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["BabelfinSubtitleIndex"] = choiceSubtitle?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(client))
            {
                details["Client"] = client;
            }

            var app = string.IsNullOrWhiteSpace(client) ? "the app" : client;
            _activity.Add(new ActivityEntry(DateTime.UtcNow, ActivityKinds.ManualTrusted, user.Id, item.Id, "Kept the tracks " + app + " asked for on " + item.Name, details));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Babelfin] Couldn't write an activity entry");
        }
    }

    private void WriteCompatibility(User user, BaseItem item, int? audio, int? subtitle)
    {
        var compatibility = _compatibility;
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await compatibility.WriteAsync(user, item, audio, subtitle, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Babelfin] Compatibility mode couldn't write the remembered selection for {Item}", item.Name);
            }
        });
    }
}
