using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Serialization;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Babelfin.Engine.Tests.Regression;

internal sealed class StorageTestLogger<T> : ILogger<T>
{
    internal readonly List<(LogLevel Level, Exception? Error, string Message)> Entries = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((level, error, formatter(state, error)));
    }
}

internal sealed class LocalStorageTestHost : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly IServiceProvider? _previousServices = Plugin.Services;
    internal string Folder { get; } = Path.Combine(Path.GetTempPath(), "babelfin-local-storage-" + Guid.NewGuid().ToString("N"));
    internal PersonStore Store => new(Folder, NullLogger<PersonStore>.Instance);
    internal Plugin CreatePlugin(PluginConfiguration configuration, ILogger<Plugin>? logger = null, bool blockBackups = false, string? rawXml = null)
    {
        Directory.CreateDirectory(Folder);
        var paths = CallProxy.Create<IApplicationPaths>((method, _) => method.ReturnType == typeof(string) ? Folder : null).Proxy;
        var serializer = CallProxy.Create<IXmlSerializer>((method, args) =>
        {
            if (method.Name == "DeserializeFromFile")
            {
                using var input = File.OpenRead(args.OfType<string>().Last());
                return new XmlSerializer(args.OfType<Type>().Single()).Deserialize(input);
            }
            if (method.Name == "SerializeToFile")
            {
                using var output = File.Create(args.OfType<string>().Last());
                var value = args.OfType<BasePluginConfiguration>().Single();
                new XmlSerializer(value.GetType()).Serialize(output, value);
            }
            return null;
        }).Proxy;
        var bootstrap = new Plugin(paths, serializer);
        if (rawXml is not null) File.WriteAllText(bootstrap.ConfigurationFilePath, rawXml, new UTF8Encoding(false));
        else using (var output = File.Create(bootstrap.ConfigurationFilePath)) new XmlSerializer(typeof(PluginConfiguration)).Serialize(output, configuration);
        if (blockBackups)
        {
            Directory.CreateDirectory(Path.Combine(Folder, "babelfin"));
            File.WriteAllText(Path.Combine(Folder, "babelfin", "backups"), "A file blocks backup directory creation.", Encoding.UTF8);
        }
        return new Plugin(paths, serializer, logger);
    }
    public void Dispose()
    {
        typeof(Plugin).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, _previous);
        Plugin.Services = _previousServices;
        // Keep isolated test artifacts available for inspection; never touch a server data folder.
    }
}
