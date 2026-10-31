using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Babelfin;

/// <summary>
/// Babelfin: the right audio and subtitles, for every person, in every library.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly object _configurationWriteGate = new();
    private int? _preservationVersion;
    private bool _explicitConfigurationSave;
    private bool _configurationLoadFailed;
    private bool _preserveUnreadableOriginal;

    /// <summary>The plugin id. Must match "guid" in meta.json.</summary>
    public static readonly Guid PluginId = new("dc7f86a9-90f3-4282-93d9-424fb4c64b4d");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server paths.</param>
    /// <param name="xmlSerializer">Serializer for the plugin configuration file.</param>
    /// <param name="logger">Startup logger supplied by the host.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin>? logger = null)
        : base(applicationPaths, xmlSerializer)
    {
        Paths = applicationPaths;
        Instance = this;

        // Jellyfin's base loader writes defaults when deserialization fails. Read existing files here instead,
        // so an unknown future enum, malformed XML or a temporary read error can never overwrite the original.
        var configurationExisted = File.Exists(ConfigurationFilePath);
        var startupLogger = logger ?? Services?.GetService<ILoggerFactory>()?.CreateLogger<Plugin>() ?? NullLogger<Plugin>.Instance;
        if (configurationExisted)
        {
            try
            {
                Configuration = xmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), ConfigurationFilePath) as PluginConfiguration
                    ?? throw new InvalidDataException("The configuration contains no settings object.");
            }
            catch (Exception ex)
            {
                _configurationLoadFailed = true;
                _preserveUnreadableOriginal = true;
                Configuration = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = false };
                startupLogger.LogError(ex, "[Babelfin] Couldn't read configuration {File}; the original bytes were left untouched and selection is disabled. Use a compatible Babelfin version or explicitly replace the configuration after reviewing a backup", ConfigurationFilePath);
                return;
            }
        }
        else
        {
            Configuration = new PluginConfiguration();
        }

        var loaded = Configuration;
        if (configurationExisted && loaded.ConfigVersion != PluginConfiguration.CurrentVersion)
            _preservationVersion = loaded.ConfigVersion;

        // A failed backup leaves both the original bytes and the in-memory configuration unchanged.
        try
        {
            if (_preservationVersion.HasValue) BackUpBeforeUpgrade();
            if (Configuration.Upgrade()) SaveConfiguration();
        }
        catch (Exception ex)
        {
            // Keep startup available and the preservation requirement in place for a later save.
            startupLogger.LogError(ex, "[Babelfin] Couldn't preserve or upgrade configuration {File} from version {Version}; check storage permissions and free space before retrying. The original configuration is retained", ConfigurationFilePath, loaded.ConfigVersion);
        }
    }

    private void BackUpBeforeUpgrade()
    {
        var version = _preservationVersion ?? Configuration.ConfigVersion;
        if (!_preserveUnreadableOriginal && !_preservationVersion.HasValue && version == PluginConfiguration.CurrentVersion) return;
        if (string.IsNullOrEmpty(ConfigurationFilePath) || !File.Exists(ConfigurationFilePath))
        {
            return;
        }

        var folder = BackupsFolder;
        Directory.CreateDirectory(folder);
        var label = _preserveUnreadableOriginal ? "unreadable" : "version" + version;
        var target = Path.Combine(folder, $"settings-{label}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.xml");
        File.Copy(ConfigurationFilePath, target, overwrite: false);
        _preservationVersion = null;
        _preserveUnreadableOriginal = false;
    }

    /// <summary>Preserves the original before any explicit administrative replacement, including a failed startup upgrade.</summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        lock (_configurationWriteGate)
        {
            if (_preserveUnreadableOriginal || _preservationVersion.HasValue || Configuration.ConfigVersion > PluginConfiguration.CurrentVersion) BackUpBeforeUpgrade();
            _explicitConfigurationSave = true;
            try { base.UpdateConfiguration(configuration); }
            finally { _explicitConfigurationSave = false; }
        }
    }

    /// <summary>Discovery metadata may not rewrite unreadable or newer configurations.</summary>
    public override void SaveConfiguration()
    {
        lock (_configurationWriteGate) SaveConfiguration(Configuration);
    }

    /// <summary>Protects every host save overload; an explicit replacement first preserves the original bytes.</summary>
    /// <param name="config">The configuration to save.</param>
    public override void SaveConfiguration(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_configurationWriteGate)
        {
            if (!_explicitConfigurationSave && (_configurationLoadFailed
                || Configuration.ConfigVersion > PluginConfiguration.CurrentVersion
                || config.ConfigVersion > PluginConfiguration.CurrentVersion)) return;
            if (_preserveUnreadableOriginal || _preservationVersion.HasValue) BackUpBeforeUpgrade();
            base.SaveConfiguration(config);
            if (_explicitConfigurationSave) _configurationLoadFailed = false;
        }
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets or sets the server's root service provider, captured when the web host starts (used by <see cref="BasePlugin{T}.OnUninstalling"/>, which has no DI).</summary>
    public static IServiceProvider? Services { get; set; }

    /// <summary>Gets the running assembly version as a four-part string.</summary>
    public static string VersionString { get; } = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>Gets the server paths.</summary>
    public IApplicationPaths Paths { get; }

    /// <summary>Gets the folder Babelfin keeps its backups in, <c>{DataPath}/babelfin/backups</c>: the settings as they were before an upgrade or a reset.</summary>
    public string BackupsFolder => Path.Combine(Paths.DataPath, "babelfin", "backups");

    /// <inheritdoc />
    public override string Name => "Babelfin";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "The right audio and subtitles, for every person, in every library.";

    /// <summary>
    /// Puts back what Babelfin changed in Jellyfin before the plugin is removed: every person's remember
    /// flags (<see cref="IRememberFlags.RestoreAllAsync"/>) and the remembered selections written in compatibility mode
    /// (<see cref="ICompatibilityWriter.UndoAllAsync"/>). Each step runs with a 30-second limit and is logged; nothing here throws.
    /// Babelfin's data folder is left as it is.
    /// </summary>
    public override void OnUninstalling()
    {
        var services = Services;
        ILogger logger = NullLogger.Instance;
        try
        {
            logger = services?.GetService<ILoggerFactory>()?.CreateLogger<Plugin>() ?? (ILogger)NullLogger.Instance;
            if (services is null)
            {
                logger.LogWarning("[Babelfin] Uninstalling before the server finished starting: Jellyfin settings Babelfin changed can't be restored now");
            }
            else
            {
                logger.LogInformation("[Babelfin] Uninstalling: restoring the Jellyfin settings Babelfin changed");
                var writer = services.GetService<ICompatibilityWriter>();
                writer?.StopWrites();
                RunStep(logger, "people's remember flags", services.GetService<IRememberFlags>() is { } flags ? ct => flags.RestoreAllAsync(ct) : null);
                RunStep(logger, "remembered selections written in compatibility mode", writer is not null ? ct => writer.UndoAllAsync(ct) : null);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Babelfin] Restoring Jellyfin settings on uninstall failed");
        }

        try
        {
            base.OnUninstalling();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Babelfin] Uninstall clean-up failed");
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "Babelfin",
                DisplayName = "Babelfin",
                EmbeddedResourcePath = "Babelfin.configPage.html",
                EnableInMainMenu = true,
                MenuIcon = "translate"
            }
        };
    }

    private static void RunStep(ILogger logger, string what, Func<CancellationToken, Task>? step)
    {
        if (step is null)
        {
            logger.LogInformation("[Babelfin] Uninstall: nothing to restore for {What} (service not available)", what);
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var task = Task.Run(() => step(cts.Token), cts.Token);
            if (task.Wait(TimeSpan.FromSeconds(30)))
            {
                logger.LogInformation("[Babelfin] Uninstall: restored {What}", what);
            }
            else
            {
                cts.Cancel();
                logger.LogWarning("[Babelfin] Uninstall: restoring {What} took longer than 30 seconds and was left to finish in the background", what);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Babelfin] Uninstall: couldn't restore {What}", what);
        }
    }
}
