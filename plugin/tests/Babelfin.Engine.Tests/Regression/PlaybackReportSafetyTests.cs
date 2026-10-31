using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Compat;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class PlaybackReportSafetyTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateExpectedTracksAreAcknowledgementsAndSubsequentManualChangesStillCount(bool trusted)
    {
        var play = Expected();
        play.Trusted = trusted;
        if (trusted) { play.ExpectedAudio = 3; play.ExpectedSubtitle = 6; }
        var targetAudio = play.ExpectedAudio;
        var targetSubtitle = play.ExpectedSubtitle;
        PlaybackMonitor.Evaluate(play, 1, 3, T0, CorrectAfterStartMode.Off, 5, false, null);
        var applied = PlaybackMonitor.Evaluate(play, targetAudio, targetSubtitle, T0.AddMinutes(20), CorrectAfterStartMode.Off, 5, false, null);
        Assert.False(applied.Manual);
        Assert.False(play.ManualChanged);
        var manual = PlaybackMonitor.Evaluate(play, 1, 7, T0.AddMinutes(21), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(manual.Manual);
        Assert.Equal((targetAudio, targetSubtitle), (manual.PreviousAudio, manual.PreviousSubtitle));
        var back = PlaybackMonitor.Evaluate(play, targetAudio, targetSubtitle, T0.AddMinutes(22), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(back.Manual);
    }

    [Fact]
    public void LateAudioAcknowledgementDoesNotPolluteAnActualSubtitleChange()
    {
        var play = Expected();
        PlaybackMonitor.Evaluate(play, 1, 3, T0, CorrectAfterStartMode.Off, 5, false, null);
        // A positive subtitle selection is independent of the audio-driven Off reset heuristic.
        var change = PlaybackMonitor.Evaluate(play, 2, 7, T0.AddMinutes(20), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(change.Manual);
        Assert.Equal(change.ManualAudio, change.PreviousAudio);
        Assert.Equal(3, change.PreviousSubtitle);
        Assert.Equal(7, change.ManualSubtitle);
    }

    [Theory]
    [InlineData(CorrectAfterStartMode.Auto)]
    [InlineData(CorrectAfterStartMode.On)]
    public void InitialOffIsCorrectedToTheRequestedSubtitleAndLateActivationIsAcknowledged(CorrectAfterStartMode mode)
    {
        var play = Expected();
        var first = PlaybackMonitor.Evaluate(play, 1, -1, T0, mode, 5, false,
            new[] { GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex });
        Assert.Equal(2, first.SendAudio);
        Assert.Equal(4, first.SendSubtitle);
        Assert.Equal(4, play.ExpectedSubtitle);
        Assert.False(first.Manual);
        var enabled = PlaybackMonitor.Evaluate(play, 2, 4, T0.AddMinutes(20), mode, 5, false, null);
        Assert.False(enabled.Manual);
        Assert.False(play.ManualChanged);
        var off = PlaybackMonitor.Evaluate(play, 2, -1, T0.AddMinutes(21), mode, 5, false, null);
        Assert.True(off.Manual);
        Assert.Equal(4, off.PreviousSubtitle);
        Assert.Equal(-1, play.ExpectedSubtitle);
        var steady = PlaybackMonitor.Evaluate(play, 2, -1, T0.AddMinutes(22), mode, 5, false, null);
        Assert.False(steady.Manual);
        Assert.Null(steady.SendSubtitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffBeforeADeferredExpectedSubtitleIsNotAWholeShowManualChange(bool omitFirstSubtitle)
    {
        var play = Expected();
        PlaybackMonitor.Evaluate(play, 2, omitFirstSubtitle ? null : -1, T0, CorrectAfterStartMode.Off, 5, false, null);
        var baseline = PlaybackMonitor.Evaluate(play, 2, -1, T0.AddMinutes(1), CorrectAfterStartMode.Off, 5, false, null);
        Assert.False(baseline.Manual);
        Assert.Equal(4, play.ExpectedSubtitle);
        var acknowledgement = PlaybackMonitor.Evaluate(play, 2, 4, T0.AddMinutes(20), CorrectAfterStartMode.Off, 5, false, null);
        Assert.False(acknowledgement.Manual);
        Assert.False(play.ManualChanged);
        var manualOff = PlaybackMonitor.Evaluate(play, 2, -1, T0.AddMinutes(21), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(manualOff.Manual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EachTrackGetsOneCorrectionWhenItsFirstIndexArrives(bool audioFirst)
    {
        var play = Expected();
        var commands = new[] { GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex };
        var first = PlaybackMonitor.Evaluate(play, audioFirst ? 2 : null, audioFirst ? null : 4, T0, CorrectAfterStartMode.Auto, 5, false, commands);
        Assert.False(first.HasCommands);
        var late = PlaybackMonitor.Evaluate(play, audioFirst ? 2 : 1, audioFirst ? -1 : 4, T0.AddSeconds(10), CorrectAfterStartMode.Auto, 5, false, commands);
        Assert.Equal(audioFirst ? null : 2, late.SendAudio);
        Assert.Equal(audioFirst ? 4 : null, late.SendSubtitle);
        Assert.False(late.Manual);
        var applied = PlaybackMonitor.Evaluate(play, 2, 4, T0.AddSeconds(20), CorrectAfterStartMode.Auto, 5, false, commands);
        Assert.False(applied.Manual);
        var off = PlaybackMonitor.Evaluate(play, 2, -1, T0.AddSeconds(40), CorrectAfterStartMode.Auto, 5, false, commands);
        Assert.True(off.Manual);
        Assert.Null(off.SendSubtitle);
    }

    [Fact]
    public void AFirstSubtitleCorrectionDoesNotHideAGenuineAudioChange()
    {
        var play = Expected();
        var commands = new[] { GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex };
        PlaybackMonitor.Evaluate(play, 2, null, T0, CorrectAfterStartMode.Auto, 5, false, commands);
        var mixed = PlaybackMonitor.Evaluate(play, 1, -1, T0.AddSeconds(10), CorrectAfterStartMode.Auto, 5, false, commands);
        Assert.True(mixed.Manual);
        Assert.Equal(2, mixed.PreviousAudio);
        Assert.Equal(1, mixed.ManualAudio);
        Assert.Equal(4, mixed.SendSubtitle);
        Assert.Equal(4, play.ExpectedSubtitle);
        Assert.False(PlaybackMonitor.Evaluate(play, 1, 4, T0.AddSeconds(20), CorrectAfterStartMode.Auto, 5, false, commands).Manual);
    }

    [Fact]
    public void OmittedSubtitleIsUnknownAndExplicitOffCanBeRememberedWithoutAnEarlierIndex()
    {
        using var host = Host();
        var user = NewUser();
        var item = new Movie { Id = Guid.NewGuid(), Name = "Synthetic movie" };
        var store = new FakePersonStore();
        store.Save(new PersonSettings { UserId = user.Id });
        var context = Ctx.Movie() with { ItemId = item.Id };
        var resolver = CallProxy.Create<IProfileResolver>((_, _) =>
            ProfileResolver.Resolve(store.TryGet(user.Id)!, new PluginConfiguration(), context, LanguageTable.Default)).Proxy;
        var activity = new ListActivityLog();
        var recorder = new TrackChangeRecorder(resolver, store, activity, NullLogger<TrackChangeRecorder>.Instance);
        var source = Source();
        Assert.Null(recorder.Record(user, item, source, null, null, null, 4, "Synthetic app"));
        Assert.Empty(store.TryGet(user.Id)!.Overrides);
        var off = recorder.Record(user, item, source, null, -1, null, null, "Synthetic app");
        Assert.NotNull(off);
        Assert.True(off!.SubtitlesOff);
        var audio = recorder.Record(user, item, source, 2, null, 1, 4, "Synthetic app");
        Assert.NotNull(audio);
        Assert.True(audio!.SubtitlesOff);
        Assert.Null(audio.Subtitle);
        Assert.NotNull(audio.Audio);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("no-decision")]
    [InlineData("throw")]
    public void UnchosenPlayLooksUpOnceAndRetriesForANewSourceItemOrUser(string outcome)
    {
        using var host = Host();
        var now = T0;
        var user = NewUser();
        var users = CallProxy.Create<IUserManager>((method, _) => method.Name == "GetUserById" ? user : null);
        var sources = CallProxy.Create<IMediaSourceManager>((method, _) =>
        {
            if (method.Name != "GetStaticMediaSources") return null;
            if (outcome == "throw") throw new IOException("Synthetic source lookup failure");
            return outcome == "empty" ? Array.Empty<MediaSourceInfo>() : new[] { Source() };
        });
        var sessions = CallProxy.Create<ISessionManager>().Proxy;
        var registry = new PlayRegistry(() => now);
        var monitor = new PlaybackMonitor(sessions, registry, null!, sources.Proxy, users.Proxy,
            CallProxy.Create<ILibraryManager>().Proxy, new ListActivityLog(), NullLogger<PlaybackMonitor>.Instance, new DecoratorStats(() => now));
        var session = new SessionInfo(sessions, NullLogger.Instance)
        {
            Id = "synthetic-session", UserId = user.Id,
            PlayState = new PlayerStateInfo { AudioStreamIndex = 1, SubtitleStreamIndex = -1, MediaSourceId = "source" },
        };
        var report = new PlaybackProgressEventArgs { Session = session, Item = new Movie { Id = Guid.NewGuid() }, MediaSourceId = "source" };
        void Reports(int count)
        {
            for (var i = 0; i < count; i++)
            {
                now = now.AddSeconds(10);
                Report(monitor, report);
            }
        }
        Reports(30);
        Assert.Equal(1, users.Recorder.Calls.Count(name => name == "GetUserById"));
        Assert.Equal(1, sources.Recorder.Calls.Count(name => name == "GetStaticMediaSources"));
        Assert.True(registry.Get(session.Id)!.HasReport);
        report.MediaSourceId = "other-source";
        Reports(3);
        Assert.Equal(2, sources.Recorder.Calls.Count(name => name == "GetStaticMediaSources"));
        report.Item = new Movie { Id = Guid.NewGuid() };
        Reports(3);
        Assert.Equal(3, sources.Recorder.Calls.Count(name => name == "GetStaticMediaSources"));
        user = NewUser();
        session.UserId = user.Id;
        Reports(3);
        Assert.Equal(4, sources.Recorder.Calls.Count(name => name == "GetStaticMediaSources"));
    }

    [Fact]
    public async Task PluginDisableRestoresFlagsAndPreservesEveryHandleNextEpisodePreference()
    {
        using var host = Host();
        var store = new FakePersonStore();
        var user = NewUser();
        var untouched = NewUser();
        store.Save(new PersonSettings { UserId = user.Id, HandleNextEpisode = true });
        store.Save(new PersonSettings { UserId = untouched.Id, HandleNextEpisode = true });
        var users = Users(user, untouched);
        var flags = new RememberFlags(users, store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        await flags.ApplyAsync(user, CancellationToken.None);
        Assert.False(user.RememberAudioSelections);
        using var provider = Services(flags);
        await new PluginDisableCleanup(Disabled(), provider, NullLogger<PluginDisableCleanup>.Instance).StopAsync(CancellationToken.None);
        Assert.True(user.RememberAudioSelections);
        Assert.True(user.RememberSubtitleSelections);
        Assert.True(store.TryGet(user.Id)!.HandleNextEpisode);
        Assert.True(store.TryGet(untouched.Id)!.HandleNextEpisode);
        Assert.Null(store.TryGet(user.Id)!.JellyfinChanges);
        await flags.ApplyAsync(user, CancellationToken.None);
        Assert.True(user.RememberAudioSelections);
        Assert.Null(store.TryGet(user.Id)!.JellyfinChanges);
        // A later enabled service instance resumes the person's preference and saves the restored flags for recovery.
        var enabledFlags = new RememberFlags(users, store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        await enabledFlags.ApplyAsync(user, CancellationToken.None);
        Assert.False(user.RememberAudioSelections);
        Assert.True(store.TryGet(user.Id)!.JellyfinChanges!.RememberAudioSelections);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RuntimeAdminResetAllAllowsExplicitReenableOnTheSameFlagsService(bool audio, bool subtitles)
    {
        using var host = Host();
        var store = new FakePersonStore();
        var user = NewUser();
        user.RememberAudioSelections = audio;
        user.RememberSubtitleSelections = subtitles;
        store.Save(new PersonSettings { UserId = user.Id, UserName = user.Username, HandleNextEpisode = true });
        var flags = new RememberFlags(Users(user), store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        await flags.ApplyAsync(user, CancellationToken.None);
        Assert.False(user.RememberAudioSelections);
        Assert.False(user.RememberSubtitleSelections);

        // Exercise the live endpoint, including its backup and per-person flag sync.
        var admin = ApiSafetyHost.Admin(store, flags, user);
        var resetResponse = await admin.ResetAll();
        var reset = Assert.IsType<ResetAllReport>(Assert.IsType<OkObjectResult>(resetResponse.Result).Value);
        Assert.Equal(1, reset.People);
        Assert.Empty(reset.FailedNames);
        Assert.True(File.Exists(Path.Combine(host.Folder, "babelfin", reset.BackupPath)));
        Assert.True(store.TryGet(user.Id)!.HandleNextEpisode);
        var kept = store.TryGet(user.Id)!.JellyfinChanges;
        Assert.NotNull(kept);
        Assert.Equal(audio, kept!.RememberAudioSelections);
        Assert.Equal(subtitles, kept.RememberSubtitleSelections);
        Assert.False(user.RememberAudioSelections);
        Assert.False(user.RememberSubtitleSelections);

        // A stale apply after reset keeps the record and the flags as they are without shutting down the service.
        await flags.ApplyAsync(user, CancellationToken.None);
        Assert.Equal(kept.ChangedAt, store.TryGet(user.Id)!.JellyfinChanges!.ChangedAt);
        Assert.False(user.RememberAudioSelections);
        Assert.False(user.RememberSubtitleSelections);

        // A real settings edit turns handling off through SyncRememberFlagsAsync: the flags are restored and the record cleared.
        var edited = store.TryGet(user.Id)!.Clone();
        edited.HandleNextEpisode = false;
        var saved = await admin.PutUserSettings(user.Id, edited);
        Assert.IsType<OkObjectResult>(saved.Result);
        Assert.False(store.TryGet(user.Id)!.HandleNextEpisode);
        Assert.Null(store.TryGet(user.Id)!.JellyfinChanges);
        Assert.Equal(audio, user.RememberAudioSelections);
        Assert.Equal(subtitles, user.RememberSubtitleSelections);

        // Turning it on again on this same instance applies it afresh.
        edited = store.TryGet(user.Id)!.Clone();
        edited.HandleNextEpisode = true;
        saved = await admin.PutUserSettings(user.Id, edited);
        Assert.IsType<OkObjectResult>(saved.Result);
        Assert.True(store.TryGet(user.Id)!.HandleNextEpisode);
        Assert.False(user.RememberAudioSelections);
        Assert.False(user.RememberSubtitleSelections);
        var recovery = store.TryGet(user.Id)!.JellyfinChanges;
        Assert.NotNull(recovery);
        Assert.Equal(audio, recovery!.RememberAudioSelections);
        Assert.Equal(subtitles, recovery.RememberSubtitleSelections);

        await flags.ApplyAsync(user, CancellationToken.None);
        Assert.False(user.RememberAudioSelections);
        Assert.False(user.RememberSubtitleSelections);
        Assert.Equal(recovery.ChangedAt, store.TryGet(user.Id)!.JellyfinChanges!.ChangedAt);
        Assert.Equal(audio, store.TryGet(user.Id)!.JellyfinChanges!.RememberAudioSelections);
        Assert.Equal(subtitles, store.TryGet(user.Id)!.JellyfinChanges!.RememberSubtitleSelections);
    }

    [Fact]
    public async Task QueuedApplyCannotUndoDisableRestoration()
    {
        using var host = Host();
        var user = NewUser();
        var store = new FakePersonStore();
        store.Save(new PersonSettings { UserId = user.Id, HandleNextEpisode = true });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var users = Users(user, onRestore: async () => { entered.TrySetResult(); await release.Task; });
        var flags = new RememberFlags(users, store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        await flags.ApplyAsync(user, CancellationToken.None);
        using var provider = Services(flags);
        var cleanup = new PluginDisableCleanup(Disabled(), provider, NullLogger<PluginDisableCleanup>.Instance).StopAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queuedApply = flags.ApplyAsync(user, CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(cleanup, queuedApply).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(user.RememberAudioSelections);
        Assert.True(store.TryGet(user.Id)!.HandleNextEpisode);
        Assert.Null(store.TryGet(user.Id)!.JellyfinChanges);
    }

    [Fact]
    public async Task DisableFailureKeepsRecoveryRecordAndPreference()
    {
        using var host = Host();
        var user = NewUser();
        var store = new FakePersonStore();
        store.Save(new PersonSettings { UserId = user.Id, HandleNextEpisode = true });
        var fail = false;
        var users = Users(user, onRestore: () => fail ? Task.FromException(new IOException("Synthetic restore failure")) : Task.CompletedTask);
        var flags = new RememberFlags(users, store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        await flags.ApplyAsync(user, CancellationToken.None);
        fail = true;
        using var provider = Services(flags);
        await new PluginDisableCleanup(Disabled(), provider, NullLogger<PluginDisableCleanup>.Instance).StopAsync(CancellationToken.None);
        Assert.True(store.TryGet(user.Id)!.HandleNextEpisode);
        Assert.NotNull(store.TryGet(user.Id)!.JellyfinChanges);
        fail = false;
        await flags.RestoreAsync(user, CancellationToken.None);
        Assert.True(user.RememberAudioSelections);
        Assert.True(store.TryGet(user.Id)!.HandleNextEpisode);
        Assert.Null(store.TryGet(user.Id)!.JellyfinChanges);
    }

    [Fact]
    public async Task AlreadyCancelledDisableStillClosesFlagWrites()
    {
        using var host = Host();
        var user = NewUser();
        var store = new FakePersonStore();
        store.Save(new PersonSettings { UserId = user.Id, HandleNextEpisode = true });
        var flags = new RememberFlags(Users(user), store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        using var provider = Services(flags);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await new PluginDisableCleanup(Disabled(), provider, NullLogger<PluginDisableCleanup>.Instance).StopAsync(cancellation.Token);
        await flags.ApplyAsync(user, CancellationToken.None);
        Assert.True(user.RememberAudioSelections);
        Assert.True(store.TryGet(user.Id)!.HandleNextEpisode);
        Assert.Null(store.TryGet(user.Id)!.JellyfinChanges);
    }

    [Fact]
    public void AnAcknowledgedTargetCanBeSelectedManuallyAfterAnEarlyAppChange()
    {
        var play = Expected();
        PlaybackMonitor.Evaluate(play, 2, 4, T0, CorrectAfterStartMode.Off, 5, false, null);
        PlaybackMonitor.Evaluate(play, 1, 3, T0.AddSeconds(2), CorrectAfterStartMode.Off, 5, false, null);
        var manual = PlaybackMonitor.Evaluate(play, 2, 4, T0.AddMinutes(20), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(manual.Manual);
        Assert.Equal((1, 3), (manual.PreviousAudio, manual.PreviousSubtitle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompletedLookupCannotPopulateANewerPlayOrResurrectAnEndedSession(bool ended)
    {
        using var host = Host();
        var user = NewUser();
        var item = new Movie { Id = Guid.NewGuid() };
        var sessions = CallProxy.Create<ISessionManager>().Proxy;
        var registry = new PlayRegistry(() => T0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var count = 0;
        var sources = CallProxy.Create<IMediaSourceManager>((method, _) =>
        {
            if (method.Name != "GetStaticMediaSources") return null;
            if (Interlocked.Increment(ref count) != 1) return Array.Empty<MediaSourceInfo>();
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release source lookup");
            return new[] { Source() };
        }).Proxy;
        var monitor = new PlaybackMonitor(sessions, registry, null!, sources, Users(user),
            CallProxy.Create<ILibraryManager>().Proxy, new ListActivityLog(), NullLogger<PlaybackMonitor>.Instance);
        var session = new SessionInfo(sessions, NullLogger.Instance)
        {
            Id = "synthetic-session", UserId = user.Id,
            PlayState = new PlayerStateInfo { AudioStreamIndex = 2, SubtitleStreamIndex = 4 },
        };
        var oldReport = new PlaybackProgressEventArgs { Session = session, Item = item, MediaSourceId = "source" };
        var pending = Task.Run(() => Report(monitor, oldReport));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (ended) registry.Remove(session.Id);
            else Report(monitor, new PlaybackProgressEventArgs { Session = session, Item = item, MediaSourceId = "other-source" });
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        if (ended)
        {
            Assert.Null(registry.Get(session.Id));
            return;
        }
        var play = registry.Get(session.Id)!;
        Assert.Equal("other-source", play.MediaSourceId);
        Assert.False(play.HasExpected);
        Assert.Null(play.ChoiceAudio);
        Assert.Null(play.ChoiceSubtitle);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(600, false)]
    public void FirstSubtitleCorrectionIsLimitedToStartup(int elapsedSeconds, bool correct)
    {
        var play = Expected();
        PlaybackMonitor.Evaluate(play, 2, null, T0, CorrectAfterStartMode.Auto, 5, false, null);
        var report = PlaybackMonitor.Evaluate(play, 2, 7, T0.AddSeconds(elapsedSeconds), CorrectAfterStartMode.Auto, 5, false,
            new[] { GeneralCommandType.SetSubtitleStreamIndex });
        Assert.Equal(correct ? 4 : null, report.SendSubtitle);
        Assert.Equal(!correct, report.Manual);
        Assert.Equal(7, play.LastReportedSubtitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturningToAnUnacknowledgedChoiceAfterARealManualPickIsRemembered(bool correctionSent)
    {
        var play = Expected();
        var mode = correctionSent ? CorrectAfterStartMode.On : CorrectAfterStartMode.Off;
        PlaybackMonitor.Evaluate(play, 1, -1, T0, mode, 5, false, null);
        var other = PlaybackMonitor.Evaluate(play, 8, 7, T0.AddMinutes(2), mode, 5, false, null);
        Assert.True(other.Manual);
        var back = PlaybackMonitor.Evaluate(play, 2, 4, T0.AddMinutes(3), mode, 5, false, null);
        Assert.True(back.Manual);
        Assert.Equal((8, 7), (back.PreviousAudio, back.PreviousSubtitle));
        Assert.Equal((2, 4), (back.ManualAudio, back.ManualSubtitle));
        Assert.Equal((2, 4), (play.ExpectedAudio, play.ExpectedSubtitle));
    }

    [Fact]
    public void ManualAudioDoesNotTurnTheFirstSubtitleAcknowledgementIntoAManualPick()
    {
        var play = Expected();
        PlaybackMonitor.Evaluate(play, 1, -1, T0, CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(PlaybackMonitor.Evaluate(play, 8, -1, T0.AddMinutes(2), CorrectAfterStartMode.Off, 5, false, null).Manual);
        Assert.False(PlaybackMonitor.Evaluate(play, 8, 4, T0.AddMinutes(3), CorrectAfterStartMode.Off, 5, false, null).Manual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANewPlaybackInfoExpectationAfterAManualPickStillHasItsOwnAcknowledgement(bool audio)
    {
        var play = Expected();
        PlaybackMonitor.Evaluate(play, 1, -1, T0, CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(PlaybackMonitor.Evaluate(play, 8, 7, T0.AddMinutes(2), CorrectAfterStartMode.Off, 5, false, null).Manual);
        if (audio) play.ExpectedAudio = 9;
        else play.ExpectedSubtitle = 9;
        var applied = PlaybackMonitor.Evaluate(play, audio ? 9 : 8, audio ? 7 : 9,
            T0.AddMinutes(3), CorrectAfterStartMode.Off, 5, false, null);
        Assert.False(applied.Manual);
    }

    private static PlayRecord Expected() => new() { HasExpected = true, ExpectedAudio = 2, ExpectedSubtitle = 4, ChoiceAudio = 2, ChoiceSubtitle = 4 };
    private static User NewUser() => new("Synthetic person", "Default", "Default")
        { Id = Guid.NewGuid(), RememberAudioSelections = true, RememberSubtitleSelections = true };
    private static MediaSourceInfo Source() => new()
    {
        Id = "source", DefaultAudioStreamIndex = 2, DefaultSubtitleStreamIndex = 4,
        MediaStreams = new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng", Codec = "aac" },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = "jpn", Codec = "aac" },
            new() { Index = 4, Type = MediaStreamType.Subtitle, Language = "eng", Codec = "subrip" },
        },
    };
    private static LocalStorageTestHost Host()
    {
        var host = new LocalStorageTestHost();
        host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        return host;
    }
    private static void Report(PlaybackMonitor monitor, PlaybackProgressEventArgs report)
        => typeof(PlaybackMonitor).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(monitor, new object?[] { null, report });
    private static IUserManager Users(User user, User? other = null, Func<Task>? onRestore = null)
        => CallProxy.Create<IUserManager>((method, args) =>
        {
            if (method.Name == "GetUserById") return (Guid)args[0]! == user.Id ? user : other;
            if (method.Name == "UpdateConfigurationAsync") return Update((UserConfiguration)args[1]!);
            return null;
            async Task Update(UserConfiguration configuration)
            {
                if (configuration.RememberAudioSelections && onRestore is not null) await onRestore();
                user.RememberAudioSelections = configuration.RememberAudioSelections;
                user.RememberSubtitleSelections = configuration.RememberSubtitleSelections;
            }
        }).Proxy;
    private static ServiceProvider Services(IRememberFlags flags) => new ServiceCollection().AddSingleton(flags)
        .AddSingleton(CallProxy.Create<ICompatibilityWriter>().Proxy).BuildServiceProvider();
    private static IPluginManager Disabled()
    {
        var manifest = new PluginManifest { Status = PluginStatus.Disabled };
        var version = typeof(Plugin).Assembly.GetName().Version!;
        var property = typeof(PluginManifest).GetProperty("Version")!;
        property.SetValue(manifest, property.PropertyType == typeof(string) ? version.ToString() : version);
        return CallProxy.Create<IPluginManager>((method, _) => method.Name == "GetPlugin" ? new LocalPlugin(Path.GetTempPath(), true, manifest) : null).Proxy;
    }
}
