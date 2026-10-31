using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Babelfin.Decisions;

/// <summary>
/// Chooses the audio and subtitle tracks for one person and one media source.
/// This is the seam the selection engine plugs into.
/// </summary>
public interface ITrackDecider
{
    /// <summary>
    /// Decides the tracks for a media source.
    /// </summary>
    /// <param name="item">The item being played or shown.</param>
    /// <param name="source">The media source, carrying Jellyfin's own defaults.</param>
    /// <param name="user">The person.</param>
    /// <returns>The decision, or null when Babelfin has nothing to say (Jellyfin's choice stands).</returns>
    TrackDecision? Decide(BaseItem item, MediaSourceInfo source, User user);
}
