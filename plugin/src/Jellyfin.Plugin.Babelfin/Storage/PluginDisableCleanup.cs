using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Storage;

/// <summary>
/// Restores Jellyfin settings when this loaded Babelfin version was explicitly disabled before shutdown.
/// Normal shutdown does not change settings. Each restoration has a ten-second limit, including non-cooperative work.
/// A crash or manual removal bypasses this hook; recovery then requires re-enabling Babelfin and restoring its changes.
/// </summary>
public sealed class PluginDisableCleanup : IHostedService
{
    private readonly IPluginManager _plugins;
    private readonly IServiceProvider _services;
    private readonly ILogger<PluginDisableCleanup> _logger;

    /// <summary>Creates the shutdown hook without resolving restoration services or changing any settings.</summary>
    public PluginDisableCleanup(IPluginManager plugins, IServiceProvider services, ILogger<PluginDisableCleanup> logger)
    {
        _plugins = plugins;
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = typeof(Plugin).Assembly.GetName().Version!;
            if (_plugins.GetPlugin(Plugin.PluginId, version)?.Manifest.Status != PluginStatus.Disabled) return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't determine whether this plugin was disabled; shutdown leaves Jellyfin settings unchanged");
            return;
        }

        IRememberFlags? remember = null;
        try
        {
            var flags = _services.GetRequiredService<IRememberFlags>();
            // Close admission before scheduling restoration, including already-cancelled shutdowns.
            flags.StopWrites();
            remember = flags;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Plugin disabled: couldn't stop remember-flag writes; recovery records are retained");
        }

        ICompatibilityWriter? compatibility = null;
        try
        {
            var writer = _services.GetRequiredService<ICompatibilityWriter>();
            // Close admission even when shutdown is already cancelled. This is nonblocking; the bounded
            // background undo below acquires the ledger lock after any admitted write has finished.
            writer.StopWrites();
            compatibility = writer;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Plugin disabled: couldn't stop compatibility writes; recovery records are retained");
        }

        if (remember is not null)
            await Restore("remember flags", ct => remember.RestoreAllAsync(ct), cancellationToken).ConfigureAwait(false);
        if (compatibility is not null)
            await Restore("compatibility selections", ct => compatibility.UndoAllAsync(ct), cancellationToken).ConfigureAwait(false);
    }

    private async Task Restore(string what, Func<CancellationToken, Task> restore, CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await Task.Run(() => restore(deadline.Token), deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            _logger.LogInformation("[Babelfin] Plugin disabled: restored {What}", what);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Plugin disabled: couldn't finish restoring {What}; retained recovery records may require re-enabling Babelfin to retry", what);
        }
    }
}
