using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Babelfin.Engine.Selection;

namespace Jellyfin.Plugin.Babelfin.Decisions;

/// <summary>
/// Babelfin's choice for one media source, with the reasons in plain sentences.
/// </summary>
/// <param name="AudioIndex">The audio stream index to use, or null to keep Jellyfin's.</param>
/// <param name="SubtitleIndex">The subtitle stream index to use (-1 = subtitles off), or null to keep Jellyfin's.</param>
/// <param name="Trace">Why, one sentence per step (never a file path).</param>
/// <param name="JellyfinAudioIndex">The audio index Jellyfin itself had chosen.</param>
/// <param name="JellyfinSubtitleIndex">The subtitle index Jellyfin itself had chosen.</param>
/// <param name="Explanation">The one-paragraph explanation for people, or null.</param>
/// <param name="Engine">The engine's structured decision (trace codes, classified tracks, fetch language), or null for a decider without the engine.</param>
public sealed record TrackDecision(
    int? AudioIndex,
    int? SubtitleIndex,
    IReadOnlyList<string> Trace,
    int? JellyfinAudioIndex = null,
    int? JellyfinSubtitleIndex = null,
    string? Explanation = null,
    Decision? Engine = null)
{
    private string? _explanation = Explanation;

    /// <summary>Gets the explanation, formatted only when a diagnostic consumer asks.</summary>
    public string? Explanation
    {
        get
        {
            var value = Volatile.Read(ref _explanation);
            if (value is null && Engine is not null)
            {
                value = Engine.Explain();
                Interlocked.CompareExchange(ref _explanation, value, null);
            }

            return value;
        }
        init => _explanation = value;
    }
}

internal sealed class DeferredTextTrace : IReadOnlyList<string>
{
    private readonly IReadOnlyList<TraceLine> _lines;
    internal DeferredTextTrace(IReadOnlyList<TraceLine> lines) => _lines = lines;
    public int Count => _lines.Count;
    public string this[int index] => _lines[index].Text;
    public IEnumerator<string> GetEnumerator() => _lines.Select(line => line.Text).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
