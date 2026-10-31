using System;
using Jellyfin.Plugin.Babelfin.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Web;

/// <summary>
/// Puts <see cref="BabelfinMiddleware"/> at the very start of Jellyfin's request pipeline.
/// </summary>
public sealed class BabelfinStartupFilter : IStartupFilter
{
    private readonly ILogger<BabelfinStartupFilter> _logger;
    private readonly DecoratorStats _stats;

    /// <summary>
    /// Initializes a new instance of the <see cref="BabelfinStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="stats">Babelfin's counters (tells whether the media source manager was wrapped).</param>
    public BabelfinStartupFilter(ILogger<BabelfinStartupFilter> logger, DecoratorStats stats)
    {
        _logger = logger;
        _stats = stats;
    }

    private static Plugin? _watchedPlugin;
    private static bool _lastEnabled;
    private static readonly System.Threading.SemaphoreSlim FlagsSync = new(1, 1);
    private static bool _lastCompatibilityMode;

    /// <summary>
    /// Compatibility mode can be turned off through Babelfin's API or Jellyfin's generic plugin-configuration endpoint (the dashboard's
    /// plain switches). Both raise the plugin's ConfigurationChanged; when the mode goes from on to off, the remembered selections
    /// Babelfin wrote are restored. Runs in the background and never throws.
    /// </summary>
    /// <param name="services">The root service provider.</param>
    internal static void WatchCompatibilityMode(IServiceProvider services)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || ReferenceEquals(_watchedPlugin, plugin))
        {
            return;
        }

        _watchedPlugin = plugin;
        _lastEnabled = plugin.Configuration.Enabled;
        SynchronizeFlags(services);
        _lastCompatibilityMode = plugin.Configuration.CompatibilityMode;
        plugin.ConfigurationChanged += (_, config) =>
        {
            var enabled = (config as Configuration.PluginConfiguration)?.Enabled ?? plugin.Configuration.Enabled;
            var wasEnabled = _lastEnabled;
            _lastEnabled = enabled;
            if (wasEnabled != enabled) SynchronizeFlags(services);
            var now = (config as Configuration.PluginConfiguration)?.CompatibilityMode ?? plugin.Configuration.CompatibilityMode;
            var was = _lastCompatibilityMode;
            _lastCompatibilityMode = now;
            if (was && !now)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var writer = services.GetService(typeof(Settings.ICompatibilityWriter)) as Settings.ICompatibilityWriter;
                        if (writer is not null)
                        {
                            await writer.UndoAllAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        (services.GetService(typeof(ILogger<BabelfinStartupFilter>)) as ILogger)?.LogError(ex, "[Babelfin] Couldn't restore Jellyfin's remembered selections after compatibility mode was turned off");
                    }
                });
            }
        };
    }

    private static void SynchronizeFlags(IServiceProvider services)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await SynchronizeFlagsAsync(services).ConfigureAwait(false); }
            catch (Exception ex)
            {
                (services.GetService(typeof(ILogger<BabelfinStartupFilter>)) as ILogger)?.LogError(ex, "[Babelfin] Couldn't synchronize Jellyfin's remember flags after Babelfin's enabled state changed");
            }
        });
    }

    internal static async System.Threading.Tasks.Task SynchronizeFlagsAsync(IServiceProvider services)
    {
        await FlagsSync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (services.GetService(typeof(Settings.IRememberFlags)) is not Settings.IRememberFlags flags
                || services.GetService(typeof(Settings.IPersonStore)) is not Settings.IPersonStore store
                || services.GetService(typeof(MediaBrowser.Controller.Library.IUserManager)) is not MediaBrowser.Controller.Library.IUserManager users) return;
            foreach (var id in store.UserIds())
            {
                try
                {
                    var person = store.TryGet(id);
                    var user = users.GetUserById(id);
                    if (person is null || user is null) continue;
                    // Read the latest state after each await so rapid toggles converge to the current choice.
                    if (Plugin.Instance?.Configuration.Enabled == true && person.Enabled && person.HandleNextEpisode)
                        await flags.ApplyAsync(user, System.Threading.CancellationToken.None).ConfigureAwait(false);
                    else if (person.JellyfinChanges is not null)
                        await flags.RestoreAsync(user, System.Threading.CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    (services.GetService(typeof(ILogger<BabelfinStartupFilter>)) as ILogger)?.LogError(ex, "[Babelfin] Couldn't synchronize Jellyfin's remember flags for {UserId}; other people will still be processed", id);
                }
            }
        }
        finally { FlagsSync.Release(); }
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            if (_stats.Wrapped)
            {
                _logger.LogInformation("[Babelfin] Media source manager wrapped ({Inner}); adding web client middleware", _stats.InnerTypeName);
            }
            else
            {
                _logger.LogWarning("[Babelfin] No IMediaSourceManager registration was found to wrap; Babelfin runs without choosing default tracks");
            }

            Plugin.Services = app.ApplicationServices;
            WatchCompatibilityMode(app.ApplicationServices);
            app.UseMiddleware<BabelfinMiddleware>();
            next(app);
        };
    }
}
