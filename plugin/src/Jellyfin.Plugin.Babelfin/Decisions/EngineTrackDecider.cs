using System;
using System.Collections.Generic;
using MediaBrowser.Model.Globalization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Decisions;

/// <summary>
/// The real track decider: resolves the person's profile for the item (cached), maps the
/// source's streams and asks the selection engine. Returns null (Jellyfin's choice stands) when Babelfin is off on the server or for the person.
/// A subtitle index of -1 means "subtitles off"; a null audio index (no audio tracks) keeps Jellyfin's.
/// </summary>
public sealed class EngineTrackDecider : ITrackDecider
{
    private readonly IProfileResolver _resolver;
    private readonly ILocalizationManager? _localization;
    private readonly ILogger<EngineTrackDecider> _logger;

    /// <summary>Initializes a new instance of the <see cref="EngineTrackDecider"/> class.</summary>
    /// <param name="resolver">The profile resolver.</param>
    /// <param name="logger">The logger.</param>
    public EngineTrackDecider(IProfileResolver resolver, ILogger<EngineTrackDecider> logger, ILocalizationManager? localization = null)
    {
        _localization = localization;
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Same normalization as Jellyfin 12.2 MediaSourceManager: region names stay exact;
    // otherwise every three-letter spelling is passed to the native subtitle policy.
    internal IReadOnlyList<string> NormalizeSubtitleLanguages(string? language)
    {
        if (string.IsNullOrEmpty(language)) return Array.Empty<string>();
        var culture = _localization?.FindLanguageInfo(language);
        return culture is null ? new[] { language }
            : culture.Name.Contains('-', StringComparison.OrdinalIgnoreCase) ? new[] { culture.Name } : culture.ThreeLetterISOLanguageNames;
    }

    /// <inheritdoc />
    public TrackDecision? Decide(BaseItem item, MediaSourceInfo source, User user)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(user);
        if (Plugin.Instance?.Configuration is { Enabled: false })
        {
            return null;
        }

        var result = _resolver.Resolve(user, item);
        if (!result.PersonEnabled)
        {
            return null;
        }

        var tracks = StreamMapper.Map(source.MediaStreams, source.Path);
        var facts = new ItemFacts(
            result.Context.OriginalLanguage,
            result.LibraryUsualOriginalLanguage,
            result.BurnedInLanguages,
            result.Context.Kind == ContentKind.Episode,
            source.DefaultAudioStreamIndex,
            source.DefaultSubtitleStreamIndex,
            user.SubtitleMode.ToString(),
            user.SubtitleLanguagePreference,
            NormalizeSubtitleLanguages(user.SubtitleLanguagePreference));
        var decision = EngineServices.Current.Selector.Decide(tracks, facts, result.Profile, device: null, result.Manual, includeTrace: false, sourceId: source.Id);

        // Both tracks left to the video: Babelfin has nothing to say, so Jellyfin's behaviour stands everywhere (no chip, no replacing,
        // no correction after start).
        if (decision.AudioLeftToVideo && decision.SubtitlesLeftToVideo)
        {
            return null;
        }

        var trace = new DeferredTextTrace(decision.Trace);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "[Babelfin] {Item} ({SourceId}): audio {Audio}, subtitles {Subtitle}",
                item.Name,
                source.Id,
                decision.AudioIndex,
                decision.SubtitleIndex ?? -1);
        }

        // A track left to the video is "not set" (null): the media source keeps Jellyfin's index for it.
        return new TrackDecision(
            decision.AudioLeftToVideo ? null : decision.AudioIndex,
            decision.SubtitlesLeftToVideo ? null : decision.SubtitleIndex ?? -1,
            trace,
            source.DefaultAudioStreamIndex,
            source.DefaultSubtitleStreamIndex,
            null, // The TrackDecision getter builds the explanation only for diagnostics.
            decision);
    }
}
