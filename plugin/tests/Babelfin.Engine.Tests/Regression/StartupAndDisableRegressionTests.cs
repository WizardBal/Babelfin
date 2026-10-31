using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class StartupAndDisableRegressionTests
{
    [Fact]
    public void FailedStartupPreservationLogsExceptionAndRetainsConfiguration()
    {
        using var host = new LocalStorageTestHost();
        var logger = new StorageTestLogger<Plugin>();
        var version = PluginConfiguration.CurrentVersion - 1;
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = version }, logger, blockBackups: true);
        var bytes = File.ReadAllBytes(plugin.ConfigurationFilePath);
        Assert.Equal(version, plugin.Configuration.ConfigVersion);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Error is IOException
            && entry.Message.Contains(plugin.ConfigurationFilePath, StringComparison.Ordinal));
        Assert.ThrowsAny<IOException>(() => plugin.SaveConfiguration());
        Assert.Equal(bytes, File.ReadAllBytes(plugin.ConfigurationFilePath));
    }

    [Theory]
    [InlineData(PluginStatus.Active)]
    [InlineData(PluginStatus.Restart)]
    [InlineData(PluginStatus.Deleted)]
    public async Task NormalOrUnrelatedShutdownNeverResolvesRestorationServices(PluginStatus status)
    {
        var plugins = Manager(status);
        var services = CallProxy.Create<IServiceProvider>((_, _) => throw new InvalidOperationException("Restoration must not be resolved")).Proxy;
        await new PluginDisableCleanup(plugins, services, NullLogger<PluginDisableCleanup>.Instance).StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitDisableRestoresBothKindsAndLogsFailuresIndependently(bool failFlags)
    {
        var restored = 0;
        var undone = 0;
        var flags = CallProxy.Create<IRememberFlags>((method, _) =>
        {
            if (method.Name != "RestoreAllAsync") return null;
            restored++;
            return failFlags ? Task.FromException(new IOException("Synthetic restoration failure")) : Task.CompletedTask;
        }).Proxy;
        var writer = CallProxy.Create<ICompatibilityWriter>((method, _) =>
        {
            if (method.Name == "UndoAllAsync") undone++;
            return Task.CompletedTask;
        }).Proxy;
        using var provider = new ServiceCollection().AddSingleton(flags).AddSingleton(writer).BuildServiceProvider();
        var logger = new StorageTestLogger<PluginDisableCleanup>();
        await new PluginDisableCleanup(Manager(PluginStatus.Disabled), provider, logger).StopAsync(CancellationToken.None);
        Assert.Equal(1, restored);
        Assert.Equal(1, undone);
        if (failFlags) Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Error is IOException);
        else Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public async Task CancellationBoundsNonCooperativeRestoration()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flags = CallProxy.Create<IRememberFlags>((_, _) => stalled.Task).Proxy;
        var writer = CallProxy.Create<ICompatibilityWriter>().Proxy;
        using var provider = new ServiceCollection().AddSingleton(flags).AddSingleton(writer).BuildServiceProvider();
        var logger = new StorageTestLogger<PluginDisableCleanup>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await new PluginDisableCleanup(Manager(PluginStatus.Disabled), provider, logger).StopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(logger.Entries, entry => entry.Error is OperationCanceledException);
        stalled.SetResult();
    }

    private static IPluginManager Manager(PluginStatus status)
    {
        var manifest = new PluginManifest { Status = status };
        var version = typeof(Plugin).Assembly.GetName().Version!;
        var versionProperty = typeof(PluginManifest).GetProperty("Version")!;
        versionProperty.SetValue(manifest, versionProperty.PropertyType == typeof(string) ? version.ToString() : version);
        var local = new LocalPlugin(Path.GetTempPath(), true, manifest);
        return CallProxy.Create<IPluginManager>((method, args) =>
        {
            if (method.Name != "GetPlugin") return null;
            Assert.Equal(Plugin.PluginId, args[0]);
            Assert.Equal(version, args[1]);
            return local;
        }).Proxy;
    }
}
