"""Generate the tiny sample library the integration rig needs: short MKV files whose tracks carry the shapes
Babelfin must handle. Usage: python tools/rig/make_media.py <out folder>   (needs ffmpeg on PATH)."""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

FFMPEG = os.environ.get("FFMPEG", "ffmpeg")
SRT = "1\n00:00:00,000 --> 00:00:01,500\n{text}\n\n2\n00:00:01,500 --> 00:00:03,000\n{text} 2\n"
ASS = ("[Script Info]\nScriptType: v4.00+\nPlayResX: 320\nPlayResY: 240\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
       "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
       "Dialogue: 0,0:00:00.00,0:00:01.50,Default,,0,0,0,,{text}\nDialogue: 0,0:00:01.50,0:00:03.00,Default,,0,0,0,,{text} 2\n")


def make(path, audios, subs, duration=3, subtitle_first=False):
    """audios: list of (language or None, title or None, channels, default, extra_disposition); subs: list of (language or None, title, kind 'srt'|'ass', default, forced, hi).
    subtitle_first puts subtitles before video/audio in the output stream order; the default order is unchanged."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        cmd = [FFMPEG, "-y", "-loglevel", "error", "-f", "lavfi", "-i", f"color=c=black:s=320x240:d={duration}:r=10"]
        for i, _ in enumerate(audios):
            cmd += ["-f", "lavfi", "-i", f"sine=frequency={220 * (i + 1)}:duration={duration}"]
        sub_files = []
        for i, (lang, title, kind, default, forced, hi) in enumerate(subs):
            f = Path(tmp) / f"sub{i}.{kind}"
            text = f"{title or lang or 'sub'} line"
            f.write_text((ASS if kind == "ass" else SRT).format(text=text), encoding="utf-8")
            cmd += ["-i", str(f)]
            sub_files.append(f)
        subtitle_maps = [value for i in range(len(subs)) for value in ("-map", f"{len(audios) + 1 + i}:s")]
        if subtitle_first:
            cmd += subtitle_maps
        cmd += ["-map", "0:v"]
        for i in range(len(audios)):
            cmd += ["-map", f"{i + 1}:a"]
        if not subtitle_first:
            cmd += subtitle_maps
        cmd += ["-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-ac", "2", "-c:s", "copy"]
        for i, (lang, title, channels, default, extra) in enumerate(audios):
            if lang:
                cmd += [f"-metadata:s:a:{i}", f"language={lang}"]
            if title:
                cmd += [f"-metadata:s:a:{i}", f"title={title}"]
            disp = ("default" if default else "0") + (("+" + extra) if extra else "")
            cmd += [f"-disposition:a:{i}", disp]
        for i, (lang, title, kind, default, forced, hi) in enumerate(subs):
            if lang:
                cmd += [f"-metadata:s:s:{i}", f"language={lang}"]
            if title:
                cmd += [f"-metadata:s:s:{i}", f"title={title}"]
            parts = [p for p, on in (("default", default), ("forced", forced), ("hearing_impaired", hi)) if on]
            cmd += [f"-disposition:s:{i}", "+".join(parts) if parts else "0"]
        cmd += [str(path)]
        subprocess.run(cmd, check=True)
    return path


def main(out):
    out = Path(out)
    anime = out / "Anime"
    tv = out / "TV Shows"
    movies = out / "Movies"
    # Dual audio: en default audio, ja audio; en forced default (Signs), en full, en SDH
    for n in (1, 2, 3):
        make(anime / "Astercoil Test (2041)" / "Season 01" / f"Astercoil Test (2041) S01E0{n}.mkv",
             [("eng", "Surround", 6, True, None), ("jpn", "Surround", 6, False, None)],
             [("eng", None, "ass", True, True, False), ("eng", None, "ass", False, False, False), ("eng", "SDH", "srt", False, False, True)])
    # Signs flagged default but NOT forced (a full dialogue alternative) + Dialogue track
    make(anime / "Velmora Test (2042)" / "Season 01" / "Velmora Test (2042) S01E01.mkv",
         [("eng", None, 2, True, None), ("jpn", None, 2, False, None)],
         [("eng", "Signs & Songs", "ass", True, False, False), ("eng", "Dialogue", "ass", False, False, False)])
    # Only a forced/default English track with dialogue; subtitle index 0, video 1, English audio 2, Japanese audio 3
    make(anime / "Quorim Test (2043)" / "Season 01" / "Quorim Test (2043) S01E01.mkv",
         [("eng", "Stereo", 2, True, None), ("jpn", "Stereo", 2, False, None)],
         [("eng", None, "ass", True, True, False)], duration=15, subtitle_first=True)
    # Untagged audio and untagged subtitle (unknown-language recognition), plus a Traditional Chinese sub and a Cantonese-titled chi audio
    make(anime / "Untagged Test (2007)" / "Season 01" / "Untagged Test (2007) S01E01.mkv",
         [(None, None, 2, True, None), ("chi", "Cantonese", 2, False, None)],
         [(None, None, "ass", True, False, False), ("chi", "繁體", "srt", False, False, False)])
    # Commentary track present; ja only audio with en full sub
    make(anime / "Commentary Test (2010)" / "Season 01" / "Commentary Test (2010) S01E01.mkv",
         [("jpn", None, 2, True, None), ("eng", "Director's Commentary", 2, False, "comment")],
         [("eng", None, "srt", False, False, False)])
    # A plain TV show: en audio, en SDH only
    make(tv / "Plain Test (2020)" / "Season 01" / "Plain Test (2020) S01E01.mkv",
         [("eng", None, 6, True, None)],
         [("eng", "SDH", "srt", False, False, True)])
    make(tv / "Plain Test (2020)" / "Season 01" / "Plain Test (2020) S01E02.mkv",
         [("eng", None, 6, True, None)],
         [("eng", "SDH", "srt", False, False, True)])
    # A German movie with English audio too: de audio, en audio, it + en subs
    make(movies / "Berlin Test (2019)" / "Berlin Test (2019).mkv",
         [("ger", None, 6, True, None), ("eng", "Dub", 2, False, None)],
         [("ita", None, "srt", False, False, False), ("eng", None, "srt", False, False, False)])
    # A Mandarin / Cantonese pair with Simplified and Traditional subs
    make(movies / "Harbour Test (2002)" / "Harbour Test (2002).mkv",
         [("chi", "國語", 2, True, None), ("chi", "粵語", 2, False, None)],
         [("chi", "简体", "srt", False, False, False), ("chi", "繁體", "srt", False, False, False), ("eng", None, "srt", False, False, False)])
    count = sum(1 for _ in out.rglob("*.mkv"))
    print(f"{count} files under {out}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "rig-media")
