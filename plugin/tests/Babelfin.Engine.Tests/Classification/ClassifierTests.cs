using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Xunit;
using static Babelfin.Engine.Tests.Classification.Tracks;

namespace Babelfin.Engine.Tests.Classification;

public class ClassifierTests
{
    [Theory]
    [InlineData("[CloudHarbor] JAP 5.1 (Opus 192Kbps)")]
    [InlineData("Shadow Edition")]
    [InlineData("Broadway")]
    [InlineData("ADR")]
    [InlineData("Loaded")]
    [InlineData("Surround")]
    [InlineData("English DTS 5.1 @ 1509 Kbps - Original")]
    [InlineData("Stereo")]
    [InlineData("Director's Cut")]
    [InlineData("English - Director's Cut 5.1")]
    [InlineData("Cast")]
    public void Audio_without_whole_word_hits_is_normal(string title)
    {
        var c = Classifier.Classify(Audio(1, "eng", title));
        Assert.Equal(AudioKind.Normal, c.AudioKind);
        Assert.Equal(new[] { "default:normal" }, c.Reasons);
    }

    [Theory]
    [InlineData("Commentary")]
    [InlineData("Director's Commentary")]
    [InlineData("Director’s Commentary")]
    [InlineData("Kommentar")]
    [InlineData("解説")]
    [InlineData("Audio Commentary with the Cast")]
    [InlineData("Cast & Crew Commentary")]
    public void Commentary_titles(string title)
    {
        Assert.Equal(AudioKind.Commentary, Classifier.Classify(Audio(3, "eng", title)).AudioKind);
    }

    [Theory]
    [InlineData("Audio Description")]
    [InlineData("AD")]
    [InlineData("English (AD)")]
    [InlineData("eng.AD")]
    [InlineData("DVS")]
    [InlineData("Descriptive Video Service")]
    [InlineData("音声ガイド")]
    [InlineData("音声解説")]
    public void Audio_description_titles(string title)
    {
        Assert.Equal(AudioKind.AudioDescription, Classifier.Classify(Audio(3, "eng", title)).AudioKind);
    }

    [Fact]
    public void Audio_keywords_are_read_from_the_comment_too()
    {
        var c = Classifier.Classify(Audio(3, "eng", "English 2.0", comment: "Commentary by the director"));
        Assert.Equal(AudioKind.Commentary, c.AudioKind);
    }

    [Theory]
    [InlineData("English [SDH]")]
    [InlineData("SDH")]
    [InlineData("English (CC)")]
    [InlineData("English (HI)")]
    [InlineData("Hearing-Impaired")]
    [InlineData("hearing_impaired")]
    public void Sdh_titles(string title)
    {
        Assert.Equal(SubtitleKind.Sdh, Classifier.Classify(Sub(4, "eng", title)).SubtitleKind);
    }

    [Fact]
    public void Sdh_from_external_file_name()
    {
        var c = Classifier.Classify(Sub(10, null, null, file: "/media/Movie/Movie.eng.sdh.srt"));
        Assert.Equal(SubtitleKind.Sdh, c.SubtitleKind);
        Assert.Equal("en", c.Language?.Code);
        Assert.Contains("keyword:sdh", c.Reasons);
    }

    [Fact]
    public void Dot_hi_means_hearing_impaired_on_an_english_track()
    {
        var c = Classifier.Classify(Sub(10, "en", null, file: "Movie.en.hi.srt"));
        Assert.Equal(SubtitleKind.Sdh, c.SubtitleKind);
        Assert.Contains("keyword:hi", c.Reasons);
    }

    [Fact]
    public void Dot_hi_on_a_hindi_track_is_the_language_not_sdh()
    {
        var c = Classifier.Classify(Sub(10, "hi", null, file: "Movie.hi.srt"));
        Assert.Equal(SubtitleKind.Full, c.SubtitleKind);
        Assert.Equal("hi", c.Language?.Code);
        var hindiTitle = Classifier.Classify(Sub(11, "hin", "Hindi (HI)"));
        Assert.Equal(SubtitleKind.Sdh, hindiTitle.SubtitleKind);
    }

    [Theory]
    [InlineData("ccc")]
    [InlineData("Accent")]
    [InlineData("This")]
    [InlineData("Shield")]
    [InlineData("English (CloudSpool)")]
    public void Sdh_words_need_word_boundaries(string title)
    {
        Assert.Equal(SubtitleKind.Full, Classifier.Classify(Sub(4, "eng", title)).SubtitleKind);
    }

    [Fact]
    public void Forced_flag_makes_partial()
    {
        var c = Classifier.Classify(Sub(3, "eng", null, isDefault: true, forced: true));
        Assert.Equal(SubtitleKind.Partial, c.SubtitleKind);
        Assert.Equal(new[] { "forced-flag" }, c.Reasons);
    }

    [Theory]
    [InlineData("Signs & Songs")]
    [InlineData("Signs/Songs [PebbleForge]")]
    [InlineData("S&S")]
    [InlineData("Forced")]
    [InlineData("Foreign parts")]
    [InlineData("English [Signs/Songs] (PGS)")]
    [InlineData("Signs")]
    [InlineData("Français forcés")]
    [InlineData("中文強制")]
    public void Partial_titles(string title)
    {
        var c = Classifier.Classify(Sub(3, "eng", title, isDefault: true));
        Assert.Equal(SubtitleKind.Partial, c.SubtitleKind);
        Assert.NotEmpty(c.Reasons);
        Assert.StartsWith("keyword:", c.Reasons[0]);
    }

    [Fact]
    public void Partial_from_external_file_name()
    {
        var c = Classifier.Classify(Sub(9, null, null, file: "movie.en.forced.srt"));
        Assert.Equal(SubtitleKind.Partial, c.SubtitleKind);
        Assert.Equal("en", c.Language?.Code);
    }

    [Fact]
    public void Full_title_is_full()
    {
        var c = Classifier.Classify(Sub(3, "eng", "English [Full] (PGS)", format: SubtitleFormat.Image));
        Assert.Equal(SubtitleKind.Full, c.SubtitleKind);
        Assert.Contains("title-word:full", c.Reasons);
    }

    [Fact]
    public void Dialogue_is_full_even_when_default()
    {
        Assert.Equal(SubtitleKind.Full, Classifier.Classify(Sub(4, "eng", "Dialogue", isDefault: true)).SubtitleKind);
    }

    [Fact]
    public void Full_title_beats_the_forced_flag()
    {
        var c = Classifier.Classify(Sub(3, "eng", "Full", forced: true));
        Assert.Equal(SubtitleKind.Full, c.SubtitleKind);
        Assert.DoesNotContain("forced-flag", c.Reasons);
    }

    [Fact]
    public void Full_title_with_hearing_impaired_flag_is_sdh()
    {
        var c = Classifier.Classify(Sub(3, "eng", "Full", hearingImpaired: true));
        Assert.Equal(SubtitleKind.Sdh, c.SubtitleKind);
        Assert.Contains("hi-flag", c.Reasons);
        Assert.Contains("title-word:full", c.Reasons);
    }

    [Fact]
    public void Full_and_sdh_words_together_are_sdh()
    {
        Assert.Equal(SubtitleKind.Sdh, Classifier.Classify(Sub(3, "eng", "Full SDH")).SubtitleKind);
    }

    [Fact]
    public void Dialogue_plus_signs_is_full()
    {
        Assert.Equal(SubtitleKind.Full, Classifier.Classify(Sub(3, "eng", "Full Subtitles (Dialogue + Signs)")).SubtitleKind);
    }

    [Fact]
    public void Hearing_impaired_flag_alone_is_sdh()
    {
        var c = Classifier.Classify(Sub(3, "eng", null, hearingImpaired: true));
        Assert.Equal(SubtitleKind.Sdh, c.SubtitleKind);
        Assert.Equal(new[] { "hi-flag" }, c.Reasons);
    }

    [Fact]
    public void Forced_sdh_is_partial()
    {
        Assert.Equal(SubtitleKind.Partial, Classifier.Classify(Sub(3, "eng", "SDH", forced: true)).SubtitleKind);
    }

    [Fact]
    public void Plain_track_is_full_with_a_reason()
    {
        var c = Classifier.Classify(Sub(4, "eng", null));
        Assert.Equal(SubtitleKind.Full, c.SubtitleKind);
        Assert.Equal(new[] { "default:full" }, c.Reasons);
    }

    [Fact]
    public void Kinds_do_not_leak_across_track_types()
    {
        Assert.Equal(AudioKind.Normal, Classifier.Classify(Sub(3, "eng", "Commentary")).AudioKind);
        Assert.Equal(SubtitleKind.Full, Classifier.Classify(Audio(1, "eng", "Signs")).SubtitleKind);
    }

    [Fact]
    public void Format_and_lossless_pass_through_untouched()
    {
        var sub = Sub(3, "eng", "Signs", format: SubtitleFormat.Image);
        var audio = Audio(1, "jpn", "FLAC 2.0", lossless: true);
        var cs = Classifier.Classify(sub);
        var ca = Classifier.Classify(audio);
        Assert.Same(sub, cs.Track);
        Assert.Same(audio, ca.Track);
        Assert.Equal(SubtitleFormat.Image, cs.Track.Format);
        Assert.True(ca.Track.IsLossless);
    }

    [Fact]
    public void Language_comes_from_hints()
    {
        Assert.Equal("yue", Classifier.Classify(Audio(1, "chi", "Cantonese")).Language?.Code);
        Assert.Equal("ja", Classifier.Classify(Audio(1, "und", "Japanese")).Language?.Code);
        Assert.Null(Classifier.Classify(Audio(1, "und", "Surround")).Language);
    }

    [Fact]
    public void ClassifyAll_keeps_order()
    {
        var all = Classifier.ClassifyAll(new[] { Sub(5, "eng", "Signs"), Sub(4, "eng", "Full"), Audio(1, "jpn", null) });
        Assert.Equal(new[] { 5, 4, 1 }, all.Select(c => c.Track.Index));
        Assert.Equal(SubtitleKind.Partial, all[0].SubtitleKind);
        Assert.Equal(SubtitleKind.Full, all[1].SubtitleKind);
    }

    [Fact]
    public void Classification_is_deterministic()
    {
        var t = Sub(3, "eng", "Signs & Songs", isDefault: true);
        var a = Classifier.Classify(t);
        var b = Classifier.Classify(t);
        Assert.Equal(a.SubtitleKind, b.SubtitleKind);
        Assert.Equal(a.Reasons, b.Reasons);
        Assert.Equal(a.Language, b.Language);
    }
}
