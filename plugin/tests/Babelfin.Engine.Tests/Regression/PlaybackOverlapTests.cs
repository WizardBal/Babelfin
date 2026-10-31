using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Decisions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

public sealed partial class PlaybackBoundaryAdversarialTests
{
    [Fact]
    public async Task OverlappingPlaybackRequestsRememberTheLatestChoice()
    {
        using var h = new Harness();
        await h.Request(h.First);
        h.Report(h.First, 2, 4);
        h.Now = T0.AddMinutes(2);
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var firstRelease = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        var calls = 0;
        h.OnManualRequest = () =>
        {
            var first = Interlocked.Increment(ref calls) == 1;
            (first ? firstEntered : secondEntered).Set();
            if (!(first ? firstRelease : secondRelease).Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Playback request was not released.");
        };
        var older = Task.Run(() => h.Request(h.First, audio: 5));
        Task? newer = null;
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            newer = Task.Run(() => h.Request(h.First, audio: 1));
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(10)));
            firstRelease.Set();
            await older;
            Assert.Equal(5, h.Registry.Get(h.Session.Id)!.ExpectedAudio);
            Assert.Empty(h.Store.TryGet(h.User.Id)!.Overrides);
            secondRelease.Set();
            await newer;
            Assert.Equal(1, h.Registry.Get(h.Session.Id)!.ExpectedAudio);
            h.Settle(h.First, 1, 4);
            Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
        }
        finally { firstRelease.Set(); secondRelease.Set(); }
    }
    [Fact]
    public async Task FirstPrePlayRequestRemembersAnExplicitChoice()
    {
        using var h = new Harness();
        h.Config.AppModes.Single(mode => mode.Client == "Boundary Oracle").RememberPrePlayChoices = true;
        var source = Source();
        h.Stats.Record(h.User.Id, h.First.Id, source.Id,
            new DecisionRecord(h.Now, 1, -1, new TrackDecision(2, 4, Array.Empty<string>()), DecisionOutcome.Applied), source);
        h.Stats.RecordResponse(h.User.Id, source, h.Session.DeviceId, h.Session.Client);
        await h.Request(h.First, audio: 5);
        Assert.Equal(5, h.Registry.Get(h.Session.Id)!.ExpectedAudio);
        Assert.Equal("fr", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task OverlappingFirstRequestsPreserveTheNewerChoice(bool sessionVisible, bool olderCompletesFirst)
    {
        using var h = new Harness();
        h.SessionVisible = sessionVisible;
        h.Config.AppModes.Single(mode => mode.Client == "Boundary Oracle").RememberPrePlayChoices = true;
        var source = Source();
        h.Stats.Record(h.User.Id, h.First.Id, source.Id,
            new DecisionRecord(h.Now, 1, -1, new TrackDecision(2, 4, Array.Empty<string>()), DecisionOutcome.Applied), source);
        h.Stats.RecordResponse(h.User.Id, source, h.Session.DeviceId, h.Session.Client);
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var firstRelease = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        var calls = 0;
        h.OnSourceLookup = () =>
        {
            var first = Interlocked.Increment(ref calls) == 1;
            (first ? firstEntered : secondEntered).Set();
            if (!(first ? firstRelease : secondRelease).Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Playback request was not released.");
        };
        var older = Task.Run(() => h.Request(h.First, audio: 5));
        Task? newer = null;
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            newer = Task.Run(() => h.Request(h.First, audio: 1));
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(10)));
            if (olderCompletesFirst) { firstRelease.Set(); await older; secondRelease.Set(); await newer; }
            else { secondRelease.Set(); await newer; firstRelease.Set(); await older; }
            var record = sessionVisible ? h.Registry.Get(h.Session.Id) : h.Registry.GetPending(h.Session.DeviceId, h.Session.Client, h.First.Id);
            Assert.Equal(1, record!.ExpectedAudio);
            Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
        }
        finally { firstRelease.Set(); secondRelease.Set(); }
    }

}
