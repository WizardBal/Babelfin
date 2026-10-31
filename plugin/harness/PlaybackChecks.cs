// PlaybackInfo modes, the play registry, the playback monitor and the change recorder, driven through the
// Kestrel host with a fake ISessionManager. Self-contained: Program.cs calls PlaybackChecks.Register(builder.Services) in StartAsync
// and PlaybackChecks.Run(...) once with a started host.
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Settings;
using Babelfin.Engine.Tests.Settings;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using BfPlugin = Jellyfin.Plugin.Babelfin.Plugin;

/// <summary>Playback behavior exercised through the Kestrel host.</summary>
public static class PlaybackChecks
{
    /// <summary>The header carrying the client name claim (Jellyfin-Client) for a fake-authenticated request.</summary>
    public const string ClientHeader = "X-Fake-Client";

    /// <summary>The header carrying the device id claim (Jellyfin-DeviceId) for a fake-authenticated request.</summary>
    public const string DeviceHeader = "X-Fake-Device";

    /// <summary>
    /// Adds what group H needs to a host: a <see cref="FakeSessionManager"/> as <see cref="ISessionManager"/> (one per host), a claims
    /// transformation that turns the X-Fake-Client / X-Fake-Device headers into Jellyfin's client and device claims, and stand-ins
    /// for the activity log and the compatibility writer when nothing else registered them. Call it from StartAsync, after the
    /// plugin's own registrations.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection Register(IServiceCollection services)
    {
        services.RemoveAll<ISessionManager>();
        services.AddSingleton<FakeSessionManager>();
        services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<FakeSessionManager>());
        services.AddHttpContextAccessor();
        services.AddTransient<IClaimsTransformation, FakeClientClaims>();
        services.TryAddSingleton<IActivityLog, HarnessActivityLog>();
        services.TryAddSingleton<ICompatibilityWriter, HarnessCompatibilityWriter>();
        return services;
    }

    /// <summary>
    /// Runs group H against a started host built with <see cref="Register"/>.
    /// </summary>
    /// <param name="app">The started host.</param>
    /// <param name="http">A client for it.</param>
    /// <param name="user">A person who can see both episodes (their document is restored at the end).</param>
    /// <param name="episode">An episode of a series with the dual-audio streams (1 eng / 2 jpn audio, 3 forced eng / 4 eng subtitles).</param>
    /// <param name="otherEpisode">Another episode with the same streams (the "next item" of the autoplay check).</param>
    /// <param name="check">Prints PASS/FAIL.</param>
    /// <returns>A task.</returns>
    public static async Task Run(WebApplication app, HttpClient http, User user, BaseItem episode, BaseItem otherEpisode, Action<bool, string> check)
    {
        Console.WriteLine("-- H. Playback handling --");
        var plugin = BfPlugin.Instance;
        if (plugin is null)
        {
            check(false, "H: a plugin instance exists");
            return;
        }

        var sp = app.Services;
        var sessions = sp.GetService<ISessionManager>() as FakeSessionManager;
        var playRegistry = sp.GetService<IPlayRegistry>();
        var registry = sp.GetService<PlayRegistry>() ?? playRegistry as PlayRegistry;
        var monitorRunning = sp.GetServices<IHostedService>().Any(s => s is PlaybackMonitor);
        check(sessions is not null && playRegistry is not null && registry is not null && monitorRunning,
            $"H: services registered (fake session manager {sessions is not null}, IPlayRegistry {playRegistry is not null}, PlayRegistry {registry is not null}, PlaybackMonitor hosted {monitorRunning})");
        if (sessions is null || playRegistry is null || registry is null)
        {
            return;
        }

        var msm = sp.GetRequiredService<IMediaSourceManager>();
        var store = sp.GetRequiredService<IPersonStore>();
        var original = plugin.Configuration;
        var seriesId = (episode as Episode)?.SeriesId ?? episode.Id;
        var otherSeriesId = (otherEpisode as Episode)?.SeriesId ?? otherEpisode.Id;

        PluginConfiguration Config(Action<PluginConfiguration>? more = null)
        {
            var cfg = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion };
            cfg.Everyday.AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "jpn"), new AudioStepSetting(AudioStepKind.FileDefault) };
            cfg.Everyday.SubtitleLanguages = new[] { "eng" };
            cfg.Everyday.WhenAudioUnderstood = SubtitleWant.Full;
            cfg.Everyday.WhenAudioNotUnderstood = SubtitleWant.Full;
            more?.Invoke(cfg);
            plugin.UpdateConfiguration(cfg);
            return cfg;
        }

        string Served(BaseItem item, SessionInfo? app = null)
        {
            var s = msm.GetStaticMediaSources(item, true, user)[0];
            // Only an identified app response is eligible as evidence of that app's picker.
            if (app is not null) sp.GetRequiredService<Jellyfin.Plugin.Babelfin.Library.DecoratorStats>()
                .RecordResponse(user.Id, s, app.DeviceId, app.Client);
            return $"{s.DefaultAudioStreamIndex}/{s.DefaultSubtitleStreamIndex}";
        }

        SessionInfo NewSession(string client, params GeneralCommandType[] commands)
        {
            var session = new SessionInfo(sessions, NullLogger.Instance)
            {
                Id = Guid.NewGuid().ToString("N"),
                DeviceId = "harness-" + Guid.NewGuid().ToString("N")[..8],
                DeviceName = "Harness device",
                Client = client,
                UserId = user.Id,
                Capabilities = new ClientCapabilities { SupportedCommands = commands },
                PlayState = new PlayerStateInfo(),
            };
            sessions.Add(session);
            return session;
        }

        async Task<string> Post(BaseItem item, string query, string client, string device)
        {
            using var m = new HttpRequestMessage(HttpMethod.Post, $"/Items/{item.Id:N}/PlaybackInfo{query}");
            m.Headers.TryAddWithoutValidation("X-Fake-User", user.Id.ToString("N"));
            m.Headers.TryAddWithoutValidation(ClientHeader, client);
            m.Headers.TryAddWithoutValidation(DeviceHeader, device);
            m.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(m);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                return $"HTTP {(int)res.StatusCode}";
            }

            var e = JsonDocument.Parse(text).RootElement;
            string N(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32().ToString() : string.Empty;
            string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            return $"{N("audioStreamIndex")}/{N("subtitleStreamIndex")} source={S("mediaSourceId")}";
        }

        PlayEntry? EntryFor(string sessionId) => playRegistry.Snapshot().FirstOrDefault(p => p.SessionId == sessionId);
        ManualMemory? Manual() => store.TryGet(user.Id)?.FindOverride(seriesId)?.Manual;
        void Backdate(SessionInfo s, int seconds) => registry.UpdateExisting(s.Id, r => r.StartedAt = r.StartedAt?.AddSeconds(-seconds));

        try
        {
            Config();
            var choice = Served(episode);
            var otherChoice = Served(otherEpisode);
            check(choice == "2/4" && otherChoice == "2/4", $"H setup: Babelfin chooses 2/4 (Japanese, full English) for both episodes ({choice}, {otherChoice})");
            var snapshot = store.TryGet(user.Id)?.Clone() ?? new PersonSettings { UserId = user.Id };
            snapshot.Overrides.RemoveAll(o => PersonSettings.SameId(o.ItemId, seriesId) && o.Manual is not null && o.Settings.IsEmpty);
            void Restore() => store.Save(snapshot);
            Restore();

            // (1) No indices: expected tracks recorded; a request before the session exists is adopted by its first start.
            var s1 = NewSession("Jellyfin Web");
            var r1 = await Post(episode, string.Empty, "Jellyfin Web", s1.DeviceId);
            var e1 = EntryFor(s1.Id);
            check(r1.StartsWith("/ ", StringComparison.Ordinal) && e1 is { ExpectedAudio: 2, ExpectedSubtitle: 4, Trusted: false } && e1.ItemId == episode.Id,
                $"H1 PlaybackInfo without indices: request untouched ({r1}), the session's expected tracks are 2/4 ({e1?.ExpectedAudio}/{e1?.ExpectedSubtitle})");
            var lateDevice = "harness-late-" + Guid.NewGuid().ToString("N")[..6];
            await Post(episode, string.Empty, "Jellyfin Web", lateDevice);
            var pending = playRegistry.Snapshot().Any(p => p.SessionId.Length == 0 && p.ItemId == episode.Id && p.ExpectedAudio == 2);
            var late = NewSession("Jellyfin Web");
            late.DeviceId = lateDevice;
            sessions.RaiseStart(late, episode, user, 2, 4);
            var adopted = EntryFor(late.Id);
            check(pending && adopted is { ExpectedAudio: 2, ExpectedSubtitle: 4 } && !playRegistry.Snapshot().Any(p => p.SessionId.Length == 0 && p.ItemId == episode.Id),
                $"H1 a PlaybackInfo before the session exists is kept by device|client|item and adopted on the first start (pending {pending}, adopted {adopted?.ExpectedAudio}/{adopted?.ExpectedSubtitle})");

            // (2) Explicit indices equal to the served choice: untouched, not manual.
            var s2 = NewSession("Jellyfin Web");
            Served(episode, s2);
            var r2 = await Post(episode, "?audioStreamIndex=2&subtitleStreamIndex=4", "Jellyfin Web", s2.DeviceId);
            check(r2.StartsWith("2/4", StringComparison.Ordinal) && Manual() is null && EntryFor(s2.Id) is { Trusted: false, ManualChanged: false },
                $"H2 indices equal to the served choice: untouched ({r2}), nothing remembered, not trusted");

            // (3) Explicit indices that differ from the served choice: trusted and remembered for the series.
            var s3 = NewSession("Jellyfin Web");
            Served(episode, s3);
            var r3 = await Post(episode, "?audioStreamIndex=1&subtitleStreamIndex=3", "Jellyfin Web", s3.DeviceId);
            var m3 = Manual();
            var o3 = store.TryGet(user.Id)?.FindOverride(seriesId);
            check(r3.StartsWith("1/3", StringComparison.Ordinal) && m3?.Audio?.Language == "en" && m3.Subtitle?.Kind == "partial" && o3?.Scope == OverrideScope.Series
                && EntryFor(s3.Id) is { Trusted: true, ManualChanged: true },
                $"H3 indices differing from the served choice: kept ({r3}) and remembered for the series (audio {m3?.Audio?.Language} {m3?.Audio?.Kind}, subtitles {m3?.Subtitle?.Language} {m3?.Subtitle?.Kind}, scope {o3?.Scope})");
            Restore();

            var otherDevice = NewSession("Jellyfin Web");
            Served(episode, otherDevice);
            var unservedDevice = NewSession("Jellyfin Web");
            var isolated = await Post(episode, "?audioStreamIndex=1&subtitleStreamIndex=3", "Jellyfin Web", unservedDevice.DeviceId);
            check(isolated.StartsWith("1/3", StringComparison.Ordinal) && Manual() is null && EntryFor(unservedDevice.Id) is { ManualChanged: false },
                "A picker served to another device never becomes this device's remembered show choice");
            Restore();

            // (4) Jellyfin Web replaces on autoplay: the previous item stopped within the window and the copied tracks differ.
            var s4 = NewSession("Jellyfin Web");
            sessions.RaiseStopped(s4, episode, user);
            var r4 = await Post(otherEpisode, "?audioStreamIndex=1&subtitleStreamIndex=3", "Jellyfin Web", s4.DeviceId);
            check(r4 == $"2/4 source={otherEpisode.Id:N}" && Manual() is null,
                $"H4 Jellyfin Web, the session's previous item stopped just now, English copied where Babelfin chose Japanese: replaced with 2/4 and the source id ({r4})");
            // The next item is asked for while the session still plays the previous one (the stop report hasn't arrived yet).
            var s4b = NewSession("Jellyfin Web");
            sessions.RaiseStart(s4b, episode, user, 2, 4);
            var r4b = await Post(otherEpisode, "?audioStreamIndex=1&subtitleStreamIndex=3", "Jellyfin Web", s4b.DeviceId);
            check(r4b == $"2/4 source={otherEpisode.Id:N}" && Manual() is null,
                $"H4 Jellyfin Web asks for the next episode before reporting the previous one stopped: copied tracks replaced with 2/4 ({r4b})");
            Restore();
            // The other episode was served audio 2 and subtitle 4; a differing request within the served window is a manual choice.
            var s5 = NewSession("Jellyfin Web");
            Served(otherEpisode, s5);
            var r5 = await Post(otherEpisode, "?audioStreamIndex=1&subtitleStreamIndex=3", "Jellyfin Web", s5.DeviceId);
            var rememberedOther = store.TryGet(user.Id)?.Overrides.Any(o => o.Manual is not null && PersonSettings.SameId(o.ItemId, otherSeriesId)) == true;
            check(r5.StartsWith("1/3 source=", StringComparison.Ordinal) && rememberedOther && EntryFor(s5.Id) is { Trusted: true },
                $"H4 the same request with no previous stop differs from the served 2/4: a manual choice, kept ({r5}) and remembered for its show");
            Restore();
            var s6 = NewSession("Jellyfin Web");
            sessions.RaiseStopped(s6, episode, user);
            registry.UpdateExisting(s6.Id, r => r.LastStoppedAt = r.LastStoppedAt?.AddSeconds(-121));
            var r6 = await Post(otherEpisode, "?audioStreamIndex=1&subtitleStreamIndex=3", "Jellyfin Web", s6.DeviceId);
            check(r6.StartsWith("1/3 source=", StringComparison.Ordinal), $"H4 a stop 121 s ago is outside the autoplay window: not replaced ({r6})");
            Restore();
            var s6b = NewSession("Android TV");
            var r6b = await Post(otherEpisode, "?audioStreamIndex=1&subtitleStreamIndex=3", "Android TV", s6b.DeviceId);
            check(r6b == $"2/4 source={otherEpisode.Id:N}", $"H4 Android TV (replace unless manual), nothing served: replaced ({r6b})");

            // (5) An unknown client gets a Seen row with the mode for unknown apps.
            var stranger = "Harness Player " + Guid.NewGuid().ToString("N")[..6];
            await Post(episode, "?audioStreamIndex=1&subtitleStreamIndex=3", stranger, "harness-stranger");
            var row = plugin.Configuration.FindAppMode(stranger);
            check(row is { Seen: true, LastSeen: not null } && row.Mode == plugin.Configuration.UnknownAppMode,
                $"H5 an unknown client is added to the per-app table as seen ({row?.Client}, {row?.Mode}, seen {row?.Seen}, last {row?.LastSeen:O})");
            Restore();

            // (6) A subtitle change in a progress report after the window is remembered as "subtitles off" once it has stayed selected for the settle period; at 2 s it is not.
            var s7 = NewSession("Jellyfin Web");
            await Post(episode, string.Empty, "Jellyfin Web", s7.DeviceId);
            sessions.RaiseStart(s7, episode, user, 2, 4);
            Backdate(s7, 6);
            sessions.RaiseProgress(s7, episode, user, 2, -1);
            check(Manual() is null, "H6 progress 6 s after start with subtitles turned off: held, not remembered until it has stayed selected for the settle period");
            registry.UpdateExisting(s7.Id, r => r.PendingSubtitle = r.PendingSubtitle! with { Since = r.PendingSubtitle.Since - PlaybackMonitor.SettlePeriod });
            sessions.RaiseProgress(s7, episode, user, 2, -1);
            var m7 = Manual();
            check(m7 is { SubtitlesOff: true, Subtitle: null } && EntryFor(s7.Id) is { ManualChanged: true } && sessions.Commands.All(c => c.SessionId != s7.Id),
                $"H6 a later progress report after the settle period still shows subtitles off: remembered as subtitles off for the series (off {m7?.SubtitlesOff})");
            Restore();
            var s8 = NewSession("Jellyfin Web");
            await Post(episode, string.Empty, "Jellyfin Web", s8.DeviceId);
            sessions.RaiseStart(s8, episode, user, 2, 4);
            Backdate(s8, 2);
            sessions.RaiseProgress(s8, episode, user, 2, -1);
            check(Manual() is null && EntryFor(s8.Id) is { ManualChanged: false }, "H6 the same change 2 s after start: not remembered");
            Restore();

            // (7) Correction after start: once, with the expected index, only for an app that advertises the command.
            var s9 = NewSession("Jellyfin Web", GeneralCommandType.SetAudioStreamIndex);
            await Post(episode, string.Empty, "Jellyfin Web", s9.DeviceId);
            sessions.RaiseProgress(s9, episode, user, 1, 4);
            sessions.RaiseProgress(s9, episode, user, 1, 4);
            var c9 = sessions.Commands.Where(c => c.SessionId == s9.Id).ToList();
            check(c9.Count == 1 && c9[0].Command.Name == GeneralCommandType.SetAudioStreamIndex && c9[0].Command.Arguments.TryGetValue("Index", out var index) && index == "2"
                && c9[0].Command.ControllingUserId == user.Id && c9[0].ControllingSessionId == s9.Id && EntryFor(s9.Id)?.CorrectionSentAt is not null,
                $"H7 first progress 1/4 where 2/4 was expected: exactly one SetAudioStreamIndex with Index=2 ({string.Join(", ", c9.Select(c => c.Command.Name + "=" + string.Join(",", c.Command.Arguments.Values)))})");
            sessions.RaiseProgress(s9, episode, user, 2, 4);
            check(Manual() is null, "H7 the commanded switch arriving later is not a manual change");
            var s10 = NewSession("Jellyfin Web", GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex);
            var r10 = await Post(episode, "?audioStreamIndex=1&subtitleStreamIndex=3", "Jellyfin Web", s10.DeviceId);
            sessions.RaiseProgress(s10, episode, user, 1, 3);
            check(r10.StartsWith("1/3", StringComparison.Ordinal) && sessions.Commands.All(c => c.SessionId != s10.Id), $"H7 a trusted play is never corrected ({r10}, {sessions.Commands.Count(c => c.SessionId == s10.Id)} commands)");
            Config(c => c.CorrectAfterStart = CorrectAfterStartMode.Off);
            var s11 = NewSession("Jellyfin Web", GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex);
            await Post(episode, string.Empty, "Jellyfin Web", s11.DeviceId);
            sessions.RaiseProgress(s11, episode, user, 1, 3);
            check(sessions.Commands.All(c => c.SessionId != s11.Id), "H7 CorrectAfterStart Off: nothing sent");
            Config();
            var s11b = NewSession("Jellyfin Web");
            await Post(episode, string.Empty, "Jellyfin Web", s11b.DeviceId);
            sessions.RaiseProgress(s11b, episode, user, 1, 3);
            check(sessions.Commands.All(c => c.SessionId != s11b.Id), "H7 Auto with no advertised commands: nothing sent");
            Restore();

            // (8) Dry run: nothing replaced, nothing remembered, nothing corrected.
            Config(c => c.DryRun = true);
            var s12 = NewSession("Android TV", GeneralCommandType.SetAudioStreamIndex);
            Served(episode, s12);
            var r12 = await Post(episode, "?audioStreamIndex=1&subtitleStreamIndex=3", "Android TV", s12.DeviceId);
            sessions.RaiseStart(s12, episode, user, 1, 3);
            Backdate(s12, 10);
            sessions.RaiseProgress(s12, episode, user, 1, -1);
            check(r12.StartsWith("1/3 source=", StringComparison.Ordinal) && Manual() is null && sessions.Commands.All(c => c.SessionId != s12.Id),
                $"H8 dry run: request unchanged ({r12}), nothing remembered, no command");
            Restore();

            // "Video default": a track left to the video belongs to the app and Jellyfin.
            // Nothing chosen: even Android TV (replace unless manual) keeps its own request; nothing is corrected or remembered.
            plugin.UpdateConfiguration(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
            var neutralServed = Served(episode);
            var s13 = NewSession("Android TV", GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex);
            var r13 = await Post(episode, "?audioStreamIndex=2&subtitleStreamIndex=5", "Android TV", s13.DeviceId);
            sessions.RaiseStart(s13, episode, user, 1, 5);
            Backdate(s13, 10);
            sessions.RaiseProgress(s13, episode, user, 2, 6);
            check(neutralServed == "1/3" && r13.StartsWith("2/5", StringComparison.Ordinal) && sessions.Commands.All(c => c.SessionId != s13.Id) && Manual() is null,
                $"H9 nothing chosen: Jellyfin's own {neutralServed} is served, Android TV's request stays {r13}, no command, nothing remembered");
            Restore();

            // Choosing audio recomputes Jellyfin's subtitle policy for that audio.
            plugin.UpdateConfiguration(new PluginConfiguration
            {
                ConfigVersion = PluginConfiguration.CurrentVersion,
                Everyday = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "jpn"), new AudioStepSetting(AudioStepKind.VideoDefault) } },
            });
            var s14 = NewSession("Android TV", GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex);
            var r14 = await Post(episode, "?audioStreamIndex=1&subtitleStreamIndex=5", "Android TV", s14.DeviceId);
            var e14 = EntryFor(s14.Id);
            sessions.RaiseStart(s14, episode, user, 1, 5);
            var c14 = sessions.Commands.Where(c => c.SessionId == s14.Id).ToList();
            var audioOnlyServed = Served(episode);
            check(audioOnlyServed == "2/3" && r14.StartsWith("2/3", StringComparison.Ordinal) && e14 is { ExpectedAudio: 2, ExpectedSubtitle: 3 }
                && c14.Count == 2 && c14.Any(c => c.Command.Name == GeneralCommandType.SetAudioStreamIndex)
                && c14.Any(c => c.Command.Name == GeneralCommandType.SetSubtitleStreamIndex),
                $"H9 only the audio chosen: served {audioOnlyServed} (Jellyfin's policy recomputed); Android TV's request becomes {r14}; both tracks are corrected after start ({string.Join(",", c14.Select(c => c.Command.Name))})");
            Restore();

            // Jellyfin Web autoplay: audio changes and Jellyfin's subtitle policy is recomputed.
            var s15 = NewSession("Jellyfin Web");
            sessions.RaiseStart(s15, otherEpisode, user, 2, 4);
            var r15 = await Post(episode, "?audioStreamIndex=1&subtitleStreamIndex=6", "Jellyfin Web", s15.DeviceId);
            check(r15.StartsWith("2/3", StringComparison.Ordinal) && Manual() is null,
                $"H9 Jellyfin Web next episode, only the audio chosen: audio replaced and Jellyfin's subtitle policy recomputed ({r15})");
            Restore();
        }
        catch (Exception ex)
        {
            check(false, "H: unexpected exception " + ex);
        }
        finally
        {
            plugin.UpdateConfiguration(original);
        }
    }
}

/// <summary>A hand-written <see cref="ISessionManager"/>: sessions from a list, the playback events raisable from the test, commands recorded.</summary>
public sealed class FakeSessionManager : ISessionManager
{
    private readonly List<SessionInfo> _sessions = new();
    private readonly object _lock = new();

    public event EventHandler<PlaybackProgressEventArgs>? PlaybackStart;

    public event EventHandler<PlaybackProgressEventArgs>? PlaybackProgress;

    public event EventHandler<PlaybackStopEventArgs>? PlaybackStopped;

    public event EventHandler<SessionEventArgs>? SessionStarted;

    public event EventHandler<SessionEventArgs>? SessionEnded;

    public event EventHandler<SessionEventArgs>? SessionActivity
    {
        add { }
        remove { }
    }

    public event EventHandler<SessionEventArgs>? SessionControllerConnected;

    public event EventHandler<SessionEventArgs>? CapabilitiesChanged;

    /// <summary>Gets the general commands sent, in order.</summary>
    public List<(string ControllingSessionId, string SessionId, GeneralCommand Command)> Commands { get; } = new();

    public IEnumerable<SessionInfo> Sessions
    {
        get
        {
            lock (_lock)
            {
                return _sessions.ToList();
            }
        }
    }

    public void Add(SessionInfo session)
    {
        lock (_lock)
        {
            _sessions.Add(session);
        }

        SessionStarted?.Invoke(this, new SessionEventArgs { SessionInfo = session });
    }

    public void RaiseStart(SessionInfo session, BaseItem item, User user, int? audio, int? subtitle)
        => PlaybackStart?.Invoke(this, Args<PlaybackProgressEventArgs>(session, item, user, audio, subtitle));

    public void RaiseProgress(SessionInfo session, BaseItem item, User user, int? audio, int? subtitle)
        => PlaybackProgress?.Invoke(this, Args<PlaybackProgressEventArgs>(session, item, user, audio, subtitle));

    public void RaiseStopped(SessionInfo session, BaseItem item, User user)
        => PlaybackStopped?.Invoke(this, Args<PlaybackStopEventArgs>(session, item, user, session.PlayState?.AudioStreamIndex, session.PlayState?.SubtitleStreamIndex));

    public void End(SessionInfo session)
    {
        lock (_lock)
        {
            _sessions.Remove(session);
        }

        SessionEnded?.Invoke(this, new SessionEventArgs { SessionInfo = session });
    }

    public Task SendGeneralCommand(string controllingSessionId, string sessionId, GeneralCommand command, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Commands.Add((controllingSessionId, sessionId, command));
        }

        return Task.CompletedTask;
    }

    public SessionInfo GetSession(string deviceId, string client, string version)
        => Sessions.FirstOrDefault(s => s.DeviceId == deviceId && string.Equals(s.Client, client, StringComparison.OrdinalIgnoreCase))!;

    public Task<SessionInfo> LogSessionActivity(string appName, string appVersion, string deviceId, string deviceName, string remoteEndPoint, User user) => Task.FromResult(GetSession(deviceId, appName, appVersion));

    public void OnSessionControllerConnected(SessionInfo session) => SessionControllerConnected?.Invoke(this, new SessionEventArgs { SessionInfo = session });

    public void UpdateDeviceName(string sessionId, string reportedDeviceName)
    {
    }

    public Task OnPlaybackStart(PlaybackStartInfo info) => Task.CompletedTask;

    public Task OnPlaybackProgress(PlaybackProgressInfo info) => Task.CompletedTask;

    public Task OnPlaybackProgress(PlaybackProgressInfo info, bool isAutomated) => Task.CompletedTask;

    public Task OnPlaybackStopped(PlaybackStopInfo info) => Task.CompletedTask;

    public ValueTask ReportSessionEnded(string sessionId) => ValueTask.CompletedTask;

    public Task SendMessageCommand(string controllingSessionId, string sessionId, MessageCommand command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendPlayCommand(string controllingSessionId, string sessionId, PlayRequest command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendSyncPlayCommand(string sessionId, SendCommand command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendSyncPlayGroupUpdate<T>(string sessionId, GroupUpdate<T> command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendBrowseCommand(string controllingSessionId, string sessionId, BrowseRequest command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendPlaystateCommand(string controllingSessionId, string sessionId, PlaystateRequest command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageToAdminSessions<T>(SessionMessageType name, T data, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageToUserSessions<T>(List<Guid> userIds, SessionMessageType name, T data, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageToUserSessions<T>(List<Guid> userIds, SessionMessageType name, Func<T> dataFn, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageToUserDeviceSessions<T>(string deviceId, SessionMessageType name, T data, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendRestartRequiredNotification(CancellationToken cancellationToken) => Task.CompletedTask;

    public void AddAdditionalUser(string controllingSessionId, string sessionId, Guid userId)
    {
    }

    public void RemoveAdditionalUser(string controllingSessionId, string sessionId, Guid userId)
    {
    }

    public void ReportNowViewingItem(string controllingSessionId, string sessionId, string itemId)
    {
    }

    public Task<AuthenticationResult> AuthenticateNewSession(AuthenticationRequest request) => throw new NotSupportedException();

    public Task<AuthenticationResult> AuthenticateDirect(AuthenticationRequest request) => throw new NotSupportedException();

    public void ReportCapabilities(string controllingSessionId, string sessionId, ClientCapabilities capabilities)
    {
        var session = Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is not null)
        {
            session.Capabilities = capabilities;
            CapabilitiesChanged?.Invoke(this, new SessionEventArgs { SessionInfo = session });
        }
    }

    public void ReportTranscodingInfo(string deviceId, TranscodingInfo info)
    {
    }

    public void ClearTranscodingInfo(string deviceId)
    {
    }

    public IReadOnlyList<SessionInfoDto> GetSessions(Guid userId, string deviceId, int? activeWithinSeconds, Guid? controllableUserToCheck, bool isApiKey) => Array.Empty<SessionInfoDto>();

    public Task<SessionInfo> GetSessionByAuthenticationToken(string token, string deviceId, string remoteEndpoint) => throw new NotSupportedException();

    public Task<SessionInfo> GetSessionByAuthenticationToken(Device info, string deviceId, string remoteEndpoint, string appVersion) => throw new NotSupportedException();

    public Task Logout(string accessToken) => Task.CompletedTask;

    public Task Logout(Device device) => Task.CompletedTask;

    public Task RevokeUserTokens(Guid userId, string currentAccessToken) => Task.CompletedTask;

    public Task CloseIfNeededAsync(SessionInfo session) => Task.CompletedTask;

    public Task CloseLiveStreamIfNeededAsync(string liveStreamId, string sessionIdOrPlaySessionId) => Task.CompletedTask;

    public SessionInfoDto ToSessionInfoDto(SessionInfo sessionInfo) => new() { Id = sessionInfo.Id, Client = sessionInfo.Client, DeviceId = sessionInfo.DeviceId, DeviceName = sessionInfo.DeviceName, UserId = sessionInfo.UserId };

    private static TArgs Args<TArgs>(SessionInfo session, BaseItem item, User user, int? audio, int? subtitle)
        where TArgs : PlaybackProgressEventArgs, new()
    {
        session.PlayState ??= new PlayerStateInfo();
        session.PlayState.AudioStreamIndex = audio;
        session.PlayState.SubtitleStreamIndex = subtitle;
        session.PlayState.MediaSourceId = item.Id.ToString("N");
        session.FullNowPlayingItem = item;
        return new TArgs
        {
            Session = session,
            Item = item,
            Users = new List<User> { user },
            MediaSourceId = item.Id.ToString("N"),
            DeviceId = session.DeviceId,
            DeviceName = session.DeviceName,
            ClientName = session.Client,
            PlaySessionId = Guid.NewGuid().ToString("N"),
        };
    }
}

/// <summary>Turns the X-Fake-Client / X-Fake-Device headers into Jellyfin's client and device claims (the fake auth sets neither).</summary>
public sealed class FakeClientClaims : IClaimsTransformation
{
    private readonly IHttpContextAccessor _http;

    public FakeClientClaims(IHttpContextAccessor http)
    {
        _http = http;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var request = _http.HttpContext?.Request;
        if (request is not null && principal.Identity is ClaimsIdentity { IsAuthenticated: true } identity)
        {
            Add(identity, request, PlaybackChecks.ClientHeader, "Jellyfin-Client");
            Add(identity, request, PlaybackChecks.DeviceHeader, "Jellyfin-DeviceId");
        }

        return Task.FromResult(principal);
    }

    private static void Add(ClaimsIdentity identity, HttpRequest request, string header, string claim)
    {
        if (request.Headers.TryGetValue(header, out var value) && !string.IsNullOrEmpty(value) && !identity.HasClaim(c => c.Type == claim))
        {
            identity.AddClaim(new Claim(claim, value.ToString()));
        }
    }
}

/// <summary>An in-memory activity log for hosts where the real one isn't registered.</summary>
public sealed class HarnessActivityLog : IActivityLog
{
    private readonly List<ActivityEntry> _entries = new();

    public void Add(ActivityEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<ActivityEntry> Query(int limit, string? kind = null, Guid? userId = null)
    {
        lock (_entries)
        {
            return _entries.AsEnumerable().Reverse().Where(e => (kind is null || e.Kind == kind) && (userId is null || e.UserId == userId)).Take(limit).ToList();
        }
    }
}

/// <summary>A compatibility writer that only counts, for hosts where the real one isn't registered.</summary>
public sealed class HarnessCompatibilityWriter : ICompatibilityWriter
{
    private int _count;
    private int _stopped;

    public void StopWrites() => Interlocked.Exchange(ref _stopped, 1);

    public int LedgerCount => _count;

    public Task WriteAsync(User user, BaseItem item, int? audioIndex, int? subtitleIndex, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _stopped) == 0) Interlocked.Increment(ref _count);
        return Task.CompletedTask;
    }

    public Task UndoAllAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _count, 0);
        return Task.CompletedTask;
    }
}
