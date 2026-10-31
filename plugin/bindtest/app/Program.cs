// Simulates Jellyfin 12.2: the server assemblies (12.2.0.0, from NuGet) live in the default context; the plugin
// (compiled against the 12.0.0 packages) is loaded through a copy of Jellyfin's PluginLoadContext, validated with
// GetTypes(), instantiated, asked to register its services, and every method is JIT-compiled so any member that
// doesn't exist in 12.2 fails here instead of on the server. Usage: dotnet run -c Release -- <plugin output folder>
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;

try
{
    return Run(args);
}
catch (Exception ex)
{
    Console.WriteLine("BIND TEST FAIL: " + ex);
    return 1;
}

static int Run(string[] args)
{
    var pluginDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../../src/Jellyfin.Plugin.Babelfin/bin/Release/net10.0"));
    var dll = Path.Combine(pluginDir, "Jellyfin.Plugin.Babelfin.dll");
    if (!File.Exists(dll)) throw new FileNotFoundException("Build the plugin first (Release)", dll);

    // The server's own assemblies, loaded in the default context like Jellyfin does.
    var server = new[] { typeof(BasePlugin).Assembly, typeof(IMediaSourceManager).Assembly, typeof(MediaStream).Assembly, typeof(Jellyfin.Data.Enums.MediaType).Assembly, typeof(User).Assembly };
    foreach (var a in server)
    {
        Console.WriteLine($"server {a.GetName().Name} {a.GetName().Version} ({(AssemblyLoadContext.GetLoadContext(a) == AssemblyLoadContext.Default ? "default context" : "OTHER context")})");
    }

    var alc = new PluginLoadContext(pluginDir);
    var asm = alc.LoadFromAssemblyPath(dll);
    Console.WriteLine($"loaded {asm.GetName().Name} {asm.GetName().Version} into '{AssemblyLoadContext.GetLoadContext(asm)?.Name ?? "?"}' ({AssemblyLoadContext.GetLoadContext(asm)?.GetType().Name})");

    var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
    Console.WriteLine("Informational version: " + informational);
    if (args.Length > 1)
    {
        if (args.Length != 3 || args[1] != "--source-commit" || !System.Text.RegularExpressions.Regex.IsMatch(args[2], "^[0-9a-f]{40}$"))
            throw new ArgumentException("Use --source-commit with a full lowercase commit id.");
        if (!informational.EndsWith("+" + args[2], StringComparison.Ordinal))
            throw new InvalidOperationException("The plugin informational version does not identify the expected source commit.");
    }

    var badRefs = new List<string>();
    foreach (var r in asm.GetReferencedAssemblies().Where(r => r.Name!.StartsWith("MediaBrowser", StringComparison.Ordinal) || r.Name!.StartsWith("Jellyfin", StringComparison.Ordinal)))
    {
        var loaded = alc.LoadFromAssemblyName(r);
        var context = AssemblyLoadContext.GetLoadContext(loaded) == AssemblyLoadContext.Default ? "default" : "plugin";
        Console.WriteLine($"  plugin references {r.Name} {r.Version} -> loaded {loaded.GetName().Version} ({context} context)");
        if (r.Version != new Version(12, 0, 0, 0)) badRefs.Add($"{r.Name} referenced as {r.Version}, expected 12.0.0.0");
        if (loaded.GetName().Version != new Version(12, 2, 0, 0)) badRefs.Add($"{r.Name} loaded as {loaded.GetName().Version}, expected 12.2.0.0");
        if (context != "default") badRefs.Add($"{r.Name} was loaded into the plugin context; Jellyfin's types would not match");
    }

    if (badRefs.Count > 0) throw new InvalidOperationException("Reference check failed:\n  " + string.Join("\n  ", badRefs));

    Type[] types;
    try
    {
        types = asm.GetTypes(); // what Jellyfin does to validate a plugin
    }
    catch (ReflectionTypeLoadException ex)
    {
        throw new InvalidOperationException("GetTypes failed:\n  " + string.Join("\n  ", ex.LoaderExceptions.Select(e => e?.Message)), ex);
    }

    Console.WriteLine($"GetTypes OK: {types.Length} types");
    var pluginType = types.Single(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);
    var regType = types.Single(t => typeof(IPluginServiceRegistrator).IsAssignableFrom(t) && !t.IsAbstract);
    Console.WriteLine($"plugin type {pluginType.FullName}, base {pluginType.BaseType} from the {(AssemblyLoadContext.GetLoadContext(pluginType.BaseType!.Assembly) == AssemblyLoadContext.Default ? "default" : "plugin")} context");

    var tmp = Directory.CreateTempSubdirectory("bfbind").FullName;

    // A schema-version 1 settings file with legacy probe fields, plus an admin everyday default
    // (audio: Japanese; subtitles: full English when the audio isn't understood). Loading it must upgrade to the current version,
    // retire the probe fields, clear the server-wide track defaults (version 4: neutral until someone chooses) and copy the old
    // file to babelfin/backups first.
    Directory.CreateDirectory(Path.Combine(tmp, "cfg"));
    File.WriteAllText(Path.Combine(tmp, "cfg", "Jellyfin.Plugin.Babelfin.xml"), """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <ConfigVersion>1</ConfigVersion>
          <Enabled>true</Enabled>
          <DryRun>false</DryRun>
          <ProbeAudioLanguage>jpn</ProbeAudioLanguage>
          <ProbeSubtitleLanguage>eng</ProbeSubtitleLanguage>
          <ProbeReplaceRequestIndices>true</ProbeReplaceRequestIndices>
          <Everyday>
            <AudioChain>
              <AudioStepSetting><Kind>Language</Kind><Language>jpn</Language></AudioStepSetting>
              <AudioStepSetting><Kind>FileDefault</Kind></AudioStepSetting>
            </AudioChain>
            <WhenAudioNotUnderstood>Full</WhenAudioNotUnderstood>
            <SubtitleLanguages><string>eng</string></SubtitleLanguages>
          </Everyday>
        </PluginConfiguration>
        """.TrimStart());

    var plugin = (IPlugin)Activator.CreateInstance(pluginType, new object?[] { new FakePaths(tmp), new FakeXml(), null })!;
    Console.WriteLine($"instance: Name={plugin.Name} Id={plugin.Id} Version={plugin.Version}");
    var cfg = ((IHasPluginConfiguration)plugin).Configuration;
    var savedXml = File.ReadAllText(Path.Combine(tmp, "cfg", "Jellyfin.Plugin.Babelfin.xml"));
    var currentVersion = cfg.GetType().GetField("CurrentVersion", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue()
        ?? throw new InvalidOperationException("PluginConfiguration.CurrentVersion not found");
    var versionTag = $"<ConfigVersion>{currentVersion}</ConfigVersion>";
    if (!versionTag.Equals("<ConfigVersion>8</ConfigVersion>", StringComparison.Ordinal) || !savedXml.Contains(versionTag, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("settings were not upgraded and saved with " + versionTag + ":\n" + savedXml);
    }

    if (savedXml.Contains("Probe", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("the retired probe fields were written back:\n" + savedXml);
    }

    if (savedXml.Contains("<Language>jpn</Language>", StringComparison.Ordinal) || savedXml.Contains("LastUpgradeNote", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("the server-wide track default survived the version 4 upgrade, or an upgrade note was written:\n" + savedXml);
    }

    var backups = Directory.Exists(Path.Combine(tmp, "babelfin", "backups")) ? Directory.GetFiles(Path.Combine(tmp, "babelfin", "backups"), "settings-version1-*.xml") : Array.Empty<string>();
    if (backups.Length != 1 || !File.ReadAllText(backups[0]).Contains("<Language>jpn</Language>", StringComparison.Ordinal) || !File.ReadAllText(backups[0]).Contains("<ProbeAudioLanguage>", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("the previous settings file was not copied to babelfin/backups before the upgrade (" + backups.Length + " copies)");
    }

    Console.WriteLine($"settings upgraded from version 1 and saved with {versionTag}; probe retired, server-wide track defaults cleared, the old file kept in {Path.GetFileName(backups[0])}, no upgrade note");

    // Now an admin default chosen after the upgrade (Japanese audio, full English when the audio isn't understood), set through
    // the plugin's own types so the decision below runs exactly as on the server.
    var pluginAssembly = pluginType.Assembly;
    var everyday = cfg.GetType().GetProperty("Everyday")!.GetValue(cfg)!;
    var stepType = pluginAssembly.GetType("Jellyfin.Plugin.Babelfin.Settings.AudioStepSetting", true)!;
    var kindType = pluginAssembly.GetType("Jellyfin.Plugin.Babelfin.Engine.Selection.AudioStepKind", true)!;
    var wantType = pluginAssembly.GetType("Jellyfin.Plugin.Babelfin.Engine.Selection.SubtitleWant", true)!;
    var chain = Array.CreateInstance(stepType, 2);
    chain.SetValue(Activator.CreateInstance(stepType, Enum.Parse(kindType, "Language"), "jpn", null), 0);
    chain.SetValue(Activator.CreateInstance(stepType, Enum.Parse(kindType, "VideoDefault"), null, null), 1);
    everyday.GetType().GetProperty("AudioChain")!.SetValue(everyday, chain);
    everyday.GetType().GetProperty("WhenAudioNotUnderstood")!.SetValue(everyday, Enum.Parse(wantType, "Full"));
    everyday.GetType().GetProperty("SubtitleLanguages")!.SetValue(everyday, new[] { "eng" });

    // RegisterServices on a collection that already holds Jellyfin's IMediaSourceManager.
    var core = new DummyMediaSourceManagerFactory();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IMediaSourceManager>(_ => core.Create());
    // The server services Babelfin's resolver asks for (12.2 types from the default context), reduced to what a decision needs.
    services.AddSingleton<ILibraryManager>(DispatchProxy.Create<ILibraryManager, EmptyLibraryManager>());
    services.AddSingleton<ICollectionManager>(DispatchProxy.Create<ICollectionManager, EmptyCollectionManager>());
    ((IPluginServiceRegistrator)Activator.CreateInstance(regType)!).RegisterServices(services, null!);
    var msmDescriptors = services.Count(s => s.ServiceType == typeof(IMediaSourceManager));
    Console.WriteLine($"registered {services.Count} services; IMediaSourceManager descriptors: {msmDescriptors}; index 0: {services[0].ServiceType.Name} -> {services[0].ImplementationType?.Name}");
    if (msmDescriptors != 1) throw new InvalidOperationException($"expected exactly one IMediaSourceManager descriptor, found {msmDescriptors}");
    if (services[0].ServiceType.Name != "IStartupFilter") throw new InvalidOperationException("Babelfin's startup filter is not at index 0");

    // Resolve the decorator across the context boundary and let the engine decide with the admin everyday default above:
    // the person "bind" has no Jellyfin preferences, so Japanese isn't understood and full English subtitles (#4, not the forced #3) are wanted.
    using (var sp = services.BuildServiceProvider())
    {
        var msm = sp.GetRequiredService<IMediaSourceManager>();
        var user = new User("bind", "Default", "Default") { Id = Guid.NewGuid() };
        var item = new Episode { Id = Guid.NewGuid(), Name = "Bind test episode" };
        var source = msm.GetStaticMediaSources(item, true, user)[0];
        Console.WriteLine($"resolved {msm.GetType().FullName} (from the {(AssemblyLoadContext.GetLoadContext(msm.GetType().Assembly) == alc ? "plugin" : "default")} context); admin default jpn / full eng chose {source.DefaultAudioStreamIndex}/{source.DefaultSubtitleStreamIndex}");
        if (msm.GetType().Name != "BabelfinMediaSourceManager" || source.DefaultAudioStreamIndex != 2 || source.DefaultSubtitleStreamIndex != 4)
        {
            throw new InvalidOperationException("the decorator did not apply the engine's choice 2/4 on Jellyfin 12.2 types");
        }

        var personFile = Directory.GetFiles(Path.Combine(tmp, "babelfin", "users"), "*.json").SingleOrDefault();
        if (personFile is null || !File.ReadAllText(personFile).Contains("\"schemaVersion\": 3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the person's settings file was not seeded under babelfin/users");
        }

        Console.WriteLine($"seeded {Path.GetFileName(personFile)} on first decision");
    }

    // A current old configuration with the retired activity field must remain untouched on startup.
    var legacyRoot = Directory.CreateTempSubdirectory("bfbind-activity").FullName;
    Directory.CreateDirectory(Path.Combine(legacyRoot, "cfg"));
    var legacyPath = Path.Combine(legacyRoot, "cfg", "Jellyfin.Plugin.Babelfin.xml");
    var legacyXml = $"<PluginConfiguration><ConfigVersion>{currentVersion}</ConfigVersion><ActivityRetentionDays>45</ActivityRetentionDays></PluginConfiguration>";
    File.WriteAllText(legacyPath, legacyXml);
    var legacyBytes = File.ReadAllBytes(legacyPath);
    var legacyPlugin = (IHasPluginConfiguration)Activator.CreateInstance(pluginType, new object?[] { new FakePaths(legacyRoot), new FakeXml(), null })!;
    if (!legacyBytes.SequenceEqual(File.ReadAllBytes(legacyPath)) || (int)legacyPlugin.Configuration.GetType().GetProperty("ActivityRetentionDays")!.GetValue(legacyPlugin.Configuration)! != 45)
        throw new InvalidOperationException("startup did not preserve the retired activity setting and original XML");
    Console.WriteLine("PASS: retired activity setting loads without rewriting XML");

    // downgrades preserve unknown XML until the admin explicitly saves.
    var futureRoot = Directory.CreateTempSubdirectory("bfbind-future").FullName;
    Directory.CreateDirectory(Path.Combine(futureRoot, "cfg"));
    var futurePath = Path.Combine(futureRoot, "cfg", "Jellyfin.Plugin.Babelfin.xml");
    const string futureXml = "<PluginConfiguration><ConfigVersion>999</ConfigVersion><Enabled>true</Enabled><FutureField>keep these bytes</FutureField></PluginConfiguration>";
    File.WriteAllText(futurePath, futureXml);
    var futurePlugin = (IHasPluginConfiguration)Activator.CreateInstance(pluginType, new object?[] { new FakePaths(futureRoot), new FakeXml(), null })!;
    if (File.ReadAllText(futurePath) != futureXml || (int)futurePlugin.Configuration.GetType().GetProperty("ConfigVersion")!.GetValue(futurePlugin.Configuration)! != 999)
        throw new InvalidOperationException("startup rewrote a newer-format configuration");
    var futureBackups = Directory.GetFiles(Path.Combine(futureRoot, "babelfin", "backups"), "settings-version999-*.xml");
    if (futureBackups.Length != 1 || File.ReadAllText(futureBackups[0]) != futureXml)
        throw new InvalidOperationException("startup did not preserve the full newer-format XML");
    var replacement = (MediaBrowser.Model.Plugins.BasePluginConfiguration)Activator.CreateInstance(cfg.GetType())!;
    replacement.GetType().GetProperty("ConfigVersion")!.SetValue(replacement, currentVersion);
    futurePlugin.UpdateConfiguration(replacement);
    if (!File.ReadAllText(futurePath).Contains(versionTag, StringComparison.Ordinal)
        || Directory.GetFiles(Path.Combine(futureRoot, "babelfin", "backups"), "settings-version999-*.xml").Any(file => File.ReadAllText(file) != futureXml))
        throw new InvalidOperationException("explicit save lost the preserved newer-format XML");
    Console.WriteLine("PASS: newer configuration retained byte-for-byte on startup; explicit save preserves an original backup");

    // JIT every method so any member reference that doesn't exist in Jellyfin 12.2 fails here, not on the server.
    var jitted = 0;
    var jitFailures = new List<string>();
    foreach (var t in types.Where(t => !t.ContainsGenericParameters))
    {
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(t.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null))
        {
            try
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
                jitted++;
            }
            catch (Exception ex)
            {
                jitFailures.Add($"{t.FullName}.{m.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    if (jitFailures.Count > 0) throw new InvalidOperationException("JIT failed (member missing in Jellyfin 12.2?):\n  " + string.Join("\n  ", jitFailures));
    Console.WriteLine($"JIT of every plugin method OK ({jitted} methods and constructors)");
    Console.WriteLine("BIND TEST PASS");
    return 0;
}

// Adapted from Jellyfin Project contributors' PluginLoadContext.cs; loader body retained.
// https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/Emby.Server.Implementations/Plugins/PluginLoadContext.cs
// Babelfin modifications (2026): local sealed fixture visibility; see NOTICE for licensing/provenance.
/// <summary>Jellyfin's plugin-loading policy used by the binding fixture.</summary>
sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string path) : base(true)
    {
        _resolver = new AssemblyDependencyResolver(path);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }
}

/// <summary>A library manager with no libraries, no collection folders and no events; GetItemById finds nothing.</summary>
public class EmptyLibraryManager : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(void)) return null;
        if (returnType.IsGenericType && (returnType.GetGenericTypeDefinition() == typeof(List<>) || returnType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) || returnType.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(returnType.GetGenericArguments()[0]));
        }

        if (returnType.IsArray) return Array.CreateInstance(returnType.GetElementType()!, 0);
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

/// <summary>A collection manager where nothing is in any collection.</summary>
public class EmptyCollectionManager : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        if (name == nameof(ICollectionManager.GetCollectionsContainingItem)) return Enumerable.Empty<MediaBrowser.Controller.Entities.Movies.BoxSet>();
        throw new NotSupportedException("EmptyCollectionManager." + name);
    }
}

/// <summary>Jellyfin's media source manager reduced to one call: a dual-audio source with defaults 1/3.</summary>
sealed class DummyMediaSourceManagerFactory
{
    public IMediaSourceManager Create() => DispatchProxy.Create<IMediaSourceManager, DummyMediaSourceManager>();
}

public class DummyMediaSourceManager : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name != nameof(IMediaSourceManager.GetStaticMediaSources)) throw new NotSupportedException(targetMethod.Name);
        MediaStream S(int i, MediaStreamType type, string lang, bool isDefault, bool forced = false) => new() { Index = i, Type = type, Language = lang, IsDefault = isDefault, IsForced = forced };
        var source = new MediaSourceInfo
        {
            Id = "bind",
            DefaultAudioStreamIndex = 1,
            DefaultSubtitleStreamIndex = 3,
            MediaStreams = new List<MediaStream>
            {
                S(1, MediaStreamType.Audio, "eng", true), S(2, MediaStreamType.Audio, "jpn", false),
                S(3, MediaStreamType.Subtitle, "eng", true, true), S(4, MediaStreamType.Subtitle, "eng", false),
            },
        };
        return (IReadOnlyList<MediaSourceInfo>)new List<MediaSourceInfo> { source };
    }
}

sealed class FakePaths : IApplicationPaths
{
    public FakePaths(string r) { ProgramDataPath = r; }
    public string ProgramDataPath { get; }
    public string WebPath => ProgramDataPath;
    public string ProgramSystemPath => ProgramDataPath;
    public string DataPath => ProgramDataPath;
    public string ImageCachePath => ProgramDataPath;
    public string PluginsPath => Path.Combine(ProgramDataPath, "plugins");
    public string PluginConfigurationsPath => Path.Combine(ProgramDataPath, "cfg");
    public string LogDirectoryPath => ProgramDataPath;
    public string ConfigurationDirectoryPath => ProgramDataPath;
    public string SystemConfigurationFilePath => ProgramDataPath;
    public string CachePath => ProgramDataPath;
    public string TempDirectory => ProgramDataPath;
    public string VirtualDataPath => ProgramDataPath;
    public string TrickplayPath => ProgramDataPath;
    public string BackupPath => ProgramDataPath;
    public void MakeSanityCheckOrThrow() { }
    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) { }
}

sealed class FakeXml : IXmlSerializer
{
    public object DeserializeFromStream(Type type, Stream stream) => new System.Xml.Serialization.XmlSerializer(type).Deserialize(stream)!;
    public void SerializeToStream(object obj, Stream stream) => new System.Xml.Serialization.XmlSerializer(obj.GetType()).Serialize(stream, obj);
    public void SerializeToFile(object obj, string file) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); using var f = File.Create(file); SerializeToStream(obj, f); }
    public object DeserializeFromFile(Type type, string file) { using var f = File.OpenRead(file); return DeserializeFromStream(type, f); }
    public object DeserializeFromBytes(Type type, byte[] buffer) { using var m = new MemoryStream(buffer); return DeserializeFromStream(type, m); }
}
