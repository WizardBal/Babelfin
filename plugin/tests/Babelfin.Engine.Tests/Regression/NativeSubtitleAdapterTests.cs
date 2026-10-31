using System;
using System.Collections.Generic;
using System.Linq;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Selection cache globals")]
public class NativeSubtitleAdapterTests
{
    // Values/order verified with the actual local 12.2 LocalizationManager.LoadCultures
    // and private MediaSourceManager.NormalizeLanguage, not Babelfin's language table.
    private static ILocalizationManager Localization(Action<string?>? observed = null) => CallProxy.Create<ILocalizationManager>((method, args) =>
    {
        if (method.Name != "FindLanguageInfo") return null;
        var raw = (string?)args[0];
        observed?.Invoke(raw);
        return raw switch
        {
            "en" or "eng" => new CultureDto("en", "English", "en", new[] { "eng" }),
            "de" or "ger" or "deu" => new CultureDto("de", "German", "de", new[] { "deu", "ger" }),
            "pt-BR" or "pt-br" => new CultureDto("pt-br", "Portuguese (Brazil)", "pt", new[] { "por" }),
            "pt-PT" or "pt-pt" => new CultureDto("pt-pt", "Portuguese (Portugal)", "pt", new[] { "por" }),
            _ => null,
        };
    }).Proxy;

    private static EngineTrackDecider Create(ResolvedProfile? profile = null, ILocalizationManager? localization = null)
    {
        var resolved = Ctx.Resolve(Ctx.Person(), null, Ctx.Movie(original: "ja")) with
        {
            Profile = profile ?? Starter("en").Chain(Lang("ja")) with
            {
                WhenAudioUnderstood = SubtitleWant.VideoDefault, WhenAudioNotUnderstood = SubtitleWant.VideoDefault,
            },
        };
        var resolver = CallProxy.Create<IProfileResolver>((m, _) => m.Name == "Resolve" ? resolved : null).Proxy;
        return new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance, localization);
    }

    private static TrackDecision Decide(EngineTrackDecider decider, string preferred, params Track[] tracks)
    {
        var source = new MediaSourceInfo
        {
            Id = "native-adapter", Path = @"C:\media\episode.mkv", DefaultAudioStreamIndex = 1,
            MediaStreams = tracks.Select(t => new MediaStream
            {
                Index = t.Index, Language = t.Language, Title = t.Title, Codec = t.Type == TrackType.Audio ? "aac" : "subrip",
                Type = t.Type == TrackType.Audio ? MediaStreamType.Audio : MediaStreamType.Subtitle,
                IsDefault = t.IsDefault, IsForced = t.IsForced,
            }).ToArray(),
        };
        var user = ApiSafetyHost.Person();
        user.SubtitleLanguagePreference = preferred;
        user.SubtitleMode = SubtitlePlaybackMode.Smart;
        return decider.Decide(new Movie { Id = Guid.NewGuid(), Name = "Native adapter fixture" }, source, user)!;
    }

    [Theory]
    [InlineData("en", "eng")]
    [InlineData("eng", "eng")]
    [InlineData("de", "ger")]
    [InlineData("de", "deu")]
    public void NormalizedNativeLanguagesReachTheRealAdapter(string preferred, string subtitleLanguage)
    {
        var result = Decide(Create(localization: Localization()), preferred, Audio(1, "eng"), Audio(2, "jpn"), Sub(3, subtitleLanguage));
        Assert.Equal(2, result.AudioIndex);
        Assert.Equal(3, result.SubtitleIndex);
        Assert.Equal("true", result.Engine!.Trace.Single(l => l.Code == TraceCodes.SubsVideoDefault).Args!["recomputed"]);
    }

    [Theory]
    [InlineData("ger", false, -1)]
    [InlineData("deu", false, -1)]
    [InlineData("ger", true, 4)]
    [InlineData("deu", true, 4)]
    public void SmartRecognizesAudioInEitherNormalizedGermanAlias(string audioLanguage, bool forcedAvailable, int expected)
    {
        var profile = Starter("en").Chain(Lang("de")) with { WhenAudioUnderstood = SubtitleWant.VideoDefault, WhenAudioNotUnderstood = SubtitleWant.VideoDefault };
        var tracks = new List<Track> { Audio(1, "jpn"), Audio(2, audioLanguage), Sub(3, "deu") };
        if (forcedAvailable) tracks.Add(Sub(4, "ger", isForced: true));
        var result = Decide(Create(profile, Localization()), "de", tracks.ToArray());
        Assert.Equal(2, result.AudioIndex);
        Assert.Equal(expected, result.SubtitleIndex);
    }

    [Theory]
    [InlineData("ger")]
    [InlineData("deu")]
    public void MultiAliasesMatchSubtitlesAndSmartAudioWithoutRewritingRawFourArgumentPolicy(string alias)
    {
        var services = new EngineServices(null);
        var tracks = services.Classifier.ClassifyAll(new[] { Sub(3, alias) });
        Assert.Null(VideoSubtitleSelector.Choose(tracks, "Always", "de", "jpn"));
        Assert.Equal(3, VideoSubtitleSelector.Choose(tracks, "Always", "de", "jpn", new[] { "deu", "ger" }));
        Assert.Null(VideoSubtitleSelector.Choose(tracks, "Smart", "de", alias, new[] { "deu", "ger" }));
        Assert.Equal(3, VideoSubtitleSelector.Choose(tracks, "Smart", "de", "jpn", new[] { "deu", "ger" }));
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, 5)]
    public void RegionalNativePreferencesMatchOnlyTheExactRegion(bool brazilianAvailable, int expected)
    {
        var tracks = new List<Track> { Audio(1, "eng"), Audio(2, "jpn"), Sub(3, "pt-PT"), Sub(4, "por") };
        if (brazilianAvailable) tracks.Add(Sub(5, "pt-BR"));
        Assert.Equal(expected, Decide(Create(localization: Localization()), "pt-BR", tracks.ToArray()).SubtitleIndex);
    }

    [Theory]
    [InlineData("en", new[] { "eng" })]
    [InlineData("de", new[] { "deu", "ger" })]
    [InlineData("ger", new[] { "deu", "ger" })]
    [InlineData("deu", new[] { "deu", "ger" })]
    [InlineData("pt-BR", new[] { "pt-br" })]
    [InlineData("pt-PT", new[] { "pt-pt" })]
    [InlineData("bf-unknown", new[] { "bf-unknown" })]
    [InlineData(" en ", new[] { " en " })]
    public void AdapterNormalizationPreservesTheExactMsmContract(string raw, string[] expected)
    {
        Assert.Equal(expected, Create(localization: Localization()).NormalizeSubtitleLanguages(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyNativePreferenceSkipsLocalizationLookup(string? raw)
    {
        var calls = 0;
        Assert.Empty(Create(localization: Localization(_ => calls++)).NormalizeSubtitleLanguages(raw));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ExplicitEmptyListOverridesTheRawPreference_AndNullKeepsFourArgumentBehavior()
    {
        var services = new EngineServices(null);
        var tracks = services.Classifier.ClassifyAll(new[] { Sub(3, "ger") });
        Assert.Null(VideoSubtitleSelector.Choose(tracks, "Always", "eng", "eng", null));
        Assert.Equal(3, VideoSubtitleSelector.Choose(tracks, "Always", "eng", "eng", Array.Empty<string>()));
        Assert.Equal(3, VideoSubtitleSelector.Choose(tracks, "Smart", "eng", "eng", Array.Empty<string>()));
        Assert.Equal(new[] { "en" }, Create().NormalizeSubtitleLanguages("en"));
    }

    [Fact]
    public void DeferredNativeTraceSnapshotsTheNormalizedPreferenceList()
    {
        var normalized = new List<string> { "deu", "ger" };
        var services = new EngineServices(null);
        var profile = Starter("en").Chain(Lang("ja")) with { WhenAudioUnderstood = SubtitleWant.VideoDefault, WhenAudioNotUnderstood = SubtitleWant.VideoDefault };
        var facts = ItemFacts.None with { JellyfinSubtitleMode = "Smart", JellyfinSubtitleLanguage = "de", JellyfinSubtitleLanguages = normalized };
        var tracks = new[] { Audio(1, "eng"), Audio(2, "jpn"), Sub(3, "ger") };
        var eager = services.Selector.Decide(tracks, facts, profile);
        var deferred = services.Selector.Decide(tracks, facts, profile, includeTrace: false);
        normalized.Clear();
        normalized.Add("eng");
        Assert.Equal(3, deferred.SubtitleIndex);
        Assert.Equal(eager.Trace.Select(l => (l.Code, l.Text, l.TrackIndex)), deferred.Trace.Select(l => (l.Code, l.Text, l.TrackIndex)));
        Assert.Equal(eager.Explain(), deferred.Explain());
    }
}