using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Xunit;
using static Babelfin.Engine.Tests.Classification.Tracks;

namespace Babelfin.Engine.Tests.Classification;

/// <summary>One test case per default keyword (every default word has a unit test).</summary>
public class KeywordTests
{
    public static IEnumerable<object[]> PartialWords() => KeywordTables.Default.PartialWords.Select(w => new object[] { w });

    public static IEnumerable<object[]> SdhWords() => KeywordTables.Default.SdhWords.Select(w => new object[] { w });

    public static IEnumerable<object[]> FullWords() => KeywordTables.Default.FullWords.Select(w => new object[] { w });

    public static IEnumerable<object[]> CommentaryWords() => KeywordTables.Default.CommentaryWords.Select(w => new object[] { w });

    public static IEnumerable<object[]> AudioDescriptionWords() => KeywordTables.Default.AudioDescriptionWords.Select(w => new object[] { w });

    [Theory]
    [MemberData(nameof(PartialWords))]
    public void Every_partial_word_makes_a_partial_subtitle(string word)
    {
        var c = Classifier.Classify(Sub(3, "eng", "English [" + word + "]"));
        Assert.Equal(SubtitleKind.Partial, c.SubtitleKind);
        Assert.Contains("keyword:" + word, c.Reasons);
    }

    [Theory]
    [MemberData(nameof(SdhWords))]
    public void Every_sdh_word_makes_an_sdh_subtitle(string word)
    {
        var c = Classifier.Classify(Sub(3, "eng", "English (" + word + ")"));
        Assert.Equal(SubtitleKind.Sdh, c.SubtitleKind);
        Assert.Contains("keyword:" + word, c.Reasons);
    }

    [Theory]
    [MemberData(nameof(FullWords))]
    public void Every_full_word_beats_the_forced_and_default_flags(string word)
    {
        var c = Classifier.Classify(Sub(3, "eng", "English - " + word, isDefault: true, forced: true));
        Assert.Equal(SubtitleKind.Full, c.SubtitleKind);
        Assert.Contains("title-word:" + word, c.Reasons);
    }

    [Theory]
    [MemberData(nameof(CommentaryWords))]
    public void Every_commentary_word_makes_a_commentary_track(string word)
    {
        var c = Classifier.Classify(Audio(2, "eng", word + " 2.0"));
        Assert.Equal(AudioKind.Commentary, c.AudioKind);
        Assert.Contains("keyword:" + word, c.Reasons);
    }

    [Theory]
    [MemberData(nameof(AudioDescriptionWords))]
    public void Every_audio_description_word_makes_an_audio_description_track(string word)
    {
        var c = Classifier.Classify(Audio(2, "eng", "English " + word + " 5.1"));
        Assert.Equal(AudioKind.AudioDescription, c.AudioKind);
        Assert.Contains("keyword:" + word, c.Reasons);
    }

    [Fact]
    public void Default_lists_are_not_empty()
    {
        var k = KeywordTables.Default;
        Assert.NotEmpty(k.PartialWords);
        Assert.NotEmpty(k.SdhWords);
        Assert.NotEmpty(k.FullWords);
        Assert.NotEmpty(k.CommentaryWords);
        Assert.NotEmpty(k.AudioDescriptionWords);
    }

    [Fact]
    public void Custom_lists_replace_the_defaults()
    {
        var custom = new TrackClassifier(
            Jellyfin.Plugin.Babelfin.Engine.Languages.LanguageTable.Default,
            new KeywordTables(new[] { "nur schilder" }, new[] { "taub" }, new[] { "alles" }, new[] { "plauderei" }, new[] { "beschreibung" }));
        Assert.Equal(SubtitleKind.Partial, custom.Classify(Sub(1, "ger", "Nur Schilder")).SubtitleKind);
        Assert.Equal(SubtitleKind.Full, custom.Classify(Sub(1, "ger", "Signs")).SubtitleKind);
        Assert.Equal(SubtitleKind.Sdh, custom.Classify(Sub(1, "ger", "Taub")).SubtitleKind);
        Assert.Equal(SubtitleKind.Full, custom.Classify(Sub(1, "ger", "Alles", forced: true)).SubtitleKind);
        Assert.Equal(AudioKind.Commentary, custom.Classify(Audio(1, "ger", "Plauderei")).AudioKind);
        Assert.Equal(AudioKind.AudioDescription, custom.Classify(Audio(1, "ger", "Beschreibung")).AudioKind);
        Assert.Equal(AudioKind.Normal, custom.Classify(Audio(1, "ger", "Commentary")).AudioKind);
    }
}
