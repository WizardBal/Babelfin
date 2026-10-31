using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using MediaBrowser.Model.Entities;

var runtimeOption = Array.IndexOf(args, "--runtime");
var runtime = runtimeOption >= 0 && runtimeOption + 1 < args.Length ? args[runtimeOption + 1] : Environment.GetEnvironmentVariable("JELLYFIN_RUNTIME");
if (string.IsNullOrWhiteSpace(runtime)) throw new ArgumentException("Supply --runtime with a Jellyfin 12.2 runtime folder, or set JELLYFIN_RUNTIME.");
var rig = Path.GetFullPath(runtime);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var dependency = Path.Combine(rig, name.Name + ".dll");
    return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};
var assemblyPath = Path.Combine(rig, "Emby.Server.Implementations.dll");
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
Console.WriteLine($"Native assembly: {assembly.FullName}; MVID={assembly.ManifestModule.ModuleVersionId}");
Console.WriteLine($"Path={assembly.Location}; SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath)))}");
if (args.Contains("--inspect-normalization")) { NativePolicyCheck.NormalizationContract.Inspect(assembly); return 0; }
var type = assembly.GetType("Emby.Server.Implementations.Library.MediaStreamSelector", throwOnError: true)!;
var choose = type.GetMethod("GetDefaultSubtitleStreamIndex", BindingFlags.Public | BindingFlags.Static)!;
var score = type.GetMethod("SetSubtitleStreamScores", BindingFlags.Public | BindingFlags.Static)!;
var modeType = choose.GetParameters()[2].ParameterType;
Console.WriteLine($"Oracle: {choose}; scoring: {score}");
var services = new EngineServices(null);
Track Audio(int index, string? language) => new(index, TrackType.Audio, language, null, null, null, "aac", 2, index == 1, false, false, false, false);
Track Subtitle(int index, string? language, int flags) => new(index, TrackType.Subtitle, language, null, null, null, "subrip", null, (flags & 1) != 0, (flags & 2) != 0, false, false, (flags & 4) != 0, SubtitleFormat.Text);
MediaStream ToNative(Track track) => new()
{
    Index = track.Index, Type = track.Type == TrackType.Audio ? MediaStreamType.Audio : MediaStreamType.Subtitle,
    Language = track.Language, IsDefault = track.IsDefault, IsForced = track.IsForced, IsExternal = track.IsExternal,
    IsHearingImpaired = track.IsHearingImpaired, Codec = track.Codec,
};
int? Native(Track[] tracks, string mode, string? preferred, string? audio, IReadOnlyList<string>? normalized = null)
{
    var streams = tracks.Select(ToNative).ToArray();
    var parameters = new object?[] { streams, normalized ?? (preferred is null ? Array.Empty<string>() : new[] { preferred }), Enum.Parse(modeType, mode), audio };
    score.Invoke(null, parameters);
    return (int?)choose.Invoke(null, parameters);
}
var failures = 0;
if (!args.Contains("--benchmark-only"))
{
    var modes = new[] { "Default", "Smart", "Always", "OnlyForced", "None" };
    string?[] languages = { "eng", "jpn", "und", null };
    var fixtures = new List<(string Name, Track[] Tracks)>
    {
        ("plan-forced-signs", new[] { Subtitle(3, "eng", 3), Subtitle(4, "eng", 0) }),
        ("smart-full-only", new[] { Subtitle(4, "eng", 0) }),
        ("default-signs-title", new[] { Subtitle(3, "eng", 1) with { Title = "Signs & Songs" }, Subtitle(4, "eng", 0) }),
        ("no-subtitles", Array.Empty<Track>()),
    };
    for (var left = 0; left < 32; left++)
    for (var right = 0; right < 32; right++)
        fixtures.Add(($"pair-{left}-{right}", new[] { Subtitle(3, languages[left / 8], left % 8), Subtitle(4, languages[right / 8], right % 8) }));
    var tested = 0;
    var byMode = new Dictionary<string, int>();
    foreach (var (name, tracks) in fixtures)
    foreach (var mode in modes)
    foreach (var preferred in new string?[] { "eng", "jpn", null })
    foreach (var audio in new string?[] { "eng", "jpn", null })
    {
        var expected = Native(tracks, mode, preferred, audio);
        var actual = VideoSubtitleSelector.Choose(services.Classifier.ClassifyAll(tracks), mode, preferred, audio);
        tested++;
        if (expected != actual)
        {
            failures++;
            byMode[mode] = byMode.GetValueOrDefault(mode) + 1;
            if (failures <= 16) Console.WriteLine($"MISMATCH {name} mode={mode} preferred={preferred ?? "<none>"} audio={audio ?? "<none>"}: native={expected?.ToString() ?? "off"}, port={actual?.ToString() ?? "off"}; {string.Join("; ", tracks.Select(t => $"#{t.Index} lang={t.Language ?? "<none>"} D={t.IsDefault} F={t.IsForced} X={t.IsExternal}"))}");
        }
    }
    // Three-track cases exercise ordering, codec/image flags and hearing impairment.
    var random = new Random(1701);
    for (var fixture = 0; fixture < 1000; fixture++)
    {
        var tracks = Enumerable.Range(3, 3).Select(i => Subtitle(i, languages[random.Next(languages.Length)], random.Next(8)) with
        {
            Codec = random.Next(2) == 0 ? "subrip" : "hdmv_pgs_subtitle",
            IsHearingImpaired = random.Next(2) == 0,
        }).ToArray();
        foreach (var mode in modes)
        foreach (var preferred in new string?[] { "eng", "jpn", null })
        foreach (var audio in new string?[] { "eng", "jpn", null })
        {
            var expected = Native(tracks, mode, preferred, audio);
            var actual = VideoSubtitleSelector.Choose(services.Classifier.ClassifyAll(tracks), mode, preferred, audio);
            tested++;
            if (expected != actual)
            {
                failures++;
                byMode[mode] = byMode.GetValueOrDefault(mode) + 1;
                if (failures <= 16) Console.WriteLine($"MISMATCH triple-{fixture} mode={mode} preferred={preferred ?? "<none>"} audio={audio ?? "<none>"}: native={expected?.ToString() ?? "off"}, port={actual?.ToString() ?? "off"}; {string.Join("; ", tracks.Select(t => $"#{t.Index} lang={t.Language ?? "<none>"} D={t.IsDefault} F={t.IsForced} X={t.IsExternal} codec={t.Codec}"))}");
            }
        }
    }
    // Mixed ISO aliases, regional tags, undefined spellings and case must obey the DLL too.
    foreach (var streamLanguage in new string?[] { "en", "ENG", "unknown", "undetermined", "mul", "zxx", "pt-BR", "pt-PT", "chi", "zh-Hant", "yue" })
    foreach (var mode in modes)
    foreach (var preferred in new[] { "eng", "pt-BR", "chi" })
    foreach (var audio in new[] { "eng", "jpn", "en" })
    {
        var tracks = new[] { Subtitle(3, streamLanguage, 2), Subtitle(4, preferred, 0) };
        var expected = Native(tracks, mode, preferred, audio);
        var actual = VideoSubtitleSelector.Choose(services.Classifier.ClassifyAll(tracks), mode, preferred, audio);
        tested++;
        if (expected != actual)
        {
            failures++;
            byMode[mode] = byMode.GetValueOrDefault(mode) + 1;
            if (failures <= 16) Console.WriteLine($"MISMATCH language-edge stream={streamLanguage} mode={mode} preferred={preferred} audio={audio}: native={expected?.ToString() ?? "off"}, port={actual?.ToString() ?? "off"}");
        }
    }
    // Use the ACTUAL outer MSM normalizer, including every three-letter alias.
    var normalize = NativePolicyCheck.NormalizationContract.CreateNormalizer(assembly);
    string?[] aliasLanguages = { "eng", "ger", "deu", "de", "pt-BR", "pt-PT", "und", null };
    var normalizedCases = 0;
    for (var left = 0; left < 64; left++)
    for (var right = 0; right < 64; right++)
    {
        var tracks = new[] { Subtitle(3, aliasLanguages[left / 8], left % 8), Subtitle(4, aliasLanguages[right / 8], right % 8) };
        var classified = services.Classifier.ClassifyAll(tracks);
        foreach (var raw in new string?[] { "en", "de", "pt-BR", null, "bf-unknown" })
        {
            var normalized = normalize(raw);
            foreach (var mode in modes)
            foreach (var audio in new[] { "eng", "ger", "deu", "de", "pt-BR", "pt-PT" })
            {
                var expected = Native(tracks, mode, raw, audio, normalized);
                var actual = VideoSubtitleSelector.Choose(classified, mode, raw, audio, normalized);
                tested++;
                normalizedCases++;
                if (expected != actual)
                {
                    failures++;
                    byMode[mode] = byMode.GetValueOrDefault(mode) + 1;
                    if (failures <= 16) Console.WriteLine($"MISMATCH normalized pair-{left}-{right} mode={mode} raw={raw ?? "<none>"} list=[{string.Join(",", normalized)}] audio={audio}: native={expected?.ToString() ?? "off"}, port={actual?.ToString() ?? "off"}");
                }
            }
        }
    }
    Console.WriteLine($"NORMALIZED PARITY cases={normalizedCases}; actual MediaSourceManager.NormalizeLanguage plus native score/choose");
    foreach (var mode in modes)
        Console.WriteLine($"POLICY EXAMPLES {mode}: forced-signs={Native(fixtures[0].Tracks, mode, "eng", "jpn")?.ToString() ?? "off"}; full-only={Native(fixtures[1].Tracks, mode, "eng", "jpn")?.ToString() ?? "off"}");
    Console.WriteLine($"PARITY cases={tested}; mismatches={failures}; {string.Join(", ", byMode.Select(p => p.Key + "=" + p.Value))}");
}
if (args.Contains("--parity-only")) return failures == 0 ? 0 : 1;
var benchTracks = new[] { Audio(1, "eng"), Audio(2, "jpn"), Audio(3, "ger") }
    .Concat(Enumerable.Range(4, 12).Select(i => Subtitle(i, i % 3 == 0 ? "jpn" : "eng", i == 4 ? 2 : 0))).ToArray();
var profile = new ResolvedProfile(
    Understood: new[] { "en" }, Reads: new[] { "en" }, UsesAudioDescription: false, PrefersSdh: false,
    AudioChain: new[] { new AudioStep(AudioStepKind.Language, "ja") }, AvoidCommentary: true,
    TieBreaks: ResolvedProfile.DefaultTieBreaks, UnlabelledAudio: UnlabelledAudioRule.OriginalWhenOnlyTrack,
    UnlabelledAudioLanguage: null, OriginalSource: OriginalLanguageSource.MetadataThenFlag,
    WhenAudioUnderstood: SubtitleWant.PartialOnly, WhenAudioNotUnderstood: SubtitleWant.Full,
    UnknownAudioCountsAsUnderstood: false, SubtitleLanguages: Array.Empty<string>(), Sdh: Preference.Neutral,
    Format: FormatPreference.PreferText, Embedded: EmbeddedPreference.Neutral, IfNothingMatches: WhenNothingMatches.ShowNothing);
var facts = ItemFacts.None with { OriginalLanguage = "ja" };
Console.WriteLine($"BENCH runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}; CPU={Environment.ProcessorCount}; tieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}; 15 tracks; Release; StopwatchFrequency={Stopwatch.Frequency}");
var sink = 0;
void Measure(string name, Action action, int count = 20000)
{
    var warmup = Stopwatch.StartNew();
    do { for (var i = 0; i < 1000; i++) action(); } while (warmup.ElapsedMilliseconds < 500);
    var times = new List<double>();
    var bytes = new List<double>();
    for (var sample = 0; sample < 7; sample++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++) action();
        var elapsed = Stopwatch.GetElapsedTime(start);
        times.Add(elapsed.TotalMicroseconds / count);
        bytes.Add((GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)count);
    }
    var ordered = times.Order().ToArray();
    Console.WriteLine($"BENCH {name}: samples_us=[{string.Join(", ", times.Select(t => t.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}]; median_us={ordered[3]:F3}; min_us={ordered[0]:F3}; max_us={ordered[^1]:F3}; median_bytes={bytes.Order().ElementAt(3):F0}; iterations/sample={count}");
}
Measure("cached-decision-deferred-trace", () => sink ^= services.Selector.Decide(benchTracks, facts, profile, includeTrace: false, sourceId: "benchmark").SubtitleIndex ?? -1);
Measure("cached-decision-eager-trace", () => sink ^= services.Selector.Decide(benchTracks, facts, profile, includeTrace: true, sourceId: "benchmark").SubtitleIndex ?? -1);
Measure("cached-decision-and-explain", () => sink ^= services.Selector.Decide(benchTracks, facts, profile, includeTrace: false, sourceId: "benchmark").Explain().Length, 5000);
Measure("uncached-fresh-selector", () => sink ^= new TrackSelector(services.Languages, services.Classifier).Decide(benchTracks, facts, profile, includeTrace: false, sourceId: "benchmark").SubtitleIndex ?? -1, 5000);
Measure("cached-decider-fixed-resolver-deferred-trace", NativePolicyCheck.AdapterBenchmark.Create(benchTracks, profile));
var nativeProfile = profile with { WhenAudioUnderstood = SubtitleWant.VideoDefault, WhenAudioNotUnderstood = SubtitleWant.VideoDefault };
var nativeFacts = facts with { JellyfinSubtitleMode = "Smart", JellyfinSubtitleLanguage = "en", JellyfinSubtitleLanguages = new[] { "eng" } };
Measure("cached-native-policy-normalized-deferred", () => sink ^= services.Selector.Decide(benchTracks, nativeFacts, nativeProfile, includeTrace: false, sourceId: "native-benchmark").SubtitleIndex ?? -1);
Measure("cached-native-adapter-real-localization-deferred", NativePolicyCheck.AdapterBenchmark.Create(benchTracks, nativeProfile, NativePolicyCheck.NormalizationContract.CreateLocalization(assembly)));
GC.KeepAlive(sink);
return failures == 0 ? 0 : 1;
