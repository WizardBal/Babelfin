using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Playback;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

public sealed partial class PlaybackBoundaryAdversarialTests
{
    private sealed class PreparationGate : IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    [ThreadStatic] private static PreparationGate? _preparationGate;

    private static void ParkPreparation()
    {
        var gate = _preparationGate;
        if (gate is null) return;
        _preparationGate = null;
        gate.Entered.Set();
        if (!gate.Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException();
    }

    private static Harness PreparationHarness(bool visible)
    {
        var h = new Harness { SessionVisible = visible };
        h.Config.AppModes.Single(m => m.Client == "Boundary Oracle").RememberPrePlayChoices = true;
        var source = Source();
        h.Stats.Record(h.User.Id, h.First.Id, source.Id,
            new DecisionRecord(h.Now, 1, -1, new TrackDecision(2, 4, Array.Empty<string>()), DecisionOutcome.Applied), source);
        h.Stats.RecordResponse(h.User.Id, source, h.Session.DeviceId, h.Session.Client);
        return h;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task OverlappingPicksAfterPriorPreparationRememberNewest(bool visible, bool olderFirst)
    {
        using var h = PreparationHarness(visible);
        await h.Request(h.First);
        using var olderGate = new PreparationGate();
        using var newerGate = new PreparationGate();
        h.OnSourceLookup = ParkPreparation;
        var older = Task.Run(() => { _preparationGate = olderGate; return h.Request(h.First, audio: 5); });
        Assert.True(olderGate.Entered.Wait(10000));
        var newer = Task.Run(() => { _preparationGate = newerGate; return h.Request(h.First, audio: 1); });
        Assert.True(newerGate.Entered.Wait(10000));
        try
        {
            if (olderFirst) { olderGate.Release.Set(); await older; newerGate.Release.Set(); await newer; }
            else { newerGate.Release.Set(); await newer; olderGate.Release.Set(); await older; }
        }
        finally { olderGate.Release.Set(); newerGate.Release.Set(); }
        h.Config.CorrectAfterStart = CorrectAfterStartMode.Auto;
        h.Now = T0.AddSeconds(2); h.Report(h.First, 1, 4);
        h.Now = T0.AddSeconds(40); h.Report(h.First, 1, 4);
        Assert.Equal("en", Assert.Single(h.Store.TryGet(h.User.Id)!.Overrides).Manual!.Audio!.Language);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SequentialPreparationsRetainOrder(bool visible)
    {
        using var h = PreparationHarness(visible);
        await h.Request(h.First);
        await h.Request(h.First);
        var record = visible ? h.Registry.Get(h.Session.Id) : h.Registry.GetPending(h.Session.DeviceId, h.Session.Client, h.First.Id);
        Assert.NotNull(record!.PreparationOrder);
    }
}
