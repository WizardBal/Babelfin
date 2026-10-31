using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Playback;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

public sealed partial class PlaybackBoundaryAdversarialTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackInfoCommandEchoPreservesTheManualChoice(bool audio)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, audio ? 1 : 2, audio ? 4 : -1);
        Assert.Single(h.Commands);
        h.Now = start.AddSeconds(8);
        h.Report(h.First, audio ? 5 : 2, audio ? 4 : 3);
        h.Settle(h.First, audio ? 5 : 2, audio ? 4 : 3);
        // The start's correction command reaches the app late, still within its lifetime.
        h.Now = start.AddSeconds(22);
        await h.Request(h.First, 2, 4);
        h.Report(h.First, 2, 4);
        h.Settle(h.First, 2, 4);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal("fr", memory.Audio!.Language);
        else Assert.Equal("partial", memory.Subtitle!.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubtitleResetInTheAudioChangeReportIsNotRemembered(bool leaveAudio)
    {
        using var h = new Harness();
        h.LeaveAudioToVideo = leaveAudio;
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, leaveAudio ? 1 : 2, 4);
        h.Now = start.AddSeconds(60);
        h.Report(h.First, 5, -1);
        h.Settle(h.First, 5, -1);
        Assert.DoesNotContain(h.Store.TryGet(h.User.Id)!.Overrides, o => o.Manual?.SubtitlesOff == true);
    }

    [Fact]
    public async Task SubtitleResetAfterAudioLeftToVideoIsNotRemembered()
    {
        using var h = new Harness();
        h.LeaveAudioToVideo = true;
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, 1, 4);
        h.Now = start.AddSeconds(60);
        h.Report(h.First, 5, 4);
        h.Now = start.AddSeconds(66);
        h.Report(h.First, 5, -1);
        h.Settle(h.First, 5, -1);
        Assert.DoesNotContain(h.Store.TryGet(h.User.Id)!.Overrides, o => o.Manual?.SubtitlesOff == true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackInfoSubtitleResetAfterAudioChangeIsNotRemembered(bool audioRequest)
    {
        using var h = new Harness();
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = start.AddSeconds(60);
        if (audioRequest) await h.Request(h.First, 5);
        h.Report(h.First, 5, 4);
        h.Now = start.AddSeconds(66);
        await h.Request(h.First, subtitle: -1);
        h.Report(h.First, 5, -1);
        h.Settle(h.First, 5, -1);
        Assert.False(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachStreamsCommandExpiresWithoutRenewalByTheOther(bool audio)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, audio ? 1 : null, audio ? null : -1);
        Assert.Single(h.Commands);
        h.Now = start.AddSeconds(29);
        h.Report(h.First, 1, -1);
        Assert.Equal(2, h.Commands.Count);
        h.Now = start.AddSeconds(31);
        h.Report(h.First, audio ? 5 : 1, audio ? -1 : 3);
        h.Now = start.AddSeconds(35);
        h.Report(h.First, audio ? 2 : 1, audio ? -1 : 4);
        h.Settle(h.First, audio ? 2 : 1, audio ? -1 : 4);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal("ja", memory.Audio!.Language);
        else Assert.Equal("full", memory.Subtitle!.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackInfoEchoAcknowledgesOnlyTheMatchingStream(bool audio)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        await h.Request(h.First);
        h.Report(h.First, 1, -1);
        h.Now = T0.AddSeconds(2);
        await h.Request(h.First, audio ? 2 : null, audio ? null : 4);
        var play = h.Registry.Get(h.Session.Id)!;
        Assert.Equal(audio ? null : 2, play.CommandedAudio);
        Assert.Equal(audio ? 4 : null, play.CommandedSubtitle);
        Assert.Equal(audio ? null : T0, play.AudioCommandSentAt);
        Assert.Equal(audio ? T0 : null, play.SubtitleCommandSentAt);
        Assert.Equal(T0, play.CorrectionSentAt);
        h.Report(h.First, audio ? 2 : 1, audio ? -1 : 4);
        h.Settle(h.First, audio ? 2 : 1, audio ? -1 : 4);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcknowledgedPlaybackInfoCorrectionAllowsALaterManualReturn(bool audio)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        await h.Request(h.First);
        h.Report(h.First, audio ? 1 : 2, audio ? 4 : -1);
        h.Now = T0.AddSeconds(2);
        await h.Request(h.First, 2, 4);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(8);
        h.Report(h.First, audio ? 5 : 2, audio ? 4 : 3);
        h.Settle(h.First, audio ? 5 : 2, audio ? 4 : 3);
        h.Now = T0.AddSeconds(22);
        await h.Request(h.First, 2, 4);
        h.Report(h.First, 2, 4);
        h.Settle(h.First, 2, 4);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal("ja", memory.Audio!.Language);
        else Assert.Equal("full", memory.Subtitle!.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackInfoEchoDoesNotHideAManualPickOnTheOtherStream(bool audio)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        await h.Request(h.First);
        h.Report(h.First, audio ? 1 : 2, audio ? 4 : -1);
        h.Now = T0.AddSeconds(8);
        h.Report(h.First, audio ? 5 : 2, audio ? 4 : 3);
        h.Settle(h.First, audio ? 5 : 2, audio ? 4 : 3);
        h.Now = T0.AddSeconds(22);
        await h.Request(h.First, audio ? 2 : 5, audio ? 3 : 4);
        h.Report(h.First, audio ? 2 : 5, audio ? 3 : 4);
        h.Settle(h.First, audio ? 2 : 5, audio ? 3 : 4);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal("fr", memory.Audio!.Language);
        Assert.Equal("partial", memory.Subtitle!.Kind);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    public async Task PlaybackInfoCommandExpiryIncludesExactlyThirtySeconds(bool audio, long extraTicks)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        await h.Request(h.First);
        h.Report(h.First, audio ? 1 : 2, audio ? 4 : -1);
        h.Now = T0.AddSeconds(8);
        h.Report(h.First, audio ? 5 : 2, audio ? 4 : 3);
        h.Settle(h.First, audio ? 5 : 2, audio ? 4 : 3);
        h.Now = T0.AddSeconds(30).AddTicks(extraTicks);
        await h.Request(h.First, 2, 4);
        h.Report(h.First, 2, 4);
        h.Settle(h.First, 2, 4);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal(extraTicks == 0 ? "fr" : "ja", memory.Audio!.Language);
        else Assert.Equal(extraTicks == 0 ? "partial" : "full", memory.Subtitle!.Kind);
    }

    [Fact]
    public void CommandAcknowledgementAndPlayResetClearIndicesAndTimestampsTogether()
    {
        var play = new PlayRecord { HasExpected = true, ChoiceAudio = 2, ChoiceSubtitle = 4, ExpectedAudio = 2, ExpectedSubtitle = 4 };
        PlaybackMonitor.Evaluate(play, 1, -1, T0, CorrectAfterStartMode.On, 5, false, null);
        Assert.Equal(T0, play.AudioCommandSentAt);
        Assert.Equal(T0, play.SubtitleCommandSentAt);
        PlaybackMonitor.Evaluate(play, 2, null, T0.AddSeconds(1), CorrectAfterStartMode.On, 5, false, null);
        Assert.Null(play.CommandedAudio);
        Assert.Null(play.AudioCommandSentAt);
        Assert.Equal(4, play.CommandedSubtitle);
        Assert.Equal(T0, play.SubtitleCommandSentAt);
        var copy = play.Clone();
        play.ResetPlay();
        Assert.Null(play.CommandedSubtitle);
        Assert.Null(play.SubtitleCommandSentAt);
        Assert.Null(play.CorrectionSentAt);
        Assert.Null(play.LastObservedAudio);
        Assert.Null(play.LastAudioChangedAt);
        Assert.Null(play.LastSubtitleChangedAt);
        Assert.Equal(4, copy.CommandedSubtitle);
        Assert.Equal(T0, copy.SubtitleCommandSentAt);
    }

    [Fact]
    public void IndexlessProgressExpiresEachPendingCommand()
    {
        var play = new PlayRecord { HasExpected = true, ChoiceAudio = 2, ChoiceSubtitle = 4, ExpectedAudio = 2, ExpectedSubtitle = 4 };
        PlaybackMonitor.Evaluate(play, 1, -1, T0, CorrectAfterStartMode.On, 5, false, null);
        PlaybackMonitor.Evaluate(play, null, null, T0.AddSeconds(31), CorrectAfterStartMode.On, 5, false, null);
        Assert.Null(play.CommandedAudio);
        Assert.Null(play.AudioCommandSentAt);
        Assert.Null(play.CommandedSubtitle);
        Assert.Null(play.SubtitleCommandSentAt);
        Assert.Equal(T0, play.CorrectionSentAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingMediaSourceClearsPendingCommands(bool playbackInfo)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        await h.Request(h.First);
        h.Report(h.First, 1, -1);
        h.Now = T0.AddSeconds(8);
        h.SourceId = "alternate-source";
        h.Config.CorrectAfterStart = CorrectAfterStartMode.Off;
        if (playbackInfo) await h.Request(h.First);
        else h.Report(h.First, 1, -1);
        var play = h.Registry.Get(h.Session.Id)!;
        Assert.Null(play.CommandedAudio);
        Assert.Null(play.AudioCommandSentAt);
        Assert.Null(play.CommandedSubtitle);
        Assert.Null(play.SubtitleCommandSentAt);
        Assert.Null(play.CorrectionSentAt);
    }
}
