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
/// sources are fetched for a person, Babelfin's choice is written into each source's
/// DefaultAudioStreamIndex / DefaultSubtitleStreamIndex afterwards. If anything goes wrong the source
/// stays exactly as Jellyfin made it.
/// </summary>
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
            }

            if (decision.SubtitleIndex.HasValue)
            {
                source.DefaultSubtitleStreamIndex = decision.SubtitleIndex;
            }

            _stats.Record(user.Id, item.Id, source.Id, new DecisionRecord(DateTime.UtcNow, jellyfinAudio, jellyfinSubtitle, decision, DecisionOutcome.Applied), source);
        }
        catch (Exception ex)
        {
            // Leave the source exactly as Jellyfin made it.
            source.DefaultAudioStreamIndex = jellyfinAudio;
            source.DefaultSubtitleStreamIndex = jellyfinSubtitle;
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
}
