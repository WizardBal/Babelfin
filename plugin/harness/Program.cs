// Babelfin integration test host. Runs the plugin's DI wrapper, IMediaSourceManager decorator, index.html middleware,
// controller and PlaybackInfo action filter the way Jellyfin 12.2 wires them, with fake server services and the
// a self-contained companion middleware fixture. Usage: dotnet run -c Release -- <outdir>
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Seeding;
using Jellyfin.Plugin.Babelfin.Settings;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using Jellyfin.Plugin.Babelfin.Web;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using BfHostSettings = Jellyfin.Plugin.Babelfin.Web.HostSettings;
using BfPlugin = Jellyfin.Plugin.Babelfin.Plugin;
using BfRegistrator = Jellyfin.Plugin.Babelfin.Web.ServiceRegistrator;

var outDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "bf-harness-out");
if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
Directory.CreateDirectory(outDir);
var webDir = Path.Combine(outDir, "web");
Directory.CreateDirectory(webDir);
var indexSource = Environment.GetEnvironmentVariable("BF_INDEX") ?? Path.Combine(AppContext.BaseDirectory, "index.html");
var originalIndex = File.ReadAllText(indexSource);
File.WriteAllText(Path.Combine(webDir, "index.html"), originalIndex);
var manifestBytes = Encoding.UTF8.GetBytes("{\"name\":\"Jellyfin\",\"short_name\":\"Jellyfin\",\"start_url\":\"index.html#/home.html\"}");
File.WriteAllBytes(Path.Combine(webDir, "manifest.json"), manifestBytes);
var otherHtml = "<!DOCTYPE html><html><head><title>other</title></head><body><p>Another page</p></body></html>";
File.WriteAllText(Path.Combine(webDir, "other.html"), otherHtml);

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
void Section(string title) => Console.WriteLine("-- " + title + " --");
void Save(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(outDir, name), bytes);

var paths = new FakePaths(Path.Combine(outDir, "babelfin-data"), webDir);
var plugin = new BfPlugin(paths, new FakeXml());
Console.WriteLine($"plugin: {plugin.Name} {plugin.Id} version={BfPlugin.VersionString}");
Console.WriteLine("server assemblies: MediaBrowser.Controller " + typeof(IMediaSourceManager).Assembly.GetName().Version
    + ", plugin compiled against " + typeof(BfPlugin).Assembly.GetReferencedAssemblies().First(a => a.Name == "MediaBrowser.Controller").Version);
// Admin everyday default standing in for the retired probe: audio = a language then the file default; subtitles = full in one language
// (whether or not the audio is understood), or off. People are seeded from the fake server's metadata language ("en", assumed).
void SetConfig(string? audio = null, string? subtitles = null, bool enabled = true, bool dryRun = false, bool replace = false, Action<PluginConfiguration>? more = null)
{
    var cfg = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = enabled, DryRun = dryRun, UnknownAppMode = replace ? AppModeKind.ReplaceUnlessManual : AppModeKind.Trust };
    if (audio is not null) cfg.Everyday.AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, audio), new AudioStepSetting(AudioStepKind.FileDefault) };
    if (subtitles == "off") { cfg.Everyday.WhenAudioUnderstood = SubtitleWant.Off; cfg.Everyday.WhenAudioNotUnderstood = SubtitleWant.Off; }
    else if (subtitles is not null) { cfg.Everyday.SubtitleLanguages = new[] { subtitles }; cfg.Everyday.WhenAudioUnderstood = SubtitleWant.Full; cfg.Everyday.WhenAudioNotUnderstood = SubtitleWant.Full; }
    more?.Invoke(cfg);
    plugin.UpdateConfiguration(cfg);
}
SetConfig();
string IdxOf(MediaSourceInfo s) => $"{s.DefaultAudioStreamIndex}/{s.DefaultSubtitleStreamIndex}";
bool NoPaths(string text) => !Regex.IsMatch(text, "/media/|\\.mkv|\"path\"", RegexOptions.IgnoreCase);
var dataFolder = Path.Combine(outDir, "babelfin-data", "babelfin");
var usersFolder = Path.Combine(dataFolder, "users");

// ── Fixtures ──
var store = new FixtureStore();
var viewerOne = new User("ViewerOne", "Default", "Default") { Id = Guid.Parse("11111111111141118111111111111111") };
var viewerTwo = new User("viewerTwo", "Default", "Default") { Id = Guid.NewGuid() };
var viewerThree = new User("viewerThree", "Default", "Default") { Id = Guid.NewGuid() };
var users = DispatchProxy.Create<IUserManager, FakeUserManager>();
foreach (var u in new[] { viewerOne, viewerTwo, viewerThree })
{
    // Jellyfin's user manager grants the default permissions; the entity alone has none. Libraries are visible only with this one.
    u.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.EnableAllFolders, true));
}

((FakeUserManager)(object)users).Users.AddRange(new[] { viewerOne, viewerTwo, viewerThree });
var library = DispatchProxy.Create<ILibraryManager, FakeLibraryManager>();
var lib = (FakeLibraryManager)(object)library;
BaseItem.LibraryManager = library; // parents, inherited original language and top-level folders resolve through the fake, as on the server
BaseItem.Logger = NullLogger<BaseItem>.Instance;
CollectionFolder.XmlSerializer = new FakeXml(); // CollectionFolder.IsVisible reads the library's options.xml (absent here → defaults)
Video.RecordingsManager = DispatchProxy.Create<MediaBrowser.Controller.LiveTv.IRecordingsManager, FakeRecordingsManager>(); // Video.SourceType (read by item visibility) asks whether the file is being recorded
var collections = DispatchProxy.Create<ICollectionManager, FakeCollectionManager>();
var serverConfig = DispatchProxy.Create<IServerConfigurationManager, FakeServerConfiguration>();
var localization = DispatchProxy.Create<ILocalizationManager, FakeLocalization>();

// Libraries: "TV Shows" (plain), "Anime" (matches the starter word list by name), "Cartoons" (no name match; every title has the genre Anime), "Movies".
var tvFolder = lib.AddFolder("TV Shows");
var animeFolder = lib.AddFolder("Anime");
var cartoonsFolder = lib.AddFolder("Cartoons");
var moviesFolder = lib.AddFolder("Movies");
var tvSeries = lib.AddSeries(tvFolder, "Skyward Lantern Voyage", "ja", "Animation", "Action");
var animeSeries = lib.AddSeries(animeFolder, "Skyward Lantern Voyage", "ja", "Animation", "Action");
var cartoonSeries = lib.AddSeries(cartoonsFolder, "Some Cartoon", "ja", "Anime");
var tvSeason = lib.AddSeason(tvSeries, 0);
var animeSeason = lib.AddSeason(animeSeries, 0);

var slvId = Guid.Parse("8d448a0e728155ef8a2a076bdc2632a5");
var slv = lib.AddEpisode(tvSeason, slvId, "Skyward Lantern Voyage (2015) - S00E01");
const string SlvPath = "/media/TV Shows/Skyward Lantern Voyage (2015)/Season 00/Skyward Lantern Voyage (2015) - S00E01.mkv";
store.Add(slv, Fx.Source(slvId.ToString("N"), SlvPath, Fx.SlvStreams()));

// The same streams inside the Anime library: an original-audio fixture (Japanese audio, full English subtitles).
var animeEpId = Guid.NewGuid();
var animeEp = lib.AddEpisode(animeSeason, animeEpId, "Skyward Lantern Voyage (2015) - S00E01");
store.Add(animeEp, Fx.Source(animeEpId.ToString("N"), "/media/Anime/Skyward Lantern Voyage (2015)/Season 00/Skyward Lantern Voyage (2015) - S00E01.mkv", Fx.SlvStreams()));

var multiId = Guid.Parse("80de6334170a5fc5af65b1485e6b85e1");
var multi = new Movie { Id = multiId, Name = "Two Versions (2025)", ParentId = moviesFolder.Id };
const string MultiA = "80de6334170a5fc5af65b1485e6b85e1", MultiB = "5fc02e4cfb8d549ca983a77225856909";
store.Add(multi,
    Fx.Source(MultiA, "/media/Movies/Two Versions (2025)/Two Versions (2025) - 1080p.mkv", Fx.SlvStreams()),
    Fx.Source(MultiB, "/media/Movies/Two Versions (2025)/Two Versions (2025) - 2160p.mkv", new List<MediaStream>
    {
        Fx.Video(0), Fx.Audio(1, "eng", true), Fx.Sub(2, "eng", true, true), Fx.Sub(3, "eng", false, false),
    }));
lib.Add(multi);

var songId = Guid.NewGuid();
var song = new MediaBrowser.Controller.Entities.Audio.Audio { Id = songId, Name = "A Song" };
store.Add(song, Fx.Source(songId.ToString("N"), "/media/Music/song.flac", new List<MediaStream> { Fx.Audio(0, "jpn", false), Fx.Audio(1, "eng", true) }));
lib.Add(song);

var hiddenId = Guid.NewGuid();
var hidden = new Movie { Id = hiddenId, Name = "Hidden Movie", ParentId = moviesFolder.Id };
store.Add(hidden, Fx.Source(hiddenId.ToString("N"), "/media/Workout/hidden.mkv", Fx.SlvStreams()));
lib.Add(hidden);
lib.Hidden.Add((hiddenId, viewerOne.Id));

var fakeHost = new FakeAppHost(new FakeConfigManager(new FakeNetworkConfig()), false);

// ════════════════════════ A. DI wrapping ════════════════════════
Section("A. DI wrapping");
ServiceCollection CoreServices(Action<ServiceCollection>? core, LogCapture? capture = null)
{
    var services = new ServiceCollection();
    services.AddSingleton(store);
    services.AddLogging(b => { if (capture is not null) b.AddProvider(capture); });
    services.AddSingleton<ILocalizationManager>(localization);
    services.AddSingleton<ILibraryManager>(library);
    services.AddSingleton<IUserManager>(users);
    // As on the server: the collection manager's graph (ProviderManager → SubtitleManager) needs IMediaSourceManager.
    // Resolving the decider inside the media source manager's factory is therefore a cycle; resolving it would deadlock startup.
    services.AddSingleton<ICollectionManager>(sp => { _ = sp.GetRequiredService<IMediaSourceManager>(); return collections; });
    services.AddSingleton<IServerConfigurationManager>(serverConfig);
    services.AddSingleton<IUserDataManager>(DispatchProxy.Create<IUserDataManager, FakeUserDataManager>());
    core?.Invoke(services);
    return services;
}

foreach (var kind in new[] { "type", "factory", "instance" })
{
    var instance = new FakeInnerMediaSourceManager(store);
    var services = CoreServices(s =>
    {
        if (kind == "type") s.AddSingleton<IMediaSourceManager, FakeInnerMediaSourceManager>();
        else if (kind == "factory") s.AddSingleton<IMediaSourceManager>(sp => new FakeInnerMediaSourceManager(sp.GetRequiredService<FixtureStore>()));
        else s.AddSingleton<IMediaSourceManager>(instance);
    });
    new BfRegistrator().RegisterServices(services, fakeHost);
    var descriptors = services.Count(d => d.ServiceType == typeof(IMediaSourceManager));
    using var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IMediaSourceManager>();
    var dec = resolved as BabelfinMediaSourceManager;
    var stats = sp.GetRequiredService<DecoratorStats>();
    var all = sp.GetServices<IMediaSourceManager>().ToList();
    Check(descriptors == 1, $"[{kind}] exactly one IMediaSourceManager descriptor remains ({descriptors})");
    Check(dec is not null && dec.Inner is FakeInnerMediaSourceManager && (kind != "instance" || ReferenceEquals(dec.Inner, instance)),
        $"[{kind}] resolves to BabelfinMediaSourceManager wrapping the fake ({resolved.GetType().Name} -> {dec?.Inner.GetType().Name})");
    Check(all.Count == 1 && ReferenceEquals(all[0], resolved), $"[{kind}] IEnumerable<IMediaSourceManager> yields one element ({all.Count})");
    Check(ReferenceEquals(resolved, sp.GetRequiredService<IMediaSourceManager>()), $"[{kind}] singleton kept (same instance twice)");
    var lazy = typeof(BabelfinMediaSourceManager).GetField("_decider", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dec) as LazyTrackDecider;
    Check(lazy is { IsResolved: false }, $"[{kind}] the decider is not created with the manager (its graph needs IMediaSourceManager: an eager startup dependency cycle)");
    var collectionsResolved = sp.GetRequiredService<ICollectionManager>();
    Check(collectionsResolved is not null && lazy is { IsResolved: false }, $"[{kind}] a service whose graph needs IMediaSourceManager resolves without a cycle");
    Check(stats.Wrapped && stats.InnerTypeName.Contains(nameof(FakeInnerMediaSourceManager)), $"[{kind}] DecoratorStats.Wrapped, InnerTypeName={stats.InnerTypeName}");
}

{
    // Another plugin replaced the core registration first; Babelfin wraps the other plugin's decorator.
    var services = CoreServices(s => s.AddSingleton<IMediaSourceManager, FakeInnerMediaSourceManager>());
    OtherPlugin.Wrap(services);
    new BfRegistrator().RegisterServices(services, fakeHost);
    using var sp = services.BuildServiceProvider();
    var dec = sp.GetRequiredService<IMediaSourceManager>() as BabelfinMediaSourceManager;
    var other = (object?)dec?.Inner as OtherPluginDecorator;
    var inner = other?.Inner as FakeInnerMediaSourceManager;
    var sources = dec?.GetStaticMediaSources(slv, true, viewerOne);
    Check(services.Count(d => d.ServiceType == typeof(IMediaSourceManager)) == 1 && dec is not null && other is not null && inner is not null,
        "chain of three: Babelfin -> OtherPluginDecorator -> FakeInner, one descriptor");
    Check(sources?.Count == 1 && other!.StaticCalls == 1 && inner!.Calls.Any(c => c.Name == "GetStaticMediaSources"), "calls go through every link of the chain");
    var innerName = sp.GetRequiredService<DecoratorStats>().InnerTypeName;
    Check(other is not null && innerName == dec!.Inner.GetType().FullName && innerName != typeof(FakeInnerMediaSourceManager).FullName,
        $"InnerTypeName names the other plugin's decorator, not the core one ({innerName})");
}

{
    var capture = new LogCapture();
    var services = CoreServices(null, capture);
    Exception? thrown = null;
    try { new BfRegistrator().RegisterServices(services, fakeHost); } catch (Exception ex) { thrown = ex; }
    using var sp = services.BuildServiceProvider();
    var stats = sp.GetRequiredService<DecoratorStats>();
    // The warning is written when the web host applies the startup filter (logging isn't up during RegisterServices).
    var filter = sp.GetServices<IStartupFilter>().OfType<BabelfinStartupFilter>().Single();
    filter.Configure(_ => { })(new Microsoft.AspNetCore.Builder.ApplicationBuilder(sp));
    Check(thrown is null && !stats.Wrapped && services.All(d => d.ServiceType != typeof(IMediaSourceManager)), "no IMediaSourceManager registered: no throw, Wrapped=false, nothing added");
    Check(capture.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("No IMediaSourceManager")), "no registration: a warning is logged (" + capture.Entries.FirstOrDefault(e => e.Level == LogLevel.Warning).Message + ")");
}

{
    var services = CoreServices(s => s.AddSingleton<IMediaSourceManager, FakeInnerMediaSourceManager>());
    services.AddTransient<IStartupFilter, CompanionFixtureStartupFilter>();
    var before = services.Count(d => d.ServiceType == typeof(IStartupFilter));
    new BfRegistrator().RegisterServices(services, fakeHost);
    Check(before == 1 && services[0].ServiceType == typeof(IStartupFilter) && services[0].ImplementationType == typeof(BabelfinStartupFilter),
        "startup filter at index 0, ahead of the companion middleware filter registered first");
    using var sp = services.BuildServiceProvider();
    var filters = sp.GetServices<IStartupFilter>().ToList();
    Check(filters.Count == 2 && filters[0] is BabelfinStartupFilter, "resolved order: Babelfin's filter first (outermost), then " + filters.Last().GetType().Name);
}

{
    var services = CoreServices(s => s.AddSingleton<IMediaSourceManager, FakeInnerMediaSourceManager>());
    new BfRegistrator().RegisterServices(services, fakeHost);
    var sp = services.BuildServiceProvider();
    var inner = (FakeInnerMediaSourceManager)((BabelfinMediaSourceManager)sp.GetRequiredService<IMediaSourceManager>()).Inner;
    var beforeDispose = inner.DisposeCount;
    sp.Dispose();
    Check(beforeDispose == 0 && inner.DisposeCount == 1, $"disposing the provider disposes the wrapped inner once ({inner.DisposeCount})");
}

// ════════════════════════ B. Decorator effect ════════════════════════
Section("B. Decorator effect (engine decider)");
ServiceProvider BuildDecorated(Action<IServiceCollection>? after = null)
{
    var services = CoreServices(s => s.AddSingleton<IMediaSourceManager, FakeInnerMediaSourceManager>());
    new BfRegistrator().RegisterServices(services, fakeHost);
    after?.Invoke(services);
    return services.BuildServiceProvider();
}

using (var sp = BuildDecorated())
{
    var msm = (BabelfinMediaSourceManager)sp.GetRequiredService<IMediaSourceManager>();
    var fake = (FakeInnerMediaSourceManager)msm.Inner;
    var stats = sp.GetRequiredService<DecoratorStats>();
    string Idx(MediaSourceInfo s) => $"{s.DefaultAudioStreamIndex}/{s.DefaultSubtitleStreamIndex}";

    SetConfig();
    var s7 = msm.GetStaticMediaSources(slv, true, viewerOne);
    Check(Idx(s7[0]) == "1/3" && stats.Applied == 0 && stats.PassedThrough == 1, $"nothing chosen anywhere: Babelfin has nothing to say and Jellyfin's own 1/3 stays ({Idx(s7[0])}), PassedThrough={stats.PassedThrough}");

    SetConfig("jpn", "eng");
    var s8 = msm.GetStaticMediaSources(slv, true, viewerOne);
    var d8 = stats.GetDecision(viewerOne.Id, slvId, s8[0].Id);
    Check(Idx(s8[0]) == "2/4" && stats.Applied == 1, $"admin everyday jpn / full eng: 2/4 ({Idx(s8[0])}), Applied={stats.Applied}");
    Check(d8?.Decision is not null && d8.Outcome == DecisionOutcome.Applied && d8.JellyfinAudioIndex == 1 && d8.JellyfinSubtitleIndex == 3
        && d8.Decision.Trace.Any(t => t.Contains("#4") && t.Contains("full")), "decision cached with a trace naming the selected full-dialogue track: " + string.Join(" | ", d8?.Decision?.Trace ?? Array.Empty<string>()));

    var p9 = await msm.GetPlaybackMediaSources(slv, viewerOne, true, true, CancellationToken.None);
    var p9null = await msm.GetPlaybackMediaSources(slv, null!, true, true, CancellationToken.None);
    var s9null = msm.GetStaticMediaSources(slv, true, null);
    Check(Idx(p9[0]) == "2/4" && fake.Calls.Any(c => c.Name == "GetPlaybackMediaSources") && stats.PlaybackCalls == 1, $"GetPlaybackMediaSources with user: 2/4 ({Idx(p9[0])})");
    Check(Idx(p9null[0]) == "1/3" && Idx(s9null[0]) == "1/3", $"user == null: untouched (playback {Idx(p9null[0])}, static {Idx(s9null[0])})");

    SetConfig("ja", "off");
    var s10 = msm.GetStaticMediaSources(slv, true, viewerOne);
    Check(Idx(s10[0]) == "2/-1", $"admin chain 'ja' matches the 'jpn' track through the language table; subtitles off -> -1 ({Idx(s10[0])})");

    SetConfig("jpn", "eng", dryRun: true);
    var dryBefore = stats.DryRun;
    var s11 = msm.GetStaticMediaSources(slv, true, viewerTwo);
    var d11 = stats.GetDecision(viewerTwo.Id, slvId, s11[0].Id);
    Check(Idx(s11[0]) == "1/3" && stats.DryRun == dryBefore + 1 && d11?.Outcome == DecisionOutcome.DryRun && d11.Decision?.AudioIndex == 2 && d11.Decision?.SubtitleIndex == 4,
        $"DryRun: indices unchanged ({Idx(s11[0])}), DryRun counter {dryBefore}->{stats.DryRun}, would-be 2/4 cached");

    SetConfig("jpn", "eng", enabled: false);
    var applied12 = stats.Applied; var dry12 = stats.DryRun;
    var s12 = msm.GetStaticMediaSources(slv, true, viewerThree);
    var d12 = stats.GetDecision(viewerThree.Id, slvId, s12[0].Id);
    Check(Idx(s12[0]) == "1/3" && d12?.Decision is null && stats.Applied == applied12 && stats.DryRun == dry12,
        $"Enabled off: unchanged ({Idx(s12[0])}), no decision recorded (record outcome: {d12?.Outcome.ToString() ?? "none"})");

    SetConfig("jpn", "eng");
    var s14 = msm.GetStaticMediaSources(multi, true, viewerOne);
    var a14 = s14.First(s => s.Id == MultiA); var b14 = s14.First(s => s.Id == MultiB);
    Check(s14.Count == 2 && Idx(a14) == "2/4" && Idx(b14) == "1/3", $"multi-version: each source decided separately (A {Idx(a14)}, B {Idx(b14)}: no Japanese track so the file default, forced #2 skipped for the full eng #3)");

    var fresh = store.Clone(slvId)[0];
    fresh.DefaultAudioStreamIndex = null; fresh.DefaultSubtitleStreamIndex = null;
    var setBefore = stats.SetDefaultsCalls;
    msm.SetDefaultAudioAndSubtitleStreamIndices(slv, fresh, viewerOne);
    Check(Idx(fresh) == "2/4" && stats.SetDefaultsCalls == setBefore + 1, $"SetDefaultAudioAndSubtitleStreamIndices directly: applied ({Idx(fresh)})");
    var songSources = msm.GetStaticMediaSources(song, true, viewerOne);
    var songInner = fake.GetStaticMediaSources(song, true, viewerOne);
    Check(Idx(songSources[0]) == Idx(songInner[0]) && songSources[0].DefaultAudioStreamIndex == 1, $"non-video item (Audio): untouched ({Idx(songSources[0])})");

    // Forwarding of every other member
    var g = Guid.NewGuid();
    var streams = msm.GetMediaStreams(g);
    Check(fake.Last("GetMediaStreams") is { } c1 && Equals(c1.Args[0], g) && ReferenceEquals(streams, fake.StreamsResult), "forwards GetMediaStreams(Guid) with the same id and returns the inner result");
    var attachments = msm.GetMediaAttachments(g);
    Check(fake.Last("GetMediaAttachments") is { } c2 && Equals(c2.Args[0], g) && ReferenceEquals(attachments, fake.AttachmentsResult), "forwards GetMediaAttachments(Guid)");
    var direct = msm.SupportsDirectStream("/media/x.mkv", MediaProtocol.File);
    Check(direct && fake.Last("SupportsDirectStream") is { } c3 && (string)c3.Args[0]! == "/media/x.mkv" && (MediaProtocol)c3.Args[1]! == MediaProtocol.File, "forwards SupportsDirectStream(path, protocol)");
    var protocol = msm.GetPathProtocol("http://example/x.mkv");
    Check(protocol == MediaProtocol.Http && fake.Last("GetPathProtocol") is { } c4 && (string)c4.Args[0]! == "http://example/x.mkv", "forwards GetPathProtocol(path)");
    var providers = new List<IMediaSourceProvider>();
    msm.AddParts(providers);
    Check(fake.Last("AddParts") is { } c5 && ReferenceEquals(c5.Args[0], providers), "forwards AddParts(providers)");
    await msm.CloseLiveStream("live-42");
    Check(fake.Last("CloseLiveStream") is { } c6 && (string)c6.Args[0]! == "live-42", "forwards CloseLiveStream(id)");

    var map = typeof(BabelfinMediaSourceManager).GetInterfaceMap(typeof(IMediaSourceManager));
    var interfaceMembers = typeof(IMediaSourceManager).GetMembers().Length;
    var missing = map.InterfaceMethods.Where((m, i) => map.TargetMethods[i].DeclaringType != typeof(BabelfinMediaSourceManager) || !map.TargetMethods[i].IsPublic).Select(m => m.Name).ToList();
    Check(interfaceMembers == map.InterfaceMethods.Length && missing.Count == 0,
        $"decorator declares a public method for all {map.InterfaceMethods.Length} IMediaSourceManager members ({interfaceMembers} members by reflection){(missing.Count > 0 ? " missing: " + string.Join(", ", missing) : string.Empty)}");
}

using (var sp = BuildDecorated(s => s.AddSingleton<ITrackDecider, ThrowingDecider>()))
{
    SetConfig("jpn", "eng");
    var msm = sp.GetRequiredService<IMediaSourceManager>();
    var stats = sp.GetRequiredService<DecoratorStats>();
    Exception? escaped = null;
    IReadOnlyList<MediaSourceInfo>? sources = null;
    try { sources = msm.GetStaticMediaSources(slv, true, viewerOne); } catch (Exception ex) { escaped = ex; }
    Check(escaped is null && sources is not null && sources[0].DefaultAudioStreamIndex == 1 && sources[0].DefaultSubtitleStreamIndex == 3 && stats.Errors == 1,
        $"throwing decider: indices unchanged, Errors={stats.Errors}, no exception escapes{(escaped is null ? string.Empty : " (" + escaped.GetType().Name + ")")}");
}

// ════════════════════════ C. Kestrel pipeline ════════════════════════
Section("C. Kestrel pipeline");
Console.WriteLine("Companion middleware fixture: independent script injection, compression and caching");

async Task<TestHost> StartAsync(string variant, string baseUrl = "", bool requireHttps = false, bool listenHttps = false, bool hostWebClient = true, bool jellyfinJson = false, bool caseInsensitiveReads = false)
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Configuration["hostwebclient"] = hostWebClient ? "True" : "False";
    var host = new FakeAppHost(new FakeConfigManager(new FakeNetworkConfig { BaseUrl = baseUrl, RequireHttps = requireHttps }), listenHttps);

    // Core services first, as Jellyfin registers them before calling plugins.
    builder.Services.AddSingleton(store);
    builder.Services.AddSingleton<IMediaSourceManager, FakeInnerMediaSourceManager>();
    builder.Services.AddSingleton<ILibraryManager>(library);
    builder.Services.AddSingleton<IUserManager>(users);
    builder.Services.AddSingleton<IPluginManager>(DispatchProxy.Create<IPluginManager, FakePluginManager>());
    builder.Services.AddSingleton<ILocalizationManager>(localization);
    builder.Services.AddSingleton<ICollectionManager>(sp => { _ = sp.GetRequiredService<IMediaSourceManager>(); return collections; });
    builder.Services.AddSingleton<IServerConfigurationManager>(serverConfig);
    builder.Services.AddSingleton<IUserDataManager>(DispatchProxy.Create<IUserDataManager, FakeUserDataManager>());

    void RegisterCompanion()
    {
        CompanionFixture.Register(builder.Services, host, webDir);
    }

    if (variant == "companion-before") RegisterCompanion();
    new BfRegistrator().RegisterServices(builder.Services, host);
    PlaybackChecks.Register(builder.Services); // a fake ISessionManager and the client/device claims for group H
    if (variant == "companion-after") RegisterCompanion();

    builder.Services.AddControllers()
        .AddApplicationPart(typeof(BfPlugin).Assembly).AddApplicationPart(typeof(MediaInfoController).Assembly)
        .AddJsonOptions(o =>
        {
            if (jellyfinJson)
            {
                // What Jellyfin's AddJellyfinApi does: everything JsonDefaults.PascalCaseOptions sets (names as the C# properties spell them, reads that
                // match the case exactly, enums as names, nulls omitted, Jellyfin's converters).
                var jellyfin = Jellyfin.Extensions.Json.JsonDefaults.PascalCaseOptions;
                o.JsonSerializerOptions.ReadCommentHandling = jellyfin.ReadCommentHandling;
                o.JsonSerializerOptions.WriteIndented = jellyfin.WriteIndented;
                o.JsonSerializerOptions.DefaultIgnoreCondition = jellyfin.DefaultIgnoreCondition;
                o.JsonSerializerOptions.NumberHandling = jellyfin.NumberHandling;
                o.JsonSerializerOptions.PropertyNameCaseInsensitive = jellyfin.PropertyNameCaseInsensitive;
                o.JsonSerializerOptions.PropertyNamingPolicy = jellyfin.PropertyNamingPolicy;
                o.JsonSerializerOptions.Converters.Clear();
                foreach (var converter in jellyfin.Converters) o.JsonSerializerOptions.Converters.Add(converter);
                if (caseInsensitiveReads) o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
                return;
            }

            // The parts of Jellyfin's JsonDefaults that matter here: camelCase, enums as names, nulls omitted, case-insensitive reads.
            o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
    builder.Services.AddAuthentication("Fake").AddScheme<AuthenticationSchemeOptions, FakeAuth>("Fake", _ => { });
    builder.Services.AddAuthorization(o => o.AddPolicy("RequiresElevation", p => p.RequireClaim("FakeAdmin", "1")));
    var app = builder.Build();
    var prefix = BfHostSettings.Normalize(baseUrl);
    app.Map(prefix, main =>
    {
        main.UseDeveloperExceptionPage(); // a 500 then carries its stack trace into the saved response body
        var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webDir);
        main.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = "/web" });
        main.UseStaticFiles(new StaticFileOptions { FileProvider = files, RequestPath = "/web" });
        main.UseRouting();
        main.UseAuthentication();
        main.UseAuthorization();
        main.UseEndpoints(e => e.MapControllers());
    });
    await app.StartAsync();
    var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.First()) };
    return new TestHost(app, http, app.Services.GetRequiredService<DecoratorStats>());
}

// Fixture resets are not account deletions. Preserve the fixture file and evict only test-host state.
// The public store deliberately has no reset-to-missing API: deleted accounts must stay tombstoned.
void ResetPersonFixture(IPersonStore store, Guid id)
{
    if (store is not PersonStore concrete) throw new InvalidOperationException("Expected the isolated harness store");
    var path = Path.GetFullPath(concrete.FilePath(id));
    var sandbox = Path.GetFullPath(outDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!path.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture path escaped the harness output directory");
    store.Save(new PersonSettings { UserId = id }); // invalidate resolver caches before clearing the fixture
    if (File.Exists(path)) File.Move(path, path + ".fixture-reset-" + Guid.NewGuid().ToString("N"));
    foreach (var name in new[] { "_cache", "_fallback", "_newerSchema", "_corruptInPlace" })
    {
        var field = typeof(PersonStore).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Store fixture field changed: " + name);
        ((System.Collections.IDictionary)field.GetValue(concrete)!).Remove(id);
    }
}

int responseSeq = 0;
async Task<Resp> Send(TestHost h, HttpMethod method, string url, Dictionary<string, string>? headers = null, string? jsonBody = null)
{
    var m = new HttpRequestMessage(method, url);
    foreach (var (k, v) in headers ?? new()) m.Headers.TryAddWithoutValidation(k, v);
    if (jsonBody is not null) m.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
    var res = await h.Http.SendAsync(m);
    var bytes = await res.Content.ReadAsByteArrayAsync();
    var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (k, v) in res.Headers) all[k] = string.Join(", ", v);
    foreach (var (k, v) in res.Content.Headers) all[k] = string.Join(", ", v);
    // Every response body examined is kept: responses/NNN-METHOD-path-status.body
    var seq = Interlocked.Increment(ref responseSeq);
    var safe = Regex.Replace(url, "[^A-Za-z0-9._-]+", "_").Trim('_');
    Directory.CreateDirectory(Path.Combine(outDir, "responses"));
    File.WriteAllBytes(Path.Combine(outDir, "responses", $"{seq:000}-{method.Method}-{(safe.Length > 80 ? safe[..80] : safe)}-{(int)res.StatusCode}.body"), bytes);
    return new Resp((int)res.StatusCode, all, bytes);
}

const string Tag = "<script src=\"/Babelfin/client.js?v=0.20.0.0\" defer></script>";
int Count(string text, string what) => Regex.Matches(text, Regex.Escape(what)).Count;

foreach (var variant in new[] { "plain", "companion-after", "companion-before" })
{
    Section($"C. variant {variant}");
    var h = await StartAsync(variant);
    var withCompanion = variant != "plain";
    string? identity = null;
    string? etag = null;
    foreach (var url in new[] { "/web/", "/web/index.html" })
    {
        var r = await Send(h, HttpMethod.Get, url);
        var body = r.Text;
        Save($"{variant}{url.Replace('/', '_')}.html", r.Body);
        var ok = r.Status == 200 && Count(body, Tag) == 1 && Regex.IsMatch(body, Regex.Escape(Tag) + @"\s*</body>\s*</html>\s*$")
            && r.H("Cache-Control") == "no-cache" && r.H("ETag").StartsWith("\"bf-") && !r.Headers.ContainsKey("Content-Encoding") && body.StartsWith("<!DOCTYPE");
        Check(ok, $"[{variant}] GET {url}: 200, one Babelfin tag right before </body>, no-cache, ETag {r.H("ETag")}");
        if (withCompanion)
        {
            Check(body.Contains("<!-- Companion -->") && body.Contains("companion/boot.js") && body.IndexOf("<!-- Companion -->") < body.IndexOf("</head>"),
                $"[{variant}] GET {url}: Companion's tags also present (both plugins survive)");
        }

        identity ??= body;
        etag ??= r.H("ETag");
    }

    var companionReply = await Send(h, HttpMethod.Get, "/web/", new() { ["If-None-Match"] = "\"companion-abc123-1\"" });
    Check(companionReply.Status == 200 && Count(companionReply.Text, Tag) == 1, $"[{variant}] If-None-Match with Companion's old ETag -> 200 with Babelfin's tag ({companionReply.Status})");
    var notModified = await Send(h, HttpMethod.Get, "/web/", new() { ["If-None-Match"] = etag! });
    Check(notModified.Status == 304 && notModified.Body.Length == 0, $"[{variant}] If-None-Match with Babelfin's ETag -> 304, no body ({notModified.Status}, {notModified.Body.Length}B)");
    var br = await Send(h, HttpMethod.Get, "/web/", new() { ["Accept-Encoding"] = "gzip, deflate, br" });
    var brText = Encoding.UTF8.GetString(Decode(br.Body, br.H("Content-Encoding")));
    Save($"{variant}_web_br.bin", br.Body);
    Check(br.Status == 200 && br.H("Content-Encoding") == "br" && brText == identity && br.H("Vary").Contains("Accept-Encoding"),
        $"[{variant}] Accept-Encoding br -> Content-Encoding br, decodes to the identity body ({br.Body.Length}B for {Encoding.UTF8.GetByteCount(identity!)}B)");
    var gz = await Send(h, HttpMethod.Get, "/web/", new() { ["Accept-Encoding"] = "gzip, br;q=0" });
    Check(gz.H("Content-Encoding") == "gzip" && Encoding.UTF8.GetString(Decode(gz.Body, "gzip")) == identity, $"[{variant}] 'gzip, br;q=0' -> gzip");
    var both = await Send(h, HttpMethod.Get, "/web/", new() { ["Accept-Encoding"] = "br", ["If-None-Match"] = "\"companion-abc123-1\"" });
    Check(both.Status == 200 && Encoding.UTF8.GetString(Decode(both.Body, both.H("Content-Encoding"))) == identity, $"[{variant}] br + Companion ETag -> a fresh 200 that decodes to the page (no 304, no double-compressed body)");
    var head = await Send(h, HttpMethod.Head, "/web/");
    Check(head.Status == 200 && head.Body.Length == 0 && head.H("ETag") == etag && head.H("Cache-Control") == "no-cache"
        && head.H("Content-Type").StartsWith("text/html") && head.H("Content-Length") == Encoding.UTF8.GetByteCount(identity!).ToString(),
        $"[{variant}] HEAD -> 200, no body, same ETag/Cache-Control/Content-Type, Content-Length {head.H("Content-Length")}");

    // Already injected
    File.WriteAllText(Path.Combine(webDir, "index.html"), originalIndex.Replace("</body>", "<script src=\"/Babelfin/client.js?v=0.0.9\" defer></script>\n</body>"));
    var pre = await Send(h, HttpMethod.Get, "/web/");
    Save($"{variant}_already_injected.html", pre.Body);
    Check(pre.Status == 200 && Count(pre.Text, "/Babelfin/client.js") == 1, $"[{variant}] page that already loads /Babelfin/client.js: not duplicated ({Count(pre.Text, "/Babelfin/client.js")})");
    File.WriteAllText(Path.Combine(webDir, "index.html"), originalIndex);

    // Other paths
    var manifest = await Send(h, HttpMethod.Get, "/web/manifest.json");
    Check(manifest.Status == 200 && manifest.Body.SequenceEqual(manifestBytes) && manifest.H("Content-Type") == "application/json"
        && manifest.Headers.ContainsKey("Last-Modified") && manifest.H("Accept-Ranges") == "bytes" && manifest.H("ETag").Length > 0 && !manifest.H("ETag").Contains("bf-"),
        $"[{variant}] /web/manifest.json passes through unchanged with static-file headers (ETag {manifest.H("ETag")})");
    var other = await Send(h, HttpMethod.Get, "/web/other.html");
    Check(other.Status == 200 && other.Text == otherHtml && !other.H("ETag").Contains("bf-"), $"[{variant}] /web/other.html is not injected");
    var post = await Send(h, HttpMethod.Post, "/web/", jsonBody: "{}");
    Check(!post.Text.Contains("/Babelfin/client.js"), $"[{variant}] POST /web/ is not touched ({post.Status})");
    await h.DisposeAsync();

    // Base URL
    var hb = await StartAsync(variant, baseUrl: "jellyfin/");
    var rb = await Send(hb, HttpMethod.Get, "/jellyfin/web/");
    Save($"{variant}_baseurl.html", rb.Body);
    Check(rb.Status == 200 && Count(rb.Text, "<script src=\"/jellyfin/Babelfin/client.js?v=0.20.0.0\" defer></script>") == 1 && (!withCompanion || rb.Text.Contains("<!-- Companion -->")),
        $"[{variant}] base URL: /jellyfin/web/ injected with /jellyfin/Babelfin/client.js");
    Check((await Send(hb, HttpMethod.Get, "/web/")).Status == 404, $"[{variant}] base URL: bare /web/ left alone (404)");
    Check((await Send(hb, HttpMethod.Get, "/jellyfin/Babelfin/client.js?v=0.20.0.0")).Status == 200, $"[{variant}] base URL: script served at /jellyfin/Babelfin/client.js");
    await hb.DisposeAsync();

    // Web client not hosted / HTTPS required
    var hn = await StartAsync(variant, hostWebClient: false);
    var rn = await Send(hn, HttpMethod.Get, "/web/");
    Check(!rn.Text.Contains("Babelfin") && !rn.H("ETag").Contains("bf-"), $"[{variant}] hostwebclient=false: page untouched");
    await hn.DisposeAsync();
    var hs = await StartAsync(variant, requireHttps: true, listenHttps: true);
    var rs = await Send(hs, HttpMethod.Get, "/web/");
    Check(!rs.Text.Contains("Babelfin") && !rs.H("ETag").Contains("bf-"), $"[{variant}] RequireHttps + ListenWithHttps over plain HTTP: untouched");
    await hs.DisposeAsync();
}

Check(!BfHostSettings.Read(new object()).Resolved && !BfHostSettings.Read(null).Resolved, "HostSettings.Read(new object()) and Read(null) -> Resolved == false");

// ── Controller ──
Section("C. Controller");
{
    var h = await StartAsync("companion-after");
    var user = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N") };
    var admin = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N"), ["X-Fake-Admin"] = "1" };

    var js = await Send(h, HttpMethod.Get, "/Babelfin/client.js?v=0.20.0.0");
    Save("client.js", js.Body);
    Check(js.Status == 200 && js.H("Content-Type").StartsWith("text/javascript") && js.Text.Contains("jellyfin_credentials")
        && js.H("Cache-Control").Contains("immutable") && js.H("Cache-Control").Contains("max-age=31536000"), $"GET /Babelfin/client.js?v=0.20.0.0: 200 text/javascript, immutable ({js.H("Cache-Control")})");
    var jsNoV = await Send(h, HttpMethod.Get, "/Babelfin/client.js");
    Check(jsNoV.Status == 200 && jsNoV.H("Cache-Control") == "no-cache", $"GET /Babelfin/client.js without v: no-cache ({jsNoV.H("Cache-Control")})");
    var js304 = await Send(h, HttpMethod.Get, "/Babelfin/client.js?v=0.20.0.0", new() { ["If-None-Match"] = js.H("ETag") });
    Check(js304.Status == 304 && js304.Body.Length == 0, $"client.js 304 on matching ETag {js.H("ETag")}");
    var jsBr = await Send(h, HttpMethod.Get, "/Babelfin/client.js?v=0.20.0.0", new() { ["Accept-Encoding"] = "br" });
    Check(jsBr.H("Content-Encoding") == "br" && Decode(jsBr.Body, "br").SequenceEqual(js.Body), "client.js brotli decodes to the same script");

    var appPage = await Send(h, HttpMethod.Get, "/Babelfin/app");
    Save("app.html", appPage.Body);
    Check(appPage.Status == 200 && appPage.H("Content-Type").StartsWith("text/html") && appPage.Text.Contains("Babelfin"), "GET /Babelfin/app: 200 text/html with Babelfin");

    var ping401 = await Send(h, HttpMethod.Get, "/Babelfin/Ping");
    var ping = await Send(h, HttpMethod.Get, "/Babelfin/Ping", user);
    Save("ping.json", ping.Body);
    Check(ping401.Status == 401, $"GET /Babelfin/Ping without a user: 401 ({ping401.Status})");
    Check(ping.Status == 200 && ping.Text.Contains("\"wrapped\":true") && ping.Text.Contains("\"version\":\"0.20.0.0\""), "GET /Babelfin/Ping signed in: 200 " + ping.Text);

    SetConfig("jpn", "eng");
    var explain = await Send(h, HttpMethod.Get, $"/Babelfin/Me/Explain?itemId={slvId:N}", user);
    Save("explain.json", explain.Body);
    var ex0 = explain.Status == 200 ? JsonDocument.Parse(explain.Text).RootElement[0] : default;
    Check(explain.Status == 200 && ex0.GetProperty("jellyfinAudioIndex").GetInt32() == 1 && ex0.GetProperty("jellyfinSubtitleIndex").GetInt32() == 3
        && ex0.GetProperty("babelfinAudioIndex").GetInt32() == 2 && ex0.GetProperty("babelfinSubtitleIndex").GetInt32() == 4
        && ex0.GetProperty("trace").ValueKind == JsonValueKind.Array && ex0.GetProperty("trace").GetArrayLength() >= 2,
        "GET /Babelfin/Me/Explain: Jellyfin 1/3, Babelfin 2/4, with a trace");
    Check(!explain.Text.Contains("path", StringComparison.OrdinalIgnoreCase) && !explain.Text.Contains("/media/") && !explain.Text.Contains(".mkv"),
        "Explain: no Path property and no file path anywhere in the body");
    Check((await Send(h, HttpMethod.Get, $"/Babelfin/Me/Explain?itemId={Guid.NewGuid():N}", user)).Status == 404, "Explain: unknown item -> 404");
    Check((await Send(h, HttpMethod.Get, $"/Babelfin/Me/Explain?itemId={hiddenId:N}", user)).Status == 404, "Explain: item the person can't see -> 404");
    Check((await Send(h, HttpMethod.Get, $"/Babelfin/Me/Explain?itemId={slvId:N}")).Status == 401, "Explain without a user -> 401");

    var status403 = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Status", user);
    var status401 = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Status");
    var status = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Status", admin);
    Save("admin-status.json", status.Body);
    Check(status403.Status == 403 && status401.Status == 401, $"GET /Babelfin/Admin/Status: plain user {status403.Status}, anonymous {status401.Status}");
    var st = status.Status == 200 ? JsonDocument.Parse(status.Text).RootElement : default;
    Check(status.Status == 200 && st.GetProperty("wrapped").GetBoolean() && st.GetProperty("applied").GetInt64() >= 1 && st.GetProperty("staticCalls").GetInt64() >= 1
        && new[] { "playbackCalls", "setDefaultsCalls", "dryRunDecisions", "passedThrough", "errors", "decisionsCached" }.All(k => st.TryGetProperty(k, out _))
        && !st.TryGetProperty("lastUpgradeNote", out _) && st.GetProperty("personFiles").GetInt32() >= 1 && st.GetProperty("unknownAppMode").GetString() == "Trust",
        "GET /Babelfin/Admin/Status as admin: 200 with counters " + status.Text);

    // ── Action filter on POST /Items/{id}/PlaybackInfo ──
    Section("C. PlaybackInfo action filter");
    // Nulls are omitted from the echo (as Jellyfin's JSON does), so absent properties read as null.
    JsonElement Prop(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    string Echo(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("itemId", out _)
        ? $"{Num(Prop(e, "audioStreamIndex"))}/{Num(Prop(e, "subtitleStreamIndex"))} source={Str(Prop(e, "mediaSourceId"))}"
        : "(no echo)";
    string DtoEcho(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("dto", out var d) && d.ValueKind == JsonValueKind.Object
        ? $"{Num(Prop(d, "audioStreamIndex"))}/{Num(Prop(d, "subtitleStreamIndex"))} source={Str(Prop(d, "mediaSourceId"))}"
        : "(no dto)";

    // A request that differs from what Babelfin just served is a manual choice (kept and remembered), so these run as viewerTwo, who was served nothing in this host.
    var viewerTwoUser = new Dictionary<string, string> { ["X-Fake-User"] = viewerTwo.Id.ToString("N") };
    async Task<JsonElement> PostInfoAs(Dictionary<string, string> who, Guid item, string query, string? body = null)
    {
        var r = await Send(h, HttpMethod.Post, $"/Items/{item:N}/PlaybackInfo{query}", who, body ?? string.Empty);
        if (r.Status != 200) Console.WriteLine($"   PlaybackInfo {item:N}{query}: HTTP {r.Status} {r.Text}");
        return r.Status == 200 ? JsonDocument.Parse(r.Text).RootElement : JsonDocument.Parse("{}").RootElement;
    }

    SetConfig("jpn", "eng", replace: false);
    var e1 = await PostInfoAs(viewerTwoUser, slvId, "?audioStreamIndex=1&subtitleStreamIndex=3");
    Check(Echo(e1) == "1/3 source=", $"unknown app in Trust mode: request unchanged ({Echo(e1)})");

    SetConfig("jpn", "eng", replace: true);
    var e2 = await PostInfoAs(viewerTwoUser, slvId, "?audioStreamIndex=1&subtitleStreamIndex=3");
    Check(Echo(e2) == $"2/4 source={slvId:N}", $"filter on, query 1/3 -> 2/4 and mediaSourceId of the single source ({Echo(e2)})");
    var e3 = await PostInfoAs(viewerTwoUser, slvId, string.Empty, "{ \"AudioStreamIndex\": 1, \"SubtitleStreamIndex\": 3 }");
    Check(DtoEcho(e3) == $"2/4 source={slvId:N}", $"filter on, body 1/3 -> dto 2/4 with MediaSourceId ({DtoEcho(e3)}; args {Echo(e3)})");
    var e4 = await PostInfoAs(viewerTwoUser, multiId, "?audioStreamIndex=1&subtitleStreamIndex=3");
    Check(Echo(e4) == "1/3 source=", $"multi-source item without mediaSourceId: untouched, no guessing ({Echo(e4)})");
    var e4b = await PostInfoAs(viewerTwoUser, multiId, $"?audioStreamIndex=1&subtitleStreamIndex=2&mediaSourceId={MultiB}");
    Check(Echo(e4b) == $"1/3 source={MultiB}", $"multi-source item with mediaSourceId B, request 1/2: B's own choice 1/3 (audio kept, forced 2 -> full eng 3) ({Echo(e4b)})");
    var e5 = await PostInfoAs(user, hiddenId, "?audioStreamIndex=1&subtitleStreamIndex=3");
    Check(Echo(e5) == "1/3 source=", $"item the person can't see: untouched ({Echo(e5)})");
    var e6 = await PostInfoAs(user, slvId, $"?audioStreamIndex=1&subtitleStreamIndex=3&userId={viewerTwo.Id:N}");
    Check(Echo(e6) == "1/3 source=", $"request on behalf of another user: untouched ({Echo(e6)})");
    SetConfig("jpn", "eng", replace: true, dryRun: true);
    var e7 = await PostInfoAs(viewerTwoUser, slvId, "?audioStreamIndex=1&subtitleStreamIndex=3");
    Check(Echo(e7) == "1/3 source=", $"filter on with DryRun: request unchanged ({Echo(e7)})");
    SetConfig();
    await h.DisposeAsync();
}

// ════════════════════════ D. Storage round trips ════════════════════════
Section("D. Storage round trips");
using (var sp = BuildDecorated())
{
    SetConfig();
    var msm = sp.GetRequiredService<IMediaSourceManager>();
    var personStore = sp.GetRequiredService<IPersonStore>();
    msm.GetStaticMediaSources(slv, true, viewerOne);
    var viewerOneFile = Path.Combine(usersFolder, viewerOne.Id.ToString("N") + ".json");
    var doc = personStore.TryGet(viewerOne.Id);
    var text = File.Exists(viewerOneFile) ? File.ReadAllText(viewerOneFile) : string.Empty;
    Save("person-ViewerOne-seeded.json", Encoding.UTF8.GetBytes(text));
    Check(doc is not null && doc.Seed is not null && doc.Seed.Assumed.Any(a => a.Field == "Understood" && a.Language == "en" && a.From == "server") && doc.Seed.Assumed.Any(a => a.Field == "Reads" && a.From == "server"),
        $"first decision seeds the person: understood and read 'en' assumed from the server's metadata language ({string.Join(", ", doc?.Seed?.Assumed.Select(a => a.Field + "=" + a.Language + "@" + a.From) ?? Array.Empty<string>())})");
    Check(text.Contains($"\"schemaVersion\": {PersonSettings.CurrentSchemaVersion}") && text.Contains("\"from\": \"server\"") && text.Contains("\"userName\": \"ViewerOne\"") && !Directory.GetFiles(usersFolder, "*.tmp").Any() && NoPaths(text),
        "the file carries the current schema, the user name and the assumed languages; no temp file remains; no path inside");

    SetConfig("jpn", "eng");
    var before = msm.GetStaticMediaSources(slv, true, viewerOne)[0];
    var original = doc!.Clone();
    var edited = doc.Clone();
    edited.Everyday.AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "eng") };
    Guid? changedId = null;
    personStore.Changed += (_, e) => changedId = e.UserId;
    personStore.Save(edited);
    var after = msm.GetStaticMediaSources(slv, true, viewerOne)[0];
    Check(IdxOf(before) == "2/4" && IdxOf(after) == "1/4" && changedId == viewerOne.Id && File.ReadAllText(viewerOneFile).Contains("\"eng\""),
        $"save round trip: the person's everyday chain (eng) beats the admin's (jpn) at once, the admin's full-eng subtitles still apply ({IdxOf(before)} -> {IdxOf(after)}); Changed raised for the person; file updated");

    var freshStore = new PersonStore(dataFolder, NullLogger<PersonStore>.Instance);
    var reloaded = freshStore.TryGet(viewerOne.Id);
    Check(reloaded is not null && reloaded.UserId == viewerOne.Id && reloaded.Everyday.AudioChain?.Length == 1 && reloaded.Everyday.AudioChain[0].Language == "eng" && reloaded.Everyday.WhenAudioUnderstood is null,
        "a fresh store reads the saved file back (chain eng; unset fields stay unset)");
    Check(freshStore.UserIds().Contains(viewerOne.Id), "UserIds lists the person");

    msm.GetStaticMediaSources(slv, true, viewerThree);
    var talFile = Path.Combine(usersFolder, viewerThree.Id.ToString("N") + ".json");
    var validBeforeCorruption = File.ReadAllBytes(talFile);
    File.WriteAllText(talFile, "{ this is not json");
    var corruptStore = new PersonStore(dataFolder, NullLogger<PersonStore>.Instance);
    var got = corruptStore.TryGet(viewerThree.Id);
    var quarantined = Directory.GetFiles(usersFolder, viewerThree.Id.ToString("N") + ".json.corrupt-*");
    Check(got is null && !File.Exists(talFile) && quarantined.Length == 1 && File.ReadAllText(quarantined[0]).Contains("not json"),
        "a corrupt file is renamed *.corrupt-<ticks> (never deleted) and the person starts from a fresh seed");
    // Resolve this test's deliberate corruption explicitly before later full-backup assertions; retain the raw recovery archive.
    File.WriteAllBytes(talFile, validBeforeCorruption);
    Check(corruptStore.Conflict(viewerThree.Id, writing: true) is null && File.ReadAllText(quarantined[0]).Contains("not json"),
        "a valid repaired account resolves its durable quarantine omission without deleting the archived original");

    personStore.Save(original); // back to the seed for the groups below
}

// ════════════════════════ E. Settings upgrade from version 1 ════════════════════════
Section("E. Settings upgrade from version 1");
{
    var xs = new FakeXml();
    const string V1 = "<?xml version=\"1.0\"?><PluginConfiguration><ConfigVersion>1</ConfigVersion><Enabled>true</Enabled><DryRun>false</DryRun><ProbeAudioLanguage>jpn</ProbeAudioLanguage><ProbeSubtitleLanguage>en</ProbeSubtitleLanguage><ProbeReplaceRequestIndices>true</ProbeReplaceRequestIndices></PluginConfiguration>";
    var cfg = (PluginConfiguration)xs.DeserializeFromBytes(typeof(PluginConfiguration), Encoding.UTF8.GetBytes(V1));
    var changed = cfg.Upgrade();
    using var ms = new MemoryStream();
    xs.SerializeToStream(cfg, ms);
    var xml = Encoding.UTF8.GetString(ms.ToArray());
    Save("config-upgraded.xml", ms.ToArray());
    Check(changed && cfg.ConfigVersion == PluginConfiguration.CurrentVersion && cfg.Enabled && !cfg.DryRun && cfg.LastUpgradeNote.Length == 0 && cfg.ProbeAudioLanguage is null && !cfg.Starter.Enabled,
        "version 1 with the probe fields -> the current version in one go, Enabled kept, probe retired, the original-language rule off, no upgrade note shown");
    Check(!xml.Contains("Probe") && !xml.Contains("ReplaceRequestIndices") && xml.Contains($"<ConfigVersion>{PluginConfiguration.CurrentVersion}</ConfigVersion>") && cfg.UnknownAppMode == AppModeKind.Trust && cfg.AppModes.Count >= 5
        && cfg.FindAppMode("Jellyfin for Android")?.Mode == AppModeKind.ReplaceOnAutoplay,
        "the saved file has no retired elements; unknown apps trust; the shipped per-app rows are present, \"Jellyfin for Android\" among them");
    Check(!cfg.Upgrade() && cfg.ConfigVersion == PluginConfiguration.CurrentVersion, "a second upgrade changes nothing");
    {
        // XmlSerializer appends a file's list to a property's default list: a plain round trip doubles the keyword and per-app lists; Upgrade() (every load) repairs it.
        var fresh3 = new PluginConfiguration { ConfigVersion = 3 };
        using var ms3 = new MemoryStream();
        xs.SerializeToStream(fresh3, ms3);
        var doubled = (PluginConfiguration)xs.DeserializeFromBytes(typeof(PluginConfiguration), ms3.ToArray());
        var rawApps = doubled.AppModes.Count; var rawWords = doubled.Keywords.PartialWords.Count;
        doubled.Upgrade();
        Check(rawApps == fresh3.AppModes.Count && doubled.AppModes.Count == fresh3.AppModes.Count && rawWords == fresh3.Keywords.PartialWords.Count && doubled.Keywords.PartialWords.Count == fresh3.Keywords.PartialWords.Count && doubled.Starter.OriginalAudioLibraryWords.Count == fresh3.Starter.OriginalAudioLibraryWords.Count,
            $"lists with shipped defaults keep their size on an XML load ({rawApps} app rows, {rawWords} partial words; the XML proxies assign instead of appending)");
        var edited = new PluginConfiguration { ConfigVersion = 3 };
        edited.Keywords.PartialWords = new List<string> { "signs", "custom-word" };
        edited.Starter.OriginalAudioGenres = new List<string>();
        using var ms4 = new MemoryStream();
        xs.SerializeToStream(edited, ms4);
        var back4 = (PluginConfiguration)xs.DeserializeFromBytes(typeof(PluginConfiguration), ms4.ToArray());
        back4.Upgrade();
        Check(back4.Keywords.PartialWords.SequenceEqual(new[] { "signs", "custom-word" }) && back4.Starter.OriginalAudioGenres.Count == 0 && back4.AppModes.Count == fresh3.AppModes.Count,
            $"an admin's edited list survives a load exactly ({string.Join(",", back4.Keywords.PartialWords)}); an emptied genre list stays empty ({back4.Starter.OriginalAudioGenres.Count}); untouched lists keep the defaults");
        var modeEdited = new PluginConfiguration { ConfigVersion = 3 };
        modeEdited.AppModes.First(a => a.Client == "Jellyfin Web").Mode = AppModeKind.Trust;
        modeEdited.AppModes.RemoveAll(a => a.Client == "Swiftfin");
        using var ms5 = new MemoryStream();
        xs.SerializeToStream(modeEdited, ms5);
        var back5 = (PluginConfiguration)xs.DeserializeFromBytes(typeof(PluginConfiguration), ms5.ToArray());
        back5.Upgrade();
        Check(back5.AppModes.Count == modeEdited.AppModes.Count && back5.FindAppMode("jellyfin web")?.Mode == AppModeKind.Trust && back5.FindAppMode("Swiftfin") is null,
            $"an edited per-app table survives a load: {back5.AppModes.Count} rows, Jellyfin Web = {back5.FindAppMode("Jellyfin Web")?.Mode}, Swiftfin removed");
        Check(!Encoding.UTF8.GetString(ms5.ToArray()).Contains("Xml>") && Encoding.UTF8.GetString(ms5.ToArray()).Contains("<AppModes>") && Encoding.UTF8.GetString(ms5.ToArray()).Contains("<PartialWords>"),
            "the XML file format is unchanged (element names without the Xml suffix)");
    }
    const string V2 = "<?xml version=\"1.0\"?><PluginConfiguration><ConfigVersion>2</ConfigVersion><Enabled>true</Enabled><ReplaceRequestIndices>true</ReplaceRequestIndices><Everyday><FetchMissing>false</FetchMissing></Everyday></PluginConfiguration>";
    var cfg2 = (PluginConfiguration)xs.DeserializeFromBytes(typeof(PluginConfiguration), Encoding.UTF8.GetBytes(V2));
    var changed2 = cfg2.Upgrade();
    Check(changed2 && cfg2.ConfigVersion == PluginConfiguration.CurrentVersion && cfg2.UnknownAppMode == AppModeKind.ReplaceUnlessManual && cfg2.AppModes.All(a => a.Mode == AppModeKind.ReplaceUnlessManual) && cfg2.LastUpgradeNote.Length == 0,
        "version 2 with the replace switch on upgrades: every app replaces unless manual; the retired subtitle-download field is ignored");
    const string V3 = "<?xml version=\"1.0\"?><PluginConfiguration><ConfigVersion>3</ConfigVersion><Enabled>true</Enabled><Starter><Enabled>true</Enabled></Starter><Everyday><AudioChain><AudioStepSetting><Kind>OriginalLanguage</Kind></AudioStepSetting><AudioStepSetting><Kind>Language</Kind><Language>en</Language></AudioStepSetting></AudioChain><IfNothingMatches>PartialInMyLanguage</IfNothingMatches></Everyday><Libraries><LibraryDefault><LibraryId>abc</LibraryId><Name>Anime</Name><OriginalAudio>Yes</OriginalAudio><Settings><AudioChain><AudioStepSetting><Kind>Language</Kind><Language>ja</Language></AudioStepSetting></AudioChain><UnlabelledAudio>SpecificLanguage</UnlabelledAudio><UnlabelledAudioLanguage>ja</UnlabelledAudioLanguage></Settings></LibraryDefault></Libraries></PluginConfiguration>";
    var cfg3 = (PluginConfiguration)xs.DeserializeFromBytes(typeof(PluginConfiguration), Encoding.UTF8.GetBytes(V3));
    var changed3 = cfg3.Upgrade();
    Check(changed3 && cfg3.ConfigVersion == PluginConfiguration.CurrentVersion && !cfg3.Starter.Enabled && cfg3.Everyday.AudioChain is null && cfg3.Everyday.IfNothingMatches is null
        && cfg3.Libraries.Single().Settings.IsEmpty && cfg3.Libraries.Single().OriginalAudio == OriginalAudioChoice.Yes && cfg3.PerLibraryDefaults is null,
        "version 3 -> current version: the server-wide audio defaults, the original-language rule and the settings the page no longer shows are cleared; the library list is kept");
    var freshInstall = new PluginConfiguration();
    freshInstall.Upgrade();
    Check(freshInstall.ConfigVersion == PluginConfiguration.CurrentVersion && freshInstall.LastUpgradeNote.Length == 0, $"a fresh install upgrades to version {freshInstall.ConfigVersion} without an upgrade note ('{freshInstall.LastUpgradeNote}')");
    var fresh = new PluginConfiguration();
    Check(fresh.Everyday.IsEmpty && fresh.Keywords.PartialWords.Count > 10 && fresh.Starter.OriginalAudioLibraryWords.Contains("anime") && fresh.Starter.OriginalAudioGenres.Contains("Anime") && fresh.NewUserTemplate is null,
        "fresh settings: everyday empty (inherit), keyword and starter lists filled, no template");

    var nested = new PluginConfiguration { ConfigVersion = 2 };
    nested.Libraries.Add(new LibraryDefault { LibraryId = animeFolder.Id.ToString("N"), Name = "Anime", OriginalAudio = OriginalAudioChoice.Yes, Settings = new SettingsLayer { SubtitleLanguages = Array.Empty<string>(), UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage, UnlabelledAudioLanguage = "ja" } });
    nested.NewUserTemplate = PersonSeeder.Seed(new SeedInputs(Guid.Empty, null, "OriginalLanguage", "eng", "Smart", null, "en", DateTime.UtcNow), LanguageTable.Default);
    nested.NewUserTemplate.Locks.Format = FormatPreference.TextOnly;
    using var ms2 = new MemoryStream();
    xs.SerializeToStream(nested, ms2);
    var back = (PluginConfiguration)xs.DeserializeFromBytes(typeof(PluginConfiguration), ms2.ToArray());
    Check(back.Libraries.Count == 1 && back.Libraries[0].OriginalAudio == OriginalAudioChoice.Yes && string.Join(",", back.Libraries[0].Settings.SetFields()) == "UnlabelledAudio,SubtitleLanguages"
        && back.Libraries[0].Settings.SubtitleLanguages!.Length == 0 && back.Everyday.IsEmpty && back.NewUserTemplate?.Everyday.Understood?.SequenceEqual(new[] { "en" }) == true && back.NewUserTemplate.Locks.Format == FormatPreference.TextOnly,
        "nested layers survive XML: an empty array stays 'set to none', unset fields stay unset, the template keeps its lock");
}

// ════════════════════════ F. Resolver and starter rules through the real item context ════════════════════════
Section("F. Resolver, starter rules and library facts");
using (var sp = BuildDecorated())
{
    var msm = sp.GetRequiredService<IMediaSourceManager>();
    var resolver = sp.GetRequiredService<IProfileResolver>();
    var personStore = sp.GetRequiredService<IPersonStore>();
    var facts = sp.GetRequiredService<ILibraryFacts>();
    var stats = sp.GetRequiredService<DecoratorStats>();
    PersonSettings Doc(User u) => personStore.GetOrCreate(u.Id, () => PersonSeeder.Seed(SeedInputs.FromUser(u, "en"), LanguageTable.Default)).Clone();
    // The original-language rule on, and subtitles only when the audio isn't understood (signs and songs otherwise). The table is
    // either/or: with no per-library rows the rule is the everyday default; once a check adds a row, it goes into
    // every library's row instead (an everyday default wouldn't count in per-library mode).
    void SmartAdmin(Action<PluginConfiguration>? more = null) => SetConfig(more: c =>
    {
        c.Starter.Enabled = true;
        more?.Invoke(c);
        if (c.Libraries.Any(l => TableModes.HasTableFields(l.Settings)))
        {
            c.PerLibraryDefaults = true;
            foreach (var folder in new[] { animeFolder, tvFolder, cartoonsFolder })
            {
                var row = c.FindLibrary(folder.Id);
                if (row is null)
                {
                    row = new LibraryDefault { LibraryId = folder.Id.ToString("N"), Name = folder.Name };
                    c.Libraries.Add(row);
                }

                row.Settings.WhenAudioUnderstood ??= SubtitleWant.PartialOnly;
                row.Settings.WhenAudioNotUnderstood ??= SubtitleWant.Full;
            }
        }
        else
        {
            c.Everyday.WhenAudioUnderstood = SubtitleWant.PartialOnly;
            c.Everyday.WhenAudioNotUnderstood = SubtitleWant.Full;
        }
    });

    SetConfig();
    var r0 = resolver.Resolve(viewerOne, animeEp);
    var passedBefore = stats.PassedThrough;
    var s0 = msm.GetStaticMediaSources(animeEp, true, viewerOne)[0];
    Check(IdxOf(s0) == "1/3" && stats.PassedThrough == passedBefore + 1 && r0.Sources["AudioChain"] == StarterRules.DefaultsLabel && r0.LibraryIsOriginalAudio,
        $"out of the box, even the Anime library is left to the video: Jellyfin's own {IdxOf(s0)} stays (the original-language rule is off)");

    SmartAdmin();
    var r1 = resolver.Resolve(viewerOne, animeEp);
    var s1 = msm.GetStaticMediaSources(animeEp, true, viewerOne)[0];
    Check(r1.Context.LibraryName == "Anime" && r1.Context.SeriesId == animeSeries.Id && r1.Context.SeasonId == animeSeason.Id && r1.Context.Kind == ContentKind.Episode
        && r1.Context.Genres.Contains("Animation") && r1.Context.OriginalLanguage == "ja" && r1.Context.SeriesName == "Skyward Lantern Voyage" && r1.LibraryIsOriginalAudio,
        $"item context from the real builder: library {r1.Context.LibraryName}, series/season ids, genres from the series, original language inherited ({r1.Context.OriginalLanguage})");
    Check(r1.Sources["AudioChain"] == StarterRules.LibraryLabel && IdxOf(s1) == "2/4", $"Anime library (by name, no facts yet) -> starter original-audio chain -> {IdxOf(s1)} (Japanese, full English, the Signs track skipped)");
    var d1 = stats.GetDecision(viewerOne.Id, animeEpId, s1.Id)?.Decision;
    Save("explain-anime-starter.txt", Encoding.UTF8.GetBytes(d1?.Explanation ?? string.Empty));
    Check(d1?.Explanation?.Contains(StarterRules.LibraryLabel) == true && d1.Engine is not null && d1.Explanation.Contains("#2") && d1.Explanation.Contains("#4"),
        "explanation names the starter rule: " + d1?.Explanation);
    Check(r1.Assumed.Count == 2 && r1.Assumed.All(a => a.Language == "en" && a.From == "server"), "the result lists the assumed languages for the banner");
    var rTv = resolver.Resolve(viewerOne, slv);
    Check(IdxOf(msm.GetStaticMediaSources(slv, true, viewerOne)[0]) == "1/3" && rTv.Sources["AudioChain"] == StarterRules.DefaultsLabel && !rTv.LibraryIsOriginalAudio,
        "the same streams in TV Shows -> the audio is the video's (1); English understood -> signs and songs (3)");

    SmartAdmin(c => c.Libraries.Add(new LibraryDefault { LibraryId = animeFolder.Id.ToString("N"), Name = "Anime", OriginalAudio = OriginalAudioChoice.No, Settings = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "eng") } } }));
    var r2 = resolver.Resolve(viewerOne, animeEp);
    Check(IdxOf(msm.GetStaticMediaSources(animeEp, true, viewerOne)[0]) == "1/3" && r2.Sources["AudioChain"] == "the admin's Anime library defaults" && !r2.LibraryIsOriginalAudio,
        $"admin library default (original-audio: No, chain eng) -> 1/3, source '{r2.Sources["AudioChain"]}'; the cache dropped the old profile on the config change");

    var w3 = Doc(viewerOne);
    w3.Libraries.Add(new LibraryLayer { LibraryId = animeFolder.Id.ToString("N"), Name = "Anime", Settings = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "jpn") } } });
    personStore.Save(w3);
    var r3 = resolver.Resolve(viewerOne, animeEp);
    Check(IdxOf(msm.GetStaticMediaSources(animeEp, true, viewerOne)[0]) == "2/4" && r3.Sources["AudioChain"] == "your Anime library settings", $"the person's library layer beats the admin's ('{r3.Sources["AudioChain"]}')");

    var w4 = Doc(viewerOne);
    w4.Locks.AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "eng") };
    personStore.Save(w4);
    var r4 = resolver.Resolve(viewerOne, animeEp);
    Check(IdxOf(msm.GetStaticMediaSources(animeEp, true, viewerOne)[0]) == "2/4" && r4.Sources["AudioChain"] == "your Anime library settings", $"an old hidden audio lock cannot override the person's visible library choice ('{r4.Sources["AudioChain"]}')");

    SmartAdmin(c => c.Libraries.Add(new LibraryDefault { LibraryId = animeFolder.Id.ToString("N"), Name = "Anime", Settings = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "eng") } } }));
    var p5 = Doc(viewerTwo);
    p5.Everyday.Understood = new[] { "cmn" };
    p5.Everyday.Reads = new[] { "en" };
    p5.PerLibrary = false;
    p5.Everyday.AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "cmn"), new AudioStepSetting(AudioStepKind.VideoDefault) };
    personStore.Save(p5);
    var r5 = resolver.Resolve(viewerTwo, animeEp);
    var s5 = msm.GetStaticMediaSources(animeEp, true, viewerTwo)[0];
    Check(IdxOf(s5) == "1/4" && r5.Sources["AudioChain"] == "your settings for every library" && r5.Profile.Understood.SequenceEqual(new[] { "cmn" }),
        $"'same for every library' (Mandarin) beats the admin's Anime default (eng); no Mandarin track, so the video's audio plays and, not understood, full English subtitles ({IdxOf(s5)}, source '{r5.Sources["AudioChain"]}')");
    p5.PerLibrary = true;
    personStore.Save(p5);
    var r5b = resolver.Resolve(viewerTwo, animeEp);
    Check(IdxOf(msm.GetStaticMediaSources(animeEp, true, viewerTwo)[0]) == "1/4" && r5b.Sources["AudioChain"] == "the admin's Anime library defaults",
        $"'choose for each library' with no rows: the admin's Anime default (eng audio, not understood by a Mandarin speaker, so full English subtitles) applies ('{r5b.Sources["AudioChain"]}')");

    SmartAdmin();
    var w6 = Doc(viewerOne);
    w6.Locks = new SettingsLayer();
    w6.Libraries.Clear();
    w6.SmartRules.Add(new SmartRule { Id = "nosubs", Name = "No subs", Conditions = { new RuleCondition { Kind = ConditionKind.Genre, Values = { "animation" } } }, Settings = new SettingsLayer { WhenAudioNotUnderstood = SubtitleWant.Off } });
    personStore.Save(w6);
    var r6 = resolver.Resolve(viewerOne, animeEp);
    Check(IdxOf(msm.GetStaticMediaSources(animeEp, true, viewerOne)[0]) == "2/-1" && r6.Sources["WhenAudioNotUnderstood"] == "your No subs genre settings" && r6.Sources["AudioChain"] == StarterRules.LibraryLabel,
        $"a person's genre row (Animation, matched case-insensitively against the series' genres) sets one field; the others fall through ('{r6.Sources["WhenAudioNotUnderstood"]}')");

    SmartAdmin(c => c.SmartRules.Add(new SmartRule { Id = "rx", Name = "Skyward regex", Conditions = { new RuleCondition { Kind = ConditionKind.TitleMatches, Values = { "^Skyward" }, IsRegex = true } }, Settings = new SettingsLayer { Format = FormatPreference.TextOnly } }));
    var w7 = Doc(viewerOne);
    w7.SmartRules.Add(new SmartRule { Id = "plain", Name = "Plain", Conditions = { new RuleCondition { Kind = ConditionKind.TitleMatches, Values = { "^Skyward" }, IsRegex = true } }, Settings = new SettingsLayer { Sdh = Jellyfin.Plugin.Babelfin.Engine.Selection.Preference.Avoid } });
    personStore.Save(w7);
    var r7 = resolver.Resolve(viewerOne, animeEp);
    Check(r7.Sources["Format"] == StarterRules.DefaultsLabel && r7.Sources["Sdh"] == StarterRules.DefaultsLabel,
        $"hidden general title rules from either person or admin do not affect active choices ('{r7.Sources["Format"]}' / '{r7.Sources["Sdh"]}')");

    await facts.RefreshAsync(null, CancellationToken.None);
    var af = facts.Get(animeFolder.Id);
    var tf = facts.Get(tvFolder.Id);
    var cf = facts.Get(cartoonsFolder.Id);
    Check(af is { DetectedOriginalAudio: true, NameMatchesWordList: true, UsualOriginalLanguage: "ja", TitleCount: 1 } && tf is { DetectedOriginalAudio: false, NameMatchesWordList: false, UsualOriginalLanguage: "ja" }
        && cf is { DetectedOriginalAudio: true, NameMatchesWordList: false, OriginalAudioGenrePercent: 100 } && File.Exists(Path.Combine(dataFolder, "library-facts.json")) && facts.ComputedAt is not null,
        $"library facts: Anime by name, Cartoons by genre share ({cf?.OriginalAudioGenrePercent}%), TV Shows neither; usual original language ja; cache file written");
    var r8 = resolver.Resolve(viewerOne, slv);
    Check(r8.LibraryUsualOriginalLanguage == "ja" && !r8.LibraryIsOriginalAudio, "after the refresh the resolver sees the library's usual original language");
    var cartoonEpId = Guid.NewGuid();
    var cartoonEp = lib.AddEpisode(lib.AddSeason(cartoonSeries, 1), cartoonEpId, "Some Cartoon S01E01");
    store.Add(cartoonEp, Fx.Source(cartoonEpId.ToString("N"), "/media/Cartoons/x.mkv", Fx.SlvStreams()));
    var r9 = resolver.Resolve(viewerOne, cartoonEp);
    Check(r9.LibraryIsOriginalAudio && r9.Sources["AudioChain"] == StarterRules.LibraryLabel && IdxOf(msm.GetStaticMediaSources(cartoonEp, true, viewerOne)[0]) == "2/4",
        "a library detected by genre share gets the original-audio chain too");

    var sw = Stopwatch.StartNew();
    for (var i = 0; i < 2000; i++) resolver.Resolve(viewerOne, animeEp);
    sw.Stop();
    Check(sw.ElapsedMilliseconds < 200, $"2,000 cached resolves in {sw.ElapsedMilliseconds} ms (dictionary lookups)");
    var swDecide = Stopwatch.StartNew();
    for (var i = 0; i < 1000; i++) msm.GetStaticMediaSources(animeEp, true, viewerOne);
    swDecide.Stop();
    Check(swDecide.ElapsedMilliseconds < 1000, $"1,000 full decisions through the decorator in {swDecide.ElapsedMilliseconds} ms (target: under 1 ms each)");

    ResetPersonFixture(personStore, viewerOne.Id);
    ResetPersonFixture(personStore, viewerTwo.Id);
    SetConfig();
}

// ════════════════════════ H. PlaybackInfo modes, manual changes, correction after start ════════════════════════
Section("H. Playback handling (modes, manual changes, correction)");
{
    var h = await StartAsync("plain");
    await PlaybackChecks.Run(h.App, h.Http, viewerOne, slv, animeEp, Check);
    await h.DisposeAsync();
}

// ════════════════════════ G. API security ════════════════════════
Section("G. API security");
{
    var h = await StartAsync("plain");
    var user = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N") };
    var admin = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N"), ["X-Fake-Admin"] = "1" };
    SetConfig();
    JsonNode Node(Resp r) => JsonNode.Parse(r.Text)!;

    Check(animeFolder.IsVisibleStandalone(viewerOne), $"fixture: the Anime folder is visible to ViewerOne (permissions: {viewerOne.Permissions.Count}, EnableAllFolders: {viewerOne.Permissions.FirstOrDefault(p => p.Kind == Jellyfin.Database.Implementations.Enums.PermissionKind.EnableAllFolders)?.Value})");
    var anon = await Send(h, HttpMethod.Get, "/Babelfin/Me/Settings");
    var me = await Send(h, HttpMethod.Get, "/Babelfin/Me/Settings", user);
    Save("me-settings.json", me.Body);
    var meNode = me.Status == 200 ? Node(me) : new JsonObject();
    Check(anon.Status == 401 && me.Status == 200 && Guid.Parse(meNode["settings"]!["userId"]!.GetValue<string>()) == viewerOne.Id
        && meNode["inherited"]!["sources"]!["AudioChain"] is not null && meNode["libraries"] is JsonArray && meNode["assumed"] is JsonArray { Count: 2 },
        $"GET Me/Settings: 401 without a user; 200 with settings, inherited sources, libraries and 2 assumed languages ({anon.Status}/{me.Status})");
    Check(NoPaths(me.Text), "Me/Settings carries no file path");
    var libs = meNode["libraries"] as JsonArray ?? new JsonArray();
    Check(libs.Any(l => l!["name"]!.GetValue<string>() == "Anime" && l["isOriginalAudio"]!.GetValue<bool>()) && libs.Any(l => l!["name"]!.GetValue<string>() == "TV Shows" && !l["isOriginalAudio"]!.GetValue<bool>()),
        $"libraries list: Anime is original-audio, TV Shows isn't ({string.Join(", ", libs.Select(l => l!["name"]))})");

    var body = (JsonObject)meNode["settings"]!.DeepClone();
    body["everyday"]!["understood"] = new JsonArray("de");
    body["locks"] = new JsonObject { ["format"] = "TextOnly" };
    var put = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, body.ToJsonString());
    Save("me-settings-put.json", put.Body);
    var putNode = put.Status == 200 ? Node(put) : new JsonObject();
    Check(put.Status == 200 && putNode["settings"]!["everyday"]!["understood"]!.AsArray().Single()!.GetValue<string>() == "de" && putNode["settings"]!["locks"]?["format"] is null,
        $"PUT Me/Settings: 200, understood saved as 'de', the locks in the body ignored ({put.Status})");
    var putAnon = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", null, body.ToJsonString());
    Check(putAnon.Status == 401, $"PUT Me/Settings without a user: 401 ({putAnon.Status})");

    body["overrides"] = new JsonArray(new JsonObject { ["itemId"] = hiddenId.ToString("N"), ["scope"] = "Title", ["settings"] = new JsonObject() });
    var hiddenPut = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, body.ToJsonString());
    Check(hiddenPut.Status == 400 && hiddenPut.Text.Contains(hiddenId.ToString("N")) && !hiddenPut.Text.Contains("Hidden Movie"), $"PUT with an override on an item the person can't see: 400 naming the id, not the item ({hiddenPut.Status})");
    body["overrides"] = new JsonArray();
    body["burnedIn"] = new JsonArray(new JsonObject { ["itemId"] = animeFolder.Id.ToString("N"), ["scope"] = "Library", ["languages"] = new JsonArray("en") });
    var libFlag = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, body.ToJsonString());
    Check(libFlag.Status == 403, $"PUT with a library-wide burned-in flag from a person: 403 ({libFlag.Status})");
    body["burnedIn"] = new JsonArray();
    body["smartRules"] = new JsonArray(new JsonObject
    {
        ["id"] = "r1", ["name"] = "Regexy", ["enabled"] = true, ["match"] = "All",
        ["conditions"] = new JsonArray(new JsonObject { ["kind"] = "TitleMatches", ["values"] = new JsonArray("^Skyward"), ["isRegex"] = true }),
        ["settings"] = new JsonObject { ["sdh"] = "Avoid" },
    });
    var rulePut = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, body.ToJsonString());
    var ruleNode = rulePut.Status == 200 ? Node(rulePut) : new JsonObject();
    Check(rulePut.Status == 200 && ruleNode["settings"]!["smartRules"]!.AsArray().Single()!["conditions"]!.AsArray().Single()!["isRegex"]!.GetValue<bool>() == false,
        $"a person's rule with isRegex=true is stored as plain text ({rulePut.Status})");
    var eff0 = await Send(h, HttpMethod.Get, $"/Babelfin/Me/Effective?itemId={animeEpId:N}", user);
    Check(eff0.Status == 200 && Node(eff0)["sources"]!["Sdh"]!.GetValue<string>() != "your smart rule 'Regexy'", "the plain pattern '^Skyward' doesn't match the title (no regex for people)");

    var ovHidden = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{hiddenId:N}?scope=Title", user, "{}");
    var ovUnknown = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{Guid.NewGuid():N}?scope=Title", user, "{}");
    Check(ovHidden.Status == 404 && ovUnknown.Status == 404, $"PUT Me/Overrides on a hidden or unknown item: 404 ({ovHidden.Status}/{ovUnknown.Status})");
    var ovSeries = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{animeEpId:N}?scope=Series", user, "{ \"audioChain\": [ { \"kind\": \"Language\", \"language\": \"eng\" } ] }");
    Save("override-series.json", ovSeries.Body);
    Check(ovSeries.Status == 200 && Guid.Parse(Node(ovSeries)["itemId"]!.GetValue<string>()) == animeSeries.Id && Node(ovSeries)["scope"]!.GetValue<string>() == "Series",
        $"PUT Me/Overrides/{{episode}}?scope=Series stores the override under the series id ({ovSeries.Status})");
    var eff1 = await Send(h, HttpMethod.Get, $"/Babelfin/Me/Effective?itemId={animeEpId:N}", user);
    Save("effective-anime.json", eff1.Body);
    Check(eff1.Status == 200 && Node(eff1)["sources"]!["AudioChain"]!.GetValue<string>() == "your settings for this show" && NoPaths(eff1.Text),
        $"GET Me/Effective: the show override is the audio source ({(eff1.Status == 200 ? Node(eff1)["sources"]!["AudioChain"] : eff1.Status)}); no path");
    var explainOv = await Send(h, HttpMethod.Get, $"/Babelfin/Me/Explain?itemId={animeEpId:N}", user);
    Save("explain-override.json", explainOv.Body);
    var exNode = explainOv.Status == 200 ? Node(explainOv)![0]! : new JsonObject();
    Check(explainOv.Status == 200 && exNode["babelfinAudioIndex"]!.GetValue<int>() == 1 && exNode["explanation"]!.GetValue<string>().Contains("your settings for this show") && exNode["engineTrace"] is JsonArray { Count: > 2 } && NoPaths(explainOv.Text),
        "Me/Explain: audio 1 from the show override, the explanation and the structured trace, no path or track list");
    Check((await Send(h, HttpMethod.Get, $"/Babelfin/Me/Effective?itemId={hiddenId:N}", user)).Status == 404 && (await Send(h, HttpMethod.Get, $"/Babelfin/Me/Effective?itemId={animeEpId:N}")).Status == 401, "Me/Effective: hidden item 404, anonymous 401");
    var del = await Send(h, HttpMethod.Delete, $"/Babelfin/Me/Overrides/{animeEpId:N}?scope=Series", user);
    var eff2 = await Send(h, HttpMethod.Get, $"/Babelfin/Me/Effective?itemId={animeEpId:N}", user);
    Check(del.Status == 204 && Node(eff2)["sources"]!["AudioChain"]!.GetValue<string>() == StarterRules.DefaultsLabel, "DELETE the override: 204, the audio is the video's again");

    var otherAsUser = await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", user);
    var otherAsAdmin = await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", admin);
    var otherAnon = await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings");
    Check(otherAsUser.Status == 403 && otherAnon.Status == 401 && otherAsAdmin.Status == 200 && Guid.Parse(Node(otherAsAdmin)["userId"]!.GetValue<string>()) == viewerTwo.Id,
        $"another person's settings: 403 for a plain user, 401 anonymous, 200 for an admin ({otherAsUser.Status}/{otherAnon.Status}/{otherAsAdmin.Status})");
    var usersList = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Users", admin);
    Save("admin-users.json", usersList.Body);
    Check(usersList.Status == 200 && usersList.Text.Contains("ViewerOne") && usersList.Text.Contains("\"hasSettings\":true") && (await Send(h, HttpMethod.Get, "/Babelfin/Admin/Users", user)).Status == 403,
        "Admin/Users lists people with their state; 403 for a plain user");

    // the People table's Audio/Subtitles columns come from the list itself (no file is read or created for people without one).
    var viewerOneRow = Node(usersList).AsArray().FirstOrDefault(n => n?["name"]?.GetValue<string>() == "ViewerOne");
    Check(viewerOneRow is not null && viewerOneRow["perLibrary"] is not null && viewerOneRow["libraryRows"] is not null,
        $"Admin/Users rows carry the table mode, the every-library choice and the library row count ({viewerOneRow?["perLibrary"]}, {viewerOneRow?["libraryRows"]})");
    var shipped = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Defaults", admin);
    Check(shipped.Status == 200 && Node(shipped)["configVersion"]!.GetValue<int>() == PluginConfiguration.CurrentVersion && shipped.Text.Contains("Jellyfin Desktop")
        && (await Send(h, HttpMethod.Get, "/Babelfin/Admin/Defaults", user)).Status == 403,
        "GET Admin/Defaults: the shipped settings for an admin (Jellyfin Desktop listed), 403 for a plain user");

    // AdvancedDefaults is what applies without the person's own everyday fields; Inherited includes them.
    var meBefore = Node(await Send(h, HttpMethod.Get, "/Babelfin/Me/Settings", user));
    var doc07 = meBefore["settings"]!.DeepClone();
    doc07["everyday"]!["format"] = "TextOnly";
    var put07 = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, doc07.ToJsonString());
    var me07 = put07.Status == 200 ? Node(put07) : new JsonObject();
    Check(put07.Status == 200 && me07["inherited"]?["profile"]?["format"]?.GetValue<string>() == "TextOnly" && me07["advancedDefaults"]?["profile"]?["format"]?.GetValue<string>() == "PreferText",
        $"Me/Settings: AdvancedDefaults leaves out the person's own choice (format {me07["advancedDefaults"]?["profile"]?["format"]}), Inherited keeps it ({me07["inherited"]?["profile"]?["format"]})");
    var maskedSave = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true&fields=everyday.reads", user, "{\"everyday\":{\"reads\":null}}");
    var maskedDoc = maskedSave.Status == 200 ? Node(maskedSave)["settings"] : null;
    Check(maskedSave.Status == 200 && maskedDoc?["everyday"]?["reads"] is null
        && maskedDoc?["everyday"]?["format"]?.GetValue<string>() == "TextOnly"
        && maskedDoc?["everyday"]?["understood"]?.AsArray().Single()?.GetValue<string>() == "de",
        "Explicit field intent honors null inheritance and preserves unedited Everyday and Advanced fields");
    Check((await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true&fields=", user, "{}")).Status == 400,
        "Empty field-intent query is rejected instead of becoming a full replacement");
    Check((await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true&fields=locks.format", user, "{}")).Status == 400,
        "Personal field intent cannot write admin locks");
    await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, meBefore["settings"]!.ToJsonString());

    var adminCfg = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Settings", admin);
    Check(adminCfg.Status == 200 && (await Send(h, HttpMethod.Get, "/Babelfin/Admin/Settings", user)).Status == 403, $"GET Admin/Settings: 200 admin, 403 user ({adminCfg.Status})");
    // Expected arrays travel in the body: legal tables can exceed HTTP header limits.
    var originalTitles = Node(adminCfg)["titles"]?.DeepClone() ?? new JsonArray();
    var largeTitles = new JsonArray(Enumerable.Range(1, 100).Select(i => (JsonNode)new JsonObject
    {
        ["itemId"] = i.ToString("x32"), ["scope"] = "Title", ["name"] = new string('A', 200), ["settings"] = new JsonObject(),
    }).ToArray());
    var largeExpected = new JsonObject { ["titles"] = largeTitles.DeepClone() };
    var compactJson = new JsonSerializerOptions { WriteIndented = false };
    Check(Convert.ToBase64String(Encoding.UTF8.GetBytes(largeExpected.ToJsonString(compactJson))).Length > 32768,
        "A legal 100-row expected title table exceeds the default Kestrel header budget");
    var seedTable = await Send(h, HttpMethod.Put, "/Babelfin/Admin/Settings?fields=titles", admin,
        new JsonObject { ["titles"] = largeTitles.DeepClone(), ["expectedValues"] = new JsonObject { ["titles"] = originalTitles.DeepClone() } }.ToJsonString());
    var changedTitles = (JsonArray)largeTitles.DeepClone(); changedTitles[0]!["name"] = "Updated title";
    var tableBody = new JsonObject { ["titles"] = changedTitles.DeepClone(), ["expectedValues"] = largeExpected.DeepClone() }.ToJsonString();
    var savedTable = await Send(h, HttpMethod.Put, "/Babelfin/Admin/Settings?fields=titles", admin, tableBody);
    var staleTable = await Send(h, HttpMethod.Put, "/Babelfin/Admin/Settings?fields=titles", admin, tableBody);
    Check(seedTable.Status == 200 && savedTable.Status == 200 && staleTable.Status == 409
        && !savedTable.Text.Contains("expectedValues", StringComparison.OrdinalIgnoreCase),
        $"Large body expectations save, reject a stale same-field write and stay out of configuration ({seedTable.Status}/{savedTable.Status}/{staleTable.Status})");
    await Send(h, HttpMethod.Put, "/Babelfin/Admin/Settings?fields=titles", admin,
        new JsonObject { ["titles"] = originalTitles.DeepClone() }.ToJsonString());
    foreach (var (path, credentials) in new[]
    {
        ("/Babelfin/Me/Settings", user),
        ($"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", admin),
    })
    {
        var fresh = Node(await Send(h, HttpMethod.Get, path, credentials));
        var currentDoc = fresh["settings"] ?? fresh;
        var oldEnabled = currentDoc["enabled"]?.GetValue<bool>() ?? true;
        var bodyExpected = new JsonObject { ["enabled"] = !oldEnabled, ["expectedValues"] = new JsonObject { ["enabled"] = oldEnabled } }.ToJsonString();
        var savedExpected = await Send(h, HttpMethod.Put, path + "?fields=enabled", credentials, bodyExpected);
        var staleExpected = await Send(h, HttpMethod.Put, path + "?fields=enabled", credentials, bodyExpected);
        Check(savedExpected.Status == 200 && staleExpected.Status == 409 && !savedExpected.Text.Contains("expectedValues", StringComparison.OrdinalIgnoreCase),
            $"{path}: body expectations save, conflict and remain request-only ({savedExpected.Status}/{staleExpected.Status})");
        await Send(h, HttpMethod.Put, path + "?fields=enabled", credentials, new JsonObject { ["enabled"] = oldEnabled }.ToJsonString());
    }
    JsonObject AdminWithRule(string pattern) {
        var cfg = (JsonObject)Node(adminCfg).DeepClone();
        cfg["smartRules"] = new JsonArray(new JsonObject
        {
            ["id"] = "rx", ["name"] = "Rx", ["enabled"] = true, ["match"] = "All",
            ["conditions"] = new JsonArray(new JsonObject { ["kind"] = "TitleMatches", ["values"] = new JsonArray(pattern), ["isRegex"] = true }),
            ["settings"] = new JsonObject { ["format"] = "TextOnly" },
        });
        return cfg;
    }
    var badRegex = await Send(h, HttpMethod.Put, "/Babelfin/Admin/Settings", admin, AdminWithRule("(a").ToJsonString());
    var backref = await Send(h, HttpMethod.Put, "/Babelfin/Admin/Settings", admin, AdminWithRule("(a)\\1").ToJsonString());
    var goodRegex = await Send(h, HttpMethod.Put, "/Babelfin/Admin/Settings", admin, AdminWithRule("^Skyward").ToJsonString());
    Save("admin-settings-put.json", goodRegex.Body);
    Check(badRegex.Status == 400 && backref.Status == 400 && goodRegex.Status == 200 && badRegex.Text.Contains("Rx"),
        $"PUT Admin/Settings: an invalid regex and a backreference are 400 (naming the rule), '^Skyward' is 200 ({badRegex.Status}/{backref.Status}/{goodRegex.Status})");
    var eff3 = await Send(h, HttpMethod.Get, $"/Babelfin/Me/Effective?itemId={animeEpId:N}", user);
    Check(Node(eff3)["sources"]!["Format"]!.GetValue<string>() != "the admin's smart rule 'Rx'", "a stored general regex rule stays readable but cannot affect active resolution");
    Check((await Send(h, HttpMethod.Put, "/Babelfin/Admin/Settings", user, AdminWithRule("^Skyward").ToJsonString())).Status == 403, "PUT Admin/Settings as a plain user: 403");

    var export = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Export", admin);
    Save("export.json", export.Body);
    var exportNode = export.Status == 200 ? Node(export) : new JsonObject();
    Check(export.Status == 200 && exportNode["admin"] is not null && exportNode["users"] is JsonArray { Count: >= 2 } && exportNode["schemaVersion"]!.GetValue<int>() == 1
        && (await Send(h, HttpMethod.Get, "/Babelfin/Admin/Export", user)).Status == 403 && !export.Text.Contains("Probe"),
        $"GET Admin/Export: 200 with admin and {(exportNode["users"] as JsonArray)?.Count} users, 403 for a plain user");
    var viewerOneEntry = exportNode["users"]!.AsArray().First(u => Guid.Parse(u!["userId"]!.GetValue<string>()) == viewerOne.Id)!;
    viewerOneEntry["everyday"]!["understood"] = new JsonArray("fr");
    var dry = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=true", admin, exportNode.ToJsonString());
    Save("import-dryrun.json", dry.Body);
    var meAfterDry = await Send(h, HttpMethod.Get, "/Babelfin/Me/Settings", user);
    Check(dry.Status == 200 && !Node(dry)["applied"]!.GetValue<bool>() && Node(dry)["users"]!.AsArray().Any(u => Guid.Parse(u!["userId"]!.GetValue<string>()) == viewerOne.Id && u["action"]!.GetValue<string>() == "update")
        && Node(meAfterDry)["settings"]!["everyday"]!["understood"]!.AsArray().Single()!.GetValue<string>() == "de",
        $"POST Admin/Import?dryRun=true: a preview (ViewerOne: update) and nothing changed ({dry.Status})");
    var apply = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, exportNode.ToJsonString());
    var meAfterApply = await Send(h, HttpMethod.Get, "/Babelfin/Me/Settings", user);
    Check(apply.Status == 200 && Node(apply)["applied"]!.GetValue<bool>() && Node(meAfterApply)["settings"]!["everyday"]!["understood"]!.AsArray().Single()!.GetValue<string>() == "fr",
        $"POST Admin/Import?dryRun=false: applied, the person's understood is now 'fr' ({apply.Status})");
    Check((await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=true", user, exportNode.ToJsonString())).Status == 403, "Import as a plain user: 403");

    var langs = await Send(h, HttpMethod.Get, "/Babelfin/Languages", user);
    Save("languages.json", langs.Body);
    var langs304 = await Send(h, HttpMethod.Get, "/Babelfin/Languages", new() { ["X-Fake-User"] = viewerOne.Id.ToString("N"), ["If-None-Match"] = langs.H("ETag") });
    Check(langs.Status == 200 && langs.Text.Contains("\"code\":\"ja\"") && langs.Text.Contains("\"code\":\"zh-Hant\"") && langs.Text.Contains("\"code\":\"yue\"") && langs304.Status == 304
        && (await Send(h, HttpMethod.Get, "/Babelfin/Languages")).Status == 401,
        $"GET Languages: 200 with ja, zh-Hant and yue; 304 on the ETag; 401 anonymous ({langs.Status}/{langs304.Status})");

    var adminDoc = (JsonObject)Node(otherAsAdmin).DeepClone();
    var viewerOneDoc = (JsonObject)Node(await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", admin)).DeepClone();
    viewerOneDoc["locks"] = new JsonObject { ["format"] = "TextOnly" };
    var lockPut = await Send(h, HttpMethod.Put, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", admin, viewerOneDoc.ToJsonString());
    var reset = await Send(h, HttpMethod.Post, "/Babelfin/Me/Reset", user);
    Save("me-reset.json", reset.Body);
    var resetNode = reset.Status == 200 ? Node(reset) : new JsonObject();
    Check(lockPut.Status == 200 && reset.Status == 200 && resetNode["locks"]!["format"]!.GetValue<string>() == "TextOnly" && resetNode["everyday"]!["understood"]!.AsArray().Single()!.GetValue<string>() == "en",
        $"an admin sets a lock through Admin/Users; POST Me/Reset re-seeds (understood back to the assumed 'en') and keeps the lock ({lockPut.Status}/{reset.Status})");
    Check((await Send(h, HttpMethod.Put, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", user, viewerOneDoc.ToJsonString())).Status == 403, "PUT Admin/Users/{id}/Settings as a plain user: 403");

    var flag = await Send(h, HttpMethod.Post, $"/Babelfin/Me/BurnedIn/{animeEpId:N}", user, "{ \"languages\": [ \"en\" ], \"scope\": \"Series\" }");
    var explainBurned = await Send(h, HttpMethod.Get, $"/Babelfin/Me/Explain?itemId={animeEpId:N}", user);
    Save("explain-burnedin.json", explainBurned.Body);
    var burnedNode = explainBurned.Status == 200 ? Node(explainBurned)![0]! : new JsonObject();
    Check(flag.Status == 200 && Guid.Parse(Node(flag)["itemId"]!.GetValue<string>()) == animeSeries.Id && burnedNode["babelfinSubtitleIndex"]!.GetValue<int>() == -1 && burnedNode["explanation"]!.GetValue<string>().Contains("burned"),
        $"POST Me/BurnedIn (series): stored under the series; Explain shows no subtitles because English is burned in ({flag.Status}: {burnedNode["explanation"]})");
    var unflag = await Send(h, HttpMethod.Delete, $"/Babelfin/Me/BurnedIn/{animeEpId:N}?scope=Series", user);
    var explainAgain = await Send(h, HttpMethod.Get, $"/Babelfin/Me/Explain?itemId={animeEpId:N}", user);
    var againNode = explainAgain.Status == 200 ? Node(explainAgain)?.AsArray().FirstOrDefault() : null;
    Check(unflag.Status == 204 && explainAgain.Status == 200 && againNode?["babelfinSubtitleIndex"] is null,
        $"DELETE the flag: nothing chosen, so the subtitles are the video's again (Babelfin sets none) ({explainAgain.Status}: {againNode?["explanation"]})");
    Check((await Send(h, HttpMethod.Post, $"/Babelfin/Me/BurnedIn/{animeEpId:N}", user, "{ \"languages\": [ \"en\" ], \"scope\": \"Library\" }")).Status == 403, "a library-wide flag through Me/BurnedIn: 403");
    Check((await Send(h, HttpMethod.Post, $"/Babelfin/Me/BurnedIn/{hiddenId:N}", user, "{ \"languages\": [ \"en\" ], \"scope\": \"Title\" }")).Status == 404, "a flag on a hidden item: 404");

    var statusAfter = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Status", admin);
    var stNode = Node(statusAfter);
    Check(statusAfter.Status == 200 && stNode["personFiles"]!.GetValue<int>() >= 2 && stNode["dataFolder"] is not null && stNode["lastUpgradeNote"] is null,
        "Admin/Status reports the data folder (admin only), the person file count and the upgrade note");
    foreach (var name in new[] { "me-settings.json", "me-settings-put.json", "effective-anime.json", "explain-override.json", "languages.json", "me-reset.json", "explain-burnedin.json" })
    {
        var saved = File.ReadAllText(Path.Combine(outDir, name));
        Check(NoPaths(saved), $"{name}: no file path in a non-admin response");
    }

    await h.DisposeAsync();
}

// ════════════════════════ J. Show and movie overrides ════════════════════════
Section("J. Show and movie overrides");
{
    var h = await StartAsync("plain");
    var user = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N") };
    var admin = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N"), ["X-Fake-Admin"] = "1" };
    JsonNode Node(Resp r) => JsonNode.Parse(r.Text)!;
    var personStore = h.App.Services.GetRequiredService<IPersonStore>();
    string StoredOverrides(Guid id) => JsonSerializer.Serialize(personStore.TryGet(id)?.Overrides ?? new List<ItemOverride>(), BabelfinJson.Options);
    int Entries(Resp activity, string text) => Node(activity).AsArray().Count(e => e!["text"]?.GetValue<string>() == text);
    async Task<Resp> Own(string url) => await Send(h, HttpMethod.Get, url, user);
    const string AudioEng = "{ \"audioChain\": [ { \"kind\": \"Language\", \"language\": \"eng\" } ] }";
    const string TextOnly = "{ \"format\": \"TextOnly\" }";

    // The admin's library choices: Anime plays Japanese, Movies Korean (so "Default" in the dialog is the library's, not the person's override).
    SetConfig(more: c =>
    {
        c.Libraries.Add(new LibraryDefault { LibraryId = animeFolder.Id.ToString("N"), Name = "Anime", Settings = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "jpn") } } });
        c.Libraries.Add(new LibraryDefault { LibraryId = moviesFolder.Id.ToString("N"), Name = "Movies", Settings = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "kor") } } });
    });
    var paperMoonId = Guid.NewGuid();
    lib.Add(new Movie { Id = paperMoonId, Name = "Paper Moon Ferry", ProductionYear = 2001, ParentId = moviesFolder.Id });
    var videoId = Guid.NewGuid();
    lib.Add(new Video { Id = videoId, Name = "Birthday 2019", ParentId = moviesFolder.Id });
    var looseId = Guid.NewGuid();
    lib.Add(new Episode { Id = looseId, Name = "Loose Episode", ParentId = moviesFolder.Id }); // an episode with no show
    var goneId = Guid.NewGuid(); // an item that was deleted from the library
    var specialsId = animeSeason.Id;
    ResetPersonFixture(personStore, viewerOne.Id);
    ResetPersonFixture(personStore, viewerTwo.Id);

    // ── GET Me/Overrides/Target/{itemId} ──
    var anonTarget = await Send(h, HttpMethod.Get, $"/Babelfin/Me/Overrides/Target/{paperMoonId:N}");
    Check(anonTarget.Status == 401, $"Overrides/Target without a user: 401 ({anonTarget.Status})");
    var tMovie = await Own($"/Babelfin/Me/Overrides/Target/{paperMoonId:N}");
    Save("override-target-movie.json", tMovie.Body);
    var tm = tMovie.Status == 200 ? Node(tMovie) : new JsonObject();
    Check(tMovie.Status == 200 && tm["targetId"]?.GetValue<string>() == paperMoonId.ToString("N") && tm["scope"]?.GetValue<string>() == "Title" && tm["name"]?.GetValue<string>() == "Paper Moon Ferry"
        && tm["type"]?.GetValue<string>() == "Movie" && tm["libraryId"]?.GetValue<string>() == moviesFolder.Id.ToString("N") && tm["libraryName"]?.GetValue<string>() == "Movies"
        && tm["override"] is null && tm["hasNarrower"]?.GetValue<bool>() == false,
        $"Target for a movie: itself, scope Title, its library, no override, nothing narrower ({tMovie.Status})");
    Check(tm["inherited"]?["profile"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "kor" && tm["inherited"]?["sources"]?["AudioChain"]?.GetValue<string>() == "the admin's Movies library defaults",
        $"Target for a movie: Inherited is the library's choice ('{tm["inherited"]?["sources"]?["AudioChain"]}', {tm["inherited"]?["profile"]?["audioChain"]?[0]?["language"]})");
    var tVideo = await Own($"/Babelfin/Me/Overrides/Target/{videoId:N}");
    var tv = tVideo.Status == 200 ? Node(tVideo) : new JsonObject();
    Check(tVideo.Status == 200 && tv["targetId"]?.GetValue<string>() == videoId.ToString("N") && tv["scope"]?.GetValue<string>() == "Title" && tv["type"]?.GetValue<string>() == "Video" && tv["libraryName"]?.GetValue<string>() == "Movies",
        $"Target for a video: itself, scope Title, type Video ({tVideo.Status})");
    var tSeries = await Own($"/Babelfin/Me/Overrides/Target/{animeSeries.Id:N}");
    Save("override-target-series.json", tSeries.Body);
    var ts = tSeries.Status == 200 ? Node(tSeries) : new JsonObject();
    Check(tSeries.Status == 200 && ts["targetId"]?.GetValue<string>() == animeSeries.Id.ToString("N") && ts["scope"]?.GetValue<string>() == "Series" && ts["name"]?.GetValue<string>() == "Skyward Lantern Voyage"
        && ts["type"]?.GetValue<string>() == "Series" && ts["libraryName"]?.GetValue<string>() == "Anime" && ts["libraryId"]?.GetValue<string>() == animeFolder.Id.ToString("N") && ts["override"] is null,
        $"Target for a show: itself, scope Series, the Anime library, no override ({tSeries.Status})");
    var tSeason = await Own($"/Babelfin/Me/Overrides/Target/{specialsId:N}");
    var tEpisode = await Own($"/Babelfin/Me/Overrides/Target/{animeEpId:N}");
    var tsn = tSeason.Status == 200 ? Node(tSeason) : new JsonObject();
    var te = tEpisode.Status == 200 ? Node(tEpisode) : new JsonObject();
    Check(tSeason.Status == 200 && tsn["targetId"]?.GetValue<string>() == animeSeries.Id.ToString("N") && tsn["scope"]?.GetValue<string>() == "Series" && tsn["name"]?.GetValue<string>() == "Skyward Lantern Voyage" && tsn["type"]?.GetValue<string>() == "Series",
        $"Target for a season: its show, scope Series, the show's name and type ({tSeason.Status})");
    Check(tEpisode.Status == 200 && te["targetId"]?.GetValue<string>() == animeSeries.Id.ToString("N") && te["scope"]?.GetValue<string>() == "Series" && te["name"]?.GetValue<string>() == "Skyward Lantern Voyage"
        && te["libraryName"]?.GetValue<string>() == "Anime" && te["inherited"]?["profile"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "jpn",
        $"Target for an episode: its show, scope Series, the show's library and library choice ({tEpisode.Status})");
    var tTv = await Own($"/Babelfin/Me/Overrides/Target/{slvId:N}");
    Check(tTv.Status == 200 && Node(tTv)["targetId"]?.GetValue<string>() == tvSeries.Id.ToString("N") && Node(tTv)["libraryName"]?.GetValue<string>() == "TV Shows",
        $"Target for an episode in another library: its own show and that show's library ({tTv.Status})");

    var tLoose = await Own($"/Babelfin/Me/Overrides/Target/{looseId:N}");
    var tSong = await Own($"/Babelfin/Me/Overrides/Target/{songId:N}");
    var tFolder = await Own($"/Babelfin/Me/Overrides/Target/{animeFolder.Id:N}");
    Check(tLoose.Status == 400 && tSong.Status == 400 && tFolder.Status == 400 && Node(tSong)["message"] is not null && NoPaths(tSong.Text),
        $"Target for an episode with no show, a song, a library: 400 with a message ({tLoose.Status}/{tSong.Status}/{tFolder.Status})");
    var tUnknown = await Own($"/Babelfin/Me/Overrides/Target/{Guid.NewGuid():N}");
    var tHidden = await Own($"/Babelfin/Me/Overrides/Target/{hiddenId:N}");
    var tEmpty = await Own($"/Babelfin/Me/Overrides/Target/{Guid.Empty:N}");
    Check(tUnknown.Status == 404 && tHidden.Status == 404 && tEmpty.Status == 404, $"Target for an unknown item, one the person can't see, an empty id: 404 ({tUnknown.Status}/{tHidden.Status}/{tEmpty.Status})");

    // An override on the show: Target shows it, and Inherited is still what applies without it (the library's choice, not eng).
    var putMovie = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{paperMoonId:N}?scope=Title", user, AudioEng);
    var putShow = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{animeEpId:N}?scope=Series", user, AudioEng);
    var tShow2 = await Own($"/Babelfin/Me/Overrides/Target/{specialsId:N}");
    var tw = tShow2.Status == 200 ? Node(tShow2) : new JsonObject();
    Check(putMovie.Status == 200 && putShow.Status == 200 && tw["override"]?["itemId"]?.GetValue<string>() == animeSeries.Id.ToString("N") && tw["override"]?["scope"]?.GetValue<string>() == "Series"
        && tw["override"]?["settings"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "eng",
        $"Target (from a season) returns the show's stored override ({tShow2.Status})");
    Check(tw["inherited"]?["profile"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "jpn" && tw["inherited"]?["sources"]?["AudioChain"]?.GetValue<string>() == "the admin's Anime library defaults",
        $"Inherited ignores the override on the target: still the library's Japanese ('{tw["inherited"]?["sources"]?["AudioChain"]}')");
    var effWithOverride = Node(await Own($"/Babelfin/Me/Effective?itemId={animeEpId:N}"));
    Check(effWithOverride["sources"]?["AudioChain"]?.GetValue<string>() == "your settings for this show" && effWithOverride["profile"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "eng",
        "(while playback does use the override: eng, 'your settings for this show')");
    var tMovie2 = Node(await Own($"/Babelfin/Me/Overrides/Target/{paperMoonId:N}"));
    Check(tMovie2["override"]?["settings"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "eng" && tMovie2["inherited"]?["profile"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "kor",
        "Target for a movie with an override: the stored override and, apart from it, the library's Korean");

    // HasNarrower: a season or title override on a season or an episode of this show; episodes of other shows and show overrides don't count.
    Check(ts["hasNarrower"]?.GetValue<bool>() == false && Node(tShow2)["hasNarrower"]?.GetValue<bool>() == false, "HasNarrower is false while the person only has an override on the show itself");
    await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{slvId:N}?scope=Title", user, TextOnly);
    var otherShowNarrower = Node(await Own($"/Babelfin/Me/Overrides/Target/{animeEpId:N}"))["hasNarrower"]?.GetValue<bool>();
    var tvShowNarrower = Node(await Own($"/Babelfin/Me/Overrides/Target/{tvSeason.Id:N}"))["hasNarrower"]?.GetValue<bool>();
    Check(otherShowNarrower == false && tvShowNarrower == true, $"HasNarrower: an episode override counts for its own show ({tvShowNarrower}), not for another show ({otherShowNarrower})");
    await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{animeEpId:N}?scope=Title", user, TextOnly);
    await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{specialsId:N}?scope=Season", user, TextOnly);
    var narrowBoth = Node(await Own($"/Babelfin/Me/Overrides/Target/{animeSeries.Id:N}"))["hasNarrower"]?.GetValue<bool>();
    await Send(h, HttpMethod.Delete, $"/Babelfin/Me/Overrides/{animeEpId:N}?scope=Title", user);
    var narrowSeasonOnly = Node(await Own($"/Babelfin/Me/Overrides/Target/{animeEpId:N}"))["hasNarrower"]?.GetValue<bool>();
    await Send(h, HttpMethod.Delete, $"/Babelfin/Me/Overrides/{specialsId:N}?scope=Season", user);
    var narrowNone = Node(await Own($"/Babelfin/Me/Overrides/Target/{animeSeries.Id:N}"))["hasNarrower"]?.GetValue<bool>();
    Check(narrowBoth == true && narrowSeasonOnly == true && narrowNone == false, $"HasNarrower: true with a title and a season override ({narrowBoth}), still true with the season's alone ({narrowSeasonOnly}), false when both are gone ({narrowNone})");
    Check(Node(await Own($"/Babelfin/Me/Overrides/Target/{paperMoonId:N}"))["hasNarrower"]?.GetValue<bool>() == false, "HasNarrower is always false for a movie");

    // ── GET Me/Overrides: the list ──
    await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{animeEpId:N}?scope=Title", user, TextOnly);
    await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{specialsId:N}?scope=Season", user, TextOnly);
    var rememberedAt = new DateTime(2026, 10, 20, 21, 5, 0, DateTimeKind.Utc);
    var stored = personStore.TryGet(viewerOne.Id)!.Clone();
    stored.FindOverride(paperMoonId)!.Manual = new ManualMemory { Audio = new RememberedTrack { Language = "ja", Kind = "commentary" }, SubtitlesOff = true, At = rememberedAt, Client = "Jellyfin Web" };
    stored.Overrides.Add(new ItemOverride { ItemId = goneId.ToString("N"), Scope = OverrideScope.Series, Name = "Cancelled Show", Settings = new SettingsLayer { Format = FormatPreference.TextOnly }, UpdatedAt = rememberedAt });
    stored.Overrides.Add(new ItemOverride { ItemId = hiddenId.ToString("N"), Scope = OverrideScope.Title, Name = "Hidden Movie", Manual = new ManualMemory { SubtitlesOff = true, At = rememberedAt }, UpdatedAt = rememberedAt });
    personStore.Save(stored);
    var anonList = await Send(h, HttpMethod.Get, "/Babelfin/Me/Overrides");
    var list = await Own("/Babelfin/Me/Overrides");
    Save("me-overrides.json", list.Body);
    var rows = list.Status == 200 ? Node(list).AsArray() : new JsonArray();
    JsonNode? Row(Guid id) => rows.FirstOrDefault(r => r?["itemId"]?.GetValue<string>() == id.ToString("N"));
    Check(anonList.Status == 401 && list.Status == 200 && rows.Count == 7 && NoPaths(list.Text),
        $"GET Me/Overrides: 401 anonymous; 200 with the person's {rows.Count} overrides (explicit and remembered, any scope), no file path ({anonList.Status}/{list.Status})");
    Check(Row(paperMoonId) is { } rMovie && rMovie["scope"]?.GetValue<string>() == "Title" && rMovie["name"]?.GetValue<string>() == "Paper Moon Ferry" && rMovie["type"]?.GetValue<string>() == "Movie" && rMovie["productionYear"]?.GetValue<int>() == 2001
        && rMovie["seriesName"] is null && rMovie["libraryName"]?.GetValue<string>() == "Movies" && rMovie["libraryId"]?.GetValue<string>() == moviesFolder.Id.ToString("N"),
        "a movie row: its current name and year, type Movie, scope Title, library Movies");
    Check(Row(paperMoonId)?["settings"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "eng" && Row(paperMoonId)?["manual"]?["audio"]?["language"]?.GetValue<string>() == "ja" && Row(paperMoonId)?["manual"]?["audio"]?["kind"]?.GetValue<string>() == "commentary"
        && Row(paperMoonId)?["manual"]?["subtitlesOff"]?.GetValue<bool>() == true && Row(paperMoonId)?["updatedAt"] is not null,
        "the row carries the explicit settings and the remembered track change as stored, and the time");
    Check(Row(animeSeries.Id) is { } rShow && rShow["scope"]?.GetValue<string>() == "Series" && rShow["type"]?.GetValue<string>() == "Series" && rShow["name"]?.GetValue<string>() == "Skyward Lantern Voyage" && rShow["libraryName"]?.GetValue<string>() == "Anime" && rShow["seriesName"] is null,
        "a show row: scope Series, type Series, library Anime");
    Check(Row(animeEpId) is { } rEp && rEp["scope"]?.GetValue<string>() == "Title" && rEp["type"]?.GetValue<string>() == "Episode" && rEp["seriesName"]?.GetValue<string>() == "Skyward Lantern Voyage" && rEp["libraryName"]?.GetValue<string>() == "Anime",
        "an episode row: type Episode, the show's name, the show's library");
    Check(Row(specialsId) is { } rSeason && rSeason["scope"]?.GetValue<string>() == "Season" && rSeason["type"]?.GetValue<string>() == "Season" && rSeason["seriesName"]?.GetValue<string>() == "Skyward Lantern Voyage" && rSeason["libraryName"]?.GetValue<string>() == "Anime",
        "a season row: scope Season, type Season, the show's name, the show's library");
    Check(Row(goneId) is { } rGone && rGone["name"]?.GetValue<string>() == "Cancelled Show" && rGone["type"] is null && rGone["libraryId"] is null && rGone["libraryName"] is null && rGone["productionYear"] is null && rGone["settings"]?["format"]?.GetValue<string>() == "TextOnly",
        "an item that is gone: the stored name, no type, no library; its settings still shown");
    Check(Row(hiddenId) is { } rHidden && rHidden["name"]?.GetValue<string>() == "Hidden Movie" && rHidden["type"] is null && rHidden["libraryName"] is null && rHidden["manual"]?["subtitlesOff"]?.GetValue<bool>() == true,
        "an item the person can no longer see counts as gone (the current item is not revealed); a remembered-only entry has empty settings and its track change");
    Check(Row(slvId) is { } rTv && rTv["libraryName"]?.GetValue<string>() == "TV Shows", "every row has its own library (a title in TV Shows reads TV Shows)");

    // A row for an item that is gone can still be removed (by its own id), like any other.
    var removeGone = await Send(h, HttpMethod.Delete, $"/Babelfin/Me/Overrides/{goneId:N}?scope=Series", user);
    var removeHidden = await Send(h, HttpMethod.Delete, $"/Babelfin/Me/Overrides/{hiddenId:N}?scope=Title", user);
    var removeUnknown = await Send(h, HttpMethod.Delete, $"/Babelfin/Me/Overrides/{Guid.NewGuid():N}?scope=Title", user);
    var afterRemoves = personStore.TryGet(viewerOne.Id)!;
    Check(removeGone.Status == 204 && removeHidden.Status == 204 && afterRemoves.FindOverride(goneId) is null && afterRemoves.FindOverride(hiddenId) is null && afterRemoves.Overrides.Count == 5 && removeUnknown.Status == 404,
        $"DELETE Me/Overrides/{{id}} for an item that is gone or hidden removes the stored entry (204); for an unknown id with nothing stored it is still 404 ({removeGone.Status}/{removeHidden.Status}/{removeUnknown.Status})");

    // ── PUT Me/Settings?keepOverrides=true ──
    var page = Node(await Own("/Babelfin/Me/Settings"))["settings"]!.DeepClone(); // what a page loaded a moment ago holds
    Check(page["overrides"]!.AsArray().Count == 5, "fixture: the page's copy of the document holds the five overrides");
    var beforeKeep = StoredOverrides(viewerOne.Id);
    var stale = (JsonObject)page.DeepClone();
    stale["overrides"] = new JsonArray();
    stale["everyday"]!["understood"] = new JsonArray("de");
    var keep = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true", user, stale.ToJsonString());
    Check(keep.Status == 200 && StoredOverrides(viewerOne.Id) == beforeKeep && personStore.TryGet(viewerOne.Id)!.Everyday.Understood!.SequenceEqual(new[] { "de" }) && personStore.TryGet(viewerOne.Id)!.FindOverride(paperMoonId)!.Manual!.At == rememberedAt,
        $"PUT Me/Settings?keepOverrides=true with a body that has no overrides: the stored ones stay exactly as they were (remembered change and times too); the rest is saved ({keep.Status})");
    var junk = (JsonObject)page.DeepClone();
    junk["overrides"] = new JsonArray(new JsonObject { ["itemId"] = hiddenId.ToString("N"), ["scope"] = "Title", ["settings"] = new JsonObject() }, new JsonObject { ["itemId"] = "not an id", ["scope"] = "Library" });
    var junkKeep = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true", user, junk.ToJsonString());
    var junkPlain = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, junk.ToJsonString());
    Check(junkKeep.Status == 200 && StoredOverrides(viewerOne.Id) == beforeKeep && junkPlain.Status == 400 && StoredOverrides(viewerOne.Id) == beforeKeep,
        $"keepOverrides=true doesn't validate or access-check the body's overrides (200); without it the same body is a 400 and nothing changes ({junkKeep.Status}/{junkPlain.Status})");

    var removedMeanwhile = await Send(h, HttpMethod.Delete, $"/Babelfin/Me/Overrides/{paperMoonId:N}?scope=Title", user);
    var addedMeanwhile = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{videoId:N}?scope=Title", user, AudioEng);
    var savedStale = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true", user, page.ToJsonString());
    var afterStale = personStore.TryGet(viewerOne.Id)!;
    Check(removedMeanwhile.Status == 204 && addedMeanwhile.Status == 200 && savedStale.Status == 200 && afterStale.FindOverride(paperMoonId) is null && afterStale.FindOverride(videoId) is not null && afterStale.Overrides.Count == 5,
        $"a stale page saved with keepOverrides=true neither brings back the override removed meanwhile nor drops the one added meanwhile ({savedStale.Status})");
    var stalePlain = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, page.ToJsonString());
    var afterPlain = personStore.TryGet(viewerOne.Id)!;
    Check(stalePlain.Status == 200 && afterPlain.FindOverride(paperMoonId) is not null && afterPlain.FindOverride(videoId) is null,
        $"without the flag nothing changed: the same stale body brings the removed override back and drops the new one ({stalePlain.Status})");

    // ── DELETE Me/Overrides ──
    var overridesBeforeAll = personStore.TryGet(viewerOne.Id)!.Overrides.Count;
    var viewerOneBefore = personStore.TryGet(viewerOne.Id)!;
    await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, Node(await Own("/Babelfin/Me/Settings"))["settings"]!.ToJsonString()); // keeps what is stored
    var activityBefore = Entries(await Own("/Babelfin/Me/Activity?limit=200"), "You removed all your show and movie overrides");
    var meDeleteAnon = await Send(h, HttpMethod.Delete, "/Babelfin/Me/Overrides/All");
    var meDelete = await Send(h, HttpMethod.Delete, "/Babelfin/Me/Overrides/All", user);
    var viewerOneAfter = personStore.TryGet(viewerOne.Id)!;
    var emptyList = await Own("/Babelfin/Me/Overrides");
    var activityAfter = Entries(await Own("/Babelfin/Me/Activity?limit=200"), "You removed all your show and movie overrides");
    Check(overridesBeforeAll >= 5 && meDeleteAnon.Status == 401 && meDelete.Status == 204 && viewerOneAfter.Overrides.Count == 0 && emptyList.Text.Trim() == "[]"
        && viewerOneAfter.Everyday.Understood!.SequenceEqual(viewerOneBefore.Everyday.Understood!) && viewerOneAfter.Locks.Format == viewerOneBefore.Locks.Format && viewerOneAfter.Seed?.Confirmed == viewerOneBefore.Seed?.Confirmed,
        $"DELETE Me/Overrides: 401 anonymous; 204 and all {overridesBeforeAll} overrides gone, explicit and remembered; the rest of the document is as it was ({meDeleteAnon.Status}/{meDelete.Status})");
    var meDeleteAgain = await Send(h, HttpMethod.Delete, "/Babelfin/Me/Overrides/All", user);
    var activityAgain = Entries(await Own("/Babelfin/Me/Activity?limit=200"), "You removed all your show and movie overrides");
    Check(activityAfter == activityBefore + 1 && meDeleteAgain.Status == 204 && activityAgain == activityAfter,
        $"one activity entry 'You removed all your show and movie overrides', and none when nothing was removed ({activityBefore} -> {activityAfter} -> {activityAgain})");
    Check(Node(await Own("/Babelfin/Me/Overrides/Target/" + paperMoonId.ToString("N")))["override"] is null, "after removing all, Target shows no override");

    // ── Admin: GET / DELETE Admin/Users/{userId}/Overrides ──
    var viewerTwoDoc = (JsonObject)Node(await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", admin));
    viewerTwoDoc["overrides"] = new JsonArray(
        new JsonObject { ["itemId"] = paperMoonId.ToString("N"), ["scope"] = "Title", ["name"] = "Paper Moon Ferry", ["settings"] = JsonNode.Parse(AudioEng) },
        new JsonObject { ["itemId"] = animeSeries.Id.ToString("N"), ["scope"] = "Series", ["name"] = "Skyward Lantern Voyage", ["settings"] = JsonNode.Parse(TextOnly) });
    var viewerTwoPut = await Send(h, HttpMethod.Put, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", admin, viewerTwoDoc.ToJsonString());
    var viewerTwoStored = personStore.TryGet(viewerTwo.Id)!.Clone();
    viewerTwoStored.FindOverride(animeSeries.Id)!.Manual = new ManualMemory { Audio = new RememberedTrack { Language = "ja" }, At = rememberedAt };
    viewerTwoStored.Overrides.Add(new ItemOverride { ItemId = goneId.ToString("N"), Scope = OverrideScope.Series, Name = "Cancelled Show", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    personStore.Save(viewerTwoStored);
    var viewerOneSeparate = Node(await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{videoId:N}?scope=Title", user, AudioEng));
    var aList = await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides", admin);
    Save("admin-overrides.json", aList.Body);
    var aRows = aList.Status == 200 ? Node(aList).AsArray() : new JsonArray();
    Check(viewerTwoPut.Status == 200 && aList.Status == 200 && aRows.Count == 3 && aRows.Any(r => r!["itemId"]!.GetValue<string>() == paperMoonId.ToString("N") && r["name"]!.GetValue<string>() == "Paper Moon Ferry" && r["libraryName"]!.GetValue<string>() == "Movies")
        && aRows.Any(r => r!["itemId"]!.GetValue<string>() == animeSeries.Id.ToString("N") && r["libraryName"]!.GetValue<string>() == "Anime" && r["manual"]?["audio"]?["language"]?.GetValue<string>() == "ja")
        && aRows.Any(r => r!["itemId"]!.GetValue<string>() == goneId.ToString("N") && r["name"]!.GetValue<string>() == "Cancelled Show" && r["libraryName"] is null) && NoPaths(aList.Text),
        $"GET Admin/Users/{{id}}/Overrides: the person's 3 rows (names, libraries, remembered change), not another person's ({aList.Status}: {aRows.Count} rows)");
    var viewerOneStill = await Own("/Babelfin/Me/Overrides");
    Check(Node(viewerOneStill).AsArray().Count == 1 && viewerOneSeparate["scope"]?.GetValue<string>() == "Title", "the admin's list of one person doesn't include anyone else's overrides");
    var forbiddenList = await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides", user);
    var forbiddenAll = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/All", user);
    var forbiddenOne = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/{paperMoonId:N}?scope=Title", user);
    var anonAdminList = await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides");
    Check(forbiddenList.Status == 403 && forbiddenAll.Status == 403 && forbiddenOne.Status == 403 && anonAdminList.Status == 401 && personStore.TryGet(viewerTwo.Id)!.Overrides.Count == 3,
        $"the admin endpoints answer 403 to a plain user (list/delete all/delete one: {forbiddenList.Status}/{forbiddenAll.Status}/{forbiddenOne.Status}) and 401 anonymously ({anonAdminList.Status}); nothing was removed");
    var unknownUser = Guid.NewGuid();
    var u1 = await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{unknownUser:N}/Overrides", admin);
    var u2 = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{unknownUser:N}/Overrides/All", admin);
    var u3 = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{unknownUser:N}/Overrides/{paperMoonId:N}?scope=Title", admin);
    Check(u1.Status == 404 && u2.Status == 404 && u3.Status == 404, $"an unknown user: 404 for the list, delete all and delete one ({u1.Status}/{u2.Status}/{u3.Status})");

    // PUT Admin/Users/{userId}/Settings?keepOverrides=true
    var viewerTwoBeforeKeep = StoredOverrides(viewerTwo.Id);
    var adminPage = (JsonObject)Node(await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", admin));
    var adminStale = (JsonObject)adminPage.DeepClone();
    adminStale["overrides"] = new JsonArray(new JsonObject { ["itemId"] = Guid.NewGuid().ToString("N"), ["scope"] = "Title", ["settings"] = new JsonObject() });
    adminStale["locks"] = new JsonObject { ["format"] = "TextOnly" };
    var adminKeep = await Send(h, HttpMethod.Put, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings?keepOverrides=true", admin, adminStale.ToJsonString());
    Check(adminKeep.Status == 200 && StoredOverrides(viewerTwo.Id) == viewerTwoBeforeKeep && personStore.TryGet(viewerTwo.Id)!.Locks.Format == FormatPreference.TextOnly && personStore.TryGet(viewerTwo.Id)!.FindOverride(animeSeries.Id)!.Manual is not null,
        $"PUT Admin/Users/{{id}}/Settings?keepOverrides=true: the stored overrides stay exactly (the body's, which names an item that doesn't exist, is ignored); the lock in the body is saved ({adminKeep.Status})");
    var adminPlain = await Send(h, HttpMethod.Put, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", admin, adminStale.ToJsonString());
    Check(adminPlain.Status == 400 && StoredOverrides(viewerTwo.Id) == viewerTwoBeforeKeep, $"without the flag the same body is a 400 and nothing changes ({adminPlain.Status})");
    var adminPlainOk = (JsonObject)adminPage.DeepClone();
    adminPlainOk["overrides"] = new JsonArray();
    var adminBodyWins = await Send(h, HttpMethod.Put, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", admin, adminPlainOk.ToJsonString());
    var viewerTwoAfterPlain = personStore.TryGet(viewerTwo.Id)!;
    Check(adminBodyWins.Status == 200 && viewerTwoAfterPlain.Overrides.Count == 1 && viewerTwoAfterPlain.Overrides[0].Settings.IsEmpty && viewerTwoAfterPlain.Overrides[0].Manual is not null,
        $"without the flag the body wins as before: only the entry that holds a remembered change is left, with its settings cleared ({adminBodyWins.Status})");

    // DELETE one
    viewerTwoStored = personStore.TryGet(viewerTwo.Id)!.Clone();
    viewerTwoStored.Overrides.Clear();
    viewerTwoStored.Overrides.Add(new ItemOverride { ItemId = paperMoonId.ToString("N"), Scope = OverrideScope.Title, Name = "Paper Moon Ferry (old name)", Settings = JsonSerializer.Deserialize<SettingsLayer>(AudioEng, BabelfinJson.Options)! });
    viewerTwoStored.Overrides.Add(new ItemOverride { ItemId = animeSeries.Id.ToString("N"), Scope = OverrideScope.Series, Name = "Skyward Lantern Voyage", Settings = new SettingsLayer { Format = FormatPreference.TextOnly }, Manual = new ManualMemory { Audio = new RememberedTrack { Language = "ja" }, At = rememberedAt } });
    viewerTwoStored.Overrides.Add(new ItemOverride { ItemId = goneId.ToString("N"), Scope = OverrideScope.Series, Name = "Cancelled Show", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    personStore.Save(viewerTwoStored);
    var viewerTwoActivity = $"/Babelfin/Admin/Activity?userId={viewerTwo.Id:N}&limit=500";
    var one1 = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/{paperMoonId:N}?scope=Title", admin);
    var left1 = personStore.TryGet(viewerTwo.Id)!;
    Check(one1.Status == 204 && left1.FindOverride(paperMoonId) is null && left1.Overrides.Count == 2 && left1.FindOverride(animeSeries.Id) is not null,
        $"DELETE Admin/Users/{{id}}/Overrides/{{itemId}}?scope=Title: 204, only that override is gone ({one1.Status})");
    var one2 = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/{animeSeries.Id:N}?scope=Series", admin);
    var one3 = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/{goneId:N}?scope=Series", admin);
    var one4 = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/{goneId:N}?scope=Series", admin);
    var left2 = personStore.TryGet(viewerTwo.Id)!;
    var viewerTwoLog = await Send(h, HttpMethod.Get, viewerTwoActivity, admin);
    Check(one2.Status == 204 && one3.Status == 204 && one4.Status == 204 && left2.Overrides.Count == 0,
        $"the whole override goes, remembered track change included; an item that is gone is removed by its id; a second delete is a plain 204 ({one2.Status}/{one3.Status}/{one4.Status})");
    Check(Entries(viewerTwoLog, "An admin removed your override for Paper Moon Ferry") == 1 && Entries(viewerTwoLog, "An admin removed your override for Skyward Lantern Voyage") == 1 && Entries(viewerTwoLog, "An admin removed your override for Cancelled Show") == 1
        && Node(viewerTwoLog).AsArray().All(e => e!["userId"]?.GetValue<string>() == viewerTwo.Id.ToString("D") || e["userId"]?.GetValue<string>() == viewerTwo.Id.ToString("N")),
        "activity for the person: 'An admin removed your override for {name}' once each (the current name; the stored one for an item that is gone), none for the second delete");

    // DELETE all
    viewerTwoStored = personStore.TryGet(viewerTwo.Id)!.Clone();
    viewerTwoStored.Overrides.Add(new ItemOverride { ItemId = paperMoonId.ToString("N"), Scope = OverrideScope.Title, Name = "Paper Moon Ferry", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    viewerTwoStored.Overrides.Add(new ItemOverride { ItemId = animeSeries.Id.ToString("N"), Scope = OverrideScope.Series, Name = "Skyward Lantern Voyage", Manual = new ManualMemory { SubtitlesOff = true, At = rememberedAt } });
    viewerTwoStored.Overrides.Add(new ItemOverride { ItemId = goneId.ToString("N"), Scope = OverrideScope.Series, Name = "Cancelled Show", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    viewerTwoStored.BurnedIn.Add(new BurnedInFlag { ItemId = paperMoonId.ToString("N"), Scope = OverrideScope.Title, Languages = new List<string> { "en" }, SetBy = viewerTwo.Id.ToString("N") });
    personStore.Save(viewerTwoStored);
    var viewerOneOverridesNow = personStore.TryGet(viewerOne.Id)!.Overrides.Count;
    var allBefore = Entries(viewerTwoLog, "An admin removed all your show and movie overrides");
    var allDelete = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/All", admin);
    var viewerTwoNow = personStore.TryGet(viewerTwo.Id)!;
    var allAgain = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/All", admin);
    var viewerTwoLog2 = await Send(h, HttpMethod.Get, viewerTwoActivity, admin);
    var emptyAdminList = await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides", admin);
    Check(allDelete.Status == 204 && allAgain.Status == 204 && viewerTwoNow.Overrides.Count == 0 && viewerTwoNow.BurnedIn.Count == 1 && emptyAdminList.Text.Trim() == "[]" && personStore.TryGet(viewerOne.Id)!.Overrides.Count == viewerOneOverridesNow,
        $"DELETE Admin/Users/{{id}}/Overrides: 204, every override of that person gone (explicit and remembered), their other settings and other people's overrides untouched ({allDelete.Status}/{allAgain.Status})");
    Check(allBefore == 0 && Entries(viewerTwoLog2, "An admin removed all your show and movie overrides") == 1,
        "activity for the person: 'An admin removed all your show and movie overrides' once, and not again when nothing was left");
    var viewerTwoOwnActivity = Node(await Send(h, HttpMethod.Get, "/Babelfin/Me/Activity?limit=200", new() { ["X-Fake-User"] = viewerTwo.Id.ToString("N") }));
    Check(viewerTwoOwnActivity.AsArray().Any(e => e!["text"]?.GetValue<string>() == "An admin removed all your show and movie overrides") && viewerTwoOwnActivity.AsArray().Any(e => e!["text"]?.GetValue<string>() == "An admin removed your override for Paper Moon Ferry"),
        "the person sees both entries in their own activity");
    Check(NoPaths(viewerTwoLog2.Text), "the activity entries carry no file path");

    // Back to where the next section expects to find them: each person has a freshly seeded document.
    ResetPersonFixture(personStore, viewerOne.Id);
    ResetPersonFixture(personStore, viewerTwo.Id);
    await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", admin);
    await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", admin);
    SetConfig();
    await h.DisposeAsync();
}

// ════════════════════════ K. Show and movie overrides: deletion and concurrent saves ════════════════════════
Section("K. Show and movie overrides: deletion and concurrent saves");
{
    var user = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N") };
    var admin = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N"), ["X-Fake-Admin"] = "1" };
    JsonNode Node(Resp r) => JsonNode.Parse(r.Text)!;
    int Entries(Resp activity, string text) => Node(activity).AsArray().Count(e => e!["text"]?.GetValue<string>() == text);
    int Added(Resp before, Resp after, string text) => Entries(after, text) - Entries(before, text); // the activity log is kept between sections, so each check counts what it added
    string Audio(JsonNode target) => target["inherited"]?["profile"]?["audioChain"]?[0]?["language"]?.GetValue<string>() ?? "(none)";
    string From(JsonNode target, string field) => target["inherited"]?["sources"]?[field]?.GetValue<string>() ?? "(none)";
    const string AudioEng = "{ \"audioChain\": [ { \"kind\": \"Language\", \"language\": \"eng\" } ] }";
    var rememberedAt = new DateTime(2026, 10, 20, 21, 5, 0, DateTimeKind.Utc);
    var remembered = () => new ManualMemory { Audio = new RememberedTrack { Language = "ja" }, SubtitlesOff = true, At = rememberedAt };

    // The admin's library choices, as in J: Anime plays Japanese, Movies Korean.
    SetConfig(more: c =>
    {
        c.Libraries.Add(new LibraryDefault { LibraryId = animeFolder.Id.ToString("N"), Name = "Anime", Settings = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "jpn") } } });
        c.Libraries.Add(new LibraryDefault { LibraryId = moviesFolder.Id.ToString("N"), Name = "Movies", Settings = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "kor") } } });
    });
    var h = await StartAsync("plain");
    var personStore = h.App.Services.GetRequiredService<IPersonStore>();
    var membership = ((FakeCollectionManager)(object)collections).Membership;
    async Task<Resp> Own(string url) => await Send(h, HttpMethod.Get, url, user);
    async Task Fresh() { ResetPersonFixture(personStore, viewerOne.Id); await Own("/Babelfin/Me/Settings"); } // a freshly seeded document
    Task<Resp> Delete(string url) => Send(h, HttpMethod.Delete, url, user);
    Task<Resp> Activity() => Own("/Babelfin/Me/Activity?limit=200");
    var copperKiteId = Guid.NewGuid();
    lib.Add(new Movie { Id = copperKiteId, Name = "Copper Kite", ProductionYear = 1992, ParentId = moviesFolder.Id });
    var clipId = Guid.NewGuid();
    lib.Add(new Video { Id = clipId, Name = "Birthday clip", ParentId = moviesFolder.Id });
    var favourites = new BoxSet { Id = Guid.NewGuid(), Name = "Anime favourites" };
    lib.Add(favourites);
    membership[animeSeries.Id] = new List<BoxSet> { favourites };
    var viewerThreeFile = Path.Combine(usersFolder, viewerThree.Id.ToString("N") + ".json");
    await Fresh();

    // ── "Default" in the dialog: every layer but the target's own entry still counts ──
    var doc = personStore.TryGet(viewerOne.Id)!.Clone();
    doc.Overrides.Add(new ItemOverride { ItemId = animeSeries.Id.ToString("N"), Scope = OverrideScope.Series, Name = "Skyward Lantern Voyage", Settings = JsonSerializer.Deserialize<SettingsLayer>(AudioEng, BabelfinJson.Options)! });
    doc.Overrides.Add(new ItemOverride { ItemId = favourites.Id.ToString("N"), Scope = OverrideScope.Collection, Name = "Anime favourites", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    doc.Overrides.Add(new ItemOverride { ItemId = slvId.ToString("N"), Scope = OverrideScope.Title, Name = "Another show's episode", Settings = new SettingsLayer { PrefersSdh = true } });
    doc.Libraries.Add(new LibraryLayer { LibraryId = animeFolder.Id.ToString("N"), Name = "Anime", Settings = new SettingsLayer { UsesAudioDescription = true } });
    personStore.Save(doc);
    var kShow = Node(await Own($"/Babelfin/Me/Overrides/Target/{animeSeries.Id:N}"));
    Check(Audio(kShow) == "jpn" && From(kShow, "AudioChain") == "the admin's Anime library defaults" && kShow["override"]?["settings"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "eng",
        $"Inherited for a show with its own override: the library's Japanese, not the override's English ('{From(kShow, "AudioChain")}')");
    Check(From(kShow, "Format") == "your settings for the Anime favourites collection" && From(kShow, "UsesAudioDescription") == "your Anime library settings",
        $"Inherited still counts the person's other overrides and layers: a collection of the show ('{From(kShow, "Format")}') and the person's library layer ('{From(kShow, "UsesAudioDescription")}')");
    Check(kShow["inherited"]?["profile"]?["prefersSdh"]?.GetValue<bool>() == false && From(kShow, "PrefersSdh") == StarterRules.DefaultsLabel,
        "Inherited ignores the override of another item (an episode of another show)");

    await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{animeEpId:N}?scope=Title", user, "{ \"audioChain\": [ { \"kind\": \"Language\", \"language\": \"kor\" } ] }");
    var kFromEpisode = Node(await Own($"/Babelfin/Me/Overrides/Target/{animeEpId:N}"));
    var epEffective = Node(await Own($"/Babelfin/Me/Effective?itemId={animeEpId:N}"));
    Check(kFromEpisode["targetId"]?.GetValue<string>() == animeSeries.Id.ToString("N") && Audio(kFromEpisode) == "jpn" && kFromEpisode["override"]?["itemId"]?.GetValue<string>() == animeSeries.Id.ToString("N")
        && kFromEpisode["hasNarrower"]?.GetValue<bool>() == true && epEffective["profile"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "kor",
        $"Target opened from an episode that has its own override: the show's Default ('{Audio(kFromEpisode)}', not the episode's Korean), the show's override, HasNarrower true (the episode itself plays Korean)");

    // Hidden content-type rules no longer participate; show/episode inherit the same visible library choice.
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    doc.Overrides.RemoveAll(o => PersonSettings.SameId(o.ItemId, animeEpId) || PersonSettings.SameId(o.ItemId, animeSeries.Id));
    doc.SmartRules.Add(new SmartRule { Id = "shows", Name = "Shows", Conditions = new List<RuleCondition> { new() { Kind = ConditionKind.ContentType, Values = new List<string> { "episode" } } }, Settings = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "deu") } } });
    doc.SmartRules.Add(new SmartRule { Id = "films", Name = "Films", Conditions = new List<RuleCondition> { new() { Kind = ConditionKind.ContentType, Values = new List<string> { "movie" } } }, Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    personStore.Save(doc);
    var ruleShow = Node(await Own($"/Babelfin/Me/Overrides/Target/{animeSeries.Id:N}"));
    var ruleEpisode = Node(await Own($"/Babelfin/Me/Effective?itemId={animeEpId:N}"));
    var ruleMovie = Node(await Own($"/Babelfin/Me/Overrides/Target/{copperKiteId:N}"));
    var ruleVideo = Node(await Own($"/Babelfin/Me/Overrides/Target/{clipId:N}"));
    Check(Audio(ruleShow) == "jpn" && From(ruleShow, "AudioChain") == "the admin's Anime library defaults" && ruleEpisode["profile"]?["audioChain"]?[0]?["language"]?.GetValue<string>() == "jpn" && From(ruleShow, "Format") != "your smart rule 'Films'",
        $"a hidden \"episode\" rule cannot replace the show's visible Default, which is what its episodes play ('{From(ruleShow, "AudioChain")}', {Audio(ruleShow)}); the \"movie\" rule doesn't");
    Check(Audio(ruleMovie) == "kor" && From(ruleMovie, "AudioChain") == "the admin's Movies library defaults" && From(ruleMovie, "Format") != "your smart rule 'Films'",
        $"a movie's Default ignores the hidden \"movie\" rule ('{From(ruleMovie, "Format")}'), the \"episode\" rule doesn't ('{From(ruleMovie, "AudioChain")}')");
    Check(Audio(ruleVideo) == "kor" && From(ruleVideo, "Format") != "your smart rule 'Films'" && ruleVideo["type"]?.GetValue<string>() == "Video",
        "a video's Default: neither the \"movie\" nor the \"episode\" rule applies");

    // ── DELETE Me/Overrides/{itemId}: what the list shows can be removed ──
    await Fresh();
    var logBefore = await Activity();
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    doc.Overrides.Add(new ItemOverride { ItemId = favourites.Id.ToString("N"), Scope = OverrideScope.Collection, Name = "Anime favourites", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    doc.Overrides.Add(new ItemOverride { ItemId = copperKiteId.ToString("N"), Scope = OverrideScope.Season, Name = "Copper Kite", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    doc.Overrides.Add(new ItemOverride { ItemId = animeSeries.Id.ToString("N"), Scope = OverrideScope.Collection, Name = "Odd one", Settings = new SettingsLayer { Format = FormatPreference.TextOnly }, Manual = remembered() });
    doc.Overrides.Add(new ItemOverride { ItemId = clipId.ToString("N"), Scope = OverrideScope.Title, Name = "Birthday clip", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    personStore.Save(doc);
    var delCollection = await Delete($"/Babelfin/Me/Overrides/{favourites.Id:N}?scope=Collection");
    Check(delCollection.Status == 204 && personStore.TryGet(viewerOne.Id)!.FindOverride(favourites.Id) is null && personStore.TryGet(viewerOne.Id)!.Overrides.Count == 3,
        $"DELETE of a Collection-scope row (the page sends no collectionId): 204, that entry is gone ({delCollection.Status})");
    var delMisScoped = await Delete($"/Babelfin/Me/Overrides/{copperKiteId:N}?scope=Season");
    Check(delMisScoped.Status == 204 && personStore.TryGet(viewerOne.Id)!.FindOverride(copperKiteId) is null && personStore.TryGet(viewerOne.Id)!.Overrides.Count == 2,
        $"DELETE of a stored entry whose scope doesn't fit its item (a movie stored under Season): 204, removed by its own id ({delMisScoped.Status})");
    var delOtherScope = await Delete($"/Babelfin/Me/Overrides/{clipId:N}?scope=Season");
    Check(delOtherScope.Status == 400 && personStore.TryGet(viewerOne.Id)!.FindOverride(clipId) is not null,
        $"a request naming another scope than the stored one still resolves through the item: 400 for a video's season, the entry stays ({delOtherScope.Status})");
    var delManual = await Delete($"/Babelfin/Me/Overrides/{animeSeries.Id:N}?scope=Collection&manual=true");
    var afterManual = personStore.TryGet(viewerOne.Id)!.FindOverride(animeSeries.Id);
    var delManualAgain = await Delete($"/Babelfin/Me/Overrides/{animeSeries.Id:N}?scope=Collection&manual=true");
    var activityK = await Activity();
    Check(delManual.Status == 204 && afterManual is { Manual: null } && !afterManual.Settings.IsEmpty && delManualAgain.Status == 204
        && Added(logBefore, activityK, "You removed a remembered track change for Skyward Lantern Voyage") == 1,
        $"manual=true on a directly named entry removes only the remembered change (settings stay); a second time is a 204 with no new activity entry ({delManual.Status}/{delManualAgain.Status})");
    var delWhole = await Delete($"/Babelfin/Me/Overrides/{animeSeries.Id:N}?scope=Collection");
    Check(delWhole.Status == 204 && personStore.TryGet(viewerOne.Id)!.FindOverride(animeSeries.Id) is null && Added(logBefore, await Activity(), "You removed your settings for Skyward Lantern Voyage") == 1
        && Added(logBefore, activityK, "You removed your settings for Anime favourites") == 1 && Added(logBefore, activityK, "You removed your settings for Copper Kite") == 1,
        "the activity entries name the item as the person sees it now (not the stored name)");
    var noneMovieSeason = await Delete($"/Babelfin/Me/Overrides/{copperKiteId:N}?scope=Season");
    var noneCollection = await Delete($"/Babelfin/Me/Overrides/{favourites.Id:N}?scope=Collection");
    var noneUnknown = await Delete($"/Babelfin/Me/Overrides/{Guid.NewGuid():N}?scope=Title");
    Check(noneMovieSeason.Status == 400 && noneCollection.Status == 400 && noneUnknown.Status == 404,
        $"nothing stored: the old answers stay (a movie has no season: 400; a collection scope without collectionId: 400; an unknown item: 404) ({noneMovieSeason.Status}/{noneCollection.Status}/{noneUnknown.Status})");
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    doc.Overrides.Add(new ItemOverride { ItemId = favourites.Id.ToString("N"), Scope = OverrideScope.Collection, Name = "Anime favourites", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    personStore.Save(doc);
    var viaItem = await Delete($"/Babelfin/Me/Overrides/{animeEpId:N}?scope=Collection&collectionId={favourites.Id:N}");
    Check(viaItem.Status == 204 && personStore.TryGet(viewerOne.Id)!.FindOverride(favourites.Id) is null, $"a collection override is still removed through an episode of its show with collectionId ({viaItem.Status})");

    // ── Activity text for an item that is gone ──
    await Fresh();
    var goneBefore = await Activity();
    var goneNamed = Guid.NewGuid();
    var goneBlank = Guid.NewGuid();
    var goneNull = Guid.NewGuid();
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    doc.Overrides.Add(new ItemOverride { ItemId = goneNamed.ToString("N"), Scope = OverrideScope.Series, Name = "Cancelled Show", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    doc.Overrides.Add(new ItemOverride { ItemId = goneBlank.ToString("N"), Scope = OverrideScope.Series, Name = "", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    doc.Overrides.Add(new ItemOverride { ItemId = goneNull.ToString("N"), Scope = OverrideScope.Title, Name = null, Manual = remembered() });
    personStore.Save(doc);
    var g1 = await Delete($"/Babelfin/Me/Overrides/{goneNamed:N}?scope=Series");
    var g2 = await Delete($"/Babelfin/Me/Overrides/{goneBlank:N}?scope=Series");
    var g3 = await Delete($"/Babelfin/Me/Overrides/{goneNull:N}?scope=Title&manual=true");
    var goneLog = await Activity();
    Check(g1.Status == 204 && g2.Status == 204 && g3.Status == 204 && Added(goneBefore, goneLog, "You removed your settings for Cancelled Show") == 1 && Added(goneBefore, goneLog, "You removed your settings for a title") == 1
        && Added(goneBefore, goneLog, "You removed a remembered track change for a title") == 1 && Entries(goneLog, "You removed your settings for ") == 0 && personStore.TryGet(viewerOne.Id)!.Overrides.Count == 0,
        $"deleting an item that is gone: the stored name, else \"a title\", never a text that ends in \"for \" ({g1.Status}/{g2.Status}/{g3.Status})");
    var viewerTwoUrl = $"/Babelfin/Admin/Activity?userId={viewerTwo.Id:N}&limit=500";
    var viewerTwoBefore = await Send(h, HttpMethod.Get, viewerTwoUrl, admin);
    var viewerTwoDoc = personStore.GetOrCreate(viewerTwo.Id, () => new PersonSettings { UserId = viewerTwo.Id, UserName = "viewerTwo" }).Clone();
    viewerTwoDoc.Overrides.Clear();
    viewerTwoDoc.Overrides.Add(new ItemOverride { ItemId = goneBlank.ToString("N"), Scope = OverrideScope.Series, Name = "", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    viewerTwoDoc.Overrides.Add(new ItemOverride { ItemId = goneNull.ToString("N"), Scope = OverrideScope.Title, Name = null, Manual = remembered() });
    personStore.Save(viewerTwoDoc);
    var a1 = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/{goneBlank:N}?scope=Series", admin);
    var a2 = await Send(h, HttpMethod.Delete, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Overrides/{goneNull:N}", admin);
    var viewerTwoLog = await Send(h, HttpMethod.Get, viewerTwoUrl, admin);
    Check(a1.Status == 204 && a2.Status == 204 && Added(viewerTwoBefore, viewerTwoLog, "An admin removed your override for a title") == 2 && Entries(viewerTwoLog, "An admin removed your override for ") == 0,
        $"the admin's delete of an item that is gone says \"a title\" the same way ({a1.Status}/{a2.Status})");

    // ── The cap counts explicit settings only; keepOverrides with more than 500 stored entries ──
    await Fresh();
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    for (var i = 0; i < 520; i++) doc.Overrides.Add(new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Scope = OverrideScope.Title, Name = "Remembered " + i, Manual = remembered(), UpdatedAt = rememberedAt });
    personStore.Save(doc);
    var fill = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{copperKiteId:N}?scope=Title", user, AudioEng);
    Check(fill.Status == 200 && personStore.TryGet(viewerOne.Id)!.Overrides.Count == 521 && personStore.TryGet(viewerOne.Id)!.FindOverride(copperKiteId)!.Settings.AudioChain is not null,
        $"520 remembered track changes don't use up the 500 overrides: a show override can still be saved ({fill.Status})");
    var page = Node(await Own("/Babelfin/Me/Settings"))["settings"]!.DeepClone();
    page["everyday"]!["understood"] = new JsonArray("de");
    var storedBefore = JsonSerializer.Serialize(personStore.TryGet(viewerOne.Id)!.Overrides, BabelfinJson.Options);
    var keepBig = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true", user, page.ToJsonString());
    Check(keepBig.Status == 200 && personStore.TryGet(viewerOne.Id)!.Overrides.Count == 521 && JsonSerializer.Serialize(personStore.TryGet(viewerOne.Id)!.Overrides, BabelfinJson.Options) == storedBefore
        && personStore.TryGet(viewerOne.Id)!.Everyday.Understood!.SequenceEqual(new[] { "de" }),
        $"PUT Me/Settings?keepOverrides=true with 521 stored entries saves fine and keeps every one ({keepBig.Status})");
    var plainBig = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, page.ToJsonString());
    Check(plainBig.Status == 200 && personStore.TryGet(viewerOne.Id)!.Overrides.Count == 521 && JsonSerializer.Serialize(personStore.TryGet(viewerOne.Id)!.Overrides, BabelfinJson.Options) == storedBefore,
        $"(without the flag the same whole-document body, 521 entries of which one has settings, is within the validator's cap too: 200, every entry as it was: {plainBig.Status})");

    await Fresh();
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    for (var i = 0; i < 499; i++) doc.Overrides.Add(new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Scope = OverrideScope.Title, Name = "Explicit " + i, Settings = new SettingsLayer { Format = FormatPreference.TextOnly }, UpdatedAt = rememberedAt });
    doc.Overrides.Add(new ItemOverride { ItemId = clipId.ToString("N"), Scope = OverrideScope.Title, Name = "Birthday clip", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });
    doc.Overrides.Add(new ItemOverride { ItemId = copperKiteId.ToString("N"), Scope = OverrideScope.Title, Name = "Copper Kite", Manual = remembered() });
    for (var i = 0; i < 100; i++) doc.Overrides.Add(new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Scope = OverrideScope.Title, Name = "Remembered " + i, Manual = remembered() });
    personStore.Save(doc);
    var capNew = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{animeSeries.Id:N}?scope=Series", user, AudioEng);
    var capRemembered = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{copperKiteId:N}?scope=Title", user, AudioEng);
    var capUpdate = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{clipId:N}?scope=Title", user, AudioEng);
    Check(capNew.Status == 400 && capRemembered.Status == 400 && capUpdate.Status == 200 && capNew.Text.Contains("At most 500", StringComparison.Ordinal),
        $"with 500 explicit overrides: a new one is refused (400), so is turning a remembered change into one (400), changing an existing one is fine ({capNew.Status}/{capRemembered.Status}/{capUpdate.Status})");
    var oneExplicit = personStore.TryGet(viewerOne.Id)!.Overrides.First(o => o.Name == "Explicit 0");
    var freeOne = await Delete($"/Babelfin/Me/Overrides/{oneExplicit.ItemId}?scope=Title");
    var capAfter = await Send(h, HttpMethod.Put, $"/Babelfin/Me/Overrides/{copperKiteId:N}?scope=Title", user, AudioEng);
    Check(freeOne.Status == 204 && capAfter.Status == 200, $"after one explicit override is removed (an item that doesn't exist is removed by its id) the next one fits ({freeOne.Status}/{capAfter.Status})");

    // ── The same cap in a whole document: remembered track changes are not counted by the validator either ──
    // (the admin's import, the backup POST Admin/ResetAll writes, a whole-document PUT, and the copy from another person all validate a whole document)
    string StoredJson(Guid id) => JsonSerializer.Serialize(personStore.TryGet(id)?.Overrides ?? new List<ItemOverride>(), BabelfinJson.Options);
    JsonNode UserRow(JsonNode preview, Guid id) => preview["users"]!.AsArray().First(u => Guid.Parse(u!["userId"]!.GetValue<string>()) == id)!;
    await Fresh();
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    for (var i = 0; i < 600; i++) doc.Overrides.Add(new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Scope = OverrideScope.Title, Name = "Remembered " + i, Manual = remembered(), UpdatedAt = rememberedAt });
    doc.Overrides.Add(new ItemOverride { ItemId = copperKiteId.ToString("N"), Scope = OverrideScope.Title, Name = "Copper Kite", Settings = new SettingsLayer { Format = FormatPreference.TextOnly }, UpdatedAt = rememberedAt });
    doc.Overrides.Add(new ItemOverride { ItemId = clipId.ToString("N"), Scope = OverrideScope.Title, Name = "Birthday clip", Settings = new SettingsLayer { Format = FormatPreference.PreferText }, Manual = remembered(), UpdatedAt = rememberedAt });
    doc.Overrides.Add(new ItemOverride { ItemId = animeSeries.Id.ToString("N"), Scope = OverrideScope.Series, Name = "Skyward Lantern Voyage", Settings = JsonSerializer.Deserialize<SettingsLayer>(AudioEng, BabelfinJson.Options)!, UpdatedAt = rememberedAt });
    personStore.Save(doc);
    var manyStored = StoredJson(viewerOne.Id);
    var manyExport = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Export", admin);
    await Fresh(); // the person is lost: a freshly seeded document
    var manyLost = personStore.TryGet(viewerOne.Id)!.Overrides.Count;
    var manyDry = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=true", admin, manyExport.Text);
    var manyRow = manyDry.Status == 200 ? UserRow(Node(manyDry), viewerOne.Id) : new JsonObject();
    var manyApply = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, manyExport.Text);
    var manyBack = personStore.TryGet(viewerOne.Id)!;
    Check(manyExport.Status == 200 && manyLost == 0 && manyDry.Status == 200 && manyRow["action"]?.GetValue<string>() == "update" && manyApply.Status == 200 && Node(manyApply)["applied"]!.GetValue<bool>()
        && manyBack.Overrides.Count == 603 && manyBack.Overrides.Count(o => o.Manual is not null) == 601 && manyBack.Overrides.Count(o => !o.Settings.IsEmpty) == 3 && StoredJson(viewerOne.Id) == manyStored,
        $"export, then import, of a person with 600 remembered track changes and 3 overrides with settings: not skipped ('{manyRow["action"]}' {manyRow["reason"]}), restored exactly (603 entries, 601 with a remembered change, nothing altered) ({manyApply.Status})");

    // 500 explicit overrides are the most; 501 are still refused, however many remembered changes come with them.
    JsonNode ManyExplicit(int explicitCount, int rememberedCount)
    {
        var node = JsonNode.Parse(manyExport.Text)!;
        var mine = UserRow(node, viewerOne.Id);
        var list = new JsonArray();
        for (var i = 0; i < explicitCount; i++) list.Add(new JsonObject { ["itemId"] = Guid.NewGuid().ToString("N"), ["scope"] = "Title", ["name"] = "Explicit " + i, ["settings"] = new JsonObject { ["format"] = "TextOnly" } });
        for (var i = 0; i < rememberedCount; i++) list.Add(new JsonObject { ["itemId"] = Guid.NewGuid().ToString("N"), ["scope"] = "Title", ["name"] = "Remembered " + i, ["settings"] = new JsonObject(), ["manual"] = new JsonObject { ["subtitlesOff"] = true } });
        mine["overrides"] = list;
        return node;
    }

    var atCap = ManyExplicit(500, 600);
    var atCapRow = UserRow(Node(await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=true", admin, atCap.ToJsonString())), viewerOne.Id);
    var overCap = ManyExplicit(501, 600);
    var overCapPreview = Node(await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, overCap.ToJsonString()));
    var overCapRow = UserRow(overCapPreview, viewerOne.Id);
    Check(atCapRow["action"]?.GetValue<string>() == "update" && overCapRow["action"]?.GetValue<string>() == "skip" && (overCapRow["reason"]?.GetValue<string>() ?? "").Contains("at most 500 overrides are allowed (501 sent)", StringComparison.Ordinal) && StoredJson(viewerOne.Id) == manyStored,
        $"import: 500 explicit + 600 remembered is accepted ('{atCapRow["action"]}'); 501 explicit is skipped ('{overCapRow["action"]}': {overCapRow["reason"]}) and the stored person is untouched");

    var wholePage = Node(await Own("/Babelfin/Me/Settings"))["settings"]!.DeepClone();
    var wholeWithRemembered = (JsonObject)wholePage.DeepClone();
    wholeWithRemembered["everyday"]!["understood"] = new JsonArray("fr");
    var wholeOk = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, wholeWithRemembered.ToJsonString());
    Check(wholeOk.Status == 200 && personStore.TryGet(viewerOne.Id)!.Overrides.Count == 603 && StoredJson(viewerOne.Id) == manyStored && personStore.TryGet(viewerOne.Id)!.Everyday.Understood!.SequenceEqual(new[] { "fr" }),
        $"PUT Me/Settings without keepOverrides with 603 entries (601 holding a remembered change) is saved, every entry as it was ({wholeOk.Status})");
    var wholeOver = (JsonObject)wholePage.DeepClone();
    var wholeList = new JsonArray();
    for (var i = 0; i < 501; i++) wholeList.Add(new JsonObject { ["itemId"] = Guid.NewGuid().ToString("N"), ["scope"] = "Title", ["settings"] = new JsonObject { ["format"] = "TextOnly" } });
    wholeOver["overrides"] = wholeList;
    var wholeRefused = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, wholeOver.ToJsonString());
    Check(wholeRefused.Status == 400 && wholeRefused.Text.Contains("at most 500 overrides are allowed (501 sent)", StringComparison.Ordinal) && StoredJson(viewerOne.Id) == manyStored,
        $"PUT Me/Settings with a body of 501 overrides with settings is still refused (400), nothing changes ({wholeRefused.Status})");

    // A settings page does not edit flags: ignored body fields must not prevent independent settings saves.
    await Fresh();
    personStore.Update(viewerOne.Id, current =>
    {
        var changed = current!.Clone();
        changed.BurnedIn.Add(new BurnedInFlag { ItemId = clipId.ToString("N"), Languages = new List<string> { "en" } });
        return changed;
    });
    var preservedFlags = JsonSerializer.Serialize(personStore.TryGet(viewerOne.Id)!.BurnedIn, BabelfinJson.Options);
    JsonArray IgnoredFlags() => new(new JsonObject { ["itemId"] = "not-a-guid", ["scope"] = "Library", ["languages"] = new JsonArray("not-a-language") });
    var ignoredMe = (JsonObject)Node(await Own("/Babelfin/Me/Settings"))["settings"]!.DeepClone();
    ignoredMe["burnedIn"] = IgnoredFlags();
    var ignoredMeResult = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true", user, ignoredMe.ToJsonString());
    Check(ignoredMeResult.Status == 200 && JsonSerializer.Serialize(personStore.TryGet(viewerOne.Id)!.BurnedIn, BabelfinJson.Options) == preservedFlags,
        $"personal settings with keepOverrides ignore invalid body flags and retain the latest stored flags ({ignoredMeResult.Status})");
    var ignoredAdmin = (JsonObject)Node(await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", admin)).DeepClone();
    ignoredAdmin["burnedIn"] = IgnoredFlags();
    var ignoredAdminResult = await Send(h, HttpMethod.Put, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings?keepOverrides=true", admin, ignoredAdmin.ToJsonString());
    Check(ignoredAdminResult.Status == 200 && JsonSerializer.Serialize(personStore.TryGet(viewerOne.Id)!.BurnedIn, BabelfinJson.Options) == preservedFlags,
        $"admin person settings with keepOverrides ignore invalid body flags and retain the latest stored flags ({ignoredAdminResult.Status})");

    // ── A save meets a removal or a recorded track change in its window: it is merged into the document the store holds when its lock is taken ──
    // The access check of the body's burned-in item looks it up between the save's first read of the document and its write; the fake library
    // manager runs the "other writer" exactly then (a removal on another device, a track change recorded during playback).
    void Meanwhile(Guid lookedUp, Action other)
    {
        lib.OnItemLookup = id =>
        {
            if (id != lookedUp) return;
            lib.OnItemLookup = null;
            other();
        };
    }

    PersonSettings Without(PersonSettings d, params Guid[] ids) { var c = d.Clone(); c.Overrides.RemoveAll(o => ids.Any(i => PersonSettings.SameId(o.ItemId, i))); return c; }
    string Ids(PersonSettings d) => string.Join(",", d.Overrides.Select(o => o.ItemId[..4]).Order());
    var remembered1 = Guid.Parse("11111111-aaaa-aaaa-aaaa-111111111111");
    var remembered2 = Guid.Parse("22222222-aaaa-aaaa-aaaa-222222222222");
    var rememberedOnly = (Guid id) => new ItemOverride { ItemId = id.ToString("N"), Scope = OverrideScope.Title, Name = "Remembered", Manual = remembered(), UpdatedAt = rememberedAt };
    var burnedInClip = new JsonArray(new JsonObject { ["itemId"] = clipId.ToString("N"), ["scope"] = "Title", ["languages"] = new JsonArray("en") });

    await Fresh();
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    doc.Overrides.Add(new ItemOverride { ItemId = copperKiteId.ToString("N"), Scope = OverrideScope.Title, Name = "Copper Kite", Settings = new SettingsLayer { Format = FormatPreference.TextOnly }, UpdatedAt = rememberedAt });
    doc.Overrides.Add(rememberedOnly(remembered1));
    personStore.Save(doc);
    var racePage = (JsonObject)Node(await Own("/Babelfin/Me/Settings"))["settings"]!.DeepClone(); // what a page loaded a moment ago holds
    racePage["everyday"]!["understood"] = new JsonArray("de");
    racePage["burnedIn"] = burnedInClip.DeepClone();
    racePage["collectionOrder"] = new JsonArray(favourites.Id.ToString("N"));
    Meanwhile(favourites.Id, () =>
    {
        // The person removes the Copper Kite row on another device, and a track change is remembered during playback.
        Check(personStore.Update(viewerOne.Id, c => { var d = Without(c!, copperKiteId); d.Overrides.Add(rememberedOnly(remembered2)); return d; }), "(the other writer's update was stored)");
    });
    var raceKeep = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true", user, racePage.ToJsonString());
    var afterRace = personStore.TryGet(viewerOne.Id)!;
    Check(raceKeep.Status == 200 && afterRace.FindOverride(copperKiteId) is null && afterRace.FindOverride(remembered1) is not null && afterRace.FindOverride(remembered2)?.Manual is not null && afterRace.Overrides.Count == 2
        && afterRace.Everyday.Understood!.SequenceEqual(new[] { "de" }) && afterRace.BurnedIn.Count == 0 && afterRace.CollectionOrder.SequenceEqual(new[] { favourites.Id.ToString("N") }),
        $"PUT Me/Settings?keepOverrides=true: an override removed and a track change remembered after the save began are neither brought back nor dropped; the rest of the body is saved ({raceKeep.Status}: {Ids(afterRace)})");

    // Without the flag a remembered change the body leaves out (an exported file has none) is kept from the document as it is then: one removed meanwhile stays removed.
    await Fresh();
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    doc.Overrides.Add(new ItemOverride { ItemId = copperKiteId.ToString("N"), Scope = OverrideScope.Title, Name = "Copper Kite", Settings = new SettingsLayer { Format = FormatPreference.TextOnly }, UpdatedAt = rememberedAt });
    doc.Overrides.Add(rememberedOnly(remembered1));
    doc.Overrides.Add(rememberedOnly(remembered2));
    personStore.Save(doc);
    var plainRace = (JsonObject)Node(await Own("/Babelfin/Me/Settings"))["settings"]!.DeepClone();
    plainRace["overrides"] = new JsonArray(plainRace["overrides"]!.AsArray().Where(o => o!["itemId"]!.GetValue<string>() == copperKiteId.ToString("N")).Select(o => o!.DeepClone()).ToArray());
    plainRace["burnedIn"] = burnedInClip.DeepClone();
    Meanwhile(clipId, () => Check(personStore.Update(viewerOne.Id, c => Without(c!, remembered1)), "(the other writer's update was stored)"));
    var racePlain = await Send(h, HttpMethod.Put, "/Babelfin/Me/Settings", user, plainRace.ToJsonString());
    afterRace = personStore.TryGet(viewerOne.Id)!;
    Check(racePlain.Status == 200 && afterRace.FindOverride(copperKiteId) is not null && afterRace.FindOverride(remembered1) is null && afterRace.FindOverride(remembered2)?.Manual is not null && afterRace.Overrides.Count == 2,
        $"PUT Me/Settings without the flag: a remembered change removed after the save began stays removed, the other is kept ({racePlain.Status}: {Ids(afterRace)})");

    // POST Me/Import goes the same way (its body names the clip again, which the document must not hold yet for the access check to look it up).
    Check(personStore.Update(viewerOne.Id, c => { var d = c!.Clone(); d.BurnedIn.Clear(); return d; }), "(the burned-in flag was cleared)");
    Meanwhile(clipId, () => Check(personStore.Update(viewerOne.Id, c => Without(c!, remembered2)), "(the other writer's update was stored)"));
    var importRace = await Send(h, HttpMethod.Post, "/Babelfin/Me/Import", user, plainRace.ToJsonString());
    afterRace = personStore.TryGet(viewerOne.Id)!;
    Check(importRace.Status == 200 && afterRace.FindOverride(copperKiteId) is not null && afterRace.FindOverride(remembered1) is null && afterRace.FindOverride(remembered2) is null && afterRace.Overrides.Count == 1,
        $"POST Me/Import: the same, a remembered change removed after it began stays removed ({importRace.Status}: {Ids(afterRace)})");

    // The admin's save of a person: the same.
    var viewerTwoRace = personStore.TryGet(viewerTwo.Id)?.Clone() ?? new PersonSettings { UserId = viewerTwo.Id, UserName = viewerTwo.Username };
    viewerTwoRace.Overrides.Clear();
    viewerTwoRace.Overrides.Add(new ItemOverride { ItemId = copperKiteId.ToString("N"), Scope = OverrideScope.Title, Name = "Copper Kite", Settings = new SettingsLayer { Format = FormatPreference.TextOnly }, UpdatedAt = rememberedAt });
    viewerTwoRace.Overrides.Add(rememberedOnly(remembered1));
    personStore.Save(viewerTwoRace);
    var adminRacePage = (JsonObject)Node(await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", admin)).DeepClone();
    adminRacePage["everyday"]!["understood"] = new JsonArray("de");
    adminRacePage["burnedIn"] = burnedInClip.DeepClone();
    adminRacePage["collectionOrder"] = new JsonArray(favourites.Id.ToString("N"));
    Meanwhile(favourites.Id, () => Check(personStore.Update(viewerTwo.Id, c => { var d = Without(c!, copperKiteId); d.Overrides.Add(rememberedOnly(remembered2)); return d; }), "(the other writer's update was stored)"));
    var adminRace = await Send(h, HttpMethod.Put, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings?keepOverrides=true", admin, adminRacePage.ToJsonString());
    var viewerTwoAfterRace = personStore.TryGet(viewerTwo.Id)!;
    Check(adminRace.Status == 200 && viewerTwoAfterRace.FindOverride(copperKiteId) is null && viewerTwoAfterRace.FindOverride(remembered1) is not null && viewerTwoAfterRace.FindOverride(remembered2)?.Manual is not null && viewerTwoAfterRace.Overrides.Count == 2
        && viewerTwoAfterRace.Everyday.Understood!.SequenceEqual(new[] { "de" }) && viewerTwoAfterRace.BurnedIn.Count == 0 && viewerTwoAfterRace.CollectionOrder.SequenceEqual(new[] { favourites.Id.ToString("N") }),
        $"PUT Admin/Users/{{id}}/Settings?keepOverrides=true: an override removed and a track change remembered after the save began are neither brought back nor dropped ({adminRace.Status}: {Ids(viewerTwoAfterRace)})");
    lib.OnItemLookup = null;
    ResetPersonFixture(personStore, viewerTwo.Id);

    // ── Thousands of stored entries stay fast: the items are asked for in batches, the libraries once per parent ──
    await Fresh();
    var longRunner = lib.AddSeries(tvFolder, "Long Runner", "en");
    var longSeason = lib.AddSeason(longRunner, 1);
    var bulk = new List<Guid>();
    for (var i = 0; i < 200; i++) { var id = Guid.NewGuid(); lib.AddEpisode(longSeason, id, "Episode " + i); bulk.Add(id); }
    for (var i = 0; i < 2000; i++) { var id = Guid.NewGuid(); lib.Add(new Movie { Id = id, Name = "Movie " + i, ParentId = moviesFolder.Id }); bulk.Add(id); }
    for (var i = 0; i < 300; i++) bulk.Add(Guid.NewGuid()); // deleted since
    doc = personStore.TryGet(viewerOne.Id)!.Clone();
    doc.Overrides.AddRange(bulk.Select((id, i) => new ItemOverride { ItemId = id.ToString("N"), Scope = OverrideScope.Title, Name = "Entry " + i, Manual = remembered(), UpdatedAt = rememberedAt }));
    personStore.Save(doc);
    lib.ResetCounters();
    var clock = Stopwatch.StartNew();
    var bigList = await Own("/Babelfin/Me/Overrides");
    var listMs = clock.ElapsedMilliseconds;
    var listCalls = (lib.ItemListCalls, lib.ItemByIdForUserCalls, lib.CollectionFolderCalls);
    var bigRows = bigList.Status == 200 ? Node(bigList).AsArray() : new JsonArray();
    Check(bigRows.Count == 2500 && listCalls.ItemListCalls == 6 && listCalls.ItemByIdForUserCalls == 0
        && bigRows.Count(r => r!["libraryName"]?.GetValue<string>() == "Movies") == 2000 && bigRows.Count(r => r!["libraryName"]?.GetValue<string>() == "TV Shows") == 200 && bigRows.Count(r => r!["type"] is null) == 300,
        $"GET Me/Overrides with 2,500 entries: 2,500 rows from {listCalls.ItemListCalls} queries (five entry batches plus shared ancestry) and no GetItemById per entry ({listCalls.ItemByIdForUserCalls}), in {listMs} ms");
    var lookups = new Jellyfin.Plugin.Babelfin.Api.PersonLookups(library, viewerOne);
    var movieItems = bulk.Skip(200).Take(2000).Select(id => library.GetItemById<BaseItem>(id)!).ToList();
    var episodeItems = bulk.Take(200).Select(id => library.GetItemById<BaseItem>(id)!).ToList();
    lookups.LibrariesOf(animeEp); // the libraries the person can see are found with the first answer
    lib.ResetCounters();
    var movieLibraries = movieItems.Select(i => lookups.LibrariesOf(i)).ToList();
    var movieAsks = lib.CollectionFolderCalls;
    var episodeLibraries = episodeItems.Select(i => lookups.LibrariesOf(i)).ToList();
    Check(movieLibraries.All(l => l.Names.SequenceEqual(new[] { "Movies" })) && episodeLibraries.All(l => l.Names.SequenceEqual(new[] { "TV Shows" })) && movieAsks == 1 && lib.CollectionFolderCalls == 2,
        $"the libraries of 2,000 movies and 200 episodes are asked of Jellyfin once per parent ({movieAsks} for the movies' library folder, {lib.CollectionFolderCalls} with the season)");
    lib.ResetCounters();
    var narrowYes = Node(await Own($"/Babelfin/Me/Overrides/Target/{longRunner.Id:N}"));
    var callsYes = (lib.ItemListCalls, lib.ItemByIdForUserCalls);
    lib.ResetCounters();
    var narrowNo = Node(await Own($"/Babelfin/Me/Overrides/Target/{animeSeries.Id:N}"));
    var callsNo = (lib.ItemListCalls, lib.ItemByIdForUserCalls);
    Check(narrowYes["hasNarrower"]?.GetValue<bool>() == true && callsYes.ItemListCalls == 2 && narrowNo["hasNarrower"]?.GetValue<bool>() == false && callsNo.ItemListCalls == 6 && callsYes.ItemByIdForUserCalls <= 1 && callsNo.ItemByIdForUserCalls <= 1,
        $"Target's HasNarrower with 2,500 entries: found after one entry batch and shared ancestry ({callsYes.ItemListCalls} queries), none found after {callsNo.ItemListCalls} queries including ancestry, never one lookup per entry");
    foreach (var id in bulk) lib.Remove(id);
    lib.Remove(longSeason.Id);
    lib.Remove(longRunner.Id);

    // ── A library the person can't see is never named ──
    var staffOnly = lib.AddFolder("Staff Only");
    var limited = new User("limited", "Default", "Default") { Id = Guid.NewGuid() };
    limited.Preferences.Add(new Jellyfin.Database.Implementations.Entities.Preference(Jellyfin.Database.Implementations.Enums.PreferenceKind.EnabledFolders, moviesFolder.Id.ToString("D")));
    ((FakeUserManager)(object)users).Users.Add(limited);
    var limitedHeaders = new Dictionary<string, string> { ["X-Fake-User"] = limited.Id.ToString("N") };
    var sharedId = Guid.NewGuid();
    lib.Add(new Movie { Id = sharedId, Name = "Shared Film", ParentId = moviesFolder.Id });
    lib.InLibraries[sharedId] = new List<CollectionFolder> { staffOnly, moviesFolder }; // two libraries, the one nobody may see first
    foreach (var who in new[] { viewerOne, limited })
    {
        personStore.Save(new PersonSettings { UserId = who.Id, UserName = who.Username, Overrides = new List<ItemOverride> { new() { ItemId = sharedId.ToString("N"), Scope = OverrideScope.Title, Name = "Shared Film", Settings = new SettingsLayer { Format = FormatPreference.TextOnly } } } });
    }

    var limitedRows = Node(await Send(h, HttpMethod.Get, "/Babelfin/Me/Overrides", limitedHeaders)).AsArray();
    var limitedTarget = Node(await Send(h, HttpMethod.Get, $"/Babelfin/Me/Overrides/Target/{sharedId:N}", limitedHeaders));
    var viewerOneRows = Node(await Own("/Babelfin/Me/Overrides")).AsArray();
    var viewerOneTarget = Node(await Own($"/Babelfin/Me/Overrides/Target/{sharedId:N}"));
    Check(limitedRows.Count == 1 && limitedRows[0]!["libraryName"]?.GetValue<string>() == "Movies" && limitedRows[0]!["libraryId"]?.GetValue<string>() == moviesFolder.Id.ToString("N")
        && limitedTarget["libraryName"]?.GetValue<string>() == "Movies" && limitedTarget["libraryId"]?.GetValue<string>() == moviesFolder.Id.ToString("N"),
        $"a person without access to the \"Staff Only\" library sees the other library of an item that sits in both ({limitedRows[0]?["libraryName"]}, {limitedTarget["libraryName"]})");
    Check(viewerOneRows[0]!["libraryName"]?.GetValue<string>() == "Staff Only" && viewerOneTarget["libraryName"]?.GetValue<string>() == "Staff Only",
        $"a person who may see every library gets the first of them ({viewerOneRows[0]?["libraryName"]}, {viewerOneTarget["libraryName"]})");
    Check(!limitedRows[0]!.ToJsonString().Contains("Staff Only", StringComparison.Ordinal) && !limitedTarget.ToJsonString().Contains("Staff Only", StringComparison.Ordinal),
        "the name of the library never appears anywhere in what the person gets");
    personStore.Delete(limited.Id);
    ((FakeUserManager)(object)users).Users.Remove(limited);
    lib.Remove(sharedId);
    lib.RemoveFolder(staffOnly);

    // ── A stored entry with no usable id never becomes a row (and is dropped when the file is read) ──
    var viewerThreeHeaders = new Dictionary<string, string> { ["X-Fake-User"] = viewerThree.Id.ToString("N") };
    ResetPersonFixture(personStore, viewerThree.Id);
    Directory.CreateDirectory(usersFolder);
    File.WriteAllText(viewerThreeFile, "{ \"schemaVersion\": 1, \"userId\": \"" + viewerThree.Id.ToString("D") + "\", \"overrides\": [ { \"itemId\": \"\", \"scope\": \"Title\", \"name\": \"Nothing\", \"manual\": { \"subtitlesOff\": true } },"
        + " { \"itemId\": \"" + copperKiteId.ToString("N") + "\", \"scope\": \"Title\", \"settings\": { \"format\": \"TextOnly\" } }, { \"itemId\": \"junk\", \"scope\": \"Series\" }, { \"scope\": \"Title\" } ] }");
    var viewerThreeRows = Node(await Send(h, HttpMethod.Get, "/Babelfin/Me/Overrides", viewerThreeHeaders)).AsArray();
    Check(viewerThreeRows.Count == 1 && viewerThreeRows[0]!["itemId"]?.GetValue<string>() == copperKiteId.ToString("N") && personStore.TryGet(viewerThree.Id)!.Overrides.Count == 1,
        $"a file with an empty, a junk and a missing item id: only the usable entry is read, listed and kept ({viewerThreeRows.Count} row)");
    ResetPersonFixture(personStore, viewerThree.Id);
    foreach (var id in new[] { copperKiteId, clipId, favourites.Id }) lib.Remove(id);
    membership.Remove(animeSeries.Id);
    await h.DisposeAsync();

    // ── The names the pages read, as Jellyfin's API writes them (PascalCase) ──
    lib.Add(new Movie { Id = copperKiteId, Name = "Copper Kite", ProductionYear = 1992, ParentId = moviesFolder.Id });
    var hj = await StartAsync("plain", jellyfinJson: true);
    var jStore = hj.App.Services.GetRequiredService<IPersonStore>();
    async Task<Resp> OwnJ(string url) => await Send(hj, HttpMethod.Get, url, user);
    ResetPersonFixture(jStore, viewerOne.Id);
    await OwnJ("/Babelfin/Me/Settings");
    const string AudioEngPascal = "{ \"AudioChain\": [ { \"Kind\": \"Language\", \"Language\": \"eng\" } ] }";
    var pMovie = await Send(hj, HttpMethod.Put, $"/Babelfin/Me/Overrides/{copperKiteId:N}?scope=Title", user, AudioEngPascal);
    var pShow = await Send(hj, HttpMethod.Put, $"/Babelfin/Me/Overrides/{animeEpId:N}?scope=Series", user, AudioEngPascal);
    var pRows = Node(await OwnJ("/Babelfin/Me/Overrides")).AsArray();
    var pMovieRow = pRows.FirstOrDefault(r => r?["ItemId"]?.GetValue<string>() == copperKiteId.ToString("N"));
    var pShowRow = pRows.FirstOrDefault(r => r?["ItemId"]?.GetValue<string>() == animeSeries.Id.ToString("N"));
    Save("jellyfin-json-overrides.json", (await OwnJ("/Babelfin/Me/Overrides")).Body);
    Check(pMovie.Status == 200 && pShow.Status == 200 && pRows.Count == 2 && pMovieRow is not null && pShowRow is not null, $"with Jellyfin's JSON settings (PascalCase, exact-case reads): PascalCase bodies are read, GET Me/Overrides has the 2 rows ({pMovie.Status}/{pShow.Status})");
    Check(pMovieRow!["Scope"]?.GetValue<string>() == "Title" && pMovieRow["Name"]?.GetValue<string>() == "Copper Kite" && pMovieRow["Type"]?.GetValue<string>() == "Movie" && pMovieRow["ProductionYear"]?.GetValue<int>() == 1992
        && pMovieRow["LibraryId"]?.GetValue<string>() == moviesFolder.Id.ToString("N") && pMovieRow["LibraryName"]?.GetValue<string>() == "Movies" && pMovieRow["Settings"]?["AudioChain"]?[0]?["Language"]?.GetValue<string>() == "eng"
        && pMovieRow["UpdatedAt"] is not null && pMovieRow["SeriesName"] is null && pMovieRow["itemId"] is null && pMovieRow["libraryName"] is null,
        "a row has ItemId, Scope, Name, Type, ProductionYear, LibraryId, LibraryName, Settings and UpdatedAt, PascalCase and nothing else spelled differently");
    Check(pShowRow!["Scope"]?.GetValue<string>() == "Series" && pShowRow["Type"]?.GetValue<string>() == "Series" && pShowRow["LibraryName"]?.GetValue<string>() == "Anime", "a show's row: Scope Series, Type Series, LibraryName Anime");
    var pTarget = Node(await OwnJ($"/Babelfin/Me/Overrides/Target/{animeEpId:N}"));
    Save("jellyfin-json-target.json", (await OwnJ($"/Babelfin/Me/Overrides/Target/{animeEpId:N}")).Body);
    Check(pTarget["TargetId"]?.GetValue<string>() == animeSeries.Id.ToString("N") && pTarget["Scope"]?.GetValue<string>() == "Series" && pTarget["Name"]?.GetValue<string>() == "Skyward Lantern Voyage" && pTarget["Type"]?.GetValue<string>() == "Series"
        && pTarget["LibraryId"]?.GetValue<string>() == animeFolder.Id.ToString("N") && pTarget["LibraryName"]?.GetValue<string>() == "Anime" && pTarget["HasNarrower"]?.GetValue<bool>() == false
        && pTarget["Override"]?["ItemId"]?.GetValue<string>() == animeSeries.Id.ToString("N") && pTarget["Override"]?["Scope"]?.GetValue<string>() == "Series" && pTarget["Override"]?["Settings"]?["AudioChain"]?[0]?["Language"]?.GetValue<string>() == "eng",
        "Target has TargetId, Scope, Name, Type, LibraryId, LibraryName, HasNarrower and Override (ItemId, Scope, Settings), PascalCase");
    Check(pTarget["Inherited"]?["Profile"]?["AudioChain"]?[0]?["Language"]?.GetValue<string>() == "jpn" && pTarget["Inherited"]?["Sources"]?["AudioChain"]?.GetValue<string>() == "the admin's Anime library defaults"
        && pTarget["Inherited"]?["BurnedInLanguages"] is JsonArray && pTarget["targetId"] is null && pTarget["inherited"] is null && pTarget["hasNarrower"] is null,
        "Target's Inherited has Profile (AudioChain…), Sources and BurnedInLanguages; no camelCase twin of any of them");
    var pPage = Node(await OwnJ("/Babelfin/Me/Settings"));
    var pKeep = await Send(hj, HttpMethod.Put, "/Babelfin/Me/Settings?keepOverrides=true", user, pPage["Settings"]!.ToJsonString());
    Check(pKeep.Status == 200 && jStore.TryGet(viewerOne.Id)!.Overrides.Count == 2, $"the page's own PUT Me/Settings?keepOverrides=true, its body read back from GET Me/Settings, works with these settings and keeps both overrides ({pKeep.Status})");
    var pDelete = await Send(hj, HttpMethod.Delete, $"/Babelfin/Me/Overrides/{pMovieRow["ItemId"]!.GetValue<string>()}?scope={pMovieRow["Scope"]!.GetValue<string>()}", user);
    var pDeleteShow = await Send(hj, HttpMethod.Delete, $"/Babelfin/Me/Overrides/{pShowRow["ItemId"]!.GetValue<string>()}?scope={pShowRow["Scope"]!.GetValue<string>()}", user);
    Check(pDelete.Status == 204 && pDeleteShow.Status == 204 && (await OwnJ("/Babelfin/Me/Overrides")).Text.Trim() == "[]", $"a row is removed with the ItemId and Scope it was listed with ({pDelete.Status}/{pDeleteShow.Status})");
    await Send(hj, HttpMethod.Put, $"/Babelfin/Me/Overrides/{copperKiteId:N}?scope=Title", user, AudioEngPascal);
    var pAll = await Send(hj, HttpMethod.Delete, "/Babelfin/Me/Overrides/All", user);
    Check(pAll.Status == 204 && (await OwnJ("/Babelfin/Me/Overrides")).Text.Trim() == "[]", $"DELETE Me/Overrides removes all ({pAll.Status})");
    lib.Remove(copperKiteId);

    // Back to where the next section expects to find things.
    ResetPersonFixture(jStore, viewerOne.Id);
    ResetPersonFixture(jStore, viewerTwo.Id);
    await OwnJ("/Babelfin/Me/Settings");
    await Send(hj, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerTwo.Id:N}/Settings", admin);
    SetConfig();
    await hj.DisposeAsync();
}

// ════════════════════════ I. Reset all settings ════════════════════════
// Last on purpose: it puts the admin settings back to the shipped ones and gives every person a fresh document.
Section("I. Reset all settings");
{
    var h = await StartAsync("plain");
    var user = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N") };
    var admin = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N"), ["X-Fake-Admin"] = "1" };
    JsonNode Node(Resp r) => JsonNode.Parse(r.Text)!;
    var personStore = h.App.Services.GetRequiredService<IPersonStore>();
    var backupsFolder = Path.Combine(dataFolder, "backups");
    string[] Backups() => Directory.Exists(backupsFolder) ? Directory.GetFiles(backupsFolder, "babelfin-settings-before-reset-*.json") : Array.Empty<string>();

    // Before: admin settings away from the shipped ones; ViewerOne with a lock, a library row, an override and "handle next-episode tracks" on
    // (Babelfin turns his Jellyfin remember flags off and records them); a file whose Jellyfin user no longer exists.
    SetConfig("jpn", "eng", more: c =>
    {
        c.Everyday.UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage;
        c.Everyday.UnlabelledAudioLanguage = "ja";
        c.Libraries.Add(new LibraryDefault { LibraryId = animeFolder.Id.ToString("N"), Name = "Anime", OriginalAudio = OriginalAudioChoice.Yes });
        c.Keywords.PartialWords.Add("harness-word");
    });
    viewerOne.RememberAudioSelections = true;
    viewerOne.RememberSubtitleSelections = true;
    var viewerOneDoc = (JsonObject)Node(await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", admin));
    viewerOneDoc["perLibrary"] = true;
    viewerOneDoc["handleNextEpisode"] = true;
    viewerOneDoc["everyday"]!["unlabelledAudio"] = "SpecificLanguage";
    viewerOneDoc["everyday"]!["unlabelledAudioLanguage"] = "ko";
    viewerOneDoc["locks"] = new JsonObject { ["format"] = "TextOnly" };
    viewerOneDoc["libraries"] = new JsonArray(new JsonObject
    {
        ["libraryId"] = animeFolder.Id.ToString("N"), ["name"] = "Anime",
        ["settings"] = new JsonObject { ["audioChain"] = new JsonArray(new JsonObject { ["kind"] = "Language", ["language"] = "jpn" }) },
    });
    viewerOneDoc["overrides"] = new JsonArray(new JsonObject { ["itemId"] = animeSeries.Id.ToString("N"), ["scope"] = "Series", ["settings"] = new JsonObject { ["format"] = "TextOnly" } });
    var setUp = await Send(h, HttpMethod.Put, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", admin, viewerOneDoc.ToJsonString());
    var ghostId = Guid.NewGuid();
    personStore.Save(new PersonSettings { UserId = ghostId, UserName = "ghost", Everyday = new SettingsLayer { Understood = new[] { "fr" } } });
    // viewerTwo has more remembered track changes than the cap of 500 overrides: his part of the backup must restore all the same (the validator counts overrides with settings only).
    var manyAt = new DateTime(2026, 10, 20, 21, 5, 0, DateTimeKind.Utc);
    var viewerTwoMany = personStore.TryGet(viewerTwo.Id)?.Clone() ?? new PersonSettings { UserId = viewerTwo.Id, UserName = viewerTwo.Username };
    viewerTwoMany.Overrides.Clear();
    for (var i = 0; i < 600; i++) viewerTwoMany.Overrides.Add(new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Scope = OverrideScope.Title, Name = "Remembered " + i, Manual = new ManualMemory { SubtitlesOff = true, At = manyAt }, UpdatedAt = manyAt });
    viewerTwoMany.Overrides.Add(new ItemOverride { ItemId = animeSeries.Id.ToString("N"), Scope = OverrideScope.Series, Name = "Skyward Lantern Voyage", Settings = new SettingsLayer { Format = FormatPreference.TextOnly }, UpdatedAt = manyAt });
    personStore.Save(viewerTwoMany);
    var viewerTwoManyJson = JsonSerializer.Serialize(personStore.TryGet(viewerTwo.Id)!.Overrides, BabelfinJson.Options);
    var viewerOneStored = personStore.TryGet(viewerOne.Id);
    Check(setUp.Status == 200 && viewerOneStored is { HandleNextEpisode: true, PerLibrary: true, Libraries.Count: 1, Overrides.Count: 1, Locks.Format: FormatPreference.TextOnly }
        && viewerOneStored.JellyfinChanges is { RememberAudioSelections: true, RememberSubtitleSelections: true } && !viewerOne.RememberAudioSelections && !viewerOne.RememberSubtitleSelections,
        $"before: ViewerOne has a lock, a library row and an override; 'handle next-episode tracks' turned his Jellyfin remember flags off and recorded them ({setUp.Status})");
    var meView = Node(await Send(h, HttpMethod.Get, "/Babelfin/Me/Settings", user));
    Check(meView["settings"]?["everyday"]?["unlabelledAudioLanguage"]?.GetValue<string>() == "ko"
        && meView["inherited"]?["profile"]?["unlabelledAudioLanguage"]?.GetValue<string>() == "ja" && meView["inherited"]?["sources"]?["UnlabelledAudio"]?.GetValue<string>() == ProfileResolver.AdminEverydayLabel
        && meView["advancedDefaults"]?["profile"]?["unlabelledAudioLanguage"]?.GetValue<string>() == "ja",
        $"audio without a language is admin-only: ViewerOne's stored 'ko' is kept, Me/Settings resolves the admin's 'ja' (Inherited and AdvancedDefaults, source '{meView["inherited"]?["sources"]?["UnlabelledAudio"]}')");

    var forbidden = await Send(h, HttpMethod.Post, "/Babelfin/Admin/ResetAll", user);
    var anonymous = await Send(h, HttpMethod.Post, "/Babelfin/Admin/ResetAll");
    Check(forbidden.Status == 403 && anonymous.Status == 401 && Backups().Length == 0 && personStore.TryGet(viewerOne.Id)?.Locks.Format == FormatPreference.TextOnly,
        $"POST Admin/ResetAll: 403 for a plain user, 401 anonymous; nothing backed up or reset ({forbidden.Status}/{anonymous.Status})");

    // A backup that can't be written stops the reset: here a file stands where the backups folder would be created.
    var keptBackups = backupsFolder + ".harness-fault-kept";
    if (!Path.GetFullPath(backupsFolder).StartsWith(Path.GetFullPath(outDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Fault injection must stay inside the harness output folder");
    if (Directory.Exists(backupsFolder)) Directory.Move(backupsFolder, keptBackups);
    var folderFree = !Directory.Exists(backupsFolder) && !File.Exists(backupsFolder);
    File.WriteAllText(backupsFolder, "not a folder");
    Resp blocked;
    Resp settingsBlocked;
    try
    {
        blocked = await Send(h, HttpMethod.Post, "/Babelfin/Admin/ResetAll", admin);
        settingsBlocked = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Settings", admin);
    }
    finally
    {
        File.Delete(backupsFolder);
        if (Directory.Exists(keptBackups)) Directory.Move(keptBackups, backupsFolder);
    }
    Check(folderFree && blocked.Status == 500 && (Node(blocked)["message"]?.GetValue<string>() ?? string.Empty).Contains("nothing was reset") && settingsBlocked.Text.Contains("harness-word")
        && personStore.TryGet(viewerOne.Id) is { HandleNextEpisode: true, Locks.Format: FormatPreference.TextOnly, Overrides.Count: 1 } && !viewerOne.RememberAudioSelections,
        $"when the backup can't be written: 500 with a message, and nothing is reset ({blocked.Status}: {blocked.Text})");

    var export = Node(await Send(h, HttpMethod.Get, "/Babelfin/Admin/Export", admin));
    var storedBefore = personStore.UserIds().Count;
    var resettable = personStore.UserIds().Count(id => users.GetUserById(id) is not null);
    var reset = await Send(h, HttpMethod.Post, "/Babelfin/Admin/ResetAll", admin);
    Save("reset-all.json", reset.Body);
    var resetNode = reset.Status == 200 ? Node(reset) : new JsonObject();
    var backupFile = resetNode["backupFile"]?.GetValue<string>() ?? string.Empty;
    var relativeBackupPath = resetNode["backupPath"]?.GetValue<string>() ?? string.Empty;
    var backupPath = Path.Combine(personStore.DataFolder, relativeBackupPath);
    Check(reset.Status == 200 && Regex.IsMatch(backupFile, @"^babelfin-settings-before-reset-\d{8}-\d{6}\.json$") && Path.GetFileName(backupPath) == backupFile
        && !Path.IsPathRooted(relativeBackupPath) && relativeBackupPath == "backups/" + backupFile
        && string.Equals(Path.GetDirectoryName(backupPath), backupsFolder, StringComparison.OrdinalIgnoreCase) && resettable >= 2 && resetNode["people"]?.GetValue<int>() == resettable,
        $"POST Admin/ResetAll as admin: 200 with the backup's name, its path relative to Babelfin's data folder and the {resettable} people reset ({reset.Text})");

    var backupText = File.Exists(backupPath) ? File.ReadAllText(backupPath) : "{}";
    var backup = JsonSerializer.Deserialize<Jellyfin.Plugin.Babelfin.Api.ExportDocument>(backupText, BabelfinJson.Options);
    var viewerOneBackup = backup?.Users.FirstOrDefault(u => u.UserId == viewerOne.Id);
    Check(backup is { SchemaVersion: 1 } && backup.BabelfinVersion == BfPlugin.VersionString && backup.Admin?.Everyday.UnlabelledAudioLanguage == "ja" && backup.Admin.Keywords.PartialWords.Contains("harness-word")
        && backup.Users.Count == storedBefore && viewerOneBackup is { HandleNextEpisode: true, Libraries.Count: 1, Overrides.Count: 1, Locks.Format: FormatPreference.TextOnly } && viewerOneBackup.JellyfinChanges is not null
        && backup.Users.Any(u => u.UserId == ghostId && u.Everyday.Understood?.SequenceEqual(new[] { "fr" }) == true),
        $"the backup parses as an export (schema 1, version {backup?.BabelfinVersion}) with the admin settings and all {backup?.Users.Count} people from before, the file of a removed user included");
    var backupNode = JsonNode.Parse(backupText)!;
    Check(JsonNode.DeepEquals(backupNode["admin"], export["admin"]) && JsonNode.DeepEquals(backupNode["users"], export["users"]),
        "the backup holds exactly what GET Admin/Export gave just before (the admin settings and every person)");

    var settingsAfter = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Settings", admin);
    var defaults = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Defaults", admin);
    Save("reset-all-settings.json", settingsAfter.Body);
    Check(settingsAfter.Status == 200 && defaults.Status == 200 && JsonNode.DeepEquals(Node(settingsAfter), Node(defaults)) && !settingsAfter.Text.Contains("harness-word"),
        "afterwards GET Admin/Settings is exactly GET Admin/Defaults (the shipped settings)");

    var fresh = personStore.UserIds().Where(id => users.GetUserById(id) is not null).Select(id => personStore.TryGet(id)).ToList();
    Check(fresh.Count == resettable && fresh.All(d => d is { Overrides.Count: 0, Libraries.Count: 0, SmartRules.Count: 0, CollectionOrder.Count: 0, BurnedIn.Count: 0, PerLibrary: null, HandleNextEpisode: true, Enabled: true, Locks.IsEmpty: true } && d.Seed is not null),
        "every person is a fresh seed: no overrides, library rows, rules or locks, the table mode automatic, 'handle next-episode tracks' on, its default");
    var viewerOneAfter = Node(await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", admin));
    Check(viewerOneAfter["overrides"]!.AsArray().Count == 0 && viewerOneAfter["libraries"]!.AsArray().Count == 0 && viewerOneAfter["perLibrary"] is null && viewerOneAfter["locks"]?["isEmpty"]?.GetValue<bool>() == true
        && viewerOneAfter["everyday"]!["understood"]!.AsArray().Single()!.GetValue<string>() == "en",
        "GET Admin/Users/{id}/Settings for ViewerOne: the seeded document ('en' assumed from the server) and nothing else");
    Check(!viewerOne.RememberAudioSelections && !viewerOne.RememberSubtitleSelections && personStore.TryGet(viewerOne.Id)?.JellyfinChanges is { RememberAudioSelections: true, RememberSubtitleSelections: true },
        "ViewerOne's Jellyfin remember flags, which Babelfin had turned off, stay off and the record of his original values is kept, because the reset leaves 'handle next-episode tracks' on");
    Check(personStore.TryGet(ghostId)?.Everyday.Understood?.SequenceEqual(new[] { "fr" }) == true, "the file of a user who no longer exists is left as it was");
    var entries = Node(await Send(h, HttpMethod.Get, "/Babelfin/Admin/Activity?kind=settings-changed&limit=50", admin)).AsArray();
    Check(entries.Any(e => e!["text"]?.GetValue<string>() == "An admin reset all Babelfin settings" && e["userId"] is null)
        && entries.Count(e => e!["text"]?.GetValue<string>() == "An admin reset your Babelfin settings") == resettable
        && entries.Any(e => e!["text"]?.GetValue<string>() == "An admin reset your Babelfin settings" && Guid.Parse(e["userId"]!.GetValue<string>()) == viewerOne.Id),
        "the activity log has one server-wide entry for the reset and one for each person reset");

    var again = await Send(h, HttpMethod.Post, "/Babelfin/Admin/ResetAll", admin);
    var againNode = again.Status == 200 ? Node(again) : new JsonObject();
    var secondFile = againNode["backupFile"]?.GetValue<string>() ?? string.Empty;
    var secondRelativePath = againNode["backupPath"]?.GetValue<string>() ?? string.Empty;
    var secondPath = Path.Combine(personStore.DataFolder, secondRelativePath);
    Check(again.Status == 200 && secondFile != backupFile && Regex.IsMatch(secondFile, @"^babelfin-settings-before-reset-\d{8}-\d{6}(-\d+)?\.json$") && Backups().Length == 2
        && !Path.IsPathRooted(secondRelativePath) && secondRelativePath == "backups/" + secondFile
        && File.Exists(secondPath) && File.ReadAllText(backupPath) == backupText,
        $"a second ResetAll right away writes a second backup ({secondFile}) and leaves the first one as it was");

    // The admin's way back: the first backup, imported as it is, puts the settings back.
    var restore = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, backupText);
    var restoredSettings = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Settings", admin);
    var viewerOneRestored = personStore.TryGet(viewerOne.Id);
    Check(restore.Status == 200 && Node(restore)["applied"]!.GetValue<bool>() && restoredSettings.Text.Contains("harness-word")
        && Node(restoredSettings)["everyday"]?["unlabelledAudioLanguage"]?.GetValue<string>() == "ja"
        && viewerOneRestored is { HandleNextEpisode: true, Libraries.Count: 1, Overrides.Count: 1, Locks.Format: FormatPreference.TextOnly } && !viewerOne.RememberAudioSelections,
        $"POST Admin/Import takes the first backup as it is and puts everything back: the admin settings, ViewerOne's lock, library row and override, and his remember flags still off ({restore.Status})");
    var viewerTwoRestored = personStore.TryGet(viewerTwo.Id);
    var viewerTwoRow = restore.Status == 200 ? Node(restore)["users"]!.AsArray().FirstOrDefault(u => Guid.Parse(u!["userId"]!.GetValue<string>()) == viewerTwo.Id) : null;
    Check(viewerTwoRow?["action"]?.GetValue<string>() == "update" && viewerTwoRestored is { Overrides.Count: 601 } && JsonSerializer.Serialize(viewerTwoRestored.Overrides, BabelfinJson.Options) == viewerTwoManyJson,
        $"the same backup restores a person with 600 remembered track changes and one override with settings: not skipped for being over 500 entries ('{viewerTwoRow?["action"]}' {viewerTwoRow?["reason"]}), all 601 entries back as they were");

    await h.DisposeAsync();
}



// Exact downloaded file bytes must remain interoperable with either host JSON convention.
Section("Import file conventions and recovery fidelity");
foreach (var hostJson in new[] { (Pascal: false, Insensitive: true), (Pascal: true, Insensitive: false), (Pascal: true, Insensitive: true) })
{
    var exactCaseHost = hostJson.Pascal;
    var h = await StartAsync("plain", jellyfinJson: exactCaseHost, caseInsensitiveReads: hostJson.Insensitive);
    var label = exactCaseHost ? (hostJson.Insensitive ? "case-insensitive PascalCase" : "exact PascalCase") : "case-insensitive camelCase";
    var jsonTag = exactCaseHost + "-" + hostJson.Insensitive;
    var admin = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N"), ["X-Fake-Admin"] = "1" };
    var user = new Dictionary<string, string> { ["X-Fake-User"] = viewerOne.Id.ToString("N") };
    var ps = (PersonStore)h.App.Services.GetRequiredService<IPersonStore>();
    JsonNode? Property(JsonNode? value, string key) => value is JsonObject obj ? obj.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value : null;
    JsonNode Body(Resp response) => JsonNode.Parse(response.Text)!;
    var originalConfig = ImportPlanner.CloneAdmin(plugin.Configuration);
    var person = ps.TryGet(viewerOne.Id)!.Clone(); person.Everyday.Reads = new[] { "fr" };
    ps.Update(viewerOne.Id, _ => person);
    var configured = ImportPlanner.CloneAdmin(plugin.Configuration); configured.DryRun = true; plugin.UpdateConfiguration(configured);
    var download = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Export", admin);
    var exportedBytes = download.Body;
    Save("import-exact-export-" + jsonTag + ".json", exportedBytes);
    ps.Update(viewerOne.Id, current => { var copy = current!.Clone(); copy.Everyday.Reads = new[] { "de" }; return copy; });
    configured = ImportPlanner.CloneAdmin(plugin.Configuration); configured.DryRun = false; plugin.UpdateConfiguration(configured);
    var preview = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=true", admin, Encoding.UTF8.GetString(exportedBytes));
    Save("import-exact-preview-" + jsonTag + ".json", preview.Body);
    Check(preview.Status == 200 && Property(Body(preview), "users") is JsonArray entries && entries.Any(entry => Guid.TryParse(Property(entry, "userId")?.GetValue<string>(), out var foundUser) && foundUser == viewerOne.Id),
        $"[{label}] exact export bytes produce a nonempty import preview");
    var restored = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, Encoding.UTF8.GetString(exportedBytes));
    Check(restored.Status == 200 && Property(Body(restored), "applied")?.GetValue<bool>() == true && plugin.Configuration.DryRun && ps.TryGet(viewerOne.Id)!.Everyday.Reads?.SingleOrDefault() == "fr",
        $"[{label}] exact admin export bytes restore nested person and admin settings");

    var missing = Guid.NewGuid();
    ps.Update(viewerOne.Id, current => { var copy = current!.Clone(); copy.Overrides.Add(new ItemOverride { ItemId = missing.ToString("N"), Settings = new SettingsLayer { PrefersSdh = true } }); return copy; });
    // Retain a pre-existing recovery file while inspection becomes read-only.
    var recoveryFolder = Path.Combine(ps.DataFolder, "pruned-overrides"); Directory.CreateDirectory(recoveryFolder);
    var recoveryPath = Path.Combine(recoveryFolder, "recovery-fixture-" + jsonTag + ".json");
    var recoveryBytes = JsonSerializer.SerializeToUtf8Bytes(new ExportDocument { Users = new List<PersonSettings> { ps.TryGet(viewerOne.Id)!.Clone() } }, BabelfinJson.Options);
    File.WriteAllBytes(recoveryPath, recoveryBytes);
    var beforeReport = File.ReadAllBytes(ps.FilePath(viewerOne.Id));
    var prune = await Send(h, HttpMethod.Post, "/Babelfin/Me/Overrides/Prune", user);
    var report = Body(prune);
    Check(prune.Status == 200 && Property(report, "reportOnly")?.GetValue<bool>() == true && Property(report, "ready")?.GetValue<bool>() == true
        && Property(report, "removedOverrides")?.GetValue<int>() == 0 && Property(report, "removedBurnedIn")?.GetValue<int>() == 0
        && Property(report, "missingOverrides")?.GetValue<int>() > 0 && Property(report, "backupFile") is null
        && File.ReadAllBytes(ps.FilePath(viewerOne.Id)).SequenceEqual(beforeReport) && File.ReadAllBytes(recoveryPath).SequenceEqual(recoveryBytes),
        $"[{label}] missing-item inspection reports only and preserves settings and pre-existing recovery bytes");
    // A deliberate fixture edit proves legacy recovery import still works; inspection itself removes nothing.
    ps.Update(viewerOne.Id, current => { var copy = current!.Clone(); copy.Overrides.RemoveAll(entry => PersonSettings.SameId(entry.ItemId, missing)); return copy; });
    var recoverReportFixture = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, Encoding.UTF8.GetString(recoveryBytes));
    Check(recoverReportFixture.Status == 200 && ps.TryGet(viewerOne.Id)!.FindOverride(missing)?.Settings.PrefersSdh == true,
        $"[{label}] exact pre-existing recovery bytes still restore a deliberately edited fixture through HTTP");
    var reset = await Send(h, HttpMethod.Post, "/Babelfin/Admin/ResetAll", admin);
    var resetPath = Property(Body(reset), "backupPath")?.GetValue<string>();
    if (reset.Status == 200 && resetPath is not null)
    {
        Check(!Path.IsPathRooted(resetPath) && resetPath.StartsWith("backups/", StringComparison.Ordinal), $"[{label}] reset backup paths are relative and reveal no server directory");
        var resetBytes = File.ReadAllBytes(Path.Combine(ps.DataFolder, resetPath));
        var recover = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, Encoding.UTF8.GetString(resetBytes));
        Check(recover.Status == 200 && plugin.Configuration.DryRun && ps.TryGet(viewerOne.Id)!.Everyday.Reads?.SingleOrDefault() == "fr", $"[{label}] exact reset-backup bytes restore nested settings through HTTP");
    }
    else Check(false, $"[{label}] reset creates an importable recovery backup ({reset.Status})");
    var personal = await Send(h, HttpMethod.Get, "/Babelfin/Me/Export", user);
    ps.Update(viewerOne.Id, current => { var copy = current!.Clone(); copy.Everyday.Reads = new[] { "de" }; return copy; });
    var personalRestore = await Send(h, HttpMethod.Post, "/Babelfin/Me/Import", user, Encoding.UTF8.GetString(personal.Body));
    Check(personalRestore.Status == 200 && ps.TryGet(viewerOne.Id)!.Everyday.Reads?.SingleOrDefault() == "fr", $"[{label}] exact personal export bytes restore personal settings through HTTP");
    var wireSource = ps.TryGet(viewerOne.Id)!.Clone();
    wireSource.Everyday.Reads = new[] { "fr" };
    wireSource.Libraries = new List<LibraryLayer> { new() { LibraryId = moviesFolder.Id.ToString("N"), Settings = new SettingsLayer { PrefersSdh = true } } };
    ps.Update(viewerOne.Id, _ => wireSource);
    var wire = await Send(h, HttpMethod.Get, $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings", admin);
    var wireNode = Body(wire);
    Check(wire.Status == 200 && Property(wireNode, "userId")?.GetValue<string>() == viewerOne.Id.ToString(exactCaseHost ? "N" : "D")
        && Property(Property(wireNode, "libraries")?.AsArray().Single(), "libraryId")?.GetValue<string>() == moviesFolder.Id.ToString("N"),
        $"[{label}] GET wire fixture contains the host's GUID convention and a nested compact library ID");
    var wireEnvelope = "{\"SchemaVersion\":1,\"Users\":[" + wire.Text + "]}";
    ps.Update(viewerOne.Id, current => { var copy = current!.Clone(); copy.Everyday.Reads = new[] { "de" }; copy.Libraries.Clear(); return copy; });
    var wireAdminRestore = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, wireEnvelope);
    Check(wireAdminRestore.Status == 200 && Property(Body(wireAdminRestore), "applied")?.GetValue<bool>() == true
        && ps.TryGet(viewerOne.Id)!.Everyday.Reads?.SingleOrDefault() == "fr" && ps.TryGet(viewerOne.Id)!.FindLibrary(moviesFolder.Id)?.Settings.PrefersSdh == true,
        $"[{label}] Admin Import restores the exact GET person body including nested compact library ID");
    ps.Update(viewerOne.Id, current => { var copy = current!.Clone(); copy.Everyday.Reads = new[] { "de" }; copy.Libraries.Clear(); return copy; });
    var wirePersonalRestore = await Send(h, HttpMethod.Post, "/Babelfin/Me/Import", user, wire.Text);
    Check(wirePersonalRestore.Status == 200 && ps.TryGet(viewerOne.Id)!.Everyday.Reads?.SingleOrDefault() == "fr"
        && ps.TryGet(viewerOne.Id)!.FindLibrary(moviesFolder.Id)?.Settings.PrefersSdh == true,
        $"[{label}] Me Import restores the exact GET person body including nested compact library ID");
    var beforeInvalidGuid = File.ReadAllBytes(ps.FilePath(viewerOne.Id));
    var invalidGuidBody = wire.Text.Replace(viewerOne.Id.ToString(exactCaseHost ? "N" : "D"), "invalid-guid", StringComparison.Ordinal);
    var invalidAdminGuid = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, "{\"Users\":[" + invalidGuidBody + "]}");
    var invalidPersonalGuid = await Send(h, HttpMethod.Post, "/Babelfin/Me/Import", user, invalidGuidBody);
    Check(invalidAdminGuid.Status == 400 && invalidPersonalGuid.Status == 400 && File.ReadAllBytes(ps.FilePath(viewerOne.Id)).SequenceEqual(beforeInvalidGuid),
        $"[{label}] invalid GUIDs return 400 from both imports without changing stored bytes");
    Check(JsonDocument.Parse(File.ReadAllBytes(ps.FilePath(viewerOne.Id))).RootElement.GetProperty("userId").GetString() == viewerOne.Id.ToString("D"),
        $"[{label}] imported wire GUIDs retain canonical D-format user IDs on disk");
    ps.Update(viewerOne.Id, _ => wireSource);
    foreach (var emptyBody in new[] { "{}", "{\"typo\":true}", "{\"users\":[]}" })
        Check((await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, emptyBody)).Status == 400, $"[{label}] no-useful-data admin import is rejected: {emptyBody}");
    Check((await Send(h, HttpMethod.Post, "/Babelfin/Me/Import", user, "{}")).Status == 400, $"[{label}] empty personal import cannot silently overwrite settings");
    var validationFixture = ps.TryGet(viewerOne.Id)!.Clone();
    ps.Update(viewerOne.Id, current =>
    {
        var copy = current!.Clone();
        copy.Overrides.Add(new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Settings = new SettingsLayer { Reads = new[] { "removed-alias" } } });
        copy.BurnedIn.Add(new BurnedInFlag { ItemId = Guid.NewGuid().ToString("N"), Languages = new List<string> { "removed-alias" } });
        copy.Libraries.Add(new LibraryLayer { LibraryId = "broken-old-id" });
        copy.CollectionOrder.Add("broken-old-id");
        return copy;
    });
    var staleOverrides = JsonSerializer.Serialize(ps.TryGet(viewerOne.Id)!.Overrides, BabelfinJson.Options);
    var staleFlags = JsonSerializer.Serialize(ps.TryGet(viewerOne.Id)!.BurnedIn, BabelfinJson.Options);
    var saveOptions = exactCaseHost ? Jellyfin.Extensions.Json.JsonDefaults.PascalCaseOptions : BabelfinJson.Options;
    foreach (var route in new[] { "/Babelfin/Me/Settings", $"/Babelfin/Admin/Users/{viewerOne.Id:N}/Settings" })
    {
        var save = await Send(h, HttpMethod.Put, route + "?keepOverrides=true&fields=everyday.reads", admin,
            JsonSerializer.Serialize(new PersonSettings { Everyday = new SettingsLayer { Reads = Array.Empty<string>() } }, saveOptions));
        Check(save.Status == 200 && ps.TryGet(viewerOne.Id)!.Everyday.Reads is { Length: 0 }
            && JsonSerializer.Serialize(ps.TryGet(viewerOne.Id)!.Overrides, BabelfinJson.Options) == staleOverrides
            && JsonSerializer.Serialize(ps.TryGet(viewerOne.Id)!.BurnedIn, BabelfinJson.Options) == staleFlags
            && ps.TryGet(viewerOne.Id)!.Libraries.All(row => Guid.TryParse(row.LibraryId, out var libraryId) && libraryId != Guid.Empty)
            && ps.TryGet(viewerOne.Id)!.CollectionOrder.Contains("broken-old-id"),
            $"[{label}] field-aware {route} saves the edited empty leaf, preserves untouched choices and retains repaired library ids");
        var beforeBadLeaf = File.ReadAllBytes(ps.FilePath(viewerOne.Id));
        var invalidLeaf = await Send(h, HttpMethod.Put, route + "?keepOverrides=true&fields=everyday.reads", admin,
            JsonSerializer.Serialize(new PersonSettings { Everyday = new SettingsLayer { Reads = new[] { "removed-alias" } } }, saveOptions));
        Check(invalidLeaf.Status == 400 && File.ReadAllBytes(ps.FilePath(viewerOne.Id)).SequenceEqual(beforeBadLeaf),
            $"[{label}] invalid edited leaf returns 400 without writing through {route}");
    }
    ps.Update(viewerOne.Id, _ => validationFixture);
    var firstBeforeImport = ps.TryGet(viewerOne.Id)!.Clone();
    var secondBeforeImport = File.ReadAllBytes(ps.FilePath(viewerTwo.Id));
    var firstIncoming = firstBeforeImport.Clone(); firstIncoming.Enabled = !firstIncoming.Enabled;
    var secondIncoming = ps.TryGet(viewerTwo.Id)!.Clone(); secondIncoming.Enabled = !secondIncoming.Enabled;
    var lateImportBody = JsonSerializer.Serialize(new ExportDocument { Users = new List<PersonSettings> { firstIncoming, secondIncoming } }, BabelfinJson.Options);
    const string lateFuture = "{\"schemaVersion\":999,\"futureValue\":true}";
    var switched = false;
    EventHandler<PersonStoreChangedEventArgs> replaceSecondAfterFirst = (_, change) =>
    {
        if (!switched && change.UserId == viewerOne.Id)
        {
            switched = true;
            File.WriteAllText(ps.FilePath(viewerTwo.Id), lateFuture, new UTF8Encoding(false));
        }
    };
    ps.Changed += replaceSecondAfterFirst;
    Resp partialImport;
    try { partialImport = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, lateImportBody); }
    finally { ps.Changed -= replaceSecondAfterFirst; }
    var partialBody = Body(partialImport);
    var partialRows = Property(Property(partialBody, "import"), "users") as JsonArray;
    Check(partialImport.Status == 409 && Property(partialBody, "applied")?.GetValue<bool>() == true && switched
        && ps.TryGet(viewerOne.Id)!.Enabled == firstIncoming.Enabled && File.ReadAllText(ps.FilePath(viewerTwo.Id), Encoding.UTF8) == lateFuture
        && partialRows?.Any(row => Guid.TryParse(Property(row, "userId")?.GetValue<string>(), out var id) && id == viewerTwo.Id && Property(row, "action")?.GetValue<string>() == "skip") == true,
        $"[{label}] a late import storage conflict reports applied=true and the skipped account after a successful earlier save");
    var beforeRefused = File.ReadAllBytes(ps.FilePath(viewerOne.Id));
    var refusedImport = await Send(h, HttpMethod.Post, "/Babelfin/Admin/Import?dryRun=false", admin, lateImportBody);
    Check(refusedImport.Status == 409 && Property(Body(refusedImport), "applied")?.GetValue<bool>() == false
        && File.ReadAllBytes(ps.FilePath(viewerOne.Id)).SequenceEqual(beforeRefused) && File.ReadAllText(ps.FilePath(viewerTwo.Id), Encoding.UTF8) == lateFuture,
        $"[{label}] preflight import refusal reports applied=false and leaves both files unchanged");
    File.WriteAllBytes(ps.FilePath(viewerTwo.Id), secondBeforeImport);
    Check(ps.Conflict(viewerTwo.Id, writing: true) is null, $"[{label}] restoring the compatible fixture clears its future-format guard");
    ps.Update(viewerOne.Id, _ => firstBeforeImport);
    var templateCurrent = ImportPlanner.CloneAdmin(plugin.Configuration);
    templateCurrent.NewUserTemplate = new PersonSettings { Everyday = new SettingsLayer { Understood = new[] { "en" }, Format = FormatPreference.TextOnly } };
    plugin.UpdateConfiguration(templateCurrent);
    var templateBody = new PluginConfiguration { NewUserTemplate = new PersonSettings { Everyday = new SettingsLayer { Understood = new[] { "de" } } } };
    var options = exactCaseHost ? Jellyfin.Extensions.Json.JsonDefaults.PascalCaseOptions : BabelfinJson.Options;
    var templateSave = await Send(h, HttpMethod.Put, "/Babelfin/Admin/Settings?fields=newUserTemplate.everyday.understood", admin, JsonSerializer.Serialize(templateBody, options));
    Check(templateSave.Status == 200 && plugin.Configuration.NewUserTemplate?.Everyday.Understood?.SingleOrDefault() == "de" && plugin.Configuration.NewUserTemplate.Everyday.Format == FormatPreference.TextOnly,
        $"[{label}] template language leaf preserves a concurrent hidden Advanced field");

    const string malformed = "{invalid";
    var badPath = ps.FilePath(viewerTwo.Id); var goodBefore = File.ReadAllBytes(ps.FilePath(viewerOne.Id));
    File.WriteAllText(badPath, malformed, new UTF8Encoding(false));
    var strictReset = await Send(h, HttpMethod.Post, "/Babelfin/Admin/ResetAll", admin);
    Check(strictReset.Status == 409 && File.ReadAllBytes(ps.FilePath(viewerOne.Id)).SequenceEqual(goodBefore), $"[{label}] malformed account blocks strict reset before changing healthy accounts");
    for (var attempt = 0; attempt < 2; attempt++)
    {
        var partialFile = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Export?allowPartial=true", admin);
        var data = Body(partialFile);
        Check(partialFile.Status == 200 && Property(data, "complete")?.GetValue<bool>() == false && Property(data, "skippedUsers") is JsonArray skipped && skipped.Any(row => Property(row, "userId")?.GetValue<string>() == viewerTwo.Id.ToString()),
            $"[{label}] export remembers the malformed account after quarantine, attempt {attempt + 1}");
    }
    File.WriteAllText(badPath, JsonSerializer.Serialize(new PersonSettings { UserId = viewerTwo.Id }, BabelfinJson.Options), new UTF8Encoding(false));
    var repairedFile = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Export", admin);
    Check(repairedFile.Status == 200 && Property(Body(repairedFile), "complete")?.GetValue<bool>() == true, $"[{label}] valid repair restores export eligibility while quarantine bytes remain");
    Check(Directory.GetFiles(Path.GetDirectoryName(badPath)!, viewerTwo.Id.ToString("N") + ".json.corrupt-*").All(path => File.ReadAllText(path, Encoding.UTF8) == malformed), $"[{label}] corrupt original bytes remain in recovery archives");
    var repairedSource = JsonSerializer.SerializeToUtf8Bytes(new PersonSettings { UserId = viewerTwo.Id, Everyday = new SettingsLayer { Reads = new[] { "fr" } } }, BabelfinJson.Options);
    var priorArchives = Directory.GetFiles(Path.GetDirectoryName(badPath)!, viewerTwo.Id.ToString("N") + ".json.corrupt-*");
    File.WriteAllText(badPath, malformed, new UTF8Encoding(false));
    using (var renameBlocker = new FileStream(badPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var blockedExport = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Export", admin);
        var archives = Directory.GetFiles(Path.GetDirectoryName(badPath)!, viewerTwo.Id.ToString("N") + ".json.corrupt-*");
        var addedArchives = archives.Except(priorArchives).ToArray();
        var preserved = OperatingSystem.IsWindows()
            ? File.Exists(badPath) && File.ReadAllText(badPath, Encoding.UTF8) == malformed && addedArchives.Length == 0 && archives.Length == priorArchives.Length
            : !File.Exists(badPath) && addedArchives.Length == 1 && archives.Length == priorArchives.Length + 1
                && File.ReadAllBytes(addedArchives[0]).SequenceEqual(Encoding.UTF8.GetBytes(malformed));
        Check(blockedExport.Status == 409 && preserved,
            $"[{label}] a corrupt source returns 409 and its exact bytes survive the platform's open-file rename behavior");
    }
    File.WriteAllBytes(badPath, repairedSource);
    var inPlaceRepair = await Send(h, HttpMethod.Get, "/Babelfin/Admin/Export", admin);
    Check(inPlaceRepair.Status == 200 && Property(Body(inPlaceRepair), "complete")?.GetValue<bool>() == true
        && ps.TryGet(viewerTwo.Id)?.Everyday.Reads?.SingleOrDefault() == "fr" && File.ReadAllBytes(badPath).SequenceEqual(repairedSource)
        && Directory.GetFiles(Path.GetDirectoryName(badPath)!, viewerTwo.Id.ToString("N") + ".json.corrupt-*").Length == priorArchives.Length + (OperatingSystem.IsWindows() ? 0 : 1)
        && Directory.GetFiles(Path.GetDirectoryName(badPath)!, viewerTwo.Id.ToString("N") + ".json.corrupt-*").All(path => File.ReadAllBytes(path).SequenceEqual(Encoding.UTF8.GetBytes(malformed))),
        $"[{label}] compatible in-place repair is reread by the same singleton store without restart");
    plugin.UpdateConfiguration(originalConfig);
    await h.DisposeAsync();
}

Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURES");
return failures == 0 ? 0 : 1;

static byte[] Decode(byte[] body, string encoding)
{
    if (string.IsNullOrEmpty(encoding)) return body;
    using var input = new MemoryStream(body);
    using Stream z = encoding == "br" ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    z.CopyTo(output);
    return output.ToArray();
}

static string Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt32().ToString() : "null";
static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : string.Empty;

// ════════════════════════ Types ════════════════════════
sealed record Resp(int Status, Dictionary<string, string> Headers, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);
    public string H(string name) => Headers.TryGetValue(name, out var v) ? v : string.Empty;
}

sealed record TestHost(WebApplication App, HttpClient Http, DecoratorStats Stats) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>A companion plugin fixture that rewrites the web page and owns its cache headers.</summary>
static class CompanionFixture
{
    public static void Register(IServiceCollection services, object host, string webDir)
    {
        services.AddSingleton(new CompanionFixtureOptions(webDir, BfHostSettings.Read(host)));
        services.AddTransient<IStartupFilter, CompanionFixtureStartupFilter>();
    }
}

sealed record CompanionFixtureOptions(string WebDir, BfHostSettings Host);

sealed class CompanionFixtureStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var options = app.ApplicationServices.GetService<CompanionFixtureOptions>();
        var config = app.ApplicationServices.GetRequiredService<IConfiguration>();
        if (options is not null && options.Host.Resolved && config.GetValue<bool>("hostwebclient"))
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                var root = options.Host.BaseUrl + "/web/";
                var redirects = options.Host.RedirectsToHttps && !context.Request.IsHttps;
                if (!redirects && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                    && (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.Equals(root + "index.html", StringComparison.OrdinalIgnoreCase)))
                {
                    var html = File.ReadAllText(Path.Combine(options.WebDir, "index.html"));
                    html = html.Insert(html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase), "<!-- Companion -->\n<script src=\"companion/boot.js?v=abc\" defer></script>\n");
                    var raw = Encoding.UTF8.GetBytes(html);
                    var etag = "\"companion-abc123-1\"";
                    context.Response.Headers.CacheControl = "no-cache";
                    context.Response.Headers.ETag = etag;
                    if (context.Request.Headers.IfNoneMatch.ToString().Contains(etag)) { context.Response.StatusCode = 304; return; }
                    var accept = context.Request.Headers.AcceptEncoding.ToString();
                    context.Response.ContentType = "text/html; charset=utf-8";
                    if (accept.Contains("br"))
                    {
                        using var ms = new MemoryStream();
                        using (var b = new BrotliStream(ms, CompressionLevel.Fastest, true)) b.Write(raw);
                        raw = ms.ToArray();
                        context.Response.Headers.ContentEncoding = "br";
                    }

                    context.Response.ContentLength = raw.Length;
                    if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(raw);
                    return; // The fixture owns this response and does not invoke the inner middleware.
                }

                await nextMiddleware(context);
            });
        }

        next(app);
    };
}

/// <summary>Echoes what MVC bound, so the PlaybackInfo action filter's changes are visible. Same names as Jellyfin's.</summary>
[ApiController]
[Microsoft.AspNetCore.Authorization.Authorize]
public class MediaInfoController : ControllerBase
{
    [HttpPost("Items/{itemId}/PlaybackInfo")]
    public ActionResult GetPostedPlaybackInfo(
        [FromRoute] Guid itemId,
        [FromQuery] Guid? userId,
        [FromQuery] int? maxStreamingBitrate,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? autoOpenLiveStream,
        [FromQuery] bool? enableDirectPlay,
        [FromQuery] bool? enableDirectStream,
        [FromQuery] bool? enableTranscoding,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PlaybackInfoDto? playbackInfoDto)
        => Ok(new
        {
            itemId,
            userId,
            audioStreamIndex,
            subtitleStreamIndex,
            mediaSourceId,
            dto = playbackInfoDto is null ? null : new
            {
                audioStreamIndex = playbackInfoDto.AudioStreamIndex,
                subtitleStreamIndex = playbackInfoDto.SubtitleStreamIndex,
                mediaSourceId = playbackInfoDto.MediaSourceId,
                userId = playbackInfoDto.UserId,
            },
        });
}

/// <summary>Stand-in for Jellyfin.Api's PlaybackInfoDto (unreachable from plugins; the filter reads it by reflection).</summary>
public class PlaybackInfoDto
{
    public int? AudioStreamIndex { get; set; }
    public int? SubtitleStreamIndex { get; set; }
    public string? MediaSourceId { get; set; }
    public Guid? UserId { get; set; }
    public object? DeviceProfile { get; set; }
}

static class Fx
{
    public static MediaStream Video(int i) => new() { Index = i, Type = MediaStreamType.Video, Codec = "h264", IsDefault = true };
    public static MediaStream Audio(int i, string lang, bool isDefault) => new() { Index = i, Type = MediaStreamType.Audio, Language = lang, Title = "Surround", IsDefault = isDefault, Codec = "aac" };
    public static MediaStream Sub(int i, string lang, bool isDefault, bool forced) => new() { Index = i, Type = MediaStreamType.Subtitle, Language = lang, IsDefault = isDefault, IsForced = forced, Codec = "ass" };

    /// <summary>Skyward Lantern Voyage S00E01: a representative stream layout with an invented title.</summary>
    public static List<MediaStream> SlvStreams() => new()
    {
        Video(0), Audio(1, "eng", true), Audio(2, "jpn", false), Sub(3, "eng", true, true), Sub(4, "eng", false, false), Sub(5, "eng", false, false), Sub(6, "eng", false, false),
    };

    public static MediaSourceInfo Source(string id, string path, List<MediaStream> streams)
        => new() { Id = id, Name = Path.GetFileNameWithoutExtension(path), Path = path, Protocol = MediaProtocol.File, Container = "mkv", MediaStreams = streams };

    public static MediaStream Clone(MediaStream s) => new()
    {
        Index = s.Index, Type = s.Type, Language = s.Language, Title = s.Title, IsDefault = s.IsDefault, IsForced = s.IsForced, Codec = s.Codec, IsExternal = s.IsExternal,
    };
}

public sealed class FixtureStore
{
    private readonly Dictionary<Guid, MediaSourceInfo[]> _sources = new();
    public void Add(BaseItem item, params MediaSourceInfo[] sources) => _sources[item.Id] = sources;

    public List<MediaSourceInfo> Clone(Guid itemId) => (_sources.TryGetValue(itemId, out var s) ? s : Array.Empty<MediaSourceInfo>())
        .Select(x => new MediaSourceInfo
        {
            Id = x.Id, Name = x.Name, Path = x.Path, Protocol = x.Protocol, Container = x.Container,
            MediaStreams = x.MediaStreams.Select(Fx.Clone).ToList(),
        })
        .ToList();
}

/// <summary>Jellyfin's MediaSourceManager reduced to what matters: own sources per item, Jellyfin-like defaults, call log.</summary>
public sealed class FakeInnerMediaSourceManager : IMediaSourceManager, IDisposable
{
    private readonly FixtureStore _store;
    public FakeInnerMediaSourceManager(FixtureStore store) { _store = store; }
    public List<(string Name, object?[] Args)> Calls { get; } = new();
    public int DisposeCount { get; private set; }
    public IReadOnlyList<MediaStream> StreamsResult { get; } = new List<MediaStream>();
    public IReadOnlyList<MediaAttachment> AttachmentsResult { get; } = new List<MediaAttachment>();
    public (string Name, object?[] Args)? Last(string name) => Calls.LastOrDefault(c => c.Name == name) is { Name: not null } c ? c : null;
    private void Log(string name, params object?[] args) { lock (Calls) Calls.Add((name, args)); }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => Log(nameof(AddParts), providers);
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) { Log(nameof(GetMediaStreams), itemId); return StreamsResult; }
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) { Log(nameof(GetMediaStreams), query); return StreamsResult; }
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) { Log(nameof(GetMediaAttachments), itemId); return AttachmentsResult; }
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) { Log(nameof(GetMediaAttachments), query); return AttachmentsResult; }

    public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        Log(nameof(GetPlaybackMediaSources), item, user, allowMediaProbe, enablePathSubstitution);
        // As the real one does: its own static sources (Babelfin's decorator is NOT called here).
        return Task.FromResult(GetStaticMediaSources(item, enablePathSubstitution, user));
    }

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
    {
        Log(nameof(GetStaticMediaSources), item, enablePathSubstitution, user);
        var sources = _store.Clone(item.Id);
        foreach (var s in sources) SetDefaults(s);
        return sources;
    }

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
    {
        Log(nameof(SetDefaultAudioAndSubtitleStreamIndices), item, source, user);
        SetDefaults(source);
    }

    private static void SetDefaults(MediaSourceInfo s)
    {
        // Jellyfin-like: the first default-flagged audio and subtitle tracks.
        s.DefaultAudioStreamIndex = s.MediaStreams.FirstOrDefault(m => m.Type == MediaStreamType.Audio && m.IsDefault)?.Index
            ?? s.MediaStreams.FirstOrDefault(m => m.Type == MediaStreamType.Audio)?.Index;
        s.DefaultSubtitleStreamIndex = s.MediaStreams.FirstOrDefault(m => m.Type == MediaStreamType.Subtitle && m.IsDefault)?.Index;
    }

    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
    { Log(nameof(GetMediaSource), item, mediaSourceId, liveStreamId); return Task.FromResult(_store.Clone(item.Id).First(s => s.Id == mediaSourceId)); }
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) { Log(nameof(OpenLiveStream), request); return Task.FromResult<LiveStreamResponse>(null!); }
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) { Log(nameof(OpenLiveStreamInternal), request); return Task.FromResult<Tuple<LiveStreamResponse, IDirectStreamProvider>>(null!); }
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) { Log(nameof(GetLiveStream), id); return Task.FromResult<MediaSourceInfo>(null!); }
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) { Log(nameof(GetLiveStreamWithDirectStreamProvider), id); return Task.FromResult<Tuple<MediaSourceInfo, IDirectStreamProvider>>(null!); }
    public ILiveStream GetLiveStreamInfo(string id) { Log(nameof(GetLiveStreamInfo), id); return null!; }
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) { Log(nameof(GetLiveStreamInfoByUniqueId), uniqueId); return null!; }
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(MediaBrowser.Controller.LiveTv.ActiveRecordingInfo info, CancellationToken cancellationToken) { Log(nameof(GetRecordingStreamMediaSources), info); return Task.FromResult<IReadOnlyList<MediaSourceInfo>>(new List<MediaSourceInfo>()); }
    public Task CloseLiveStream(string id) { Log(nameof(CloseLiveStream), id); return Task.CompletedTask; }
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) { Log(nameof(GetLiveStreamMediaInfo), id); return Task.FromResult<MediaSourceInfo>(null!); }
    public bool SupportsDirectStream(string path, MediaProtocol protocol) { Log(nameof(SupportsDirectStream), path, protocol); return true; }
    public MediaProtocol GetPathProtocol(string path) { Log(nameof(GetPathProtocol), path); return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaProtocol.Http : MediaProtocol.File; }
    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) { Log(nameof(AddMediaInfoWithProbe), mediaSource); return Task.CompletedTask; }
    public void Dispose() => DisposeCount++;
}

/// <summary>Another plugin's decorator, registered the same way Babelfin does (remove, re-add as a wrapping factory).</summary>
public class OtherPluginDecorator : DispatchProxy
{
    public IMediaSourceManager Inner { get; set; } = null!;
    public int StaticCalls { get; private set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IMediaSourceManager.GetStaticMediaSources)) StaticCalls++;
        try { return targetMethod.Invoke(Inner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }
}

static class OtherPlugin
{
    public static void Wrap(IServiceCollection services)
    {
        var d = services.Last(x => x.ServiceType == typeof(IMediaSourceManager));
        services.Remove(d);
        services.AddSingleton<IMediaSourceManager>(sp =>
        {
            var inner = d.ImplementationType is not null ? (IMediaSourceManager)ActivatorUtilities.CreateInstance(sp, d.ImplementationType)
                : d.ImplementationFactory is not null ? (IMediaSourceManager)d.ImplementationFactory(sp) : (IMediaSourceManager)d.ImplementationInstance!;
            var proxy = DispatchProxy.Create<IMediaSourceManager, OtherPluginDecorator>();
            ((OtherPluginDecorator)(object)proxy).Inner = inner;
            return proxy;
        });
    }
}

sealed class ThrowingDecider : ITrackDecider
{
    public TrackDecision? Decide(BaseItem item, MediaSourceInfo source, User user) => throw new InvalidOperationException("decider exploded");
}

// No plugin is disabled during ordinary harness shutdown; restoration behavior has dedicated tests.
public class FakePluginManager : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "GetPlugin" ? null : throw new NotSupportedException(targetMethod?.Name);
}

public class FakeUserManager : DispatchProxy
{
    public List<User> Users { get; } = new();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name switch
        {
            "GetUserById" => Users.FirstOrDefault(u => u.Id == (Guid)args![0]!),
            "GetUsers" => Users.AsEnumerable(),
            "GetUsersIds" => Users.Select(u => u.Id),
            "UpdateConfigurationAsync" => UpdateConfiguration((Guid)args![0]!, (UserConfiguration)args[1]!),
            _ => throw new NotSupportedException("FakeUserManager." + targetMethod.Name),
        };

    // Only the two remember flags are applied: the part of a user's configuration Babelfin changes (and puts back).
    private Task UpdateConfiguration(Guid userId, UserConfiguration config)
    {
        var user = Users.First(u => u.Id == userId);
        user.RememberAudioSelections = config.RememberAudioSelections;
        user.RememberSubtitleSelections = config.RememberSubtitleSelections;
        return Task.CompletedTask;
    }
}

/// <summary>Jellyfin's library manager reduced to what Babelfin asks: items by id (with per-user hiding), parents, collection folders, virtual folders, item lists per library.</summary>
public class FakeLibraryManager : DispatchProxy
{
    private readonly Dictionary<Guid, BaseItem> _items = new();
    public HashSet<(Guid Item, Guid User)> Hidden { get; } = new();
    public List<CollectionFolder> Folders { get; } = new();

    /// <summary>Items that sit in several libraries: the libraries, in order, that GetCollectionFolders answers for them (instead of the one their parents lead to).</summary>
    public Dictionary<Guid, List<CollectionFolder>> InLibraries { get; } = new();

    /// <summary>How often each kind of lookup was asked, so a test can tell one query from one per item.</summary>
    public int ItemListCalls { get; private set; }
    public int ItemByIdForUserCalls { get; private set; }
    public int CollectionFolderCalls { get; private set; }
    public void ResetCounters() { ItemListCalls = 0; ItemByIdForUserCalls = 0; CollectionFolderCalls = 0; }

    /// <summary>Called with the id at the start of every GetItemById: a test uses it to make something happen while a request is between its first read of the person's document and its save (the access check looks items up in that gap).</summary>
    public Action<Guid>? OnItemLookup { get; set; }

    public void Add(BaseItem item) => _items[item.Id] = item;
    public void Remove(Guid id) { _items.Remove(id); InLibraries.Remove(id); }

    public void RemoveFolder(CollectionFolder folder) { Folders.Remove(folder); Remove(folder.Id); }

    public CollectionFolder AddFolder(string name)
    {
        var folder = new CollectionFolder { Id = Guid.NewGuid(), Name = name, Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bf-harness-libs", name) };
        Folders.Add(folder);
        Add(folder);
        return folder;
    }

    public Series AddSeries(CollectionFolder folder, string name, string? originalLanguage, params string[] genres)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = name, ParentId = folder.Id, OriginalLanguage = originalLanguage, Genres = genres };
        Add(series);
        return series;
    }

    public Season AddSeason(Series series, int number)
    {
        var season = new Season { Id = Guid.NewGuid(), Name = number == 0 ? "Specials" : "Season " + number, IndexNumber = number, ParentId = series.Id, SeriesId = series.Id, SeriesName = series.Name };
        Add(season);
        return season;
    }

    public Episode AddEpisode(Season season, Guid id, string name)
    {
        var episode = new Episode { Id = id, Name = name, ParentId = season.Id, SeasonId = season.Id, SeriesId = season.SeriesId, SeriesName = season.SeriesName };
        Add(episode);
        return episode;
    }

    private BaseItem? Parent(BaseItem item) => item.ParentId != Guid.Empty && _items.TryGetValue(item.ParentId, out var p) ? p : null;

    private bool Under(BaseItem item, Guid folderId)
    {
        for (var cur = Parent(item); cur is not null; cur = Parent(cur))
        {
            if (cur.Id == folderId) return true;
        }

        return false;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        switch (name)
        {
            case "GetItemById":
            {
                var id = (Guid)args![0]!;
                OnItemLookup?.Invoke(id);
                if (args.Length > 1 && args[1] is User) ItemByIdForUserCalls++;
                if (!_items.TryGetValue(id, out var item)) return null;
                if (args.Length > 1 && args[1] is User u && Hidden.Contains((id, u.Id))) return null;
                if (args.Length > 1 && args[1] is Guid uid && Hidden.Contains((id, uid))) return null;
                return targetMethod.ReturnType.IsInstanceOfType(item) ? item : null;
            }

            case "GetCollectionFolders":
            {
                CollectionFolderCalls++;
                var result = new List<Folder>(); // the interface returns List<Folder>
                if (InLibraries.TryGetValue(((BaseItem)args![0]!).Id, out var listed)) { result.AddRange(listed); return result; }
                for (var cur = (BaseItem?)args![0]; cur is not null; cur = Parent(cur))
                {
                    if (cur is CollectionFolder folder) { result.Add(folder); break; }
                }

                return result;
            }

            case "GetVirtualFolders":
                return Folders.Select(f => new VirtualFolderInfo { Name = f.Name, ItemId = f.Id.ToString("N"), CollectionType = f.CollectionType is null ? null : (MediaBrowser.Model.Entities.CollectionTypeOptions?)Enum.Parse<MediaBrowser.Model.Entities.CollectionTypeOptions>(f.CollectionType.Value.ToString(), true), Locations = Array.Empty<string>() }).ToList();

            case "GetItemList":
            {
                ItemListCalls++;
                var query = (InternalItemsQuery)args![0]!;
                var kinds = query.IncludeItemTypes ?? Array.Empty<BaseItemKind>();
                if (query.ItemIds.Length > 0)
                {
                    // By id: what the person can see among them (the fake's per-person hiding), of the asked types.
                    return (IReadOnlyList<BaseItem>)query.ItemIds
                        .Select(id => _items.TryGetValue(id, out var found) ? found : null)
                        .OfType<BaseItem>()
                        .Where(i => (query.User is null || !Hidden.Contains((i.Id, query.User.Id))) && (kinds.Length == 0 || kinds.Contains(i.GetBaseItemKind())))
                        .ToList();
                }

                var ancestors = query.AncestorIds ?? Array.Empty<Guid>();
                return (IReadOnlyList<BaseItem>)_items.Values
                    .Where(i => (ancestors.Length > 0 ? ancestors.Any(id => Under(i, id)) : query.ParentId == Guid.Empty || Under(i, query.ParentId))
                        && (query.User is null || !Hidden.Contains((i.Id, query.User.Id))) && (kinds.Length == 0 || kinds.Contains(i.GetBaseItemKind())))
                    .Take(query.Limit ?? int.MaxValue).ToList();
            }

            default:
                throw new NotSupportedException("FakeLibraryManager." + name);
        }
    }
}

/// <summary>Live TV's recordings manager with nothing recording.</summary>
public class FakeRecordingsManager : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod!.ReturnType;
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>A collection manager with a hand-filled membership table.</summary>
public class FakeCollectionManager : DispatchProxy
{
    public Dictionary<Guid, List<BoxSet>> Membership { get; } = new();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        if (name != nameof(ICollectionManager.GetCollectionsContainingItem)) throw new NotSupportedException("FakeCollectionManager." + name);
        return Membership.TryGetValue((Guid)args![1]!, out var sets) ? sets.AsEnumerable() : Enumerable.Empty<BoxSet>();
    }
}

/// <summary>Jellyfin's user data reduced to a per-(user, item) table in memory (compatibility mode writes and reads it).</summary>
public class FakeUserDataManager : DispatchProxy
{
    private readonly Dictionary<(Guid, Guid), UserItemData> _data = new();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        switch (name)
        {
            case "GetUserData":
            {
                var user = (User)args![0]!; var item = (BaseItem)args[1]!;
                lock (_data)
                {
                    if (!_data.TryGetValue((user.Id, item.Id), out var data)) { data = new UserItemData { Key = item.Id.ToString("N") }; _data[(user.Id, item.Id)] = data; }
                    return data;
                }
            }

            case "SaveUserData":
            {
                var user = (User)args![0]!; var item = (BaseItem)args[1]!;
                if (args[2] is UserItemData data) lock (_data) _data[(user.Id, item.Id)] = data;
                return null;
            }

            default:
                throw new NotSupportedException("FakeUserDataManager." + name);
        }
    }
}

/// <summary>The server configuration as Jellyfin ships it: the preferred metadata language defaults to "en" (never a person's language without "assumed").</summary>
public class FakeServerConfiguration : DispatchProxy
{
    public static ServerConfiguration Configuration { get; } = new();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        if (name is "get_Configuration" or "get_CommonConfiguration") return Configuration;
        throw new NotSupportedException("FakeServerConfiguration." + name);
    }
}

public class FakeLocalization : DispatchProxy
{
    private static readonly CultureDto[] Cultures =
    {
        new("en", "English", "en", new[] { "eng" }),
        new("ja", "Japanese", "ja", new[] { "jpn" }),
        new("zh", "Chinese", "zh", new[] { "zho", "chi" }),
    };

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name != "FindLanguageInfo") throw new NotSupportedException("FakeLocalization." + targetMethod.Name);
        var lang = (string)args![0]!;
        return Cultures.FirstOrDefault(c => string.Equals(c.Name, lang, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.TwoLetterISOLanguageName, lang, StringComparison.OrdinalIgnoreCase)
            || c.ThreeLetterISOLanguageNames.Contains(lang, StringComparer.OrdinalIgnoreCase));
    }
}

sealed class LogCapture : ILoggerProvider
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new L(this);
    public void Dispose() { }
    private sealed class L(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (owner.Entries) owner.Entries.Add((logLevel, formatter(state, exception))); }
    }
}

// ── Fakes shaped like Jellyfin's real types, read by the plugins through reflection ──
public sealed class FakeNetworkConfig { public string BaseUrl { get; set; } = ""; public bool RequireHttps { get; set; } }
public sealed class FakeConfigManager
{
    private readonly FakeNetworkConfig _n;
    public FakeConfigManager(FakeNetworkConfig n) { _n = n; }
    public object GetConfiguration(string key) => key == "network" ? _n : throw new ArgumentException(key);
}

public sealed class FakeAppHost : MediaBrowser.Controller.IServerApplicationHost
{
    public FakeAppHost(FakeConfigManager c, bool https) { ConfigurationManager = c; ListenWithHttps = https; }
    public FakeConfigManager ConfigurationManager { get; }
    public bool ListenWithHttps { get; }
#pragma warning disable CS0067
    public event EventHandler? HasPendingRestartChanged;
#pragma warning restore CS0067
    public bool CoreStartupHasCompleted => true;
    public int HttpPort => 8096;
    public int HttpsPort => 8920;
    public string FriendlyName => "harness";
    public string RestoreBackupPath { get; set; } = string.Empty;
    public string Name => "Jellyfin Server";
    public string SystemId => "harness";
    public bool HasPendingRestart => false;
    public bool ShouldRestart { get; set; }
    public Version ApplicationVersion => new(12, 2, 0);
    public IServiceProvider? ServiceProvider { get; set; }
    public string ApplicationVersionString => "12.2.0";
    public string ApplicationUserAgent => "Jellyfin/12.2.0";
    public string ApplicationUserAgentAddress => "https://jellyfin.org";
    public string GetSmartApiUrl(HttpRequest request) => throw new NotSupportedException();
    public string GetSmartApiUrl(IPAddress remoteAddr) => throw new NotSupportedException();
    public string GetSmartApiUrl(string hostname) => throw new NotSupportedException();
    public string GetApiUrlForLocalAccess(IPAddress? ipAddress = null, bool allowHttps = true) => throw new NotSupportedException();
    public string GetLocalApiUrl(string hostname, string? scheme = null, int? port = null) => throw new NotSupportedException();
    public string ExpandVirtualPath(string path) => path;
    public string ReverseVirtualPath(string path) => path;
    public IEnumerable<Assembly> GetApiPluginAssemblies() => Array.Empty<Assembly>();
    public void NotifyPendingRestart() { }
    public IReadOnlyCollection<T> GetExports<T>(bool manageLifetime = true) => Array.Empty<T>();
    public IReadOnlyCollection<T> GetExports<T>(MediaBrowser.Common.CreationDelegateFactory defaultFunc, bool manageLifetime = true) => Array.Empty<T>();
    public IEnumerable<Type> GetExportTypes<T>() => Array.Empty<Type>();
    public T Resolve<T>() => throw new NotSupportedException();
    public void Init(IServiceCollection serviceCollection) { }
}

public sealed class FakePaths : IApplicationPaths
{
    public FakePaths(string root, string web) { ProgramDataPath = root; WebPath = web; }
    public string ProgramDataPath { get; }
    public string WebPath { get; }
    public string ProgramSystemPath => ProgramDataPath;
    public string DataPath => ProgramDataPath;
    public string ImageCachePath => ProgramDataPath;
    public string PluginsPath => Path.Combine(ProgramDataPath, "plugins");
    public string PluginConfigurationsPath => Path.Combine(ProgramDataPath, "plugins", "configurations");
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

public sealed class FakeXml : IXmlSerializer
{
    public object DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream)!;
    public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);
    public void SerializeToFile(object obj, string file) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); using var f = File.Create(file); SerializeToStream(obj, f); }
    public object DeserializeFromFile(Type type, string file) { using var f = File.OpenRead(file); return DeserializeFromStream(type, f); }
    public object DeserializeFromBytes(Type type, byte[] buffer) { using var m = new MemoryStream(buffer); return DeserializeFromStream(type, m); }
}

/// <summary>Jellyfin-like auth: X-Fake-User gives the Jellyfin-UserId claim; X-Fake-Admin: 1 passes RequiresElevation.</summary>
sealed class FakeAuth : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public FakeAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : base(o, l, e) { }
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Fake-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.Name, "tester"), new("Jellyfin-UserId", user.ToString()) };
        if (Request.Headers["X-Fake-Admin"] == "1") claims.Add(new Claim("FakeAdmin", "1"));
        var id = new ClaimsIdentity(claims, "Fake");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Fake")));
    }
}
