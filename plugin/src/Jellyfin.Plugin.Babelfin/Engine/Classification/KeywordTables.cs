using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Babelfin.Engine.Classification;

/// <summary>
/// The editable keyword lists the <see cref="TrackClassifier"/> matches against a track's raw title, comment and file name.
/// Latin-script words match as whole words (case-insensitive; '.', '_', '-' and spaces are interchangeable);
/// CJK words match as substrings.
/// </summary>
/// <param name="PartialWords">Words that make a subtitle partial (forced, Signs &amp; Songs).</param>
/// <param name="SdhWords">Words that make a subtitle SDH.</param>
/// <param name="FullWords">Words that make a subtitle full, overriding forced and default flags.</param>
/// <param name="CommentaryWords">Words that make an audio track a commentary.</param>
/// <param name="AudioDescriptionWords">Words that make an audio track audio description.</param>
public sealed record KeywordTables(
    IReadOnlyList<string> PartialWords,
    IReadOnlyList<string> SdhWords,
    IReadOnlyList<string> FullWords,
    IReadOnlyList<string> CommentaryWords,
    IReadOnlyList<string> AudioDescriptionWords)
{
    /// <summary>Gets the built-in multilingual lists (English, German, French, Spanish, Italian, Portuguese, Dutch, Japanese, Chinese).</summary>
    public static KeywordTables Default { get; } = new(
        PartialWords: Array.AsReadOnly(new[]
        {
            "sign", "signs", "song", "songs", "s&s", "signs&songs", "signs & songs", "signs/songs", "signs and songs",
            "forced", "foreign", "partial", "foreign parts", "foreign only",
            "schilder", "erzwungen",
            "forcé", "forcés", "forcée", "forcées", "panneaux",
            "forzado", "forzados", "forzada", "forzadas", "carteles", "letreros",
            "forzato", "forzati", "cartelli",
            "forçado", "forçados", "forçada", "forçadas",
            "geforceerd",
            "強制",
            "强制",
        }),
        SdhWords: Array.AsReadOnly(new[]
        {
            "sdh", "cc", "hi", "hoh", "hearing impaired", "hard of hearing", "closed captions", "closed caption", "closed captioned", "deaf",
            "hörgeschädigte", "für hörgeschädigte",
            "malentendants", "sourds et malentendants",
            "sordos", "para sordos",
            "non udenti",
            "surdos",
            "slechthorenden",
            "聴覚障害者向け", "聴覚障害",
            "听障", "聽障",
        }),
        FullWords: Array.AsReadOnly(new[]
        {
            "full", "complete", "dialogue", "dialog", "full subs", "full subtitles",
            "vollständig", "komplett",
            "complet", "complets",
            "completo", "completos", "completa", "completas",
            "volledig",
            "完全",
            "完整",
        }),
        CommentaryWords: Array.AsReadOnly(new[]
        {
            "commentary", "commentaries", "audio commentary",
            "director's commentary", "director commentary", "directors commentary", "cast commentary",
            "kommentar", "kommentare",
            "commentaire", "commentaires",
            "commento",
            "comentario", "comentarios", "comentário",
            "commentaar",
            "解説", "コメンタリー",
            "评论", "評論",
        }),
        AudioDescriptionWords: Array.AsReadOnly(new[]
        {
            "audio description", "audio-description", "audiodescription", "audio described",
            "audiodeskription", "hörfilm",
            "audio descriptivo", "audiodescripción",
            "audiodescrizione",
            "audiodescrição",
            "audiodescriptie",
            "descriptive", "descriptive audio", "described video", "ad", "dvs",
            "visually impaired", "for the visually impaired",
            "音声ガイド", "音声解説",
            "口述影像",
        }));
}
