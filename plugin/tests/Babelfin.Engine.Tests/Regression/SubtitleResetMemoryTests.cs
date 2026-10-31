using System;
using System.Threading.Tasks;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

public sealed partial class PlaybackBoundaryAdversarialTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    public async Task SubtitleOffResetWindowEndsAfterExactlyTenSeconds(bool leaveAudio, bool playbackInfo, long extraTicks)
    {
        using var h = new Harness();
        h.LeaveAudioToVideo = leaveAudio;
        await h.Request(h.First);
        h.Report(h.First, leaveAudio ? 1 : 2, 4);
        h.Now = T0.AddSeconds(60);
        if (playbackInfo) await h.Request(h.First, audio: 5);
        else h.Report(h.First, 5, 4);
        h.Now = T0.AddSeconds(69);
        h.Report(h.First, 5, 4); // Repeating the same audio must not extend the reset window.
        h.Now = T0.AddSeconds(70).AddTicks(extraTicks);
        if (playbackInfo) await h.Request(h.First, subtitle: -1);
        h.Report(h.First, 5, -1);
        h.Settle(h.First, 5, -1);
        Assert.Equal(extraTicks > 0, h.Store.TryGet(h.User.Id)!.Overrides.Exists(o => o.Manual?.SubtitlesOff == true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackInfoAudioAndSubtitleResetInOneRequestDoNotRememberOff(bool leaveAudio)
    {
        using var h = new Harness();
        h.LeaveAudioToVideo = leaveAudio;
        await h.Request(h.First);
        h.Report(h.First, leaveAudio ? 1 : 2, 4);
        h.Now = T0.AddSeconds(60);
        var request = await h.Request(h.First, 5, -1);
        Assert.Equal(-1, request.ActionArguments["subtitleStreamIndex"]);
        h.Report(h.First, 5, -1);
        h.Settle(h.First, 5, -1);
        Assert.DoesNotContain(h.Store.TryGet(h.User.Id)!.Overrides, o => o.Manual?.SubtitlesOff == true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AManualSubtitlePickAfterAnyAudioSwitchAllowsDeliberateOff(bool leaveAudio, bool playbackInfo)
    {
        using var h = new Harness();
        h.LeaveAudioToVideo = leaveAudio;
        await h.Request(h.First);
        h.Report(h.First, leaveAudio ? 1 : 2, 4);
        h.Now = T0.AddSeconds(60);
        h.Report(h.First, 5, 4);
        h.Now = T0.AddSeconds(62);
        if (playbackInfo) await h.Request(h.First, subtitle: 3);
        h.Report(h.First, 5, 3);
        h.Now = T0.AddSeconds(66);
        if (playbackInfo) await h.Request(h.First, subtitle: -1);
        h.Report(h.First, 5, -1);
        h.Settle(h.First, 5, -1);
        Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
    }
}
