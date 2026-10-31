using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Playback;

/// <summary>What one start or progress report calls for, decided under the registry's lock and carried out after it.</summary>
/// <param name="SendAudio">The audio index to command, or null.</param>
/// <param name="SendSubtitle">The subtitle index to command (-1 = off), or null.</param>
/// <param name="Manual">Whether the report is a manual change to remember.</param>
/// <param name="ManualAudio">The audio index now playing (for the recorder).</param>
/// <param name="ManualSubtitle">The subtitle index now playing (for the recorder).</param>
/// <param name="PreviousAudio">The audio index before the change.</param>
/// <param name="PreviousSubtitle">The subtitle index before the change.</param>
public readonly record struct ReportPlan(
    int? SendAudio,
    int? SendSubtitle,
    bool Manual,
    int? ManualAudio,
    int? ManualSubtitle,
    int? PreviousAudio,
    int? PreviousSubtitle)
{
    /// <summary>Gets a value indicating whether a track-switch command is to be sent.</summary>
    public bool HasCommands => SendAudio.HasValue || SendSubtitle.HasValue;
}

/// <summary>
/// Watches Jellyfin's playback events: keeps each session's play in the <see cref="PlayRegistry"/>,
/// corrects each stream whose first reported index differs from the expected track (once, when the app accepts the commands), and turns a
/// track change made more than <see cref="PluginConfiguration.ManualChangeAfterSeconds"/> after start into a remembered override.
/// Cheap to construct (events are subscribed in <see cref="StartAsync"/>); every handler catches and logs; nothing here ever
/// stops or delays playback.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item>The start report counts as the play's first report: correction is considered once for each stream, when its first index arrives,
/// and that index becomes its baseline for manual-change detection.</item>
/// <item>A null index in a report means "not reported" (some apps never send one) and is never read as a change or as "off";
/// a negative subtitle index is "off".</item>
/// <item>A report that shows a commanded index clears the command, so a later change back to it by hand is manual.</item>
/// <item>A stop for another item than the one the session's entry holds (the next item's PlaybackInfo arrived first) only notes
/// the stop; the new play is kept.</item>
/// <item>In a dry run, the registry is kept up to date but nothing is corrected or remembered; with Babelfin switched off, the
/// monitor does nothing. A test host without a plugin instance runs with the default settings.</item>
/// <item>Babelfin's choice for a play that never went through PlaybackInfo comes from the decision cache when it is recent,
/// else from one <c>GetStaticMediaSources</c> call (noted as Babelfin's own decision, so it never counts as "served").</item>
/// </list>
/// </remarks>
public sealed class PlaybackMonitor : IHostedService
{
    private static readonly PluginConfiguration DefaultConfiguration = new();

    private readonly ISessionManager _sessionManager;
    private readonly PlayRegistry _registry;
    private readonly TrackChangeRecorder _recorder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IActivityLog _activity;
    private readonly ILogger<PlaybackMonitor> _logger;
    private readonly DecoratorStats? _stats;
    private int _subscribed;

    /// <summary>Initializes a new instance of the <see cref="PlaybackMonitor"/> class. Resolves nothing and subscribes to nothing.</summary>
    /// <param name="sessionManager">Jellyfin's sessions.</param>
    /// <param name="registry">The play registry.</param>
    /// <param name="recorder">Remembers manual changes.</param>
    /// <param name="mediaSourceManager">The media source manager (Babelfin's decorator when wrapped).</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="activity">Babelfin's activity log.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="stats">Babelfin's recent decisions (optional: without them the choice is always recomputed).</param>
    public PlaybackMonitor(
        ISessionManager sessionManager,
        PlayRegistry registry,
        TrackChangeRecorder recorder,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IActivityLog activity,
        ILogger<PlaybackMonitor> logger,
        DecoratorStats? stats = null)
    {
        _sessionManager = sessionManager;
        _registry = registry;
        _recorder = recorder;
        _mediaSourceManager = mediaSourceManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _activity = activity;
        _logger = logger;
        _stats = stats;
    }

    /// <summary>
    /// Decides what a start or progress report calls for and updates the entry (pure apart from the entry; runs under the
    /// registry's lock): correction for each first reported stream index, plus manual-change detection after the window.
    /// </summary>
    /// <param name="r">The play's entry (changed in place).</param>
    /// <param name="audio">The reported audio index (null = not reported).</param>
    /// <param name="subtitle">The reported subtitle index (null = not reported, negative = off).</param>
    /// <param name="now">The time of the report (UTC).</param>
    /// <param name="correct">The correction setting.</param>
    /// <param name="manualAfterSeconds">Seconds after start from which a change counts as manual.</param>
    /// <param name="dryRun">Whether Babelfin only logs.</param>
    /// <param name="supportedCommands">The commands the app advertises (null or empty = unknown).</param>
    /// <returns>The plan.</returns>
    public static ReportPlan Evaluate(
        PlayRecord r,
        int? audio,
        int? subtitle,
        DateTime now,
        CorrectAfterStartMode correct,
        int manualAfterSeconds,
        bool dryRun,
        IReadOnlyList<GeneralCommandType>? supportedCommands)
    {
        ArgumentNullException.ThrowIfNull(r);
        r.StartedAt ??= now;
        r.Playing = true;
        r.LastProgressAt = now;
        r.ExpireCommands(now);
        // Audio can be left to Jellyfin and can change in the same report as a subtitle reset.
        r.ObserveAudio(audio, now);
        // Even indexless reports must notice a new target, so an older acknowledgement
        // cannot survive a target change and later make its first application look manual.
        SyncExpectedTargets(r);
        if (!audio.HasValue && !subtitle.HasValue)
        {
            return default;
        }

        int? sendAudio = null, sendSubtitle = null;
        // Start one shared correction window at the first indexed report. Indexless starts
        // may take up to a minute to become useful; they cannot open a window later in playback.
        r.FirstIndexedAt ??= now;
        var firstAudio = audio.HasValue && !r.LastReportedAudio.HasValue;
        var firstSubtitle = subtitle.HasValue && !r.LastReportedSubtitle.HasValue;
        var anchor = r.FirstIndexedAt.Value;
        var withinStartup = now >= anchor && now - anchor <= TimeSpan.FromSeconds(30)
            && anchor >= r.StartedAt.Value && anchor - r.StartedAt.Value <= TimeSpan.FromSeconds(60);
        if ((firstAudio || firstSubtitle) && withinStartup)
        {
            if (correct != CorrectAfterStartMode.Off && !dryRun && r.HasExpected && !r.Trusted && !r.ManualChanged)
            {
                var unknown = supportedCommands is null || supportedCommands.Count == 0;
                bool CanSend(GeneralCommandType type) => (supportedCommands?.Contains(type) ?? false) || (correct == CorrectAfterStartMode.On && unknown);

                if (firstAudio && r.ExpectedAudio.HasValue && audio != r.ExpectedAudio && CanSend(GeneralCommandType.SetAudioStreamIndex))
                {
                    sendAudio = r.ExpectedAudio;
                    r.CommandedAudio = sendAudio;
                    r.AudioCommandSentAt = now;
                }

                if (firstSubtitle && r.ExpectedSubtitle.HasValue && Sub(subtitle) != Sub(r.ExpectedSubtitle) && CanSend(GeneralCommandType.SetSubtitleStreamIndex))
                {
                    sendSubtitle = Sub(r.ExpectedSubtitle);
                    r.CommandedSubtitle = sendSubtitle;
                    r.SubtitleCommandSentAt = now;
                }

                if (sendAudio.HasValue || sendSubtitle.HasValue)
                {
                    r.CorrectionSentAt = now;
                }
            }
        }

        var manual = false;
        int? previousAudio = null, previousSubtitle = null;
        if (r.HasReport && !dryRun
            && r.StartedAt is { } started && (now - started).TotalSeconds > Math.Max(0, manualAfterSeconds))
        {
            previousAudio = r.LastReportedAudio;
            previousSubtitle = r.LastReportedSubtitle;
            // Only a track Babelfin chose is remembered when changed by hand; a track left to the video is Jellyfin's (its own
            // "remember selections" handles it), so Babelfin never starts choosing for someone who didn't ask it to.
            // A delayed acknowledgement of tracks already requested through PlaybackInfo is no more
            // a manual change than acknowledgement of a correction command. Elapsed time cannot tell
            // us who selected those tracks. Expected indices advance with real changes, so returning
            // to an earlier choice after such a change is still remembered.
            var audioChanged = !sendAudio.HasValue && r.ChoiceAudio.HasValue && audio.HasValue && (previousAudio.HasValue || (firstAudio && !withinStartup)) && audio != previousAudio
                && (audio != r.ExpectedAudio || r.ExpectedAudioAcknowledged)
                && (r.ManualAudioChanged || audio != r.ChoiceAudio || r.ChoiceAudioAcknowledged)
                && !r.IsAudioCommandEcho(audio, now);
            var subtitleChanged = !sendSubtitle.HasValue && r.ChoiceSubtitle.HasValue && subtitle.HasValue && (previousSubtitle.HasValue || (firstSubtitle && !withinStartup)) && Sub(subtitle) != Sub(previousSubtitle)
                && !(r.ExpectedSubtitle.HasValue && Sub(subtitle) == Sub(r.ExpectedSubtitle) && !r.ExpectedSubtitleAcknowledged)
                && (r.ManualSubtitleChanged || !(r.ChoiceSubtitle.HasValue && Sub(subtitle) == Sub(r.ChoiceSubtitle) && !r.ChoiceSubtitleAcknowledged))
                && !r.IsSubtitleCommandEcho(subtitle, now)
                && !r.IsSubtitleReset(subtitle, now);
            if (audioChanged || subtitleChanged)
            {
                manual = true;
                r.ManualChanged = true;
                if (!audioChanged)
                {
                    previousAudio = audio ?? previousAudio;
                }

                if (!subtitleChanged)
                {
                    previousSubtitle = subtitle ?? previousSubtitle;
                }

                r.HasExpected = true;
                if (audioChanged)
                {
                    r.ExpectedAudio = audio;
                    r.ManualAudioChanged = true;
                    r.ManualAudioRevision++;
                }
                if (subtitleChanged)
                {
                    r.ExpectedSubtitle = subtitle;
                    r.ManualSubtitleChanged = true;
                    r.ManualSubtitleRevision++;
                    r.LastSubtitleChangedAt = now;
                }
            }
        }

        // With no prepared PlaybackInfo, the first app report is only a baseline, not
        // evidence of a manual choice. Advance it when Babelfin's deferred choice arrives.
        if (audio.HasValue && audio == r.ChoiceAudio && !r.ChoiceAudioAcknowledged && sendAudio is null)
            r.ExpectedAudio = audio;
        if (subtitle.HasValue && r.ChoiceSubtitle.HasValue && Sub(subtitle) == Sub(r.ChoiceSubtitle)
            && !r.ChoiceSubtitleAcknowledged && sendSubtitle is null)
            r.ExpectedSubtitle = subtitle;

        // A command that took effect is spent: a later change back to it by hand counts.
        if (audio.HasValue && sendAudio is null && audio == r.CommandedAudio)
        {
            r.ClearAudioCommand();
        }

        if (subtitle.HasValue && sendSubtitle is null && r.CommandedSubtitle.HasValue && Sub(subtitle) == Sub(r.CommandedSubtitle))
        {
            r.ClearSubtitleCommand();
        }

        SyncExpectedTargets(r);
        r.ExpectedAudioAcknowledged |= audio.HasValue && audio == r.ExpectedAudio;
        r.ExpectedSubtitleAcknowledged |= subtitle.HasValue && r.ExpectedSubtitle.HasValue && Sub(subtitle) == Sub(r.ExpectedSubtitle);
        r.ChoiceAudioAcknowledged |= audio.HasValue && audio == r.ChoiceAudio;
        r.ChoiceSubtitleAcknowledged |= subtitle.HasValue && r.ChoiceSubtitle.HasValue && Sub(subtitle) == Sub(r.ChoiceSubtitle);
        r.LastReportedAudio = audio ?? r.LastReportedAudio;
        r.LastReportedSubtitle = subtitle ?? r.LastReportedSubtitle;
        r.HasReport = true;
        return new ReportPlan(sendAudio, sendSubtitle, manual, audio ?? previousAudio, subtitle ?? previousSubtitle, previousAudio, previousSubtitle);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _sessionManager.SessionEnded += OnSessionEnded;
            _logger.LogDebug("[Babelfin] Playback monitor started");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _sessionManager.SessionEnded -= OnSessionEnded;
        }

        return Task.CompletedTask;
    }

    private static void SyncExpectedTargets(PlayRecord r)
    {
        if (r.ReportExpectedAudio != r.ExpectedAudio)
        {
            r.ReportExpectedAudio = r.ExpectedAudio;
            r.ExpectedAudioAcknowledged = false;
        }

        var subtitle = r.ExpectedSubtitle.HasValue ? (int?)Sub(r.ExpectedSubtitle) : null;
        if (r.ReportExpectedSubtitle != subtitle)
        {
            r.ReportExpectedSubtitle = subtitle;
            r.ExpectedSubtitleAcknowledged = false;
        }
        if (r.ReportChoiceAudio != r.ChoiceAudio)
        {
            r.ReportChoiceAudio = r.ChoiceAudio;
            r.ManualAudioChanged = false;
            r.ChoiceAudioAcknowledged = false;
        }

        var choiceSubtitle = r.ChoiceSubtitle.HasValue ? (int?)Sub(r.ChoiceSubtitle) : null;
        if (r.ReportChoiceSubtitle != choiceSubtitle)
        {
            r.ReportChoiceSubtitle = choiceSubtitle;
            r.ManualSubtitleChanged = false;
            r.ChoiceSubtitleAcknowledged = false;
        }
    }

    private static int Sub(int? subtitle) => subtitle is { } s && s >= 0 ? s : -1;

    private static bool Differs(int? audio, int? subtitle, int? refAudio, int? refSubtitle)
        => (audio.HasValue && audio != refAudio) || (subtitle.HasValue && Sub(subtitle) != Sub(refSubtitle));

    private static PluginConfiguration Config() => Plugin.Instance?.Configuration ?? DefaultConfiguration;

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            HandleReport(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't follow a playback start");
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            HandleReport(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't follow a playback progress report");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var session = e?.Session;
            if (session is null || string.IsNullOrEmpty(session.Id) || !Config().Enabled)
            {
                return;
            }

            var now = _registry.Now;
            var stopped = e!.Item?.Id ?? session.FullNowPlayingItem?.Id;
            _registry.Update(session.Id, r =>
            {
                var itemId = stopped ?? r.ItemId;
                r.LastStoppedItemId = itemId;
                r.LastStoppedAt = now;
                r.Client ??= session.Client;
                r.DeviceId ??= session.DeviceId;
                r.DeviceName ??= session.DeviceName;
                if (r.UserId == Guid.Empty)
                {
                    r.UserId = session.UserId;
                }

                // A different item may already have prepared expectations in this entry: keep them.
                if (r.ItemId is null || itemId is null || r.ItemId == itemId)
                {
                    r.ResetPlay();
                    r.ItemId = itemId ?? r.ItemId;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't follow a playback stop");
        }
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        try
        {
            if (e?.SessionInfo?.Id is { Length: > 0 } id)
            {
                _registry.Remove(id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't follow a session end");
        }
    }

    private void HandleReport(PlaybackProgressEventArgs? e)
    {
        var config = Config();
        var session = e?.Session;
        if (e is null || session is null || string.IsNullOrEmpty(session.Id) || !config.Enabled)
        {
            return;
        }

        var item = e.Item ?? session.FullNowPlayingItem;
        if (item is null && session.NowPlayingItem is { } dto && dto.Id != Guid.Empty)
        {
            item = _libraryManager.GetItemById(dto.Id);
        }

        if (item is null || !BabelfinMediaSourceManager.CanChooseTracks(item))
        {
            return;
        }

        // Most reports only update the registry. A database entity is needed only for a fresh choice or a manual change.
        var user = e.Users?.FirstOrDefault(u => u is not null);
        var userId = user?.Id ?? session.UserId;
        if (userId == Guid.Empty)
        {
            return;
        }

        var sourceId = !string.IsNullOrEmpty(e.MediaSourceId) ? e.MediaSourceId : session.PlayState?.MediaSourceId;
        var audio = session.PlayState?.AudioStreamIndex;
        var subtitle = session.PlayState?.SubtitleStreamIndex;

        // 1. The session's entry holds this play: the one it already has, a pending one from PlaybackInfo, or a new one.
        var current = _registry.Get(session.Id);
        var adopted = current is not null && current.UserId == userId && current.Playing && current.ItemId == item.Id
            ? null
            : _registry.Adopt(session.Id, session.DeviceId, session.Client, item.Id, userId);
        var resolveChoice = false;
        var entry = _registry.Update(session.Id, r =>
        {
            var sourceChanged = sourceId is not null && (r.MediaSourceId is not null || r.ChoiceLookupAttempted)
                && !string.Equals(sourceId, r.MediaSourceId, StringComparison.OrdinalIgnoreCase);
            if (sourceChanged || (adopted is null && (r.UserId != userId || r.ItemId != item.Id || (!r.Playing && !r.HasExpected))))
            {
                r.ResetPlay();
                r.Playing = true;
            }

            r.ItemId = item.Id;
            r.ItemName = item.Name;
            r.UserId = userId;
            r.Client = session.Client ?? r.Client;
            r.DeviceId = session.DeviceId ?? r.DeviceId;
            r.DeviceName = session.DeviceName ?? r.DeviceName;
            r.MediaSourceId = sourceId ?? r.MediaSourceId;
            if (!r.HasExpected && !r.ChoiceLookupAttempted && !config.DryRun)
            {
                // Reserve even a failed/empty attempt under the registry lock: progress is not a retry
                // loop. A new play/source resets it; PlaybackInfo can still supply expected tracks later.
                r.ChoiceLookupAttempted = true;
                resolveChoice = true;
            }
        });

        // 2. No expected tracks yet (the app never asked PlaybackInfo with this session): Babelfin's choice, or the app's own.
        if (resolveChoice)
        {
            user ??= _userManager.GetUserById(userId);
            if (user is null) return;
            var choice = Choice(user, item, entry.MediaSourceId, config);
            if (choice.Found)
            {
                var reported = audio.HasValue || subtitle.HasValue;
                var own = reported && Differs(audio, subtitle, choice.Audio, choice.Subtitle);
                _registry.UpdateExisting(session.Id, r =>
                {
                    if (r.PlayId == entry.PlayId && !r.HasExpected)
                    {
                        r.HasExpected = true;
                        r.ChoiceAudio = choice.Audio;
                        r.ChoiceSubtitle = choice.Subtitle;
                        r.ExpectedAudio = own ? audio ?? choice.Audio : choice.Audio;
                        r.ExpectedSubtitle = own ? subtitle ?? choice.Subtitle : choice.Subtitle;
                        r.Trusted |= own;
                    }
                });
            }
        }

        // 3. The report itself.
        var plan = default(ReportPlan);
        ManualChangeOrder? order = null;
        var capabilities = session.Capabilities?.SupportedCommands;
        var now = _registry.Now;
        var after = _registry.UpdateExisting(session.Id, r =>
        {
            if (r.PlayId == entry.PlayId)
            {
                var audioRevision = r.ManualAudioRevision;
                var subtitleRevision = r.ManualSubtitleRevision;
                plan = Evaluate(r, audio, subtitle, now, config.CorrectAfterStart, config.ManualChangeAfterSeconds, config.DryRun, capabilities);
                if (plan.Manual)
                {
                    order = ManualChangeOrder.Next();
                    if (r.ManualAudioRevision != audioRevision) r.ManualAudioOrder = order;
                    if (r.ManualSubtitleRevision != subtitleRevision) r.ManualSubtitleOrder = order;
                }
            }
        });
        if (after is null || after.PlayId != entry.PlayId) return;

        if (plan.HasCommands)
        {
            SendCorrection(session, userId, item, after, plan);
        }

        if (plan.Manual)
        {
            user ??= _userManager.GetUserById(userId);
            if (user is not null) RecordManual(session, user, item, after, plan, order);
        }
    }

    private (bool Found, int? Audio, int? Subtitle) Choice(User user, BaseItem item, string? sourceId, PluginConfiguration config)
    {
        var id = string.IsNullOrEmpty(sourceId) ? item.Id.ToString("N") : sourceId;
        if (_stats?.GetDecision(user.Id, item.Id, id) is { Decision: { } decision } record
            && record.Outcome == DecisionOutcome.Applied
            && record.At >= _registry.Now.AddMinutes(-Math.Max(1, config.ServedWindowMinutes)))
        {
            // A track left to the video has no expected index: the app's choice stands and is never corrected.
            return (true, decision.AudioIndex, decision.SubtitleIndex);
        }

        var source = Source(user, item, sourceId);
        var own = source is null || _stats is null ? null : _stats.GetDecision(user.Id, item.Id, source.Id)?.Decision;
        if (source is null || (_stats is not null && own is null))
        {
            // Babelfin had nothing to say for this play.
            return (false, null, null);
        }

        return (true, own is not null && own.AudioIndex is null ? null : source.DefaultAudioStreamIndex, own is not null && own.SubtitleIndex is null ? null : source.DefaultSubtitleStreamIndex);
    }

    private MediaSourceInfo? Source(User user, BaseItem item, string? sourceId)
    {
        var sources = _mediaSourceManager.GetStaticMediaSources(item, true, user);
        if (sources is null || sources.Count == 0)
        {
            return null;
        }

        return string.IsNullOrEmpty(sourceId)
            ? (sources.Count == 1 ? sources[0] : null)
            : sources.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
    }

    private void SendCorrection(SessionInfo session, Guid userId, BaseItem item, PlayRecord entry, ReportPlan plan)
    {
        if (plan.SendAudio is { } audio)
        {
            _ = SendAsync(session.Id, userId, GeneralCommandType.SetAudioStreamIndex, audio, item.Name);
        }

        if (plan.SendSubtitle is { } subtitle)
        {
            _ = SendAsync(session.Id, userId, GeneralCommandType.SetSubtitleStreamIndex, subtitle, item.Name);
        }

        try
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ReportedAudioIndex"] = entry.LastReportedAudio?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["ReportedSubtitleIndex"] = entry.LastReportedSubtitle?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            };
            if (plan.SendAudio.HasValue)
            {
                details["AudioIndex"] = plan.SendAudio.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (plan.SendSubtitle.HasValue)
            {
                details["SubtitleIndex"] = plan.SendSubtitle.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(session.Client))
            {
                details["Client"] = session.Client;
            }

            var app = string.IsNullOrWhiteSpace(session.Client) ? "the app" : session.Client;
            _activity.Add(new ActivityEntry(_registry.Now, ActivityKinds.CorrectionSent, userId, item.Id, "Switched " + item.Name + " to the chosen tracks in " + app, details));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Babelfin] Couldn't write an activity entry");
        }

        _logger.LogInformation(
            "[Babelfin] {Client} started {Item} with {Audio}/{Subtitle}; asked it to switch to {NewAudio}/{NewSubtitle}",
            session.Client,
            item.Name,
            entry.LastReportedAudio,
            entry.LastReportedSubtitle,
            plan.SendAudio,
            plan.SendSubtitle);
    }

    private async Task SendAsync(string sessionId, Guid userId, GeneralCommandType type, int index, string? itemName)
    {
        try
        {
            var command = new GeneralCommand
            {
                Name = type,
                ControllingUserId = userId,
                Arguments = { ["Index"] = index.ToString(CultureInfo.InvariantCulture) },
            };
            await _sessionManager.SendGeneralCommand(sessionId, sessionId, command, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Babelfin] Couldn't send {Command} for {Item}", type, itemName);
        }
    }

    private void RecordManual(SessionInfo session, User user, BaseItem item, PlayRecord entry, ReportPlan plan, ManualChangeOrder? order)
    {
        var source = Source(user, item, entry.MediaSourceId);
        if (source is null)
        {
            _logger.LogDebug("[Babelfin] A track change on {Item} has no single media source to describe; not remembered", item.Name);
            return;
        }

        _recorder.Record(user, item, source, plan.ManualAudio, plan.ManualSubtitle, plan.PreviousAudio, plan.PreviousSubtitle, session.Client, order);
    }
}
