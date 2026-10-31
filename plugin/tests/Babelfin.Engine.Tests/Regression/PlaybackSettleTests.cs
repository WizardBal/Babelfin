using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

// A track change during play is remembered only once it has stayed selected for the settle period.
public sealed partial class PlaybackBoundaryAdversarialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOffFlickerWhileTheStreamRestartsRemembersOnlyTheRealTrack(bool flickerInReports)
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        // jellyfin-web restarts the stream with subtitles off, then reports the subtitle actually picked.
        h.Now = T0.AddSeconds(60);
        await h.Request(h.First, subtitle: -1);
        h.Now = T0.AddSeconds(62);
        h.Report(h.First, 2, 3);
        if (flickerInReports)
        {
            h.Now = T0.AddSeconds(64);
            h.Report(h.First, 2, -1);
            h.Now = T0.AddSeconds(66);
            h.Report(h.First, 2, 3);
        }

        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        h.Settle(h.First, 2, 3);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.False(memory.SubtitlesOff);
        Assert.Equal("partial", memory.Subtitle!.Kind);
        var entry = Assert.Single(h.Activity.Entries, e => e.Kind == ActivityKinds.OverrideCreated);
        Assert.Equal("3", entry.Details!["SubtitleIndex"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AChangeRevertedWithinTheSettlePeriodRemembersNothing(bool audio, bool playbackInfo)
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(60);
        if (playbackInfo) await h.Request(h.First, audio ? 5 : null, audio ? null : -1);
        h.Report(h.First, audio ? 5 : 2, audio ? 4 : -1);
        var changed = h.Registry.Get(h.Session.Id)!;
        Assert.True(changed.ManualChanged);
        Assert.Equal(audio ? 5 : 2, changed.ExpectedAudio);
        Assert.Equal(audio ? 4 : -1, changed.ExpectedSubtitle);

        h.Now = T0.AddSeconds(60) + PlaybackMonitor.SettlePeriod - TimeSpan.FromTicks(1);
        if (playbackInfo) await h.Request(h.First, audio ? 2 : null, audio ? null : 4);
        h.Report(h.First, 2, 4);
        h.Settle(h.First, 2, 4);
        h.Now = T0.AddMinutes(5);
        h.Report(h.First, 2, 4);
        h.Stop(h.First);

        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.DoesNotContain(h.Activity.Entries, e => e.Kind == ActivityKinds.OverrideCreated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChangeStillSelectedOneSettlePeriodLaterIsRemembered(bool playbackInfo)
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(60);
        if (playbackInfo) await h.Request(h.First, audio: 5);
        h.Report(h.First, 5, 4);
        h.Now = T0.AddSeconds(60) + PlaybackMonitor.SettlePeriod - TimeSpan.FromTicks(1);
        h.Report(h.First, 5, 4);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);

        h.Now = T0.AddSeconds(60) + PlaybackMonitor.SettlePeriod;
        h.Report(h.First, 5, 4);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal("fr", memory.Audio!.Language);
        Assert.Null(memory.Subtitle);
        Assert.False(memory.SubtitlesOff);
    }

    [Fact]
    public void ANewerChangeReplacesTheHeldOneAndRestartsItsClock()
    {
        using var h = new Harness();
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(60);
        h.Report(h.First, 5, 4);
        h.Now = T0.AddSeconds(65);
        h.Report(h.First, 1, 4);
        h.Now = T0.AddSeconds(72);
        h.Report(h.First, 1, 4);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);

        h.Now = T0.AddSeconds(75);
        h.Report(h.First, 1, 4);
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
        Assert.Single(h.Activity.Entries, e => e.Kind == ActivityKinds.OverrideCreated);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(600, true)]
    public void AChangeStillSelectedAtTheStopIsRememberedOnceItHeldForTheSettlePeriod(int secondsBeforeStop, bool remembered)
    {
        using var h = new Harness();
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(60);
        h.Report(h.First, 2, -1);
        h.Now = T0.AddSeconds(60 + secondsBeforeStop);
        h.Stop(h.First);

        if (remembered) Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
        else Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.False(h.Registry.Get(h.Session.Id)!.Playing);
    }

    [Fact]
    public async Task AnInPlayRequestFromAnAppThatNeverReportsIndicesIsRememberedAtTheStop()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, null, null);
        h.Now = T0.AddSeconds(60);
        await h.Request(h.First, subtitle: -1);
        h.Now = T0.AddSeconds(70);
        h.Report(h.First, null, null);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);

        h.Now = T0.AddSeconds(80);
        h.Stop(h.First);
        Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public void AChangeStillSelectedWhenTheNextItemStartsWithoutAStopIsRememberedOnceItHeld(int secondsBeforeNext, bool remembered)
    {
        using var h = new Harness();
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(60);
        h.Report(h.First, 2, -1);
        h.Now = T0.AddSeconds(60 + secondsBeforeNext);
        h.Report(h.Second, 2, 4);
        h.Stop(h.First);

        if (remembered) Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
        else Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.Equal(h.Second.Id, h.Registry.Get(h.Session.Id)!.ItemId);
    }

    [Fact]
    public void AHeldChangeIsNotRememberedOnceADryRunStarts()
    {
        using var h = new Harness();
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(60);
        h.Report(h.First, 5, 4);
        h.Config.DryRun = true;
        h.Settle(h.First, 5, 4);
        h.Now = T0.AddMinutes(2);
        h.Stop(h.First);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Fact]
    public async Task APrePlayPickIsStillRememberedAtOnce()
    {
        using var h = new Harness();
        h.Config.AppModes.Single(mode => mode.Client == "Boundary Oracle").RememberPrePlayChoices = true;
        var source = Source();
        h.Stats.Record(h.User.Id, h.First.Id, source.Id,
            new DecisionRecord(h.Now, 1, -1, new TrackDecision(2, 4, Array.Empty<string>()), DecisionOutcome.Applied), source);
        h.Stats.RecordResponse(h.User.Id, source, h.Session.DeviceId, h.Session.Client);

        await h.Request(h.First, audio: 5);

        Assert.Equal("fr", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
        Assert.Null(h.Registry.Get(h.Session.Id)!.PendingAudio);
    }
}
