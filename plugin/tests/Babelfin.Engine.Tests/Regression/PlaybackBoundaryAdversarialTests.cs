using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Fx = Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Regression;

// These tests describe user-visible event sequences. Time is controlled independently of
// the monitor; no assertion derives its expected result from the implementation's flags.
[Collection("Playback response safety globals")]
public sealed partial class PlaybackBoundaryAdversarialTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false, CorrectAfterStartMode.Off)]
    [InlineData(true, CorrectAfterStartMode.Off)]
    [InlineData(false, CorrectAfterStartMode.Auto)]
    [InlineData(true, CorrectAfterStartMode.Auto)]
    [InlineData(false, CorrectAfterStartMode.On)]
    [InlineData(true, CorrectAfterStartMode.On)]
    public void OffAtStartupThenLateRequestedOnDoesNotCreateShowMemory(bool audioOnlyFirst, CorrectAfterStartMode correction)
    {
        using var h = new Harness();
        var play = Prepared(h);
        if (audioOnlyFirst) RememberIfManual(h, PlaybackMonitor.Evaluate(play, 2, null, T0, correction, 5, false, null));
        var baseline = PlaybackMonitor.Evaluate(play, 2, -1, T0.AddSeconds(1), correction, 5, false, null);
        RememberIfManual(h, baseline);
        Assert.False(baseline.Manual);

        // An app can finish applying the tracks from PlaybackInfo long after its first report.
        var applied = PlaybackMonitor.Evaluate(play, 2, 4, T0.AddMinutes(20), correction, 5, false, null);
        RememberIfManual(h, applied);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.False(applied.Manual);
        Assert.False(play.ManualChanged);
        var manualOff = PlaybackMonitor.Evaluate(play, 2, -1, T0.AddMinutes(21), correction, 5, false, null);
        Assert.True(manualOff.Manual);
        RememberIfManual(h, manualOff);
        Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
    }

    [Fact]
    public void AudioOnlyReportsUntilLateExpectedSubtitleDoNotInventAManualChange()
    {
        var play = new PlayRecord { HasExpected = true, ChoiceAudio = 2, ChoiceSubtitle = 4, ExpectedAudio = 2, ExpectedSubtitle = 4 };
        foreach (var seconds in new[] { 0, 1, 6, 30, 1200 })
        {
            var plan = PlaybackMonitor.Evaluate(play, 2, null, T0.AddSeconds(seconds), CorrectAfterStartMode.Off, 5, false, null);
            Assert.False(plan.Manual);
            Assert.Null(play.LastReportedSubtitle);
        }
        var applied = PlaybackMonitor.Evaluate(play, null, 4, T0.AddMinutes(21), CorrectAfterStartMode.Off, 5, false, null);
        Assert.False(applied.Manual);
        Assert.False(play.ManualChanged);
        var manual = PlaybackMonitor.Evaluate(play, null, -1, T0.AddMinutes(22), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(manual.Manual);
        Assert.Equal(4, manual.PreviousSubtitle);
        Assert.Equal(-1, manual.ManualSubtitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealOffAfterAcknowledgedOnIsRememberedAndWinsOnNextEpisode(bool audioOnlyFirst)
    {
        using var h = new Harness();
        var play = Prepared(h);
        if (audioOnlyFirst) PlaybackMonitor.Evaluate(play, 2, null, T0, CorrectAfterStartMode.Off, 5, false, null);
        PlaybackMonitor.Evaluate(play, 2, 4, T0.AddSeconds(1), CorrectAfterStartMode.Off, 5, false, null);
        var manualOff = PlaybackMonitor.Evaluate(play, 2, -1, T0.AddMinutes(20), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(manualOff.Manual);
        RememberIfManual(h, manualOff);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.Equal(OverrideScope.Series, memory.Scope);
        Assert.Equal(Ids.N(Ids.Series), memory.ItemId);
        Assert.True(memory.Manual!.SubtitlesOff);
        Assert.Null(memory.Manual.Subtitle);

        var next = Fx.Decide(new[] { Fx.Audio(2, "jpn"), Fx.Sub(17, "eng", "Full dialogue") },
            Fx.OriginalAudio("en"), Fx.Item(original: "ja"), manual: new ManualOverride(null, memory.Manual.SubtitlesOff, null));
        Assert.Null(next.SubtitleIndex);
        Assert.Contains(next.Trace, line => line.Code == "subs.manual.off");
        Assert.False(PlaybackMonitor.Evaluate(play, 2, -1, T0.AddMinutes(21), CorrectAfterStartMode.Off, 5, false, null).Manual);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("error-result")]
    [InlineData("exception-result")]
    public async Task PlaybackInfoWithoutAnyStartOrProgressIsNotActivePlayback(string completion)
    {
        using var h = new Harness();
        if (completion == "throw") await Assert.ThrowsAsync<IOException>(() => h.Request(h.First, completion: completion));
        else if (completion == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Request(h.First, completion: completion));
        else await h.Request(h.First, completion: completion);

        var entry = h.Registry.Get(h.Session.Id);
        Assert.True(entry is null || !entry.Playing, "Preparing, failing, or cancelling PlaybackInfo is not proof that an item started playing.");
        Assert.True(entry?.StartedAt is null);
        Assert.True(entry?.LastProgressAt is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstAudioOnlyReportKeepsPreparedTargetsAndLateSubtitleAcknowledgement(bool noSessionAtPreparation)
    {
        using var h = new Harness();
        h.SessionVisible = !noSessionAtPreparation;
        await h.Request(h.First);
        h.SessionVisible = true;
        var lookupsBeforeStart = h.SourceLookupCalls;
        h.Now = T0.AddHours(1); // The diagnostic cache cannot accidentally rescue a dropped expectation.
        h.SourceLookupFails = true; // The start must consume the prepared choice, without a fresh lookup.
        h.Report(h.First, 2, null);
        var first = h.Registry.Get(h.Session.Id)!;
        Assert.True(first.Playing);
        Assert.True(first.HasExpected);
        Assert.Equal(2, first.ExpectedAudio);
        Assert.Equal(4, first.ExpectedSubtitle);
        Assert.Null(first.LastReportedSubtitle);
        Assert.Equal(lookupsBeforeStart, h.SourceLookupCalls);
        h.Now = h.Now.AddMinutes(20);
        h.Report(h.First, 2, 4);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.False(h.Registry.Get(h.Session.Id)!.ManualChanged);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void NoPlaybackInfoFallbackDoesNotRememberLateBabelfinApplicationAsAManualShowChoice(bool audioOnlyFirst, int initialAudio)
    {
        using var h = new Harness();
        // No PlaybackInfo: the monitor independently obtains Babelfin's choice on the first report.
        h.Report(h.First, initialAudio, audioOnlyFirst ? null : -1);
        Assert.True(h.Registry.Get(h.Session.Id)!.HasExpected);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        if (audioOnlyFirst)
        {
            h.Now = T0.AddSeconds(1);
            h.Report(h.First, initialAudio, -1);
        }
        h.Now = T0.AddMinutes(20);
        h.Report(h.First, 2, 4);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.False(h.Registry.Get(h.Session.Id)!.ManualChanged);

        h.Now = T0.AddMinutes(21);
        h.Report(h.First, 2, -1);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.True(memory.SubtitlesOff);
        Assert.Null(memory.Subtitle);
        Assert.Null(memory.Audio);
    }

    [Fact]
    public async Task TrustedAppChoiceIsLoggedAtInformationWithoutPaths()
    {
        using var h = new Harness();
        h.Config.AppModes.Single(a => a.Client == "Boundary Oracle").Mode = AppModeKind.Trust;
        h.Config.UnknownAppMode = AppModeKind.Trust;
        await h.Request(h.First, 1, 3);
        var line = Assert.Single(h.Logs, e => e.Level == LogLevel.Information && e.Message.Contains("the app's own choice", StringComparison.Ordinal));
        Assert.DoesNotContain("C:\\", line.Message);
        Assert.DoesNotContain("\\", line.Message);
    }

    [Fact]
    public void NoPlaybackInfoFallbackStillRemembersAGenuineOffBeforeBabelfinsChoiceWasEverReported()
    {
        using var h = new Harness();
        h.Report(h.First, 2, 3); // A trusted app-selected subtitle, distinct from Babelfin's #4.
        h.Now = T0.AddMinutes(20);
        h.Report(h.First, 2, -1);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.True(memory.SubtitlesOff);
        Assert.Null(memory.Subtitle);
        Assert.Null(memory.Audio);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeferredChoiceOnOneStreamDoesNotContaminateTheOtherStreamsManualMemory(bool deferredAudio)
    {
        using var h = new Harness();
        h.Report(h.First, 1, 3); // Both app startup picks differ from Babelfin's 2/4.
        if (deferredAudio)
        {
            h.Now = T0.AddMinutes(19);
            h.Report(h.First, 2, 3); // A later deliberate Off is outside the audio reset window.
        }
        h.Now = T0.AddMinutes(20);
        h.Report(h.First, deferredAudio ? 2 : 5, deferredAudio ? -1 : 4);
        var first = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (deferredAudio)
        {
            Assert.True(first.SubtitlesOff);
            Assert.Null(first.Subtitle);
            Assert.Null(first.Audio);
        }
        else
        {
            Assert.Equal("fr", first.Audio!.Language);
            Assert.False(first.SubtitlesOff);
            Assert.Null(first.Subtitle);
        }

        // Returning to the original choice after changing that stream manually is a new pick.
        // Only the unrelated stream's first deferred acknowledgement stays out of memory.
        h.Now = T0.AddMinutes(21);
        h.Report(h.First, 2, 4);
        var afterAcknowledgement = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal(deferredAudio ? null : "ja", afterAcknowledgement.Audio?.Language);
        Assert.False(afterAcknowledgement.SubtitlesOff);
        Assert.Equal(deferredAudio ? "en" : null, afterAcknowledgement.Subtitle?.Language);

        // Both choice targets are now acknowledged: a later return to an app pick is manual.
        if (deferredAudio)
        {
            h.Now = T0.AddMinutes(21).AddSeconds(30);
            h.Report(h.First, 1, 4);
        }
        h.Now = T0.AddMinutes(22);
        h.Report(h.First, deferredAudio ? 1 : 2, -1);
        var final = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal(deferredAudio ? "en" : "ja", final.Audio!.Language);
        Assert.True(final.SubtitlesOff);
        Assert.Null(final.Subtitle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangingOneChoiceTargetDoesNotResetTheOtherStreamsAcknowledgement(bool changeAudioTarget)
    {
        var play = new PlayRecord { HasExpected = true, ChoiceAudio = 2, ChoiceSubtitle = 4, ExpectedAudio = 2, ExpectedSubtitle = 4 };
        PlaybackMonitor.Evaluate(play, 2, 4, T0, CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(PlaybackMonitor.Evaluate(play, 1, -1, T0.AddMinutes(20), CorrectAfterStartMode.Off, 5, false, null).Manual);
        if (changeAudioTarget) play.ChoiceAudio = 5;
        else play.ChoiceSubtitle = 3;

        var mixed = PlaybackMonitor.Evaluate(play, changeAudioTarget ? 5 : 2, changeAudioTarget ? 4 : 3,
            T0.AddMinutes(21), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(mixed.Manual);
        if (changeAudioTarget)
        {
            Assert.Equal(mixed.ManualAudio, mixed.PreviousAudio);
            Assert.Equal(-1, mixed.PreviousSubtitle);
            Assert.Equal(4, mixed.ManualSubtitle);
        }
        else
        {
            Assert.Equal(mixed.ManualSubtitle, mixed.PreviousSubtitle);
            Assert.Equal(1, mixed.PreviousAudio);
            Assert.Equal(2, mixed.ManualAudio);
        }
        Assert.Equal(changeAudioTarget ? 5 : 2, play.ExpectedAudio);
        Assert.Equal(changeAudioTarget ? 4 : 3, play.ExpectedSubtitle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnIndexlessReportStillObservesChoiceTargetChangesBeforeTheChoiceReturns(bool changeAudioTarget)
    {
        var play = new PlayRecord { HasExpected = true, ChoiceAudio = 2, ChoiceSubtitle = 4, ExpectedAudio = 2, ExpectedSubtitle = 4 };
        PlaybackMonitor.Evaluate(play, 2, 4, T0, CorrectAfterStartMode.Off, 5, false, null);
        PlaybackMonitor.Evaluate(play, 1, 4, T0.AddMinutes(19), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(PlaybackMonitor.Evaluate(play, 1, -1, T0.AddMinutes(20), CorrectAfterStartMode.Off, 5, false, null).Manual);

        // PlaybackInfo can update a target while the app is omitting both reported indices.
        if (changeAudioTarget) play.ChoiceAudio = 5;
        else play.ChoiceSubtitle = 3;
        Assert.False(PlaybackMonitor.Evaluate(play, null, null, T0.AddMinutes(21), CorrectAfterStartMode.Off, 5, false, null).Manual);
        if (changeAudioTarget) play.ChoiceAudio = 2;
        else play.ChoiceSubtitle = 4;

        var applied = PlaybackMonitor.Evaluate(play, changeAudioTarget ? 2 : null, changeAudioTarget ? null : 4,
            T0.AddMinutes(22), CorrectAfterStartMode.Off, 5, false, null);
        Assert.False(applied.Manual);
        Assert.Equal(changeAudioTarget ? 2 : 1, play.ExpectedAudio);
        Assert.Equal(changeAudioTarget ? -1 : 4, play.ExpectedSubtitle);
    }

    [Fact]
    public void APreviouslyAcknowledgedOffChoiceStillCountsWhenSelectedManuallyAfterNegativeIndexNormalization()
    {
        var play = new PlayRecord { HasExpected = true, ChoiceAudio = 2, ChoiceSubtitle = -1, ExpectedAudio = 2, ExpectedSubtitle = -1 };
        PlaybackMonitor.Evaluate(play, 2, -1, T0, CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(PlaybackMonitor.Evaluate(play, 2, 4, T0.AddMinutes(20), CorrectAfterStartMode.Off, 5, false, null).Manual);
        play.ChoiceSubtitle = -2; // The same logical Off target, not a new choice.
        var off = PlaybackMonitor.Evaluate(play, 2, -1, T0.AddMinutes(21), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(off.Manual);
        Assert.Equal(4, off.PreviousSubtitle);
        Assert.Equal(-1, off.ManualSubtitle);
    }

    [Fact]
    public void ReplayingTheSameItemDoesNotInheritChoiceAcknowledgementsFromThePreviousPlay()
    {
        using var h = new Harness();
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(20);
        h.Stop(h.First);
        h.Now = T0.AddMinutes(21);
        h.Report(h.First, 1, -1);
        h.Now = T0.AddMinutes(40);
        h.Report(h.First, 2, 4);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.False(h.Registry.Get(h.Session.Id)!.ManualChanged);
        h.Now = T0.AddMinutes(41);
        h.Report(h.First, 2, -1);
        Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
    }

    [Theory]
    [InlineData(CorrectAfterStartMode.Auto)]
    [InlineData(CorrectAfterStartMode.On)]
    public void AudioFirstReportCannotSpendTheUnreportedSubtitleCorrection(CorrectAfterStartMode correction)
    {
        var play = new PlayRecord { HasExpected = true, ChoiceAudio = 2, ChoiceSubtitle = 4, ExpectedAudio = 2, ExpectedSubtitle = 4 };
        var commands = new[] { GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex };
        var audio = PlaybackMonitor.Evaluate(play, 1, null, T0, correction, 5, false, commands);
        Assert.Equal(2, audio.SendAudio);
        Assert.Null(audio.SendSubtitle);
        var subtitle = PlaybackMonitor.Evaluate(play, 2, 3, T0.AddSeconds(10), correction, 5, false, commands);
        Assert.Equal(4, subtitle.SendSubtitle);
        Assert.Null(subtitle.SendAudio);
        Assert.False(subtitle.Manual);
        var repeated = PlaybackMonitor.Evaluate(play, 2, 3, T0.AddSeconds(11), correction, 5, false, commands);
        Assert.False(repeated.HasCommands);
        Assert.False(PlaybackMonitor.Evaluate(play, 2, 4, T0.AddSeconds(12), correction, 5, false, commands).Manual);
        Assert.True(PlaybackMonitor.Evaluate(play, 2, -1, T0.AddSeconds(21), correction, 5, false, commands).Manual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHandlerDispatchesEachDeferredStreamCorrectionAndRemembersOnlyLaterManualOff(bool audioFirst)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.Auto;
        h.Session.Capabilities = new ClientCapabilities
        {
            SupportedCommands = new[] { GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex },
        };
        await h.Request(h.First);
        h.Report(h.First, audioFirst ? 1 : null, audioFirst ? null : 3);
        var first = Assert.Single(h.Commands);
        Assert.Equal(audioFirst ? GeneralCommandType.SetAudioStreamIndex : GeneralCommandType.SetSubtitleStreamIndex, first.Name);
        Assert.Equal(audioFirst ? "2" : "4", first.Arguments["Index"]);
        h.Now = T0.AddSeconds(10);
        h.Report(h.First, audioFirst ? 2 : 1, audioFirst ? 3 : 4);
        Assert.Equal(2, h.Commands.Count);
        var late = h.Commands[1];
        Assert.Equal(audioFirst ? GeneralCommandType.SetSubtitleStreamIndex : GeneralCommandType.SetAudioStreamIndex, late.Name);
        Assert.Equal(audioFirst ? "4" : "2", late.Arguments["Index"]);
        h.Now = T0.AddMinutes(20);
        h.Report(h.First, 2, 4);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.Equal(2, h.Commands.Count);
        h.Now = T0.AddMinutes(21);
        h.Report(h.First, 2, -1);
        Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
        Assert.Equal(2, h.Commands.Count);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task AutoplayFreshnessHasAnInclusiveTwoMinuteBoundary(long ticksBeyondTwoMinutes, bool autoplay)
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(2).AddTicks(ticksBeyondTwoMinutes);
        var request = await h.Request(h.Second, audio: 1, subtitle: -1);
        Assert.Equal(autoplay ? 2 : 1, request.ActionArguments["audioStreamIndex"]);
        Assert.Equal(autoplay ? 4 : -1, request.ActionArguments["subtitleStreamIndex"]);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Fact]
    public async Task LongRunningPlaybackWithRecentProgressStillTriggersAutoplay()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddHours(6).AddMinutes(1);
        h.Report(h.First, 2, 4);
        h.Now = h.Now.AddSeconds(30);
        var request = await h.Request(h.Second, audio: 1, subtitle: -1);
        Assert.Equal(2, request.ActionArguments["audioStreamIndex"]);
        Assert.Equal(4, request.ActionArguments["subtitleStreamIndex"]);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Fact]
    public async Task TouchingTheRegistryWithoutProgressDoesNotRefreshPlaybackEvidence()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddHours(6);
        h.Registry.Update(h.Session.Id, p => p.DeviceName = "Recently refreshed display name");
        var request = await h.Request(h.Second, audio: 1, subtitle: -1);
        Assert.Equal(1, request.ActionArguments["audioStreamIndex"]);
        Assert.Equal(-1, request.ActionArguments["subtitleStreamIndex"]);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecentActualPlaybackDetectsNextItemBeforeItsPredecessorStops(bool indicesWereReported)
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, indicesWereReported ? 2 : null, indicesWereReported ? 4 : null);
        h.Now = T0.AddSeconds(30);
        var request = await h.Request(h.Second, audio: 1, subtitle: -1);
        Assert.Equal(2, request.ActionArguments["audioStreamIndex"]);
        Assert.Equal(4, request.ActionArguments["subtitleStreamIndex"]);
        Assert.Equal(h.First.Id, h.Registry.Get(h.Session.Id)!.ItemId);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Theory]
    [InlineData(121)]
    [InlineData(21600)]
    [InlineData(21601)]
    public async Task OldPlayingFlagWithoutRecentProgressDoesNotProveAutoplay(int secondsSinceProgress)
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(secondsSinceProgress);
        var request = await h.Request(h.Second, audio: 1, subtitle: -1);
        Assert.Equal(1, request.ActionArguments["audioStreamIndex"]);
        Assert.Equal(-1, request.ActionArguments["subtitleStreamIndex"]);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task UnstartedPreparationCannotMakeTheFollowingUnrelatedRequestAutoplay(string completion)
    {
        using var h = new Harness();
        if (completion == "throw") await Assert.ThrowsAsync<IOException>(() => h.Request(h.First, completion: completion));
        else if (completion == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Request(h.First, completion: completion));
        else await h.Request(h.First, completion: completion);
        h.Now = T0.AddSeconds(1);
        var request = await h.Request(h.Second, audio: 1, subtitle: -1);
        Assert.Equal(1, request.ActionArguments["audioStreamIndex"]);
        Assert.Equal(-1, request.ActionArguments["subtitleStreamIndex"]);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NextItemPrefetchThenStopAndStartKeepsChoiceAndNewPlayIdentity(bool stopBeforeNextStart)
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(30);
        await h.Request(h.Second, audio: 1, subtitle: -1);
        Assert.Equal(h.First.Id, h.Registry.Get(h.Session.Id)!.ItemId);
        if (stopBeforeNextStart) h.Stop(h.First);
        h.Now = T0.AddSeconds(31);
        h.Report(h.Second, 2, null);
        if (!stopBeforeNextStart) h.Stop(h.First);
        var next = h.Registry.Get(h.Session.Id)!;
        Assert.Equal(h.Second.Id, next.ItemId);
        Assert.True(next.Playing);
        Assert.True(next.HasExpected);
        Assert.Equal(4, next.ExpectedSubtitle);
        h.Now = T0.AddMinutes(20);
        h.Report(h.Second, 2, 4);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        Assert.Equal(h.Second.Id, h.Registry.Get(h.Session.Id)!.ItemId);
    }

    [Fact]
    public async Task FailedNextItemPrefetchDoesNotChangeTheActuallyPlayingItem()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddSeconds(10);
        await Assert.ThrowsAsync<IOException>(() => h.Request(h.Second, completion: "throw"));
        var active = h.Registry.Get(h.Session.Id)!;
        Assert.Equal(h.First.Id, active.ItemId);
        Assert.True(active.Playing);
        var pending = h.Registry.GetPending(h.Session.DeviceId, h.Session.Client, h.Second.Id);
        Assert.True(pending is null || !pending.Playing);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Fact]
    public async Task LongPauseStillPreservesSameItemManualRequestInReplaceUnlessManualMode()
    {
        using var h = new Harness();
        h.Config.FindAppMode("Boundary Oracle")!.Mode = AppModeKind.ReplaceUnlessManual;
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Session.PlayState.IsPaused = true;
        h.Now = T0.AddMinutes(30);
        var request = await h.Request(h.First, audio: 1, subtitle: 3);
        Assert.Equal(1, request.ActionArguments["audioStreamIndex"]);
        Assert.Equal(3, request.ActionArguments["subtitleStreamIndex"]);
        var manual = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal("partial", manual.Subtitle!.Kind);
        Assert.Equal("en", manual.Audio!.Language);
    }

    [Fact]
    public void ReturningToTheOriginalTracksReplacesThePreviousShowMemory()
    {
        using var h = new Harness();
        var play = Prepared(h);
        RememberIfManual(h, PlaybackMonitor.Evaluate(play, 1, 3, T0, CorrectAfterStartMode.Off, 5, false, null));
        RememberIfManual(h, PlaybackMonitor.Evaluate(play, 5, 3, T0.AddMinutes(1), CorrectAfterStartMode.Off, 5, false, null));
        RememberIfManual(h, PlaybackMonitor.Evaluate(play, 5, -1, T0.AddMinutes(2), CorrectAfterStartMode.Off, 5, false, null));
        var old = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal("fr", old.Audio!.Language);
        Assert.True(old.SubtitlesOff);
        var back = PlaybackMonitor.Evaluate(play, 2, 4, T0.AddMinutes(3), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(back.Manual);
        RememberIfManual(h, back);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal("ja", memory.Audio!.Language);
        Assert.False(memory.SubtitlesOff);
        Assert.Equal("en", memory.Subtitle!.Language);
        Assert.Equal(OverrideScope.Series, Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Scope);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackInfoManualPickRetiresOnlyThatStreamsPendingChoiceAcknowledgement(bool audio)
    {
        using var h = new Harness();
        await h.Request(h.First, 1, 3);
        h.Report(h.First, 1, 3);
        h.Now = T0.AddMinutes(2);
        await h.Request(h.First, audio ? 5 : 1, audio ? 3 : -1);
        var firstMemory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal("fr", firstMemory.Audio!.Language);
        else Assert.True(firstMemory.SubtitlesOff);
        h.Report(h.First, audio ? 5 : 1, audio ? 3 : -1);
        h.Now = T0.AddMinutes(3);
        h.Report(h.First, audio ? 2 : 1, audio ? 3 : 4);
        var finalMemory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal("ja", finalMemory.Audio!.Language);
        else { Assert.False(finalMemory.SubtitlesOff); Assert.Equal("en", finalMemory.Subtitle!.Language); }
    }
    [Fact]
    public async Task AudioPickedThroughPlaybackInfoDoesNotRetireTheSubtitlesFirstAcknowledgement()
    {
        using var h = new Harness();
        await h.Request(h.First, 1, 3);
        h.Report(h.First, 1, 3);
        h.Now = T0.AddMinutes(2);
        await h.Request(h.First, 5, null);
        h.Report(h.First, 5, 3);
        var before = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal("fr", before.Audio!.Language);
        Assert.Null(before.Subtitle);
        h.Now = T0.AddMinutes(3);
        h.Report(h.First, 5, 4);
        var after = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal("fr", after.Audio!.Language);
        Assert.Null(after.Subtitle);
        Assert.False(after.SubtitlesOff);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DelayedCorrectionEchoPreservesTheManualLanguage(bool audio, bool repeatRequest)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, audio ? 1 : 2, audio ? 4 : -1);
        Assert.Single(h.Commands);
        h.Now = start.AddSeconds(8);
        if (repeatRequest) await h.Request(h.First, audio ? 5 : 2, audio ? 4 : 3);
        h.Report(h.First, audio ? 5 : 2, audio ? 4 : 3);
        var before = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal("fr", before.Audio!.Language);
        else Assert.Equal("partial", before.Subtitle!.Kind);
        h.Now = start.AddSeconds(12);
        h.Report(h.First, 2, 4);
        var after = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal("fr", after.Audio!.Language);
        else Assert.Equal("partial", after.Subtitle!.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcknowledgedCorrectionAllowsALaterDeliberateReturn(bool audio)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, audio ? 1 : 2, audio ? 4 : -1);
        h.Now = start.AddSeconds(2);
        h.Report(h.First, 2, 4);
        h.Now = start.AddSeconds(8);
        h.Report(h.First, audio ? 5 : 2, audio ? 4 : 3);
        h.Now = start.AddSeconds(12);
        h.Report(h.First, 2, 4);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal("ja", memory.Audio!.Language);
        else Assert.Equal("full", memory.Subtitle!.Kind);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(35, true)]
    [InlineData(40, true)]
    [InlineData(60, true)]
    [InlineData(90, false)]
    public async Task IndexlessStartupAnchorsCorrectionAtTheFirstIndexedReport(int seconds, bool correct)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, null, null);
        h.Now = start.AddSeconds(seconds);
        h.Report(h.First, 1, -1);
        Assert.Equal(correct ? 2 : 0, h.Commands.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstLateTrackPickIsRememberedWithoutCorrection(bool audio)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, audio ? null : 2, audio ? 4 : null);
        h.Now = start.AddMinutes(10);
        h.Report(h.First, audio ? 5 : 2, audio ? 4 : 3);
        Assert.Empty(h.Commands);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        if (audio) Assert.Equal("fr", memory.Audio!.Language);
        else Assert.Equal("partial", memory.Subtitle!.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubtitleOffSoonAfterAudioSwitchIsNotRemembered(bool repeatRequest)
    {
        using var h = new Harness();
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = start.AddSeconds(60);
        if (repeatRequest) await h.Request(h.First, 5);
        h.Report(h.First, 5, 4);
        h.Now = start.AddSeconds(66);
        h.Report(h.First, 5, -1);
        Assert.False(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
        h.Now = start.AddSeconds(80);
        h.Report(h.First, 5, 3);
        h.Now = start.AddSeconds(90);
        h.Report(h.First, 5, -1);
        Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
    }

    [Fact]
    public async Task RepeatPlaybackInfoDoesNotCopyAnObservedIndexForAudioLeftToVideo()
    {
        using var h = new Harness();
        h.LeaveAudioToVideo = true;
        await h.Request(h.First);
        h.Report(h.First, 1, 4);
        h.Registry.Update(h.Session.Id, r => r.ExpectedAudio = 1);
        h.Now = h.Now.AddMinutes(1);
        await h.Request(h.First);
        var play = h.Registry.Get(h.Session.Id)!;
        Assert.Null(play.ChoiceAudio);
        Assert.Null(play.ExpectedAudio);
        Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASubtitlePickAfterTheAudioSwitchMakesFollowingOffDeliberate(bool repeatRequest)
    {
        using var h = new Harness();
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = start.AddSeconds(60);
        h.Report(h.First, 5, 4);
        h.Now = start.AddSeconds(62);
        if (repeatRequest) await h.Request(h.First, subtitle: 3);
        h.Report(h.First, 5, 3);
        h.Now = start.AddSeconds(66);
        h.Report(h.First, 5, -1);
        Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public async Task UnacknowledgedCorrectionStopsMaskingManualPicksAfterItsLifetime(int seconds, bool manual)
    {
        using var h = new Harness();
        h.Config.CorrectAfterStart = CorrectAfterStartMode.On;
        var start = h.Now;
        await h.Request(h.First);
        h.Report(h.First, 1, 4);
        h.Now = start.AddSeconds(8);
        h.Report(h.First, 5, 4);
        h.Now = start.AddSeconds(seconds);
        h.Report(h.First, 2, 4);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal(manual ? "ja" : "fr", memory.Audio!.Language);
    }

    private static PlayRecord Prepared(Harness h) => new()
    {
        UserId = h.User.Id, ItemId = h.First.Id, HasExpected = true,
        ExpectedAudio = 2, ExpectedSubtitle = 4, ChoiceAudio = 2, ChoiceSubtitle = 4,
    };

    private static void RememberIfManual(Harness h, ReportPlan p)
    {
        if (p.Manual) h.Recorder.Record(h.User, h.First, Source(), p.ManualAudio, p.ManualSubtitle, p.PreviousAudio, p.PreviousSubtitle, "Boundary Oracle");
    }

    private static MediaSourceInfo Source() => new()
    {
        Id = "oracle-source", DefaultAudioStreamIndex = 2, DefaultSubtitleStreamIndex = 4,
        MediaStreams = new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng", Codec = "aac", Channels = 2 },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = "jpn", Codec = "aac", Channels = 2 },
            new() { Index = 3, Type = MediaStreamType.Subtitle, Language = "eng", Codec = "srt", Title = "Signs and songs", IsForced = true },
            new() { Index = 4, Type = MediaStreamType.Subtitle, Language = "eng", Codec = "srt", Title = "Full dialogue" },
            new() { Index = 5, Type = MediaStreamType.Audio, Language = "fra", Codec = "aac", Channels = 2 },
        },
    };

    [Fact]
    public async Task NewerProgressChoiceSurvivesAnOlderDelayedRecording()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var claimed = 0;
        h.OnSourceLookup = () =>
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed source lookup was not released.");
        };
        var older = Task.Run(() => h.Report(h.First, 5, 4));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "The first manual choice did not reach source lookup.");
            h.Report(h.First, 1, 4);
            Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
        }
        finally { release.Set(); }
        await older;
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Fact]
    public async Task NewerProgressChoiceSurvivesAnOlderPlaybackInfoRequest()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.OnManualRequest = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed PlaybackInfo request was not released.");
        };
        var older = Task.Run(() => h.Request(h.First, audio: 5));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "The request did not reach its manual-choice decision.");
            h.Report(h.First, 1, 4);
            Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
        }
        finally { release.Set(); }
        await older;
        Assert.Equal(1, h.Registry.Get(h.Session.Id)!.ExpectedAudio);
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Fact]
    public async Task NewerSubtitleOffSurvivesAnOlderDelayedRecording()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var claimed = 0;
        h.OnSourceLookup = () =>
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed source lookup was not released.");
        };
        var older = Task.Run(() => h.Report(h.First, 2, 3));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            h.Report(h.First, 2, -1);
            Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
        }
        finally { release.Set(); }
        await older;
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.True(memory.SubtitlesOff);
        Assert.Null(memory.Subtitle);
    }

    [Fact]
    public async Task DelayedAudioAndNewerSubtitleChoicesBothSurvive()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var claimed = 0;
        h.OnSourceLookup = () =>
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed source lookup was not released.");
        };
        var older = Task.Run(() => h.Report(h.First, 5, 4));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            h.Now = T0.AddMinutes(3);
            h.Report(h.First, 5, -1);
            Assert.True(Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.SubtitlesOff);
        }
        finally { release.Set(); }
        await older;
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal("fr", memory.Audio!.Language);
        Assert.True(memory.SubtitlesOff);
    }

    [Fact]
    public async Task NewerEpisodeChoiceSurvivesAnEarlierEpisodesDelayedRecording()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var claimed = 0;
        h.OnSourceLookup = () =>
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed source lookup was not released.");
        };
        var older = Task.Run(() => h.Report(h.First, 5, 4));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            h.Report(h.Second, 2, 4);
            h.Now = T0.AddMinutes(4);
            h.Report(h.Second, 1, 4);
            Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
        }
        finally { release.Set(); }
        await older;
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Fact]
    public async Task StalePlaybackInfoAudioDoesNotDiscardItsIndependentSubtitleChoice()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.OnManualRequest = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed PlaybackInfo request was not released.");
        };
        var older = Task.Run(() => h.Request(h.First, audio: 5, subtitle: 3));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            h.Report(h.First, 1, 4);
        }
        finally { release.Set(); }
        await older;
        var play = h.Registry.Get(h.Session.Id)!;
        Assert.Equal(1, play.ExpectedAudio);
        Assert.Equal(3, play.ExpectedSubtitle);
        var memory = Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!;
        Assert.Equal("en", memory.Audio!.Language);
        Assert.NotNull(memory.Subtitle);
        Assert.False(memory.SubtitlesOff);
    }

    [Fact]
    public void InvalidNewerChoiceDoesNotPreventAnOlderValidChoiceFromBeingRemembered()
    {
        using var h = new Harness();
        var older = ManualChangeOrder.Next();
        var newer = ManualChangeOrder.Next();
        Assert.Null(h.Recorder.Record(h.User, h.First, Source(), 999, 4, 2, 4, h.Session.Client, newer));
        Assert.NotNull(h.Recorder.Record(h.User, h.First, Source(), 5, 4, 2, 4, h.Session.Client, older));
        Assert.Equal("fr", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Fact]
    public void OrderingMetadataFromAnEarlierProcessDoesNotBlockNewChoices()
    {
        using var h = new Harness();
        var legacy = ManualChangeOrder.Next();
        h.Recorder.Record(h.User, h.First, Source(), 5, 4, 2, 4, h.Session.Client, legacy);
        var person = h.Store.TryGet(h.User.Id)!.Clone();
        var memory = Assert.Single(person.Overrides).Manual!;
        memory.AudioOrderEpoch = Guid.NewGuid();
        memory.AudioOrder = long.MaxValue;
        h.Store.Save(person);
        h.Recorder.Record(h.User, h.First, Source(), 1, 4, 5, 4, h.Session.Client, ManualChangeOrder.Next());
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Fact]
    public async Task NewerProgressChoiceSurvivesAnOlderPlaybackInfoSourceLookup()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var claimed = 0;
        h.OnSourceLookup = () =>
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed request source lookup was not released.");
        };
        var older = Task.Run(() => h.Request(h.First, audio: 5));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            h.Report(h.First, 1, 4);
        }
        finally { release.Set(); }
        await older;
        Assert.Equal(1, h.Registry.Get(h.Session.Id)!.ExpectedAudio);
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackStartingDuringAnOlderPreparationKeepsItsNewerManualPick(bool prepared)
    {
        using var h = new Harness();
        h.Config.AppModes[0].Mode = AppModeKind.ReplaceUnlessManual;
        if (prepared) await h.Request(h.First);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.OnRequestDecision = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed preparation was not released.");
        };
        var older = Task.Run(() => h.Request(h.First, audio: 5));
        Guid playId;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            h.Report(h.First, 2, 4);
            h.Now = T0.AddMinutes(2);
            h.Report(h.First, 1, 4);
            playId = h.Registry.Get(h.Session.Id)!.PlayId;
        }
        finally { release.Set(); }
        var request = await older;
        var play = h.Registry.Get(h.Session.Id)!;
        Assert.Equal(playId, play.PlayId);
        Assert.True(play.Playing);
        Assert.Equal(1, play.ExpectedAudio);
        Assert.Equal(5, request.ActionArguments["audioStreamIndex"]);
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Fact]
    public async Task SessionBecomingVisibleDuringPreparationDoesNotLeaveStalePendingTracks()
    {
        using var h = new Harness();
        h.SessionVisible = false;
        h.Config.AppModes[0].Mode = AppModeKind.ReplaceUnlessManual;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.OnRequestDecision = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed preparation was not released.");
        };
        var older = Task.Run(() => h.Request(h.First, audio: 5));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            h.SessionVisible = true;
            h.Report(h.First, 2, 4);
            h.Now = T0.AddMinutes(2);
            h.Report(h.First, 1, 4);
        }
        finally { release.Set(); }
        var request = await older;
        Assert.Equal(1, h.Registry.Get(h.Session.Id)!.ExpectedAudio);
        Assert.Null(h.Registry.GetPending(h.Session.DeviceId, h.Session.Client, h.First.Id));
        Assert.Equal(5, request.ActionArguments["audioStreamIndex"]);
    }

    [Fact]
    public void ManualOrderingSurvivesCloningAndJsonRoundTrips()
    {
        using var h = new Harness();
        var older = ManualChangeOrder.Next();
        var newer = ManualChangeOrder.Next();
        h.Recorder.Record(h.User, h.First, Source(), 1, 4, 2, 4, h.Session.Client, newer);
        var serialized = JsonSerializer.Serialize(h.Store.TryGet(h.User.Id)!.Clone(), BabelfinJson.Options);
        h.Store.Save(JsonSerializer.Deserialize<PersonSettings>(serialized, BabelfinJson.Options)!);
        Assert.Null(h.Recorder.Record(h.User, h.First, Source(), 5, 4, 2, 4, h.Session.Client, older));
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Fact]
    public void LegacyManualMemoryWithoutOrderingMetadataAcceptsANewChoice()
    {
        using var h = new Harness();
        var person = h.Store.TryGet(h.User.Id)!.Clone();
        person.Overrides.Add(new ItemOverride
        {
            ItemId = Ids.Series.ToString("N"), Scope = OverrideScope.Series,
            Manual = JsonSerializer.Deserialize<ManualMemory>("{\"audio\":{\"language\":\"fr\"}}", BabelfinJson.Options),
        });
        h.Store.Save(person);
        Assert.NotNull(h.Recorder.Record(h.User, h.First, Source(), 1, 4, 5, 4, h.Session.Client));
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Fact]
    public void ManualOrderingIsIsolatedBetweenPeopleRememberingTheSameShow()
    {
        using var h = new Harness();
        var other = new User("Other", "Default", "Default") { Id = Guid.NewGuid() };
        var person = h.Store.TryGet(h.User.Id)!.Clone();
        person.UserId = other.Id;
        h.Store.Save(person);
        var older = ManualChangeOrder.Next();
        var newer = ManualChangeOrder.Next();
        h.Recorder.Record(h.User, h.First, Source(), 1, 4, 2, 4, h.Session.Client, newer);
        h.Recorder.Record(other, h.First, Source(), 5, 4, 2, 4, "Another app", older);
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
        Assert.Equal("fr", Assert.Single(h.Store.TryGet(other.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DelayedManualRecordingHonorsSelectionBeingDisabled(int disableScope)
    {
        using var h = new Harness();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.OnProfileResolved = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delayed profile lookup was not released.");
        };
        var pending = Task.Run(() => h.Recorder.Record(h.User, h.First, Source(), 5, 4, 2, 4, h.Session.Client));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "The manual choice did not reach profile resolution.");
            if (disableScope == 1)
            {
                var person = h.Store.TryGet(h.User.Id)!.Clone();
                person.Enabled = false;
                h.Store.Save(person);
            }
            else if (disableScope == 2)
            {
                h.Config.Enabled = false;
            }
        }
        finally { release.Set(); }
        var saved = await pending;
        if (disableScope == 0)
        {
            Assert.NotNull(saved);
            Assert.Equal("fr", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
        }
        else
        {
            Assert.Null(saved);
            Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
        }
    }

    private sealed class CallbackLogger : ILogger<PlaybackInfoFilter>
    {
        private readonly Action<LogLevel, string> _onMessage;
        internal CallbackLogger(Action<LogLevel, string> onMessage) => _onMessage = onMessage;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _onMessage(level, formatter(state, exception));
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly Plugin? _previous = Plugin.Instance;
        private readonly PlaybackInfoFilter _filter;
        private readonly PlaybackMonitor _monitor;
        internal DateTime Now = T0;
        internal bool SourceLookupFails;
        internal Action? OnSourceLookup;
        internal Action? OnManualRequest;
        internal Action? OnRequestDecision;
        internal Action? OnProfileResolved;
        internal bool LeaveAudioToVideo;
        internal string SourceId = "oracle-source";
        internal int SourceLookupCalls;
        internal bool SessionVisible = true;
        internal readonly PluginConfiguration Config;
        internal readonly User User = new("Oracle", "Default", "Default") { Id = Ids.Person };
        internal readonly Episode First = new() { Id = Ids.Item, SeriesId = Ids.Series, SeasonId = Ids.Season, Name = "Oracle episode A", SeriesName = "Oracle show" };
        internal readonly Episode Second = new() { Id = Guid.NewGuid(), SeriesId = Ids.Series, SeasonId = Ids.Season, Name = "Oracle episode B", SeriesName = "Oracle show" };
        internal readonly FakePersonStore Store = new();
        internal readonly PlayRegistry Registry;
        internal readonly SessionInfo Session;
        internal readonly TrackChangeRecorder Recorder;
        internal readonly DecoratorStats Stats;
        internal readonly List<GeneralCommand> Commands = new();
        internal readonly List<(LogLevel Level, string Message)> Logs = new();

        internal Harness()
        {
            Config = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, CorrectAfterStart = CorrectAfterStartMode.Off };
            Config.AppModes.Add(new AppMode { Client = "Boundary Oracle", Mode = AppModeKind.ReplaceOnAutoplay });
            var folder = Path.Combine(Path.GetTempPath(), "babelfin-boundary-oracle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var paths = CallProxy.Create<IApplicationPaths>((m, _) => m.ReturnType == typeof(string) ? folder : null).Proxy;
            _ = new MemoryPlugin(paths, CallProxy.Create<IXmlSerializer>().Proxy, Config);
            Registry = new PlayRegistry(() => Now);
            var stats = Stats = new DecoratorStats(() => Now);
            var person = Ctx.Person(new[] { "en" }, new[] { "en" });
            person.Everyday.Memory = MemoryScope.RestOfShow;
            Store.Save(person);
            var resolver = CallProxy.Create<IProfileResolver>((m, _) =>
            {
                if (m.Name != "Resolve") return null;
                var resolved = Ctx.Resolve(Store.TryGet(User.Id)!, Config, Ctx.Episode());
                OnProfileResolved?.Invoke();
                return resolved;
            }).Proxy;
            var activity = new ListActivityLog();
            Recorder = new TrackChangeRecorder(resolver, Store, activity, NullLogger<TrackChangeRecorder>.Instance);
            var sources = CallProxy.Create<IMediaSourceManager>((m, args) =>
            {
                if (m.Name != "GetStaticMediaSources") return null;
                SourceLookupCalls++;
                OnSourceLookup?.Invoke();
                if (SourceLookupFails) throw new IOException("The prepared choice must survive a failed fallback lookup.");
                var item = args.OfType<BaseItem>().Single();
                var source = Source();
                source.Id = SourceId;
                stats.Record(User.Id, item.Id, source.Id, new DecisionRecord(Now, 1, -1, new TrackDecision(LeaveAudioToVideo ? null : 2, 4, Array.Empty<string>()), DecisionOutcome.Applied), source);
                return new[] { source };
            }).Proxy;
            var users = CallProxy.Create<IUserManager>((m, _) => m.Name == "GetUserById" ? User : null).Proxy;
            var library = CallProxy.Create<ILibraryManager>((m, args) => m.Name == "GetItemById" ? ((Guid)args[0]! == First.Id ? First : Second) : null).Proxy;
            Session = new SessionInfo(null!, NullLogger.Instance)
            {
                Id = "boundary-session", UserId = User.Id, DeviceId = "boundary-device", Client = "Boundary Oracle",
                PlayState = new PlayerStateInfo { MediaSourceId = "oracle-source" },
            };
            var sessions = CallProxy.Create<ISessionManager>((m, args) =>
            {
                if (m.Name == "get_Sessions") return SessionVisible ? new[] { Session } : Array.Empty<SessionInfo>();
                if (m.Name == "SendGeneralCommand") Commands.Add(args.OfType<GeneralCommand>().Single());
                return null;
            }).Proxy;
            _filter = new PlaybackInfoFilter(users, library, sources, stats, Registry, Recorder, sessions, activity,
                CallProxy.Create<ICompatibilityWriter>().Proxy, new CallbackLogger((level, message) =>
                {
                    Logs.Add((level, message));
                    if (message.Contains("a manual choice", StringComparison.Ordinal)) OnManualRequest?.Invoke();
                    if (message.Contains("PlaybackInfo for", StringComparison.Ordinal)) OnRequestDecision?.Invoke();
                }));
            _monitor = new PlaybackMonitor(sessions, Registry, Recorder, sources, users, library, activity, NullLogger<PlaybackMonitor>.Instance, stats);
        }

        internal async Task<ActionExecutingContext> Request(Episode item, int? audio = null, int? subtitle = null, string completion = "success")
        {
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimsHelper.UserIdClaim, User.Id.ToString()),
                    new Claim(ClaimsHelper.ClientClaim, Session.Client),
                    new Claim(ClaimsHelper.DeviceIdClaim, Session.DeviceId),
                }, "oracle")),
            };
            var action = new ActionContext(http, new RouteData(), new ControllerActionDescriptor { ControllerName = "MediaInfo", ActionName = "GetPostedPlaybackInfo" }, new ModelStateDictionary());
            var args = new Dictionary<string, object?> { ["itemId"] = item.Id };
            if (audio.HasValue) args["audioStreamIndex"] = audio.Value;
            if (subtitle.HasValue) args["subtitleStreamIndex"] = subtitle.Value;
            var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), args, new object());
            await _filter.OnActionExecutionAsync(context, () =>
            {
                if (completion == "throw") return Task.FromException<ActionExecutedContext>(new IOException("PlaybackInfo failed."));
                if (completion == "cancel") return Task.FromCanceled<ActionExecutedContext>(new CancellationToken(true));
                var executed = new ActionExecutedContext(action, new List<IFilterMetadata>(), new object());
                if (completion == "error-result") executed.Result = new StatusCodeResult(500);
                if (completion == "exception-result") executed.Exception = new IOException("PlaybackInfo failed.");
                return Task.FromResult(executed);
            });
            return context;
        }

        internal void Report(Episode item, int? audio, int? subtitle)
        {
            Session.PlayState.AudioStreamIndex = audio;
            Session.PlayState.SubtitleStreamIndex = subtitle;
            Invoke("HandleReport", new PlaybackProgressEventArgs { Session = Session, Item = item, MediaSourceId = SourceId });
        }

        internal void Stop(Episode item) => Invoke("OnPlaybackStopped", null, new PlaybackStopEventArgs { Session = Session, Item = item });

        private void Invoke(string method, params object?[] arguments)
            => typeof(PlaybackMonitor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_monitor, arguments);

        public void Dispose()
            => typeof(Plugin).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, _previous);
    }

    private sealed class MemoryPlugin : Plugin
    {
        internal MemoryPlugin(IApplicationPaths paths, IXmlSerializer xml, PluginConfiguration config) : base(paths, xml) => Configuration = config;
        public override void SaveConfiguration() { }
    }
}
