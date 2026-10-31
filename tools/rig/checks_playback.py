"""Playback and web-integration checks, using the signed-in rig administrator."""
import json
import time


def run_playback_checks(api, check, viewer, ids, anime_library_id):
    check(viewer is not None and ids.get("Astercoil Test (2041) S01E01") is not None, "Viewer playback checks can run (a signed-in second user and the Astercoil Test episode)")
    # ── Playback memory: generic-app pre-play choices are not personal memory; in-play changes still are.
    dualAudio = ids.get("Astercoil Test (2041) S01E01")
    if dualAudio and viewer is not None:
        status, item, _ = viewer.get(f"/Users/{viewer.user_id}/Items/{dualAudio}")
        ms = item["MediaSources"][0]
        served = f"{ms.get('DefaultAudioStreamIndex')}/{ms.get('DefaultSubtitleStreamIndex')}"
        check(served == "2/4", f"viewer served {served} for Astercoil Test before the manual choice")
        status_events, before_events, _ = api.get("/Babelfin/Admin/Activity", limit=50, kind="override-created", userId=viewer.user_id)
        check(status_events == 200, f"can inspect the viewer's memory events before pre-play ({status_events})")
        body = {"UserId": viewer.user_id, "MediaSourceId": ms["Id"], "AudioStreamIndex": 1, "SubtitleStreamIndex": 3}
        status, info, _ = viewer.post(f"/Items/{dualAudio}/PlaybackInfo", body, userId=viewer.user_id)
        returned = (info or {}).get("MediaSources", [{}])[0]
        indices = f"{returned.get('DefaultAudioStreamIndex')}/{returned.get('DefaultSubtitleStreamIndex')}"
        check(status == 200 and indices == "2/4", f"generic BabelfinRig pre-play 1/3 follows ReplaceUnlessManual, without a manual-memory opt-in: {status}, {indices}")
        time.sleep(1.5)
        status, me, _ = viewer.get("/Babelfin/Me/Settings")
        overrides = (me or {}).get("Settings", {}).get("Overrides", [])
        manual = [o for o in overrides if o.get("Manual")]
        check(status == 200 and not manual,
              f"a generic app's differing pre-play pick creates no personal or show-wide memory ({[(o.get('Scope'), o.get('Manual')) for o in manual]})")
        status, playback, _ = api.get("/Babelfin/Admin/Playback")
        check(status == 200 and isinstance(playback, list), f"GET Admin/Playback: {status}, {len(playback or [])} entries")
        status, activity, _ = api.get("/Babelfin/Admin/Activity", limit=50, kind="override-created", userId=viewer.user_id)
        check(status == 200 and status_events == 200 and activity == before_events,
              f"generic pre-play creates no override-created event for the viewer ({len(activity or [])} events)")
        status, mine, _ = viewer.get("/Babelfin/Me/Activity", limit=20)
        check(status == 200 and all(e.get("UserId") in (None, viewer.user_id) for e in (mine or [])) and "\\\\" not in json.dumps(mine), "Me/Activity shows only the person's own entries, no paths")

        # Forgetting is harmless when pre-play created no memory; the served choice stays intact.
        status, _, _ = viewer.delete(f"/Babelfin/Me/Overrides/{dualAudio}", scope="Series", manual="true")
        status2, item2, _ = viewer.get(f"/Users/{viewer.user_id}/Items/{dualAudio}")
        ms2 = item2["MediaSources"][0]
        check(status == 204 and status2 == 200 and f"{ms2.get('DefaultAudioStreamIndex')}/{ms2.get('DefaultSubtitleStreamIndex')}" == "2/4", f"DELETE the remembered change (manual=true): {status}; served again {ms2.get('DefaultAudioStreamIndex')}/{ms2.get('DefaultSubtitleStreamIndex')}")

        # A play reported through Jellyfin's session endpoints: a subtitle change 6 s after start is remembered as "subtitles off"
        # once a report at least 10 s later (Babelfin's settle period) still shows it.
        play_session = "rig-play-1"
        start = {"ItemId": dualAudio, "MediaSourceId": ms["Id"], "AudioStreamIndex": 2, "SubtitleStreamIndex": 4, "PlaySessionId": play_session, "PositionTicks": 0, "CanSeek": True, "IsPaused": False, "PlayMethod": "DirectPlay"}
        status, _, _ = viewer.post("/Sessions/Playing", start)
        check(status in (200, 204), f"POST /Sessions/Playing (the app reports Babelfin's tracks): {status}")
        progress = dict(start, PositionTicks=20_000_000, EventName="TimeUpdate")
        viewer.post("/Sessions/Playing/Progress", progress)
        time.sleep(6.5)
        changed = dict(start, PositionTicks=80_000_000, SubtitleStreamIndex=-1, EventName="TimeUpdate")
        status, _, _ = viewer.post("/Sessions/Playing/Progress", changed)
        time.sleep(1.5)
        status, held, _ = viewer.get("/Babelfin/Me/Settings")
        held_manual = [o for o in (held or {}).get("Settings", {}).get("Overrides", []) if o.get("Manual")]
        check(status == 200 and not held_manual,
              f"an in-play subtitle change is not remembered before it has stayed selected for 10 s ({[o.get('Manual') for o in held_manual]})")
        time.sleep(9.5)
        confirmed = dict(changed, PositionTicks=190_000_000)
        viewer.post("/Sessions/Playing/Progress", confirmed)
        time.sleep(1.5)
        status, me2, _ = viewer.get("/Babelfin/Me/Settings")
        manual2 = [o for o in (me2 or {}).get("Settings", {}).get("Overrides", []) if o.get("Manual")]
        check(status == 200 and any(o.get("Scope") == "Series" and o["Manual"].get("SubtitlesOff") is True for o in manual2),
              f"an in-play subtitle change from the generic app is still remembered for the show ({[o.get('Manual') for o in manual2]})")
        status, played_events, _ = api.get("/Babelfin/Admin/Activity", limit=50, kind="override-created", userId=viewer.user_id)
        new_events = [e for e in (played_events or []) if e not in (before_events or [])]
        check(status == 200 and any(e.get("ItemId") == dualAudio and (e.get("Details") or {}).get("Client") == "BabelfinRig"
                                    and (e.get("Details") or {}).get("SubtitleIndex") == "-1" for e in new_events),
              f"Admin/Activity records the generic app's in-play memory as override-created ({len(new_events)} new events)")
        viewer.post("/Sessions/Playing/Stopped", {"ItemId": dualAudio, "MediaSourceId": ms["Id"], "PlaySessionId": play_session, "PositionTicks": 200_000_000})
        status3, item3, _ = viewer.get(f"/Users/{viewer.user_id}/Items/{dualAudio}")
        ms3 = item3["MediaSources"][0]
        check(ms3.get("DefaultSubtitleStreamIndex") in (-1, None), f"the remembered 'off' applies to the next decision for the show ({ms3.get('DefaultAudioStreamIndex')}/{ms3.get('DefaultSubtitleStreamIndex')})")
        viewer.delete(f"/Babelfin/Me/Overrides/{dualAudio}", scope="Series", manual="true")

        # Compatibility mode: a PlaybackInfo writes Jellyfin's remembered selection; turning the mode off restores it.
        # Jellyfin's UserData endpoint doesn't expose stream indices, so the proof is end to end: with Babelfin off for this person,
        # Jellyfin's own choice uses the remembered selection (their remember flags are on by default).
        def set_enabled(on):
            st, doc, _ = viewer.get("/Babelfin/Me/Settings")
            settings = doc["Settings"]
            settings["Enabled"] = on
            st2, _, _ = viewer.put("/Babelfin/Me/Settings", settings)
            return st2

        def jellyfin_own():
            st, it, _ = viewer.get(f"/Users/{viewer.user_id}/Items/{dualAudio}")
            m = it["MediaSources"][0]
            return f"{m.get('DefaultAudioStreamIndex')}/{m.get('DefaultSubtitleStreamIndex')}"

        check(set_enabled(False) == 200, "Babelfin turned off for the viewer (to see Jellyfin's own choice)")
        before_compat = jellyfin_own()
        check(before_compat != "2/4", f"Jellyfin's own choice for the viewer before compatibility mode: {before_compat} (Jellyfin's own remembered selection from the earlier play, not Babelfin's 2/4)")
        set_enabled(True)
        status, admin, _ = api.get("/Babelfin/Admin/Settings")
        admin["CompatibilityMode"] = True
        status, _, _ = api.put("/Babelfin/Admin/Settings", admin)
        check(status == 200, f"compatibility mode on: {status}")
        viewer.post(f"/Items/{dualAudio}/PlaybackInfo", {"UserId": viewer.user_id, "MediaSourceId": ms["Id"]}, userId=viewer.user_id)
        time.sleep(2)
        set_enabled(False)
        with_compat = jellyfin_own()
        check(with_compat == "2/4", f"with Babelfin off, Jellyfin's own choice now follows the remembered selection Babelfin wrote: {with_compat}")
        status, act, _ = api.get("/Babelfin/Admin/Activity", limit=50, kind="jellyfin-setting-changed")
        check(status == 200 and any(e.get("ItemId") and "compatibility" in (e.get("Text") or "") for e in (act or [])), "the write is in the activity log")
        set_enabled(True)
        admin["CompatibilityMode"] = False
        api.put("/Babelfin/Admin/Settings", admin)
        time.sleep(3)
        set_enabled(False)
        after_undo = jellyfin_own()
        check(after_undo == before_compat, f"compatibility mode off: the remembered selection Babelfin overwrote is restored exactly ({after_undo}, was {before_compat})")
        status, act2, _ = api.get("/Babelfin/Admin/Activity", limit=50, kind="restored")
        check(status == 200 and len(act2 or []) >= 1, f"the restore is in the activity log ({len(act2 or [])} entries)")
        set_enabled(True)

    # Babelfin's events stay in its own activity list; Jellyfin's activity log gets none of them.
    status, jf_log, _ = api.get("/System/ActivityLog/Entries", limit=500)
    babelfin_entries = [e for e in ((jf_log or {}).get("Items") or []) if e.get("Type") == "Babelfin"]
    check(status == 200 and not babelfin_entries, f"Jellyfin's activity log has no Babelfin entries ({len(babelfin_entries)}: {[e.get('Name') for e in babelfin_entries][:3]})")

    status, apps, _ = api.get("/Babelfin/Admin/Apps")
    check(status == 200 and any(a.get("Client") == "BabelfinRig" and a.get("Seen") for a in (apps or [])), f"Admin/Apps lists the rig's client as seen ({[a.get('Client') for a in (apps or [])]})")

    # ── Page assets, choice data and simulator endpoints.
    status, page, _ = api.call("GET", "/Babelfin/app", raw=True)
    check(status == 200 and b"Babelfin" in page and b"/Babelfin/assets/" in page, f"GET /Babelfin/app: {status}, {len(page)} bytes")
    for asset in ("app.js", "ui.js", "app.css", "i18n.js", "client.js", "client.css", "dashboard.js", "dashboard.css", "i18n/en.json", "i18n/ja.json", "i18n/pt-BR.json", "i18n-admin/en.json", "i18n-admin/de.json"):
        status, body, headers = api.call("GET", f"/Babelfin/assets/{asset}", raw=True)
        check(status == 200 and len(body) > 50 and headers.get("ETag"), f"asset {asset}: {status}, {len(body) if body else 0} bytes")
    status, body, _ = api.call("GET", "/Babelfin/assets/../Plugin.cs", raw=True)
    check(status in (400, 404), f"asset path traversal refused ({status})")
    status, client, _ = api.call("GET", "/Babelfin/client.js", raw=True)
    text = client.decode("utf-8", "replace") if status == 200 else ""
    check("myPreferencesMenuPage" in text and "Me/Choice" in text and "itemDetailPage" in text and "app-user-menu" in text,
          "client.js carries the avatar menu entry, legacy menu row, choice refresh and item dialog code")
    status, en, _ = api.get("/Babelfin/assets/i18n/en.json")
    status2, ja, _ = api.get("/Babelfin/assets/i18n/ja.json")
    check(status == 200 and en.get("_machineTranslated") is False and status2 == 200 and ja.get("_machineTranslated") is True and set(ja) >= set(k for k in en if not k.startswith("_")) - set(["_machineTranslated"]),
          f"i18n: English is the source, Japanese is marked machine-translated and carries every key ({len(en)} / {len(ja)})")
    if dualAudio:
        status, choice, _ = api.get("/Babelfin/Me/Choice", itemId=dualAudio)
        c0 = (choice or [{}])[0]
        check(status == 200 and c0.get("AudioLanguage") == "ja" and c0.get("SubtitleLanguage") == "en" and c0.get("SubtitleKind") == "full", f"Me/Choice: {c0.get('AudioLanguage')} / {c0.get('SubtitleLanguage')} ({c0.get('SubtitleKind')})")
        # Series choice queries are made for the show's first episode.
        status, ep, _ = api.get(f"/Users/{api.user_id}/Items/{dualAudio}")
        series_id = (ep or {}).get("SeriesId")
        status, schoice, _ = api.get("/Babelfin/Me/Choice", itemId=series_id) if series_id else (0, None, None)
        s0 = (schoice or [{}])[0]
        check(status == 200 and s0.get("AudioLanguage") == "ja" and s0.get("ForItemId") and s0.get("ForItemId") != str(series_id).replace("-", ""),
              f"Me/Choice on the series page: {s0.get('AudioLanguage')} / {s0.get('SubtitleLanguage')} for its first episode ({s0.get('ForItemId')})")
        status, folder_choice, _ = api.get("/Babelfin/Me/Choice", itemId=anime_library_id)
        check(status == 200 and folder_choice == [], f"Me/Choice on a library folder: no chip ({status}, {folder_choice})")

    # The "Audio and subtitles" table's Default values, for the person and for the admin's People editor.
    status, me, _ = api.get("/Babelfin/Me/Settings")
    defaults = (me or {}).get("Defaults") or {}
    rows = defaults.get("Libraries") or {}
    anime_key = str(anime_library_id or "").replace("-", "").lower()
    anime_row = rows.get(anime_key) or {}
    check(status == 200 and (defaults.get("AllLibraries") or {}).get("AudioChain") and anime_row.get("AudioChain") and "WhenAudioNotUnderstood" in anime_row and "AudioFromLibrary" in anime_row,
          f"Me/Settings carries the table's defaults: all libraries {((defaults.get('AllLibraries') or {}).get('AudioChain') or [{}])[0].get('Kind')}, Anime {(anime_row.get('AudioChain') or [{}])[0]} ({len(rows)} library rows)")
    status, admin_defaults, _ = api.get(f"/Babelfin/Admin/Users/{api.user_id}/Defaults")
    check(status == 200 and set((admin_defaults or {}).get("Libraries") or {}) == set(rows), f"Admin/Users/{{id}}/Defaults: {status}, same library rows as the person's page")

    status, job, _ = api.post("/Babelfin/Admin/Simulate", {"UserId": api.user_id, "ItemId": anime_library_id})
    check(status in (200, 202) and job and job.get("JobId"), f"POST Admin/Simulate: {status} {job}")
    rows = []
    if status in (200, 202):
        for _ in range(60):
            status, state, _ = api.get(f"/Babelfin/Admin/Simulate/{job['JobId']}")
            if status == 200 and state.get("Done"):
                rows = state.get("Rows") or []
                break
            time.sleep(1)
        flags = {f for r in rows for f in (r.get("Flags") or [])}
        partial = [r for r in rows if "partial-chosen" in (r.get("Flags") or [])]
        forcedOnly = ids.get("Quorim Test (2043) S01E01")
        check(len(rows) >= 7 and all("Explanation" in r for r in rows) and "no-subtitles" in flags
              and len(partial) == 1 and partial[0].get("ItemId") == forcedOnly and partial[0].get("AudioIndex") == 3
              and partial[0].get("SubtitleIndex") == 0 and "coverage" in (partial[0].get("Explanation") or "")
              and "\\\\" not in json.dumps(rows) and "/media/" not in json.dumps(rows),
              f"simulator over the Anime library: {len(rows)} rows, flags {sorted(flags)}, no paths")
    # The retired coverage endpoint answers 404 even with valid parameters.
    status, _, _ = api.call("GET", f"/Babelfin/Admin/Coverage?libraryId={anime_library_id}&userId={api.user_id}", raw=True)
    check(status == 404, f"GET Admin/Coverage is gone: {status} (want 404)")
