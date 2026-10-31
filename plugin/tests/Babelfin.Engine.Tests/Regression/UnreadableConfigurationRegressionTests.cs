using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class UnreadableConfigurationRegressionTests
{
    private const string Future = "<PluginConfiguration><ConfigVersion>999</ConfigVersion><UnknownAppMode>FutureMode</UnknownAppMode><FutureField>keep exact bytes</FutureField></PluginConfiguration>";

    [Theory]
    [InlineData(Future)]
    [InlineData("<PluginConfiguration><ConfigVersion>8</ConfigVersion><Everyday><Memory>FutureMemory</Memory></Everyday></PluginConfiguration>")]
    [InlineData("<PluginConfiguration><ConfigVersion>999</ConfigVersion><FutureField>")]
    public void UnreadableXmlIsNeverReplacedByTheHostFallback(string original)
    {
        using var host = new LocalStorageTestHost();
        var logger = new StorageTestLogger<Plugin>();
        var plugin = host.CreatePlugin(new PluginConfiguration(), logger, rawXml: original);
        Assert.Equal(original, File.ReadAllText(plugin.ConfigurationFilePath));
        Assert.False(plugin.Configuration.Enabled);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Error is not null);
        plugin.SaveConfiguration();
        plugin.SaveConfiguration(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        Assert.Equal(original, File.ReadAllText(plugin.ConfigurationFilePath));
    }

    [Fact]
    public void ExplicitReplacementBacksUpUnreadableBytesAndEnablesNormalSavesAgain()
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration(), rawXml: Future);
        plugin.UpdateConfiguration(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = true });
        var backup = Assert.Single(Directory.GetFiles(plugin.BackupsFolder, "settings-unreadable-*.xml"));
        Assert.Equal(Future, File.ReadAllText(backup));
        Assert.True(plugin.Configuration.Enabled);
        Assert.DoesNotContain("FutureMode", File.ReadAllText(plugin.ConfigurationFilePath));
        plugin.Configuration.Enabled = false;
        plugin.SaveConfiguration();
        Assert.Contains("<Enabled>false</Enabled>", File.ReadAllText(plugin.ConfigurationFilePath));
        Assert.Equal(Future, File.ReadAllText(backup));
    }

    [Fact]
    public void FailedBackupRefusesExplicitReplacementAndKeepsUnreadableBytes()
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration(), blockBackups: true, rawXml: Future);
        var error = Record.Exception(() => plugin.UpdateConfiguration(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion }));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(Future, File.ReadAllText(plugin.ConfigurationFilePath));
        Assert.False(plugin.Configuration.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedAutomaticSaveUsesTheConfigurationFromTheCompletedUpdate(bool unreadable)
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = false },
            rawXml: unreadable ? Future : null);
        var gate = typeof(Plugin).GetField("_configurationWriteGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        using var started = new ManualResetEventSlim();
        Exception? error = null;
        var save = new Thread(() =>
        {
            try { started.Set(); plugin.SaveConfiguration(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        Monitor.Enter(gate);
        try
        {
            save.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => (save.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
            plugin.UpdateConfiguration(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = true });
        }
        finally
        {
            Monitor.Exit(gate);
            Assert.True(save.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(error);
        Assert.True(plugin.Configuration.Enabled);
        Assert.Equal("true", XDocument.Load(plugin.ConfigurationFilePath).Root!.Element("Enabled")!.Value);
    }

    [Fact]
    public void TypedHostSaveCannotReplaceAReadableFutureConfiguration()
    {
        using var host = new LocalStorageTestHost();
        const string original = "<PluginConfiguration><ConfigVersion>999</ConfigVersion><FutureField>keep exact bytes</FutureField></PluginConfiguration>";
        var plugin = host.CreatePlugin(new PluginConfiguration(), rawXml: original);
        plugin.SaveConfiguration(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        Assert.Equal(original, File.ReadAllText(plugin.ConfigurationFilePath));
        Assert.Equal(999, plugin.Configuration.ConfigVersion);
    }
}
