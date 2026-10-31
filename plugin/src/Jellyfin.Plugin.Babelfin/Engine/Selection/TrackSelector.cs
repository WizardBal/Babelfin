using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;

namespace Jellyfin.Plugin.Babelfin.Engine.Selection;

/// <summary>
/// Decides an audio track and a subtitle track (or none) together, with an explanation trace.
/// Pure and deterministic: no I/O, no shared mutable state; safe to share between threads. Input order doesn't matter (tracks are
/// processed by stream index).
/// </summary>
/// <remarks>
/// Selection behavior:
/// <list type="bullet">
/// <item>An unlabelled audio track is assumed per <see cref="ResolvedProfile.UnlabelledAudio"/> before any step runs; the assumption is used for
/// matching steps and for "understood", and the trace and explanation say "assumed".</item>
/// <item><see cref="ResolvedProfile.PrefersSdh"/> upgrades both "off" and "partial only" to "full", because SDH is a full transcript.</item>
/// <item>A remembered manual change matches only tracks in the same language (<see cref="TrackDescriptor.Score"/>); otherwise the chain decides,
/// with an "audio.manual.nomatch" / "subs.manual.nomatch" trace line.</item>
/// <item>Avoided audio (commentary; audio description unless the person uses it) is excluded from every step, including the file default and first track.
/// The fallback after all steps uses the tie-breaks over the remaining tracks, then over avoided ones.</item>
/// <item>Subtitles are ranked within a language by: exact before generic language match, device support, kind (per the SDH preference),
/// text/image preference, embedded/external preference, default flag, lowest index. The default flag therefore never lifts a partial track over a full one.</item>
/// <item>Burned-in languages are compared against every wanted subtitle language; any match means no subtitle track is picked.</item>
/// <item>A remembered manual subtitle choice is honoured even when the profile would have subtitles off; a remembered off choice stays off.</item>
/// <item>"If nothing matches" fallbacks apply only when full subtitles were wanted; a partial-only want that finds nothing shows nothing.</item>
/// <item>Generic forced metadata can satisfy a full request when no identified full-dialogue track exists in any requested language.
/// Explicit signs, songs or foreign-parts-only content remains partial.</item>
/// </list>
/// </remarks>
public sealed class TrackSelector
{
    private static readonly string[] LosslessCodecs = { "flac", "alac", "truehd", "mlp", "dts-hd ma", "dts-hd_ma", "dtshd_ma" };

    private readonly LanguageTable _languages;
    private readonly TrackClassifier _classifier;
    private readonly ClassifiedTrackCache _classified = new();

    /// <summary>Initializes a new instance of the <see cref="TrackSelector"/> class.</summary>
    /// <param name="languages">The language table.</param>
    /// <param name="classifier">The track classifier.</param>
    public TrackSelector(LanguageTable languages, TrackClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(classifier);
        _languages = languages;
        _classifier = classifier;
    }

    /// <summary>Decides audio and subtitles for one media source.</summary>
    /// <param name="tracks">The source's audio and subtitle tracks (other types are ignored by the classifier's caller).</param>
    /// <param name="item">Facts about the item.</param>
    /// <param name="profile">The person's resolved profile.</param>
    /// <param name="device">The app's capabilities, or null when unknown (everything supported).</param>
    /// <param name="manual">A remembered manual change, or null.</param>
    /// <returns>The decision with its trace.</returns>
    public Decision Decide(IReadOnlyList<Track> tracks, ItemFacts item, ResolvedProfile profile, DeviceCapabilities? device = null, ManualOverride? manual = null, bool includeTrace = true, string? sourceId = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(profile);
        var classified = _classified.Get(tracks, sourceId, _classifier);
        if (!includeTrace)
        {
            // The later diagnostic run must describe the same inputs even if a caller edits its collections.
            profile = profile with
            {
                Understood = profile.Understood.ToArray(), Reads = profile.Reads.ToArray(),
                AudioChain = profile.AudioChain.ToArray(), TieBreaks = profile.TieBreaks.ToArray(),
                SubtitleLanguages = profile.SubtitleLanguages.ToArray(),
                FieldSources = profile.FieldSources is null ? null : new Dictionary<string, string>(profile.FieldSources),
            };
            item = item with { BurnedInLanguages = item.BurnedInLanguages.ToArray(), JellyfinSubtitleLanguages = item.JellyfinSubtitleLanguages?.ToArray() };
        }
        var capabilities = device ?? new DeviceCapabilities();
        var decision = new Run(_languages, classified, item, profile, capabilities, manual, includeTrace).Execute();
        return includeTrace ? decision : decision with
        {
            Trace = new DeferredTrace(() => new Run(_languages, classified, item, profile, capabilities, manual, true).Execute().Trace),
        };
    }

    private static bool IsLossless(Track track) =>
        track.IsLossless
        || (track.Codec is { } codec
            && (LosslessCodecs.Any(c => string.Equals(c, codec.Trim(), StringComparison.OrdinalIgnoreCase))
                || codec.Trim().StartsWith("pcm", StringComparison.OrdinalIgnoreCase)));

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string KindWord(SubtitleKind kind) => kind switch
    {
        SubtitleKind.Partial => "partial",
        SubtitleKind.Sdh => "SDH",
        _ => "full",
    };

    private static string KindWord(ClassifiedTrack track) => TrackClassifier.HasUncertainForcedCoverage(track) ? "forced" : KindWord(track.SubtitleKind);

    private static string WantWord(SubtitleWant want) => want switch
    {
        SubtitleWant.Off => "off",
        SubtitleWant.PartialOnly => "partial",
        SubtitleWant.VideoDefault => "the video's default",
        _ => "full",
    };

    /// <summary>The state of one <see cref="Decide"/> call.</summary>
    private sealed class Run
    {
        private readonly LanguageTable _l;
        private readonly IReadOnlyList<ClassifiedTrack> _tracks;
        private readonly bool _captureTrace;
        private readonly List<ClassifiedTrack> _audio;
        private readonly List<ClassifiedTrack> _subs;
        private readonly ItemFacts _item;
        private readonly ResolvedProfile _p;
        private readonly DeviceCapabilities _device;
        private readonly ManualOverride? _manual;
        private readonly List<TraceLine> _trace = new();
        private readonly Dictionary<int, LanguageTag> _audioLang = new();
        private readonly HashSet<int> _assumed = new();
        private readonly Preference _sdh;
        private List<LanguageTag> _understood = new();
        private List<LanguageTag> _reads = new();
        private LanguageTag? _original;
        private LanguageTag? _libraryUsual;
        private bool _audioLeft;
        private bool _subtitlesLeft;
        private int? _chosenAudioIndex;

        public Run(LanguageTable languages, IReadOnlyList<ClassifiedTrack> tracks, ItemFacts item, ResolvedProfile profile, DeviceCapabilities device, ManualOverride? manual, bool captureTrace)
        {
            _l = languages;
            _tracks = tracks;
            _audio = tracks.Where(t => t.Track.Type == TrackType.Audio).ToList();
            _subs = tracks.Where(t => t.Track.Type == TrackType.Subtitle).ToList();
            _item = item;
            _p = profile;
            _device = device;
            _manual = manual;
            _captureTrace = captureTrace;
            _sdh = profile.PrefersSdh ? Preference.Prefer : profile.Sdh;
            foreach (var a in _audio)
            {
                if (a.Language is not null)
                {
                    _audioLang[a.Track.Index] = a.Language;
                }
            }
        }

        public Decision Execute()
        {
            _understood = ResolveList(_p.Understood, "My languages");
            _reads = ResolveList(_p.Reads, "Languages for subtitles");
            var subLangs = _p.SubtitleLanguages.Count > 0 ? ResolveList(_p.SubtitleLanguages, "your subtitle languages") : _reads;
            if (subLangs.Count == 0)
            {
                subLangs = _reads;
            }

            _libraryUsual = ResolveItem(_item.LibraryUsualOriginalLanguage, "this library's usual original language");
            ResolveOriginal();
            AssumeUnlabelled();

            var audio = ChooseAudio();
            _chosenAudioIndex = audio?.Track.Index;
            var understood = DecideUnderstood(audio, out var audioName, out var understoodWord);
            var sub = ChooseSubtitles(understood, audioName, understoodWord, subLangs, out var wanted);

            // Left to the video: the indices say what Jellyfin plays (for the chip and the admin tools); the flags say Babelfin sets nothing.
            var subtitleIndex = _subtitlesLeft
                ? (_item.JellyfinSubtitleIndex is int js && js >= 0 && _subs.Any(t => t.Track.Index == js) ? js : null)
                : sub?.Track.Index;
            return new Decision(
                audio?.Track.Index,
                subtitleIndex,
                wanted,
                understood,
                _tracks,
                _trace.ToArray(),
                _audioLeft,
                _subtitlesLeft);
        }

        // ---------- helpers ----------
        private void Add(string code, string text, int? index = null, params (string Key, string Value)[] args)
        {
            IReadOnlyDictionary<string, string>? dict = null;
            if (args.Length > 0)
            {
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, value) in args)
                {
                    d[key] = value;
                }

                dict = new ReadOnlyDictionary<string, string>(d);
            }

            _trace.Add(new TraceLine(code, text, index, dict));
        }

        private string Name(LanguageTag? tag) => tag is null ? "an unknown language" : _l.EnglishName(tag) ?? tag.Code;

        private LanguageTag? Lang(ClassifiedTrack audio) => _audioLang.TryGetValue(audio.Track.Index, out var tag) ? tag : null;

        private string AudioName(ClassifiedTrack audio)
        {
            var tag = Lang(audio);
            if (tag is null)
            {
                return "unknown language";
            }

            return _assumed.Contains(audio.Track.Index) ? Name(tag) + " (assumed)" : Name(tag);
        }

        private static string Label(ClassifiedTrack track)
        {
            var title = track.Track.Title?.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                return title.Length > 40 ? title[..39] + "…" : title;
            }

            if (track.Track.Type == TrackType.Subtitle)
            {
                return track.Track.IsForced ? "forced" : KindWord(track.SubtitleKind);
            }

            return TrackDescriptor.KindOf(track);
        }

        private string Names(IEnumerable<LanguageTag> tags)
        {
            var list = tags.Select(Name).ToList();
            return list.Count <= 3 ? string.Join(", ", list) : string.Join(", ", list.Take(3)) + " and others";
        }

        private List<LanguageTag> ResolveList(IReadOnlyList<string>? raw, string field)
        {
            var result = new List<LanguageTag>();
            if (raw is null)
            {
                return result;
            }

            foreach (var value in raw)
            {
                var tag = _l.Resolve(value);
                if (tag is null)
                {
                    if (_captureTrace) Add(TraceCodes.ProfileLanguageUnknown, $"\"{value}\" in {field} isn't a language Babelfin recognises, so it was ignored.", null, ("value", value ?? string.Empty), ("field", field));
                    continue;
                }

                if (!result.Any(t => string.Equals(t.Code, tag.Code, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(tag);
                }
            }

            return result;
        }

        private LanguageTag? ResolveItem(string? raw, string field, bool metadata = false)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var tag = metadata ? _l.ResolveMetadataOriginalLanguage(raw) : _l.Resolve(raw);
            if (tag is null)
            {
                if (_captureTrace) Add(TraceCodes.ProfileLanguageUnknown, $"\"{raw}\" ({field}) isn't a language Babelfin recognises, so it was ignored.", null, ("value", raw), ("field", field));
            }

            return tag;
        }

        // ---------- original language and unlabelled audio ----------
        // The table's original-language choice is metadata-only. Older multi-step chains retain their historical flag fallback.
        private bool MetadataOriginalChoice => _p.AudioChain.Count == 2
            && _p.AudioChain[0].Kind == AudioStepKind.OriginalLanguage
            && _p.AudioChain[1].Kind == AudioStepKind.VideoDefault
            && string.IsNullOrEmpty(_p.AudioChain[0].TitleContains);
        private void ResolveOriginal()
        {
            var meta = ResolveItem(_item.OriginalLanguage, "the title's original language", metadata: true);
            var flagged = MetadataOriginalChoice ? null : _audio.FirstOrDefault(a => a.Track.IsOriginal && a.Language is not null);
            var flag = flagged?.Language;
            var flagFrom = flagged is null ? string.Empty : $"the track flagged original, #{Num(flagged.Track.Index)}";
            string from;
            if (MetadataOriginalChoice || _p.OriginalSource == OriginalLanguageSource.MetadataThenFlag)
            {
                (_original, from) = meta is not null ? (meta, "metadata") : (flag, flagFrom);
            }
            else
            {
                (_original, from) = flag is not null ? (flag, flagFrom) : (meta, "metadata");
            }

            if (_original is not null)
            {
                if (_captureTrace) Add(TraceCodes.OriginalKnown, $"The original language is {Name(_original)} (from {from}).", flagged is not null && from == flagFrom ? flagged.Track.Index : null, ("language", _original.Code), ("name", Name(_original)), ("from", from));
            }
        }

        private void AssumeUnlabelled()
        {
            var usableCount = _audio.Count(a => !Avoided(a));
            foreach (var a in _audio.Where(a => a.Language is null))
            {
                LanguageTag? assumed;
                string why;
                switch (_p.UnlabelledAudio)
                {
                    case UnlabelledAudioRule.OriginalLanguage:
                        assumed = _original;
                        why = assumed is null ? "the original language is unknown" : "the server's setting for audio tracks with no language: the original language";
                        break;
                    case UnlabelledAudioRule.LibraryUsualOriginalLanguage:
                        assumed = _libraryUsual;
                        why = assumed is null ? "this library has no usual original language" : "the server's setting for audio tracks with no language: this library's usual original language";
                        break;
                    case UnlabelledAudioRule.SpecificLanguage:
                        assumed = ResolveItem(_p.UnlabelledAudioLanguage, "the server's language for audio tracks with no language");
                        why = assumed is null ? "no language is set for audio tracks with no language" : "the server's setting for audio tracks with no language";
                        break;
                    case UnlabelledAudioRule.OriginalWhenOnlyTrack:
                        assumed = usableCount == 1 && !Avoided(a) ? _original : null;
                        why = usableCount != 1 || Avoided(a)
                            ? "it isn't the only audio track"
                            : assumed is null ? "the original language is unknown" : "it's the only audio track, so it's taken as the original language";
                        break;
                    default:
                        assumed = null;
                        why = "the server's setting leaves audio tracks with no language unknown";
                        break;
                }

                var index = a.Track.Index;
                if (assumed is not null)
                {
                    _audioLang[index] = assumed;
                    _assumed.Add(index);
                    if (_captureTrace) Add(TraceCodes.AudioUnlabelledAssumed, $"Audio #{Num(index)} has no language; assumed {Name(assumed)} ({why}).", index, ("index", Num(index)), ("language", assumed.Code), ("name", Name(assumed)));
                }
                else
                {
                    if (_captureTrace) Add(TraceCodes.AudioUnlabelledUnknown, $"Audio #{Num(index)} has no language and stays unknown ({why}).", index, ("index", Num(index)));
                }
            }
        }

        // ---------- audio ----------
        private bool Avoided(ClassifiedTrack a) =>
            (a.AudioKind == AudioKind.Commentary && _p.AvoidCommentary)
            || (a.AudioKind == AudioKind.AudioDescription && !_p.UsesAudioDescription);

        private int CompareAudio(ClassifiedTrack x, ClassifiedTrack y)
        {
            int c;
            if (_p.UsesAudioDescription)
            {
                c = (y.AudioKind == AudioKind.AudioDescription).CompareTo(x.AudioKind == AudioKind.AudioDescription);
                if (c != 0)
                {
                    return c;
                }
            }

            foreach (var tieBreak in _p.TieBreaks)
            {
                c = tieBreak switch
                {
                    TieBreak.DirectPlayable => _device.CanDirectPlayAudio is { } canPlay ? canPlay(y.Track).CompareTo(canPlay(x.Track)) : 0,
                    TieBreak.MoreChannels => (y.Track.Channels ?? 0).CompareTo(x.Track.Channels ?? 0),
                    TieBreak.Lossless => IsLossless(y.Track).CompareTo(IsLossless(x.Track)),
                    TieBreak.IsOriginalFlag => y.Track.IsOriginal.CompareTo(x.Track.IsOriginal),
                    TieBreak.DefaultFlag => y.Track.IsDefault.CompareTo(x.Track.IsDefault),
                    _ => x.Track.Index.CompareTo(y.Track.Index),
                };
                if (c != 0)
                {
                    return c;
                }
            }

            return x.Track.Index.CompareTo(y.Track.Index);
        }

        private List<ClassifiedTrack> OrderAudio(IEnumerable<ClassifiedTrack> pool) =>
            pool.OrderBy(t => t, Comparer<ClassifiedTrack>.Create(CompareAudio)).ToList();

        private ClassifiedTrack? ChooseAudio()
        {
            if (_audio.Count == 0)
            {
                _audioLeft = true;
                if (_captureTrace) Add(TraceCodes.AudioNone, "This file has no audio tracks.");
                return null;
            }

            if (_manual?.Audio is { } remembered)
            {
                var best = _audio
                    .Select(a => (Track: a, Score: remembered.Score(a, _l)))
                    .Where(x => x.Score >= TrackDescriptor.MatchThreshold)
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Track.Track.Index)
                    .FirstOrDefault();
                if (best.Track is not null)
                {
                    var i = best.Track.Track.Index;
                    if (_captureTrace) Add(TraceCodes.AudioManual, $"Audio #{Num(i)} ({AudioName(best.Track)}) matches your earlier track change (score {Num(best.Score)}).", i, ("index", Num(i)), ("name", AudioName(best.Track)), ("score", Num(best.Score)));
                    return best.Track;
                }

                if (_captureTrace) Add(TraceCodes.AudioManualNoMatch, "Your earlier audio change matches no track in this file, so your audio steps decide.");
            }

            string? source = null;
            if (_p.FieldSources is not null && _p.FieldSources.TryGetValue("AudioChain", out var s) && !string.IsNullOrWhiteSpace(s))
            {
                source = s;
            }

            source ??= "your everyday settings";
            for (var n = 0; n < _p.AudioChain.Count; n++)
            {
                var step = _p.AudioChain[n];
                var stepNo = Num(n + 1);
                if (step.Kind == AudioStepKind.VideoDefault)
                {
                    // Babelfin sets nothing; what Jellyfin plays still decides whether the audio is understood (for the subtitles).
                    _audioLeft = true;
                    var kept = VideoDefaultAudio();
                    if (kept is null)
                    {
                        if (_captureTrace) Add(TraceCodes.AudioVideoDefault, "The audio is left to the video.", null, ("source", source));
                        return null;
                    }

                    var ki = kept.Track.Index;
                    var missingMetadata = MetadataOriginalChoice && _original is null;
                    var shownName = AudioName(kept) + (missingMetadata ? " (original language missing from metadata)" : string.Empty);
                    if (_captureTrace) Add(
                        TraceCodes.AudioVideoDefault,
                        _audioLeft ? $"The audio is left to the video: audio #{Num(ki)} ({AudioName(kept)})."
                            : $"The video default was skipped by your audio preferences; audio #{Num(ki)} ({AudioName(kept)}) in the same language plays instead.",
                        ki,
                        ("index", Num(ki)),
                        ("name", shownName),
                        ("step", stepNo),
                        ("source", source),
                        ("filtered", _audioLeft ? "false" : "true"));
                    return kept;
                }

                var candidates = StepCandidates(step, out var why, out var detail);
                var description = Describe(step) + (detail.Length > 0 ? ", " + detail : string.Empty);
                if (candidates.Count > 0)
                {
                    var chosen = candidates[0];
                    var i = chosen.Track.Index;
                    if (_captureTrace) Add(
                        TraceCodes.AudioStepMatched,
                        $"Step {stepNo} ({description}) of {source} matched audio #{Num(i)} ({AudioName(chosen)}).",
                        i,
                        ("index", Num(i)),
                        ("name", AudioName(chosen)),
                        ("step", stepNo),
                        ("kind", step.Kind.ToString()),
                        ("source", source));
                    return chosen;
                }

                if (_captureTrace) Add(TraceCodes.AudioStepSkipped, $"Step {stepNo} ({description}) was skipped: {why}.", null, ("step", stepNo), ("kind", step.Kind.ToString()), ("reason", why));
            }

            var usable = _audio.Where(a => !Avoided(a)).ToList();
            var fallback = OrderAudio(usable.Count > 0 ? usable : _audio)[0];
            var fi = fallback.Track.Index;
            if (_captureTrace) Add(
                TraceCodes.AudioFallback,
                usable.Count > 0
                    ? $"No audio step matched, so audio #{Num(fi)} ({AudioName(fallback)}) was chosen by the tie-breaks."
                    : $"Only commentary or audio-description tracks exist, so audio #{Num(fi)} ({AudioName(fallback)}) was used.",
                fi,
                ("index", Num(fi)),
                ("name", AudioName(fallback)));
            return fallback;
        }

        private string Describe(AudioStep step) => step.Kind switch
        {
            AudioStepKind.Language => (string.IsNullOrWhiteSpace(step.Language) ? "a language" : _l.Resolve(step.Language) is { } tag ? Name(tag) : $"\"{step.Language}\"")
                + (string.IsNullOrEmpty(step.TitleContains) ? string.Empty : $" titled \"{step.TitleContains}\""),
            AudioStepKind.OriginalLanguage => "the original language",
            AudioStepKind.LibraryUsualOriginalLanguage => "this library's usual original language",
            AudioStepKind.MyLanguages => "your languages",
            AudioStepKind.FileDefault => "the file's default track",
            AudioStepKind.VideoDefault => "the video's default",
            _ => "the first track",
        };

        // The track Jellyfin plays when Babelfin sets nothing: its own choice when known, else the file's default flag, else the first track.
        private ClassifiedTrack? VideoDefaultAudio()
        {
            if (_item.JellyfinAudioIndex is int j && _audio.FirstOrDefault(a => a.Track.Index == j) is { } jellyfin)
            {
                return FilterVideoAudio(jellyfin);
            }

            return FilterVideoAudio(_audio.FirstOrDefault(a => a.Track.IsDefault) ?? _audio.OrderBy(a => a.Track.Index).FirstOrDefault());
        }

        private ClassifiedTrack? FilterVideoAudio(ClassifiedTrack? chosen)
        {
            if (chosen is null || !Avoided(chosen) || Lang(chosen) is not { } language)
            {
                return chosen;
            }

            var replacement = OrderAudio(_audio.Where(a => !Avoided(a) && Lang(a) is { } tag && _l.Match(language, tag) != MatchQuality.None)).FirstOrDefault();
            if (replacement is not null)
            {
                _audioLeft = false;
            }

            return replacement ?? chosen;
        }

        private List<ClassifiedTrack> StepCandidates(AudioStep step, out string why, out string detail)
        {
            detail = string.Empty;
            switch (step.Kind)
            {
                case AudioStepKind.Language:
                    if (string.IsNullOrWhiteSpace(step.Language))
                    {
                        why = "no language is set for this step";
                        return new List<ClassifiedTrack>();
                    }

                    var wanted = _l.Resolve(step.Language);
                    if (wanted is null)
                    {
                        if (_captureTrace) Add(TraceCodes.ProfileLanguageUnknown, $"\"{step.Language}\" in your audio steps isn't a language Babelfin recognises, so it was ignored.", null, ("value", step.Language), ("field", "your audio steps"));
                        why = $"\"{step.Language}\" isn't a recognised language";
                        return new List<ClassifiedTrack>();
                    }

                    return ByLanguage(wanted, step.TitleContains, out why);

                case AudioStepKind.OriginalLanguage:
                    if (_original is null)
                    {
                        why = MetadataOriginalChoice ? "the title's metadata has no original language" : "the original language is unknown";
                        return new List<ClassifiedTrack>();
                    }

                    detail = Name(_original);
                    return ByLanguage(_original, step.TitleContains, out why);

                case AudioStepKind.LibraryUsualOriginalLanguage:
                    if (_libraryUsual is null)
                    {
                        why = "this library has no usual original language";
                        return new List<ClassifiedTrack>();
                    }

                    detail = Name(_libraryUsual);
                    return ByLanguage(_libraryUsual, step.TitleContains, out why);

                case AudioStepKind.MyLanguages:
                    if (_understood.Count == 0)
                    {
                        why = "My languages is empty";
                        return new List<ClassifiedTrack>();
                    }

                    var reasons = new List<string>();
                    foreach (var u in _understood)
                    {
                        var found = ByLanguage(u, null, out var reason);
                        if (found.Count > 0)
                        {
                            detail = Name(u);
                            why = string.Empty;
                            return found;
                        }

                        reasons.Add(reason);
                    }

                    why = string.Join("; ", reasons.Take(3));
                    return new List<ClassifiedTrack>();

                case AudioStepKind.FileDefault:
                    var defaults = _audio.Where(a => a.Track.IsDefault).ToList();
                    if (defaults.Count == 0)
                    {
                        why = "no audio track is flagged default";
                        return defaults;
                    }

                    var usableDefaults = defaults.Where(a => !Avoided(a)).ToList();
                    why = usableDefaults.Count == 0 ? "the default track is commentary or audio description" : string.Empty;
                    return OrderAudio(usableDefaults);

                default:
                    var usable = _audio.Where(a => !Avoided(a)).OrderBy(a => a.Track.Index).Take(1).ToList();
                    why = usable.Count == 0 ? "only commentary or audio-description tracks exist" : string.Empty;
                    return usable;
            }
        }

        private List<ClassifiedTrack> ByLanguage(LanguageTag wanted, string? titleContains, out string why)
        {
            var inLanguage = _audio.Where(a => Lang(a) is { } tag && _l.Match(wanted, tag) != MatchQuality.None).ToList();
            if (inLanguage.Count == 0)
            {
                why = $"no {Name(wanted)} audio track";
                return inLanguage;
            }

            if (!string.IsNullOrEmpty(titleContains))
            {
                inLanguage = inLanguage.Where(a => a.Track.Title?.Contains(titleContains, StringComparison.OrdinalIgnoreCase) == true).ToList();
                if (inLanguage.Count == 0)
                {
                    why = $"no {Name(wanted)} audio track with \"{titleContains}\" in its title";
                    return inLanguage;
                }
            }

            var usable = inLanguage.Where(a => !Avoided(a)).ToList();
            if (usable.Count == 0)
            {
                why = $"the only {Name(wanted)} audio is commentary or audio description";
                return usable;
            }

            why = string.Empty;
            var exact = usable.Where(a => _l.Match(wanted, Lang(a)!) == MatchQuality.Exact).ToList();
            return OrderAudio(exact.Count > 0 ? exact : usable);
        }

        private bool DecideUnderstood(ClassifiedTrack? audio, out string audioName, out string word)
        {
            if (audio is null)
            {
                audioName = string.Empty;
                word = "unknown";
                var u = _p.UnknownAudioCountsAsUnderstood;
                if (_captureTrace) Add(TraceCodes.AudioUnknown, $"With no audio, it counts as {(u ? "in My languages" : "outside My languages")} (your setting for unknown audio).");
                return u;
            }

            var i = audio.Track.Index;
            audioName = AudioName(audio);
            var tag = Lang(audio);
            if (tag is null)
            {
                word = "unknown";
                var u = _p.UnknownAudioCountsAsUnderstood;
                if (_captureTrace) Add(TraceCodes.AudioUnknown, $"The language of audio #{Num(i)} is unknown, so it counts as {(u ? "in My languages" : "outside My languages")} (your setting).", i, ("index", Num(i)));
                return u;
            }

            if (_understood.Any(u => _l.Match(u, tag) != MatchQuality.None))
            {
                word = "true";
                if (_captureTrace) Add(TraceCodes.AudioUnderstood, $"{audioName} is in My languages.", i, ("name", audioName));
                return true;
            }

            word = "false";
            if (_captureTrace) Add(TraceCodes.AudioNotUnderstood, $"{audioName} is outside My languages.", i, ("name", audioName));
            return false;
        }

        // ---------- subtitles ----------
        private ClassifiedTrack? ChooseSubtitles(bool understood, string audioName, string understoodWord, List<LanguageTag> wantedLanguages, out bool wanted)
        {
            var want = understood ? _p.WhenAudioUnderstood : _p.WhenAudioNotUnderstood;
            var field = understood ? "WhenAudioUnderstood" : "WhenAudioNotUnderstood";
            var why = understood ? "your setting when the audio is in My languages" : "your setting when the audio is outside My languages";
            if (_p.FieldSources is not null && _p.FieldSources.TryGetValue(field, out var src) && !string.IsNullOrWhiteSpace(src))
            {
                why += ", from " + src;
            }

            if (_p.PrefersSdh && want != SubtitleWant.Full)
            {
                want = SubtitleWant.Full;
                why = "Always show subtitles is on";
            }

            wanted = want != SubtitleWant.Off && want != SubtitleWant.VideoDefault;
            if (_captureTrace) Add(TraceCodes.SubsWant, $"Subtitles wanted: {WantWord(want)} ({why}).", null, ("want", WantWord(want)), ("understood", understoodWord), ("audio", audioName));

            if (wanted || want == SubtitleWant.VideoDefault)
            {
                var burned = ResolveList(_item.BurnedInLanguages, "the burned-in languages");
                var hit = burned.FirstOrDefault(b => wantedLanguages.Any(w => _l.Match(w, b) != MatchQuality.None));
                if (hit is not null)
                {
                    if (_captureTrace) Add(TraceCodes.SubsBurnedIn, $"{Name(hit)} subtitles are burned into the picture, so no subtitle track is picked.", null, ("language", hit.Code), ("name", Name(hit)));
                    return null;
                }
            }

            if (_manual is not null && _manual.SubtitlesOff)
            {
                if (_captureTrace) Add(TraceCodes.SubsManualOff, "You turned subtitles off by hand earlier, so they stay off.");
                return null;
            }

            if (_manual?.Subtitle is { } remembered)
            {
                var best = _subs
                    .Select(t => (Track: t, Score: remembered.Score(t, _l)))
                    .Where(x => x.Score >= TrackDescriptor.MatchThreshold)
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Track.Track.Index)
                    .FirstOrDefault();
                if (best.Track is not null)
                {
                    var i = best.Track.Track.Index;
                    var name = Name(best.Track.Language);
                    if (_captureTrace) Add(TraceCodes.SubsManual, $"Subtitle #{Num(i)} ({name} {KindWord(best.Track)}) matches your earlier track change (score {Num(best.Score)}).", i, ("index", Num(i)), ("name", name), ("kind", KindWord(best.Track)), ("score", Num(best.Score)));
                    return best.Track;
                }

                if (_captureTrace) Add(TraceCodes.SubsManualNoMatch, "Your earlier subtitle change matches no track in this file, so your subtitle settings decide.");
            }

            if (want == SubtitleWant.VideoDefault)
            {
                // Only a change of audio changes the input to Jellyfin's subtitle policy.
                // Selecting the audio already in use preserves the person's existing subtitle choice, including Off.
                // Without the person's policy (older callers), leave the existing choice alone.
                if (!_audioLeft && _chosenAudioIndex != _item.JellyfinAudioIndex && _item.JellyfinSubtitleMode is { } mode)
                {
                    var currentAudio = _audio.FirstOrDefault(a => a.Track.Index == _chosenAudioIndex);
                    var selected = VideoSubtitleSelector.Choose(_tracks, mode, _item.JellyfinSubtitleLanguage, currentAudio?.Track.Language, _item.JellyfinSubtitleLanguages);
                    var native = selected is int index ? _subs.FirstOrDefault(t => t.Track.Index == index) : null;
                    wanted = native is not null;
                    if (_captureTrace) Add(TraceCodes.SubsVideoDefault, "Jellyfin's subtitle policy was recalculated for the chosen audio.", native?.Track.Index,
                        ("index", native?.Track.Index.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                        ("name", native is null ? string.Empty : Name(native.Language)),
                        ("kind", native is null ? string.Empty : KindWord(native)), ("recomputed", "true"));
                    return native;
                }

                _subtitlesLeft = true;
                var shown = _item.JellyfinSubtitleIndex is int js && js >= 0 ? _subs.FirstOrDefault(t => t.Track.Index == js) : null;
                if (shown is null)
                {
                    if (_captureTrace) Add(TraceCodes.SubsVideoDefault, "The subtitles are left to the video.");
                }
                else
                {
                    var si = shown.Track.Index;
                    var sname = Name(shown.Language);
                    if (_captureTrace) Add(
                        TraceCodes.SubsVideoDefault,
                        $"The subtitles are left to the video: subtitle #{Num(si)} ({sname} {KindWord(shown)}).",
                        si,
                        ("index", Num(si)),
                        ("name", sname),
                        ("kind", KindWord(shown)));
                }

                return null;
            }

            if (!wanted)
            {
                if (_captureTrace) Add(TraceCodes.SubsOff, understood && audioName.Length > 0 ? $"Subtitles are off because {audioName} is in My languages." : "Subtitles are off in your settings.");
                return null;
            }

            foreach (var s in _subs.Where(s => s.Language is null
                && !(want == SubtitleWant.PartialOnly && s.Track.IsForced && s.SubtitleKind == SubtitleKind.Partial)))
            {
                if (_captureTrace) Add(TraceCodes.SubsUnlabelledIgnored, $"Subtitle #{Num(s.Track.Index)} has no language, so it was ignored.", s.Track.Index, ("index", Num(s.Track.Index)));
            }

            var skipped = new HashSet<int>();
            (ClassifiedTrack Track, int Position)? uncertainForced = null;
            for (var p = 0; p < wantedLanguages.Count; p++)
            {
                var language = wantedLanguages[p];
                var found = BestSubtitle(language, want, skipped, traceSkips: true);
                if (found is not null)
                {
                    // Search every requested language for full/SDH before using uncertain forced metadata.
                    if (want == SubtitleWant.Full && TrackClassifier.HasUncertainForcedCoverage(found))
                    {
                        uncertainForced ??= (found, p);
                        continue;
                    }

                    return AddSubtitleMatch(found, wantedLanguages, p);
                }

                if (_captureTrace) Add(TraceCodes.SubsSkippedLanguage, $"No usable {Name(language)} subtitles.", null, ("language", language.Code), ("name", Name(language)));
            }

            if (uncertainForced is { } deferred)
            {
                return AddSubtitleMatch(deferred.Track, wantedLanguages, deferred.Position);
            }

            var tried = Names(wantedLanguages);
            if (want == SubtitleWant.PartialOnly)
            {
                var unlabelledForced = _subs.Where(s => s.Language is null && s.Track.IsForced && s.SubtitleKind == SubtitleKind.Partial
                    && !(_p.Format == FormatPreference.TextOnly && s.Track.Format == SubtitleFormat.Image))
                    .OrderBy(DevicePenalty).ThenBy(FormatRank).ThenBy(EmbeddedRank)
                    .ThenBy(s => s.Track.IsDefault ? 0 : 1).ThenBy(s => s.Track.Index).FirstOrDefault();
                if (unlabelledForced is not null)
                {
                    return AddFallback(unlabelledForced, tried, "a forced track with no language tag");
                }
            }

            if (_captureTrace) Add(TraceCodes.SubsNothing, wantedLanguages.Count == 0 ? "No subtitle languages are set." : $"No {WantWord(want)} subtitles in {tried}.", null, ("tried", wantedLanguages.Count == 0 ? string.Empty : tried));
            return want == SubtitleWant.Full ? Fallback(wantedLanguages, tried) : null;
        }

        private ClassifiedTrack AddSubtitleMatch(ClassifiedTrack found, IReadOnlyList<LanguageTag> wantedLanguages, int position)
        {
            var i = found.Track.Index;
            var name = Name(found.Language);
            var language = wantedLanguages[position];
            if (_captureTrace) Add(
                TraceCodes.SubsMatched,
                $"Subtitle #{Num(i)} ({name} {KindWord(found)}) is the best match in {Name(language)}.",
                i,
                ("index", Num(i)),
                ("name", name),
                ("kind", KindWord(found)),
                ("language", language.Code),
                ("position", Num(position + 1)),
                ("firstName", Name(wantedLanguages[0])),
                ("coverage", TrackClassifier.HasUncertainForcedCoverage(found) ? "unknown" : "identified"));
            return found;
        }

        private ClassifiedTrack? Fallback(List<LanguageTag> tried, string triedNames)
        {
            switch (_p.IfNothingMatches)
            {
                case WhenNothingMatches.PartialInMyLanguage:
                    foreach (var r in _reads)
                    {
                        var found = BestSubtitle(r, SubtitleWant.PartialOnly, new HashSet<int>(), traceSkips: false);
                        if (found is not null)
                        {
                            return AddFallback(found, triedNames, "partial subtitles in Languages for subtitles");
                        }
                    }

                    break;

                case WhenNothingMatches.FullInAnyBackupLanguage:
                    var backups = new List<LanguageTag>();
                    foreach (var tag in _reads.Concat(_understood))
                    {
                        if (!tried.Concat(backups).Any(t => string.Equals(t.Code, tag.Code, StringComparison.OrdinalIgnoreCase)))
                        {
                            backups.Add(tag);
                        }
                    }

                    foreach (var b in backups)
                    {
                        var found = BestSubtitle(b, SubtitleWant.Full, new HashSet<int>(), traceSkips: false);
                        if (found is not null)
                        {
                            return AddFallback(found, triedNames, "full subtitles in a backup language");
                        }
                    }

                    break;

                default:
                    break;
            }

            return null;
        }

        private ClassifiedTrack AddFallback(ClassifiedTrack found, string triedNames, string what)
        {
            var i = found.Track.Index;
            var name = Name(found.Language);
            if (_captureTrace) Add(
                TraceCodes.SubsFallback,
                $"Nothing in {triedNames}, so subtitle #{Num(i)} ({name} {KindWord(found)}) is shown: {what}.",
                i,
                ("index", Num(i)),
                ("name", name),
                ("kind", KindWord(found)),
                ("tried", triedNames));
            return found;
        }

        private void Skip(string code, ClassifiedTrack track, string why, HashSet<int> skipped, bool traceSkips)
        {
            var i = track.Track.Index;
            if (traceSkips && skipped.Add(i))
            {
                if (_captureTrace) Add(code, $"Subtitle #{Num(i)} ({Label(track)}) was skipped: {why}.", i, ("index", Num(i)), ("label", Label(track)));
            }
        }

        private ClassifiedTrack? BestSubtitle(LanguageTag language, SubtitleWant want, HashSet<int> skipped, bool traceSkips)
        {
            var eligible = new List<ClassifiedTrack>();
            foreach (var s in _subs)
            {
                if (s.Language is not { } tag || _l.Match(language, tag) == MatchQuality.None)
                {
                    continue;
                }

                if (want == SubtitleWant.Full && s.SubtitleKind == SubtitleKind.Partial && !TrackClassifier.HasUncertainForcedCoverage(s))
                {
                    Skip(TraceCodes.SubsSkippedPartial, s, "it's partial", skipped, traceSkips);
                }
                else if (want == SubtitleWant.PartialOnly && s.SubtitleKind != SubtitleKind.Partial)
                {
                    Skip(TraceCodes.SubsSkippedFull, s, "only partial subtitles are wanted", skipped, traceSkips);
                }
                else if (_p.Format == FormatPreference.TextOnly && s.Track.Format == SubtitleFormat.Image)
                {
                    Skip(TraceCodes.SubsSkippedImage, s, "it's an image track and you chose text only", skipped, traceSkips);
                }
                else
                {
                    eligible.Add(s);
                }
            }

            // Prefer identified full dialogue to uncertain forced metadata, including generic language matches.
            if (want == SubtitleWant.Full && eligible.Any(s => s.SubtitleKind != SubtitleKind.Partial))
            {
                eligible.RemoveAll(TrackClassifier.HasUncertainForcedCoverage);
            }

            if (want == SubtitleWant.Full && _sdh == Preference.Avoid && eligible.Any(s => s.SubtitleKind == SubtitleKind.Full))
            {
                foreach (var s in eligible.Where(s => s.SubtitleKind == SubtitleKind.Sdh))
                {
                    Skip(TraceCodes.SubsSkippedSdh, s, "it's SDH, which you avoid, and a full track exists", skipped, traceSkips);
                }

                eligible.RemoveAll(s => s.SubtitleKind == SubtitleKind.Sdh);
            }

            return eligible
                .OrderBy(s => _l.Match(language, s.Language!) == MatchQuality.Exact ? 0 : 1)
                .ThenBy(DevicePenalty)
                .ThenBy(KindRank)
                .ThenBy(FormatRank)
                .ThenBy(EmbeddedRank)
                .ThenBy(s => s.Track.IsDefault ? 0 : 1)
                .ThenBy(s => s.Track.Index)
                .FirstOrDefault();
        }

        private int DevicePenalty(ClassifiedTrack s) =>
            (s.Track.Format == SubtitleFormat.Image && !_device.SupportsImageSubtitles)
            || (s.Track.Format == SubtitleFormat.Text && !_device.SupportsTextSubtitles) ? 1 : 0;

        private int KindRank(ClassifiedTrack s) => s.SubtitleKind switch
        {
            SubtitleKind.Sdh => _sdh == Preference.Prefer ? 0 : 1,
            SubtitleKind.Full => _sdh == Preference.Prefer ? 1 : 0,
            _ => 2,
        };

        private int FormatRank(ClassifiedTrack s) => _p.Format == FormatPreference.Neutral
            ? 0
            : s.Track.Format switch
            {
                SubtitleFormat.Text => 0,
                SubtitleFormat.Image => 2,
                _ => 1,
            };

        private int EmbeddedRank(ClassifiedTrack s) => _p.Embedded switch
        {
            EmbeddedPreference.PreferEmbedded => s.Track.IsExternal ? 1 : 0,
            EmbeddedPreference.PreferExternal => s.Track.IsExternal ? 0 : 1,
            _ => 0,
        };
    }
}
