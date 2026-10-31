using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Compat;

/// <summary>
/// The person's "let Babelfin handle next-episode tracks" switch: turns off their Jellyfin
/// "remember audio selections" and "remember subtitle selections", keeping the old values in <see cref="PersonSettings.JellyfinChanges"/>,
/// and writes them back when the switch goes off or the plugin is uninstalled.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item>The old values are saved in the person's document before Jellyfin is changed, so a crash in between never loses them. When
/// <see cref="PersonSettings.JellyfinChanges"/> already exists (applied before), the stored values are kept: they are the person's own,
/// not Babelfin's "off".</item>
/// <item>Jellyfin is changed through <see cref="IUserManager.UpdateConfigurationAsync"/> with a <see cref="UserConfiguration"/> built from the
/// person's current values (<see cref="BuildConfiguration"/>), so only the two flags change. When both flags are already what Babelfin wants,
/// Jellyfin is not called.</item>
/// <item>A restore writes only the flags that were recorded (a null stays as it is now), then clears the record. If Jellyfin can't be
/// updated the record is kept, so a later restore (or the uninstall) tries again.</item>
/// <item><see cref="RestoreAllAsync"/> closes admission to flag writes. Uninstall also turns
/// <see cref="PersonSettings.HandleNextEpisode"/> off; temporary disable passes <c>turnSwitchOff: false</c>
/// so re-enabling can resume the person's saved preference.</item>
/// <item>Each change is an activity entry (<c>jellyfin-setting-changed</c> / <c>restored</c>) for the person. Calls are serialised; nothing throws.</item>
/// </list>
/// </remarks>
public sealed class RememberFlags : IRememberFlags
{
    private readonly IUserManager _userManager;
    private readonly IPersonStore _store;
    private readonly IActivityLog _activity;
    private readonly ILogger<RememberFlags> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _writesStopped;

    /// <summary>Initializes a new instance of the <see cref="RememberFlags"/> class.</summary>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="store">The per-person store.</param>
    /// <param name="activity">Babelfin's activity log.</param>
    /// <param name="logger">Logger.</param>
    public RememberFlags(IUserManager userManager, IPersonStore store, IActivityLog activity, ILogger<RememberFlags> logger)
    {
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds a Jellyfin user configuration from the person's current values with the two remember flags replaced
    /// (the same mapping Jellyfin uses when it sends a user's configuration to the apps).
    /// </summary>
    /// <param name="user">The person.</param>
    /// <param name="rememberAudio">The "remember audio selections" value to set.</param>
    /// <param name="rememberSubtitles">The "remember subtitle selections" value to set.</param>
    /// <returns>The configuration.</returns>
    public static UserConfiguration BuildConfiguration(User user, bool rememberAudio, bool rememberSubtitles)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserConfiguration
        {
            AudioLanguagePreference = user.AudioLanguagePreference,
            PlayDefaultAudioTrack = user.PlayDefaultAudioTrack,
            SubtitleLanguagePreference = user.SubtitleLanguagePreference,
            DisplayMissingEpisodes = user.DisplayMissingEpisodes,
            GroupedFolders = user.GetPreferenceValues<Guid>(PreferenceKind.GroupedFolders),
            SubtitleMode = user.SubtitleMode,
            DisplayCollectionsView = user.DisplayCollectionsView,
            EnableLocalPassword = user.EnableLocalPassword,
            OrderedViews = user.GetPreferenceValues<Guid>(PreferenceKind.OrderedViews),
            LatestItemsExcludes = user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes),
            MyMediaExcludes = user.GetPreferenceValues<Guid>(PreferenceKind.MyMediaExcludes),
            HidePlayedInLatest = user.HidePlayedInLatest,
            RememberAudioSelections = rememberAudio,
            RememberSubtitleSelections = rememberSubtitles,
            EnableNextEpisodeAutoPlay = user.EnableNextEpisodeAutoPlay,
            CastReceiverId = user.CastReceiverId,
        };
    }

    /// <inheritdoc />
    public async Task ApplyAsync(User user, CancellationToken cancellationToken)
    {
        if (user is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _writesStopped) != 0) return;

            var hasDocument = false;
            var shouldApply = false;
            _store.Update(user.Id, current =>
            {
                hasDocument = current is not null;
                if (current is null || !current.Enabled || !current.HandleNextEpisode || Plugin.Instance?.Configuration.Enabled == false)
                {
                    return null;
                }

                // A queued request must use the latest switch, particularly after shutdown restoration turned it off.
                shouldApply = true;
                if (current.JellyfinChanges is not null) return null;
                var doc = current.Clone();
                doc.JellyfinChanges = new JellyfinChanges
                {
                    RememberAudioSelections = user.RememberAudioSelections,
                    RememberSubtitleSelections = user.RememberSubtitleSelections,
                    ChangedAt = DateTime.UtcNow,
                };
                return doc;
            });
            if (!hasDocument)
            {
                _logger.LogWarning("[Babelfin] {User} has no Babelfin settings; their Jellyfin remember flags were left as they are", user.Username);
                return;
            }

            if (!shouldApply) return;
            if (!user.RememberAudioSelections && !user.RememberSubtitleSelections)
            {
                _logger.LogInformation("[Babelfin] {User}'s Jellyfin remember flags are already off", user.Username);
                return;
            }

            var before = (Audio: user.RememberAudioSelections, Subtitles: user.RememberSubtitleSelections);
            await _userManager.UpdateConfigurationAsync(user.Id, BuildConfiguration(user, false, false)).ConfigureAwait(false);
            _logger.LogInformation("[Babelfin] Turned off Jellyfin's remember flags for {User} (they were audio {Audio}, subtitles {Subtitles})", user.Username, before.Audio, before.Subtitles);
            _activity.Add(new ActivityEntry(
                DateTime.UtcNow,
                ActivityKinds.JellyfinSettingChanged,
                user.Id,
                null,
                "Babelfin turned off Jellyfin's \"remember audio and subtitle selections\" for you, so it can choose next-episode tracks",
                new Dictionary<string, string>
                {
                    ["rememberAudioSelections"] = Format(before.Audio),
                    ["rememberSubtitleSelections"] = Format(before.Subtitles),
                }));
        }
        catch (OperationCanceledException)
        {
            // Nothing more to do; the record (if written) lets a later restore put things back.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't turn off Jellyfin's remember flags for {User}", user.Username);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RestoreAsync(User user, CancellationToken cancellationToken)
    {
        if (user is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RestoreCoreAsync(user, turnSwitchOff: false).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void StopWrites() => Interlocked.Exchange(ref _writesStopped, 1);

    /// <inheritdoc />
    public async Task RestoreAllAsync(CancellationToken cancellationToken, bool turnSwitchOff = true)
    {
        StopWrites();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var count = 0;
            foreach (var id in _store.UserIds())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("[Babelfin] Restoring Jellyfin's remember flags was stopped before every person was done");
                    break;
                }

                var doc = _store.TryGet(id);
                if (doc is null || (doc.JellyfinChanges is null && !doc.HandleNextEpisode))
                {
                    continue;
                }

                var user = _userManager.GetUserById(id);
                if (user is null)
                {
                    continue;
                }

                if (await RestoreCoreAsync(user, turnSwitchOff).ConfigureAwait(false))
                {
                    count++;
                }
            }

            _logger.LogInformation("[Babelfin] Restored Jellyfin's remember flags for {Count} person(s)", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't restore every person's Jellyfin remember flags");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Format(bool value) => value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();

    private async Task<bool> RestoreCoreAsync(User user, bool turnSwitchOff)
    {
        try
        {
            var stored = _store.TryGet(user.Id);
            if (stored is null)
            {
                return false;
            }

            var changes = stored.JellyfinChanges;
            var restored = false;
            if (changes is not null)
            {
                var audio = changes.RememberAudioSelections ?? user.RememberAudioSelections;
                var subtitles = changes.RememberSubtitleSelections ?? user.RememberSubtitleSelections;
                if (audio != user.RememberAudioSelections || subtitles != user.RememberSubtitleSelections)
                {
                    await _userManager.UpdateConfigurationAsync(user.Id, BuildConfiguration(user, audio, subtitles)).ConfigureAwait(false);
                    restored = true;
                }

                _logger.LogInformation("[Babelfin] Restored Jellyfin's remember flags for {User} (audio {Audio}, subtitles {Subtitles})", user.Username, audio, subtitles);
                _activity.Add(new ActivityEntry(
                    DateTime.UtcNow,
                    ActivityKinds.Restored,
                    user.Id,
                    null,
                    "Babelfin put back your Jellyfin \"remember audio and subtitle selections\" settings",
                    new Dictionary<string, string>
                    {
                        ["rememberAudioSelections"] = Format(audio),
                        ["rememberSubtitleSelections"] = Format(subtitles),
                        ["changed"] = Format(restored),
                    }));
            }

            if (changes is not null || (turnSwitchOff && stored.HandleNextEpisode))
            {
                _store.Update(user.Id, current =>
                {
                    if (current is null)
                    {
                        return null;
                    }

                    var latest = current.JellyfinChanges;
                    if (latest is not null && (changes is null || latest.ChangedAt != changes.ChangedAt
                        || latest.RememberAudioSelections != changes.RememberAudioSelections
                        || latest.RememberSubtitleSelections != changes.RememberSubtitleSelections))
                    {
                        // Another writer installed a new recovery record while Jellyfin was being updated.
                        return null;
                    }

                    var doc = current.Clone();
                    doc.JellyfinChanges = null;
                    if (turnSwitchOff)
                    {
                        doc.HandleNextEpisode = false;
                    }

                    return doc;
                });
            }

            return changes is not null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't restore Jellyfin's remember flags for {User}; the record is kept for a later try", user.Username);
            return false;
        }
    }
}
