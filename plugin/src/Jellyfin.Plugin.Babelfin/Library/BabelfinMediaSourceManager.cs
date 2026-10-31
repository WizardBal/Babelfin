using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Decisions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Library;

/// <summary>
/// Wraps Jellyfin's <see cref="IMediaSourceManager"/>. Every member is forwarded unchanged; when media
/// sources are fetched for a person, Babelfin's choice is written into each source afterwards. If anything
/// goes wrong the source stays exactly as Jellyfin made it.
/// </summary>
/// <remarks>
/// What is written for each track Babelfin chooses (a track left to the video keeps everything Jellyfin set):
/// <list type="bullet">
/// <item>Audio: <see cref="MediaSourceInfo.DefaultAudioStreamIndex"/> is the chosen stream and
/// <see cref="MediaSourceInfo.DefaultAudioIndexSource"/> is <see cref="AudioIndexSource.User"/>. For a PlaybackInfo request
/// without an audio index, Jellyfin's StreamBuilder re-selects among all, default-flagged or same-language audio streams unless
/// that flag marks the index as the person's choice; with it, StreamBuilder keeps the chosen stream and transcodes its audio
/// when the device can't play the codec.</item>
/// <item>Subtitles: <see cref="MediaSourceInfo.DefaultSubtitleStreamIndex"/> is the chosen stream, or -1 for off. For a request
/// without a subtitle index, StreamBuilder uses that index unless more than one subtitle stream shares the highest
/// <see cref="MediaStream.Score"/> (scored by Jellyfin from the person's Jellyfin subtitle settings); then it takes the first of
/// them that an External subtitle profile of the device matches. So the chosen stream gets the highest score on its own (one
/// above the highest subtitle score, or 1 when none is scored); with subtitles off and a shared highest score, the first of the
/// tied streams gets one more point, which leaves -1 in charge. Other scores are untouched.</item>
/// <item>A request that names an index is unaffected: StreamBuilder uses a requested index as it is, without reading the flag
/// or the scores.</item>
/// <item>On an error, both indices, the flag and every changed score go back to Jellyfin's values.</item>
/// </list>
/// </remarks>
public sealed class BabelfinMediaSourceManager : IMediaSourceManager, IDisposable
{
    private static readonly long ErrorLogInterval = TimeSpan.FromMinutes(1).Ticks;

    private readonly ITrackDecider _decider;
    private readonly DecoratorStats _stats;
    private readonly ILogger _logger;
    private long _lastErrorLogTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="BabelfinMediaSourceManager"/> class.
    /// </summary>
    /// <param name="inner">Jellyfin's own media source manager.</param>
    /// <param name="decider">Chooses the tracks.</param>
    /// <param name="stats">Counters and recent decisions.</param>
    /// <param name="logger">Logger.</param>
    public BabelfinMediaSourceManager(IMediaSourceManager inner, ITrackDecider decider, DecoratorStats stats, ILogger logger)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _decider = decider;
        _stats = stats;
        _logger = logger;
    }

    /// <summary>Gets the wrapped media source manager.</summary>
    public IMediaSourceManager Inner { get; }

    /// <inheritdoc />
    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => Inner.AddParts(providers);

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => Inner.GetMediaStreams(itemId);

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => Inner.GetMediaStreams(query);

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => Inner.GetMediaAttachments(itemId);

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => Inner.GetMediaAttachments(query);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        var sources = await Inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        if (user is not null)
        {
            // The inner manager already applied Jellyfin's defaults; applying Babelfin's again is idempotent.
            _stats.CountPlayback();
            ApplyAll(item, sources, user);
        }

        return sources;
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
    {
        var sources = Inner.GetStaticMediaSources(item, enablePathSubstitution, user);
        if (user is not null)
        {
            _stats.CountStatic();
            ApplyAll(item, sources, user);
        }

        return sources;
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
        => Inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

    /// <inheritdoc />
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        => Inner.OpenLiveStream(request, cancellationToken);

    /// <inheritdoc />
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
        => Inner.OpenLiveStreamInternal(request, cancellationToken);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        => Inner.GetLiveStream(id, cancellationToken);

    /// <inheritdoc />
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
        => Inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    /// <inheritdoc />
    public ILiveStream GetLiveStreamInfo(string id) => Inner.GetLiveStreamInfo(id);

    /// <inheritdoc />
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => Inner.GetLiveStreamInfoByUniqueId(uniqueId);

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
        => Inner.GetRecordingStreamMediaSources(info, cancellationToken);

    /// <inheritdoc />
    public Task CloseLiveStream(string id) => Inner.CloseLiveStream(id);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        => Inner.GetLiveStreamMediaInfo(id, cancellationToken);

    /// <inheritdoc />
    public bool SupportsDirectStream(string path, MediaProtocol protocol) => Inner.SupportsDirectStream(path, protocol);

    /// <inheritdoc />
    public MediaProtocol GetPathProtocol(string path) => Inner.GetPathProtocol(path);

    /// <inheritdoc />
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
    {
        Inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
        if (user is not null)
        {
            _stats.CountSetDefaults();
            Apply(item, source, user);
        }
    }

    /// <inheritdoc />
    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
        => Inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>Only library videos are eligible. Live TV channels, programs and active recording streams pass through.</summary>
    public static bool CanChooseTracks(BaseItem? item)
        => item is not null && item.MediaType == MediaType.Video && item is not LiveTvChannel && item is not LiveTvProgram;

    private void ApplyAll(BaseItem item, IReadOnlyList<MediaSourceInfo>? sources, User user)
    {
        if (sources is null)
        {
            return;
        }

        foreach (var source in sources)
        {
            Apply(item, source, user);
        }
    }

    /// <summary>
    /// Writes Babelfin's choice into one media source. Never throws.
    /// </summary>
    private void Apply(BaseItem? item, MediaSourceInfo? source, User user)
    {
        if (source is null)
        {
            return;
        }

        var jellyfinAudio = source.DefaultAudioStreamIndex;
        var jellyfinSubtitle = source.DefaultSubtitleStreamIndex;
        var jellyfinAudioSource = source.DefaultAudioIndexSource;
        List<(MediaStream Stream, int? Score)>? jellyfinScores = null;
        try
        {
            // Live TV, music and anything without an item are left alone.
            if (item is null || !CanChooseTracks(item))
            {
                return;
            }

            var decision = _decider.Decide(item, source, user);
            if (decision is null)
            {
                _stats.Record(user.Id, item.Id, source.Id, new DecisionRecord(DateTime.UtcNow, jellyfinAudio, jellyfinSubtitle, null, DecisionOutcome.PassedThrough), source);
                return;
            }

            if (Plugin.Instance?.Configuration.DryRun == true)
            {
                _logger.LogInformation(
                    "[Babelfin] Dry run for {Item} ({SourceId}): audio {OldAudio} -> {NewAudio}, subtitles {OldSubtitle} -> {NewSubtitle}. {Trace}",
                    item.Name,
                    source.Id,
                    jellyfinAudio,
                    decision.AudioIndex ?? jellyfinAudio,
                    jellyfinSubtitle,
                    decision.SubtitleIndex ?? jellyfinSubtitle,
                    string.Join(" ", decision.Trace));
                _stats.Record(user.Id, item.Id, source.Id, new DecisionRecord(DateTime.UtcNow, jellyfinAudio, jellyfinSubtitle, decision, DecisionOutcome.DryRun), source);
                return;
            }

            if (decision.AudioIndex.HasValue)
            {
                source.DefaultAudioStreamIndex = decision.AudioIndex;

                // Without a request index, Jellyfin's StreamBuilder re-selects among all, default-flagged or same-language
                // audio streams unless the source marks the index as the person's choice.
                source.DefaultAudioIndexSource = AudioIndexSource.User;
            }

            if (decision.SubtitleIndex.HasValue)
            {
                source.DefaultSubtitleStreamIndex = decision.SubtitleIndex;
                SettleSubtitleScores(source, decision.SubtitleIndex.Value, ref jellyfinScores);
            }

            _stats.Record(user.Id, item.Id, source.Id, new DecisionRecord(DateTime.UtcNow, jellyfinAudio, jellyfinSubtitle, decision, DecisionOutcome.Applied), source);
        }
        catch (Exception ex)
        {
            // Leave the source exactly as Jellyfin made it.
            source.DefaultAudioStreamIndex = jellyfinAudio;
            source.DefaultSubtitleStreamIndex = jellyfinSubtitle;
            source.DefaultAudioIndexSource = jellyfinAudioSource;
            RestoreScores(jellyfinScores);
            if (item is not null)
            {
                // This lookup passed through; PlaybackInfo must not act on an earlier successful decision.
                _stats.Record(user.Id, item.Id, source.Id, new DecisionRecord(DateTime.UtcNow, jellyfinAudio, jellyfinSubtitle, null, DecisionOutcome.PassedThrough), source);
            }

            _stats.CountError();
            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastErrorLogTicks);
            if (now - last >= ErrorLogInterval && Interlocked.CompareExchange(ref _lastErrorLogTicks, now, last) == last)
            {
                _logger.LogError(ex, "[Babelfin] Couldn't choose tracks for {Item}; Jellyfin's choice kept (further errors are logged at most once a minute)", item?.Name);
            }
        }
    }

    /// <summary>
    /// Adjusts subtitle scores so that StreamBuilder's default subtitle (used when a request carries no subtitle index) is
    /// <paramref name="subtitleIndex"/>. StreamBuilder returns the source's DefaultSubtitleStreamIndex unless more than one
    /// subtitle stream shares the highest Score; then it returns the first of those an External subtitle profile matches.
    /// </summary>
    /// <param name="source">The media source.</param>
    /// <param name="subtitleIndex">Babelfin's subtitle index (-1 = off).</param>
    /// <param name="changed">Receives each changed stream with its previous Score, before the change is made.</param>
    internal static void SettleSubtitleScores(MediaSourceInfo source, int subtitleIndex, ref List<(MediaStream Stream, int? Score)>? changed)
    {
        var streams = source.MediaStreams;
        if (streams is null)
        {
            return;
        }

        MediaStream? chosen = null;
        MediaStream? firstTop = null;
        int? top = null;
        var topCount = 0;
        foreach (var stream in streams)
        {
            if (stream is null || stream.Type != MediaStreamType.Subtitle)
            {
                continue;
            }

            if (subtitleIndex >= 0 && stream.Index == subtitleIndex)
            {
                chosen ??= stream;
            }

            if (stream.Score is not int score)
            {
                continue;
            }

            if (top is null || score > top.Value)
            {
                top = score;
                topCount = 1;
                firstTop = stream;
            }
            else if (score == top.Value)
            {
                topCount++;
            }
        }

        if (top == int.MaxValue)
        {
            // No higher score exists; Jellyfin never scores this high.
            return;
        }

        if (chosen is not null)
        {
            // The chosen stream carries the highest score on its own.
            if (top.HasValue && chosen.Score == top && topCount == 1)
            {
                return;
            }

            SetScore(chosen, Math.Max(top ?? 0, 0) + 1, ref changed);
        }
        else if (topCount > 1 && firstTop is not null && top.HasValue)
        {
            // Off (or an index naming no subtitle stream): a unique highest score leaves DefaultSubtitleStreamIndex in charge.
            SetScore(firstTop, top.Value + 1, ref changed);
        }
    }

    private static void SetScore(MediaStream stream, int score, ref List<(MediaStream Stream, int? Score)>? changed)
    {
        (changed ??= new List<(MediaStream Stream, int? Score)>(1)).Add((stream, stream.Score));
        stream.Score = score;
    }

    /// <summary>Puts back the scores <see cref="SettleSubtitleScores"/> changed, newest first.</summary>
    /// <param name="changed">The changed streams with their previous scores, or null when none changed.</param>
    internal static void RestoreScores(List<(MediaStream Stream, int? Score)>? changed)
    {
        if (changed is null)
        {
            return;
        }

        for (var i = changed.Count - 1; i >= 0; i--)
        {
            changed[i].Stream.Score = changed[i].Score;
        }
    }
}
