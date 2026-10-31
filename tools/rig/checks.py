"""The rig's checks against a running Jellyfin 12.2 with the plugin and the sample library (see rig.py)."""
import re
import time


def streams(api, item_id):
    status, item, _ = api.get(f"/Users/{api.user_id}/Items/{item_id}")
    ms = item["MediaSources"][0]
    return ms, {s["Index"]: s for s in ms["MediaStreams"]}


def idx(ms):
    return f"{ms.get('DefaultAudioStreamIndex')}/{ms.get('DefaultSubtitleStreamIndex')}"


def find_item(api, name):
    """'Astercoil Test (2041) S01E01' -> the episode by series name and number; 'Berlin Test (2019)' -> the movie by name."""
    m = re.match(r"^(.*?) \(\d{4}\) S(\d+)E(\d+)$", name)
    if m:
        series_name, season, episode = m.group(1), int(m.group(2)), int(m.group(3))
        status, body, _ = api.get("/Items", userId=api.user_id, Recursive="true", IncludeItemTypes="Series", Limit=100)
        series = next((s for s in (body or {}).get("Items", []) if s["Name"] == series_name), None)
        if series is None:
            return None
        status, eps, _ = api.get(f"/Shows/{series['Id']}/Episodes", userId=api.user_id, Fields="MediaSources")
        ep = next((e for e in (eps or {}).get("Items", []) if e.get("ParentIndexNumber") == season and e.get("IndexNumber") == episode), None)
        return ep["Id"] if ep else None
    status, body, _ = api.get("/Items", userId=api.user_id, Recursive="true", IncludeItemTypes="Movie", Limit=100)
    movie = next((i for i in (body or {}).get("Items", []) if i["Name"].lower().startswith(name.split(" (")[0].lower())), None)
    return movie["Id"] if movie else None


def no_paths(text):
    return not re.search(r'"Path"|\\\\|/media/|\.mkv', text, re.IGNORECASE)


def run_checks(api, check, media):
    # ── Library facts now (the scheduled task), so the usual original language is known.
    status, tasks, _ = api.get("/ScheduledTasks")
    facts_task = next((t for t in tasks if t.get("Key") == "BabelfinLibraryFacts"), None)
    check(facts_task is not None, "the 'Babelfin: refresh library facts' task is registered")
    if facts_task:
        api.post(f"/ScheduledTasks/Running/{facts_task['Id']}")
        for _ in range(30):
            status, libs, _ = api.get("/Babelfin/Admin/Libraries")
            if status == 200 and all(l.get("Facts") for l in libs):
                break
            time.sleep(1)
        status, libs, _ = api.get("/Babelfin/Admin/Libraries")
        by = {l["Name"]: l for l in (libs or [])}
        check(status == 200 and by.get("Anime", {}).get("IsOriginalAudio") is True and by.get("Anime", {}).get("UsualOriginalLanguage") == "ja"
              and by.get("Movies", {}).get("IsOriginalAudio") is False and by.get("TV Shows", {}).get("IsOriginalAudio") is False,
              f"library facts: Anime original-audio with usual ja; TV Shows and Movies not ({[(n, l.get('IsOriginalAudio'), l.get('UsualOriginalLanguage')) for n, l in by.items()]})")

    # ── Out of the box Babelfin is neutral: nothing chosen, so every item keeps Jellyfin's own tracks.
    status, admin, _ = api.get("/Babelfin/Admin/Settings")
    check(status == 200 and admin.get("ConfigVersion") == 9 and admin.get("Everyday", {}).get("IsEmpty") is True and admin.get("Starter", {}).get("Enabled") is False,
          f"Admin/Settings out of the box: version 9, everyday empty, the original-language rule off ({status}, {admin.get('ConfigVersion')}, starter {admin.get('Starter', {}).get('Enabled')})")
    status, me0, _ = api.get("/Babelfin/Me/Settings")
    rows0 = [((me0 or {}).get("Defaults") or {}).get("AllLibraries") or {}] + list((((me0 or {}).get("Defaults") or {}).get("Libraries") or {}).values())
    check(status == 200 and len(rows0) >= 4 and all([s.get("Kind") for s in (r.get("AudioChain") or [])] == ["VideoDefault"] and r.get("WhenAudioUnderstood") == "VideoDefault" and r.get("WhenAudioNotUnderstood") == "VideoDefault" for r in rows0),
          f"every row of the person's table defaults to the video ({len(rows0)} rows)")
    neutral_names = ["Astercoil Test (2041) S01E01", "Velmora Test (2042) S01E01", "Berlin Test (2019)", "Harbour Test (2002)"]
    for name in neutral_names:
        item_id = find_item(api, name)
        if item_id is None:
            check(False, f"{name}: item found")
            continue
        ms0, _ = streams(api, item_id)
        status, choice, _ = api.get("/Babelfin/Me/Choice", itemId=item_id)
        c0 = (choice or [{}])[0] if choice else {}
        sub0 = ms0.get("DefaultSubtitleStreamIndex")
        check(status == 200 and c0.get("Outcome") == "PassedThrough" and c0.get("AudioIndex") == ms0.get("DefaultAudioStreamIndex")
              and c0.get("SubtitleIndex") == (sub0 if sub0 is not None and sub0 >= 0 else -1),
              f"{name}: nothing chosen, so Jellyfin's own {idx(ms0)} plays and the chip shows it ({status}, {c0.get('Outcome')}, {c0.get('AudioLanguage')} / {c0.get('SubtitleLanguage')})")
    # Babelfin switched off: no chip at all.
    admin["Enabled"] = False
    api.put("/Babelfin/Admin/Settings", admin)
    off_item = find_item(api, neutral_names[0])
    status, off_choice, _ = api.get("/Babelfin/Me/Choice", itemId=off_item) if off_item else (0, None, None)
    check(status == 200 and off_choice == [], f"Babelfin switched off: no chip ({status}, {off_choice})")
    admin["Enabled"] = True
    api.put("/Babelfin/Admin/Settings", admin)

    # ── The engine end to end on real Jellyfin: the admin switches on the smart settings explicitly (original-language
    # libraries, dubs for titles in another language, subtitles when the audio isn't understood); the admin is seeded 'en'.
    SMART_EVERYDAY = {"ForeignTitles": "DubInMyLanguage", "WhenAudioUnderstood": "PartialOnly", "WhenAudioNotUnderstood": "Full"}
    admin["Everyday"] = dict(SMART_EVERYDAY)
    admin.setdefault("Starter", {})["Enabled"] = True
    status, saved, _ = api.put("/Babelfin/Admin/Settings", admin)
    check(status == 200 and saved.get("Starter", {}).get("Enabled") is True, f"PUT Admin/Settings: smart settings switched on for the engine checks ({status})")
    expected = {
        "Astercoil Test (2041) S01E01": ("2/4", "Japanese audio; the forced Signs track skipped for the full English one"),
        "Velmora Test (2042) S01E01": ("2/4", "Signs & Songs flagged default but not forced is skipped by its title; Dialogue chosen"),
        "Commentary Test (2010) S01E01": ("1/3", "the commentary track avoided; English full subtitles"),
        "Quorim Test (2043) S01E01": ("3/0", "Japanese audio and the only English track, whose Forced metadata leaves dialogue coverage uncertain"),
        "Untagged Test (2007) S01E01": ("1/-1", "untagged audio with a second track stays unknown; the untagged subtitle is ignored"),
        "Plain Test (2020) S01E01": ("1/-1", "English understood: partial only wanted, only SDH exists"),
        "Berlin Test (2019)": ("2/-1", "my languages first: the English dub, understood, no partial track"),
        "Harbour Test (2002)": ("1/5", "no English audio or original: file default (Mandarin), full English subtitles"),
    }
    ids = {}
    for name, (want, why) in expected.items():
        item_id = find_item(api, name)
        ids[name] = item_id
        if item_id is None:
            check(False, f"{name}: item found")
            continue
        ms, by_index = streams(api, item_id)
        check(idx(ms) == want, f"{name}: {idx(ms)} (expected {want}: {why})")

    dualAudio = ids.get("Astercoil Test (2041) S01E01")
    if dualAudio:
        # PlaybackInfo without a media source id (the Home-card case) carries the same choice.
        status, info, _ = api.post(f"/Items/{dualAudio}/PlaybackInfo", {"UserId": api.user_id}, userId=api.user_id)
        ms = (info or {}).get("MediaSources", [{}])[0]
        check(status == 200 and idx(ms) == "2/4", f"PlaybackInfo without MediaSourceId: {idx(ms)}")
        # this generic client must explicitly opt in to pre-play memory, unlike the shipped web family.
        status, preplay_config, _ = api.get("/Babelfin/Admin/Settings")
        rig_row = next(a for a in preplay_config["AppModes"] if a.get("Client") == "BabelfinRig")
        rig_row["RememberPrePlayChoices"] = True
        rig_row["SeenOnly"] = False
        status, _, _ = api.put("/Babelfin/Admin/Settings", preplay_config)
        check(status == 200, f"the rig client explicitly opts into pre-play memory for this case ({status})")
        status, info2, _ = api.post(f"/Items/{dualAudio}/PlaybackInfo", {"UserId": api.user_id, "AudioStreamIndex": 1, "SubtitleStreamIndex": 3, "MediaSourceId": ms.get("Id")}, userId=api.user_id)
        ms2 = (info2 or {}).get("MediaSources", [{}])[0]
        status_me, me_doc, _ = api.get("/Babelfin/Me/Settings")
        remembered = [o for o in (me_doc or {}).get("Settings", {}).get("Overrides", []) if o.get("Manual")]
        check(status == 200 and idx(ms2) == "1/3" and len(remembered) == 1 and remembered[0].get("Scope") == "Series",
              f"PlaybackInfo with explicit 1/3 right after the served 2/4: a manual choice, kept ({idx(ms2)}) and remembered for the show ({[o.get('Scope') for o in remembered]})")
        status, _, _ = api.delete(f"/Babelfin/Me/Overrides/{dualAudio}", scope="Series", manual="true")
        ms3, _ = streams(api, dualAudio)
        check(status == 204 and idx(ms3) == "2/4", f"forgetting the remembered change restores Babelfin's choice ({idx(ms3)})")
        rig_row["RememberPrePlayChoices"] = False
        status, _, _ = api.put("/Babelfin/Admin/Settings", preplay_config)
        check(status == 200, f"generic-client pre-play memory is off again for the remaining checks ({status})")

        status, ex, _ = api.get("/Babelfin/Me/Explain", itemId=dualAudio)
        e0 = (ex or [{}])[0]
        check(status == 200 and e0.get("babelfinAudioIndex") == 2 and e0.get("babelfinSubtitleIndex") == 4 and "original-audio libraries" in (e0.get("explanation") or ""),
              f"Me/Explain names the starter rule: {e0.get('explanation')}")
        check(no_paths(str(ex)), "Me/Explain carries no file path")

    # ── Admin settings change applies live (cache invalidation on the real server).
    status, admin, _ = api.get("/Babelfin/Admin/Settings")
    check(status == 200 and admin.get("ConfigVersion") == 9 and admin.get("Everyday", {}).get("WhenAudioNotUnderstood") == "Full", f"Admin/Settings: version 9 with the smart settings ({status})")
    admin["Everyday"] = dict(SMART_EVERYDAY, IfNothingMatches="PartialInMyLanguage")
    status, saved, _ = api.put("/Babelfin/Admin/Settings", admin)
    check(status == 200 and saved.get("Everyday", {}).get("IfNothingMatches") == "PartialInMyLanguage", f"PUT Admin/Settings: partial fallback saved ({status})")
    forcedOnly = ids.get("Quorim Test (2043) S01E01")
    if forcedOnly:
        ms, _ = streams(api, forcedOnly)
        check(idx(ms) == "3/0", f"Quorim Test after enabling partial fallback: {idx(ms)} (the available English track remains selected)")
    admin["Everyday"] = dict(SMART_EVERYDAY)
    api.put("/Babelfin/Admin/Settings", admin)

    # ── A plain user: own settings yes, admin routes no, nothing leaks.
    viewer = None
    status, user, _ = api.post("/Users/New", {"Name": "rigviewer", "Password": ""})
    check(status == 200, f"second user created ({status})")
    if status == 200:
        from rig import Api  # noqa: E402
        viewer = Api(api.base)
        status, body, _ = viewer.post("/Users/AuthenticateByName", {"Username": "rigviewer", "Pw": ""})
        if status == 200:
            viewer.token = body["AccessToken"]
            viewer.user_id = body["User"]["Id"]
        check(status == 200, f"second user signed in ({status})")
        status, me, _ = viewer.get("/Babelfin/Me/Settings")
        check(status == 200 and me and me["Settings"]["UserName"] == "rigviewer" and me["Settings"]["Seed"]["Assumed"], f"viewer GET Me/Settings: 200 with a seeded document and assumed languages ({status})")
        check(no_paths(str(me)), "viewer Me/Settings carries no file path")
        status, _, _ = viewer.get("/Babelfin/Admin/Settings")
        check(status == 403, f"viewer GET Admin/Settings: 403 ({status})")
        status, _, _ = viewer.get(f"/Babelfin/Admin/Users/{api.user_id}/Settings")
        check(status == 403, f"viewer reading the admin's settings: 403 ({status})")
        if dualAudio:
            status, ov, _ = viewer.put(f"/Babelfin/Me/Overrides/{dualAudio}", {"AudioChain": [{"Kind": "Language", "Language": "en"}]}, scope="Series")
            check(status == 200 and ov.get("Scope") == "Series", f"viewer PUT Me/Overrides scope=Series: {status}")
            ms, _ = streams(viewer, dualAudio)
            check(idx(ms) == "1/3", f"the viewer's own series override applies to them: {idx(ms)} (English audio, understood, partial #3)")
            ms_admin, _ = streams(api, dualAudio)
            check(idx(ms_admin) == "2/4", f"and not to the admin: {idx(ms_admin)}")
            status, _, _ = viewer.delete(f"/Babelfin/Me/Overrides/{dualAudio}", scope="Series")
            check(status == 204, f"viewer DELETE Me/Overrides: {status}")

    # ── Playback handling, assets and title checks.
    try:
        from checks_playback import run_playback_checks  # noqa: E402
        anime_lib = next((l["Id"] for l in (libs or []) if l.get("Name") == "Anime"), None)
        run_playback_checks(api, check, viewer if (viewer is not None and viewer.token) else None, ids, anime_lib)
    except Exception as ex:  # noqa: BLE001
        check(False, f"Playback and settings checks crashed: {ex!r}")

    # ── Web client injection on the real static files, and the admin status.
    status, html, _ = api.call("GET", "/web/index.html", raw=True)
    text = html.decode("utf-8", "replace") if status == 200 else ""
    check(status == 200 and "/Babelfin/client.js" in text, f"GET /web/index.html carries the Babelfin script tag ({status})")
    status, st, _ = api.get("/Babelfin/Admin/Status")
    check(status == 200 and st.get("wrapped") is True and st.get("personFiles", 0) >= 2 and st.get("errors", 0) == 0,
          f"Admin/Status: wrapped, {st.get('personFiles')} person files, {st.get('errors')} errors, {st.get('applied')} applied")
    status, langs, _ = api.get("/Babelfin/Languages")
    check(status == 200 and any(l.get("Code") == "zh-Hant" for l in (langs or [])), f"GET /Babelfin/Languages: {status}, {len(langs or [])} languages")
