using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Babelfin.Engine.Tests.Regression;

public sealed partial class PlaybackBoundaryAdversarialTests
{
    private readonly ITestOutputHelper _preparationOutput;
    public PlaybackBoundaryAdversarialTests(ITestOutputHelper output) => _preparationOutput = output;

    private enum PreparationState { None, OnePrior, TwoPriors, Stopped }

    private static string RememberedAudio(Harness h)
    {
        var overrides = h.Store.TryGet(h.User.Id)!.Overrides;
        return overrides.Count == 0 || overrides[0].Manual?.Audio is null ? "none" : overrides[0].Manual!.Audio!.Language ?? "?";
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task NullSnapshotPicksAfterSequentialPreparations(int priorCount, bool visible)
    {
        using var h = PreparationHarness(visible);
        using var olderGate = new PreparationGate();
        using var newerGate = new PreparationGate();
        h.OnSourceLookup = ParkPreparation;
        var older = Task.Run(() => { _preparationGate = olderGate; return h.Request(h.First, audio: 5); });
        Assert.True(olderGate.Entered.Wait(10000));
        var newer = Task.Run(() => { _preparationGate = newerGate; return h.Request(h.First, audio: 1); });
        Assert.True(newerGate.Entered.Wait(10000));
        try
        {
            for (var i = 0; i < priorCount; i++) await h.Request(h.First);
            olderGate.Release.Set(); await older;
            newerGate.Release.Set(); await newer;
        }
        finally { olderGate.Release.Set(); newerGate.Release.Set(); }
        _preparationOutput.WriteLine($"priorCount={priorCount} visible={visible} remembered={RememberedAudio(h)}");
    }

    private static List<string[]> PreparationInterleavings(int count)
    {
        var results = new List<string[]>();
        void Visit(int next, List<int> open, List<string> sequence)
        {
            if (next == count && open.Count == 0) { results.Add(sequence.ToArray()); return; }
            if (next < count)
            {
                open.Add(next); sequence.Add("S" + next);
                Visit(next + 1, open, sequence);
                sequence.RemoveAt(sequence.Count - 1); open.Remove(next);
            }
            foreach (var index in open.ToList())
            {
                open.Remove(index); sequence.Add("E" + index);
                Visit(next, open, sequence);
                sequence.RemoveAt(sequence.Count - 1); open.Add(index);
            }
        }
        Visit(0, new List<int>(), new List<string>());
        return results;
    }

    private async Task<string> RunPreparationSequence(PreparationState state, bool visible, int?[] picks, string[] sequence)
    {
        using var h = PreparationHarness(visible);
        switch (state)
        {
            case PreparationState.OnePrior: await h.Request(h.First); break;
            case PreparationState.TwoPriors: await h.Request(h.First); await h.Request(h.First); break;
            case PreparationState.Stopped:
                await h.Request(h.First); h.Report(h.First, 2, 4); h.Now = T0.AddSeconds(30);
                h.Report(h.First, 2, 4); h.Stop(h.First); h.Now = T0.AddSeconds(31); break;
        }
        h.OnSourceLookup = ParkPreparation;
        var gates = picks.Select(_ => new PreparationGate()).ToArray();
        var tasks = new Task?[picks.Length];
        try
        {
            foreach (var step in sequence)
            {
                var index = step[1] - '0';
                if (step[0] == 'S')
                {
                    tasks[index] = Task.Run(() => { _preparationGate = gates[index]; return h.Request(h.First, audio: picks[index]); });
                    Assert.True(gates[index].Entered.Wait(10000));
                }
                else { gates[index].Release.Set(); await tasks[index]!; }
            }
            return RememberedAudio(h);
        }
        finally { foreach (var gate in gates) { gate.Release.Set(); gate.Dispose(); } }
    }

    [Fact]
    public async Task ExplorePreparationInterleavings()
    {
        var sets = new[] { new int?[] { 5, 1 }, new int?[] { 5, 1, null, null } };
        foreach (var picks in sets)
        foreach (var state in Enum.GetValues<PreparationState>())
        foreach (var visible in new[] { true, false })
        {
            var counts = new SortedDictionary<string, int>();
            foreach (var sequence in PreparationInterleavings(picks.Length))
            {
                var memory = await RunPreparationSequence(state, visible, picks, sequence);
                counts[memory] = counts.GetValueOrDefault(memory) + 1;
            }
            _preparationOutput.WriteLine($"requests={picks.Length} state={state} visible={visible}: " +
                string.Join(", ", counts.Select(pair => pair.Key + "=" + pair.Value)));
        }
    }
}
