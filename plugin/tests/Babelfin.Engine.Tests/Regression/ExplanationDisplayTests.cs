using System;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Regression;

public class ExplanationDisplayTests
{
    /// <summary>Missing preferred subtitles explain the available outcome and fallback.</summary>
    [Theory]
    [InlineData("none", true)]
    [InlineData("none", false)]
    [InlineData("partial", true)]
    [InlineData("partial", false)]
    [InlineData("backup", true)]
    [InlineData("backup", false)]
    [InlineData("next-language", true)]
    [InlineData("next-language", false)]
    public void MissingPreferredSubtitles_ExplainSelectionAndFallback(string scenario, bool includeTrace)
    {
        var profile = OriginalAudio("en");
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "spa") };
        if (scenario == "partial")
        {
            profile = profile with { IfNothingMatches = WhenNothingMatches.PartialInMyLanguage };
            tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", "Signs & Songs", isForced: true) };
        }
        else if (scenario == "backup")
            profile = profile with { Reads = new[] { "en", "es" }, SubtitleLanguages = new[] { "en" }, IfNothingMatches = WhenNothingMatches.FullInAnyBackupLanguage };
        else if (scenario == "next-language")
            profile = profile with { SubtitleLanguages = new[] { "en", "es" } };

        var decision = Selector.Decide(tracks, Item("ja"), profile, includeTrace: includeTrace);
        Assert.Equal(1, decision.AudioIndex);
        Assert.Equal(scenario == "none" ? (int?)null : 2, decision.SubtitleIndex);
        Assert.True(decision.SubtitlesWanted);
        if (scenario == "none")
            Assert.Contains("Subtitles: none found in English.", decision.Explain(), StringComparison.Ordinal);
        else if (scenario == "next-language")
            Assert.Contains("so your next language", decision.Explain(), StringComparison.Ordinal);
        else
            Assert.Contains("so a fallback.", decision.Explain(), StringComparison.Ordinal);
        Assert.DoesNotContain("queued", decision.Explain(), StringComparison.OrdinalIgnoreCase);
        Assert.All(decision.Trace, line => Assert.DoesNotContain("queued", line.Text, StringComparison.OrdinalIgnoreCase));
        var expected = scenario == "none" ? TraceCodes.SubsNothing : scenario == "next-language" ? TraceCodes.SubsMatched : TraceCodes.SubsFallback;
        Assert.Contains(decision.Trace, line => line.Code == expected);
    }
}
