using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;

namespace NativePolicyCheck;

// Includes StreamMapper, the EngineServices.Current lookup and TrackDecision allocation;
// the resolver deliberately returns a precomputed profile, and no server or disk is used.
internal static class AdapterBenchmark
{
    private static int _sink;

    internal static Action Create(Track[] tracks, ResolvedProfile profile, ILocalizationManager? localization = null)
    {
        var result = new ResolvedProfileResult(profile, new Dictionary<string, string>(), true,
            Array.Empty<AssumedLanguage>(), false, null, Array.Empty<string>(), null,
            ItemContext.None with { OriginalLanguage = "ja" });
        var decider = new EngineTrackDecider(new FixedResolver(result), NullLogger<EngineTrackDecider>.Instance, localization);
        var item = new Movie { Id = Guid.NewGuid(), Name = "Benchmark" };
        var user = new User("Benchmark", "Default", "Default") { SubtitleLanguagePreference = "en", SubtitleMode = Jellyfin.Database.Implementations.Enums.SubtitlePlaybackMode.Smart };
        var source = new MediaSourceInfo
        {
            Id = "adapter-benchmark", Path = @"C:\benchmark\episode.mkv", DefaultAudioStreamIndex = 1,
            MediaStreams = tracks.Select(t => new MediaStream
            {
                Index = t.Index, Language = t.Language, Title = t.Title, Codec = t.Codec,
                Type = t.Type == TrackType.Audio ? MediaStreamType.Audio : MediaStreamType.Subtitle,
                Channels = t.Channels, IsDefault = t.IsDefault, IsForced = t.IsForced,
                IsHearingImpaired = t.IsHearingImpaired, IsExternal = t.IsExternal,
            }).ToArray(),
        };
        return () => _sink ^= decider.Decide(item, source, user)?.SubtitleIndex ?? -1;
    }

    private sealed class FixedResolver(ResolvedProfileResult result) : IProfileResolver
    {
        public ResolvedProfileResult Resolve(User user, BaseItem item, string? client = null) => result;
        public ResolvedProfileResult Resolve(User user, ItemContext context) => result;
        public void Invalidate(Guid? userId) { }
    }
}