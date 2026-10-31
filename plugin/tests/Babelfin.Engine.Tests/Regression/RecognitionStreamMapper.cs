using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using MediaBrowser.Model.Entities;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Regression;

public class RecognitionStreamMapper
{
    private static MediaStream External(string path, string? language = null) => new()
    {
        Index = 3, Type = MediaStreamType.Subtitle, IsExternal = true, Path = path, Language = language, Codec = "subrip",
    };

    [Theory]
    [InlineData("Signs (2002)", ".en.srt", "en", SubtitleKind.Full)]
    [InlineData("Foreign Correspondent (1940)", ".en.srt", "en", SubtitleKind.Full)]
    [InlineData("Song of the Sea (2014)", ".en.srt", "en", SubtitleKind.Full)]
    [InlineData("Full House - S01E01 - Our Very First Show", ".en.forced.srt", "en", SubtitleKind.Partial)]
    [InlineData("Hi Score Girl - S01E01", ".en.srt", "en", SubtitleKind.Full)]
    [InlineData("Black.Cat.White.Cat.1998", ".srt", null, SubtitleKind.Full)]
    [InlineData("Mad.Men.S01E01", ".srt", null, SubtitleKind.Full)]
    [InlineData("Cantonese Movie", ".zh.srt", "zh", SubtitleKind.Full)]
    [InlineData("Brasil", ".por.srt", "pt", SubtitleKind.Full)]
    public void RawMovieAndEpisodeNamesCannotSupplyKindOrLanguage(string title, string suffix, string? language, SubtitleKind kind)
    {
        var stream = External("/media/" + title + suffix);
        var track = StreamMapper.Map(stream, "/media/" + title + ".mkv");
        var classified = Classifier.Classify(track);
        Assert.Equal(suffix, track.FileName);
        Assert.Equal(stream.Path, track.ExternalPath);
        Assert.Equal(language, classified.Language?.Code);
        Assert.Equal(kind, classified.SubtitleKind);
    }

    [Theory]
    [InlineData("Other.Signs.Cat.Men.hi.srt", "Movie.mkv")]
    [InlineData("Movie 2.en.forced.srt", "Movie.mkv")]
    [InlineData("MovieExtra.en.forced.srt", "Movie.mkv")]
    [InlineData("Movie.en.forced.srt", null)]
    [InlineData("Movie.en.forced.srt", "")]
    public void UnmatchedOrUnknownVideoNamesSupplyNoFilenameHints(string subtitle, string? video)
    {
        var track = StreamMapper.Map(External(subtitle), video);
        Assert.Null(track.FileName);
        var classified = Classifier.Classify(track);
        Assert.Null(classified.Language);
        Assert.Equal(SubtitleKind.Full, classified.SubtitleKind);
    }

    [Fact]
    public void TrustedJellyfinTitleAndFlagsStillWorkWhenThePathDoesNotMatch()
    {
        var stream = External("unmatched.srt", "en");
        stream.Title = "Full";
        stream.IsHearingImpaired = true;
        var classified = Classifier.Classify(StreamMapper.Map(stream, "movie.mkv"));
        Assert.Equal(SubtitleKind.Sdh, classified.SubtitleKind);
        stream.Title = "Signs & Songs";
        stream.IsForced = true;
        Assert.Equal(SubtitleKind.Partial, Classifier.Classify(StreamMapper.Map(stream, "movie.mkv")).SubtitleKind);
    }

    [Theory]
    [InlineData("D:\\Media\\Show\\Full House.en.hi.srt", "D:\\Media\\Show\\Full House.mkv", ".en.hi.srt")]
    [InlineData("/media/SIGNS (2002).pt-BR.srt", "/media/Signs (2002).mkv", ".pt-BR.srt")]
    public void SuffixExtractionWorksForBothPathStylesAndCase(string subtitle, string video, string suffix)
    {
        var mapped = StreamMapper.Map(new[] { External(subtitle) }, video);
        Assert.Equal(suffix, Assert.Single(mapped).FileName);
    }

    [Fact]
    public void EmbeddedStreamsAndExternalAudioDoNotUsePathsAsSubtitleHints()
    {
        var stream = External("Movie.en.forced.srt", "en");
        stream.IsExternal = false;
        var embedded = StreamMapper.Map(stream, "Movie.mkv");
        Assert.Null(embedded.FileName);
        Assert.Null(embedded.ExternalPath);
        stream.IsExternal = true;
        stream.Type = MediaStreamType.Audio;
        Assert.Null(StreamMapper.Map(stream, "Movie.mkv").FileName);
        Assert.Equal(stream.Path, StreamMapper.Map(stream, "Movie.mkv").ExternalPath);
    }

    [Fact]
    public void OldMapCallsRemainCompatibleAndFailClosedWithoutTheVideoPath()
    {
        var stream = External("Signs (2002).en.srt", "en");
        Assert.Null(StreamMapper.Map(stream).FileName);
        Assert.Null(Assert.Single(StreamMapper.Map(new[] { stream })).FileName);
        Assert.Equal(SubtitleKind.Full, Classifier.Classify(StreamMapper.Map(stream)).SubtitleKind);
    }
}
