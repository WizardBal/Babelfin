using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;

namespace Jellyfin.Plugin.Babelfin.Decisions;

/// <summary>
/// The engine objects built from the admin's editable tables (keyword lists and extra language aliases): the language table,
/// the classifier and the selector. Immutable once built; <see cref="Current"/> is rebuilt when the configuration changes.
/// </summary>
public sealed class EngineServices
{
    private static readonly object SyncRoot = new();
    private static Binding? _binding;
    private static Plugin? _subscribedPlugin;
    private static long _nextRecognitionVersion;

    /// <summary>Initializes a new instance of the <see cref="EngineServices"/> class from a configuration.</summary>
    /// <param name="config">The configuration (null = built-in defaults).</param>
    public EngineServices(PluginConfiguration? config)
    {
        RecognitionVersion = Interlocked.Increment(ref _nextRecognitionVersion);
        var aliases = new Dictionary<string, string>(AliasTable.Default.RawToCode, StringComparer.Ordinal);
        if (config?.LanguageAliases is { } extra)
        {
            foreach (var alias in extra)
            {
                if (!string.IsNullOrWhiteSpace(alias?.Raw))
                {
                    aliases[LanguageTable.NormalizeKey(alias.Raw)] = (alias.Code ?? string.Empty).Trim();
                }
            }
        }

        Languages = config?.LanguageAliases is { Count: > 0 }
            ? new LanguageTable(IsoLanguages.All, new AliasTable(aliases), VarietyKeywordTable.Default)
            : LanguageTable.Default;
        var keywords = config?.Keywords?.ToTables() ?? KeywordTables.Default;
        Classifier = new TrackClassifier(Languages, keywords);
        Selector = new TrackSelector(Languages, Classifier);
    }

    /// <summary>Gets the engine built from the running plugin's configuration (the built-in defaults when no plugin instance exists).</summary>
    public static EngineServices Current
    {
        get
        {
            var plugin = Plugin.Instance;
            var binding = Volatile.Read(ref _binding);
            if (binding is not null && ReferenceEquals(binding.Plugin, plugin))
            {
                return binding.Services;
            }

            lock (SyncRoot)
            {
                binding = _binding;
                if (binding is not null && ReferenceEquals(binding.Plugin, plugin))
                {
                    return binding.Services;
                }

                if (!ReferenceEquals(_subscribedPlugin, plugin))
                {
                    if (_subscribedPlugin is not null)
                    {
                        _subscribedPlugin.ConfigurationChanged -= OnConfigurationChanged;
                    }

                    _subscribedPlugin = plugin;
                    if (plugin is not null)
                    {
                        plugin.ConfigurationChanged += OnConfigurationChanged;
                    }
                }

                var services = new EngineServices(plugin?.Configuration);
                Volatile.Write(ref _binding, new Binding(plugin, services));
                return services;
            }
        }
    }

    /// <summary>Gets the recognition generation; every rebuilt engine owns a fresh classification cache.</summary>
    public long RecognitionVersion { get; }

    /// <summary>Gets the language table.</summary>
    public LanguageTable Languages { get; }

    /// <summary>Gets the track classifier.</summary>
    public TrackClassifier Classifier { get; }

    /// <summary>Gets the track selector.</summary>
    public TrackSelector Selector { get; }

    /// <summary>Forgets the cached engine so the next <see cref="Current"/> rebuilds it from the configuration.</summary>
    public static void Reset()
    {
        lock (SyncRoot)
        {
            Volatile.Write(ref _binding, null);
        }
    }

    private static void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration args) => Reset();
    private sealed record Binding(Plugin? Plugin, EngineServices Services);
}
