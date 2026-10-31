#!/usr/bin/env node
/* Babelfin's browser tests: the real jellyfin-web 12.2 and Babelfin's real pages in a real (headless) Chrome, against a running
   Jellyfin with the plugin, normally the rig's (tools/rig/rig.py --keep, or rig.py --e2e, which runs this file).

   Usage: node tools/rig/e2e/run.js --base http://127.0.0.1:8098 [--only 4,7] [--skip 4] [--headed] [--serial] [--verbose]
            [--settle 800] [--user rigadmin] [--password rig-pass-1] [--viewer rigviewer] [--quick-sweeps]
   Prints one PASS/FAIL line per check, then "ALL PASS" or "FAILURES: n"; the exit code is 0 only when everything passed.
   The keyboard sweeps (every option of every "Audio and subtitles" dropdown, picked with the arrow keys) run one browser tab per
   dropdown in parallel, once per table mode; --serial runs them one after another in the main tab; --quick-sweeps checks most
   languages on the person page after 100 ms instead of the full settle time. --verbose prints every sweep step.
   Tests (--only/--skip take numbers or behavior ids): 1 signin, 2 menu, 3 legacy, 4 mode, 5 table (sweeps),
   6 defaults, 7 effect, 8 advanced, 9 admin (sweeps), 10 tools, 11 item, 12 phone, 14 menus, 15 restore,
   16 people, 17 newusers, 18 wording, 19 v8 (v6/v7 aliases), 20 alignment, 21 route, 22 info, 23 reset,
   24 itemmenu, 25 overrides, 26 preplay-memory, 27 partial-reset, 28 upgrade-note, 29 smart-subtitles,
   30 audio-choices, 31 genres, 32 genre-precedence, 33 original-audio, 34 choice-status, 35 genre-migration,
   36 screenshots-dir (real desktop/375 Settings PNGs), 37 forced-only, 38 burned-removal, 39 admin-leave, 40 legacy-admin-leave, 41 repeated-admin-leave, 42 sidebar-admin-leave, 43 field-conflicts; 13 hygiene always runs last.
   With --verbose test 20 also saves full-page screenshots look-*.png.
   Screenshots of failures go to %TEMP%\babelfin-e2e\. Every test puts back what it changed through the API, so they can run
   in any order (each signs in first when the browser isn't signed in yet). Test 16 adds a Jellyfin user (rige2enofile) and
   removes it again. Tests 11, 18, 20, 24 and 25 add show and movie overrides (24 also marks an episode played, and 18, 20 and 25
   sign the second person in through the API): the overrides are taken away again with DELETE Me/Overrides/All (a settings PUT keeps
   remembered track changes), the second person's whole document is put back, and the session ends. */
'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { Chrome, sleep } = require('./cdp');

// ───────────────────────────── What the tests rely on ─────────────────────────────

const SPEC = {
    audioFor: (row) => 'Audio for ' + row,
    subtitlesFor: (row) => 'Subtitles for ' + row,
    allLibraries: 'All libraries',
    videoDefault: 'Video default',
    custom: 'Custom',
    audioChoices: { mine: 'My languages, in order', original: "The title's original language" },
    subtitleLabels: { off: 'Off', smart: "When the audio isn't in my languages", signs: 'Signs and songs only' },
    adminSmart: "When the audio isn't in the viewer's languages",
    mineGroup: 'My languages',
    allGroup: 'All languages',
    extraCodes: ['pt-BR', 'pt-PT', 'zh-Hans', 'zh-Hant', 'es-419', 'fr-CA', 'yue', 'fil'],
    helpVideo: 'Video default: the track the video file marks as default',
    helpSmart: "When the audio isn't in my languages: full subtitles when the audio isn't in a language listed under My languages",
    menuText: 'Babelfin Settings',
    tableFields: ['AudioChain', 'ForeignTitles', 'WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'SubtitleLanguages'],
    // The either/or control.
    modeGroup: 'Audio and subtitles',
    modeSame: 'Same for every library',
    modePer: 'Choose for each library',
    serverEach: 'Default (set by the server for each library)',
    // Advanced groups.
    enabledSwitch: 'Let Babelfin choose your audio and subtitles',
    // Advanced groups in their displayed order.
    personGroups: ['Languages for subtitles', 'Subtitles', 'Audio', 'Track changes', 'Start over'],
    burnedInGroup: 'Burned-in subtitles',
    personGroupContents: {
        Subtitles: ['When there are no full subtitles in your languages', 'Text or picture subtitles', /^Embedded or separate/, 'Always show subtitles',
            'Subtitles for the deaf and hard of hearing (SDH)', "When the audio's language is unknown"],
        Audio: ['Audio description tracks', 'Commentary tracks'],
        // "Replace Jellyfin's remembered tracks" shows only while it is on (checked in 14 and 15).
        'Track changes': ['Remember track changes for']
    },
    personRemoved: ['Rules', 'Use my settings everywhere', 'Export settings', 'Import settings', 'Export, import and reset', 'If several tracks match',
        'How the original language is found', "Titles in a language I don't understand",
        // Retired names and the option now controlled by the administrator.
        'Languages I read', 'Babelfin chooses my tracks', 'If there are no full subtitles', 'Subtitle format', 'SDH subtitles', /^I prefer subtitles/,
        'Audio tracks without a language', 'I use audio description', 'Skip commentary tracks', 'Treat audio in an unknown language as understood'],
    adminGroups: ['Apps', 'New users', 'Track recognition'],
    adminGroupContents: {
        Apps: ['Apps not listed', /^Fix tracks after playback starts/, "Also save choices as Jellyfin's remembered tracks"],
        'New users': ['My languages'],
        'Track recognition': ['Track names', 'Language codes', 'Audio tracks with no language']
    },
    adminRemoved: ['Rules for everyone', 'Show and movie defaults', 'Burned-in subtitles reported by people', 'Original-language libraries', 'Timing',
        /^Test mode/, 'If there are no full subtitles', 'Coverage', 'Preview', 'Show only', 'Download CSV'],
    // Troubleshooting and the separate Back up and restore section.
    toolsGroups: ['Check a title', 'Activity'],
    chosen: 'Chosen by Babelfin',
    videoMark: 'Video default',
    capRows: 50,
    activityPage: 50,
    // Restore defaults and Start over.
    restore: 'Restore defaults',
    startOver: 'Start over',
    // The admin page.
    topTitle: 'Defaults for everyone',
    topLine: 'People who leave a row on Default get these. Changes apply right away.',
    people: 'People',
    peopleColumns: ['Name', 'Audio', 'Subtitles', 'Edit'],
    perLibraryWords: 'Set per library',
    defaultWord: 'Default',
    adminSmartWords: "When the audio isn't in their languages",
    adminSections: ['Defaults for everyone', 'People', 'Advanced', 'Troubleshooting', 'Back up and restore'],
    backup: 'Back up and restore',
    detected: '(detected)',
    // Displayed wording.
    myLanguages: 'My languages',
    oldWording: ['Languages understood', 'Languages I understand', 'Understands', 'Languages I read', 'I use audio description', 'I prefer subtitles',
        'Babelfin chooses my tracks'],
    // Babelfin Settings under Jellyfin's header.
    route: '#/babelfin',
    // Reset all settings.
    resetButton: 'Reset all settings',
    resetDone: 'All settings were reset.',
    // Server versions and app defaults.
    pluginVersion: '0.19.0.0',
    configVersion: 8,
    personSchemaVersion: 3,
    desktop: 'Jellyfin Desktop',
    desktopMode: 'ReplaceOnAutoplay',
    desktopLabel: "Use Babelfin's choice when playing the next episode"
};

/**
 * The person page's plain option menus, with the field each stores in Everyday (label: the English label; group:
 * the Advanced group; enumName: the i18n enum of its values; bool: Yes/No; avoid: values a test must not pick, since they need more input).
 */
const PERSON_MENUS = [
    { field: 'IfNothingMatches', label: 'When there are no full subtitles in your languages', group: 'Subtitles', enumName: 'WhenNothingMatches', avoid: ['FullInAnyBackupLanguage'] },
    { field: 'Format', label: 'Text or picture subtitles', group: 'Subtitles', enumName: 'FormatPreference' },
    { field: 'Embedded', label: /^Embedded or separate/, group: 'Subtitles', enumName: 'EmbeddedPreference' },
    // Before "Always show subtitles", which disables it while it is on.
    { field: 'Sdh', label: 'Subtitles for the deaf and hard of hearing (SDH)', group: 'Subtitles', enumName: 'Preference' },
    { field: 'PrefersSdh', label: 'Always show subtitles', group: 'Subtitles', bool: true },
    { field: 'UnknownAudioCountsAsUnderstood', label: "When the audio's language is unknown", group: 'Subtitles', bool: true },
    { field: 'UsesAudioDescription', label: 'Audio description tracks', group: 'Audio', bool: true },
    { field: 'AvoidCommentary', label: 'Commentary tracks', group: 'Audio', bool: true },
    { field: 'Memory', label: 'Remember track changes for', group: 'Track changes', enumName: 'MemoryScope' }
];

/** The person-level switch Restore defaults turns off. */
const NEXT_EPISODE = { field: 'HandleNextEpisode', label: "Replace Jellyfin's remembered tracks", group: 'Track changes', bool: true, person: true };

/** What the person page's Restore defaults clears: these Everyday fields, and HandleNextEpisode = false. */
const PERSON_RESTORED = ['IfNothingMatches', 'Format', 'Embedded', 'Sdh', 'PrefersSdh', 'UnlabelledAudio', 'UnlabelledAudioLanguage', 'UsesAudioDescription',
    'AvoidCommentary', 'UnknownAudioCountsAsUnderstood', 'Memory'];

/** What the admin page's Restore defaults puts back: the Apps group and Track recognition. */
const ADMIN_RESTORED = ['AppModes', 'UnknownAppMode', 'CorrectAfterStart', 'CompatibilityMode', 'Keywords', 'LanguageAliases', 'Everyday.UnlabelledAudio', 'Everyday.UnlabelledAudioLanguage'];

/** The admin page's option menus outside the table; the per-app rows are found by their aria-label prefix. */
const ADMIN_MENUS = {
    notListed: { field: 'UnknownAppMode', label: 'Apps not listed', group: 'Apps' },
    unlabelled: { field: 'Everyday.UnlabelledAudio', label: 'Audio tracks with no language', group: 'Track recognition' },
    appPrefix: 'When the app asks for specific tracks'
};

/** What a table choice stores: the five table fields of a layer; null = not set. */
function storedFor(audio, subtitles) {
    const out = {};
    if (audio !== undefined) {
        out.ForeignTitles = null;
        if (audio === '') {
            out.AudioChain = null;
        } else if (audio === 'video') {
            out.AudioChain = [{ Kind: 'VideoDefault' }];
        } else if (audio === 'mine' || audio === 'original') {
            out.AudioChain = [{ Kind: audio === 'mine' ? 'MyLanguages' : 'OriginalLanguage' }, { Kind: 'VideoDefault' }];
        } else {
            out.AudioChain = [{ Kind: 'Language', Language: audio.slice(5) }, { Kind: 'VideoDefault' }];
        }
    }
    if (subtitles !== undefined) {
        const table = { '': [null, null], video: ['VideoDefault', 'VideoDefault'], off: ['Off', 'Off'], smart: ['PartialOnly', 'Full'], signs: ['PartialOnly', 'PartialOnly'] };
        const pair = table[subtitles] || ['Full', 'Full'];
        out.WhenAudioUnderstood = pair[0];
        out.WhenAudioNotUnderstood = pair[1];
        out.SubtitleLanguages = table[subtitles] ? null : [subtitles.slice(5)];
    }
    return out;
}

// ───────────────────────────── Arguments and output ─────────────────────────────

function parseArgs(argv) {
    const o = {
        base: null, only: null, skip: [], headed: false, serial: false, verbose: false, settle: 800, quickSweeps: false,
        user: 'rigadmin', password: 'rig-pass-1', viewer: 'rigviewer',
        shots: path.join(process.env.TEMP || os.tmpdir(), 'babelfin-e2e'),
        screenshotsDir: path.resolve(__dirname, '../../../.artifacts/screenshots')
    };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        const value = () => {
            if (i + 1 >= argv.length) {
                throw new Error(a + ' needs a value');
            }
            return argv[++i];
        };
        switch (a) {
            case '--base': o.base = value().replace(/\/+$/, ''); break;
            case '--only': o.only = value().split(',').map((s) => s.trim()).filter(Boolean); break;
            case '--skip': o.skip = value().split(',').map((s) => s.trim()).filter(Boolean); break;
            case '--headed': o.headed = true; break;
            case '--serial': o.serial = true; break;
            case '--verbose': o.verbose = true; break;
            case '--quick-sweeps': o.quickSweeps = true; break;
            case '--settle': o.settle = Number(value()); break;
            case '--user': o.user = value(); break;
            case '--password': o.password = value(); break;
            case '--viewer': o.viewer = value(); break;
            case '--shots': o.shots = value(); break;
            case '--screenshots-dir': o.screenshotsDir = path.resolve(value()); break;
            case '-h': case '--help': o.help = true; break;
            default: throw new Error('unknown argument ' + a);
        }
    }
    return o;
}

const results = [];
let shotCount = 0;
const MAX_SHOTS = 60;

function slug(text) {
    return String(text).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 60);
}

function clone(value) {
    return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

/** JSON with sorted keys and null/undefined properties left out, so "not set" compares equal however it is written. */
function canon(value) {
    if (Array.isArray(value)) {
        return '[' + value.map(canon).join(',') + ']';
    }
    if (value && typeof value === 'object') {
        return '{' + Object.keys(value).filter((k) => value[k] !== null && value[k] !== undefined).sort()
            .map((k) => JSON.stringify(k) + ':' + canon(value[k])).join(',') + '}';
    }
    return JSON.stringify(value === undefined ? null : value);
}

function same(a, b) {
    return canon(a) === canon(b);
}

function compactId(id) {
    return String(id || '').replace(/-/g, '').toLowerCase();
}

function apos(text) {
    return String(text == null ? '' : text).replace(/[‘’]/g, "'");
}

function uniq(list) {
    return list.filter((x, i) => list.indexOf(x) === i);
}

/**
 * Flattens a settings document to { "path": leaf } for diffs: null and absent are the same; arrays of library layers and of
 * overrides are keyed by their ids so a reorder isn't a change.
 */
function flatten(value, prefix, out) {
    out = out || {};
    if (value === null || value === undefined) {
        return out;
    }
    if (Array.isArray(value)) {
        if (value.length && value.every((v) => v && typeof v === 'object' && v.LibraryId)) {
            value.forEach((v) => flatten(v, prefix + '[' + compactId(v.LibraryId) + ']', out));
        } else if (value.length && value.every((v) => v && typeof v === 'object' && v.ItemId && v.Scope !== undefined)) {
            value.forEach((v) => flatten(v, prefix + '[' + compactId(v.ItemId) + ':' + v.Scope + ']', out));
        } else if (!value.length) {
            out[prefix] = '[]';
        } else {
            value.forEach((v, i) => flatten(v, prefix + '[' + i + ']', out));
        }
        return out;
    }
    if (typeof value === 'object') {
        Object.keys(value).forEach((k) => flatten(value[k], prefix ? prefix + '.' + k : k, out));
        return out;
    }
    out[prefix] = value;
    return out;
}

/** The leaf paths that differ (a layer's computed IsEmpty flag is left out; an empty list and no list are the same). */
function changedPaths(before, after) {
    const a = flatten(before, '');
    const b = flatten(after, '');
    const v = (m, k) => (m[k] === '[]' ? undefined : m[k]);
    return Array.from(new Set(Object.keys(a).concat(Object.keys(b)))).filter((k) => v(a, k) !== v(b, k) && !/(^|\.)IsEmpty$/.test(k)).sort();
}

// ───────────────────────────── API (Node fetch, with the token jellyfin-web stored) ─────────────────────────────

class Api {
    constructor(base, token, userId) {
        this.base = base;
        this.token = token;
        this.userId = userId;
    }

    header() {
        return 'MediaBrowser Client="Babelfin E2E", Device="e2e", DeviceId="babelfin-e2e", Version="1", Token="' + String(this.token).replace(/"/g, '') + '"';
    }

    async call(method, p, body, query) {
        const url = new URL(this.base + p);
        Object.keys(query || {}).forEach((k) => url.searchParams.set(k, query[k]));
        const headers = { Authorization: this.header(), Accept: 'application/json' };
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
        }
        const r = await fetch(url, { method: method, headers: headers, body: body === undefined ? undefined : JSON.stringify(body) });
        const text = await r.text();
        let parsed = null;
        try {
            parsed = text ? JSON.parse(text) : null;
        } catch (e) {
            parsed = text;
        }
        return { status: r.status, body: parsed };
    }

    get(p, query) { return this.call('GET', p, undefined, query); }
    put(p, body, query) { return this.call('PUT', p, body, query); }
    post(p, body, query) { return this.call('POST', p, body, query); }
    del(p, query) { return this.call('DELETE', p, undefined, query); }
}

// ───────────────────────────── Page functions (run in the browser; self-contained, no closures) ─────────────────────────────

/**
 * A small helper library installed in every document of every tab (Page.addScriptToEvaluateOnNewDocument), so the page functions
 * below can share the page lookups: visibility, the either/or radiogroup, and collapsible sections and groups.
 */
function pageHelpers() {
    if (window.__bfE2E) {
        return;
    }
    function text(el) {
        return el ? (el.textContent || '').replace(/\s+/g, ' ').trim() : '';
    }
    // A control's name without decorative glyphs (chevrons, arrows) at either end.
    function label(el) {
        return text(el).replace(/^[^\p{L}\p{N}]+|[^\p{L}\p{N})]+$/gu, '');
    }
    function vis(el) {
        if (!el || !el.isConnected || el.closest('.hide') || el.closest('[hidden]')) {
            return false;
        }
        var r = el.getBoundingClientRect();
        var cs = getComputedStyle(el);
        return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none';
    }
    function root(scope) {
        if (!scope) {
            return document;
        }
        return typeof scope === 'string' ? document.querySelector(scope) : scope;
    }
    function nameOf(el) {
        var l = el.getAttribute('aria-label');
        if (l) {
            return l.trim();
        }
        var by = el.getAttribute('aria-labelledby');
        if (by) {
            return by.split(/\s+/).map(function (id) { var n = document.getElementById(id); return n ? text(n) : ''; }).join(' ').trim();
        }
        if (el.tagName === 'FIELDSET') {
            var lg = el.querySelector('legend');
            return lg ? text(lg) : '';
        }
        return '';
    }
    function headingOf(el) {
        return el.closest('h1, h2, h3, h4, h5, h6, [role="heading"]');
    }
    function isOpen(ctrl) {
        return ctrl.tagName === 'DETAILS' ? ctrl.open : ctrl.getAttribute('aria-expanded') === 'true';
    }
    function clickTarget(ctrl) {
        return ctrl.tagName === 'DETAILS' ? ctrl.querySelector(':scope > summary') : ctrl;
    }
    function regionOf(ctrl) {
        if (ctrl.tagName === 'DETAILS') {
            return ctrl;
        }
        var id = ctrl.getAttribute('aria-controls');
        if (id) {
            var r = document.getElementById(id);
            if (r) {
                return r;
            }
        }
        var h = headingOf(ctrl) || ctrl;
        return h.nextElementSibling;
    }
    function regionShown(ctrl) {
        if (ctrl.tagName === 'DETAILS') {
            return ctrl.open;
        }
        var r = regionOf(ctrl);
        return !!r && vis(r);
    }
    // A collapsible section or group called `title` under scope: a heading button with aria-expanded, else a <details>.
    function disclosure(scope, title) {
        var r = root(scope);
        if (!r) {
            return null;
        }
        var buttons = Array.prototype.filter.call(r.querySelectorAll('button[aria-expanded], [role="button"][aria-expanded]'), function (b) { return label(b) === title; });
        var b = buttons.find(vis) || buttons[0];
        if (b) {
            return b;
        }
        var ds = Array.prototype.filter.call(r.querySelectorAll('details'), function (d) {
            var s = d.querySelector(':scope > summary');
            return s && label(s) === title;
        });
        return ds.find(function (d) { return vis(d.querySelector(':scope > summary')); }) || ds[0] || null;
    }
    // The visible heading buttons (aria-expanded) inside a region, in order, without those nested in another group's content.
    function groups(region) {
        if (!region) {
            return [];
        }
        var all = Array.prototype.filter.call(region.querySelectorAll('button[aria-expanded], [role="button"][aria-expanded]'), function (b) {
            return !!headingOf(b) && vis(b);
        });
        return all.filter(function (c) {
            return !all.some(function (o) {
                var reg = o !== c ? regionOf(o) : null;
                return reg && reg !== region && !reg.contains(o) && reg.contains(c);
            });
        });
    }
    // <details> inside a region, for diagnostics; normal groups use heading buttons.
    function detailsIn(region) {
        return region ? Array.prototype.filter.call(region.querySelectorAll('details'), function (d) { return d !== region && vis(d); })
            .map(function (d) { return label(d.querySelector(':scope > summary')); }) : [];
    }
    function describe(ctrl) {
        var region = regionOf(ctrl);
        var h = ctrl.tagName === 'DETAILS' ? null : headingOf(ctrl);
        return {
            name: label(clickTarget(ctrl)),
            kind: ctrl.tagName === 'DETAILS' ? 'details' : 'button',
            expanded: isOpen(ctrl),
            heading: !!h,
            headingTag: h ? (h.getAttribute('role') === 'heading' ? 'role=heading' : h.tagName.toLowerCase()) : null,
            controls: ctrl.getAttribute('aria-controls'),
            regionFound: !!region,
            regionShown: regionShown(ctrl),
            visible: vis(clickTarget(ctrl)),
            focused: document.activeElement === clickTarget(ctrl)
        };
    }
    function radios(g) {
        return g ? Array.prototype.slice.call(g.querySelectorAll('input[type="radio"], [role="radio"]')) : [];
    }
    function radioLabel(x) {
        if (x.getAttribute('role') === 'radio') {
            return x.getAttribute('aria-label') || text(x);
        }
        if (x.getAttribute('aria-label')) {
            return x.getAttribute('aria-label');
        }
        if (x.labels && x.labels.length) {
            return text(x.labels[0]);
        }
        var l = x.closest('label');
        return l ? text(l) : '';
    }
    function radioChecked(x) {
        return x.getAttribute('role') === 'radio' ? x.getAttribute('aria-checked') === 'true' : !!x.checked;
    }
    function radioClickEl(x) {
        if (x.getAttribute('role') === 'radio') {
            return x;
        }
        var r = x.getBoundingClientRect();
        var cs = getComputedStyle(x);
        if (r.width > 4 && r.height > 4 && Number(cs.opacity) > 0.05 && cs.visibility !== 'hidden') {
            return x;
        }
        return (x.labels && x.labels[0]) || x.closest('label') || x;
    }
    // The either/or control under scope: role=radiogroup named `name`, else the group around a radio labelled `fallbackLabel`.
    function modeGroup(scope, name, fallbackLabel) {
        var r = root(scope);
        if (!r) {
            return null;
        }
        var named = Array.prototype.filter.call(r.querySelectorAll('[role="radiogroup"], fieldset, [role="group"]'), function (g) { return nameOf(g) === name; });
        var g = named.find(vis) || named[0] || null;
        if (g) {
            return g;
        }
        var x = Array.prototype.filter.call(r.querySelectorAll('input[type="radio"], [role="radio"]'), function (e) { return radioLabel(e) === fallbackLabel; });
        var hit = x.find(function (e) { return vis(radioClickEl(e)); }) || x[0];
        if (!hit) {
            return null;
        }
        return hit.closest('[role="radiogroup"], fieldset, [role="group"]') || (hit.parentElement && hit.parentElement.parentElement) || null;
    }
    function modeInfo(scope, name, fallbackLabel) {
        var g = modeGroup(scope, name, fallbackLabel);
        if (!g) {
            return { found: false };
        }
        var list = radios(g).map(function (x) {
            var c = radioClickEl(x);
            var rect = c.getBoundingClientRect();
            return {
                label: radioLabel(x), checked: radioChecked(x), focused: document.activeElement === x, native: x.tagName === 'INPUT',
                tabIndex: x.tabIndex, disabled: !!x.disabled || x.getAttribute('aria-disabled') === 'true', visible: vis(c),
                box: { left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom, width: rect.width },
                clipped: c.scrollWidth > c.clientWidth + 1
            };
        });
        var checked = list.filter(function (x) { return x.checked; });
        var gr = g.getBoundingClientRect();
        return {
            found: true, role: g.getAttribute('role') || g.tagName.toLowerCase(), name: nameOf(g), visible: vis(g), radios: list,
            checked: checked.length === 1 ? checked[0].label : (checked.length ? checked.map(function (x) { return x.label; }).join('+') : null),
            box: { left: gr.left, right: gr.right, width: gr.width }
        };
    }
    function modeRadio(scope, name, fallbackLabel, which) {
        var g = modeGroup(scope, name, fallbackLabel);
        return radios(g).find(function (x) { return radioLabel(x) === which; }) || null;
    }
    // ── Plain option menus and switches outside the "Audio and subtitles" table ──
    // A form control's name: aria-label, its <label for>, the <label> around it, aria-labelledby, else the label of its row.
    function controlLabel(el) {
        var a = el.getAttribute('aria-label');
        if (a) {
            return a.trim();
        }
        if (el.id) {
            var l = document.querySelector('label[for="' + (window.CSS && CSS.escape ? CSS.escape(el.id) : el.id) + '"]');
            if (l) {
                return label(l) || text(l);
            }
        }
        var wrap = el.closest('label');
        if (wrap) {
            return label(wrap) || text(wrap);
        }
        if (el.getAttribute('aria-labelledby')) {
            return nameOf(el);
        }
        var row = el.closest('.bfui-row, .bf-row');
        var rl = row && row.querySelector('.bfui-row-label, .bf-row-label');
        return rl ? (label(rl) || text(rl)) : '';
    }
    function isTableSelect(el) {
        return el.tagName === 'SELECT' && /^(Audio|Subtitles) for /.test(el.getAttribute('aria-label') || '');
    }
    // A checkbox counts as shown when it or its label is (switches hide the input itself).
    function controlShown(el) {
        if (!el.isConnected || el.closest('.hide') || el.closest('[hidden]')) {
            return false;
        }
        if (el.tagName === 'SELECT') {
            return vis(el);
        }
        return vis(el) || (el.labels && Array.prototype.some.call(el.labels, vis)) || (!!el.closest('label') && vis(el.closest('label')));
    }
    // The name of the collapsible group (or section) a node sits in: the nearest region that a heading button controls.
    function groupOf(el) {
        for (var n = el.parentElement; n; n = n.parentElement) {
            if (n.id) {
                var b = document.querySelector('[aria-controls="' + n.id + '"]');
                if (b && b !== el) {
                    return label(b);
                }
            }
            if (n.tagName === 'DETAILS') {
                var s = n.querySelector(':scope > summary');
                return s ? label(s) : '';
            }
        }
        return '';
    }
    // Every shown <select> and checkbox under scope except the table's dropdowns (and, with skip, anything inside skip).
    function controlsIn(scope, skip) {
        var r = root(scope);
        if (!r) {
            return [];
        }
        var skipEls = skip ? Array.prototype.slice.call(document.querySelectorAll(skip)) : [];
        return Array.prototype.filter.call(r.querySelectorAll('select, input[type="checkbox"]'), function (el) {
            return !isTableSelect(el) && controlShown(el) && !skipEls.some(function (s) { return s.contains(el); });
        });
    }
    function controlInfo(el) {
        var sel = el.tagName === 'SELECT';
        var opt = sel ? el.options[el.selectedIndex] : null;
        var f = el.closest('[data-field]');
        return {
            kind: sel ? 'select' : 'checkbox', label: controlLabel(el), field: f ? f.getAttribute('data-field') : null, group: groupOf(el),
            value: sel ? el.value : String(!!el.checked), text: opt ? text(opt) : null, disabled: !!el.disabled,
            options: sel ? Array.prototype.map.call(el.options, function (o) { return { v: o.value, l: text(o), d: !!o.disabled }; }) : null
        };
    }
    // want: { field, label } or { label: { re } }; a data-field match wins, else the label.
    function controlMatches(info, want) {
        if (want.field && info.field && info.field === want.field) {
            return true;
        }
        var l = want.label;
        return l && l.re ? new RegExp(l.re).test(info.label) : info.label === l;
    }
    function findControl(scope, want, skip) {
        var list = controlsIn(scope, skip);
        return list.find(function (el) { return controlMatches(controlInfo(el), want); }) || null;
    }
    window.__bfE2E = {
        text: text, label: label, vis: vis, root: root, nameOf: nameOf, headingOf: headingOf, isOpen: isOpen, clickTarget: clickTarget,
        regionOf: regionOf, regionShown: regionShown, disclosure: disclosure, groups: groups, detailsIn: detailsIn, describe: describe,
        radios: radios, radioLabel: radioLabel, radioChecked: radioChecked, radioClickEl: radioClickEl, modeGroup: modeGroup,
        modeInfo: modeInfo, modeRadio: modeRadio, controlLabel: controlLabel, controlsIn: controlsIn, controlInfo: controlInfo,
        controlMatches: controlMatches, findControl: findControl, groupOf: groupOf
    };
}

const PAGE_HELPERS = '(' + pageHelpers.toString() + ')();';

/** The either/or control under scope (see pageHelpers.modeInfo). */
function pgModeInfo(scope, name, fallbackLabel) {
    return window.__bfE2E.modeInfo(scope, name, fallbackLabel);
}

/** The element to click for one of the two modes (its label when the radio input itself is visually hidden). */
function pgModeClickEl(scope, name, fallbackLabel, which) {
    var h = window.__bfE2E;
    var x = h.modeRadio(scope, name, fallbackLabel, which);
    return x ? h.radioClickEl(x) : null;
}

/** Focuses one of the two radios (what Tab or an arrow key would land on) without clicking it. */
function pgModeFocus(scope, name, fallbackLabel, which) {
    var x = window.__bfE2E.modeRadio(scope, name, fallbackLabel, which);
    if (!x) {
        return false;
    }
    x.scrollIntoView({ block: 'center' });
    x.focus();
    return document.activeElement === x;
}

/** A collapsible section (or group) called title under scope, described; with its groups when it is open. */
function pgSection(scope, title) {
    var h = window.__bfE2E;
    var ctrl = h.disclosure(scope, title);
    if (!ctrl) {
        return { found: false };
    }
    var out = h.describe(ctrl);
    out.found = true;
    var region = h.regionOf(ctrl);
    out.groups = out.expanded ? h.groups(region).map(h.describe) : [];
    out.details = out.expanded ? h.detailsIn(region) : [];
    return out;
}

/** The element to click to open or close the section or group `title` (inside the section `within`, when given). */
function pgDisclosureEl(scope, title, within) {
    var h = window.__bfE2E;
    var r = scope;
    if (within) {
        var outer = h.disclosure(scope, within);
        r = outer ? h.regionOf(outer) : null;
        if (!r) {
            return null;
        }
    }
    var ctrl = h.disclosure(r, title);
    return ctrl ? h.clickTarget(ctrl) : null;
}

/** Focuses the heading button of a group. */
function pgDisclosureFocus(scope, title, within) {
    var h = window.__bfE2E;
    var r = scope;
    if (within) {
        var outer = h.disclosure(scope, within);
        r = outer ? h.regionOf(outer) : null;
    }
    var ctrl = r ? h.disclosure(r, title) : null;
    var el = ctrl ? h.clickTarget(ctrl) : null;
    if (!el) {
        return false;
    }
    el.scrollIntoView({ block: 'center' });
    el.focus();
    return document.activeElement === el;
}

/** One group of a section: described, with the texts of its content's labels, headings, buttons and table headers. */
function pgGroup(scope, within, title) {
    var h = window.__bfE2E;
    var outer = within ? h.disclosure(scope, within) : null;
    var r = within ? (outer ? h.regionOf(outer) : null) : h.root(scope);
    var ctrl = r ? h.disclosure(r, title) : null;
    if (!ctrl) {
        return { found: false };
    }
    var out = h.describe(ctrl);
    out.found = true;
    var region = h.regionOf(ctrl);
    out.texts = region ? Array.prototype.filter.call(region.querySelectorAll('label, h2, h3, h4, h5, legend, button, th, .bf-label, .bfui-label, .bf-row-label, .bfui-row-label, p, span, summary'), function (n) { return h.text(n).length < 160; })
        .map(function (n) { return h.text(n); }).filter(Boolean) : [];
    out.radiogroups = region ? Array.prototype.map.call(region.querySelectorAll('[role="radiogroup"]'), h.nameOf) : [];
    out.selects = region ? Array.prototype.map.call(region.querySelectorAll('select[aria-label]'), function (s) { return s.getAttribute('aria-label'); }) : [];
    return out;
}

/** Which of the given texts (strings for an exact match, { re } for a pattern) any element under scope shows, hidden or not. */
function pgFindTexts(scope, wanted) {
    var h = window.__bfE2E;
    var r = h.root(scope);
    if (!r) {
        return { scope: false, found: [] };
    }
    var found = [];
    var nodes = Array.prototype.filter.call(r.querySelectorAll('*'), function (n) { return n.tagName !== 'OPTION' && n.tagName !== 'SCRIPT' && n.tagName !== 'STYLE'; });
    wanted.forEach(function (w) {
        var re = w && w.re ? new RegExp(w.re) : null;
        var hit = nodes.find(function (n) {
            var t = h.text(n);
            if (t.length > 200) {
                return false;
            }
            var l = h.label(n);
            return re ? (re.test(t) || re.test(l)) : (t === w || l === w);
        });
        if (hit) {
            found.push((re ? '/' + w.re + '/' : w) + ' (' + hit.tagName.toLowerCase() + (hit.className && typeof hit.className === 'string' ? '.' + hit.className.trim().split(/\s+/).join('.') : '') + ', ' + (h.vis(hit) ? 'visible' : 'hidden') + ')');
        }
    });
    return { scope: true, found: found };
}

/** localStorage as { key: value } (values cut to 80 characters). */
function pgStorage() {
    var out = {};
    try {
        for (var i = 0; i < localStorage.length; i++) {
            var k = localStorage.key(i);
            out[k] = String(localStorage.getItem(k)).slice(0, 80);
        }
    } catch (e) {
        out.__error = String(e);
    }
    return out;
}

/** Removes Babelfin's remembered open/closed states from localStorage (not jellyfin-web's keys, not the person page's device id). */
function pgClearBabelfinStorage() {
    var removed = [];
    try {
        var keys = [];
        for (var i = 0; i < localStorage.length; i++) {
            keys.push(localStorage.key(i));
        }
        keys.forEach(function (k) {
            if (/babelfin|^bf[._-]/i.test(k) && k !== 'babelfin-device-id' && k !== 'jellyfin_credentials') {
                localStorage.removeItem(k);
                removed.push(k);
            }
        });
    } catch (e) {
        removed.push('error: ' + e);
    }
    return removed;
}

/**
 * Every visible "Audio for …" / "Subtitles for …" dropdown under scope, the save bar and the change-event log. With
 * dirtyButtonScope (the People editor, which has a Save button instead of a bar), "saveBar" describes that Save button:
 * visible = enabled.
 */
function pgSnap(scope, fullLabel, saveBarSelector, dirtyButtonScope) {
    function shown(el) {
        if (el.closest('.hide') || el.closest('[hidden]')) {
            return false;
        }
        var r = el.getBoundingClientRect();
        var cs = getComputedStyle(el);
        return (r.width > 0 || r.height > 0) && cs.display !== 'none' && cs.visibility !== 'hidden';
    }
    var root = scope ? document.querySelector(scope) : document;
    var out = { found: !!root, selects: [], saveBar: null, changes: (window.__bfChanges || []).slice(), active: null };
    var a = document.activeElement;
    out.active = a ? (a.getAttribute('aria-label') || a.tagName) : null;
    if (root) {
        Array.prototype.forEach.call(root.querySelectorAll('select[aria-label]'), function (s) {
            var label = s.getAttribute('aria-label');
            if (!/^(Audio|Subtitles) for /.test(label) || !shown(s)) {
                return;
            }
            var opt = s.options[s.selectedIndex] || null;
            var first = s.options[0] || null;
            var entry = {
                label: label, kind: /^Audio for /.test(label) ? 'audio' : 'subtitles', row: label.replace(/^(Audio|Subtitles) for /, ''),
                value: s.value, index: s.selectedIndex, text: opt ? opt.textContent : null,
                defaultLabel: first && first.value === '' ? first.textContent : null,
                hasVideo: Array.prototype.some.call(s.options, function (o) { return o.value === 'video'; }),
                count: s.options.length, disabled: s.disabled
            };
            if (fullLabel === '*' || label === fullLabel) {
                entry.options = Array.prototype.map.call(s.options, function (o) {
                    var g = o.parentNode && o.parentNode.tagName === 'OPTGROUP' ? o.parentNode : null;
                    return { v: o.value, l: o.textContent, g: g ? g.label : null, d: !!(o.disabled || (g && g.disabled)) };
                });
            }
            out.selects.push(entry);
        });
    }
    if (dirtyButtonScope) {
        var host = document.querySelector(dirtyButtonScope);
        var button = host && Array.prototype.find.call(host.querySelectorAll('button'), function (b) { return b.textContent.trim() === 'Save'; });
        out.saveBar = button ? { visible: !button.disabled, saveEnabled: !button.disabled, text: 'Save button' } : null;
        return out;
    }
    var bars = Array.prototype.slice.call(document.querySelectorAll(saveBarSelector || '.bf-savebar'));
    var bar = bars.find(function (b) { return !b.closest('.hide') || b.classList.contains('hide'); }) || bars[0] || null;
    if (bar) {
        var r = bar.getBoundingClientRect();
        var cs = getComputedStyle(bar);
        var buttons = Array.prototype.slice.call(bar.querySelectorAll('button'));
        var save = buttons.find(function (b) { return b.textContent.trim() === 'Save'; });
        out.saveBar = {
            visible: !bar.hidden && !bar.classList.contains('hide') && cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0,
            saveEnabled: !!save && !save.disabled,
            text: bar.textContent.replace(/\s+/g, ' ').trim()
        };
    }
    return out;
}

/** Records every change event on a dropdown (capturing, so a re-rendered select is still seen). */
function pgInstallChangeLog() {
    if (!window.__bfChanges) {
        window.__bfChanges = [];
        document.addEventListener('change', function (e) {
            var s = e.target;
            if (s && s.tagName === 'SELECT') {
                window.__bfChanges.push({ label: s.getAttribute('aria-label'), value: s.value, trusted: e.isTrusted });
            }
        }, true);
    }
    return true;
}

/** The dropdown with this aria-label under scope, the visible one first. */
function pgFindSelect(scope, label) {
    var root = scope ? document.querySelector(scope) : document;
    var all = root ? Array.prototype.filter.call(root.querySelectorAll('select[aria-label]'), function (x) {
        return x.getAttribute('aria-label') === label && !x.closest('.hide') && !x.closest('[hidden]');
    }) : [];
    return all.find(function (x) { var r = x.getBoundingClientRect(); return r.width > 0 && r.height > 0; }) || all[0] || null;
}

/** Focuses the dropdown with this aria-label (what Tab would land on). */
function pgFocusSelect(scope, label) {
    var root = scope ? document.querySelector(scope) : document;
    var all = root ? Array.prototype.filter.call(root.querySelectorAll('select[aria-label]'), function (x) {
        return x.getAttribute('aria-label') === label && !x.closest('.hide') && !x.closest('[hidden]');
    }) : [];
    var s = all.find(function (x) { var r = x.getBoundingClientRect(); return r.width > 0 && r.height > 0; }) || all[0] || null;
    if (!s) {
        return false;
    }
    s.scrollIntoView({ block: 'center' });
    s.focus();
    return document.activeElement === s;
}

/** Sets a dropdown's value and sends the browser's own event sequence (input, then change, both bubbling). */
function pgPick(scope, label, value) {
    var root = scope ? document.querySelector(scope) : document;
    var all = root ? Array.prototype.filter.call(root.querySelectorAll('select[aria-label]'), function (x) {
        return x.getAttribute('aria-label') === label && !x.closest('.hide') && !x.closest('[hidden]');
    }) : [];
    var s = all.find(function (x) { var r = x.getBoundingClientRect(); return r.width > 0 && r.height > 0; }) || all[0] || null;
    if (!s) {
        return { ok: false, reason: 'no dropdown labelled "' + label + '"' };
    }
    var has = Array.prototype.some.call(s.options, function (o) { return o.value === value && !o.disabled; });
    if (!has) {
        return { ok: false, reason: 'option ' + JSON.stringify(value) + ' not offered (has ' + Array.prototype.slice.call(s.options, 0, 12).map(function (o) { return o.value; }).join(', ') + (s.options.length > 12 ? ', …' : '') + ')' };
    }
    s.scrollIntoView({ block: 'center' });
    s.focus();
    if (s.value === value) {
        return { ok: true, unchanged: true };
    }
    s.value = value;
    s.dispatchEvent(new Event('input', { bubbles: true }));
    s.dispatchEvent(new Event('change', { bubbles: true }));
    return { ok: true };
}

/** The first visible button (or link) under scope whose text is exactly `text`. */
function pgButton(scope, text) {
    var roots = scope ? Array.prototype.slice.call(document.querySelectorAll(scope)) : [document];
    for (var i = 0; i < roots.length; i++) {
        var hit = Array.prototype.find.call(roots[i].querySelectorAll('button, a, [role="button"]'), function (b) {
            var r = b.getBoundingClientRect();
            return b.textContent.replace(/\s+/g, ' ').trim() === text && r.width > 0 && r.height > 0 && !b.closest('.hide');
        });
        if (hit) {
            return hit;
        }
    }
    return null;
}

function pgDialogButton(text) {
    var boxes = Array.prototype.slice.call(document.querySelectorAll('[role="dialog"], [role="alertdialog"]'));
    for (var i = boxes.length - 1; i >= 0; i--) {
        var b = Array.prototype.find.call(boxes[i].querySelectorAll('button'), function (x) {
            var r = x.getBoundingClientRect();
            return x.textContent.replace(/\s+/g, ' ').trim() === text && r.width > 0;
        });
        if (b) {
            return b;
        }
    }
    return null;
}

/** The height of the page's content (the admin page's .bf-dash under scope; the page element itself is a fixed-height box), else the document's. */
function pgHeight(scope) {
    var el = scope ? document.querySelector(scope) : null;
    if (el) {
        var inner = el.querySelector('.bf-dash') || el;
        return Math.round(Math.max(inner.scrollHeight, inner.getBoundingClientRect().height));
    }
    return Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0);
}

// ───────────────────────────── The test context ─────────────────────────────

class Context {
    constructor(chrome, page, opts) {
        this.chrome = chrome;
        this.page = page;
        this.pages = [page];
        this.opts = opts;
        this.base = opts.base;
        this.settle = opts.settle;
        this.api = null;
        this.ids = null;
        this.test = null;

        this.deferred = [];
        this.notes = [];
        this.expectedMockFailures = [];
    }

    tag() {
        return '[' + (this.test ? this.test.n : '-') + '] ';
    }

    /** Records one check and prints its PASS/FAIL line; a failure also gets a screenshot. */
    async check(ok, what, page) {
        const line = (ok ? 'PASS ' : 'FAIL ') + this.tag() + what;
        console.log(line);
        results.push({ ok: !!ok, test: this.test ? this.test.n : 0, what: what });
        if (!ok) {
            await this.shot(page || this.page, what);
        }
        return !!ok;
    }

    async shot(page, what) {
        if (shotCount >= MAX_SHOTS || !page) {
            return null;
        }
        shotCount += 1;
        const file = path.join(this.opts.shots, 'e2e-' + String(shotCount).padStart(3, '0') + '-t' + (this.test ? this.test.n : 0) + '-' + slug(what) + '.png');
        const saved = await page.screenshot(file);
        if (saved) {
            console.log('     screenshot: ' + saved);
        }
        return saved;
    }

    note(text) {
        console.log('NOTE ' + this.tag() + text);
    }

    verbose(text) {
        if (this.opts.verbose) {
            console.log('     ' + text);
        }
    }

    /** Registers an undo step; they run last-in first-out when the test ends, failed or not. */
    defer(what, fn) {
        this.deferred.push({ what: what, fn: fn });
    }

    async newPage() {
        const page = await this.chrome.newPage();
        await installHelpers(page);
        this.pages.push(page);
        return page;
    }

    url(p) {
        return this.base + p;
    }
}

/** Installs pageHelpers in every document the tab loads from now on, and in the current one. */
async function installHelpers(page) {
    await page.send('Page.addScriptToEvaluateOnNewDocument', { source: PAGE_HELPERS });
    await page.evaluate(PAGE_HELPERS).catch(() => {});
}

// ───────────────────────────── Shared steps ─────────────────────────────

async function readCredentials(page) {
    const raw = await page.evaluate(function () { try { return localStorage.getItem('jellyfin_credentials'); } catch (e) { return null; } }).catch(() => null);
    try {
        const servers = (JSON.parse(raw || '{}').Servers || []).filter((s) => s && s.AccessToken);
        servers.sort((a, b) => (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0));
        return servers[0] || null;
    } catch (e) {
        return null;
    }
}

async function waitWebReady(t, page) {
    await page.waitFor(function () {
        return !!(window.ApiClient && typeof ApiClient.getCurrentUserId === 'function' && ApiClient.getCurrentUserId() && document.querySelector('.skinHeader') && window.Babelfin);
    }, { timeout: 30000, what: 'jellyfin-web signed in and ready' });
}

/**
 * Signs in through jellyfin-web's own login page (clicking "Connect Anyway" when the server-mismatch page shows, and "Manual
 * Login" when only user cards show). Returns { connectAnyway: bool }.
 */
async function signInThroughLoginPage(t) {
    const page = t.page;
    const info = { connectAnyway: false, steps: [] };
    await page.navigate(t.url('/web/'));
    const deadline = Date.now() + 60000;
    let typed = false;
    for (;;) {
        const state = await page.evaluate(function () {
            function vis(el) {
                if (!el) { return false; }
                var r = el.getBoundingClientRect();
                return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden' && !el.closest('.hide');
            }
            var anyway = Array.prototype.find.call(document.querySelectorAll('button, a'), function (b) { return /connect anyway/i.test(b.textContent) && vis(b); });
            if (anyway) { return 'connectAnyway'; }
            var token = null;
            try { token = (JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}').Servers || []).some(function (s) { return s.AccessToken; }); } catch (e) { token = null; }
            if (token && /^#\/home/.test(location.hash) && document.querySelector('.skinHeader')) { return 'home'; }
            if (vis(document.querySelector('#txtManualName'))) { return 'form'; }
            if (vis(document.querySelector('.btnManual'))) { return 'manualButton'; }
            return 'waiting ' + location.hash;
        }).catch((e) => 'waiting (' + e.message + ')');
        info.steps.push(state);
        if (state === 'home') {
            return info;
        }
        if (Date.now() > deadline) {
            throw new Error('the login page did not reach the home page within 60 s (last state: ' + state + ')');
        }
        if (state === 'connectAnyway') {
            info.connectAnyway = true;
            await page.click(function () {
                return Array.prototype.find.call(document.querySelectorAll('button, a'), function (b) { return /connect anyway/i.test(b.textContent); }) || null;
            }, { what: 'the "Connect Anyway" button' });
            await sleep(1000);
            continue;
        }
        if (state === 'manualButton') {
            // Only a laid-out button: during the login page's transitions a hidden copy can be the first match.
            try {
                await page.click(function () {
                    return Array.prototype.find.call(document.querySelectorAll('.btnManual'), function (b) { var r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0; }) || null;
                }, { what: 'the "Manual Login" button', timeout: 5000 });
            } catch (e) {
                info.steps.push('the "Manual Login" button moved (' + e.message + ')');
            }
            await sleep(500);
            continue;
        }
        if (state === 'form' && !typed) {
            typed = true;
            await page.click('#txtManualName');
            await page.evaluate(function () { document.querySelector('#txtManualName').value = ''; document.querySelector('#txtManualPassword').value = ''; });
            await page.type(t.opts.user);
            await page.click('#txtManualPassword');
            await page.type(t.opts.password);
            await page.click(function () {
                var form = document.querySelector('#txtManualName').closest('form');
                return form && (form.querySelector('button[type="submit"]') || form.querySelector('.button-submit'));
            }, { what: 'the Sign In button' });
            await sleep(500);
            continue;
        }
        await sleep(250);
    }
}

/** Makes sure the browser is signed in (through the login page when it isn't) and the API helper has the stored token. */
async function ensureSignedIn(t) {
    let creds = await readCredentials(t.page);
    if (creds && t.api && creds.AccessToken === t.api.token) {
        const me = await t.api.get('/Users/Me');
        if (me.status === 200) {
            return;
        }
    }
    const valid = !!creds && (await new Api(t.base, creds.AccessToken).get('/Users/Me')).status === 200;
    if (!valid) {
        if (!/\/web\//.test(t.page.url)) {
            await t.page.navigate(t.url('/web/'));
        }
        await signInThroughLoginPage(t);
        creds = await readCredentials(t.page);
    }
    if (!creds) {
        throw new Error('no access token in jellyfin_credentials after signing in');
    }
    t.api = new Api(t.base, creds.AccessToken, creds.UserId);
    if (!t.ids) {
        t.ids = await findIds(t);
    }
}

/** The ids the tests use: libraries, the Astercoil Test episode and series, the Berlin Test movie, the second user. */
async function findIds(t) {
    const api = t.api;
    const uid = api.userId;
    const ids = { userId: uid, libraries: {} };
    const views = await api.get('/UserViews', { userId: uid });
    ((views.body && views.body.Items) || []).forEach((v) => { ids.libraries[v.Name] = v.Id; });
    const series = await api.get('/Items', { userId: uid, Recursive: 'true', IncludeItemTypes: 'Series' });
    const dualAudio = ((series.body && series.body.Items) || []).find((s) => s.Name === 'Astercoil Test');
    if (dualAudio) {
        ids.seriesId = dualAudio.Id;
        const eps = await api.get('/Shows/' + dualAudio.Id + '/Episodes', { userId: uid });
        const list = (eps.body && eps.body.Items) || [];
        const ep = list.find((e) => e.ParentIndexNumber === 1 && e.IndexNumber === 1) || list[0];
        ids.episodeId = ep ? ep.Id : null;
        ids.episodeCount = list.length;
        // Item-page tests: the season of that episode, and the next episode (a "Next Up" card once the first is played).
        ids.seasonId = ep ? ep.SeasonId : null;
        const next = list.find((e) => e.ParentIndexNumber === 1 && e.IndexNumber === 2);
        ids.episode2Id = next ? next.Id : null;
    }
    ids.series = {};
    ((series.body && series.body.Items) || []).forEach((s) => { ids.series[s.Name] = s.Id; });
    const movies = await api.get('/Items', { userId: uid, Recursive: 'true', IncludeItemTypes: 'Movie' });
    const berlin = ((movies.body && movies.body.Items) || []).find((m) => /^Berlin Test/.test(m.Name));
    ids.movieId = berlin ? berlin.Id : null;
    const harbour = ((movies.body && movies.body.Items) || []).find((m) => /^Harbour Test/.test(m.Name));
    ids.harbourId = harbour ? harbour.Id : null;
    const users = await api.get('/Users');
    const viewer = ((users.body) || []).find((u) => u.Name === t.opts.viewer);
    ids.viewerId = viewer ? viewer.Id : null;
    return ids;
}

/** Sets jellyfin-web's layout ('' = the default modern one) and reloads so it takes effect. */
async function setLayout(t, layout) {
    const page = t.page;
    if (!/\/web\//.test(page.url)) {
        await page.navigate(t.url('/web/#/home'));
    }
    await page.evaluate(function (l) { if (l) { localStorage.setItem('layout', l); } else { localStorage.removeItem('layout'); } }, layout);
    await page.navigate(t.url('/web/#/home'));
    await page.reload();
    await waitWebReady(t, page);
}

async function currentLayout(page) {
    return page.evaluate(function () { try { return localStorage.getItem('layout') || ''; } catch (e) { return ''; } });
}

/** The person page has drawn its table: any visible "Audio for …" dropdown. */
function pgTableReady(scope) {
    var root = scope ? document.querySelector(scope) : document;
    return !!root && Array.prototype.some.call(root.querySelectorAll('select[aria-label]'), function (s) {
        var r = s.getBoundingClientRect();
        return /^Audio for /.test(s.getAttribute('aria-label')) && r.width > 0 && r.height > 0 && !s.closest('.hide') && !s.closest('[hidden]');
    });
}

/** Opens the person page and waits for its table. */
async function openPersonPage(t, page) {
    await page.navigate(t.url('/Babelfin/app'));
    await page.waitFor(pgTableReady, { timeout: 30000, what: 'the person page table' }, null);
    await page.evaluate(pgInstallChangeLog);
}

async function reloadPersonPage(t, page) {
    await page.reload();
    await page.waitFor(pgTableReady, { timeout: 30000, what: 'the person page table after a reload' }, null);
    await page.evaluate(pgInstallChangeLog);
}

async function snap(page, scope, fullLabel, saveBar, dirtyButtonScope) {
    return page.evaluate(pgSnap, scope || null, fullLabel || null, saveBar || '.bf-savebar', dirtyButtonScope || null);
}

function byLabel(s, label) {
    return s.selects.find((x) => x.label === label) || null;
}

/** Whether two row lists hold the same rows (in any order). */
function sameRows(a, b) {
    return a.slice().sort().join('|') === b.slice().sort().join('|');
}

/** The table rows shown (by their dropdowns), in page order. */
function rowsOf(s) {
    return uniq(s.selects.map((x) => x.row));
}

/** Picks values (direct: set value + input + change) one after another; returns the failures. */
async function pickAll(page, scope, list) {
    const failed = [];
    for (const p of list) {
        const r = await page.evaluate(pgPick, scope || null, p.label, p.value);
        if (!r.ok) {
            failed.push(p.label + ' = ' + JSON.stringify(p.value) + ': ' + r.reason);
        }
        await sleep(150);
    }
    return failed;
}

/** Picks for several rows: { row: [audio, subtitles] } (undefined leaves that dropdown alone). */
function picksFor(map) {
    const list = [];
    Object.keys(map).forEach((row) => {
        if (map[row][0] !== undefined) {
            list.push({ label: SPEC.audioFor(row), value: map[row][0] });
        }
        if (map[row][1] !== undefined) {
            list.push({ label: SPEC.subtitlesFor(row), value: map[row][1] });
        }
    });
    return list;
}

/** Which picks a snapshot doesn't show. */
function notShown(s, list) {
    return list.filter((p) => { const x = byLabel(s, p.label); return !x || x.value !== p.value; })
        .map((p) => p.label + ' shows ' + JSON.stringify((byLabel(s, p.label) || {}).value) + ' (want ' + JSON.stringify(p.value) + ')');
}

/** Clicks the person page's Save and waits until the save finished (the bar hides). */
async function savePersonPage(t, page) {
    await page.click(pgButton, { args: ['.bf-savebar', 'Save'], what: 'Save in the save bar' });
    await page.waitFor(function () {
        var bar = document.querySelector('.bf-savebar');
        return bar && (bar.hidden || bar.classList.contains('hide') || getComputedStyle(bar).display === 'none') && !document.querySelector('.bfui-has-error');
    }, { timeout: 15000, what: 'the save to finish (save bar hidden)' });
}

async function getMe(t) {
    const r = await t.api.get('/Babelfin/Me/Settings');
    if (r.status !== 200) {
        throw new Error('GET Babelfin/Me/Settings: ' + r.status);
    }
    return r.body;
}

async function putMe(t, doc, what) {
    const r = await t.api.put('/Babelfin/Me/Settings', doc);
    if (r.status !== 200) {
        throw new Error('PUT Babelfin/Me/Settings' + (what ? ' (' + what + ')' : '') + ': ' + r.status + ' ' + JSON.stringify(r.body).slice(0, 300));
    }
    return r.body;
}

// A complete GET snapshot omits nullable values. Legacy PUT preserves omitted fields,
// so explicitly restore the editable nullable mode when it was absent in the snapshot.
function completePersonSnapshot(snapshot) {
    const doc = clone(snapshot);
    if (!Object.prototype.hasOwnProperty.call(doc, 'PerLibrary')) doc.PerLibrary = null;
    return doc;
}

async function restorePersonSnapshot(t, api, path, snapshot, label) {
    const result = await api.put(path, completePersonSnapshot(snapshot));
    const read = result.status === 200 ? await api.get(path) : { status: 0 };
    const actual = read.body && (read.body.Settings || read.body);
    const diff = actual ? changedPaths(snapshot, actual).filter(p => !/(^|\.)(UpdatedAt|LastChangedAt|LastChangedBy|LastChangedByName)$/.test(p)) : ['unavailable'];
    await t.check(result.status === 200 && read.status === 200 && diff.length === 0,
        label + ' (' + result.status + (diff.length ? '; still different: ' + diff.slice(0, 6).join(', ') : '') + ')', null);
}

async function getAdmin(t) {
    const r = await t.api.get('/Babelfin/Admin/Settings');
    if (r.status !== 200) {
        throw new Error('GET Babelfin/Admin/Settings: ' + r.status);
    }
    return r.body;
}

async function putAdmin(t, doc, what) {
    const r = await t.api.put('/Babelfin/Admin/Settings', doc);
    if (r.status !== 200) {
        throw new Error('PUT Babelfin/Admin/Settings' + (what ? ' (' + what + ')' : '') + ': ' + r.status + ' ' + JSON.stringify(r.body).slice(0, 300));
    }
    return r.body;
}

/** The document without any table choice: the five table fields removed from Everyday and every library layer. */
function withoutTable(doc) {
    const d = clone(doc);
    d.Everyday = d.Everyday || {};
    SPEC.tableFields.forEach((f) => { delete d.Everyday[f]; });
    d.Libraries = (d.Libraries || []).map((l) => {
        const s = Object.assign({}, l.Settings || {});
        SPEC.tableFields.forEach((f) => { delete s[f]; });
        return Object.assign({}, l, { Settings: s });
    }).filter((l) => setKeys(l.Settings).length > 0 || Object.keys(l).some((k) => ['LibraryId', 'Name', 'Settings'].indexOf(k) < 0 && l[k] !== null && l[k] !== undefined));
    return d;
}

/**
 * A copy of a document with a table set up: perLibrary (true, false or null = automatic; mode stored as `modeField`), everyday
 * [audio, subtitles] and libs { name: [audio, subtitles] } in the table values. Everything else stays as in `doc`.
 */
function withTable(t, doc, o) {
    const d = withoutTable(doc);
    // Include null explicitly: omitting the mode from a legacy PUT preserves its old value.
    d[o.modeField || 'PerLibrary'] = o.perLibrary === undefined ? null : o.perLibrary;
    if (o.everyday) {
        Object.assign(d.Everyday, storedFor(o.everyday[0], o.everyday[1]));
    }
    Object.keys(o.libs || {}).forEach((name) => {
        const id = t.ids.libraries[name];
        if (!id) {
            throw new Error('no library called ' + name);
        }
        const stored = storedFor(o.libs[name][0], o.libs[name][1]);
        const at = d.Libraries.find((l) => compactId(l.LibraryId) === compactId(id));
        if (at) {
            at.Settings = Object.assign({}, at.Settings || {}, stored);
        } else {
            d.Libraries.push({ LibraryId: compactId(id), Name: name, Settings: stored });
        }
    });
    return d;
}

/** The admin settings as they are out of the box for the table: no table choices, the original-language rule off, mode automatic. */
function adminNeutral(doc) {
    const d = withoutTable(doc);
    d.PerLibraryDefaults = null;
    if (d.Starter) {
        d.Starter.Enabled = false;
    }
    return d;
}

function tableOf(layer) {
    const out = {};
    SPEC.tableFields.forEach((f) => { out[f] = layer && layer[f] !== undefined ? layer[f] : null; });
    return out;
}

function libraryLayer(doc, libraryId) {
    const l = (doc.Libraries || []).find((x) => compactId(x.LibraryId) === compactId(libraryId));
    return l ? (l.Settings || {}) : null;
}

/** Whether a layer stores anything for a table column ('audio': AudioChain/ForeignTitles; 'subtitles': the other three). */
function storesKind(layer, kind) {
    const fields = kind === 'audio' ? ['AudioChain', 'ForeignTitles'] : ['WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'SubtitleLanguages'];
    return !!layer && fields.some((f) => layer[f] !== null && layer[f] !== undefined);
}

/** The layer a table row edits in a document: Everyday for All libraries, else that library's Settings (null when absent). */
function rowLayer(doc, row, libraryIdOf) {
    if (!doc) {
        return null;
    }
    return row === SPEC.allLibraries ? (doc.Everyday || null) : libraryLayer(doc, libraryIdOf(row));
}

/** The fields a layer sets (IsEmpty is a computed, read-only property of the JSON, not a stored field). */
function setKeys(layer) {
    return Object.keys(layer || {}).filter((k) => k !== 'IsEmpty' && layer[k] !== null && layer[k] !== undefined).sort();
}

/** The table fields of every library layer, keyed by library id (for "the other mode's values are kept"). */
function libraryTables(doc) {
    const out = {};
    (doc.Libraries || []).forEach((l) => {
        const tbl = tableOf(l.Settings || {});
        if (Object.keys(tbl).some((k) => tbl[k] !== null)) {
            out[compactId(l.LibraryId)] = tbl;
        }
    });
    return out;
}

/** The changed paths that aren't allowed by any of the patterns (server audit stamps are always allowed). */
function strayPaths(before, after, allowed) {
    return changedPaths(before, after).filter((p) => !/^(UpdatedAt|LastSaved|LastChangedAt|LastChangedBy)(\.|$)/.test(p) && !allowed.some((a) => (typeof a === 'string' ? p === a || p.indexOf(a + '.') === 0 || p.indexOf(a + '[') === 0 : a.test(p))));
}

const TABLE_FIELD_RE = '(AudioChain|ForeignTitles|WhenAudioUnderstood|WhenAudioNotUnderstood|SubtitleLanguages)';

/** Allowed changed paths for edits of the Everyday table fields. */
const EVERYDAY_TABLE = new RegExp('^Everyday\\.' + TABLE_FIELD_RE + '\\b');

/** Allowed changed paths for edits of one library's table fields (and the entry's own id and name when it is new). */
function libraryTablePaths(libraryId) {
    return new RegExp('^Libraries\\[' + compactId(libraryId) + '\\]\\.(LibraryId|Name|Settings\\.' + TABLE_FIELD_RE + ')\\b');
}

// ───────────────────────────── The either/or control ─────────────────────────────

async function modeInfo(page, scope) {
    return page.evaluate(pgModeInfo, scope || null, SPEC.modeGroup, SPEC.modeSame);
}

/** Clicks one of the two modes with the mouse. */
async function clickMode(page, scope, which) {
    await page.click(pgModeClickEl, { args: [scope || null, SPEC.modeGroup, SPEC.modeSame, which], what: 'the "' + which + '" radio' });
}

/** Waits until `which` is the checked mode and the table shows `rows`; returns { ok, info, rows }. */
async function waitMode(page, scope, which, rows, timeout) {
    const deadline = Date.now() + (timeout || 4000);
    let info = null;
    let shown = [];
    for (;;) {
        info = await modeInfo(page, scope);
        shown = rowsOf(await snap(page, scope, null));
        if (info.found && info.checked === which && (!rows || sameRows(shown, rows))) {
            return { ok: true, info: info, rows: shown };
        }
        if (Date.now() > deadline) {
            return { ok: false, info: info, rows: shown };
        }
        await sleep(150);
    }
}

function modeText(r) {
    return 'checked: ' + (r.info && r.info.found ? (r.info.checked || 'none') : 'no radiogroup') + '; rows: ' + (r.rows.length ? r.rows.join(', ') : 'none');
}

/** Makes sure the page shows `which` (clicking it when another mode shows); returns whether it got there. */
async function ensureMode(page, scope, which, rows) {
    const now = await modeInfo(page, scope);
    if (!now.found) {
        return false;
    }
    if (now.checked !== which) {
        await clickMode(page, scope, which);
    }
    return (await waitMode(page, scope, which, rows)).ok;
}

// ───────────────────────────── 1. Sign in ─────────────────────────────

async function testSignIn(t) {
    const before = await readCredentials(t.page);
    if (before) {
        // A signed-in browser from an earlier test: sign out through the API's token so the login page shows.
        await t.page.evaluate(function () { localStorage.removeItem('jellyfin_credentials'); });
    }
    const info = await signInThroughLoginPage(t);
    if (info.connectAnyway) {
        t.note('the server-mismatch page showed; "Connect Anyway" was clicked');
    }
    await t.check(true, 'the real login page signed ' + t.opts.user + ' in and showed the home page (' + info.steps.filter((s, i, a) => a.indexOf(s) === i).join(' → ') + ')');
    const creds = await readCredentials(t.page);
    await t.check(!!(creds && creds.AccessToken), 'jellyfin-web stored an access token in localStorage jellyfin_credentials');
    t.api = new Api(t.base, creds.AccessToken, creds.UserId);
    const me = await t.api.get('/Users/Me');
    await t.check(me.status === 200 && me.body && me.body.Name === t.opts.user, 'the stored token works for API calls (GET /Users/Me: ' + me.status + ', ' + (me.body && me.body.Name) + ')');
    await waitWebReady(t, t.page);
    const client = await t.page.evaluate('window.Babelfin ? window.Babelfin.version : null');
    await t.check(!!client, "Babelfin's client script is loaded in jellyfin-web (window.Babelfin.version " + client + ')');
    t.ids = await findIds(t);
    const missing = ['seriesId', 'episodeId', 'movieId', 'viewerId'].filter((k) => !t.ids[k]).concat(['Anime', 'Movies', 'TV Shows'].filter((n) => !t.ids.libraries[n]));
    await t.check(missing.length === 0, "the rig's test media and second user are there (Astercoil Test, Berlin Test, Anime, Movies, TV Shows, " + t.opts.viewer + ')' + (missing.length ? ': missing ' + missing.join(', ') : ''));
}

// ───────────────────────────── 2. Modern layout: avatar menu ─────────────────────────────

async function testModernMenu(t) {
    const page = t.page;
    const layout = await currentLayout(page);
    t.defer('layout back to "' + layout + '"', () => setLayout(t, layout));
    await setLayout(t, '');
    await page.click('button[aria-controls="app-user-menu"]');
    await page.waitForSelector('#app-user-menu .bfMenuItem');
    const menu = await page.evaluate(function () {
        var menu = document.getElementById('app-user-menu');
        var items = Array.prototype.slice.call(menu.querySelectorAll('[role="menuitem"], a[href], hr'));
        var settings = items.find(function (n) { return /mypreferencesmenu/i.test(n.getAttribute('href') || ''); });
        var bf = menu.querySelector('.bfMenuItem');
        function classes(el) {
            var drop = ['bfMenuItem', 'Mui-focusVisible', 'Mui-selected', 'active'];
            return Array.from(new Set(Array.prototype.filter.call(el.classList, function (c) { return drop.indexOf(c) < 0; }))).sort().join(' ');
        }
        function shape(el) {
            return el.tagName + '(' + classes(el) + ')[' + Array.prototype.map.call(el.children, shape).join(',') + ']';
        }
        var neighbours = items.filter(function (n) { return n.getAttribute('role') === 'menuitem' && n !== bf; });
        return {
            count: menu.querySelectorAll('.bfMenuItem').length,
            order: items.map(function (n) { return n.tagName === 'HR' ? '—' : n.textContent.trim(); }),
            afterSettings: !!settings && settings.nextElementSibling === bf,
            text: bf ? bf.textContent.trim() : null,
            href: bf ? bf.getAttribute('href') : null,
            role: bf ? bf.getAttribute('role') : null,
            sameClasses: !!settings && classes(bf) === classes(settings),
            sameShape: !!settings && shape(bf).replace(/MuiSvgIcon[^)]*/, '') === shape(settings).replace(/MuiSvgIcon[^)]*/, ''),
            neighbourClasses: neighbours.every(function (n) { return n.tagName !== 'A' || classes(n) === classes(bf); }),
            bfShape: shape(bf),
            settingsShape: settings ? shape(settings) : null,
            ids: bf ? bf.querySelectorAll('[id]').length + (bf.id ? 1 : 0) : 0
        };
    });
    await t.check(menu.count === 1 && menu.text === SPEC.menuText, 'the avatar menu has one "' + SPEC.menuText + '" item (' + menu.count + ', "' + menu.text + '")');
    await t.check(menu.afterSettings, '"' + SPEC.menuText + '" comes directly after "Settings" (' + menu.order.join(' | ') + ')');
    await t.check(menu.sameClasses && menu.sameShape && menu.role === 'menuitem',
        'it has the same markup classes as "Settings": ' + menu.bfShape + (menu.sameShape ? '' : ' vs ' + menu.settingsShape));
    await t.check(menu.neighbourClasses, 'its classes match the other link items of the menu');
    await t.check(menu.ids === 0, 'the copied item carries no duplicate element ids (' + menu.ids + ')');
    await page.click('#app-user-menu .bfMenuItem');
    const landed = await waitSettingsRoute(page, 20000);
    await t.check(landed.ok, 'clicking it opens ' + SPEC.route + ' with Babelfin Settings under Jellyfin\'s header (' + routeText(landed.r) + ')', page);
    await routeChecks(t, page, 'modern layout, from the avatar menu', landed.r, { menuClosed: true });
    await page.navigate(t.url('/web/#/mypreferencesmenu'));
    await page.waitForSelector('#myPreferencesMenuPage a.lnkSubtitlePreferences', { timeout: 20000 });
    await sleep(1500);
    const row = await page.evaluate('document.querySelectorAll(".bfLanguagesRow").length');
    await t.check(row === 0, '#/mypreferencesmenu has no Babelfin row in the modern layout (' + row + ')');
}

// ───────────────────────────── 3. Legacy layouts ─────────────────────────────

/** Observe the real Settings frame without moving focus or dispatching input. */
function pgTvSettingsState() {
    const frame = document.querySelector('.bfSettingsFrame');
    const doc = frame && frame.contentDocument;
    if (!doc) return null;
    const nodes = Array.from(doc.querySelectorAll('#bf-app a[href], #bf-app button, #bf-app input, #bf-app select, #bf-app textarea, #bf-app summary, #bf-app [tabindex]'));
    const eligible = nodes.filter(node => {
        if (node.disabled || node.tabIndex < 0 || node.type === 'hidden') return false;
        for (let at = node; at && at !== doc; at = at.parentElement) {
            const style = frame.contentWindow.getComputedStyle(at);
            if (at.hidden || at.classList.contains('hide') || style.display === 'none' || style.visibility === 'hidden') return false;
            if (at.tagName === 'DETAILS' && !at.open && node !== at.querySelector('summary')) return false;
        }
        return true;
    });
    const active = doc.activeElement;
    const select = active && active.tagName === 'SELECT' ? active : null;
    const options = select ? Array.from(select.options).filter(option => !option.disabled) : [];
    return {
        frameFocused: document.activeElement === frame,
        tv: doc.documentElement.classList.contains('bf-tv'),
        active: nodes.indexOf(active), eligible: eligible.map(node => nodes.indexOf(node)),
        label: active && active.getAttribute('aria-label'),
        audio: !!select && /^Audio for /.test(select.getAttribute('aria-label') || ''),
        value: select && select.value,
        canRight: !!select && options.findIndex(option => option.value === select.value) < options.length - 1,
        disclosure: !!active && active.classList.contains('bfui-disclosure-button') && active.getAttribute('aria-expanded') === 'false',
        save: !!active && !!active.closest('.bf-savebar') && active.classList.contains('bfui-btn-primary'),
        saveInert: !!active && active.getAttribute('aria-disabled') === 'true',
        saveOpacity: active && frame.contentWindow.getComputedStyle(active).opacity,
        saveStatus: (doc.querySelector('.bf-savebar-status') || {}).textContent
    };
}

/** Real remote/keyboard events enter the same-origin frame, edit, visit controls, save and return to the opener. */
async function tvSettingsKeyboardChecks(t, page) {
    const before = await getMe(t);
    t.defer('restore settings after TV keyboard checks', () => restorePersonSnapshot(t, t.api, '/Babelfin/Me/Settings', before.Settings, 'TV keyboard settings restored'));
    let state = await page.evaluate(pgTvSettingsState);
    await t.check(!!state && state.frameFocused && state.active >= 0 && state.tv, 'tv: opening Settings focuses the iframe and its first control', page);
    if (!state || !state.frameFocused || state.active < 0) return;
    for (let i = 0; i < 300 && !state.audio; i++) {
        await page.press('ArrowDown'); state = await page.evaluate(pgTvSettingsState);
    }
    await t.check(!!state && state.audio, 'tv: D-pad Down reaches the audio select inside Settings', page);
    if (!state || !state.audio) return;
    const original = state.value;
    await page.press(state.canRight ? 'ArrowRight' : 'ArrowLeft');
    state = await page.evaluate(pgTvSettingsState);
    await t.check(state.value !== original, 'tv: Left/Right changes the focused Settings select', page);
    // Open collapsible groups only by OK while traversing. No pointer or scripted focus is used.
    for (let i = 0; i < 600; i++) {
        if (state.disclosure) { await page.press('Enter'); await sleep(80); }
        const previous = (await page.evaluate(pgTvSettingsState)).active;
        await page.press('ArrowDown'); state = await page.evaluate(pgTvSettingsState);
        if (state.active === previous) break;
    }
    for (let i = 0; i < 600; i++) {
        const previous = state.active;
        await page.press('ArrowUp'); state = await page.evaluate(pgTvSettingsState);
        if (state.active === previous) break;
    }
    const visited = new Set([state.active]);
    for (let i = 0; i < 600; i++) {
        const previous = state.active;
        await page.press('ArrowDown'); state = await page.evaluate(pgTvSettingsState);
        visited.add(state.active);
        if (state.active === previous) break;
    }
    await t.check(state.eligible.every(index => visited.has(index)), 'tv: Up/Down reaches every visible enabled Settings control, including expanded groups and Save', page);
    for (let i = 0; i < 600 && !state.save; i++) {
        await page.press('ArrowUp'); state = await page.evaluate(pgTvSettingsState);
    }
    await t.check(state.save, 'tv: Save is reachable with the D-pad', page);
    if (!state.save) return;
    await page.press('Enter');
    const saved = await waitToast(page, (await strings(t)).person['app.saved'], true, 10000);
    state = await page.evaluate(pgTvSettingsState);
    await t.check(saved && !!state && await page.evaluate(pgSaveBarShown, true) && state.save && state.saveInert,
        'tv: Enter on Save persists Settings and retains focus on the clean Save control', page);
    await t.check(state.saveOpacity === '0.4' && state.saveStatus === (await strings(t)).person['app.saved'],
        'tv: the focused clean Save is dimmed and the status says Saved', page);
    const cleanSaveMark = page.requests.length;
    await page.press('Enter');
    await sleep(t.settle);
    await t.check(!page.requests.slice(cleanSaveMark).some(request => request.method === 'PUT' && /\/Babelfin\/Me\/Settings(?:\?|$)/.test(request.url)),
        'tv: Enter on a clean Save does not send another settings write', page);
    await page.press('ArrowUp');
    await t.check(!(await page.evaluate(pgSaveBarShown, true)), 'tv: moving focus out of the clean save bar hides it', page);
    const after = await getMe(t);
    await t.check(!same(before.Settings, after.Settings), 'tv: the keyboard change is present in persisted settings', page);
    for (const key of ['Escape', 'BrowserBack', 'GoBack', 'WebOSBack', 'TizenBack']) {
        await page.press(key);
        const closed = await waitNoPanel(page, 10000);
        await page.waitFor(function () { return !!document.activeElement && document.activeElement.classList.contains('bfLanguagesRow'); }, { timeout: 10000, what: 'focus returned to the Settings menu item' }).catch(() => {});
        const focused = await page.evaluate(function () { return !!document.activeElement && document.activeElement.classList.contains('bfLanguagesRow'); });
        await t.check(closed && focused, 'tv: ' + key + ' closes Settings and returns focus to its menu item', page);
        if (key === 'TizenBack' || !closed || !focused) break;
        await page.press('Enter'); await waitSettingsRoute(page, 20000);
    }
}

async function testLegacyLayouts(t) {
    const page = t.page;
    const layout = await currentLayout(page);
    t.defer('layout back to "' + layout + '"', () => setLayout(t, layout));
    for (const mode of ['desktop-legacy', 'tv']) {
        await setLayout(t, mode);
        await page.navigate(t.url('/web/#/mypreferencesmenu'));
        await page.waitForSelector('#myPreferencesMenuPage a.lnkSubtitlePreferences', { timeout: 20000 });
        let found = true;
        try {
            await page.waitForSelector('#myPreferencesMenuPage .bfLanguagesRow', { timeout: 10000 });
        } catch (e) {
            found = false;
        }
        const r = await page.evaluate(function () {
            var pageEl = Array.prototype.find.call(document.querySelectorAll('#myPreferencesMenuPage'), function (p) { return !p.classList.contains('hide'); });
            var rows = pageEl ? pageEl.querySelectorAll('.bfLanguagesRow') : [];
            var row = rows[0] || null;
            var subs = pageEl ? pageEl.querySelector('a.lnkSubtitlePreferences') : null;
            return {
                count: rows.length,
                afterSubtitles: !!row && !!subs && subs.nextElementSibling === row,
                href: row ? row.getAttribute('href') : null,
                text: row ? row.textContent.trim() : null,
                sameBase: !!row && !!subs && subs.className.replace('lnkSubtitlePreferences', '').replace(/\s+/g, ' ').trim() === row.className.replace('bfLanguagesRow', '').replace(/\s+/g, ' ').trim(),
                avatar: document.querySelectorAll('button[aria-controls="app-user-menu"]').length,
                menuItems: document.querySelectorAll('.bfMenuItem').length
            };
        });
        await t.check(found && r.count === 1 && r.text === SPEC.menuText, mode + ': #/mypreferencesmenu has one "' + SPEC.menuText + '" row (' + r.count + ', "' + r.text + '")');
        await t.check(r.afterSubtitles, mode + ': the row comes directly after "Subtitles"');
        await t.check(r.sameBase, mode + ': the row has the Subtitles row\'s classes');
        await t.check(r.href === SPEC.route, mode + ': the row links to ' + SPEC.route + ' (' + r.href + ')');
        await t.check(r.avatar === 0 && r.menuItems === 0, mode + ': no avatar menu and no avatar-menu item (' + r.avatar + ', ' + r.menuItems + ')');
        if (found) {
            if (mode === 'tv') {
                for (let i = 0; i < 80; i++) {
                    const subtitlesFocused = await page.evaluate(function () { return !!document.activeElement && document.activeElement.classList.contains('lnkSubtitlePreferences'); });
                    if (subtitlesFocused) break;
                    await page.press('Tab');
                }
                await page.press('ArrowDown');
                await sleep(350);
                const reached = await page.evaluate(function () { return !!document.activeElement && document.activeElement.classList.contains('bfLanguagesRow'); });
                await t.check(reached, 'tv: D-pad Down from Subtitles reaches Babelfin Settings', page);
                await page.press('Enter');
            } else {
                await page.click('#myPreferencesMenuPage:not(.hide) .bfLanguagesRow');
            }
            const landed = await waitSettingsRoute(page, 20000);
            await t.check(landed.ok, mode + ': clicking the row opens ' + SPEC.route + ' with Babelfin Settings under Jellyfin\'s header (' + routeText(landed.r) + ')', page);
            await routeChecks(t, page, mode + ', from the Settings row', landed.r, { legacyTitle: true });
            if (mode === 'tv' && landed.ok) { await tvSettingsKeyboardChecks(t, page); }
        }
    }
}

// ───────────────────────────── Table contract and keyboard sweeps ─────────────────────────────

/**
 * Table contract checks for one table in one mode (person page, People editor or admin Libraries). o: { where, rows (exactly the
 * rows that must show), languages, admin, mine(kind), help, storedLayer(row) }.
 */
async function contractChecks(t, page, scope, o) {
    const s = await snap(page, scope, '*');
    const labels = s.selects.map((x) => x.label);
    const wanted = [];
    o.rows.forEach((r) => { wanted.push(SPEC.audioFor(r), SPEC.subtitlesFor(r)); });
    const missing = wanted.filter((w) => labels.indexOf(w) < 0);
    const extra = labels.filter((l) => wanted.indexOf(l) < 0);
    await t.check(missing.length === 0 && extra.length === 0, o.where + ': exactly the dropdowns "Audio for <row>" and "Subtitles for <row>" for ' + o.rows.join(', ')
        + (missing.length ? '; missing: ' + missing.join(', ') : '') + (extra.length ? '; also shown: ' + extra.join(', ') : ''), page);
    const apiLangs = o.languages || [];
    const expectedCodes = new Set(apiLangs.filter((l) => l.Code === l.Base && /^[a-z]{2}$/i.test(l.Base)).map((l) => l.Code.toLowerCase())
        .concat(SPEC.extraCodes.map((c) => c.toLowerCase())).concat((o.storedCodes || []).map((c) => c.toLowerCase())));
    const collator = new Intl.Collator('en', { sensitivity: 'base' });
    const problems = [];
    if (!s.selects.some((x) => wanted.indexOf(x.label) >= 0)) {
        problems.push('no dropdown to check');
    }
    for (const sel of s.selects) {
        if (wanted.indexOf(sel.label) < 0) {
            continue;
        }
        const p = [];
        const opts = sel.options || [];
        const first = opts[0];
        if (!first || first.v !== '' || !(/^Default \(.+\)$/.test(first.l) || first.l === SPEC.videoDefault)) {
            p.push('first option is ' + (first ? JSON.stringify(first.v) + ' "' + first.l + '"' : 'missing') + ', not "" "Default (…)" / "Video default"');
        }
        if (o.admin && first && first.l !== SPEC.videoDefault) {
            p.push('Default reads "' + first.l + '" (on the admin page every default is "Video default")');
        }
        const video = opts.find((x) => x.v === 'video');
        const isVideoDefault = first && first.l === SPEC.videoDefault;
        if (!!video === !!isVideoDefault) {
            p.push(video ? '"video" offered although Default is already the video default' : '"video" missing although Default is "' + (first && first.l) + '"');
        }
        if (video && video.l !== SPEC.videoDefault) {
            p.push('"video" is labelled "' + video.l + '"');
        }
        const fixed = sel.kind === 'audio' ? ['', 'video', 'mine', 'original', 'custom'] : ['', 'video', 'off', 'smart', 'signs', 'custom'];
        const odd = opts.filter((x) => fixed.indexOf(x.v) < 0 && !/^lang:[A-Za-z0-9-]+$/.test(x.v)).map((x) => x.v);
        if (odd.length) {
            p.push('values outside the allowed choices: ' + Array.from(new Set(odd)).join(', '));
        }
        if (sel.kind === 'audio') {
            Object.entries(SPEC.audioChoices).forEach(([value,label]) => {
                const hits = opts.filter(x => x.v === value);
                if (hits.length !== 1 || apos(hits[0].l) !== label || hits[0].d) p.push(value + ': missing, doubled, disabled or incorrectly labelled');
            });
        }
        // "Custom" only for a stored value; a stored value never hides behind Default.
        const stored = o.storedLayer ? storesKind(o.storedLayer(sel.row), sel.kind) : false;
        if (opts.some((x) => x.v === 'custom') && !stored) {
            p.push('"custom" offered with nothing stored for this ' + sel.kind);
        }
        if (stored && sel.value === '') {
            p.push('shows Default although ' + canon(tableOf(o.storedLayer(sel.row))) + ' is stored');
        }
        if (sel.kind === 'subtitles') {
            Object.keys(SPEC.subtitleLabels).forEach((v) => {
                const hit = opts.find((x) => x.v === v);
                const label = o.admin && v === 'smart' ? SPEC.adminSmart : SPEC.subtitleLabels[v];
                if (!hit || apos(hit.l) !== label) {
                    p.push(v + (hit ? ' is labelled "' + hit.l + '"' : ' missing') + ' (want "' + label + '")');
                }
            });
        }
        const langOpts = opts.filter((x) => /^lang:/.test(x.v));
        const ungrouped = langOpts.filter((x) => x.g !== SPEC.mineGroup && x.g !== SPEC.allGroup);
        if (ungrouped.length) {
            p.push(ungrouped.length + ' languages outside the "My languages"/"All languages" groups (' + ungrouped.slice(0, 4).map((x) => x.v).join(', ') + ')');
        }
        const groups = [];
        opts.forEach((x) => { if (x.g && groups[groups.length - 1] !== x.g) { groups.push(x.g); } });
        const wantGroups = o.admin ? [SPEC.allGroup] : (o.mine(sel.kind).length ? [SPEC.mineGroup, SPEC.allGroup] : [SPEC.allGroup]);
        if (groups.join('|') !== wantGroups.join('|')) {
            p.push('groups are [' + groups.join(', ') + '], want [' + wantGroups.join(', ') + ']');
        }
        if (!o.admin) {
            const mine = langOpts.filter((x) => x.g === SPEC.mineGroup).map((x) => x.v.slice(5).toLowerCase());
            const wantMine = o.mine(sel.kind).map((c) => c.toLowerCase());
            if (mine.join(',') !== wantMine.join(',')) {
                p.push('"My languages" is [' + mine.join(', ') + '], want [' + wantMine.join(', ') + ']');
            }
        }
        const all = langOpts.filter((x) => x.g === SPEC.allGroup);
        const unsorted = all.findIndex((x, i) => i > 0 && collator.compare(all[i - 1].l, x.l) > 0);
        if (unsorted > 0) {
            p.push('"All languages" not alphabetical at "' + all[unsorted - 1].l + '" > "' + all[unsorted].l + '"');
        }
        // The language list is "My languages" plus "All languages" (a language may sit in only one of them).
        const codes = new Set(langOpts.filter((x) => x.g === SPEC.allGroup || x.g === SPEC.mineGroup).map((x) => x.v.slice(5).toLowerCase()));
        const lacking = Array.from(expectedCodes).filter((c) => !codes.has(c));
        const extraCodes = Array.from(codes).filter((c) => !expectedCodes.has(c));
        if (all.length < 150 || all.length > 260) {
            p.push('"All languages" has ' + all.length + ' entries (about 185 expected)');
        }
        if (lacking.length) {
            p.push('the language list lacks ' + lacking.length + ': ' + lacking.slice(0, 10).join(', '));
        }
        if (extraCodes.length) {
            p.push('the language list has ' + extraCodes.length + ' codes not in the expected list: ' + extraCodes.slice(0, 10).join(', '));
        }
        if (p.length) {
            problems.push(sel.label + ': ' + p.join('; '));
        }
        t.verbose(sel.label + ': ' + opts.length + ' options, groups [' + groups.join(', ') + '], ' + all.length + ' in All languages');
    }
    await t.check(problems.length === 0, o.where + ': every dropdown matches the table contract (Default first, "video" only when Default isn\'t the video default, '
        + 'Off/When the audio isn\'t in my languages/Signs and songs only, "My languages" then an alphabetical "All languages" of about 185'
        + (o.admin ? ', every Default "Video default"' : '') + ')' + (problems.length ? '\n       ' + problems.join('\n       ') : ''), page);
    if (o.help) {
        const text = apos(await page.evaluate(function (sc) { var r = sc ? document.querySelector(sc) : document.body; return r ? r.innerText : ''; }, scope || null));
        await t.check(text.indexOf(SPEC.helpVideo) >= 0 && text.indexOf(apos(SPEC.helpSmart)) >= 0, o.where + ': the two help lines under the table ("Video default: …", "When the audio isn\'t in my languages: …")', page);
    }
}

/**
 * Sweeps one dropdown with the keyboard: focus, ArrowDown to each next option, check. A full step waits `settle` ms and checks the
 * value, the change event, the save bar (or Save button) and that no Default (…) label moved; a quick step (options ctx.sample
 * leaves out) waits ctx.quick ms and checks the value and the change event. Home goes back to Default at the end.
 * ctx: { scope, saveBarSel, dirtyButtonScope, before { label: Default label }, checkVideo, baseDirty, sample(option, options), quick }.
 */
async function sweepSelect(t, page, label, ctx) {
    const failures = [];
    let shot = null;
    const look = () => snap(page, ctx.scope, label, ctx.saveBarSel, ctx.dirtyButtonScope);
    let s = await look();
    const start = byLabel(s, label);
    if (!start) {
        return { steps: 0, full: 0, failures: ['no dropdown labelled "' + label + '"'], keys: 0, direct: 0 };
    }
    const options = start.options;
    const initialValue = start.value;
    let keys = 0;
    let direct = 0;
    let steps = 0;
    let full = 0;
    for (let cursor = start.index + 1; cursor < options.length; cursor++) {
        const target = options[cursor];
        if (target.d) {
            continue;
        }
        const thorough = !ctx.sample || ctx.sample(target, options);
        const now0 = byLabel(s, label);
        const changesBefore = s.changes.length;
        if (now0 && now0.index === cursor - 1 && now0.count === options.length) {
            if (!(await page.evaluate(pgFocusSelect, ctx.scope || null, label))) {
                failures.push(target.v + ': could not focus the dropdown');
                continue;
            }
            await page.press('ArrowDown');
            keys++;
        } else {
            await page.evaluate(pgPick, ctx.scope || null, label, target.v);
            direct++;
        }
        await sleep(thorough ? t.settle : (ctx.quick || 100));
        s = await look();
        steps++;
        full += thorough ? 1 : 0;
        const now = byLabel(s, label);
        const p = [];
        if (!now) {
            p.push('the dropdown is gone');
        } else {
            if (now.value !== target.v) {
                p.push('shows ' + JSON.stringify(now.value) + ' ("' + now.text + '") instead');
            } else if (now.text !== target.l) {
                p.push('shows the text "' + now.text + '"');
            }
            const fired = s.changes.slice(changesBefore).filter((c) => c.label === label);
            if (!fired.length) {
                p.push('no change event');
            }
        }
        if (thorough) {
            const dirty = !!ctx.baseDirty || target.v !== initialValue;
            if (!s.saveBar || s.saveBar.visible !== dirty || (dirty && !s.saveBar.saveEnabled)) {
                p.push((ctx.dirtyButtonScope ? 'Save button ' + (s.saveBar && s.saveBar.saveEnabled ? 'enabled' : 'disabled') : 'save bar ' + (s.saveBar ? (s.saveBar.visible ? 'shown' : 'hidden') + (s.saveBar.saveEnabled ? '' : ', Save disabled') : 'missing'))
                    + ' (want ' + (dirty ? (ctx.dirtyButtonScope ? 'enabled' : 'shown') : (ctx.dirtyButtonScope ? 'disabled' : 'hidden')) + ')');
            }
            const labelsOff = [];
            const videoOff = [];
            for (const other of s.selects) {
                if (other.label === label) {
                    continue;
                }
                // No live composition; every other row's Default (…) stays what the server says.
                const want = ctx.before[other.label];
                if (want !== null && want !== undefined && other.defaultLabel !== want) {
                    labelsOff.push('"' + other.label + '" Default reads "' + other.defaultLabel + '" (want "' + want + '")');
                }
                if (ctx.checkVideo && !!other.hasVideo === (other.defaultLabel === SPEC.videoDefault) && other.value !== 'video') {
                    videoOff.push('"' + other.label + '" ' + (other.hasVideo ? 'offers' : 'lacks') + ' "video" with Default "' + other.defaultLabel + '"');
                }
            }
            if (labelsOff.length) {
                p.push(labelsOff.slice(0, 3).join('; ') + (labelsOff.length > 3 ? ' (+' + (labelsOff.length - 3) + ' more Default labels)' : ''));
            }
            if (videoOff.length) {
                p.push(videoOff[0] + (videoOff.length > 1 ? ' (+' + (videoOff.length - 1) + ' more dropdowns)' : ''));
            }
        }
        if (p.length) {
            failures.push('→ ' + JSON.stringify(target.v) + ' "' + target.l + '": ' + p.join('; '));
            if (!shot) {
                shot = await t.shot(page, label + ' ' + target.v);
            }
        }
        t.verbose(label + ' → ' + target.v + (thorough ? '' : ' (quick)') + (p.length ? ' FAIL ' + p.join('; ') : ' ok'));
    }
    // Back to Default with the keyboard (Home).
    if (await page.evaluate(pgFocusSelect, ctx.scope || null, label)) {
        await page.press('Home');
        await sleep(t.settle);
        s = await look();
        const end = byLabel(s, label);
        const barOk = !!s.saveBar && s.saveBar.visible === !!ctx.baseDirty;
        if (!end || end.value !== '' || !barOk) {
            await page.evaluate(pgPick, ctx.scope || null, label, '');
            failures.push('Home: ' + (end ? 'shows ' + JSON.stringify(end.value) : 'dropdown gone') + (barOk ? '' : ', ' + (ctx.dirtyButtonScope ? 'Save button' : 'save bar') + ' still ' + (s.saveBar && s.saveBar.visible ? 'on' : 'off'))
                + ' (want Default' + (ctx.baseDirty ? ')' : ' and nothing to save)'));
        }
    }
    return { steps: steps, full: full, failures: failures, keys: keys, direct: direct, options: options.length };
}

/** Thorough checks for every option except "All languages", where every 12th, the first, the last and a few picked ones get them. */
function sampleLanguages(option, options) {
    if (option.g !== SPEC.allGroup) {
        return true;
    }
    const all = options.filter((x) => x.g === SPEC.allGroup);
    const i = all.findIndex((x) => x.v === option.v);
    return i === 0 || i === all.length - 1 || i % 12 === 0 || ['lang:ja', 'lang:fr', 'lang:de', 'lang:zh-Hant', 'lang:pt-BR', 'lang:es-419', 'lang:yue'].indexOf(option.v) >= 0;
}

/**
 * Sweeps every dropdown of the rows shown in one mode with the keyboard, one browser tab per dropdown (in parallel unless --serial;
 * tabs are independent because nothing is saved). The stored mode must already show `rows` when a tab opens.
 * o: { where, mode, rows, open(tab), scope, saveBarSel, dirtyButtonScope, sample }. Prints one check per dropdown.
 */
async function sweepTable(t, o) {
    const jobs = [];
    o.rows.forEach((row) => { jobs.push({ row: row, label: SPEC.audioFor(row) }, { row: row, label: SPEC.subtitlesFor(row) }); });
    const sweepOne = async (job) => {
        const tab = t.opts.serial ? t.page : await t.newPage();
        try {
            await o.open(tab);
            await tab.evaluate(pgInstallChangeLog);
            const s0 = await snap(tab, o.scope, null, o.saveBarSel, o.dirtyButtonScope);
            const shown = rowsOf(s0);
            if (!sameRows(shown, o.rows)) {
                return { label: job.label, r: { steps: 0, failures: ['the page shows the rows ' + (shown.join(', ') || 'none') + ' instead of ' + o.rows.join(', ')], keys: 0, direct: 0 } };
            }
            const before = {};
            s0.selects.forEach((x) => { before[x.label] = x.defaultLabel; });
            const r = await sweepSelect(t, tab, job.label, {
                scope: o.scope, saveBarSel: o.saveBarSel, dirtyButtonScope: o.dirtyButtonScope, sample: o.sample, before: before, checkVideo: true, baseDirty: false
            });
            return { label: job.label, r: r };
        } catch (e) {
            return { label: job.label, r: { steps: 0, failures: ['crashed: ' + e.message], keys: 0, direct: 0 } };
        } finally {
            if (tab !== t.page) {
                await t.chrome.closePage(tab);
            }
        }
    };
    const started = Date.now();
    let swept = [];
    if (t.opts.serial) {
        for (const job of jobs) {
            swept.push(await sweepOne(job));
        }
    } else {
        swept = await Promise.all(jobs.map(sweepOne));
    }
    for (const x of swept) {
        const f = x.r.failures;
        const how = o.sample ? (x.r.full || 0) + ' with the full ' + t.settle + ' ms check, the rest checked after 100 ms' : 'each checked ' + t.settle + ' ms later';
        await t.check(f.length === 0 && x.r.steps > 0, o.where + ' (' + o.mode + '), ' + x.label + ': all ' + (x.r.options || 0) + ' options picked with the keyboard (' + x.r.keys + ' arrow presses'
            + (x.r.direct ? ', ' + x.r.direct + ' direct picks' : '') + '; ' + how + '): each stays selected, '
            + (o.dirtyButtonScope ? 'Save is enabled' : 'the save bar shows') + ', no other Default (…) label moves'
            + (f.length ? ' — ' + f.length + ' problem(s):\n       ' + f.slice(0, 12).join('\n       ') + (f.length > 12 ? '\n       … and ' + (f.length - 12) + ' more' : '') : ''), null);
    }
    t.note(o.where + ' (' + o.mode + '): keyboard sweep of ' + swept.reduce((n, x) => n + x.r.steps, 0) + ' choices took ' + Math.round((Date.now() - started) / 1000) + ' s');
}

// ───────────────────────────── 4. Person page: the either/or control ─────────────────────────────

/** Snapshots the person's and the admin's settings, with undo steps that put them back. */
async function keepPersonAndAdmin(t) {
    const me0 = await getMe(t);
    const snapshot = clone(me0.Settings);
    t.defer('person settings restored', async () => {
        await restorePersonSnapshot(t, t.api, '/Babelfin/Me/Settings', snapshot, 'person settings put back through the API');
    });
    const adminSnapshot = clone(await getAdmin(t));
    t.defer('admin settings restored', async () => {
        const r = await t.api.put('/Babelfin/Admin/Settings', adminSnapshot);
        await t.check(r.status === 200, 'admin settings put back through the API (' + r.status + ')', null);
    });
    return { me: me0, snapshot: snapshot, admin: adminSnapshot };
}

/** The names of the libraries the person can see, as Me/Settings lists them. */
function libraryNames(me) {
    return (me.Libraries || []).map((l) => l.Name);
}

/** Reports a missing radiogroup once. */
async function requireMode(t, page, scope, where) {
    const info = await modeInfo(page, scope);
    if (!info.found) {
        await t.check(false, where + ': the "' + SPEC.modeGroup + '" radiogroup with "' + SPEC.modeSame + '" and "' + SPEC.modePer + '" is there', page);
        return null;
    }
    return info;
}

async function testPersonMode(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    const libs = libraryNames(kept.me);
    const animeId = t.ids.libraries.Anime;
    await putAdmin(t, adminNeutral(kept.admin), 'neutral for the mode tests');
    const clean = withTable(t, kept.snapshot, { perLibrary: null });

    // 4a. Automatic (PerLibrary null) with no library choice: "Same for every library", one row "All libraries".
    await putMe(t, clean, 'automatic, nothing chosen');
    await openPersonPage(t, page);
    const info = await requireMode(t, page, null, 'person page');
    if (!info) {
        return;
    }
    await t.check(info.role === 'radiogroup' && info.name === SPEC.modeGroup,
        'the mode control is a radiogroup labelled "' + SPEC.modeGroup + '" (role ' + info.role + ', name "' + info.name + '")', page);
    const labels = info.radios.map((r) => r.label);
    await t.check(labels.join('|') === [SPEC.modeSame, SPEC.modePer].join('|') && info.radios.every((r) => r.visible && !r.disabled),
        'it has exactly two radios, "' + SPEC.modeSame + '" then "' + SPEC.modePer + '", both visible and enabled (' + labels.map((l) => '"' + l + '"').join(', ') + ')', page);
    let s = await snap(page, null);
    await t.check(info.checked === SPEC.modeSame && sameRows(rowsOf(s), [SPEC.allLibraries]),
        'automatic mode with no library choice stored: "' + SPEC.modeSame + '" is checked and the table has one row, "All libraries" (checked: ' + info.checked + '; rows: ' + rowsOf(s).join(', ') + ')', page);
    await t.check(s.saveBar && !s.saveBar.visible, 'no save bar after loading', page);
    const d0 = (await getMe(t)).Defaults || {};
    await t.check(d0.PerLibrary === false, 'the server agrees: GET Me/Settings Defaults.PerLibrary is false (' + d0.PerLibrary + ')', null);

    // 4a'. A library entry with only a field outside the table doesn't count.
    const other = clone(clean);
    const moviesId = t.ids.libraries.Movies;
    other.Libraries = (other.Libraries || []).filter((l) => compactId(l.LibraryId) !== compactId(moviesId));
    other.Libraries.push({ LibraryId: compactId(moviesId), Name: 'Movies', Settings: { AvoidCommentary: true } });
    const putOther = await t.api.put('/Babelfin/Me/Settings', other);
    if (putOther.status === 200) {
        await reloadPersonPage(t, page);
        const r = await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries], 3000);
        await t.check(r.ok, 'automatic mode with a library entry that sets only a field outside the table (Movies: Skip commentary tracks) still shows "' + SPEC.modeSame + '" (' + modeText(r) + ')', page);
    } else {
        t.note('a library entry with only AvoidCommentary was refused (' + putOther.status + '); that case is skipped');
    }

    // 4b. Automatic with a library entry that sets a table field: "Choose for each library", one row per library.
    await putMe(t, withTable(t, kept.snapshot, { perLibrary: null, libs: { Anime: ['lang:ja', undefined] } }), 'automatic, Anime audio chosen');
    await reloadPersonPage(t, page);
    let r = await waitMode(page, null, SPEC.modePer, libs, 3000);
    s = await snap(page, null);
    const animeAudio = byLabel(s, SPEC.audioFor('Anime'));
    await t.check(r.ok && animeAudio && animeAudio.value === 'lang:ja' && s.saveBar && !s.saveBar.visible,
        'automatic mode with Anime audio stored: "' + SPEC.modePer + '" is checked, one row per library and no All libraries row, Anime shows Japanese, no save bar (' + modeText(r) + '; Anime audio ' + (animeAudio && animeAudio.value) + ')', page);
    const d1 = (await getMe(t)).Defaults || {};
    await t.check(d1.PerLibrary === true, 'the server agrees: Defaults.PerLibrary is true (' + d1.PerLibrary + ')', null);

    // 4c. The mouse switches the rows; a switch is a change (save bar).
    await putMe(t, clean, 'automatic, nothing chosen');
    await reloadPersonPage(t, page);
    await clickMode(page, null, SPEC.modePer);
    r = await waitMode(page, null, SPEC.modePer, libs);
    s = await snap(page, null);
    await t.check(r.ok && s.saveBar && s.saveBar.visible,
        'clicking "' + SPEC.modePer + '" checks it, shows one row per library (' + libs.join(', ') + ') without All libraries, and shows the save bar (' + modeText(r) + '; save bar ' + (s.saveBar && s.saveBar.visible ? 'shown' : 'hidden') + ')', page);
    await clickMode(page, null, SPEC.modeSame);
    r = await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries]);
    s = await snap(page, null);
    await t.check(r.ok, 'clicking "' + SPEC.modeSame + '" checks it and shows only the All libraries row (' + modeText(r) + ')', page);
    t.note('after switching back to the mode shown at load the save bar is ' + (s.saveBar && s.saveBar.visible ? 'shown (automatic → "' + SPEC.modeSame + '" is stored as false)' : 'hidden'));

    // 4d. The keyboard: arrows inside the radiogroup move and check, Space checks the focused radio. Each step starts from a known
    // state: the "from" radio checked (clicked first when it isn't) and focused.
    await reloadPersonPage(t, page);
    const keyStep = async (from, key, want) => {
        const now = await modeInfo(page, null);
        if (key !== 'Space' && now.checked !== from) {
            await clickMode(page, null, from);
            await waitMode(page, null, from, from === SPEC.modeSame ? [SPEC.allLibraries] : libs);
        }
        const focusOn = key === 'Space' ? want : from;
        if (!(await page.evaluate(pgModeFocus, null, SPEC.modeGroup, SPEC.modeSame, focusOn))) {
            return 'could not focus "' + focusOn + '"';
        }
        await page.press(key);
        const rows = want === SPEC.modeSame ? [SPEC.allLibraries] : libs;
        const got = await waitMode(page, null, want, rows, 3000);
        const focused = got.info.found ? (got.info.radios.find((x) => x.focused) || {}).label : null;
        const ok = got.ok && focused === want;
        return (key === 'Space' ? 'focus "' + want + '", Space' : 'on "' + from + '", ' + key) + ' → ' + (ok ? 'ok' : 'WRONG (' + modeText(got) + '; focus on ' + (focused || 'something else') + ')');
    };
    const steps = [];
    steps.push(await keyStep(SPEC.modeSame, 'ArrowRight', SPEC.modePer));
    steps.push(await keyStep(SPEC.modePer, 'ArrowLeft', SPEC.modeSame));
    steps.push(await keyStep(SPEC.modeSame, 'ArrowDown', SPEC.modePer));
    steps.push(await keyStep(SPEC.modePer, 'ArrowUp', SPEC.modeSame));
    await t.check(steps.every((x) => / ok$/.test(x)), 'keyboard: ArrowRight/ArrowDown and ArrowLeft/ArrowUp inside the radiogroup move the focus to the other radio, check it and switch the rows: ' + steps.join('; '), page);
    const wrap = [];
    wrap.push(await keyStep(SPEC.modeSame, 'ArrowLeft', SPEC.modePer));
    wrap.push(await keyStep(SPEC.modePer, 'ArrowRight', SPEC.modeSame));
    await t.check(wrap.every((x) => / ok$/.test(x)), 'keyboard: the arrows wrap around (ArrowLeft on the first radio goes to the last, ArrowRight on the last to the first): ' + wrap.join('; '), page);
    const space = [];
    await clickMode(page, null, SPEC.modeSame);
    await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries]);
    space.push(await keyStep(null, 'Space', SPEC.modePer));
    space.push(await keyStep(null, 'Space', SPEC.modeSame));
    await t.check(space.every((x) => / ok$/.test(x)), 'keyboard: Space on the focused, unchecked radio checks it and switches the rows: ' + space.join('; '), page);

    // 4e. Saving stores exactly the mode plus the edited fields; the other mode's values stay stored.
    await putMe(t, clean, 'automatic, nothing chosen');
    await reloadPersonPage(t, page);
    const before = (await getMe(t)).Settings;
    await clickMode(page, null, SPEC.modePer);
    await waitMode(page, null, SPEC.modePer, libs);
    let picks = picksFor({ Anime: ['lang:ja', 'lang:en'] });
    let failed = await pickAll(page, null, picks);
    await sleep(t.settle);
    s = await snap(page, null);
    await t.check(!failed.length && !notShown(s, picks).length, '"' + SPEC.modePer + '": Anime Japanese / English picked and still shown' + (failed.concat(notShown(s, picks)).length ? ' — ' + failed.concat(notShown(s, picks)).join('; ') : ''), page);
    await savePersonPage(t, page);
    const after1 = (await getMe(t)).Settings;
    await t.check(after1.PerLibrary === true, 'Save stores PerLibrary true (GET Me/Settings: ' + after1.PerLibrary + ')', page);
    let stray = strayPaths(before, after1, ['PerLibrary', libraryTablePaths(animeId)]);
    await t.check(same(tableOf(libraryLayer(after1, animeId)), storedFor('lang:ja', 'lang:en')) && same(tableOf(after1.Everyday), tableOf(before.Everyday)) && !stray.length,
        'Save stores exactly PerLibrary and Anime ' + canon(storedFor('lang:ja', 'lang:en')) + ' (Anime stored ' + canon(tableOf(libraryLayer(after1, animeId))) + (stray.length ? '; also changed: ' + stray.join(', ') : '') + ')', page);
    await clickMode(page, null, SPEC.modeSame);
    await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries]);
    picks = picksFor({ 'All libraries': ['lang:en', 'off'] });
    failed = await pickAll(page, null, picks);
    await sleep(t.settle);
    await savePersonPage(t, page);
    const after2 = (await getMe(t)).Settings;
    await t.check(after2.PerLibrary === false, 'switching to "' + SPEC.modeSame + '" and saving stores PerLibrary false (' + after2.PerLibrary + ')', page);
    stray = strayPaths(after1, after2, ['PerLibrary', EVERYDAY_TABLE]);
    await t.check(!failed.length && same(tableOf(after2.Everyday), storedFor('lang:en', 'off')) && !stray.length,
        'it stores exactly PerLibrary and the All libraries row ' + canon(storedFor('lang:en', 'off')) + ' in Everyday (Everyday ' + canon(tableOf(after2.Everyday)) + (stray.length ? '; also changed: ' + stray.join(', ') : '') + (failed.length ? '; ' + failed.join('; ') : '') + ')', page);
    await t.check(same(tableOf(libraryLayer(after2, animeId)), storedFor('lang:ja', 'lang:en')),
        'the per-library values stay stored while "' + SPEC.modeSame + '" is in use (Anime ' + canon(tableOf(libraryLayer(after2, animeId))) + ')', page);

    // 4f. Both modes' values survive switching back and forth, across a save and a reload.
    await reloadPersonPage(t, page);
    r = await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries], 3000);
    s = await snap(page, null);
    await t.check(r.ok && !notShown(s, picks).length, 'after a reload "' + SPEC.modeSame + '" is checked and All libraries shows English / Off (' + modeText(r) + (notShown(s, picks).length ? '; ' + notShown(s, picks).join('; ') : '') + ')', page);
    await clickMode(page, null, SPEC.modePer);
    r = await waitMode(page, null, SPEC.modePer, libs);
    s = await snap(page, null);
    const animePicks = picksFor({ Anime: ['lang:ja', 'lang:en'], Movies: ['', ''] });
    await t.check(r.ok && !notShown(s, animePicks).length, 'switching to "' + SPEC.modePer + '" brings back the stored per-library choices (Anime Japanese / English, Movies Default)' + (notShown(s, animePicks).length ? ' — ' + notShown(s, animePicks).join('; ') : ''), page);
    failed = await pickAll(page, null, [{ label: SPEC.audioFor('Movies'), value: 'lang:fr' }]);
    await sleep(t.settle);
    await clickMode(page, null, SPEC.modeSame);
    const back1 = await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries]);
    const s1 = await snap(page, null);
    await clickMode(page, null, SPEC.modePer);
    const back2 = await waitMode(page, null, SPEC.modePer, libs);
    const s2 = await snap(page, null);
    const unsaved = picksFor({ Anime: ['lang:ja', 'lang:en'], Movies: ['lang:fr', ''] });
    await t.check(!failed.length && back1.ok && !notShown(s1, picks).length && back2.ok && !notShown(s2, unsaved).length,
        'an unsaved change (Movies audio French) survives switching to "' + SPEC.modeSame + '" (All libraries still English / Off) and back' + (failed.concat(notShown(s1, picks), notShown(s2, unsaved)).length ? ' — ' + failed.concat(notShown(s1, picks), notShown(s2, unsaved)).join('; ') : ''), page);
    await page.click(pgButton, { args: ['.bf-savebar', 'Discard'], what: 'Discard in the save bar' });
    try {
        await page.click(pgDialogButton, { args: ['Discard'], what: 'Discard in the confirmation', timeout: 5000 });
    } catch (e) {
        // no confirmation
    }
    r = await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries], 4000);
    s = await snap(page, null);
    await t.check(r.ok && !notShown(s, picks).length && s.saveBar && !s.saveBar.visible && same((await getMe(t)).Settings, after2),
        'Discard puts the stored mode back too ("' + SPEC.modeSame + '", All libraries English / Off), hides the save bar and saves nothing (' + modeText(r) + ')', page);
    await clickMode(page, null, SPEC.modePer);
    await waitMode(page, null, SPEC.modePer, libs);
    s = await snap(page, null);
    await t.check(s.saveBar && s.saveBar.visible, 'a mode switch alone shows the save bar', page);
    if (s.saveBar && s.saveBar.visible) {
        await savePersonPage(t, page);
    }
    const after3 = (await getMe(t)).Settings;
    stray = strayPaths(after2, after3, ['PerLibrary']);
    await t.check(after3.PerLibrary === true && !stray.length,
        'saving a mode switch alone stores PerLibrary true and nothing else (' + after3.PerLibrary + (stray.length ? '; also changed: ' + stray.join(', ') : '') + ')', page);
    await t.check(same(tableOf(after3.Everyday), storedFor('lang:en', 'off')) && same(tableOf(libraryLayer(after3, animeId)), storedFor('lang:ja', 'lang:en')),
        'both modes\' values are still stored (Everyday ' + canon(tableOf(after3.Everyday)) + ', Anime ' + canon(tableOf(libraryLayer(after3, animeId))) + ')', page);
    await reloadPersonPage(t, page);
    r = await waitMode(page, null, SPEC.modePer, libs, 3000);
    s = await snap(page, null);
    const ok1 = r.ok && !notShown(s, animePicks).length;
    await clickMode(page, null, SPEC.modeSame);
    const r2 = await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries]);
    const s3 = await snap(page, null);
    await t.check(ok1 && r2.ok && !notShown(s3, picks).length,
        'after a reload "' + SPEC.modePer + '" is checked with Anime Japanese / English, and switching to "' + SPEC.modeSame + '" still shows English / Off'
        + (notShown(s, animePicks).concat(notShown(s3, picks)).length ? ' — ' + notShown(s, animePicks).concat(notShown(s3, picks)).join('; ') : ''), page);
}

// ───────────────────────────── 5. Person page: the table's dropdowns in each mode ─────────────────────────────

async function testPersonTable(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    const libs = libraryNames(kept.me);
    const libIdOf = (name) => t.ids.libraries[name];
    // The admin sets Anime and Movies per library, so those rows' Default isn't the video default (they offer "video") while TV Shows'
    // is (it doesn't), and the All libraries row reads "set by the server for each library".
    await putAdmin(t, withTable(t, adminNeutral(kept.admin), { modeField: 'PerLibraryDefaults', perLibrary: true, libs: { Anime: ['lang:ja', 'lang:en'], Movies: ['lang:en', 'off'] } }), 'Anime and Movies defaults');
    const clean = withTable(t, kept.snapshot, { perLibrary: false });
    await putMe(t, clean, 'every library, nothing chosen');
    const me = await getMe(t);
    const everyday = me.Settings.Everyday || {};
    const inherited = (me.Inherited && me.Inherited.Profile) || {};
    const understood = everyday.Understood || inherited.Understood || [];
    const reads = everyday.Reads || inherited.Reads || [];
    const languages = (await t.api.get('/Babelfin/Languages')).body || [];
    const mineFor = (kind) => {
        const list = kind === 'audio' ? understood.slice() : reads.concat(understood);
        return list.filter((c, i) => list.findIndex((d) => d.toLowerCase() === c.toLowerCase()) === i);
    };

    await openPersonPage(t, page);
    if (!(await requireMode(t, page, null, 'person page'))) {
        return;
    }
    // 5a. The dropdowns' options against the table contract in each mode.
    await contractChecks(t, page, null, { where: 'person page, "' + SPEC.modeSame + '"', rows: [SPEC.allLibraries], languages: languages, mine: mineFor, help: true, storedLayer: (row) => rowLayer(me.Settings, row, libIdOf) });
    await clickMode(page, null, SPEC.modePer);
    await waitMode(page, null, SPEC.modePer, libs);
    await contractChecks(t, page, null, { where: 'person page, "' + SPEC.modePer + '"', rows: libs, languages: languages, mine: mineFor, help: true, storedLayer: (row) => rowLayer(me.Settings, row, libIdOf) });

    // 5b. Every option of every dropdown, picked with the keyboard, in each mode (the stored mode decides what a tab shows).
    const sample = t.opts.quickSweeps ? sampleLanguages : null;
    await sweepTable(t, { where: 'person page', mode: SPEC.modeSame, rows: [SPEC.allLibraries], scope: null, saveBarSel: '.bf-savebar', sample: sample, open: (tab) => openPersonPage(t, tab) });
    await putMe(t, withTable(t, kept.snapshot, { perLibrary: true }), 'per library, nothing chosen');
    await sweepTable(t, { where: 'person page', mode: SPEC.modePer, rows: libs, scope: null, saveBarSel: '.bf-savebar', sample: sample, open: (tab) => openPersonPage(t, tab) });

    // 5c. One library row: A, B, A, Default, checked after each step; no other Default label moves.
    await openPersonPage(t, page);
    await waitMode(page, null, SPEC.modePer, libs, 3000);
    const loaded = await snap(page, null);
    const initialDefaults = {};
    loaded.selects.forEach((x) => { initialDefaults[x.label] = x.defaultLabel; });
    for (const kind of ['audio', 'subtitles']) {
        const label = kind === 'audio' ? SPEC.audioFor('Anime') : SPEC.subtitlesFor('Anime');
        const seq = kind === 'audio' ? ['lang:ja', 'lang:en', 'lang:ja', ''] : ['smart', 'lang:en', 'smart', ''];
        const problems = [];
        for (const v of seq) {
            const r = await page.evaluate(pgPick, null, label, v);
            if (!r.ok) {
                problems.push(JSON.stringify(v) + ': ' + r.reason);
                continue;
            }
            await sleep(t.settle);
            const s = await snap(page, null, label);
            const now = byLabel(s, label);
            if (!now || now.value !== v) {
                problems.push(JSON.stringify(v) + ': shows ' + (now ? JSON.stringify(now.value) : 'nothing'));
            }
            if (!s.saveBar || s.saveBar.visible !== (v !== '')) {
                problems.push(JSON.stringify(v) + ': save bar ' + (s.saveBar && s.saveBar.visible ? 'shown' : 'hidden'));
            }
            const moved = s.selects.filter((o) => o.label !== label && o.defaultLabel !== initialDefaults[o.label]).map((o) => o.label + ' "' + o.defaultLabel + '"');
            if (moved.length) {
                problems.push(JSON.stringify(v) + ': other Default labels changed: ' + moved.join(', '));
            }
        }
        await t.check(problems.length === 0, label + ': ' + seq.map((v) => JSON.stringify(v)).join(', ') + ' each stays selected after ' + t.settle + ' ms with the right save bar, and no Default label moves' + (problems.length ? ' — ' + problems.join('; ') : ''), page);
    }

    // 5d. Rounds in both modes: pick, save, check what is stored (and that the other mode's values are kept), reload, check.
    await putMe(t, clean, 'every library, nothing chosen');
    await openPersonPage(t, page);
    const rounds = [
        { mode: SPEC.modePer, rows: { Anime: ['lang:ja', 'lang:en'], Movies: ['video', 'off'], 'TV Shows': ['', 'signs'] } },
        { mode: SPEC.modeSame, rows: { 'All libraries': ['lang:en', 'smart'] } },
        { mode: SPEC.modePer, rows: { Anime: ['video', 'smart'], Movies: ['', 'video'], 'TV Shows': ['lang:fr', 'off'] } },
        { mode: SPEC.modeSame, rows: { 'All libraries': ['', ''] } },
        { mode: SPEC.modePer, rows: { Anime: ['', ''], Movies: ['', ''], 'TV Shows': ['', ''] } }
    ];
    let prev = (await getMe(t)).Settings;
    for (let i = 0; i < rounds.length; i++) {
        const round = rounds[i];
        const per = round.mode === SPEC.modePer;
        const shownRows = per ? libs : [SPEC.allLibraries];
        const order = Object.keys(round.rows).filter((r) => shownRows.indexOf(r) >= 0);
        const list = picksFor(order.reduce((m, r) => { m[r] = round.rows[r]; return m; }, {}));
        const switched = await ensureMode(page, null, round.mode, shownRows);
        const failed = await pickAll(page, null, list);
        await sleep(t.settle);
        const s = await snap(page, null);
        const off = notShown(s, list);
        const what = 'round ' + (i + 1) + ' ("' + round.mode + '": ' + order.map((r) => r + ' ' + round.rows[r].map((v) => JSON.stringify(v)).join('/')).join(', ') + ')';
        await t.check(switched && failed.length === 0 && off.length === 0, what + ': picked and still shown after ' + t.settle + ' ms' + (!switched ? ' — the mode didn\'t switch' : '') + (failed.length || off.length ? ' — ' + failed.concat(off).join('; ') : ''), page);
        if (failed.length || !switched) {
            t.note('round ' + (i + 1) + ' skipped');
            await openPersonPage(t, page);
            prev = (await getMe(t)).Settings;
            continue;
        }
        if (s.saveBar && s.saveBar.visible) {
            await savePersonPage(t, page);
        }
        const doc = (await getMe(t)).Settings;
        const problems = [];
        order.forEach((r) => {
            const want = storedFor(round.rows[r][0], round.rows[r][1]);
            const layer = r === SPEC.allLibraries ? (doc.Everyday || {}) : libraryLayer(doc, libIdOf(r));
            const got = tableOf(layer);
            if (!same(got, want)) {
                problems.push(r + ' stored ' + canon(got) + ', want ' + canon(want));
            }
            if (r !== SPEC.allLibraries && layer) {
                const keys = setKeys(layer);
                const wantKeys = setKeys(want);
                if (keys.join(',') !== wantKeys.join(',')) {
                    problems.push(r + ' layer has fields [' + keys.join(', ') + '], want exactly [' + wantKeys.join(', ') + ']');
                }
            }
        });
        // The other mode's values are kept as they were.
        if (per && !same(tableOf(doc.Everyday), tableOf(prev.Everyday))) {
            problems.push('the All libraries values changed while "' + SPEC.modePer + '" was in use: ' + canon(tableOf(prev.Everyday)) + ' → ' + canon(tableOf(doc.Everyday)));
        }
        if (!per && !same(libraryTables(doc), libraryTables(prev))) {
            problems.push('the per-library values changed while "' + SPEC.modeSame + '" was in use: ' + canon(libraryTables(prev)) + ' → ' + canon(libraryTables(doc)));
        }
        const otherEveryday = (d) => { const e = Object.assign({}, d.Everyday || {}); SPEC.tableFields.forEach((f) => delete e[f]); return e; };
        if (!same(otherEveryday(doc), otherEveryday(clean))) {
            problems.push('Everyday fields outside the table changed: ' + changedPaths(otherEveryday(clean), otherEveryday(doc)).join(', '));
        }
        await t.check(problems.length === 0, what + ': Save stores exactly the table fields and keeps the other mode\'s values' + (problems.length ? ' — ' + problems.join('; ') : ''), page);
        await t.check(doc.PerLibrary === per, what + ': PerLibrary stored as ' + per + ' (' + doc.PerLibrary + ')', page);
        prev = doc;
        await reloadPersonPage(t, page);
        const m = await waitMode(page, null, round.mode, shownRows, 3000);
        const after = await snap(page, null);
        const wrong = notShown(after, list);
        await t.check(m.ok && wrong.length === 0 && after.saveBar && !after.saveBar.visible, what + ': after a reload the same mode is checked, every dropdown shows the stored choice and there is no save bar'
            + (m.ok ? '' : ' (' + modeText(m) + ')') + (wrong.length ? ' — ' + wrong.join('; ') : ''), page);
        if (i === 0) {
            // 5e. Discard puts the saved values and the saved mode back.
            const savedDoc = (await getMe(t)).Settings;
            const f = await pickAll(page, null, [{ label: SPEC.subtitlesFor('Anime'), value: 'off' }, { label: SPEC.audioFor('Movies'), value: 'lang:fr' }]);
            await clickMode(page, null, SPEC.modeSame);
            await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries]);
            await sleep(t.settle);
            const mid = await snap(page, null);
            await t.check(f.length === 0 && mid.saveBar && mid.saveBar.visible, 'Discard test: two changes picked and the mode switched, the save bar shows' + (f.length ? ' — ' + f.join('; ') : ''), page);
            await page.click(pgButton, { args: ['.bf-savebar', 'Discard'], what: 'Discard in the save bar' });
            let confirmed = false;
            try {
                await page.click(pgDialogButton, { args: ['Discard'], what: 'Discard in the confirmation', timeout: 5000 });
                confirmed = true;
            } catch (e) {
                confirmed = false;
            }
            const mb = await waitMode(page, null, SPEC.modePer, libs, 4000);
            await sleep(t.settle);
            const back = await snap(page, null);
            const notBack = notShown(back, list);
            await t.check(mb.ok && notBack.length === 0 && back.saveBar && !back.saveBar.visible, 'Discard' + (confirmed ? ' (and its confirmation)' : '') + ' puts the saved mode and every saved choice back and hides the save bar'
                + (mb.ok ? '' : ' (' + modeText(mb) + ')') + (notBack.length ? ' — ' + notBack.join('; ') : ''), page);
            const stillSaved = (await getMe(t)).Settings;
            await t.check(same(stillSaved, savedDoc), 'Discard saved nothing (GET Me/Settings unchanged)' + (same(stillSaved, savedDoc) ? '' : ': ' + changedPaths(savedDoc, stillSaved).join(', ')), page);
        }
    }

    // 5f. A stored value the table can't show appears as "Custom" in its mode and is kept.
    const customDoc = withTable(t, kept.snapshot, { perLibrary: false });
    customDoc.Everyday.AudioChain = [{ Kind: 'Language', Language: 'ja' }, { Kind: 'Language', Language: 'en' }, { Kind: 'VideoDefault' }];
    const animeId = libIdOf('Anime');
    customDoc.Libraries = (customDoc.Libraries || []).filter((l) => compactId(l.LibraryId) !== compactId(animeId));
    customDoc.Libraries.push({ LibraryId: compactId(animeId), Name: 'Anime', Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Full' } });
    const putCustom = await t.api.put('/Babelfin/Me/Settings', customDoc);
    await t.check(putCustom.status === 200, 'a stored audio order and subtitle mix the table can\'t show saved through the API (' + putCustom.status + ')', null);
    if (putCustom.status === 200) {
        const stored = (await getMe(t)).Settings;
        await openPersonPage(t, page);
        const m0 = await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries], 3000);
        const c = await snap(page, null);
        const a = byLabel(c, SPEC.audioFor(SPEC.allLibraries));
        await clickMode(page, null, SPEC.modePer);
        await waitMode(page, null, SPEC.modePer, libs);
        const c1 = await snap(page, null);
        const sb = byLabel(c1, SPEC.subtitlesFor('Anime'));
        await t.check(m0.ok && a && a.value === 'custom' && a.text === SPEC.custom && sb && sb.value === 'custom' && sb.text === SPEC.custom,
            'they show as "Custom" in their own mode (Audio for All libraries: ' + (a && JSON.stringify(a.value)) + ', Subtitles for Anime: ' + (sb && JSON.stringify(sb.value)) + ')', page);
        const f = await pickAll(page, null, [{ label: SPEC.audioFor('TV Shows'), value: 'lang:fr' }]);
        await sleep(t.settle);
        if (!f.length) {
            await savePersonPage(t, page);
        }
        const kept2 = (await getMe(t)).Settings;
        await t.check(!f.length && same(kept2.Everyday.AudioChain, stored.Everyday.AudioChain) && same(tableOf(libraryLayer(kept2, animeId)), tableOf(libraryLayer(stored, animeId)))
            && same(tableOf(libraryLayer(kept2, libIdOf('TV Shows'))), storedFor('lang:fr', '')),
        'saving another change keeps both Custom values untouched' + (f.length ? ' — ' + f.join('; ') : ''), page);
        await putMe(t, Object.assign(clone(kept2), { PerLibrary: false }), 'back to every library');
        await openPersonPage(t, page);
        const f2 = await pickAll(page, null, [{ label: SPEC.audioFor(SPEC.allLibraries), value: 'lang:de' }]);
        await sleep(t.settle);
        const f3 = await pickAll(page, null, [{ label: SPEC.audioFor(SPEC.allLibraries), value: 'custom' }]);
        await sleep(t.settle);
        const c2 = await snap(page, null);
        const a2 = byLabel(c2, SPEC.audioFor(SPEC.allLibraries));
        await t.check(!f2.length && !f3.length && a2 && a2.value === 'custom' && c2.saveBar && !c2.saveBar.visible,
            'picking German then "Custom" again restores the stored order (no save bar)' + (f2.concat(f3).length ? ' — ' + f2.concat(f3).join('; ') : ''), page);
    }
}

// ───────────────────────────── 6. Default labels ─────────────────────────────

/** The Default (…) label of every shown dropdown: { label: text }. */
async function defaultLabels(page, scope) {
    const out = {};
    (await snap(page, scope, null)).selects.forEach((x) => { out[x.label] = x.defaultLabel; });
    return out;
}

/** Compares Default labels with what is wanted ({ row: [audio label, subtitles label] }); returns the differences. */
function labelProblems(got, want) {
    const p = [];
    Object.keys(want).forEach((row) => {
        [[SPEC.audioFor(row), want[row][0]], [SPEC.subtitlesFor(row), want[row][1]]].forEach((pair) => {
            if (pair[1] !== undefined && got[pair[0]] !== pair[1]) {
                p.push(pair[0] + ' reads ' + (got[pair[0]] === undefined ? 'nothing (no such dropdown)' : '"' + got[pair[0]] + '"') + ' (want "' + pair[1] + '")');
            }
        });
    });
    return p;
}

async function testDefaultLabels(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    const libs = libraryNames(kept.me);
    await putMe(t, withTable(t, kept.snapshot, { perLibrary: false }), 'every library, nothing chosen');
    const neutral = adminNeutral(kept.admin);
    const cases = [
        {
            what: 'admin defaults per library that differ (Anime Japanese / English, Movies English / Italian, TV Shows none; an admin Everyday of French / Signs and songs only that per-library mode ignores)',
            admin: { modeField: 'PerLibraryDefaults', perLibrary: true, everyday: ['lang:fr', 'signs'], libs: { Anime: ['lang:ja', 'lang:en'], Movies: ['lang:en', 'lang:it'] } },
            all: [SPEC.serverEach, SPEC.serverEach],
            rows: { Anime: ['Default (Japanese)', 'Default (English)'], Movies: ['Default (English)', 'Default (Italian)'], 'TV Shows': [SPEC.videoDefault, SPEC.videoDefault] }
        },
        {
            what: 'admin defaults per library that agree (every library German / Off)',
            admin: { modeField: 'PerLibraryDefaults', perLibrary: true, libs: { Anime: ['lang:de', 'off'], Movies: ['lang:de', 'off'], 'TV Shows': ['lang:de', 'off'] } },
            all: ['Default (German)', 'Default (Off)'],
            rows: { Anime: ['Default (German)', 'Default (Off)'], Movies: ['Default (German)', 'Default (Off)'], 'TV Shows': ['Default (German)', 'Default (Off)'] }
        },
        {
            what: 'admin "same for every library" French / Signs and songs only (with an Anime entry of Japanese / English that this mode ignores)',
            admin: { modeField: 'PerLibraryDefaults', perLibrary: false, everyday: ['lang:fr', 'signs'], libs: { Anime: ['lang:ja', 'lang:en'] } },
            all: ['Default (French)', 'Default (Signs and songs only)'],
            rows: { Anime: ['Default (French)', 'Default (Signs and songs only)'], Movies: ['Default (French)', 'Default (Signs and songs only)'], 'TV Shows': ['Default (French)', 'Default (Signs and songs only)'] }
        },
        {
            what: 'no admin defaults',
            admin: { modeField: 'PerLibraryDefaults', perLibrary: null },
            all: [SPEC.videoDefault, SPEC.videoDefault],
            rows: { Anime: [SPEC.videoDefault, SPEC.videoDefault], Movies: [SPEC.videoDefault, SPEC.videoDefault], 'TV Shows': [SPEC.videoDefault, SPEC.videoDefault] }
        }
    ];
    let first = true;
    for (const c of cases) {
        await putAdmin(t, withTable(t, neutral, c.admin), c.what);
        await openPersonPage(t, page);
        if (first && !(await requireMode(t, page, null, 'person page'))) {
            return;
        }
        first = false;
        const m1 = await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries], 3000);
        const g1 = await defaultLabels(page, null);
        const p1 = labelProblems(g1, { 'All libraries': c.all });
        await t.check(m1.ok && !p1.length, c.what + ': the All libraries row reads "' + c.all.join('" / "') + '"' + (m1.ok ? '' : ' (' + modeText(m1) + ')') + (p1.length ? ' — ' + p1.join('; ') : ''), page);
        await clickMode(page, null, SPEC.modePer);
        const m2 = await waitMode(page, null, SPEC.modePer, libs);
        const g2 = await defaultLabels(page, null);
        const p2 = labelProblems(g2, c.rows);
        await t.check(m2.ok && !p2.length, c.what + ': each library row shows its own server default (' + Object.keys(c.rows).map((r) => r + ' "' + c.rows[r].join('" / "') + '"').join(', ') + ')'
            + (m2.ok ? '' : ' (' + modeText(m2) + ')') + (p2.length ? ' — ' + p2.join('; ') : ''), page);
    }

    // No live composition: an unsaved All libraries choice changes no library row's Default.
    const c0 = cases[0];
    await putAdmin(t, withTable(t, neutral, c0.admin), c0.what);
    await openPersonPage(t, page);
    await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries], 3000);
    const f = await pickAll(page, null, picksFor({ 'All libraries': ['lang:en', 'off'] }));
    await sleep(t.settle);
    const g3 = await defaultLabels(page, null);
    const p3 = labelProblems(g3, { 'All libraries': c0.all });
    await clickMode(page, null, SPEC.modePer);
    await waitMode(page, null, SPEC.modePer, libs);
    const g4 = await defaultLabels(page, null);
    const p4 = labelProblems(g4, c0.rows);
    await t.check(!f.length && !p3.length && !p4.length, 'with All libraries on English / Off (not saved), the All libraries Default still reads "' + SPEC.serverEach + '" and every library row keeps its server default (no live composition)'
        + (f.concat(p3, p4).length ? ' — ' + f.concat(p3, p4).join('; ') : ''), page);
    // Throw the unsaved change away, so leaving the page asks nothing.
    try {
        await page.click(pgButton, { args: ['.bf-savebar', 'Discard'], what: 'Discard in the save bar', timeout: 3000 });
        await page.click(pgDialogButton, { args: ['Discard'], what: 'Discard in the confirmation', timeout: 3000 });
    } catch (e) {
        // nothing to discard, or no confirmation
    }
    // The API's own view of the same thing (what the labels come from).
    const d = (await getMe(t)).Defaults || {};
    const rows = d.Libraries || {};
    const key = (name) => Object.keys(rows).find((k) => compactId(k) === compactId(t.ids.libraries[name]));
    const animeRow = rows[key('Anime')] || {};
    const tvRow = rows[key('TV Shows')] || {};
    const animeAudio = ((animeRow.AudioChain || [])[0] || {});
    const tvAudio = ((tvRow.AudioChain || [])[0] || {});
    await t.check(animeAudio.Language === 'ja' && tvAudio.Kind === 'VideoDefault',
        'GET Me/Settings Defaults: Anime audio ' + canon(animeAudio) + ', TV Shows audio ' + canon(tvAudio) + ' (want Japanese and the video default: the admin\'s per-library defaults, without its ignored Everyday)', null);
}

// ───────────────────────────── 7. Server effect of each mode ─────────────────────────────

async function choiceOf(t, itemId, accept) {
    let last = null;
    for (let i = 0; i < 5; i++) {
        last = await t.api.get('/Babelfin/Me/Choice', { itemId: itemId });
        if (last.status === 200 && accept(last.body)) {
            return last;
        }
        await sleep(1000);
    }
    return last;
}

function choiceText(r) {
    if (!r || r.status !== 200) {
        return 'HTTP ' + (r && r.status);
    }
    if (!Array.isArray(r.body) || !r.body.length) {
        return 'nothing (empty)';
    }
    const c = r.body[0];
    return (c.AudioLanguage || '?') + ' #' + c.AudioIndex + ' / ' + (c.SubtitleLanguage ? c.SubtitleLanguage + ' (' + c.SubtitleKind + ') #' + c.SubtitleIndex : 'no subtitles') + ', outcome ' + c.Outcome;
}

async function testServerEffect(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    const libs = libraryNames(kept.me);
    const neutral = adminNeutral(kept.admin);
    const adminPer = withTable(t, neutral, { modeField: 'PerLibraryDefaults', perLibrary: true, libs: { Anime: ['lang:ja', 'lang:en'], Movies: ['lang:en', 'lang:it'] } });
    await putAdmin(t, adminPer, 'per-library admin defaults: Anime Japanese / English, Movies English / Italian');
    await t.check(true, 'admin defaults set through PUT Admin/Settings: per library, Anime Japanese / English, Movies English / Italian', null);
    await putMe(t, withTable(t, kept.snapshot, { perLibrary: null }), 'nothing chosen');
    const movieItem = await t.api.get('/Users/' + t.api.userId + '/Items/' + t.ids.movieId);
    const movieStreams = (movieItem.body && movieItem.body.MediaSources && movieItem.body.MediaSources[0] && movieItem.body.MediaSources[0].MediaStreams) || [];
    const movieDefaultAudio = movieStreams.find((x) => x.Type === 'Audio' && x.IsDefault) || movieStreams.find((x) => x.Type === 'Audio') || {};
    const noSubs = (c) => !c.SubtitleLanguage && (c.SubtitleIndex == null || c.SubtitleIndex < 0);

    await openPersonPage(t, page);
    if (!(await requireMode(t, page, null, 'person page'))) {
        return;
    }
    // 7a. "Same for every library": English / Off everywhere, over the admin's Anime Japanese.
    const sameOk = await ensureMode(page, null, SPEC.modeSame, [SPEC.allLibraries]);
    const f1 = await pickAll(page, null, picksFor({ 'All libraries': ['lang:en', 'off'] }));
    await sleep(t.settle);
    await t.check(sameOk && !f1.length, '"' + SPEC.modeSame + '" with English / Off picked on the person page' + (f1.length ? ' — ' + f1.join('; ') : ''), page);
    if (sameOk && !f1.length) {
        await savePersonPage(t, page);
        const ep = await choiceOf(t, t.ids.episodeId, (b) => b && b[0] && b[0].AudioLanguage === 'en' && noSubs(b[0]));
        const e0 = (ep.body || [])[0] || {};
        await t.check(ep.status === 200 && e0.AudioLanguage === 'en' && noSubs(e0) && e0.Outcome !== 'PassedThrough',
            'Me/Choice for the Anime episode (Astercoil Test S01E01): ' + choiceText(ep) + ' (want English, no subtitles, chosen by Babelfin: the person\'s every-library choice outranks the admin\'s Anime Japanese / English)', null);
        const mv = await choiceOf(t, t.ids.movieId, (b) => b && b[0] && b[0].AudioLanguage === 'en' && noSubs(b[0]));
        const m0 = (mv.body || [])[0] || {};
        await t.check(mv.status === 200 && m0.AudioLanguage === 'en' && noSubs(m0),
            'Me/Choice for the movie (Berlin Test): ' + choiceText(mv) + ' (want English, no subtitles over the admin\'s Movies English / Italian)', null);
    }

    // 7b. "Choose for each library": Anime Japanese / English; Movies left on Default gets the admin's Movies default.
    await clickMode(page, null, SPEC.modePer);
    const perOk = (await waitMode(page, null, SPEC.modePer, libs)).ok;
    const f2 = await pickAll(page, null, picksFor({ Anime: ['lang:ja', 'lang:en'] }));
    await sleep(t.settle);
    await t.check(perOk && !f2.length, 'switched to "' + SPEC.modePer + '" and picked Anime Japanese / English (Movies and TV Shows left on Default)' + (f2.length ? ' — ' + f2.join('; ') : ''), page);
    if (perOk && !f2.length) {
        await savePersonPage(t, page);
        const ep = await choiceOf(t, t.ids.episodeId, (b) => b && b[0] && b[0].AudioLanguage === 'ja');
        const e0 = (ep.body || [])[0] || {};
        await t.check(ep.status === 200 && e0.AudioLanguage === 'ja' && e0.SubtitleLanguage === 'en' && String(e0.SubtitleKind).toLowerCase() === 'full',
            'Me/Choice for the Anime episode: ' + choiceText(ep) + ' (want ja / en full)', null);
        const mv = await choiceOf(t, t.ids.movieId, (b) => b && b[0] && b[0].SubtitleLanguage === 'it');
        const m0 = (mv.body || [])[0] || {};
        await t.check(mv.status === 200 && m0.AudioLanguage === 'en' && m0.SubtitleLanguage === 'it',
            'Me/Choice for the movie with Movies on Default: ' + choiceText(mv) + ' (want the admin\'s Movies default, en / it; the person\'s stored English / Off for every library is ignored in this mode)', null);
        // The existing ⋮ dialog reports the same Japanese / English library choice.
        const episodeDialog = await openItemMenuDialog(t, page, t.ids.episodeId);
        await t.check(!!episodeDialog && /Japanese/.test(episodeDialog.fields[0].text) && /English/.test(episodeDialog.fields[1].text),
            'the episode page ⋮ dialog defaults to Japanese / English (' + JSON.stringify(episodeDialog && episodeDialog.fields.map(f => f.text)) + ')', page);
        await escapeBfDialog(page, DETAIL_PAGE + ' .btnMoreCommands');
        // No admin Movies default: the movie is left to the video.
        await putAdmin(t, withTable(t, neutral, { modeField: 'PerLibraryDefaults', perLibrary: true, libs: { Anime: ['lang:ja', 'lang:en'] } }), 'admin Movies default removed');
        const passed = (b) => Array.isArray(b) && (b.length === 0 || b.every((c) => c.Outcome === 'PassedThrough' || c.Outcome === 'None'));
        const mv2 = await choiceOf(t, t.ids.movieId, passed);
        const v0 = (mv2.body || [])[0] || {};
        await t.check(passed(mv2.body) && (!mv2.body.length || v0.AudioIndex === movieDefaultAudio.Index),
            'with no admin Movies default, Me/Choice for the movie leaves it to the video: ' + choiceText(mv2) + ' (want PassedThrough or nothing, the file\'s default audio #' + movieDefaultAudio.Index + ' ' + movieDefaultAudio.Language + ')', null);
    }

    // 7c. Everything back to Default in both modes: Babelfin leaves the tracks to the video.
    await putAdmin(t, neutral, 'neutral');
    await openPersonPage(t, page);
    const reset = [];
    if (await ensureMode(page, null, SPEC.modePer, libs)) {
        reset.push(...(await pickAll(page, null, picksFor({ Anime: ['', ''] }))));
    }
    if (await ensureMode(page, null, SPEC.modeSame, [SPEC.allLibraries])) {
        reset.push(...(await pickAll(page, null, picksFor({ 'All libraries': ['', ''] }))));
    }
    await sleep(t.settle);
    const sr = await snap(page, null);
    if (sr.saveBar && sr.saveBar.visible) {
        await savePersonPage(t, page);
    }
    const neutralChoice = (b) => Array.isArray(b) && (b.length === 0 || b.every((c) => c.Outcome === 'None' || c.Outcome === 'PassedThrough'));
    const ep2 = await choiceOf(t, t.ids.episodeId, neutralChoice);
    const mv3 = await choiceOf(t, t.ids.movieId, neutralChoice);
    await t.check(!reset.length && neutralChoice(ep2.body) && neutralChoice(mv3.body),
        'with every row back on Default (both modes) and no admin defaults, Me/Choice leaves the tracks to the video: episode ' + choiceText(ep2) + ', movie ' + choiceText(mv3) + (reset.length ? ' — ' + reset.join('; ') : ''), null);
}

// ───────────────────────────── 8. Advanced: collapsible groups ─────────────────────────────

async function sectionInfo(page, scope, title) {
    return page.evaluate(pgSection, scope || null, title);
}

/** Opens (or closes) a section by clicking its heading and waits until it is in that state with its groups drawn (count steady). */
async function setSection(page, scope, title, open) {
    const now = await sectionInfo(page, scope, title);
    if (!now.found) {
        return now;
    }
    if (now.expanded !== open) {
        await page.click(pgDisclosureEl, { args: [scope || null, title, null], what: 'the "' + title + '" heading' });
    }
    const deadline = Date.now() + 15000;
    let lastCount = -1;
    let steadySince = Date.now();
    for (;;) {
        const s = await sectionInfo(page, scope, title);
        if (s.groups.length !== lastCount) {
            lastCount = s.groups.length;
            steadySince = Date.now();
        }
        const steady = Date.now() - steadySince >= 800;
        if (s.expanded === open && (!open || (s.groups.length > 0 && steady) || (steady && Date.now() - steadySince >= 3000))) {
            return s;
        }
        if (Date.now() > deadline) {
            return s;
        }
        await sleep(200);
    }
}

async function groupInfo(page, scope, section, title) {
    return page.evaluate(pgGroup, scope || null, section, title);
}

/** Clicks a group heading (or presses a key on it) and waits for the wanted state; returns the group's description. */
async function toggleGroup(page, scope, section, title, how, wantOpen) {
    if (how === 'click') {
        try {
            await page.click(pgDisclosureEl, { args: [scope || null, title, section], what: 'the "' + title + '" group heading', timeout: 4000 });
        } catch (e) {
            return { found: false, why: e.message };
        }
    } else {
        const ok = await page.evaluate(pgDisclosureFocus, scope || null, title, section);
        if (!ok) {
            return { found: false, why: 'could not focus' };
        }
        await page.press(how);
    }
    const deadline = Date.now() + 4000;
    for (;;) {
        const g = await groupInfo(page, scope, section, title);
        if (!g.found || (g.expanded === wantOpen && g.regionShown === wantOpen) || Date.now() > deadline) {
            return g;
        }
        await sleep(100);
    }
}

function groupState(g) {
    return g.found ? 'aria-expanded ' + g.expanded + ', content ' + (g.regionShown ? 'shown' : 'hidden') : 'not found';
}

/**
 * The checks for one collapsible section and its groups. o: { where, scope, section, groups (wanted names, in order),
 * contents ({ group: [text or RegExp] }), reload() }.
 */
async function disclosureChecks(t, page, o) {
    let sec = await sectionInfo(page, o.scope, o.section);
    if (!sec.found) {
        await t.check(false, o.where + ': a collapsible "' + o.section + '" section (not found; its group checks are skipped)', page);
        return false;
    }
    await t.check(!sec.expanded, o.where + ': "' + o.section + '" starts collapsed (' + sec.kind + ', expanded ' + sec.expanded + ')', page);
    sec = await setSection(page, o.scope, o.section, true);
    const names = sec.groups.map((g) => g.name);
    await t.check(sec.expanded && names.join('|') === o.groups.join('|'),
        o.where + ': "' + o.section + '" opens with the groups ' + o.groups.map((g) => '"' + g + '"').join(', ') + ' (found heading buttons: ' + (names.length ? names.map((g) => '"' + g + '"').join(', ') : 'none')
        + (sec.details.length ? '; <details> inside: ' + sec.details.map((g) => '"' + g + '"').join(', ') : '') + ')', page);
    if (!sec.groups.length) {
        return false;
    }
    const shape = sec.groups.filter((g) => !(g.kind === 'button' && g.heading && !g.expanded && !g.regionShown && g.controls && g.regionFound))
        .map((g) => '"' + g.name + '": ' + [g.kind !== 'button' ? g.kind : null, g.heading ? null : 'not inside a heading', g.expanded ? 'starts open' : null,
            g.regionShown ? 'content shown' : null, g.controls ? null : 'no aria-controls', g.regionFound ? null : 'its content not found'].filter(Boolean).join(', '));
    await t.check(shape.length === 0, o.where + ': every group is a heading button (' + uniq(sec.groups.map((g) => g.headingTag || 'no heading')).join(', ') + ') with aria-expanded and aria-controls, closed by default with its content hidden'
        + (shape.length ? ' — ' + shape.join('; ') : ''), page);
    // Mouse.
    const mouse = [];
    for (const g of sec.groups) {
        const opened = await toggleGroup(page, o.scope, o.section, g.name, 'click', true);
        const closed = await toggleGroup(page, o.scope, o.section, g.name, 'click', false);
        if (!(opened.expanded && opened.regionShown && !closed.expanded && !closed.regionShown)) {
            mouse.push('"' + g.name + '": after one click ' + groupState(opened) + ', after two ' + groupState(closed));
        }
    }
    await t.check(mouse.length === 0, o.where + ': a click opens each group (aria-expanded true, content shown) and a second click closes it' + (mouse.length ? ' — ' + mouse.join('; ') : ''), page);
    // Keyboard.
    const keys = [];
    for (const g of sec.groups) {
        const opened = await toggleGroup(page, o.scope, o.section, g.name, 'Enter', true);
        const closed = await toggleGroup(page, o.scope, o.section, g.name, 'Space', false);
        if (!(opened.expanded && opened.regionShown && opened.focused && !closed.expanded && !closed.regionShown)) {
            keys.push('"' + g.name + '": after Enter ' + groupState(opened) + (opened.focused ? '' : ', focus lost') + ', after Space ' + groupState(closed));
        }
    }
    await t.check(keys.length === 0, o.where + ': Enter on a focused group heading opens it and Space closes it, focus staying on the heading' + (keys.length ? ' — ' + keys.join('; ') : ''), page);
    // What each group holds.
    if (o.contents) {
        const lacking = [];
        for (const name of Object.keys(o.contents)) {
            if (names.indexOf(name) < 0) {
                lacking.push('"' + name + '": no such group');
                continue;
            }
            const g = await toggleGroup(page, o.scope, o.section, name, 'click', true);
            const texts = (g.texts || []).map(apos);
            const miss = o.contents[name].filter((w) => !texts.some((x) => (w instanceof RegExp ? w.test(x) : x === apos(w))));
            if (miss.length) {
                lacking.push('"' + name + '" lacks ' + miss.map(String).join(', '));
            }
            if (o.radiogroupIn && o.radiogroupIn === name && (g.radiogroups || []).indexOf(SPEC.modeGroup) < 0) {
                lacking.push('"' + name + '" has no "' + SPEC.modeGroup + '" radiogroup (radiogroups: ' + ((g.radiogroups || []).join(', ') || 'none') + ')');
            }
            await toggleGroup(page, o.scope, o.section, name, 'click', false);
        }
        await t.check(lacking.length === 0, o.where + ': the groups hold ' + Object.keys(o.contents).map((k) => k + ' (' + o.contents[k].map((x) => (x instanceof RegExp ? x.source.replace(/^\^/, '') + '…' : x)).join(', ') + ')').join('; ')
            + (lacking.length ? ' — ' + lacking.join('; ') : ''), page);
    }
    // Remembered across a reload (in localStorage).
    const pick = sec.groups.length > 2 ? [sec.groups[1].name, sec.groups[sec.groups.length - 1].name] : [sec.groups[0].name];
    const store0 = await page.evaluate(pgStorage);
    for (const name of pick) {
        await toggleGroup(page, o.scope, o.section, name, 'click', true);
    }
    const store1 = await page.evaluate(pgStorage);
    const changedKeys = Object.keys(store1).filter((k) => store1[k] !== store0[k]);
    await o.reload();
    let after = await sectionInfo(page, o.scope, o.section);
    const deadline = Date.now() + 8000;
    while (after.expanded && after.groups.length < sec.groups.length && Date.now() < deadline) {
        await sleep(200);
        after = await sectionInfo(page, o.scope, o.section);
    }
    const wrong = sec.groups.map((g) => g.name).filter((n) => {
        const x = after.groups.find((y) => y.name === n);
        return !x || x.expanded !== (pick.indexOf(n) >= 0);
    });
    await t.check(after.expanded && wrong.length === 0, o.where + ': after a reload "' + o.section + '" is still open and exactly ' + pick.map((n) => '"' + n + '"').join(' and ') + ' are open'
        + (after.expanded ? '' : ' ("' + o.section + '" is closed)') + (wrong.length ? ' — wrong: ' + wrong.join(', ') : ''), page);
    await t.check(changedKeys.length > 0, o.where + ': the open groups are remembered in localStorage (' + (changedKeys.length ? changedKeys.map((k) => k + '=' + store1[k]).join(', ') : 'no key changed') + ')', page);
    for (const name of pick) {
        await toggleGroup(page, o.scope, o.section, name, 'click', false);
    }
    await o.reload();
    after = await sectionInfo(page, o.scope, o.section);
    while (after.expanded && after.groups.length < sec.groups.length && Date.now() < deadline + 8000) {
        await sleep(200);
        after = await sectionInfo(page, o.scope, o.section);
    }
    const stillOpen = after.groups.filter((g) => g.expanded).map((g) => g.name);
    await t.check(after.groups.length === sec.groups.length && stillOpen.length === 0, o.where + ': closed again, they stay closed after another reload' + (stillOpen.length ? ' — open: ' + stillOpen.join(', ') : ''), page);
    return true;
}

/** Opens every group of a section (so lazily drawn content exists) and returns their names. */
async function openAllGroups(page, scope, section) {
    const sec = await setSection(page, scope, section, true);
    for (const g of sec.groups || []) {
        if (!g.expanded) {
            await toggleGroup(page, scope, section, g.name, 'click', true);
        }
    }
    return (sec.groups || []).map((g) => g.name);
}

async function testAdvanced(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    const noFlags = clone(kept.snapshot);
    noFlags.BurnedIn = [];
    await putMe(t, noFlags, 'no burned-in flags');

    // 8a. The person page.
    await openPersonPage(t, page);
    const cleared = await page.evaluate(pgClearBabelfinStorage);
    t.verbose('cleared localStorage keys: ' + cleared.join(', '));
    await reloadPersonPage(t, page);
    const personOk = await disclosureChecks(t, page, {
        where: 'person page', scope: null, section: 'Advanced', groups: SPEC.personGroups, contents: SPEC.personGroupContents,
        reload: () => reloadPersonPage(t, page)
    });
    if (personOk) {
        await setSection(page, null, 'Advanced', true);
        const sw = await page.evaluate(function (title, label) {
            var h = window.__bfE2E;
            var ctrl = h.disclosure(null, title);
            var region = ctrl && h.regionOf(ctrl);
            if (!region) {
                return { found: false };
            }
            var hit = Array.prototype.find.call(region.querySelectorAll('label, span, div, p, h3, h4'), function (n) { return h.text(n) === label && h.vis(n); });
            var groups = h.groups(region);
            var inGroup = !!hit && groups.some(function (g) { var r = h.regionOf(g); return r && r.contains(hit); });
            var first = groups[0];
            return { found: !!hit, inGroup: inGroup, before: !!hit && !!first && !!(hit.compareDocumentPosition(first) & Node.DOCUMENT_POSITION_FOLLOWING) };
        }, 'Advanced', SPEC.enabledSwitch);
        await t.check(sw.found && !sw.inGroup && sw.before, 'person page: "' + SPEC.enabledSwitch + '" sits at the top of Advanced, outside any group (found ' + sw.found + ', inside a group ' + sw.inGroup + ', before the first group ' + sw.before + ')', page);
        // The Burned-in subtitles group shows only when there are flags.
        const flag = await t.api.post('/Babelfin/Me/BurnedIn/' + compactId(t.ids.episodeId), { Languages: ['en'], Scope: 'Title' });
        if (flag.status === 200 || flag.status === 204) {
            t.defer('burned-in flag removed', async () => { await t.api.del('/Babelfin/Me/BurnedIn/' + compactId(t.ids.episodeId), { scope: 'Title' }); });
            await reloadPersonPage(t, page);
            const withFlag = await setSection(page, null, 'Advanced', true);
            const want = SPEC.personGroups.slice(0, SPEC.personGroups.length - 1).concat([SPEC.burnedInGroup, SPEC.startOver]);
            const got = withFlag.groups.map((g) => g.name);
            await t.check(got.join('|') === want.join('|'), 'person page: with a burned-in flag stored, the groups are ' + want.map((g) => '"' + g + '"').join(', ') + ' (' + got.map((g) => '"' + g + '"').join(', ') + ')', page);
            await t.api.del('/Babelfin/Me/BurnedIn/' + compactId(t.ids.episodeId), { scope: 'Title' });
            await reloadPersonPage(t, page);
        } else {
            t.note('a burned-in flag could not be stored through the API (' + flag.status + '); the "' + SPEC.burnedInGroup + '" case is skipped');
        }
        // Start over asks first; only its Cancel path runs here.
        const beforeReset = (await getMe(t)).Settings;
        const dialogs0 = page.dialogs.length;
        await setSection(page, null, 'Advanced', true);
        const so = await toggleGroup(page, null, 'Advanced', SPEC.startOver, 'click', true);
        const soButton = so.found ? await page.evaluate(function (title, group) {
            var h = window.__bfE2E;
            var outer = h.disclosure(null, title);
            var r = outer ? h.regionOf(outer) : null;
            var ctrl = r ? h.disclosure(r, group) : null;
            var region = ctrl ? h.regionOf(ctrl) : null;
            var b = region ? Array.prototype.find.call(region.querySelectorAll('button'), function (x) { return h.vis(x) && x !== ctrl; }) : null;
            return b ? h.text(b) : null;
        }, 'Advanced', SPEC.startOver) : null;
        let asked = null;
        if (soButton) {
            await page.click(pgGroupButtonEl, { args: [null, 'Advanced', SPEC.startOver], what: 'the button in "' + SPEC.startOver + '"' });
            asked = await waitDialog(page, 5000);
            if (asked) {
                await answerDialog(page, false);
            }
        }
        await sleep(t.settle);
        const afterReset = (await getMe(t)).Settings;
        const native = page.dialogs.length > dialogs0;
        await t.check(!!asked && !native && same(afterReset, beforeReset), 'person page: the button of the "' + SPEC.startOver + '" group' + (soButton ? ' ("' + soButton + '")' : '') + ' asks for confirmation first, and Cancel keeps every setting'
            + (asked ? ' (' + asked.role + ' "' + asked.title + '")' : '') + (native ? ' (a browser confirm() showed; the test browser accepts those, the settings get put back)' : '')
            + (so.found ? (soButton ? (asked ? '' : ' (no confirmation dialog)') : ' (no button in the group)') : ' (no "' + SPEC.startOver + '" group)'), page);
        if (!same(afterReset, beforeReset)) {
            await restorePersonSnapshot(t, t.api, '/Babelfin/Me/Settings', beforeReset, 'person settings restored after Start over');
        }
        if (so.found) {
            await toggleGroup(page, null, 'Advanced', SPEC.startOver, 'click', false);
        }
        // It stays the last group.
        const groupsNow = (await setSection(page, null, 'Advanced', true)).groups.map((g) => g.name);
        await t.check(groupsNow[groupsNow.length - 1] === SPEC.startOver, 'person page: "' + SPEC.startOver + '" is the last group of Advanced (' + groupsNow.join(', ') + ')', page);
    }
    // Removed from the person page (stored values stay in the file).
    await reloadPersonPage(t, page);
    await openAllGroups(page, null, 'Advanced');
    const goneP = await page.evaluate(pgFindTexts, null, SPEC.personRemoved.map((w) => (w instanceof RegExp ? { re: w.source } : w)));
    await t.check(goneP.scope && goneP.found.length === 0, 'person page: no ' + SPEC.personRemoved.map((w) => '"' + w + '"').join(', ') + ' anywhere (Advanced and all its groups open)'
        + (goneP.found.length ? ' — found: ' + goneP.found.join('; ') : ''), page);
    await page.evaluate(pgClearBabelfinStorage);

    // 8b. The admin page.
    await openAdminPage(t, page);
    await page.evaluate(pgClearBabelfinStorage);
    await reloadAdminPage(t, page);
    await disclosureChecks(t, page, {
        where: 'admin page', scope: ADMIN, section: 'Advanced', groups: SPEC.adminGroups, contents: SPEC.adminGroupContents,
        reload: () => reloadAdminPage(t, page)
    });
    await setSection(page, ADMIN, 'Advanced', false);
    const tools = await toolsTitle(page);
    await disclosureChecks(t, page, {
        where: 'admin page', scope: ADMIN, section: tools, groups: SPEC.toolsGroups, contents: null,
        reload: () => reloadAdminPage(t, page)
    });
    await setSection(page, ADMIN, tools, false);
    // Back up and restore is its own collapsible section, closed at first and remembered.
    let bk = await sectionInfo(page, ADMIN, SPEC.backup);
    const nested = bk.found ? await page.evaluate(function (scope, title, toolsTitle) {
        var h = window.__bfE2E;
        var tl = h.disclosure(scope, toolsTitle);
        var r = tl ? h.regionOf(tl) : null;
        var b = h.disclosure(scope, title);
        return !!(r && b && r.contains(b));
    }, ADMIN, SPEC.backup, tools) : false;
    await t.check(bk.found && !nested && !bk.expanded, 'admin page: "' + SPEC.backup + '" is a collapsible section of its own (not inside ' + tools + '), closed at first (' + (bk.found ? bk.kind + ', expanded ' + bk.expanded + ', inside ' + tools + ' ' + nested : 'not found') + ')', page);
    if (bk.found && !nested) {
        bk = await setSection(page, ADMIN, SPEC.backup, true);
        // Native details toggles dispatch asynchronously. Verify persistence before
        // unloading the document, then still check the actual state after reload.
        await page.waitFor(function () { return localStorage.getItem('babelfin.admin.open.backup') === '1'; },
            { what: 'the opened backup section is persisted before reload' });
        await reloadAdminPage(t, page);
        const again = await sectionInfo(page, ADMIN, SPEC.backup);
        await setSection(page, ADMIN, SPEC.backup, false);
        await page.waitFor(function () { return localStorage.getItem('babelfin.admin.open.backup') === '0'; },
            { what: 'the closed backup section is persisted before reload' });
        await reloadAdminPage(t, page);
        const closed = await sectionInfo(page, ADMIN, SPEC.backup);
        await t.check(bk.expanded && again.expanded && !closed.expanded, 'admin page: "' + SPEC.backup + '" opens with a click, stays open after a reload and stays closed once closed (open ' + bk.expanded + ', after a reload ' + again.expanded + ', closed again ' + !closed.expanded + ')', page);
    }
    await reloadAdminPage(t, page);
    await openEverything(page, true);
    await sleep(1500);
    const goneA = await page.evaluate(pgFindTexts, ADMIN, SPEC.adminRemoved.map((w) => (w instanceof RegExp ? { re: w.source } : w)));
    await t.check(goneA.scope && goneA.found.length === 0, 'admin page: no ' + SPEC.adminRemoved.map((w) => (w instanceof RegExp ? '"' + w.source.replace(/^\^/, '') + '…"' : '"' + w + '"')).join(', ') + ' anywhere (Advanced, Tools and all their groups open)'
        + (goneA.found.length ? ' — found: ' + goneA.found.join('; ') : ''), page);
    await page.evaluate(pgClearBabelfinStorage);
}

// ───────────────────────────── 9. Admin page ─────────────────────────────

const ADMIN = '#babelfinConfigPage:not(.hide)';
const LIBS = ADMIN + ' [data-section="libraries"]';
const EDITOR = ADMIN + ' [data-section="people"] .bf-editor-row';

async function openAdminPage(t, page) {
    // Already there: navigating to the same #hash changes nothing, so reload to draw the page from fresh data.
    if (/configurationpage\?name=Babelfin/i.test(page.url || '')) {
        await reloadAdminPage(t, page);
        return;
    }
    await page.navigate(t.url('/web/#/configurationpage?name=Babelfin'));
    await page.waitFor(function (scope) {
        var p = document.querySelector(scope);
        return !!(p && p.querySelector('[data-section="libraries"] select') && p.querySelector('[data-section="people"] tr[data-user]'));
    }, { timeout: 30000, what: 'the admin page with its Libraries and People tables' }, ADMIN);
    await page.evaluate(pgInstallChangeLog);
}

async function reloadAdminPage(t, page) {
    await page.reload();
    await waitWebReady(t, page);
    await page.waitFor(function (scope) {
        var p = document.querySelector(scope);
        return !!(p && p.querySelector('[data-section="libraries"] select') && p.querySelector('[data-section="people"] tr[data-user]'));
    }, { timeout: 30000, what: 'the admin page after a reload' }, ADMIN);
    await page.evaluate(pgInstallChangeLog);
}

/** The Tools section's title: "Tools", or "Troubleshooting". */
async function toolsTitle(page) {
    for (const title of ['Tools', 'Troubleshooting']) {
        if ((await sectionInfo(page, ADMIN, title)).found) {
            return title;
        }
    }
    return 'Tools';
}

/** Clicks the admin save bar's Save and waits for the bar to hide. */
async function saveAdminPage(t, page) {
    await page.click(function (scope) { return document.querySelector(scope + ' #bfSave'); }, { args: [ADMIN], what: 'the admin Save button' });
    await page.waitFor(function (scope) { var b = document.querySelector(scope + ' #bfSaveBar'); return b && (b.classList.contains('hide') || b.hidden || getComputedStyle(b).display === 'none'); }, { timeout: 15000, what: 'the admin save to finish' }, ADMIN);
}

/** Opens the People editor for a person (in `tab`) and waits for its table. */
async function openEditor(t, tab, userId) {
    // Already on the admin page (an editor may be open): reload, so Edit opens a fresh editor instead of closing the open one.
    if (/configurationpage\?name=Babelfin/i.test(tab.url || '')) {
        await reloadAdminPage(t, tab);
    } else {
        await openAdminPage(t, tab);
    }
    await tab.click(function (scope, id) {
        var row = document.querySelector(scope + ' [data-section="people"] tr[data-user="' + id + '"]');
        return row && Array.prototype.find.call(row.querySelectorAll('button'), function (b) { return b.textContent.trim() === 'Edit'; });
    }, { args: [ADMIN, compactId(userId)], what: 'Edit on ' + t.opts.viewer });
    await tab.waitFor(pgTableReady, { timeout: 20000, what: 'the People editor table' }, EDITOR);
}

/** Waits until the People editor saved and the stored document passes `accept` (the editor has no save bar to watch). */
async function waitStored(t, userPath, accept) {
    for (let i = 0; i < 20; i++) {
        await sleep(500);
        const r = await t.api.get(userPath);
        if (r.status === 200 && accept(r.body)) {
            return r.body;
        }
    }
    return null;
}

async function testAdminPage(t) {
    const page = t.page;
    const adminSnapshot = clone(await getAdmin(t));
    t.defer('admin settings restored', async () => {
        const r = await t.api.put('/Babelfin/Admin/Settings', adminSnapshot);
        const now = await t.api.get('/Babelfin/Admin/Settings');
        const diff = changedPaths(adminSnapshot, now.body).filter((p) => !/^(UpdatedAt|LastSaved)/.test(p));
        await t.check(r.status === 200 && diff.length === 0, 'admin settings put back through the API (' + r.status + (diff.length ? '; still different: ' + diff.slice(0, 6).join(', ') : '') + ')', null);
    });
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    await openAdminPage(t, page);
    await page.evaluate(pgClearBabelfinStorage);
    await reloadAdminPage(t, page);
    const tools = await toolsTitle(page);

    // 9a. What is visible: header with Enabled, Defaults for everyone, People, then collapsed Advanced, Troubleshooting and Back up and restore.
    const blocks = await page.evaluate(pgAdminBlocks, ADMIN);
    const layout = await page.evaluate(function (scope) {
        var p = document.querySelector(scope);
        var dash = p.querySelector('.bf-dash') || p;
        function vis(el) { var r = el.getBoundingClientRect(); var cs = getComputedStyle(el); return r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden'; }
        var blocks = Array.prototype.filter.call(dash.children, vis).map(function (c) {
            var title = c.querySelector('.bf-section-title, h2, h3');
            return c.getAttribute('data-section') || c.getAttribute('data-fold') || (c.classList.contains('bf-head') ? 'header' : (title ? title.textContent.trim().toLowerCase() : c.tagName.toLowerCase() + '.' + c.className));
        });
        var enabled = p.querySelector('#bfEnabled');
        var label = enabled && enabled.closest('label');
        var titles = {};
        Array.prototype.forEach.call(p.querySelectorAll('[data-section]'), function (s) {
            var h = s.querySelector('.bf-section-title, h2, h3');
            titles[s.getAttribute('data-section')] = h ? h.textContent.trim() : null;
        });
        var errors = Array.prototype.filter.call(p.querySelectorAll('.bf-alert-error, [role="alert"]'), function (e) { return vis(e) && e.textContent.trim(); }).map(function (e) { return e.textContent.trim(); });
        return {
            blocks: blocks, titles: titles, errors: errors,
            title: (p.querySelector('.bf-head h2, .bf-head .sectionTitle') || {}).textContent,
            enabled: !!enabled && vis(label || enabled) && /Enabled/.test(label ? label.textContent : ''),
            enabledChecked: enabled ? enabled.checked : null
        };
    }, ADMIN);
    const adv = await sectionInfo(page, ADMIN, 'Advanced');
    const tl = await sectionInfo(page, ADMIN, tools);
    const bk = await sectionInfo(page, ADMIN, SPEC.backup);
    const wantBlocks = ['header'].concat(SPEC.adminSections);
    await t.check(blocks.join('|') === wantBlocks.join('|'), 'admin page shows exactly, in order: ' + wantBlocks.join(', ') + ' (' + blocks.join(', ') + ')', page);
    await t.check(layout.title === 'Babelfin' && layout.enabled, 'the header reads "Babelfin" and has an "Enabled" checkbox (' + layout.title + ', checked ' + layout.enabledChecked + ')');
    const topText = apos(await page.evaluate(function (sel) { var e = document.querySelector(sel); return e ? e.textContent.replace(/\s+/g, ' ') : ''; }, LIBS));
    await t.check(layout.titles.libraries === SPEC.topTitle && topText.indexOf(apos(SPEC.topLine)) >= 0, 'the top table is headed "' + SPEC.topTitle + '" with the line "' + SPEC.topLine + '" (title "' + layout.titles.libraries + '")', page);
    await t.check(layout.titles.people === SPEC.people && adv.found && tl.found, 'section titles: People, Advanced, ' + tools + ' (' + JSON.stringify(layout.titles) + ', Advanced ' + adv.found + ', ' + tools + ' ' + tl.found + ')');
    await t.check(adv.found && !adv.expanded && tl.found && !tl.expanded && (!bk.found || !bk.expanded), 'Advanced, ' + tools + ' and ' + SPEC.backup + ' start collapsed (Advanced ' + (adv.found ? (adv.expanded ? 'open' : 'closed') : 'missing') + ', ' + tools + ' ' + (tl.found ? (tl.expanded ? 'open' : 'closed') : 'missing')
        + ', ' + SPEC.backup + ' ' + (bk.found ? (bk.expanded ? 'open' : 'closed') : 'missing') + ')');
    // The groups inside, in order: Advanced (Apps, New users, Track recognition, then the Restore defaults button), Troubleshooting (Check a title, Activity).
    const advOpen = await setSection(page, ADMIN, 'Advanced', true);
    const restoreAt = await page.evaluate(pgSectionButton, ADMIN, 'Advanced', SPEC.restore);
    await t.check(advOpen.groups.map((g) => g.name).join('|') === SPEC.adminGroups.join('|') && restoreAt.found && restoreAt.afterGroups && !restoreAt.inGroup && !restoreAt.later.length,
        'Advanced holds ' + SPEC.adminGroups.join(', ') + ', then "' + SPEC.restore + '" (groups: ' + advOpen.groups.map((g) => g.name).join(', ') + '; ' + (restoreAt.found ? 'the button after them ' + restoreAt.afterGroups + ', last ' + !restoreAt.later.length : restoreAt.why) + ')', page);
    await setSection(page, ADMIN, 'Advanced', false);
    const tlOpen = await setSection(page, ADMIN, tools, true);
    await t.check(tlOpen.groups.map((g) => g.name).join('|') === SPEC.toolsGroups.join('|'), tools + ' holds ' + SPEC.toolsGroups.join(', ') + ' (' + tlOpen.groups.map((g) => g.name).join(', ') + ')', page);
    await setSection(page, ADMIN, tools, false);
    await t.check(layout.errors.length === 0, 'no error line on the admin page' + (layout.errors.length ? ': ' + layout.errors.join(' | ') : ''));
    const version = adminSnapshot.ConfigVersion;
    await t.check(version === SPEC.configVersion, 'GET Admin/Settings: settings version ' + SPEC.configVersion + ' (ConfigVersion ' + version + ')', null);

    // 9b. The Libraries table: the either/or control stores PerLibraryDefaults.

    const adminLibs = (await t.api.get('/Babelfin/Admin/Libraries')).body || [];
    const libRows = adminLibs.map((l) => l.Name);
    const languages = (await t.api.get('/Babelfin/Languages')).body || [];
    const animeId = t.ids.libraries.Anime;
    const neutral = adminNeutral(adminSnapshot);
    await putAdmin(t, neutral, 'neutral, automatic mode');
    await reloadAdminPage(t, page);
    const info = await requireMode(t, page, LIBS, 'admin Libraries table');
    if (info) {
        await t.check(info.role === 'radiogroup' && info.name === SPEC.modeGroup && info.radios.map((r) => r.label).join('|') === [SPEC.modeSame, SPEC.modePer].join('|'),
            'admin Libraries table: a radiogroup "' + SPEC.modeGroup + '" with "' + SPEC.modeSame + '" and "' + SPEC.modePer + '" (role ' + info.role + ', name "' + info.name + '", radios ' + info.radios.map((r) => '"' + r.label + '"').join(', ') + ')', page);
        let r = await waitMode(page, LIBS, SPEC.modeSame, [SPEC.allLibraries], 3000);
        await t.check(r.ok, 'admin Libraries table, automatic with no library choice: "' + SPEC.modeSame + '", one row "All libraries" (' + modeText(r) + ')', page);
        await putAdmin(t, withTable(t, neutral, { modeField: 'PerLibraryDefaults', perLibrary: null, libs: { Anime: [undefined, 'off'] } }), 'automatic, Anime subtitles set');
        await reloadAdminPage(t, page);
        r = await waitMode(page, LIBS, SPEC.modePer, libRows, 3000);
        await t.check(r.ok, 'admin Libraries table, automatic with Anime subtitles stored: "' + SPEC.modePer + '", one row per library (' + modeText(r) + ')', page);
        // Contract checks in both modes (every Default on the admin page is "Video default").
        await putAdmin(t, withTable(t, neutral, { modeField: 'PerLibraryDefaults', perLibrary: false }), 'every library');
        await reloadAdminPage(t, page);
        await waitMode(page, LIBS, SPEC.modeSame, [SPEC.allLibraries], 3000);
        await contractChecks(t, page, LIBS, { where: 'admin Libraries table, "' + SPEC.modeSame + '"', rows: [SPEC.allLibraries], languages: languages, admin: true, mine: () => [], storedLayer: () => null });
        await clickMode(page, LIBS, SPEC.modePer);
        await waitMode(page, LIBS, SPEC.modePer, libRows);
        await contractChecks(t, page, LIBS, { where: 'admin Libraries table, "' + SPEC.modePer + '"', rows: libRows, languages: languages, admin: true, mine: () => [], storedLayer: () => null });
        // Keyboard on the admin radiogroup.
        await reloadAdminPage(t, page);
        const keySteps = [];
        if (await page.evaluate(pgModeFocus, LIBS, SPEC.modeGroup, SPEC.modeSame, SPEC.modeSame)) {
            await page.press('ArrowRight');
            const k1 = await waitMode(page, LIBS, SPEC.modePer, libRows, 3000);
            keySteps.push('ArrowRight → ' + (k1.ok ? 'ok' : modeText(k1)));
            await page.press('ArrowLeft');
            const k2 = await waitMode(page, LIBS, SPEC.modeSame, [SPEC.allLibraries], 3000);
            keySteps.push('ArrowLeft → ' + (k2.ok ? 'ok' : modeText(k2)));
        } else {
            keySteps.push('could not focus the radio');
        }
        await t.check(keySteps.every((x) => / ok$/.test(x)), 'admin Libraries table: the arrow keys switch the mode (' + keySteps.join('; ') + ')', page);
        // Cancel throws a change and a mode switch away.
        await reloadAdminPage(t, page);
        const before0 = await getAdmin(t);
        await clickMode(page, LIBS, SPEC.modePer);
        await waitMode(page, LIBS, SPEC.modePer, libRows);
        const cp = await pickAll(page, LIBS, [{ label: SPEC.audioFor('Movies'), value: 'lang:fr' }]);
        await sleep(t.settle);
        const c1 = await snap(page, LIBS, null, ADMIN + ' #bfSaveBar');
        let cancelled = false;
        if (!cp.length && c1.saveBar && c1.saveBar.visible) {
            await page.click(function (scope) { return document.querySelector(scope + ' #bfDiscard'); }, { args: [ADMIN], what: 'the admin Cancel button' });
            try {
                await page.click(pgDialogButton, { args: ['OK'], what: 'OK in the confirmation', timeout: 2000 });
            } catch (e) {
                // no confirmation
            }
            const back = await waitMode(page, LIBS, SPEC.modeSame, [SPEC.allLibraries], 4000);
            const c2 = await snap(page, LIBS, null, ADMIN + ' #bfSaveBar');
            cancelled = back.ok && c2.saveBar && !c2.saveBar.visible && same(await getAdmin(t), before0);
        }
        await t.check(cancelled, 'admin Libraries table: a mode switch and a change (Audio for Movies French) show the save bar, and Cancel puts the mode back, hides the bar and saves nothing' + (cp.length ? ' — ' + cp.join('; ') : ''), page);
        // Saving stores exactly PerLibraryDefaults plus the edited fields; the other mode's values stay.
        await putAdmin(t, neutral, 'neutral, automatic mode');
        await reloadAdminPage(t, page);
        const a0 = await getAdmin(t);
        await clickMode(page, LIBS, SPEC.modePer);
        await waitMode(page, LIBS, SPEC.modePer, libRows);
        const lp = picksFor({ Anime: ['lang:ja', 'lang:en'] });
        const lf = await pickAll(page, LIBS, lp);
        await sleep(t.settle);
        let a1 = a0;
        if (!lf.length) {
            await saveAdminPage(t, page);
            a1 = await getAdmin(t);
        }
        await t.check(a1.PerLibraryDefaults === true, 'admin Libraries table: Save stores PerLibraryDefaults true (' + a1.PerLibraryDefaults + ')', page);
        let stray = strayPaths(a0, a1, ['PerLibraryDefaults', 'Libraries[' + compactId(animeId) + ']']);
        await t.check(!lf.length && same(tableOf(libraryLayer(a1, animeId)), storedFor('lang:ja', 'lang:en')) && !stray.length,
            'admin Libraries table: Save stores exactly PerLibraryDefaults and Anime ' + canon(storedFor('lang:ja', 'lang:en')) + ' (Anime ' + canon(tableOf(libraryLayer(a1, animeId))) + (stray.length ? '; also changed: ' + stray.join(', ') : '') + (lf.length ? '; ' + lf.join('; ') : '') + ')', page);
        await clickMode(page, LIBS, SPEC.modeSame);
        await waitMode(page, LIBS, SPEC.modeSame, [SPEC.allLibraries]);
        const ep = picksFor({ 'All libraries': [undefined, 'off'] });
        const ef = await pickAll(page, LIBS, ep);
        await sleep(t.settle);
        let a2 = a1;
        if (!ef.length) {
            await saveAdminPage(t, page);
            a2 = await getAdmin(t);
        }
        await t.check(a2.PerLibraryDefaults === false, 'admin Libraries table: switching to "' + SPEC.modeSame + '" and saving stores PerLibraryDefaults false (' + a2.PerLibraryDefaults + ')', page);
        stray = strayPaths(a1, a2, ['PerLibraryDefaults', EVERYDAY_TABLE]);
        await t.check(!ef.length && same(tableOf(a2.Everyday), Object.assign(tableOf(a1.Everyday), storedFor(undefined, 'off'))) && same(tableOf(libraryLayer(a2, animeId)), storedFor('lang:ja', 'lang:en')) && !stray.length,
            'admin Libraries table: it stores exactly PerLibraryDefaults and Everyday subtitles Off, and keeps Anime\'s per-library values (Everyday ' + canon(tableOf(a2.Everyday)) + ', Anime ' + canon(tableOf(libraryLayer(a2, animeId))) + (stray.length ? '; also changed: ' + stray.join(', ') : '') + ')', page);
        await reloadAdminPage(t, page);
        r = await waitMode(page, LIBS, SPEC.modeSame, [SPEC.allLibraries], 3000);
        const s1 = await snap(page, LIBS, null);
        const ok1 = r.ok && !notShown(s1, ep).length;
        await clickMode(page, LIBS, SPEC.modePer);
        const r2 = await waitMode(page, LIBS, SPEC.modePer, libRows);
        const s2 = await snap(page, LIBS, null);
        await t.check(ok1 && r2.ok && !notShown(s2, lp).length, 'admin Libraries table: after a reload "' + SPEC.modeSame + '" shows subtitles Off, and switching to "' + SPEC.modePer + '" shows Anime Japanese / English again'
            + (notShown(s1, ep).concat(notShown(s2, lp)).length ? ' — ' + notShown(s1, ep).concat(notShown(s2, lp)).join('; ') : ''), page);

        // 9c. Every option of the Libraries table with the keyboard, per mode, from no admin table choices.
        await putAdmin(t, withTable(t, neutral, { modeField: 'PerLibraryDefaults', perLibrary: false }), 'every library, no choices');
        await sweepTable(t, { where: 'admin Libraries table', mode: SPEC.modeSame, rows: [SPEC.allLibraries], scope: LIBS, saveBarSel: ADMIN + ' #bfSaveBar', sample: sampleLanguages, open: (tab) => openAdminPage(t, tab) });
        await putAdmin(t, withTable(t, neutral, { modeField: 'PerLibraryDefaults', perLibrary: true }), 'per library, no choices');
        await sweepTable(t, { where: 'admin Libraries table', mode: SPEC.modePer, rows: libRows, scope: LIBS, saveBarSel: ADMIN + ' #bfSaveBar', sample: sampleLanguages, open: (tab) => openAdminPage(t, tab) });
    }

    // 9d. People → Edit on the second user: the same either/or table, stored as their PerLibrary.
    const viewerId = t.ids.viewerId;
    const userPath = '/Babelfin/Admin/Users/' + viewerId + '/Settings';
    const v0 = await t.api.get(userPath);
    if (v0.status !== 200) {
        throw new Error('GET ' + userPath + ': ' + v0.status);
    }
    const viewerSnapshot = clone(v0.body);
    t.defer('second user\'s settings restored', async () => {
        await restorePersonSnapshot(t, t.api, userPath, viewerSnapshot, t.opts.viewer + "'s settings put back through the API");
    });
    const putViewer = async (doc, what) => {
        const r = await t.api.put(userPath, doc);
        if (r.status !== 200) {
            throw new Error('PUT ' + userPath + ' (' + what + '): ' + r.status + ' ' + JSON.stringify(r.body).slice(0, 300));
        }
    };
    const vDefaults = await t.api.get('/Babelfin/Admin/Users/' + viewerId + '/Defaults');
    const vLibs = Object.keys((vDefaults.body && vDefaults.body.Libraries) || {});
    const viewerRows = adminLibs.filter((l) => vLibs.some((k) => compactId(k) === compactId(l.Id))).map((l) => l.Name);
    const vEveryday = viewerSnapshot.Everyday || {};
    const vMine = (kind) => { const l = kind === 'audio' ? (vEveryday.Understood || []) : (vEveryday.Reads || []).concat(vEveryday.Understood || []); return l.filter((c, i) => l.indexOf(c) === i); };
    // The admin's per-library defaults differ, so the editor's labels can be checked too.
    await putAdmin(t, withTable(t, neutral, { modeField: 'PerLibraryDefaults', perLibrary: true, libs: { Anime: ['lang:ja', 'lang:en'], Movies: ['lang:en', 'lang:it'] } }), 'per-library admin defaults');
    // Automatic (PerLibrary null) with a library choice stored for them: the editor opens on "Choose for each library".
    await putViewer(withTable(t, viewerSnapshot, { perLibrary: null, libs: { Anime: [undefined, 'off'] } }), 'automatic, Anime subtitles chosen');
    await openEditor(t, page, viewerId);
    const autoPer = await waitMode(page, EDITOR, SPEC.modePer, viewerRows, 3000);
    await t.check(autoPer.ok, 'People editor (' + t.opts.viewer + ', automatic, Anime subtitles stored): "' + SPEC.modePer + '", one row per library (' + modeText(autoPer) + ')', page);
    await putViewer(withTable(t, viewerSnapshot, { perLibrary: null }), 'automatic, nothing chosen');
    const viewerBase = (await t.api.get(userPath)).body;
    await openEditor(t, page, viewerId);
    const einfo = await requireMode(t, page, EDITOR, 'People editor');
    if (einfo) {
        let r = await waitMode(page, EDITOR, SPEC.modeSame, [SPEC.allLibraries], 3000);
        const l0 = await defaultLabels(page, EDITOR);
        const p0 = labelProblems(l0, { 'All libraries': [SPEC.serverEach, SPEC.serverEach] });
        await t.check(einfo.role === 'radiogroup' && einfo.name === SPEC.modeGroup && r.ok && !p0.length,
            'People editor (' + t.opts.viewer + ', automatic, nothing chosen): a radiogroup "' + SPEC.modeGroup + '" on "' + SPEC.modeSame + '", and All libraries reads "' + SPEC.serverEach + '" (the admin\'s per-library defaults differ)'
            + (r.ok ? '' : ' (' + modeText(r) + ')') + (p0.length ? ' — ' + p0.join('; ') : ''), page);
        await contractChecks(t, page, EDITOR, { where: 'People editor (' + t.opts.viewer + '), "' + SPEC.modeSame + '"', rows: [SPEC.allLibraries], languages: languages, storedLayer: () => null, mine: vMine });
        await clickMode(page, EDITOR, SPEC.modePer);
        r = await waitMode(page, EDITOR, SPEC.modePer, viewerRows);
        const l1 = await defaultLabels(page, EDITOR);
        const p1 = labelProblems(l1, { Anime: ['Default (Japanese)', 'Default (English)'], Movies: ['Default (English)', 'Default (Italian)'], 'TV Shows': [SPEC.videoDefault, SPEC.videoDefault] });
        await t.check(r.ok && !p1.length, 'People editor, "' + SPEC.modePer + '": one row per library with its own server default (Anime "Default (Japanese)" / "Default (English)", Movies "Default (English)" / "Default (Italian)", TV Shows "Video default")'
            + (r.ok ? '' : ' (' + modeText(r) + ')') + (p1.length ? ' — ' + p1.join('; ') : ''), page);
        await contractChecks(t, page, EDITOR, { where: 'People editor (' + t.opts.viewer + '), "' + SPEC.modePer + '"', rows: viewerRows, languages: languages, storedLayer: () => null, mine: vMine });
        // Save stores exactly PerLibrary plus the edited fields; nothing of the admin's own settings changes.
        const adminOwnBefore = (await getMe(t)).Settings;
        const pp = picksFor({ Anime: ['lang:ja', 'lang:en'] });
        const pf = await pickAll(page, EDITOR, pp);
        await sleep(t.settle);
        let saved = null;
        if (!pf.length) {
            await page.click(pgButton, { args: [EDITOR, 'Save'], what: 'Save in the People editor' });
            saved = await waitStored(t, userPath, (b) => !same(tableOf(libraryLayer(b, animeId)), tableOf(libraryLayer(viewerBase, animeId))));
        }
        await t.check(!!saved && same(tableOf(libraryLayer(saved, animeId)), storedFor('lang:ja', 'lang:en')),
            'People editor: "' + SPEC.modePer + '", Anime Japanese / English and Save store Anime ' + canon(storedFor('lang:ja', 'lang:en')) + (pf.length ? ' — ' + pf.join('; ') : '') + (saved ? ' (stored ' + canon(tableOf(libraryLayer(saved, animeId))) + ')' : ' (nothing stored)'), page);
        if (saved) {
            await t.check(saved.PerLibrary === true, 'People editor: Save stores ' + t.opts.viewer + '\'s PerLibrary true (' + saved.PerLibrary + ')', page);
            const stray = strayPaths(viewerBase, saved, ['PerLibrary', libraryTablePaths(animeId), /^Seed\.Confirmed$/]);
            await t.check(stray.length === 0, 'People editor: nothing else in ' + t.opts.viewer + '\'s document changed' + (stray.length ? ': ' + stray.slice(0, 8).join(', ') : ''), page);
            const adminOwnAfter = (await getMe(t)).Settings;
            await t.check(same(adminOwnAfter, adminOwnBefore), "the admin's own settings (GET Me/Settings) did not change" + (same(adminOwnAfter, adminOwnBefore) ? '' : ': ' + changedPaths(adminOwnBefore, adminOwnAfter).slice(0, 8).join(', ')), page);
            await clickMode(page, EDITOR, SPEC.modeSame);
            await waitMode(page, EDITOR, SPEC.modeSame, [SPEC.allLibraries]);
            const sp = picksFor({ 'All libraries': ['lang:en', 'off'] });
            const sf = await pickAll(page, EDITOR, sp);
            await sleep(t.settle);
            let saved2 = null;
            if (!sf.length) {
                await page.click(pgButton, { args: [EDITOR, 'Save'], what: 'Save in the People editor' });
                saved2 = await waitStored(t, userPath, (b) => same(tableOf(b.Everyday), storedFor('lang:en', 'off')));
            }
            await t.check(!!saved2 && same(tableOf(libraryLayer(saved2, animeId)), storedFor('lang:ja', 'lang:en')) && !strayPaths(saved, saved2, ['PerLibrary', EVERYDAY_TABLE]).length,
                'People editor: "' + SPEC.modeSame + '" with English / Off stores Everyday ' + canon(storedFor('lang:en', 'off')) + ' and keeps Anime\'s per-library values' + (sf.length ? ' — ' + sf.join('; ') : '')
                + (saved2 ? (strayPaths(saved, saved2, ['PerLibrary', EVERYDAY_TABLE]).length ? '; also changed: ' + strayPaths(saved, saved2, ['PerLibrary', EVERYDAY_TABLE]).join(', ') : '') : ' (nothing stored)'), page);
            await t.check(!!saved2 && saved2.PerLibrary === false, 'People editor: and PerLibrary false (' + (saved2 && saved2.PerLibrary) + ')', page);
        }
        // 9e. Every option of the People editor's table with the keyboard, per mode.
        await putViewer(withTable(t, viewerSnapshot, { perLibrary: false }), 'every library, nothing chosen');
        await sweepTable(t, {
            where: 'People editor (' + t.opts.viewer + ')', mode: SPEC.modeSame, rows: [SPEC.allLibraries], scope: EDITOR, dirtyButtonScope: EDITOR, sample: sampleLanguages,
            open: (tab) => openEditor(t, tab, viewerId)
        });
        await putViewer(withTable(t, viewerSnapshot, { perLibrary: true }), 'per library, nothing chosen');
        await sweepTable(t, {
            where: 'People editor (' + t.opts.viewer + ')', mode: SPEC.modePer, rows: viewerRows, scope: EDITOR, dirtyButtonScope: EDITOR, sample: sampleLanguages,
            open: (tab) => openEditor(t, tab, viewerId)
        });
    }
}

// ───────────────────────────── 10. Tools: Check a title, Activity, Back up and restore ─────────────────────────────

/** The content element of a group inside a section (page-side helper for the Tools functions below). */
function pgToolRegion(scope, section, group) {
    var h = window.__bfE2E;
    var outer = h.disclosure(scope, section);
    var r = outer ? h.regionOf(outer) : null;
    var ctrl = r ? h.disclosure(r, group) : null;
    return ctrl ? h.regionOf(ctrl) : null;
}

/** Picks the option whose text is `name` in the first dropdown of the group that has one. */
function pgToolPickPerson(scope, section, group, name) {
    var h = window.__bfE2E;
    var outer = h.disclosure(scope, section);
    var r = outer ? h.regionOf(outer) : null;
    var ctrl = r ? h.disclosure(r, group) : null;
    var region = ctrl ? h.regionOf(ctrl) : null;
    if (!region) {
        return { ok: false, why: 'no "' + group + '" content' };
    }
    var sel = Array.prototype.find.call(region.querySelectorAll('select'), function (s) {
        return Array.prototype.some.call(s.options, function (o) { return o.textContent.trim() === name; });
    });
    if (!sel) {
        return { ok: false, why: 'no dropdown offering ' + name + ' (dropdowns: ' + Array.prototype.map.call(region.querySelectorAll('select'), function (s) { return s.getAttribute('aria-label') || s.id || 'select'; }).join(', ') + ')' };
    }
    var opt = Array.prototype.find.call(sel.options, function (o) { return o.textContent.trim() === name; });
    sel.scrollIntoView({ block: 'center' });
    sel.focus();
    sel.value = opt.value;
    sel.dispatchEvent(new Event('input', { bubbles: true }));
    sel.dispatchEvent(new Event('change', { bubbles: true }));
    return { ok: true, label: sel.getAttribute('aria-label') || (sel.labels && sel.labels[0] ? h.text(sel.labels[0]) : null), selects: region.querySelectorAll('select').length };
}

/** Focuses the group's title search box and empties it. */
function pgToolFocusSearch(scope, section, group) {
    var h = window.__bfE2E;
    var outer = h.disclosure(scope, section);
    var r = outer ? h.regionOf(outer) : null;
    var ctrl = r ? h.disclosure(r, group) : null;
    var region = ctrl ? h.regionOf(ctrl) : null;
    var input = region && (region.querySelector('input[type="search"]') || region.querySelector('input[role="combobox"]') || region.querySelector('input[type="text"]'));
    if (!input) {
        return { ok: false };
    }
    input.scrollIntoView({ block: 'center' });
    input.focus();
    input.value = '';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    return { ok: document.activeElement === input, label: input.getAttribute('aria-label') || (input.labels && input.labels[0] ? h.text(input.labels[0]) : null) };
}

/** A search result (button, option or link) in the group whose text matches `pattern` and not `exclude`. */
function pgToolResult(scope, section, group, pattern, exclude) {
    var h = window.__bfE2E;
    var outer = h.disclosure(scope, section);
    var r = outer ? h.regionOf(outer) : null;
    var ctrl = r ? h.disclosure(r, group) : null;
    var region = ctrl ? h.regionOf(ctrl) : null;
    if (!region) {
        return null;
    }
    var re = new RegExp(pattern);
    var ex = exclude ? new RegExp(exclude) : null;
    return Array.prototype.find.call(region.querySelectorAll('[role="option"], button, a, li'), function (n) {
        var t = h.text(n);
        return h.vis(n) && re.test(t) && !(ex && ex.test(t)) && !n.querySelector('button, [role="option"], a');
    }) || null;
}

/** A button in the group whose text matches `pattern` (e.g. a "Check" button). */
function pgToolButton(scope, section, group, pattern) {
    var h = window.__bfE2E;
    var outer = h.disclosure(scope, section);
    var r = outer ? h.regionOf(outer) : null;
    var ctrl = r ? h.disclosure(r, group) : null;
    var region = ctrl ? h.regionOf(ctrl) : null;
    if (!region) {
        return null;
    }
    var re = new RegExp(pattern, 'i');
    return Array.prototype.find.call(region.querySelectorAll('button'), function (b) { return h.vis(b) && !b.disabled && re.test(h.text(b)); }) || null;
}

/** The group's result table: headers, body rows (cells), "Showing x of y", what else is in the group. */
function pgToolTable(scope, section, group) {
    var h = window.__bfE2E;
    var outer = h.disclosure(scope, section);
    var r = outer ? h.regionOf(outer) : null;
    var ctrl = r ? h.disclosure(r, group) : null;
    var region = ctrl ? h.regionOf(ctrl) : null;
    if (!region) {
        return { found: false, why: 'no "' + group + '" content' };
    }
    var tables = Array.prototype.filter.call(region.querySelectorAll('table'), h.vis);
    var table = tables[tables.length - 1] || null;
    var text = h.text(region);
    var m = /Showing\s+(\d+)\s+of\s+(\d+)/i.exec(text);
    var out = {
        found: !!table, showing: m ? m[0] : null, shown: m ? Number(m[1]) : null, total: m ? Number(m[2]) : null,
        checkboxes: region.querySelectorAll('input[type="checkbox"]').length, selects: region.querySelectorAll('select').length,
        buttons: Array.prototype.filter.call(region.querySelectorAll('button'), h.vis).map(function (b) { return h.text(b); }),
        busy: !!region.querySelector('.bf-loading, progress:not(.hide), [aria-busy="true"]'),
        text: text.slice(0, 400), headers: [], rows: []
    };
    if (table) {
        var headRow = table.querySelector('thead tr') || Array.prototype.find.call(table.querySelectorAll('tr'), function (tr) { return tr.querySelector('th') && !tr.querySelector('td'); });
        out.headers = headRow ? Array.prototype.map.call(headRow.querySelectorAll('th, td'), function (c) { return h.text(c); }) : [];
        out.rows = Array.prototype.filter.call(table.querySelectorAll('tr'), function (tr) { return tr !== headRow && tr.querySelector('td') && h.vis(tr); })
            .map(function (tr) { return { cells: Array.prototype.map.call(tr.querySelectorAll('td, th'), function (c) { return h.text(c); }), text: h.text(tr) }; });
    }
    return out;
}

/** Waits for the group's table to have rows (and nothing loading); returns the last look. */
async function waitToolTable(page, section, group, accept, timeout) {
    const deadline = Date.now() + (timeout || 30000);
    let last = null;
    for (;;) {
        last = await page.evaluate(pgToolTable, ADMIN, section, group);
        if (last.found && !last.busy && last.rows.length && (!accept || accept(last))) {
            return last;
        }
        if (Date.now() > deadline) {
            return last;
        }
        await sleep(300);
    }
}

/** Searches for a title in "Check a title", picks the result matching `pattern`, and waits for the result table. */
async function checkTitle(t, page, section, person, term, pattern, exclude, accept) {
    const group = SPEC.toolsGroups[0];
    const out = { ok: false, steps: [] };
    const who = await page.evaluate(pgToolPickPerson, ADMIN, section, group, person);
    out.personLabel = who.label;
    if (!who.ok) {
        out.steps.push('person: ' + who.why);
        return out;
    }
    const focus = await page.evaluate(pgToolFocusSearch, ADMIN, section, group);
    out.searchLabel = focus.label;
    if (!focus.ok) {
        out.steps.push('no title search box');
        return out;
    }
    await page.type(term);
    try {
        await page.waitFor(pgToolResult, { timeout: 10000, what: 'a search result matching ' + pattern }, ADMIN, section, group, pattern, exclude || null);
    } catch (e) {
        out.steps.push('no search result matching /' + pattern + '/ for "' + term + '"');
        return out;
    }
    const mark = page.requests.length;
    await page.click(pgToolResult, { args: [ADMIN, section, group, pattern, exclude || null], what: 'the search result /' + pattern + '/' });
    const posted = () => page.requests.slice(mark).some((r) => r.method === 'POST' && /\/Babelfin\/Admin\/Simulate/i.test(r.url));
    const until = Date.now() + 3000;
    while (!posted() && Date.now() < until) {
        await sleep(150);
    }
    if (!posted()) {
        try {
            await page.click(pgToolButton, { args: [ADMIN, section, group, '^(check|run|check (this|the) title|show)$'], what: 'the Check button', timeout: 2000 });
            out.steps.push('picking the result sent nothing; a Check button was clicked');
            const until2 = Date.now() + 3000;
            while (!posted() && Date.now() < until2) {
                await sleep(150);
            }
        } catch (e) {
            out.steps.push('picking the result sent no POST Admin/Simulate and there is no Check button');
        }
    }
    const table = await waitToolTable(page, section, group, accept, 30000);
    out.table = table;
    out.posted = posted();
    out.ok = !!(table && table.found && table.rows.length);
    if (!out.posted) {
        out.steps.push('no POST Admin/Simulate was sent');
    }
    return out;
}

/** Column index by header text. */
function col(table, name) {
    return (table.headers || []).findIndex((x) => x === name);
}

/** "Chosen by Babelfin" / "Video default" for a result row (null when it says neither). */
function markOf(row) {
    if (row.text.indexOf(SPEC.chosen) >= 0) {
        return 'chosen';
    }
    return row.text.indexOf(SPEC.videoMark) >= 0 ? 'video' : null;
}

/** Runs Admin/Simulate through the API and returns its rows (the same thing the page asked for). */
async function simulateRows(t, userId, itemId) {
    let start = await t.api.post('/Babelfin/Admin/Simulate', { UserId: userId, ItemId: itemId });
    for (let i = 0; i < 20 && start.status === 409; i++) {
        await sleep(500);
        start = await t.api.post('/Babelfin/Admin/Simulate', { UserId: userId, ItemId: itemId });
    }
    const body = start.body || {};
    if (Array.isArray(body.Rows) && body.Done !== false) {
        return { status: start.status, rows: body.Rows, total: body.Total };
    }
    const id = body.JobId || body.jobId;
    if (!id) {
        return { status: start.status, rows: null };
    }
    for (let i = 0; i < 60; i++) {
        const r = await t.api.get('/Babelfin/Admin/Simulate/' + id);
        if (r.status === 200 && r.body && r.body.Done) {
            return { status: 200, rows: r.body.Rows || [], total: r.body.Total };
        }
        await sleep(500);
    }
    return { status: 0, rows: null };
}

const LANGUAGE_NAMES = { ja: 'Japanese', en: 'English', de: 'German', it: 'Italian', fr: 'French', zh: 'Chinese' };

/** Compares the page's result rows with the API's: count, the mark, and the audio and subtitle languages. */
function compareRows(table, apiRows) {
    const p = [];
    if (!apiRows) {
        return ['the API gave no rows'];
    }
    if (table.rows.length !== Math.min(apiRows.length, SPEC.capRows)) {
        p.push(table.rows.length + ' rows shown, the API has ' + apiRows.length);
    }
    const ai = col(table, 'Audio');
    const si = col(table, 'Subtitles');
    apiRows.slice(0, Math.min(apiRows.length, table.rows.length)).forEach((a, i) => {
        // The page row for this API row: by its "S01E02" number when it has one (episode names repeat), else by name, else by position.
        const numbered = a.EpisodeNumber !== null && a.EpisodeNumber !== undefined
            ? new RegExp('S0*' + (a.SeasonNumber == null ? '\\d+' : a.SeasonNumber) + '\\s*E0*' + a.EpisodeNumber + '\\b') : null;
        const row = (numbered ? table.rows.find((r) => numbered.test(r.text)) : table.rows.find((r) => r.text.indexOf(a.Name) >= 0)) || table.rows[i];
        const chosen = a.Outcome === 'Applied' || a.Outcome === 'DryRun';
        const mark = markOf(row);
        if (mark !== (chosen ? 'chosen' : 'video')) {
            p.push('"' + a.Name + '" (' + a.Outcome + ') is marked ' + (mark ? '"' + (mark === 'chosen' ? SPEC.chosen : SPEC.videoMark) + '"' : 'with neither') + ' (want "' + (chosen ? SPEC.chosen : SPEC.videoMark) + '")');
        }
        const al = LANGUAGE_NAMES[String(a.AudioLanguage || '').slice(0, 2)];
        if (al && ai >= 0 && (row.cells[ai] || '').indexOf(al) < 0) {
            p.push('"' + a.Name + '" Audio reads "' + row.cells[ai] + '" (the API: ' + a.AudioLanguage + ')');
        }
        const sl = LANGUAGE_NAMES[String(a.SubtitleLanguage || '').slice(0, 2)];
        if (sl && si >= 0 && (row.cells[si] || '').indexOf(sl) < 0) {
            p.push('"' + a.Name + '" Subtitles reads "' + row.cells[si] + '" (the API: ' + a.SubtitleLanguage + ')');
        }
    });
    return p;
}

async function testTools(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    const viewerId = t.ids.viewerId;
    const userPath = '/Babelfin/Admin/Users/' + viewerId + '/Settings';
    const viewerSnapshot = clone((await t.api.get(userPath)).body);
    t.defer('second user\'s settings restored', async () => {
        await restorePersonSnapshot(t, t.api, userPath, viewerSnapshot, t.opts.viewer + "'s settings put back through the API");
    });
    await putAdmin(t, adminNeutral(kept.admin), 'neutral');
    const viewerAnime = withTable(t, viewerSnapshot, { perLibrary: true, libs: { Anime: ['lang:ja', 'lang:en'] } });
    const pv = await t.api.put(userPath, viewerAnime);
    if (pv.status !== 200) {
        throw new Error('PUT ' + userPath + ': ' + pv.status);
    }

    // 10a. Coverage is gone from the API.
    const cov = await t.api.get('/Babelfin/Admin/Coverage', { libraryId: compactId(t.ids.libraries.Anime), userId: compactId(t.api.userId) });
    await t.check(cov.status === 404, 'GET Babelfin/Admin/Coverage (with a library and a person) answers 404 (' + cov.status + ')', null);

    // Enough activity for "Show more": at least 130 entries, 60 of them about the second user.
    let act = ((await t.api.get('/Babelfin/Admin/Activity', { limit: 5000 })).body) || [];
    let viewerCount = act.filter((e) => compactId(e.UserId) === compactId(viewerId)).length;
    for (let i = viewerCount; i < 60; i++) {
        await t.api.put(userPath, viewerAnime);
    }
    const meNow = (await getMe(t)).Settings;
    act = ((await t.api.get('/Babelfin/Admin/Activity', { limit: 5000 })).body) || [];
    for (let i = act.length; i < 130; i++) {
        await t.api.put('/Babelfin/Me/Settings', meNow);
    }
    act = ((await t.api.get('/Babelfin/Admin/Activity', { limit: 5000 })).body) || [];
    viewerCount = act.filter((e) => compactId(e.UserId) === compactId(viewerId)).length;
    t.note('Babelfin\'s activity log holds ' + act.length + ' entries, ' + viewerCount + ' about ' + t.opts.viewer);

    await openAdminPage(t, page);
    await page.evaluate(pgClearBabelfinStorage);
    await reloadAdminPage(t, page);
    const tools = await toolsTitle(page);
    const h0 = await page.evaluate(pgHeight, ADMIN);
    const sec = await setSection(page, ADMIN, tools, true);
    if (!sec.found || sec.groups.map((g) => g.name).indexOf(SPEC.toolsGroups[0]) < 0) {
        await t.check(false, tools + ' opens with a "' + SPEC.toolsGroups[0] + '" group (groups: ' + ((sec.groups || []).map((g) => '"' + g.name + '"').join(', ') || 'none') + '; the Check a title checks are skipped)', page);
    } else {
        // 10b. Check a title: the episode, then its show.
        const group = SPEC.toolsGroups[0];
        await toggleGroup(page, ADMIN, tools, group, 'click', true);
        // Search results read "Astercoil Test · S1E1 · <name>" (any zero padding).
        const ep = await checkTitle(t, page, tools, t.opts.viewer, 'Astercoil', 'Astercoil Test\\W.*\\bS0?1\\s*E0?1\\b', null);
        const epApi = await simulateRows(t, viewerId, t.ids.episodeId);
        const headers = ep.table ? ep.table.headers : [];
        const want = ['Title', 'Audio', 'Subtitles', 'Why'];
        const order = want.map((w) => headers.indexOf(w));
        await t.check(ep.ok && order.every((i) => i >= 0) && order.every((i, k) => k === 0 || i > order[k - 1]),
            'Check a title (' + t.opts.viewer + ', the episode Astercoil Test S01E01): a person dropdown' + (ep.personLabel ? ' ("' + ep.personLabel + '")' : '') + ', a title search' + (ep.searchLabel ? ' ("' + ep.searchLabel + '")' : '')
            + ', POST Admin/Simulate sent (' + !!ep.posted + '), and a table with the columns Title, Audio, Subtitles, Why (' + (headers.length ? headers.map((x) => '"' + x + '"').join(', ') : 'no table') + ')' + (ep.steps.length ? ' — ' + ep.steps.join('; ') : ''), page);
        if (ep.ok) {
            const p = compareRows(ep.table, epApi.rows);
            const row = ep.table.rows[0] || { text: '' };
            await t.check(ep.table.rows.length === 1 && markOf(row) === 'chosen' && !p.length,
                'the episode: one row, "' + SPEC.chosen + '", Japanese audio and English subtitles, matching the API (' + (row.text || '').slice(0, 160) + ')' + (p.length ? ' — ' + p.join('; ') : ''), page);
        }
        const show = await checkTitle(t, page, tools, t.opts.viewer, 'Astercoil', '^Astercoil Test \\(\\d{4}\\)', 'S\\d+E\\d+|Season', (tbl) => tbl.rows.length >= (t.ids.episodeCount || 3));
        const showApi = await simulateRows(t, viewerId, t.ids.seriesId);
        if (show.ok) {
            const p = compareRows(show.table, showApi.rows);
            const titles = show.table.rows.map((r) => r.cells[col(show.table, 'Title')] || r.text);
            await t.check(show.table.rows.length === t.ids.episodeCount && show.table.rows.every((r) => markOf(r) === 'chosen') && uniq(titles).length === titles.length && !p.length,
                'the show (Astercoil Test): ' + show.table.rows.length + ' rows, one per episode (' + t.ids.episodeCount + '), each "' + SPEC.chosen + '" with Japanese / English, matching the API' + (p.length ? ' — ' + p.join('; ') : ''), page);
        } else {
            await t.check(false, 'the show (Astercoil Test): a result table' + (show.steps.length ? ' — ' + show.steps.join('; ') : ''), page);
        }
        await t.check(show.table && show.table.checkboxes === 0 && show.table.selects === 1, 'Check a title has no counters and no filters (' + (show.table ? show.table.checkboxes + ' checkboxes, ' + show.table.selects + ' dropdowns' : 'no table') + ')', page);
        // With nothing chosen for the person, every row is "Video default".
        await t.api.put(userPath, withTable(t, viewerSnapshot, { perLibrary: null }));
        const plain = await checkTitle(t, page, tools, t.opts.viewer, 'Astercoil', '^Astercoil Test \\(\\d{4}\\)', 'S\\d+E\\d+|Season', (tbl) => tbl.rows.length >= (t.ids.episodeCount || 3) && tbl.rows.every((r) => markOf(r) === 'video'));
        const plainApi = await simulateRows(t, viewerId, t.ids.seriesId);
        if (plain.ok) {
            const p = compareRows(plain.table, plainApi.rows);
            await t.check(plain.table.rows.length === t.ids.episodeCount && plain.table.rows.every((r) => markOf(r) === 'video') && !p.length,
                'with nothing chosen for ' + t.opts.viewer + ', the show\'s rows are each "' + SPEC.videoMark + '" and show the tracks Jellyfin plays (the API\'s PassedThrough rows: ' + (plainApi.rows || []).map((x) => (x.AudioLanguage || '?') + '/' + (x.SubtitleLanguage || 'off')).join(', ') + ')' + (p.length ? ' — ' + p.join('; ')
                    + '\n       rows: ' + plain.table.rows.map((x) => x.text).join(' | ') + '\n       API: ' + JSON.stringify((plainApi.rows || []).map((x) => [x.Name, x.Outcome, x.AudioLanguage, x.SubtitleLanguage])) : ''), page);
        } else {
            await t.check(false, 'with nothing chosen: a result table' + (plain.steps.length ? ' — ' + plain.steps.join('; ') : ''), page);
        }
        // The cap: the simulation answer is multiplied to 120 rows in the browser; the page shows 50 and says so.
        await t.api.put(userPath, viewerAnime);
        const stop = await page.interceptResponses('*Babelfin/Admin/Simulate*', (text) => {
            const body = JSON.parse(text);
            const key = Array.isArray(body.Rows) ? 'Rows' : (Array.isArray(body.rows) ? 'rows' : null);
            if (!key || !body[key].length) {
                return null;
            }
            const base = body[key];
            const many = [];
            for (let i = 0; i < 120; i++) {
                const r = Object.assign({}, base[i % base.length]);
                r.ItemId = String(r.ItemId || '').slice(0, 24) + String(i).padStart(8, '0');
                r.Name = (r.Name || 'Title') + ' copy ' + (i + 1);
                many.push(r);
            }
            body[key] = many;
            ['Total', 'total'].forEach((k) => { if (k in body) { body[k] = 120; } });
            return JSON.stringify(body);
        });
        const capped = await checkTitle(t, page, tools, t.opts.viewer, 'Astercoil', '^Astercoil Test \\(\\d{4}\\)', 'S\\d+E\\d+|Season', (tbl) => tbl.rows.length >= SPEC.capRows || /of 120/.test(tbl.showing || ''));
        const hits = await stop();
        await t.check(hits.rewritten > 0 && capped.ok && capped.table.rows.length === SPEC.capRows && capped.table.shown === SPEC.capRows && capped.table.total === 120,
            'with 120 rows in the answer (rewritten in the browser), the table shows ' + SPEC.capRows + ' rows and "Showing 50 of 120" (' + (capped.table ? capped.table.rows.length + ' rows, "' + (capped.table.showing || 'no Showing line') + '"' : 'no table')
            + (hits.rewritten ? '' : '; the answer had no Rows to multiply') + ')', page);
        // The page stays a reasonable size, and collapsing gives the space back.
        const h1 = await page.evaluate(pgHeight, ADMIN);
        await toggleGroup(page, ADMIN, tools, group, 'click', false);
        const h2 = await page.evaluate(pgHeight, ADMIN);
        await setSection(page, ADMIN, tools, false);
        await sleep(300);
        const h3 = await page.evaluate(pgHeight, ADMIN);
        await t.check(h1 - h0 <= 5000, 'with ' + SPEC.capRows + ' result rows the admin page grows by ' + (h1 - h0) + ' px (' + h0 + ' → ' + h1 + '; at most 5000 px)', page);
        await t.check(h2 < h1 && Math.abs(h3 - h0) <= 40, 'collapsing "' + group + '" shrinks it (' + h1 + ' → ' + h2 + ' px) and collapsing ' + tools + ' brings it back to its first height (' + h3 + ' px, was ' + h0 + ')', page);
        await setSection(page, ADMIN, tools, true);
    }

    // 10c. Activity: the newest 50, Show more, a person filter.
    const actGroup = SPEC.toolsGroups[1];
    const ag = await toggleGroup(page, ADMIN, tools, actGroup, 'click', true);
    if (!ag.found) {
        await t.check(false, tools + ' has an "' + actGroup + '" group', page);
    } else {
        const newest = ((await t.api.get('/Babelfin/Admin/Activity', { limit: 1 })).body || [])[0] || {};
        const a1 = await waitToolTable(page, tools, actGroup, null, 15000);
        const firstText = a1.rows.length ? a1.rows[0].text : '';
        await t.check(a1.rows.length === SPEC.activityPage && firstText.indexOf(newest.Text || '\u0000') >= 0,
            'Activity shows the newest ' + SPEC.activityPage + ' entries (' + a1.rows.length + ' rows; the first reads "' + firstText.slice(0, 100) + '", the newest entry is "' + newest.Text + '")', page);
        let more = false;
        try {
            await page.click(pgToolButton, { args: [ADMIN, tools, actGroup, '^Show more$'], what: 'Show more', timeout: 3000 });
            more = true;
        } catch (e) {
            more = false;
        }
        const a2 = more ? await waitToolTable(page, tools, actGroup, (x) => x.rows.length > SPEC.activityPage, 10000) : a1;
        await t.check(more && a2.rows.length > SPEC.activityPage && a2.rows.length <= act.length + 5,
            '"Show more" shows more entries (' + (more ? a1.rows.length + ' → ' + a2.rows.length + ' of ' + act.length : 'no "Show more" button: ' + a1.buttons.join(', ')) + ')', page);
        const who = await page.evaluate(pgToolPickPerson, ADMIN, tools, actGroup, t.opts.viewer);
        const a3 = who.ok ? await waitToolTable(page, tools, actGroup, (x) => x.rows.length && x.rows.every((r) => r.text.indexOf(t.opts.viewer) >= 0), 10000) : a2;
        const onlyViewer = a3.rows.length > 0 && a3.rows.every((r) => r.text.indexOf(t.opts.viewer) >= 0);
        await t.check(who.ok && onlyViewer && a3.rows.length === Math.min(SPEC.activityPage, viewerCount),
            'the person filter (' + (who.label || 'dropdown') + ') shows only ' + t.opts.viewer + '\'s entries, the newest ' + Math.min(SPEC.activityPage, viewerCount) + ' (' + (who.ok ? a3.rows.length + ' rows, ' + (onlyViewer ? 'all theirs' : 'some not theirs') : who.why) + ')', page);
        await toggleGroup(page, ADMIN, tools, actGroup, 'click', false);
    }

    // 10d. The separate Back up and restore section opens with Download and Restore from a file.
    const bk = await setSection(page, ADMIN, SPEC.backup, true);
    const bkLook = bk.found ? await page.evaluate(function (scope, section) {
        var h = window.__bfE2E;
        var ctrl = h.disclosure(scope, section);
        var region = ctrl ? h.regionOf(ctrl) : null;
        if (!region) {
            return { buttons: [], file: false };
        }
        return {
            buttons: Array.prototype.filter.call(region.querySelectorAll('button, label'), h.vis).map(function (b) { return h.text(b); }),
            file: !!region.querySelector('input[type="file"]')
        };
    }, ADMIN, SPEC.backup) : { buttons: [], file: false };
    await t.check(bk.found && bk.expanded && bkLook.buttons.some((b) => /^Download/.test(b)) && bkLook.file,
        '"' + SPEC.backup + '" opens with a download button and a file picker for restoring (' + (bk.found ? bkLook.buttons.join(', ') + '; file input ' + bkLook.file : 'no such section') + ')', page);
    await setSection(page, ADMIN, SPEC.backup, false);

    // 10e. No Coverage anywhere on the page.
    const gone = await page.evaluate(pgFindTexts, ADMIN, ['Coverage', 'Count', 'Download CSV']);
    await t.check(gone.found.length === 0, 'no Coverage on the admin page ("Coverage", "Count", "Download CSV")' + (gone.found.length ? ' — found: ' + gone.found.join('; ') : ''), page);
}

// ───────────────────────────── Show and movie overrides, shared steps ─────────────────────────────

/** The expected English override wording by key in Assets/i18n/en.json (test 18 checks that the shipped file says the same). */
const OVERRIDE_WORDS = {
    'client.menu.override': 'Audio and subtitles (Babelfin)',
    'dialog.applies.show': "Applies to the whole show, in place of the {library} library's choice.",
    'dialog.applies.movie': "Applies to this movie, in place of the {library} library's choice.",
    'dialog.applies.showNoLibrary': "Applies to the whole show, in place of the library's choice.",
    'dialog.applies.movieNoLibrary': "Applies to this movie, in place of the library's choice.",
    'dialog.narrower': 'An episode or season of this show has its own override, which comes first for it. It is listed under Show and movie overrides in Babelfin Settings.',
    'dialog.burnedIn.show': 'All episodes of this show have burned-in subtitles in',
    'dialog.burnedIn.movie': 'This movie has burned-in subtitles in',
    'dialog.burnedIn.video': 'This video has burned-in subtitles in',
    'dialog.remove': 'Remove override',
    'dialog.removed': 'Override removed',
    'overrides.title': 'Show and movie overrides',
    'overrides.help': 'Choices for single shows and movies, in place of the library choice. To add one, open the ⋮ menu of a show or movie and choose "Audio and subtitles (Babelfin)".',
    'overrides.empty': 'No show or movie overrides yet.',
    'overrides.col.item': 'Show or movie',
    'overrides.col.library': 'Library',
    'overrides.col.audio': 'Audio',
    'overrides.col.subtitles': 'Subtitles',
    'overrides.remembered': 'Remembered from a track change',
    'overrides.noLibrary': 'Not in a library',
    'overrides.notSet': "Library's choice",
    'overrides.removeFor': 'Remove the override for {name}',
    'overrides.removeAll': 'Remove all show and movie overrides',
    'overrides.removeAll.confirmTitle': 'Remove all show and movie overrides?',
    'overrides.removeAll.confirmText': "Every show and movie goes back to its library choice. This can't be undone.",
    'overrides.removeAll.confirmTextFor': "Every show and movie goes back to {name}'s library choices. This can't be undone.",
    'overrides.removeAll.done': 'All show and movie overrides were removed.',
    'overrides.removed': 'Override removed.'
};

/** An override message with its {placeholders} filled in. */
function overrideWord(key, vars) {
    return String(OVERRIDE_WORDS[key]).replace(/\{(\w+)\}/g, (m, name) => (vars && vars[name] !== undefined ? vars[name] : m));
}

const ME_OVERRIDES = '/Babelfin/Me/Overrides';
const HOME_PAGE = '#indexPage:not(.hide)';
const DETAIL_PAGE = '#itemDetailPage:not(.hide)';

/** What PUT Me/Overrides/{id} takes for table values: storedFor() without its null fields. */
function layerFor(audio, subtitles) {
    const stored = storedFor(audio, subtitles);
    const out = {};
    Object.keys(stored).forEach((k) => { if (stored[k] !== null && stored[k] !== undefined) { out[k] = stored[k]; } });
    return out;
}

/** The stored override entry of an item in a list of GET Me/Overrides summaries or of Me/Settings' Overrides (null when there is none). */
function overrideOf(rows, id, scope) {
    return (rows || []).find((o) => compactId(o.ItemId) === compactId(id) && (!scope || o.Scope === scope)) || null;
}

async function overrideSummaries(t, api) {
    const r = await (api || t.api).get(ME_OVERRIDES);
    return r.status === 200 && Array.isArray(r.body) ? r.body : null;
}

async function putOverride(t, id, scope, layer, api) {
    return (api || t.api).put(ME_OVERRIDES + '/' + compactId(id), layer, { scope: scope });
}

async function deleteOverride(t, id, scope, api) {
    return (api || t.api).del(ME_OVERRIDES + '/' + compactId(id), { scope: scope });
}

/**
 * Everything a test adds to the person's show and movie overrides is taken away at the end (DELETE Me/Overrides/All), and the overrides the person
 * had are put back. The settings PUT of keepPersonAndAdmin brings the explicit ones back (it runs after this undo step); a remembered track
 * change can only come back through an import, so the export is kept when there is one. Call it right after keepPersonAndAdmin.
 */
async function protectOverrides(t, kept) {
    const had = kept.snapshot.Overrides || [];
    let backup = null;
    if (had.some((o) => o.Manual)) {
        const ex = await t.api.get('/Babelfin/Admin/Export');
        backup = ex.status === 200 ? ex.body : null;
    }
    t.defer('show and movie overrides taken away again', async () => {
        const r = await t.api.del(ME_OVERRIDES + '/All');
        if (backup) {
            await t.api.post('/Babelfin/Admin/Import', backup, { dryRun: 'false' });
        }
        await t.check(r.status === 204, 'every show and movie override this test added is removed again (DELETE Me/Overrides/All: ' + r.status + (backup ? '; the remembered track changes from before come back through an import' : '') + ')', null);
    });
}

/** Where a tab's console and uncaught-error lists stand now; errorsSince(page, mark) then returns what came after (any origin, not only Babelfin's). */
function errorMark(page) {
    return { c: page.console.length, e: page.exceptions.length };
}

/** The console errors (the browser's own failed resource loads and the listed filters aside) and uncaught errors, including unhandled rejections, since a mark. */
function errorsSince(page, mark) {
    const errs = page.console.slice(mark.c).filter((c) => (c.type === 'error' || c.type === 'assert') && c.source !== 'network' && !FILTERS.some((f) => f.console && f.console.test(c.text + ' ' + (c.url || ''))));
    return errs.map((c) => c.text.slice(0, 160)).concat(page.exceptions.slice(mark.e).map((e) => e.text.split('\n')[0].slice(0, 160)));
}

/** English name of a language code, as the pages write it ("ja" → "Japanese"; ISO 639-2 codes of the test media are mapped first). */
function languageName2(code) {
    const iso3 = { eng: 'en', jpn: 'ja', deu: 'de', ger: 'de', fra: 'fr', fre: 'fr', ita: 'it', zho: 'zh', chi: 'zh' };
    const c = String(code || '').toLowerCase();
    return languageWords(iso3[c] || c);
}

/** The Babelfin dialog (client.js) as it stands: words, dropdowns with their options, burned-in row, buttons; null when there is none. */
function pgBfDialog() {
    var h = window.__bfE2E;
    var d = document.querySelector('.bfDialog');
    if (!d) { return null; }
    var txt = function (n) { return n ? h.text(n) : null; };
    var fields = Array.prototype.map.call(d.querySelectorAll('.bfField'), function (f) {
        var s = f.querySelector('select');
        var l = f.querySelector('label');
        return {
            label: txt(l), forId: l ? l.getAttribute('for') : null, id: s ? s.id : null, aria: s ? s.getAttribute('aria-label') : null,
            value: s ? s.value : null, text: s && s.options[s.selectedIndex] ? s.options[s.selectedIndex].textContent : null,
            options: s ? Array.prototype.map.call(s.options, function (o) {
                return { v: o.value, l: o.textContent, g: o.parentNode && o.parentNode.tagName === 'OPTGROUP' ? o.parentNode.label : null };
            }) : []
        };
    });
    var burn = d.querySelector('.bfBurned');
    var actions = d.querySelector(':scope > .bfActions');
    var r = d.getBoundingClientRect();
    var a = document.activeElement;
    return {
        loading: !!d.querySelector('.bfLoading'), role: d.getAttribute('role'), modal: d.getAttribute('aria-modal'), labelledby: d.getAttribute('aria-labelledby'),
        focusInside: d.contains(a), active: a ? a.tagName + (a.id ? '#' + a.id : '') + '.' + String(a.className).split(' ')[0] : null,
        title: txt(d.querySelector('.bfTitle')), target: txt(d.querySelector('.bfTarget')), subtitle: txt(d.querySelector('.bfSubtitle')),
        explains: Array.prototype.map.call(d.querySelectorAll(':scope > .bfExplain'), h.text), fields: fields,
        burned: burn ? {
            checked: burn.querySelector('input[type="checkbox"]').checked, label: txt(burn.querySelector('.bfRadioLabel')), aria: burn.querySelector('select').getAttribute('aria-label'), select: burn.querySelector('select').value,
            selectText: burn.querySelector('select').options[burn.querySelector('select').selectedIndex].textContent, text: burn.querySelector('input[type="text"]').value
        } : null,
        buttons: actions ? Array.prototype.map.call(actions.querySelectorAll('button'), function (b) { return { text: h.text(b), cls: b.className, disabled: b.disabled }; }) : [],
        error: txt(d.querySelector(':scope > .bfError')), picker: !!d.querySelector('.bfPicker'),
        box: [Math.round(r.left), Math.round(r.top), Math.round(r.right), Math.round(r.bottom)], vw: window.innerWidth, vh: window.innerHeight
    };
}

/** Waits until the Babelfin dialog has drawn its dropdowns (or an error), then past the dialog's 400 ms guard against stray clicks. */
async function waitBfDialog(page, timeout) {
    await page.waitFor(function () {
        var d = document.querySelector('.bfDialog');
        return !!d && !d.querySelector('.bfLoading') && !!(d.querySelector('.bfField select') || d.querySelector('.bfError'));
    }, { timeout: timeout || 20000, what: 'the Babelfin dialog with its dropdowns' });
    await sleep(550);
    return page.evaluate(pgBfDialog);
}

async function waitNoBfDialog(page, timeout) {
    try {
        await page.waitFor('!document.querySelector(".bfDialog")', { timeout: timeout || 5000, what: 'the Babelfin dialog to close' });
        return true;
    } catch (e) {
        return false;
    }
}

/** The element of one of the Babelfin dialog's own buttons (Cancel, Save, Remove override). */
function pgBfDialogButton(text) {
    var d = document.querySelector('.bfDialog');
    var actions = d ? d.querySelector(':scope > .bfActions') : null;
    return actions ? (Array.prototype.find.call(actions.querySelectorAll('button'), function (b) { return b.textContent.trim() === text && !b.disabled; }) || null) : null;
}

/** The redundant detail-page control must stay absent, including on cached pages and after saves. */
async function checkNoItemChip(t, page, where) {
    const absent = await page.evaluate('!document.querySelector(".bfChip")');
    await t.check(absent, where + ': no Babelfin detail-page chip', page);
}

/** Opens an item's existing ⋮ menu and its Babelfin entry, returning the rendered show/movie dialog. */
async function openItemMenuDialog(t, page, itemId) {
    await page.navigate(t.url('/web/#/details?id=' + compactId(itemId)));
    await page.waitForSelector(DETAIL_PAGE + ' .btnMoreCommands', { timeout: 30000 });
    await sleep(800);
    await checkNoItemChip(t, page, 'item ' + compactId(itemId));
    await centerOn(page, DETAIL_PAGE + ' .btnMoreCommands');
    await page.click(DETAIL_PAGE + ' .btnMoreCommands');
    const sheet = await waitSheet(page, true, 9000);
    await t.check(!!sheet && sheet.bfCount === 1 && sheet.bfText === OVERRIDE_WORDS['client.menu.override'],
        'the item ⋮ menu offers exactly one "' + OVERRIDE_WORDS['client.menu.override'] + '" entry', page);
    if (!sheet || !sheet.bfCount) { return null; }
    await page.click('.actionSheet .bfSheetItem');
    const dialog = await waitBfDialog(page);
    await t.check(await page.evaluate('!document.querySelector(".actionSheet")'), 'the ⋮ sheet closes when the Babelfin dialog opens', page);
    return dialog;
}

/** Closes the Babelfin dialog with Escape and reports whether it closed and where the focus went (the selector of what should hold it). */
async function escapeBfDialog(page, focusSel) {
    await page.press('Escape');
    const closed = await waitNoBfDialog(page, 5000);
    const focus = await page.evaluate(function (sel) {
        var el = sel ? document.querySelector(sel) : null;
        var a = document.activeElement;
        return { ok: !!el && !!a && el.contains(a), active: a ? a.tagName + '.' + String(a.className).split(' ')[0] : null };
    }, focusSel || null);
    return { closed: closed, focus: focus };
}

/** Sets one of the dialog's dropdowns like a person picking (input + change); returns whether it is on the value. */
async function pickInBfDialog(page, label, value) {
    const r = await page.evaluate(pgPick, '.bfDialog', label, value);
    await sleep(150);
    return r;
}

/** The dialog's dropdowns as [{ label, options: [{v, l, g}] }] with the text of the first option cut off at "Default" when asked. */
function choicesOf(dialog) {
    return dialog.fields.map((f) => ({ label: f.label, options: f.options.map((o) => ({ v: o.v, l: o.l, g: o.g })) }));
}

/**
 * The header of a dialog: title, the target's name and library, the applies line, the narrower-override note, the dropdowns, the
 * buttons. want: { name, library, kind ('movie' | 'show'), narrower (bool), remove (bool) }.
 */
async function dialogHeaderChecks(t, page, where, d, want) {
    await t.check(!!d && d.role === 'dialog' && d.modal === 'true' && d.focusInside && !d.loading,
        where + ': a modal dialog with focus inside it (role ' + (d && d.role) + ', aria-modal ' + (d && d.modal) + ', focus ' + (d && d.active) + ')', page);
    if (!d) {
        return;
    }
    const sub = apos(d.subtitle);
    const subWant = want.name + (want.library ? ' · ' + want.library : '');
    await t.check(d.title === 'Audio and subtitles' && d.target === want.name && sub === subWant,
        where + ': titled "Audio and subtitles", with the target\'s name and library under it ("' + d.title + '", "' + d.subtitle + '"; want "' + subWant + '")', page);
    const applies = overrideWord('dialog.applies.' + want.kind + (want.library ? '' : 'NoLibrary'), { library: want.library });
    await t.check(d.explains.length >= 1 && apos(d.explains[0]) === applies,
        where + ': the applies line reads "' + applies + '" (' + JSON.stringify(d.explains[0]) + ')', page);
    const narrower = d.explains.slice(1).some((x) => apos(x) === overrideWord('dialog.narrower'));
    await t.check(narrower === !!want.narrower && d.explains.length === (want.narrower ? 2 : 1),
        where + ': ' + (want.narrower ? 'the note that an episode or season has its own override shows' : 'no note about a narrower override') + ' (' + d.explains.length + ' lines)', page);
    const labels = d.fields.map((f) => f.label);
    await t.check(same(labels, ['Audio', 'Subtitles']) && d.fields.every((f) => f.aria === f.label && f.forId === f.id && !!f.id),
        where + ': two labelled dropdowns, Audio then Subtitles (' + labels.join(', ') + ')', page);
    const burnLabel = overrideWord('dialog.burnedIn.' + want.kind);
    await t.check(!!d.burned && d.burned.label === burnLabel && d.burned.aria === burnLabel,
        where + ': the burned-in checkbox and language selector name the full ' + want.kind + ' scope ("' + (d.burned && d.burned.label) + '")', page);
    const buttons = d.buttons.map((b) => b.text);
    const wantButtons = (want.remove ? ['Remove override'] : []).concat(['Cancel', 'Save']);
    await t.check(same(buttons, wantButtons), where + ': the buttons are ' + wantButtons.join(', ') + ' (' + buttons.join(', ') + ')', page);
}

// ───────────────────────────── 11. Item pages: the ⋮ dialog ─────────────────────────────

async function testItemPages(t) {
    const page = t.page;
    const epId = t.ids.episodeId;
    const movieId = t.ids.movieId;
    const seriesId = t.ids.seriesId;
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    t.defer('burned-in flag removed', async () => { await t.api.del('/Babelfin/Me/BurnedIn/' + compactId(movieId), { scope: 'Title' }); });
    // Nothing chosen anywhere: Me/Choice reports the video's own tracks (Outcome PassedThrough).
    const nothing = withTable(t, kept.snapshot, { perLibrary: null });
    nothing.Overrides = (nothing.Overrides || []).filter((o) => [epId, seriesId, movieId, t.ids.seasonId].every((id) => compactId(o.ItemId) !== compactId(id)));
    nothing.BurnedIn = (nothing.BurnedIn || []).filter((f) => compactId(f.ItemId) !== compactId(movieId));
    await putMe(t, nothing, 'nothing chosen');
    await putAdmin(t, adminNeutral(kept.admin), 'neutral');
    await t.api.del(ME_OVERRIDES + '/All');
    const itemR = await t.api.get('/Users/' + t.api.userId + '/Items/' + epId);
    const streams = ((itemR.body && itemR.body.MediaSources && itemR.body.MediaSources[0] && itemR.body.MediaSources[0].MediaStreams) || []);
    const audios = streams.filter((s) => s.Type === 'Audio');
    const defaultAudio = audios.find((s) => s.IsDefault) || audios[0] || {};
    const passed = await choiceOf(t, epId, (b) => b && b[0] && b[0].Outcome === 'PassedThrough');
    const p0 = (passed.body || [])[0] || {};
    await t.check(passed.status === 200 && p0.Outcome === 'PassedThrough' && p0.AudioIndex === defaultAudio.Index,
        'with nothing chosen, Me/Choice for the episode reports the video\'s own tracks: ' + choiceText(passed) + ' (want PassedThrough with the file\'s default audio #' + defaultAudio.Index + ')');

    // 11a. Episode, series and movie pages keep their ⋮ dialog and have no redundant chip.
    for (const [id, kind] of [[epId, 'show'], [seriesId, 'show'], [movieId, 'movie']]) {
        const dialog = await openItemMenuDialog(t, page, id);
        await t.check(!!dialog && !!dialog.burned && dialog.burned.label === overrideWord('dialog.burnedIn.' + kind),
            'the ' + kind + ' dialog names the complete burned-in scope (' + (dialog && dialog.burned && dialog.burned.label) + ')', page);
        await escapeBfDialog(page, DETAIL_PAGE + ' .btnMoreCommands');
    }

    // 11b. The ⋮ entry opens the dialog for a movie: the movie itself, its library, the applies line, the table's choices.
    const movieTarget = (await t.api.get(ME_OVERRIDES + '/Target/' + compactId(movieId))).body || {};
    const showTarget = (await t.api.get(ME_OVERRIDES + '/Target/' + compactId(epId))).body || {};
    await t.check(compactId(movieTarget.TargetId) === compactId(movieId) && movieTarget.Scope === 'Title' && movieTarget.LibraryName === 'Movies' && !movieTarget.Override && movieTarget.HasNarrower === false,
        'GET Me/Overrides/Target/{movie}: the movie itself, scope Title, library Movies, no override yet, nothing narrower (' + JSON.stringify([movieTarget.TargetId, movieTarget.Scope, movieTarget.LibraryName, movieTarget.HasNarrower]) + ')', null);
    await t.check(compactId(showTarget.TargetId) === compactId(seriesId) && showTarget.Scope === 'Series' && showTarget.LibraryName === 'Anime' && showTarget.Name === 'Astercoil Test',
        'GET Me/Overrides/Target/{episode}: its show Astercoil Test, scope Series, library Anime (' + JSON.stringify([showTarget.TargetId, showTarget.Scope, showTarget.LibraryName, showTarget.Name]) + ')', null);
    const movieDlg = await openItemMenuDialog(t, page, movieId);
    if (!movieDlg) {
        throw new Error('no Babelfin dialog from the movie page ⋮ menu');
    }
    await dialogHeaderChecks(t, page, 'the ⋮ entry on the movie page', movieDlg, { name: movieTarget.Name, library: 'Movies', kind: 'movie', narrower: false, remove: false });
    const movieAudio = movieDlg.fields[0] || { options: [] };
    const movieSubs = movieDlg.fields[1] || { options: [] };
    await t.check(movieAudio.value === '' && movieSubs.value === '' && /^(Default \(.+\)|Video default)$/.test(movieAudio.text) && /^(Default \(.+\)|Video default)$/.test(movieSubs.text),
        'with no override both dropdowns show their Default ("' + movieAudio.text + '", "' + movieSubs.text + '")', page);
    const choicesMovie = choicesOf(movieDlg);
    const escape1 = await escapeBfDialog(page, DETAIL_PAGE + ' .btnMoreCommands');
    await t.check(escape1.closed, 'Escape closes the dialog', page);
    await t.check(escape1.focus.ok, 'focus returns to the More button after Escape (' + escape1.focus.active + ')', page);

    // The backdrop closes it too (after the dialog's 400 ms guard against the click that opened it).
    await openItemMenuDialog(t, page, movieId);
    await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: 4, y: 4, button: 'left', buttons: 1, clickCount: 1 });
    await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 4, y: 4, button: 'left', buttons: 0, clickCount: 1 });
    await t.check(await waitNoBfDialog(page, 5000), 'a click on the backdrop closes the dialog too', page);
    await t.check(!(await overrideSummaries(t)).length, 'closing it saved nothing (no show or movie override)', null);

    // 11c. The ⋮ entry on an episode page opens the dialog for its whole show.
    const showDlg = await openItemMenuDialog(t, page, epId);
    if (!showDlg) {
        throw new Error('no Babelfin dialog from the episode page ⋮ menu');
    }
    await dialogHeaderChecks(t, page, 'the ⋮ entry on the episode page', showDlg, { name: 'Astercoil Test', library: 'Anime', kind: 'show', narrower: false, remove: false });
    const targetAsked = page.requests.filter((r) => r.method === 'GET' && /\/Babelfin\/Me\/Overrides\/Target\//.test(r.url)).slice(-1)[0];
    await t.check(!!targetAsked && targetAsked.url.indexOf(compactId(epId)) >= 0, 'the dialog asks GET Me/Overrides/Target for the page\'s item and the server answers with the show (' + (targetAsked ? targetAsked.url.split('/Babelfin/')[1] : 'no request') + ')', page);
    const choicesShow = choicesOf(showDlg);
    const dlgFocus = await page.evaluate('(function(){ var d = document.querySelector(".bfDialog"); var s = d && d.querySelector("select"); return !!s && document.activeElement === s; })()');
    await t.check(dlgFocus, 'focus starts on the Audio dropdown', page);
    await escapeBfDialog(page, DETAIL_PAGE + ' .btnMoreCommands');

    // 11d. The dropdowns' choices are the library table's (BabelfinUI.tableChoice): same options, same groups, same words.
    await putMe(t, withTable(t, nothing, { perLibrary: true }), 'a choice per library, nothing chosen');
    await openPersonPage(t, page);
    const tableSnap = await snap(page, null, '*');
    const tableChoices = (row) => ['Audio for ', 'Subtitles for '].map((p) => (byLabel(tableSnap, p + row) || { options: [] }).options.map((o) => ({ v: o.v, l: o.l, g: o.g })));
    const sameOptions = (a, b) => canon(a.map((o) => [o.v, o.l, o.g])) === canon(b.map((o) => [o.v, o.l, o.g]));
    const moviesRow = tableChoices('Movies');
    const animeRow = tableChoices('Anime');
    // The words of the first option ("Default (…)") come from what applies, which the dialog reads for the one title: compared below.
    const sameButDefault = (a, b) => a.length === b.length && a.length > 1 && sameOptions(a.slice(1), b.slice(1)) && a[0].v === b[0].v;
    await t.check(sameButDefault(choicesMovie[0].options, moviesRow[0]) && sameButDefault(choicesMovie[1].options, moviesRow[1]),
        'the movie dialog\'s Audio and Subtitles choices are exactly the table\'s for the Movies library (' + choicesMovie[0].options.length + ' and ' + choicesMovie[1].options.length + ' options: '
        + 'Video default' + (choicesMovie[1].options.some((o) => o.v === 'off') ? ', Off, …' : '') + ', My languages, All languages)', page);
    await t.check(sameButDefault(choicesShow[0].options, animeRow[0]) && sameButDefault(choicesShow[1].options, animeRow[1]),
        'the show dialog\'s choices are exactly the table\'s for the Anime library (' + choicesShow[0].options.length + ' and ' + choicesShow[1].options.length + ' options)', page);
    const groupsOf = (list) => uniq(list.map((o) => o.g).filter(Boolean));
    const plainSubs = choicesMovie[1].options.filter((o) => !o.g && o.v !== 'video').map((o) => o.v);
    const plainAudio = choicesMovie[0].options.filter((o) => !o.g && o.v !== 'video').map((o) => o.v);
    await t.check(same(groupsOf(choicesMovie[0].options), ['My languages', 'All languages']) && same(groupsOf(choicesMovie[1].options), ['My languages', 'All languages'])
        && same(plainAudio, ['', 'mine', 'original']) && same(plainSubs, ['', 'off', 'smart', 'signs']),
        'the groups are "My languages" and "All languages"; audio offers exactly Default, "My languages, in order" and "The title\'s original language" outside the language groups (plus Video default when applicable); Subtitles also offers Off, "When the audio isn\'t in my languages" and "Signs and songs only" (' + plainAudio.length + ' and ' + plainSubs.length + ' ungrouped options)', page);
    // With nothing chosen the dialog's Default reads like the table's for the same library.
    await t.check(movieAudio.text === (moviesRow[0][0] || {}).l && movieSubs.text === (moviesRow[1][0] || {}).l && choicesShow[0].options[0].l === (animeRow[0][0] || {}).l && choicesShow[1].options[0].l === (animeRow[1][0] || {}).l,
        'with nothing chosen "Default (…)" reads as the table\'s for that library: Movies "' + (moviesRow[0][0] || {}).l + '" / "' + (moviesRow[1][0] || {}).l + '", Anime "' + (animeRow[0][0] || {}).l + '" / "' + (animeRow[1][0] || {}).l + '"', page);

    // 11e. "Default (X)" is what applies without the override: a library choice for Movies and for Anime sets X.
    await putMe(t, withTable(t, nothing, { perLibrary: true, libs: { Movies: ['lang:de', 'off'], Anime: ['lang:ja', 'lang:en'] } }), 'library choices for Movies and Anime');
    const movieChoice = (await choiceOf(t, movieId, (b) => b && b[0] && b[0].AudioLanguage === 'de')).body || [];
    const epChoice = (await choiceOf(t, epId, (b) => b && b[0] && b[0].AudioLanguage === 'ja')).body || [];
    const mc = movieChoice[0] || {};
    const ec = epChoice[0] || {};
    const wantMovie = ['Default (' + languageName2(mc.AudioLanguage) + ')', mc.SubtitleIndex === undefined || mc.SubtitleIndex === null || mc.SubtitleIndex < 0 ? 'Default (Off)' : 'Default (' + languageName2(mc.SubtitleLanguage) + ')'];
    const wantShow = ['Default (' + languageName2(ec.AudioLanguage) + ')', 'Default (' + languageName2(ec.SubtitleLanguage) + ')'];
    const movieDlg2 = await openItemMenuDialog(t, page, movieId);
    await t.check(!!movieDlg2 && movieDlg2.fields[0].text === wantMovie[0] && movieDlg2.fields[1].text === wantMovie[1],
        'with a Movies library choice (German, subtitles Off) the movie dialog\'s Default reads what Me/Choice plays without an override: "' + (movieDlg2 && movieDlg2.fields[0].text) + '" / "' + (movieDlg2 && movieDlg2.fields[1].text) + '" (Me/Choice: '
        + mc.AudioLanguage + ', subtitle #' + mc.SubtitleIndex + '; want "' + wantMovie.join('" / "') + '")', page);
    await escapeBfDialog(page, DETAIL_PAGE + ' .btnMoreCommands');
    const showDlg2 = await openItemMenuDialog(t, page, epId);
    await t.check(!!showDlg2 && showDlg2.fields[0].text === wantShow[0] && showDlg2.fields[1].text === wantShow[1],
        'with an Anime library choice (Japanese, subtitles English) the show dialog\'s Default reads what Me/Choice plays: "' + (showDlg2 && showDlg2.fields[0].text) + '" / "' + (showDlg2 && showDlg2.fields[1].text) + '" (Me/Choice: '
        + ec.AudioLanguage + ', ' + ec.SubtitleLanguage + '; want "' + wantShow.join('" / "') + '")', page);
    await escapeBfDialog(page, DETAIL_PAGE + ' .btnMoreCommands');
    // Back to nothing chosen, so the track Me/Choice plays for the movie is the video's own again.
    await putMe(t, nothing, 'nothing chosen again');

    // 11f. Save writes the override through the API: German audio and Off subtitles for the movie (the audio that doesn't play now).
    const movieStreams = (((await t.api.get('/Users/' + t.api.userId + '/Items/' + movieId)).body || {}).MediaSources || [{}])[0].MediaStreams || [];
    const movieAudios = movieStreams.filter((s) => s.Type === 'Audio');
    const nowPlays = (((await choiceOf(t, movieId, (b) => !!(b && b[0]))).body || [])[0] || {});
    const otherAudio = movieAudios.find((s) => s.Index !== nowPlays.AudioIndex) || movieAudios[0];
    const otherCode = ({ eng: 'en', jpn: 'ja', deu: 'de', ger: 'de' })[String(otherAudio.Language).toLowerCase()] || String(otherAudio.Language).slice(0, 2);
    const dlgSave = await openItemMenuDialog(t, page, movieId);
    const pickedA = await pickInBfDialog(page, 'Audio', 'lang:' + otherCode);
    const pickedS = await pickInBfDialog(page, 'Subtitles', 'off');
    const beforeSave = page.requests.length;
    await page.click(pgBfDialogButton, { args: ['Save'], what: 'Save in the dialog' });
    const savedClosed = await waitNoBfDialog(page, 10000);
    const stored = await t.api.get(ME_OVERRIDES + '/Target/' + compactId(movieId));
    const ovStored = stored.body && stored.body.Override;
    const rows1 = await overrideSummaries(t);
    await t.check(!!dlgSave && pickedA.ok && pickedS.ok && savedClosed && !!ovStored && same(tableOf(ovStored.Settings), storedFor('lang:' + otherCode, 'off')) && rows1.length === 1 && rows1[0].Scope === 'Title',
        'Save wrote the override: ' + languageName2(otherCode) + ' audio and Off subtitles for the movie, scope Title (GET Me/Overrides/Target: ' + (ovStored ? canon(tableOf(ovStored.Settings)) : 'no override') + '; ' + (rows1 || []).length + ' override)', page);
    const puts = page.requests.slice(beforeSave).filter((r) => r.method === 'PUT' && /\/Babelfin\/Me\/Overrides\//.test(r.url));
    await t.check(puts.length === 1 && /Overrides\/[0-9a-f]{32}\?scope=Title$/.test(puts[0].url), 'it sent one PUT Me/Overrides/{movie}?scope=Title (' + puts.map((r) => r.url.split('/Babelfin/')[1]).join(', ') + ')', null);
    const afterChoice = (((await choiceOf(t, movieId, (b) => b && b[0] && b[0].AudioIndex === otherAudio.Index)).body || [])[0] || {});
    await t.check(afterChoice.AudioIndex === otherAudio.Index && (afterChoice.SubtitleIndex === undefined || afterChoice.SubtitleIndex === null || afterChoice.SubtitleIndex === -1),
        'Me/Choice now plays the track picked in the dialog: audio #' + afterChoice.AudioIndex + ' (' + afterChoice.AudioName + '), no subtitles', null);
    let tracksAfter = null;
    try {
        tracksAfter = await page.waitFor(function (sel, audio, subtitles) {
            var p = document.querySelector(sel), a = p && p.querySelector('select.selectAudio'), b = p && p.querySelector('select.selectSubtitles');
            return a && b && a.value === String(audio) && b.value === String(subtitles) ? [a.value, b.value] : null;
        }, { timeout: 8000, what: 'the page track dropdowns to follow the saved choice' }, DETAIL_PAGE, afterChoice.AudioIndex, afterChoice.SubtitleIndex == null ? -1 : afterChoice.SubtitleIndex);
    } catch (e) { tracksAfter = null; }
    await t.check(!!tracksAfter, 'the page track dropdowns refreshed to the saved backend choice (' + JSON.stringify(tracksAfter) + ')', page);
    await checkNoItemChip(t, page, 'after saving the movie override');

    // 11g. The dialog for an override that exists: its values, "Remove override"; both dropdowns back on Default delete it.
    const dlgAgain = await openItemMenuDialog(t, page, movieId);
    await t.check(!!dlgAgain && dlgAgain.fields[0].value === 'lang:' + otherCode && dlgAgain.fields[1].value === 'off' && dlgAgain.buttons.some((b) => b.text === 'Remove override'),
        'reopened, the dialog shows ' + languageName2(otherCode) + ' / Off and offers "Remove override" (' + (dlgAgain ? dlgAgain.fields.map((f) => f.value).join(' / ') + '; ' + dlgAgain.buttons.map((b) => b.text).join(', ') : 'no dialog') + ')', page);
    await dialogHeaderChecks(t, page, 'the dialog of a movie with an override', dlgAgain, { name: movieTarget.Name, library: 'Movies', kind: 'movie', narrower: false, remove: true });
    // "Default (X)" is what applies without the override: with an override stored it reads as it did before (not "Default (English)").
    await t.check(!!dlgAgain && dlgAgain.fields[0].text !== undefined && dlgAgain.fields[0].options[0].l === movieAudio.text && dlgAgain.fields[1].options[0].l === movieSubs.text,
        'with the override stored, "Default (…)" still reads what applies without it ("' + (dlgAgain && dlgAgain.fields[0].options[0].l) + '" / "' + (dlgAgain && dlgAgain.fields[1].options[0].l) + '", as before: "' + movieAudio.text + '" / "' + movieSubs.text + '")', page);
    await pickInBfDialog(page, 'Audio', '');
    await pickInBfDialog(page, 'Subtitles', '');
    const mark = page.requests.length;
    await page.click(pgBfDialogButton, { args: ['Save'], what: 'Save with both dropdowns on Default' });
    await waitNoBfDialog(page, 10000);
    const gone = await overrideSummaries(t);
    const sent = page.requests.slice(mark).filter((r) => /\/Babelfin\/Me\/Overrides\/[0-9a-f]{32}/.test(r.url)).map((r) => r.method + ' ' + r.url.split('/Babelfin/')[1]);
    await t.check(gone.length === 0 && sent.length === 1 && /^DELETE Me\/Overrides\/[0-9a-f]{32}\?scope=Title$/.test(sent[0]),
        'both dropdowns back on Default and Save removed the override: one DELETE, no empty override stored (' + sent.join(', ') + '; ' + gone.length + ' left)', page);
    const noMore = (await t.api.get(ME_OVERRIDES + '/Target/' + compactId(movieId))).body || {};
    await t.check(!noMore.Override, 'GET Me/Overrides/Target/{movie} has no override any more', null);

    // 11h. Remove override (on the show, from an episode page): the whole override goes and a toast says so.
    const putShow = await putOverride(t, seriesId, 'Series', layerFor('lang:ja', 'lang:en'));
    await t.check(putShow.status === 200, 'set up: an override for the show through the API (' + putShow.status + ')', null);
    const dlgRemove = await openItemMenuDialog(t, page, epId);
    await t.check(!!dlgRemove && dlgRemove.buttons.some((b) => b.text === 'Remove override') && dlgRemove.fields[0].value === 'lang:ja' && dlgRemove.fields[1].value === 'lang:en',
        'the show\'s dialog opened from an episode shows its override and "Remove override" (' + (dlgRemove ? dlgRemove.fields.map((f) => f.value).join(' / ') : 'no dialog') + ')', page);
    const mark2 = page.requests.length;
    await page.click(pgBfDialogButton, { args: ['Remove override'], what: 'Remove override' });
    let toastText = null;
    try {
        toastText = await page.waitFor(function (text) {
            var n = Array.prototype.find.call(document.querySelectorAll('.bfui-toasts .bfui-toast, .bfToast'), function (x) { return x.textContent.trim() === text; });
            return n ? n.textContent.trim() : null;
        }, { timeout: 8000, what: 'the "Override removed" toast' }, overrideWord('dialog.removed'));
    } catch (e) {
        toastText = null;
    }
    const closed2 = await waitNoBfDialog(page, 5000);
    const left = await overrideSummaries(t);
    const sent2 = page.requests.slice(mark2).filter((r) => /\/Babelfin\/Me\/Overrides\/[0-9a-f]{32}/.test(r.url)).map((r) => r.method + ' ' + r.url.split('/Babelfin/')[1]);
    await t.check(closed2 && left.length === 0 && sent2.length === 1 && /^DELETE Me\/Overrides\/[0-9a-f]{32}\?scope=Series$/.test(sent2[0]) && toastText === overrideWord('dialog.removed'),
        'Remove override sent DELETE Me/Overrides/{show}?scope=Series, closed the dialog and said "' + overrideWord('dialog.removed') + '" (' + sent2.join(', ') + '; toast ' + JSON.stringify(toastText) + '; ' + left.length + ' left)', page);

    // 11i. A narrower override (an episode-level one, set through the API) is announced in the show's dialog, and in no other.
    const epOv = await putOverride(t, epId, 'Title', layerFor('lang:en', undefined));
    const narrowTarget = (await t.api.get(ME_OVERRIDES + '/Target/' + compactId(seriesId))).body || {};
    await t.check(epOv.status === 200 && narrowTarget.HasNarrower === true, 'set up: an override for one episode (' + epOv.status + '); GET Me/Overrides/Target/{show} says HasNarrower ' + narrowTarget.HasNarrower, null);
    const narrowDlg = await openItemMenuDialog(t, page, seriesId);
    await dialogHeaderChecks(t, page, 'the show\'s dialog while one of its episodes has an override', narrowDlg, { name: 'Astercoil Test', library: 'Anime', kind: 'show', narrower: true, remove: false });
    await escapeBfDialog(page, DETAIL_PAGE + ' .btnMoreCommands');
    const movieNoNote = await openItemMenuDialog(t, page, movieId);
    await t.check(!!movieNoNote && !movieNoNote.explains.some((x) => apos(x) === overrideWord('dialog.narrower')), 'a movie\'s dialog has no such note', page);
    await escapeBfDialog(page, DETAIL_PAGE + ' .btnMoreCommands');
    await deleteOverride(t, epId, 'Title');
    const goneNarrow = (await t.api.get(ME_OVERRIDES + '/Target/' + compactId(seriesId))).body || {};
    await putOverride(t, t.ids.seasonId, 'Season', layerFor(undefined, 'signs'));
    const seasonNarrow = (await t.api.get(ME_OVERRIDES + '/Target/' + compactId(seriesId))).body || {};
    await deleteOverride(t, t.ids.seasonId, 'Season');
    const noNarrow = (await t.api.get(ME_OVERRIDES + '/Target/' + compactId(seriesId))).body || {};
    await t.check(goneNarrow.HasNarrower === false && seasonNarrow.HasNarrower === true && noNarrow.HasNarrower === false,
        'HasNarrower follows the overrides under the show: false without any, true with a season override, false again after it is removed (' + [goneNarrow.HasNarrower, seasonNarrow.HasNarrower, noNarrow.HasNarrower].join(', ') + ')', null);

    // 11j. The API behind the dialog: a season stands for its show, other items are refused, unknown ones are not found, the list names items and libraries.
    const seasonTarget = await t.api.get(ME_OVERRIDES + '/Target/' + compactId(t.ids.seasonId));
    await t.check(seasonTarget.status === 200 && compactId(seasonTarget.body.TargetId) === compactId(seriesId) && seasonTarget.body.Scope === 'Series' && seasonTarget.body.Type === 'Series',
        'GET Me/Overrides/Target/{season}: its show (' + JSON.stringify([seasonTarget.status, seasonTarget.body && seasonTarget.body.Scope, seasonTarget.body && seasonTarget.body.Type]) + ')', null);
    const libTarget = await t.api.get(ME_OVERRIDES + '/Target/' + compactId(t.ids.libraries.Anime));
    const noTarget = await t.api.get(ME_OVERRIDES + '/Target/00000000000000000000000000000001');
    await t.check(libTarget.status === 400 && /shows and movies only/.test((libTarget.body && libTarget.body.Message) || '') && noTarget.status === 404,
        'GET Me/Overrides/Target for a library answers 400 ("' + ((libTarget.body && libTarget.body.Message) || libTarget.status) + '") and for an unknown item 404 (' + noTarget.status + ')', null);
    await putOverride(t, movieId, 'Title', layerFor('lang:de', 'off'));
    await putOverride(t, t.ids.seasonId, 'Season', layerFor(undefined, 'signs'));
    const listed = (await overrideSummaries(t)) || [];
    const lm = overrideOf(listed, movieId) || {};
    const ls = overrideOf(listed, t.ids.seasonId) || {};
    await t.check(lm.Name === 'Berlin Test (2019)' && lm.Type === 'Movie' && lm.ProductionYear === 2019 && lm.LibraryName === 'Movies' && !!lm.LibraryId && lm.Scope === 'Title' && !!lm.UpdatedAt && !!lm.Settings
        && ls.Type === 'Season' && ls.SeriesName === 'Astercoil Test' && ls.LibraryName === 'Anime' && ls.Scope === 'Season',
        'GET Me/Overrides lists each override with the item\'s current name, type, year, show, library and stored settings (movie: ' + JSON.stringify([lm.Name, lm.Type, lm.ProductionYear, lm.LibraryName]) + '; season: ' + JSON.stringify([ls.Name, ls.Type, ls.SeriesName, ls.LibraryName]) + ')', null);
    await t.api.del(ME_OVERRIDES + '/All');

    // 11k. The burned-in subtitles row still works: flag a language, see it back, take it off.
    const burnDlg = await openItemMenuDialog(t, page, movieId);
    await t.check(!!burnDlg && !!burnDlg.burned && !burnDlg.burned.checked && /burned-in subtitles/.test(burnDlg.burned.label || ''), 'the dialog has the burned-in subtitles row, unchecked (' + JSON.stringify(burnDlg && burnDlg.burned && [burnDlg.burned.label, burnDlg.burned.checked]) + ')', page);
    if (burnDlg && burnDlg.burned) {
        await page.evaluate(function () {
            var b = document.querySelector('.bfDialog .bfBurned');
            var text = b.querySelector('input[type="text"]');
            text.focus();
            text.value = 'ja';
            text.dispatchEvent(new Event('input', { bubbles: true }));
        });
        await page.click(pgBfDialogButton, { args: ['Save'], what: 'Save with a burned-in language' });
        await waitNoBfDialog(page, 10000);
        const flagged = ((await getMe(t)).Settings.BurnedIn || []).find((f) => compactId(f.ItemId) === compactId(movieId) && f.Scope === 'Title');
        const noOverride = (await overrideSummaries(t)).length === 0;
        await t.check(!!flagged && same(flagged.Languages, ['ja']) && noOverride, 'Save with a typed language flagged burned-in Japanese subtitles on the movie and stored no override (' + (flagged ? canon(flagged.Languages) : 'no flag') + ')', page);
        const burnAgain = await openItemMenuDialog(t, page, movieId);
        await t.check(!!burnAgain && burnAgain.burned.checked && /ja|Japanese/i.test(burnAgain.burned.select + ' ' + burnAgain.burned.selectText + ' ' + burnAgain.burned.text), 'reopened, the row is checked and names Japanese (' + (burnAgain ? JSON.stringify(burnAgain.burned) : 'no dialog') + ')', page);
        await page.evaluate(function () {
            var c = document.querySelector('.bfDialog .bfBurned input[type="checkbox"]');
            c.checked = false;
            c.dispatchEvent(new Event('change', { bubbles: true }));
        });
        await page.click(pgBfDialogButton, { args: ['Save'], what: 'Save with the burned-in row unchecked' });
        await waitNoBfDialog(page, 10000);
        const unflagged = !((await getMe(t)).Settings.BurnedIn || []).some((f) => compactId(f.ItemId) === compactId(movieId));
        await t.check(unflagged, 'unchecking the row and saving removed the flag again', page);
    }
}

// ───────────────────────────── 12. Phone width ─────────────────────────────

/** Every visible table dropdown has a visible "Audio"/"Subtitles" label and fits the screen. */
function pgPhoneLabels() {
    function vis(el) { var r = el.getBoundingClientRect(); var cs = getComputedStyle(el); return r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden'; }
    var out = [];
    Array.prototype.forEach.call(document.querySelectorAll('select[aria-label]'), function (s) {
        var aria = s.getAttribute('aria-label');
        var m = /^(Audio|Subtitles) for /.exec(aria);
        if (!m || !vis(s) || s.closest('.hide') || s.closest('[hidden]')) { return; }
        var want = m[1];
        var found = null;
        if (s.id) {
            var l = document.querySelector('label[for="' + s.id + '"]');
            if (l && vis(l) && l.textContent.trim() === want) { found = 'label'; }
        }
        var cell = s.closest('td, th, .bfui-table-cell, [data-label]') || s.parentNode;
        if (!found && cell) {
            Array.prototype.forEach.call(cell.querySelectorAll('*'), function (n) {
                if (!found && n !== s && !s.contains(n) && n.children.length === 0 && n.textContent.trim() === want && vis(n)) { found = 'text'; }
            });
            ['::before', '::after'].forEach(function (pseudo) {
                var cs = getComputedStyle(cell, pseudo);
                var content = cs.content && cs.content !== 'none' ? cs.content.replace(/^["']|["']$/g, '') : '';
                if (!found && content === want && cs.display !== 'none' && cs.visibility !== 'hidden') { found = 'cell ' + pseudo; }
            });
        }
        var r = s.getBoundingClientRect();
        out.push({ aria: aria, found: found, within: r.left >= 0 && r.right <= window.innerWidth + 0.5 });
    });
    return out;
}

async function phoneTableChecks(t, page, where) {
    const widths = await page.evaluate('({ doc: document.documentElement.scrollWidth, body: document.body.scrollWidth, inner: window.innerWidth })');
    await t.check(widths.doc <= widths.inner && widths.body <= widths.inner, where + ': no horizontal scroll (scrollWidth ' + widths.doc + '/' + widths.body + ', innerWidth ' + widths.inner + ')', page);
    const labels = await page.evaluate(pgPhoneLabels);
    const unlabeled = labels.filter((x) => !x.found).map((x) => x.aria);
    const outside = labels.filter((x) => !x.within).map((x) => x.aria);
    await t.check(labels.length > 0 && unlabeled.length === 0 && outside.length === 0, where + ': every dropdown has a visible "Audio" or "Subtitles" label and fits the width (' + labels.length + ' dropdowns; '
        + Array.from(new Set(labels.map((x) => x.found))).join(', ') + ')' + (unlabeled.length ? '; unlabelled: ' + unlabeled.join(', ') : '') + (outside.length ? '; outside: ' + outside.join(', ') : ''), page);
}

/** The segmented control fits the screen: the group and both radios inside the width, their text not cut off. */
async function modeFits(t, page, scope, where) {
    const info = await modeInfo(page, scope);
    const width = await page.evaluate('window.innerWidth');
    const bad = [];
    if (!info.found) {
        bad.push('no radiogroup');
    } else {
        if (info.box.left < -0.5 || info.box.right > width + 0.5) {
            bad.push('the group spans ' + Math.round(info.box.left) + '–' + Math.round(info.box.right) + ' px');
        }
        info.radios.forEach((r) => {
            if (!r.visible) {
                bad.push('"' + r.label + '" not visible');
            } else if (r.box.left < -0.5 || r.box.right > width + 0.5) {
                bad.push('"' + r.label + '" spans ' + Math.round(r.box.left) + '–' + Math.round(r.box.right) + ' px');
            }
            if (r.clipped) {
                bad.push('"' + r.label + '" text is cut off');
            }
        });
    }
    await t.check(!bad.length, where + ': the "' + SPEC.modeSame + '" / "' + SPEC.modePer + '" control fits ' + width + ' px' + (bad.length ? ' — ' + bad.join('; ') : ''), page);
}

async function testPhoneWidth(t) {
    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    t.defer('viewport back to desktop', () => page.clearViewport());
    const libs = libraryNames(kept.me);
    const start = withTable(t, kept.snapshot, { perLibrary: false });
    await putMe(t, start, 'every library, nothing chosen');
    const stored = (await getMe(t)).Settings;
    await page.setViewport(375, 812, true);
    await openPersonPage(t, page);
    await sleep(500);
    await phoneTableChecks(t, page, 'person page at 375x812, "' + SPEC.modeSame + '"');
    await modeFits(t, page, null, 'person page at 375x812');
    const info = await modeInfo(page, null);
    if (info.found) {
        await clickMode(page, null, SPEC.modePer);
        const r = await waitMode(page, null, SPEC.modePer, libs);
        await t.check(r.ok, 'tapping "' + SPEC.modePer + '" at phone width shows one row per library (' + modeText(r) + ')', page);
        await sleep(300);
        await phoneTableChecks(t, page, 'person page at 375x812, "' + SPEC.modePer + '"');
    } else {
        // Without the mode control: change something with the keyboard so the save bar shows.
        await page.evaluate(pgFocusSelect, null, SPEC.subtitlesFor(SPEC.allLibraries));
        await page.press('ArrowDown');
    }
    await sleep(t.settle);
    const bar = await page.evaluate(function () {
        var b = document.querySelector('.bf-savebar');
        if (!b || b.hidden || b.classList.contains('hide')) { return { shown: false }; }
        var buttons = Array.prototype.slice.call(b.querySelectorAll('button')).map(function (x) {
            var r = x.getBoundingClientRect();
            var at = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
            return { text: x.textContent.trim(), onScreen: r.top >= 0 && r.bottom <= window.innerHeight && r.left >= 0 && r.right <= window.innerWidth, hit: !!at && (at === x || x.contains(at)), h: Math.round(r.height), w: Math.round(r.width) };
        });
        return { shown: true, buttons: buttons, position: getComputedStyle(b).position };
    });
    const save = bar.shown ? bar.buttons.find((b) => b.text === 'Save') : null;
    await t.check(!!save && save.onScreen && save.hit, 'after a change the save bar shows with Save on screen and not covered (' + JSON.stringify(bar.buttons || []) + ', ' + bar.position + ')', page);
    await page.click(pgButton, { args: ['.bf-savebar', 'Discard'], what: 'Discard at phone width' });
    try {
        await page.click(pgDialogButton, { args: ['Discard'], what: 'Discard in the confirmation', timeout: 5000 });
    } catch (e) {
        // no confirmation
    }
    await sleep(t.settle);
    const after = await snap(page, null);
    await t.check(after.saveBar && !after.saveBar.visible && same((await getMe(t)).Settings, stored), 'Discard works by tapping at phone width and nothing was saved', page);
    // The admin page at phone width.
    await openAdminPage(t, page);
    await sleep(500);
    const adminWidths = await page.evaluate('({ doc: document.documentElement.scrollWidth, inner: window.innerWidth })');
    await t.check(adminWidths.doc <= adminWidths.inner, 'admin page at 375x812 has no horizontal page scroll (scrollWidth ' + adminWidths.doc + ', innerWidth ' + adminWidths.inner + ')', page);
    await modeFits(t, page, LIBS, 'admin Libraries table at 375x812');
}

// ───────────────────────────── 13. Hygiene ─────────────────────────────

const FILTERS = [
    { why: "jellyfin-web's websocket (refused before sign-in)", console: /WebSocket connection to '[^']*\/socket[^']*' failed/i, response: /\/socket(\?|$)/i },
    { why: 'image 404 (jellyfin-web artwork that the test media has none of)', response: /\/(Items|Users|Persons|Genres|Studios)\/[^/]+\/Images\//i, status: [404] },
    { why: "the browser's automatic /favicon.ico request on /Babelfin/app (a 404: the page links no icon)", response: /^https?:\/\/[^/]+\/favicon\.ico$/i, status: [404], console: /favicon\.ico/i }
];

function isBabelfin(entry) {
    const where = [entry.url || ''].concat(entry.frames || []).join(' ');
    return /\/babelfin\//i.test(where) || /babelfin/i.test(entry.text || '') || /\/babelfin\/app/i.test(entry.page || '');
}

async function testHygiene(t) {
    const consoleAll = [].concat(...t.pages.map((p) => p.console.map(c => Object.assign({ owner: p }, c))));
    const exceptions = [].concat(...t.pages.map((p) => p.exceptions));
    const responses = [].concat(...t.pages.map((p) => p.badResponses.map(r => Object.assign({ owner: p }, r))));
    const requests = [].concat(...t.pages.map((p) => p.requests));
    const dialogs = [].concat(...t.pages.map((p) => p.dialogs));
    const errors = consoleAll.filter((c) => c.type === 'error' || c.type === 'assert');
    const filteredConsole = (c) => FILTERS.find((f) => f.console && f.console.test(c.text + ' ' + (c.url || '')));
    const expectedMock = (entry, consoleEntry) => t.expectedMockFailures.find(f => f.owner === entry.owner && f.url === entry.url
        && entry.at >= f.from && entry.at <= f.until && (consoleEntry
            ? entry.source === 'network' && /Failed to load resource/i.test(entry.text || '') && entry.text.includes(String(f.status))
            : entry.status === f.status));
    const bfErrors = errors.filter((c) => isBabelfin(c) && !filteredConsole(c) && !expectedMock(c, true));
    const bfExceptions = exceptions.filter(isBabelfin);
    await t.check(bfErrors.length === 0, 'no console errors from Babelfin scripts or pages across the run (' + bfErrors.length + ')'
        + (bfErrors.length ? ':\n       ' + bfErrors.slice(0, 10).map((c) => c.text.slice(0, 240) + ' @ ' + (c.url || c.page)).join('\n       ') : ''));
    await t.check(bfExceptions.length === 0, 'no uncaught exceptions from Babelfin scripts across the run (' + bfExceptions.length + ')'
        + (bfExceptions.length ? ':\n       ' + bfExceptions.slice(0, 10).map((e) => e.text.split('\n').slice(0, 3).join(' | ').slice(0, 300) + ' @ ' + e.url).join('\n       ') : ''));
    const bfResponses = responses.filter((r) => /\/babelfin\//i.test(r.url) && !expectedMock(r, false));
    await t.check(bfResponses.length === 0, 'no 4xx/5xx responses for /Babelfin/ URLs (' + bfResponses.length + ')'
        + (bfResponses.length ? ':\n       ' + bfResponses.slice(0, 10).map((r) => r.status + ' ' + r.url + ' (on ' + r.page + ')').join('\n       ') : ''));
    const coverage = requests.filter((r) => /\/Babelfin\/Admin\/Coverage/i.test(r.url));
    await t.check(coverage.length === 0, 'the pages never asked for Babelfin/Admin/Coverage (' + requests.length + ' requests seen)'
        + (coverage.length ? ':\n       ' + coverage.slice(0, 5).map((r) => r.method + ' ' + r.url + ' (on ' + r.page + ')').join('\n       ') : ''), null);
    const others = responses.filter((r) => !/\/babelfin\//i.test(r.url) && !expectedMock(r, false));
    const allowed = [];
    const unexpected = [];
    others.forEach((r) => {
        const f = FILTERS.find((x) => x.response && x.response.test(r.url) && (!x.status || x.status.indexOf(r.status) >= 0));
        (f ? allowed : unexpected).push({ r: r, why: f ? f.why : null });
    });
    await t.check(unexpected.length === 0, 'no other 4xx/5xx responses outside the listed filters (' + unexpected.length + ')'
        + (unexpected.length ? ':\n       ' + unexpected.slice(0, 10).map((x) => x.r.status + ' ' + x.r.url + ' (on ' + x.r.page + ')').join('\n       ') : ''));
    // What was filtered out or is jellyfin-web's own, listed.
    const groups = new Map();
    const add = (key, line) => { if (!groups.has(key)) { groups.set(key, new Set()); } groups.get(key).add(line); };
    allowed.forEach((x) => add('filtered response: ' + x.why, x.r.status + ' ' + x.r.url.replace(/([?&](api_key|ApiKey|deviceId)=)[^&]+/gi, '$1…')));
    errors.filter((c) => !isBabelfin(c) || filteredConsole(c)).forEach((c) => {
        const f = filteredConsole(c);
        add(f ? 'filtered console error: ' + f.why : "jellyfin-web's own console error (not Babelfin's)", c.text.slice(0, 200).replace(/([?&](api_key|ApiKey)=)[^&'"]+/gi, '$1…'));
    });
    exceptions.filter((e) => !isBabelfin(e)).forEach((e) => add("jellyfin-web's own uncaught exception (not Babelfin's)", e.text.split('\n')[0].slice(0, 200) + ' @ ' + e.url));
    dialogs.forEach((d) => add('browser dialog (accepted)', d.type + ': ' + d.message + ' on ' + d.page));
    responses.filter(r => expectedMock(r, false)).forEach(r => add('expected loopback browser failure fixture', r.status + ' ' + r.url));
    const warnings = consoleAll.filter((c) => c.type === 'warning' && /\[Babelfin\]/.test(c.text));
    warnings.forEach((c) => add('Babelfin warning (not an error)', c.text.slice(0, 200)));
    groups.forEach((lines, key) => {
        t.note(key + ' (' + lines.size + '):');
        Array.from(lines).slice(0, 12).forEach((l) => console.log('       ' + l));
    });
}

// ───────────────────────────── Shared controls: option menus, switches, dialogs, i18n ─────────────────────────────

/** Every shown option menu and switch under scope outside the "Audio and subtitles" table (skip: a selector left out), described. */
function pgControls(scope, skip) {
    var h = window.__bfE2E;
    return h.controlsIn(scope, skip).map(h.controlInfo);
}

/** One control (want: { field, label | { re } }), described; null when it isn't shown. */
function pgControl(scope, want, skip) {
    var h = window.__bfE2E;
    var el = h.findControl(scope, want, skip);
    return el ? h.controlInfo(el) : null;
}

/** Picks a value in an option menu found by pgControl's rule (the browser's own input + change events). */
function pgSetMenu(scope, want, value, skip) {
    var h = window.__bfE2E;
    var s = h.findControl(scope, want, skip);
    if (!s || s.tagName !== 'SELECT') {
        return { ok: false, reason: s ? 'it is not a dropdown' : 'not found' };
    }
    var has = Array.prototype.some.call(s.options, function (o) { return o.value === value && !o.disabled; });
    if (!has) {
        return { ok: false, reason: 'option ' + JSON.stringify(value) + ' not offered (has ' + Array.prototype.map.call(s.options, function (o) { return o.value; }).join(', ') + ')' };
    }
    s.scrollIntoView({ block: 'center' });
    s.focus();
    if (s.value === value) {
        return { ok: true, unchanged: true };
    }
    s.value = value;
    s.dispatchEvent(new Event('input', { bubbles: true }));
    s.dispatchEvent(new Event('change', { bubbles: true }));
    return { ok: true };
}

/** The element to click to toggle a checkbox or switch (its label when the input itself is hidden or tiny). */
function pgControlClickEl(scope, want, skip) {
    var h = window.__bfE2E;
    var el = h.findControl(scope, want, skip);
    if (!el) {
        return null;
    }
    var r = el.getBoundingClientRect();
    var cs = getComputedStyle(el);
    if (h.vis(el) && r.width > 4 && r.height > 4 && Number(cs.opacity) > 0.05) {
        return el;
    }
    return (el.labels && el.labels[0]) || el.closest('label') || el;
}

/** Appends a line to the text area labelled `label` under scope (as typing would: an input event). */
function pgAppendLine(scope, label, line) {
    var h = window.__bfE2E;
    var r = h.root(scope);
    var area = r && Array.prototype.find.call(r.querySelectorAll('textarea'), function (a) { return h.controlLabel(a) === label && h.vis(a); });
    if (!area) {
        return { ok: false, reason: 'no text area labelled "' + label + '"' };
    }
    area.scrollIntoView({ block: 'center' });
    area.focus();
    area.value = (area.value ? area.value.replace(/\s+$/, '') + '\n' : '') + line;
    area.dispatchEvent(new Event('input', { bubbles: true }));
    area.dispatchEvent(new Event('change', { bubbles: true }));
    return { ok: true, value: area.value };
}

/** The value of the text area labelled `label` under scope (null when there is none). */
function pgTextArea(scope, label) {
    var h = window.__bfE2E;
    var r = h.root(scope);
    var area = r && Array.prototype.find.call(r.querySelectorAll('textarea'), function (a) { return h.controlLabel(a) === label && h.vis(a); });
    return area ? area.value : null;
}

/** Types into the last empty text box of a group's table of rows (the Language codes table after "Add a code"). */
function pgFillLastRow(scope, section, group, values) {
    var h = window.__bfE2E;
    var outer = h.disclosure(scope, section);
    var r = outer ? h.regionOf(outer) : null;
    var ctrl = r ? h.disclosure(r, group) : null;
    var region = ctrl ? h.regionOf(ctrl) : null;
    if (!region) {
        return { ok: false, reason: 'no "' + group + '" content' };
    }
    var rows = Array.prototype.filter.call(region.querySelectorAll('tr'), function (tr) { return tr.querySelectorAll('input[type="text"]').length >= values.length && h.vis(tr); });
    var row = rows[rows.length - 1];
    if (!row) {
        return { ok: false, reason: 'no row with ' + values.length + ' text boxes' };
    }
    var inputs = row.querySelectorAll('input[type="text"]');
    values.forEach(function (v, i) {
        inputs[i].focus();
        inputs[i].value = v;
        inputs[i].dispatchEvent(new Event('input', { bubbles: true }));
        inputs[i].dispatchEvent(new Event('change', { bubbles: true }));
    });
    return { ok: true };
}

/**
 * A button called `text` inside the section `section`: whether it sits inside one of the section's groups, and whether it comes after
 * every group (and is the section's last visible control).
 */
function pgSectionButton(scope, section, text) {
    var h = window.__bfE2E;
    var ctrl = h.disclosure(scope, section);
    var region = ctrl ? h.regionOf(ctrl) : null;
    if (!region) {
        return { found: false, why: 'no "' + section + '" section' };
    }
    var buttons = Array.prototype.filter.call(region.querySelectorAll('button'), h.vis);
    var b = buttons.find(function (x) { return h.label(x) === text; });
    if (!b) {
        return { found: false, why: 'no button "' + text + '" in "' + section + '" (buttons outside groups: ' + buttons.filter(function (x) { return !x.getAttribute('aria-expanded'); }).map(h.label).slice(0, 8).join(', ') + ')' };
    }
    var groups = h.groups(region);
    var inGroup = groups.some(function (g) { var r = h.regionOf(g); return r && r !== region && r.contains(b); });
    var after = groups.every(function (g) {
        var r = h.regionOf(g) || g;
        return !!(g.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) && !r.contains(b) && !!(r.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
    });
    var later = Array.prototype.filter.call(region.querySelectorAll('button, select, input, textarea'), function (x) {
        return x !== b && h.vis(x) && !!(b.compareDocumentPosition(x) & Node.DOCUMENT_POSITION_FOLLOWING) && !b.contains(x);
    }).map(function (x) { return h.label(x) || h.controlLabel(x) || x.tagName.toLowerCase(); });
    return { found: true, inGroup: inGroup, afterGroups: after, groups: groups.map(function (g) { return h.label(g); }), later: later };
}

function pgSectionButtonEl(scope, section, text) {
    var h = window.__bfE2E;
    var ctrl = h.disclosure(scope, section);
    var region = ctrl ? h.regionOf(ctrl) : null;
    return region ? (Array.prototype.find.call(region.querySelectorAll('button'), function (x) { return h.vis(x) && h.label(x) === text; }) || null) : null;
}

/** The first visible button inside a group of a section (what a group like "Start over" holds). */
function pgGroupButtonEl(scope, section, group) {
    var h = window.__bfE2E;
    var outer = h.disclosure(scope, section);
    var r = outer ? h.regionOf(outer) : null;
    var ctrl = r ? h.disclosure(r, group) : null;
    var region = ctrl ? h.regionOf(ctrl) : null;
    return region ? (Array.prototype.find.call(region.querySelectorAll('button'), function (x) { return h.vis(x) && x !== ctrl; }) || null) : null;
}

/** The topmost in-page dialog: role, title, text and buttons (null when none shows). */
function pgDialog() {
    var h = window.__bfE2E;
    var boxes = Array.prototype.filter.call(document.querySelectorAll('[role="dialog"], [role="alertdialog"]'), function (b) { return h.vis(b) && !b.classList.contains('bfDialog'); });
    var box = boxes[boxes.length - 1];
    if (!box) {
        return null;
    }
    var title = box.querySelector('h1, h2, h3, .bfui-dialog-title');
    return {
        role: box.getAttribute('role'), modal: box.getAttribute('aria-modal'), title: title ? h.text(title) : '', text: h.text(box).slice(0, 300),
        buttons: Array.prototype.filter.call(box.querySelectorAll('button'), h.vis).map(function (b) { return h.text(b); }),
        focusInside: box.contains(document.activeElement)
    };
}

/** The dialog's button: confirm = the primary or danger one (else the last that isn't Cancel); otherwise Cancel (else the first). */
function pgDialogAnswerEl(confirm) {
    var h = window.__bfE2E;
    var boxes = Array.prototype.filter.call(document.querySelectorAll('[role="dialog"], [role="alertdialog"]'), function (b) { return h.vis(b) && !b.classList.contains('bfDialog'); });
    var box = boxes[boxes.length - 1];
    if (!box) {
        return null;
    }
    var buttons = Array.prototype.filter.call(box.querySelectorAll('button'), h.vis);
    var isCancel = function (b) { return /^(cancel|no|close|keep)/i.test(h.text(b)); };
    if (confirm) {
        return buttons.find(function (b) { return /bfui-btn-(primary|danger)|button-submit/.test(b.className) && !isCancel(b); })
            || buttons.filter(function (b) { return !isCancel(b); }).pop() || null;
    }
    return buttons.find(isCancel) || buttons[0] || null;
}

/** Waits for an in-page dialog (null after the timeout). */
async function waitDialog(page, timeout) {
    try {
        return await page.waitFor(pgDialog, { timeout: timeout || 5000, what: 'a confirmation dialog' });
    } catch (e) {
        return null;
    }
}

/** Answers the open dialog (confirm or cancel) and waits for it to close; returns whether it closed. */
async function answerDialog(page, confirm) {
    await page.click(pgDialogAnswerEl, { args: [!!confirm], what: confirm ? 'the confirming button of the dialog' : 'Cancel in the dialog', timeout: 5000 });
    try {
        await page.waitFor('!(' + pgDialog.toString() + ')()', { timeout: 5000, what: 'the dialog to close' });
        return true;
    } catch (e) {
        return false;
    }
}

/** The English strings of the person page (i18n/en.json) and the admin page (i18n-admin/en.json), loaded once. */
async function strings(t) {
    if (!t.strings) {
        const person = await t.api.get('/Babelfin/assets/i18n/en.json');
        const admin = await t.api.get('/Babelfin/assets/i18n-admin/en.json');
        t.strings = { person: person.status === 200 ? person.body : {}, admin: admin.status === 200 ? admin.body : {} };
    }
    return t.strings;
}

/** A person-page menu entry (PERSON_MENUS) as the page functions take it. */
function wantOf(m) {
    return { field: m.field, label: m.label instanceof RegExp ? { re: m.label.source } : m.label };
}

function matchesMenu(info, m) {
    return (!!info.field && info.field === m.field) || (m.label instanceof RegExp ? m.label.test(info.label) : info.label === m.label);
}

/** The text the person page shows for a value of a field (from i18n/en.json). */
function valueText(en, m, v) {
    if (m.bool) {
        return en['field.' + m.field + '.' + String(v)] || en[String(v) === 'true' ? 'ui.yes' : 'ui.no'];
    }
    return en['enum.' + m.enumName + '.' + v];
}

/** Whether a control shows the value v (an option value equal to v, else an option text equal to v's English name). */
function showsValue(ctrl, en, m, v) {
    if (!ctrl || v === undefined || v === null) {
        return false;
    }
    if (ctrl.kind === 'checkbox') {
        return ctrl.value === String(!!v);
    }
    const text = valueText(en, m, v);
    return ctrl.value === String(v) || (text !== undefined && ctrl.text === text);
}

/** The stored form of an option: its value when that is one of the field's values, else the value whose English name is the option's text. */
function storedOf(en, m, option) {
    if (m.bool) {
        if (option.v === 'true' || option.v === 'false') {
            return option.v === 'true';
        }
        const yes = en['field.' + m.field + '.true'] || en['ui.yes'];
        const no = en['field.' + m.field + '.false'] || en['ui.no'];
        return option.l === yes ? true : (option.l === no ? false : undefined);
    }
    if (en['enum.' + m.enumName + '.' + option.v] !== undefined) {
        return option.v;
    }
    const key = Object.keys(en).find((k) => k.indexOf('enum.' + m.enumName + '.') === 0 && en[k] === option.l);
    return key ? key.slice(('enum.' + m.enumName + '.').length) : option.v;
}

/** What a person-page field is when nothing is stored: AdvancedDefaults.Profile of Me/Settings; { value, from }. */
function personDefault(me, m) {
    const ad = me.AdvancedDefaults || {};
    const p = ad.Profile || {};
    if (m.person) {
        return { value: ad[m.field] === undefined ? false : ad[m.field], from: ad[m.field] === undefined ? 'built-in false' : 'AdvancedDefaults.' + m.field };
    }
    if (p[m.field] !== undefined && p[m.field] !== null) {
        return { value: p[m.field], from: 'AdvancedDefaults.Profile.' + m.field };
    }
    if (ad[m.field] !== undefined && ad[m.field] !== null) {
        return { value: ad[m.field], from: 'AdvancedDefaults.' + m.field };
    }
    // The resolved profile has no Memory: the engine's built-in default is the rest of the show (StarterRules).
    return m.field === 'Memory' ? { value: 'RestOfShow', from: 'the built-in RestOfShow (not in AdvancedDefaults)' } : { value: undefined, from: 'nothing in AdvancedDefaults' };
}

/** A document without any of the person-page Advanced fields Restore defaults clears (and HandleNextEpisode false). */
function withoutAdvanced(doc) {
    const d = clone(doc);
    d.Everyday = d.Everyday || {};
    PERSON_RESTORED.forEach((f) => { delete d.Everyday[f]; });
    d.HandleNextEpisode = false;
    return d;
}

/** A control's options other than its current value and the avoided ones (the values a test can pick instead). */
function otherOptions(ctrl, avoid) {
    return (ctrl.options || []).filter((o) => !o.d && o.v !== ctrl.value && (avoid || []).indexOf(o.v) < 0);
}

/** "No Default (…) entry and nothing repeated" for every dropdown in a list of controls; returns the problems. */
function plainMenuProblems(controls) {
    const p = [];
    controls.filter((c) => c.kind === 'select').forEach((c) => {
        const texts = c.options.map((o) => o.l);
        const def = texts.filter((x) => /^Default(\s*\(|$)/.test(x));
        if (def.length) {
            p.push('"' + (c.label || '?') + '" offers ' + def.map((x) => '"' + x + '"').join(', '));
        }
        const dup = texts.filter((x, i) => x && texts.indexOf(x) !== i);
        if (dup.length) {
            p.push('"' + (c.label || '?') + '" lists ' + uniq(dup).map((x) => '"' + x + '"').join(', ') + ' twice');
        }
    });
    return p;
}

/** The save bar of the person page, or the admin page's (#bfSaveBar), as { visible, saveEnabled }. */
async function barOf(page, admin) {
    const s = await snap(page, admin ? ADMIN : null, null, admin ? ADMIN + ' #bfSaveBar' : '.bf-savebar');
    return s.saveBar || { visible: false, saveEnabled: false };
}

/** Clicks a checkbox or switch found by pgControl's rule and waits until it shows `want` (true/false); returns whether it does. */
async function setSwitch(page, scope, want, on) {
    const now = await page.evaluate(pgControl, scope || null, want, null);
    if (!now) {
        return false;
    }
    if (now.kind !== 'checkbox') {
        return false;
    }
    if ((now.value === 'true') !== on) {
        await page.click(pgControlClickEl, { args: [scope || null, want, null], what: 'the "' + (want.label && want.label.re ? want.label.re : want.label) + '" switch' });
        await sleep(200);
    }
    const after = await page.evaluate(pgControl, scope || null, want, null);
    return !!after && (after.value === 'true') === on;
}

/** Opens (or closes) one group of a section only when it isn't in that state yet (a click would toggle it); returns its description. */
async function ensureGroup(page, scope, section, title, open) {
    const g = await groupInfo(page, scope, section, title);
    if (!g.found || g.expanded === open) {
        return g;
    }
    return toggleGroup(page, scope, section, title, 'click', open);
}

/** Opens Advanced and every group in it (and on the admin page Troubleshooting, Back up and restore, and their groups). */
async function openEverything(page, admin) {
    if (!admin) {
        await openAllGroups(page, null, 'Advanced');
        return;
    }
    await openAllGroups(page, ADMIN, 'Advanced');
    await openAllGroups(page, ADMIN, await toolsTitle(page));
    if ((await sectionInfo(page, ADMIN, SPEC.backup)).found) {
        await setSection(page, ADMIN, SPEC.backup, true);
    }
    await sleep(800);
}

/** The English name of a language code, as the pages write it. */
function languageWords(code) {
    const c = String(code || '');
    if (LANGUAGE_NAMES[c.slice(0, 2)] && c.length === 2) {
        return LANGUAGE_NAMES[c];
    }
    try {
        return new Intl.DisplayNames(['en'], { type: 'language' }).of(c);
    } catch (e) {
        return c;
    }
}

/** The People table's words for a row of GET Admin/Users ([audio, subtitles]). */
function peopleWords(row) {
    if (!row.HasSettings) {
        return [SPEC.defaultWord, SPEC.defaultWord];
    }
    if (row.PerLibrary) {
        return [SPEC.perLibraryWords, SPEC.perLibraryWords];
    }
    const l = row.Table || {};
    let audio = 'Custom';
    const chain = l.AudioChain;
    if (!chain && !l.ForeignTitles) {
        audio = SPEC.defaultWord;
    } else if (chain && !l.ForeignTitles && chain.length === 1 && chain[0].Kind === 'VideoDefault') {
        audio = SPEC.videoDefault;
    } else if (chain && !l.ForeignTitles && chain.length === 2 && chain[1].Kind === 'VideoDefault' && ['MyLanguages', 'OriginalLanguage'].includes(chain[0].Kind)) {
        audio = SPEC.audioChoices[chain[0].Kind === 'MyLanguages' ? 'mine' : 'original'];
    } else if (chain && !l.ForeignTitles && chain.length === 2 && chain[0].Kind === 'Language' && chain[0].Language && !chain[0].TitleContains && chain[1].Kind === 'VideoDefault') {
        audio = languageWords(chain[0].Language);
    }
    const u = l.WhenAudioUnderstood || null;
    const n = l.WhenAudioNotUnderstood || null;
    const langs = l.SubtitleLanguages && l.SubtitleLanguages.length ? l.SubtitleLanguages : null;
    let subs = 'Custom';
    if (!u && !n && !langs) {
        subs = SPEC.defaultWord;
    } else if (!langs && u === 'VideoDefault' && n === 'VideoDefault') {
        subs = SPEC.videoDefault;
    } else if (!langs && u === 'Off' && n === 'Off') {
        subs = SPEC.subtitleLabels.off;
    } else if (!langs && u === 'PartialOnly' && n === 'Full') {
        subs = SPEC.adminSmartWords;
    } else if (!langs && u === 'PartialOnly' && n === 'PartialOnly') {
        subs = SPEC.subtitleLabels.signs;
    } else if (langs && langs.length === 1 && u === 'Full' && n === 'Full') {
        subs = languageWords(langs[0]);
    }
    return [audio, subs];
}

/** The People table: its column headers and, per person row, the user id, the cell texts and whether it has an Edit button. */
function pgPeopleTable(scope) {
    var h = window.__bfE2E;
    var p = document.querySelector(scope);
    var sec = p && (p.querySelector('[data-section="people"]') || Array.prototype.find.call(p.querySelectorAll('section'), function (s) {
        var t = s.querySelector('h2, h3, .bf-section-title');
        return t && h.text(t) === 'People';
    }));
    if (!sec) {
        return { found: false };
    }
    var table = Array.prototype.find.call(sec.querySelectorAll('table'), function (tb) { return !tb.closest('.bf-editor-row') && tb.querySelector('tr[data-user]'); });
    if (!table) {
        return { found: false };
    }
    var head = table.querySelector('thead tr');
    var rows = Array.prototype.filter.call(table.querySelectorAll('tr[data-user]'), function (tr) { return !tr.classList.contains('bf-editor-row'); });
    return {
        found: true,
        headers: head ? Array.prototype.map.call(head.children, function (c) { return h.text(c); }) : [],
        rows: rows.map(function (tr) {
            return {
                user: tr.getAttribute('data-user'),
                cells: Array.prototype.map.call(tr.children, function (c) { return h.text(c); }),
                edit: Array.prototype.some.call(tr.querySelectorAll('button'), function (b) { return h.text(b) === 'Edit'; }),
                text: h.text(tr)
            };
        })
    };
}

/** Text a person reads anywhere under scope, hidden or not: text nodes, option texts and the aria-label, title, placeholder and alt attributes. */
function pgAllWords(scope) {
    var r = scope ? document.querySelector(scope) : document.body;
    if (!r) {
        return '';
    }
    var parts = [];
    var walker = document.createTreeWalker(r, NodeFilter.SHOW_TEXT, {
        acceptNode: function (n) { return n.parentElement && n.parentElement.closest('script, style, noscript') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT; }
    });
    for (var n = walker.nextNode(); n; n = walker.nextNode()) {
        parts.push(n.nodeValue);
    }
    Array.prototype.forEach.call(r.querySelectorAll('[aria-label], [title], [placeholder], [alt]'), function (n) {
        ['aria-label', 'title', 'placeholder', 'alt'].forEach(function (a) { if (n.getAttribute(a)) { parts.push(n.getAttribute(a)); } });
    });
    return parts.join('\n').replace(/[‘’]/g, "'");
}

/** The visible blocks of the admin page in order: 'header', else each block's title (section heading, or the disclosure's summary). */
function pgAdminBlocks(scope) {
    var h = window.__bfE2E;
    var p = document.querySelector(scope);
    var dash = p && (p.querySelector('.bf-dash') || p);
    if (!dash) {
        return [];
    }
    return Array.prototype.filter.call(dash.children, h.vis).map(function (c) {
        if (c.classList.contains('bf-head')) {
            return 'header';
        }
        var hd = c.tagName === 'DETAILS' ? c.querySelector(':scope > summary') : c.querySelector('.bf-section-title, h2, h3, [aria-expanded]');
        return hd ? h.label(hd) : c.tagName.toLowerCase() + '.' + c.className;
    });
}

// ───────────────────────────── 14. Plain option menus ─────────────────────────────

async function testPlainMenus(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    const en = (await strings(t)).person;
    const neutral = adminNeutral(kept.admin);
    await putAdmin(t, neutral, 'neutral');
    await putMe(t, withoutAdvanced(kept.snapshot), 'no Advanced field stored');
    let me = await getMe(t);
    const ad = me.AdvancedDefaults || null;
    await t.check(!!(ad && ad.Profile), 'GET Me/Settings carries AdvancedDefaults.Profile (keys: ' + Object.keys(me).join(', ') + ')', null);
    if (ad && ad.Profile) {
        const mine = (me.Settings.Everyday || {}).Understood || ((me.Inherited || {}).Profile || {}).Understood || [];
        await t.check(same((ad.Profile.Understood || []).map((c) => c.toLowerCase()), mine.map((c) => c.toLowerCase())),
            'AdvancedDefaults keeps the person\'s My languages (' + (ad.Profile.Understood || []).join(', ') + '; the person: ' + mine.join(', ') + ')', null);
    }

    // 14a. The person page with Advanced and every group open: plain menus, each on its default.
    await openPersonPage(t, page);
    await openEverything(page, false);
    let all = await page.evaluate(pgControls, null);
    let p = plainMenuProblems(all);
    await t.check(all.some((c) => c.kind === 'select') && !p.length, 'person page, every group open: ' + all.filter((c) => c.kind === 'select').length + ' option menus outside the table, none with a "Default (…)" entry or a repeated option'
        + (p.length ? ' — ' + p.join('; ') : ''), page);
    const found = PERSON_MENUS.map((m) => ({ m: m, c: all.find((c) => matchesMenu(c, m)) }));
    const missing = found.filter((x) => !x.c).map((x) => x.m.field);
    const notMenus = found.filter((x) => x.c && !x.m.bool && x.c.kind !== 'select').map((x) => x.m.field);
    await t.check(!missing.length && !notMenus.length, 'person page: Advanced has a control for each of ' + PERSON_MENUS.map((m) => m.field).join(', ') + ' (dropdowns, or a switch for a Yes/No setting)'
        + (missing.length ? ' — missing: ' + missing.join(', ') : '') + (notMenus.length ? ' — not dropdowns: ' + notMenus.join(', ') : ''), page);
    const offDefault = found.filter((x) => x.c).map((x) => {
        const d = personDefault(me, x.m);
        return showsValue(x.c, en, x.m, d.value) ? null : x.m.field + ' shows ' + JSON.stringify(x.c.text || x.c.value) + ', its default is ' + JSON.stringify(d.value) + ' (' + d.from + ')';
    }).filter(Boolean);
    await t.check(found.some((x) => x.c) && !offDefault.length, 'person page, nothing stored: every menu shows the value from AdvancedDefaults (' + found.filter((x) => x.c).map((x) => x.m.field + '=' + personDefault(me, x.m).value).join(', ') + ')'
        + (offDefault.length ? ' — ' + offDefault.join('; ') : ''), page);
    const sw = await page.evaluate(pgControl, null, wantOf(NEXT_EPISODE), null);
    await t.check(!sw, 'person page: "' + NEXT_EPISODE.label + '" is not shown while it is off (' + (sw ? 'shown: ' + sw.kind + ' ' + sw.value : 'not shown') + ')', page);
    const tcHelp = en['advanced.trackChanges.help'];
    const tcText = await page.evaluate(function () { return document.body.innerText; });
    await t.check(!!tcHelp && tcText.indexOf(tcHelp) >= 0, 'person page: Track changes says what a remembered change does ("' + String(tcHelp).slice(0, 60) + '…")', page);

    // 14b. The server's own defaults reach the menus: the admin's Everyday sets Format, Sdh and AvoidCommentary.
    const adminEveryday = { Format: 'TextOnly', Sdh: 'Avoid', AvoidCommentary: false };
    const adminDoc = clone(neutral);
    adminDoc.Everyday = Object.assign({}, adminDoc.Everyday || {}, adminEveryday);
    const putA = await t.api.put('/Babelfin/Admin/Settings', adminDoc);
    let serverDefaults = false;
    if (putA.status === 200) {
        me = await getMe(t);
        const prof = ((me.AdvancedDefaults || {}).Profile) || {};
        serverDefaults = prof.Format === 'TextOnly' && prof.Sdh === 'Avoid' && prof.AvoidCommentary === false;
        await t.check(serverDefaults, 'with the admin\'s Everyday on Text only / SDH Avoid / no commentary skipping, AdvancedDefaults.Profile says Format ' + prof.Format + ', Sdh ' + prof.Sdh + ', AvoidCommentary ' + prof.AvoidCommentary, null);
        await reloadPersonPage(t, page);
        await openEverything(page, false);
        all = await page.evaluate(pgControls, null);
        const wrong = PERSON_MENUS.filter((m) => adminEveryday[m.field] !== undefined).map((m) => {
            const c = all.find((x) => matchesMenu(x, m));
            return showsValue(c, en, m, adminEveryday[m.field]) ? null : m.field + ' shows ' + JSON.stringify(c ? (c.text || c.value) : null);
        }).filter(Boolean);
        await t.check(!wrong.length, 'person page, nothing stored: Subtitle format shows "Text only", SDH subtitles "Avoid" and Skip commentary tracks "No" (the server\'s defaults)' + (wrong.length ? ' — ' + wrong.join('; ') : ''), page);
    } else {
        t.note('the admin\'s Everyday Format/Sdh/AvoidCommentary were refused (' + putA.status + '); the built-in defaults are used below');
        await putAdmin(t, neutral, 'neutral');
    }

    // 14c. Picking another value stores it; picking the default again clears the field.
    me = await getMe(t);
    await reloadPersonPage(t, page);
    await openEverything(page, false);
    all = await page.evaluate(pgControls, null);
    const picks = [];
    const pickProblems = [];
    for (const m of PERSON_MENUS) {
        const c = all.find((x) => matchesMenu(x, m));
        if (!c) {
            continue;
        }
        if (c.kind === 'checkbox') {
            const want = c.value !== 'true';
            const ok = await setSwitch(page, null, wantOf(m), want);
            picks.push({ m: m, stored: want, text: want ? 'on' : 'off' });
            if (!ok) {
                pickProblems.push(m.field + ': the switch did not turn ' + (want ? 'on' : 'off'));
            }
            continue;
        }
        const o = otherOptions(c, m.avoid)[0];
        if (!o) {
            pickProblems.push(m.field + ': nothing else to pick');
            continue;
        }
        const r = await page.evaluate(pgSetMenu, null, wantOf(m), o.v, null);
        picks.push({ m: m, stored: storedOf(en, m, o), text: o.l, v: o.v });
        if (!r.ok) {
            pickProblems.push(m.field + ': ' + r.reason);
        }
        await sleep(150);
    }
    await sleep(t.settle);
    let bar = await barOf(page, false);
    await t.check(picks.length > 0 && !pickProblems.length && bar.visible, 'person page: a value other than the default picked in each menu (' + picks.map((x) => x.m.field + ' "' + x.text + '"').join(', ') + '), the save bar shows'
        + (pickProblems.length ? ' — ' + pickProblems.join('; ') : ''), page);
    if (bar.visible) {
        await savePersonPage(t, page);
    }
    let doc = (await getMe(t)).Settings;
    let wrongStored = picks.filter((x) => !same((doc.Everyday || {})[x.m.field], x.stored)).map((x) => x.m.field + ' stored ' + JSON.stringify((doc.Everyday || {})[x.m.field]) + ' (want ' + JSON.stringify(x.stored) + ')');
    await t.check(picks.length > 0 && !wrongStored.length, 'Save stores each picked value in Everyday (' + picks.map((x) => x.m.field + '=' + JSON.stringify(x.stored)).join(', ') + ')' + (wrongStored.length ? ' — ' + wrongStored.join('; ') : ''), page);
    await reloadPersonPage(t, page);
    await openEverything(page, false);
    all = await page.evaluate(pgControls, null);
    // While "Always show subtitles" is on, the SDH menu shows Prefer and is disabled (its stored value is kept).
    const alwaysOn = picks.some((x) => x.m.field === 'PrefersSdh' && x.stored === true);
    let shownWrong = picks.filter((x) => {
        const c = all.find((cc) => matchesMenu(cc, x.m));
        if (x.m.field === 'Sdh' && alwaysOn) {
            return !(c && c.value === 'Prefer' && c.disabled);
        }
        return !showsValue(c, en, x.m, x.stored);
    }).map((x) => x.m.field);
    await t.check(picks.length > 0 && !shownWrong.length, 'after a reload each menu shows the stored value' + (alwaysOn ? ' (SDH shows "Prefer", disabled, while "Always show subtitles" is on)' : '')
        + (shownWrong.length ? ' — not: ' + shownWrong.join(', ') : ''), page);
    // Back to the default in each menu: the field is cleared, not stored.
    const back = [];
    const backProblems = [];
    for (const x of picks) {
        const d = personDefault(me, x.m).value;
        const c = all.find((y) => matchesMenu(y, x.m));
        if (!c || d === undefined) {
            backProblems.push(x.m.field + ': ' + (c ? 'no known default' : 'not found'));
            continue;
        }
        if (c.kind === 'checkbox') {
            if (!(await setSwitch(page, null, wantOf(x.m), !!d))) {
                backProblems.push(x.m.field + ': the switch did not turn ' + (d ? 'on' : 'off'));
            }
            back.push(x.m.field);
            continue;
        }
        const o = c.options.find((y) => storedOf(en, x.m, y) === d || y.v === String(d));
        if (!o) {
            backProblems.push(x.m.field + ': no option for the default ' + JSON.stringify(d));
            continue;
        }
        const r = await page.evaluate(pgSetMenu, null, wantOf(x.m), o.v, null);
        if (!r.ok) {
            backProblems.push(x.m.field + ': ' + r.reason);
        }
        back.push(x.m.field);
        await sleep(150);
    }
    await sleep(t.settle);
    bar = await barOf(page, false);
    if (bar.visible) {
        await savePersonPage(t, page);
    }
    const before = doc;
    doc = (await getMe(t)).Settings;
    const stillSet = back.filter((f) => (doc.Everyday || {})[f] !== null && (doc.Everyday || {})[f] !== undefined).map((f) => f + '=' + JSON.stringify(doc.Everyday[f]));
    const stray = strayPaths(before, doc, back.map((f) => 'Everyday.' + f));
    await t.check(back.length > 0 && !backProblems.length && !stillSet.length && !stray.length, 'picking the default again' + (serverDefaults ? ' (the server\'s Text only / Avoid / No included)' : '') + ' and saving clears each field instead of storing it ('
        + back.join(', ') + ')' + (backProblems.length ? ' — ' + backProblems.join('; ') : '') + (stillSet.length ? ' — still stored: ' + stillSet.join(', ') : '') + (stray.length ? ' — also changed: ' + stray.join(', ') : ''), page);
    // A later change of the server's defaults still reaches the person.
    if (serverDefaults) {
        const later = clone(adminDoc);
        later.Everyday.Format = 'PreferText';
        await putAdmin(t, later, 'the admin\'s Format now Prefer text');
        await reloadPersonPage(t, page);
        await openEverything(page, false);
        const fm = PERSON_MENUS.find((m) => m.field === 'Format');
        const c = await page.evaluate(pgControl, null, wantOf(fm), null);
        await t.check(showsValue(c, en, fm, 'PreferText'), 'after the admin changes the default to Prefer text, Subtitle format follows it ("' + (c && c.text) + '"): the cleared field reaches the new default', page);
    }

    // 14d. The admin page with every section and group open (and the People editor): plain menus.
    await putAdmin(t, neutral, 'neutral');
    const defaults = await t.api.get('/Babelfin/Admin/Defaults');
    await t.check(defaults.status === 200 && defaults.body && Array.isArray(defaults.body.AppModes), 'GET Babelfin/Admin/Defaults answers the shipped settings (' + defaults.status + ', ' + (defaults.body && defaults.body.AppModes ? defaults.body.AppModes.length + ' app rows' : 'no AppModes') + ')', null);
    const shipped = defaults.status === 200 ? defaults.body : null;
    const ena = (await strings(t)).admin;
    const mark = page.requests.length;
    await openEditor(t, page, t.ids.viewerId);
    await openEverything(page, true);
    const asked = page.requests.slice(mark).some((r) => r.method === 'GET' && /\/Babelfin\/Admin\/Defaults(\?|$)/i.test(r.url));
    await t.check(asked, 'the admin page asks GET Babelfin/Admin/Defaults for its menus', page);
    const adminAll = await page.evaluate(pgControls, ADMIN, null);
    p = plainMenuProblems(adminAll);
    await t.check(adminAll.some((c) => c.kind === 'select') && !p.length, 'admin page, every section and group open (and the People editor): ' + adminAll.filter((c) => c.kind === 'select').length + ' option menus outside the tables, none with a "Default (…)" entry or a repeated option'
        + (p.length ? ' — ' + p.join('; ') : ''), page);
    const adminNow = await getAdmin(t);
    const apps = (await t.api.get('/Babelfin/Admin/Apps')).body || [];
    const appMenus = adminAll.filter((c) => c.kind === 'select' && c.label.indexOf(ADMIN_MENUS.appPrefix) === 0);
    const appWrong = appMenus.map((c) => {
        const client = c.label.replace(/^.*·\s*/, '');
        const a = apps.find((x) => x.Client === client);
        const want = a ? String(a.Mode) : null;
        return a && (c.value === want || c.text === ena['apps.mode.' + want]) ? null : client + ' shows ' + JSON.stringify(c.text) + ' (stored ' + want + ')';
    }).filter(Boolean);
    const notListed = adminAll.find((c) => matchesMenu(c, ADMIN_MENUS.notListed));
    const nlOk = !!notListed && (notListed.value === String(adminNow.UnknownAppMode) || notListed.text === ena['apps.mode.' + adminNow.UnknownAppMode]);
    await t.check(appMenus.length > 0 && !appWrong.length && nlOk, 'admin page: each app\'s menu (' + appMenus.length + ') and "Apps not listed" show the stored modes' + (appWrong.length ? ' — ' + appWrong.join('; ') : '')
        + (nlOk ? '' : ' — Apps not listed shows ' + JSON.stringify(notListed ? notListed.text : null) + ' (stored ' + adminNow.UnknownAppMode + ')'), page);
    const unl = adminAll.find((c) => matchesMenu(c, ADMIN_MENUS.unlabelled));
    const shippedUnl = shipped && shipped.Everyday && shipped.Everyday.UnlabelledAudio ? shipped.Everyday.UnlabelledAudio : null;
    const unlDefault = shippedUnl || 'OriginalWhenOnlyTrack';
    const stored = (adminNow.Everyday || {}).UnlabelledAudio || null;
    await t.check(!stored && !!unl && (unl.value === unlDefault || unl.text === ena['unlabelled.' + unlDefault]),
        'admin page, nothing stored: "Audio tracks without a language" shows ' + JSON.stringify(unl ? unl.text : null) + ' (want ' + unlDefault + ', ' + (shippedUnl ? 'Admin/Defaults' : 'the engine\'s own default; Admin/Defaults leaves it unset') + ')', page);

    // 14e. Admin: picking another value stores it; picking the default clears (or, for a field that always has a value, stores the shipped one).
    const adminPicks = [
        { name: 'Audio tracks without a language', want: { field: 'Everyday.UnlabelledAudio', label: ADMIN_MENUS.unlabelled.label }, other: 'Unknown', def: unlDefault, read: (a) => (a.Everyday || {}).UnlabelledAudio || null, cleared: true },
        { name: 'Apps not listed', want: { field: 'UnknownAppMode', label: ADMIN_MENUS.notListed.label }, other: null, def: shipped ? String(shipped.UnknownAppMode) : null, read: (a) => (a.UnknownAppMode === undefined ? null : String(a.UnknownAppMode)) },
        { name: 'Jellyfin Web', want: { label: ADMIN_MENUS.appPrefix + ' · Jellyfin Web' }, other: null, def: shipped ? String(((shipped.AppModes || []).find((x) => x.Client === 'Jellyfin Web') || {}).Mode) : null, read: (a) => { const r = (a.AppModes || []).find((x) => x.Client === 'Jellyfin Web'); return r ? String(r.Mode) : null; } }
    ];
    for (const ap of adminPicks) {
        await reloadAdminPage(t, page);
        await openEverything(page, true);
        const c = await page.evaluate(pgControl, ADMIN, ap.want, null);
        if (!c || c.kind !== 'select') {
            await t.check(false, 'admin page: the "' + ap.name + '" menu is there (' + (c ? c.kind : 'not found') + ')', page);
            continue;
        }
        const other = ap.other || (otherOptions(c, ['__pick__'])[0] || {}).v;
        const r1 = await page.evaluate(pgSetMenu, ADMIN, ap.want, other, null);
        await sleep(t.settle);
        const b1 = await barOf(page, true);
        if (r1.ok && b1.visible) {
            await saveAdminPage(t, page);
        }
        const a1 = await getAdmin(t);
        await t.check(r1.ok && b1.visible && ap.read(a1) === other, 'admin page: picking ' + JSON.stringify(other) + ' in "' + ap.name + '" and saving stores it (' + JSON.stringify(ap.read(a1)) + ')' + (r1.ok ? '' : ' — ' + r1.reason), page);
        if (!ap.def) {
            continue;
        }
        const c2 = await page.evaluate(pgControl, ADMIN, ap.want, null);
        const defOption = c2 && c2.options.find((o) => o.v === ap.def);
        const r2 = defOption ? await page.evaluate(pgSetMenu, ADMIN, ap.want, defOption.v, null) : { ok: false, reason: 'no option ' + ap.def };
        await sleep(t.settle);
        const b2 = await barOf(page, true);
        if (r2.ok && b2.visible) {
            await saveAdminPage(t, page);
        }
        const a2 = await getAdmin(t);
        const stray2 = strayPaths(a1, a2, [ap.want.field || 'AppModes', 'AppModes']);
        await t.check(r2.ok && (ap.cleared ? ap.read(a2) === null : ap.read(a2) === ap.def) && !stray2.length,
            'admin page: picking the default ' + JSON.stringify(ap.def) + ' in "' + ap.name + '" and saving ' + (ap.cleared ? 'clears the field' : 'stores the shipped value') + ' (stored ' + JSON.stringify(ap.read(a2)) + ')'
            + (r2.ok ? '' : ' — ' + r2.reason) + (stray2.length ? ' — also changed: ' + stray2.join(', ') : ''), page);
    }
}

// ───────────────────────────── 15. Restore defaults and Start over ─────────────────────────────

/** Every person-page Advanced control as { field: control } (Advanced and its groups open). */
async function personAdvanced(page) {
    const all = await page.evaluate(pgControls, null);
    const out = {};
    PERSON_MENUS.concat([NEXT_EPISODE]).forEach((m) => { out[m.field] = all.find((c) => matchesMenu(c, m)) || null; });
    return out;
}

async function testRestoreDefaults(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    const en = (await strings(t)).person;
    const neutral = adminNeutral(kept.admin);
    await putAdmin(t, neutral, 'neutral');

    // The person: languages, Languages I read, the table in both modes and a per-title choice, set through the API.
    const base = withTable(t, withoutAdvanced(kept.snapshot), { perLibrary: true, everyday: ['lang:en', 'off'], libs: { Anime: ['lang:ja', 'lang:en'] } });
    base.Everyday.Understood = ['en', 'ja'];
    base.Everyday.Reads = ['en', 'fr'];
    base.Enabled = true;
    // "Replace Jellyfin's remembered tracks" shows only while it is on, so it is turned on here (as an older version would have).
    base.HandleNextEpisode = true;
    await putMe(t, base, 'languages, table, nothing in Advanced, next-episode switch on');
    const ov = await t.api.put('/Babelfin/Me/Overrides/' + compactId(t.ids.movieId), { AudioChain: [{ Kind: 'Language', Language: 'de' }, { Kind: 'VideoDefault' }] }, { scope: 'Title' });
    if (ov.status !== 200) {
        t.note('a per-title choice could not be stored (' + ov.status + '); "shows and movies" is checked without one');
    }
    let me = await getMe(t);

    // 15a. Several Advanced settings changed through the page and saved.
    await openPersonPage(t, page);
    await openEverything(page, false);
    let ctl = await personAdvanced(page);
    const set = [];
    const setProblems = [];
    for (const m of PERSON_MENUS) {
        const c = ctl[m.field];
        if (!c) {
            setProblems.push(m.field + ': not found');
            continue;
        }
        if (c.kind === 'checkbox') {
            const want = c.value !== 'true';
            if (!(await setSwitch(page, null, wantOf(m), want))) {
                setProblems.push(m.field + ': the switch did not change');
            }
            set.push({ m: m, stored: want });
            continue;
        }
        const o = otherOptions(c, m.avoid)[0];
        const r = o ? await page.evaluate(pgSetMenu, null, wantOf(m), o.v, null) : { ok: false, reason: 'nothing else to pick' };
        if (!r.ok) {
            setProblems.push(m.field + ': ' + r.reason);
        } else {
            set.push({ m: m, stored: storedOf(en, m, o) });
        }
        await sleep(150);
    }
    const nextOn = !!ctl.HandleNextEpisode && ctl.HandleNextEpisode.value === 'true';
    await sleep(t.settle);
    if ((await barOf(page, false)).visible) {
        await savePersonPage(t, page);
    }
    const before = (await getMe(t)).Settings;
    const notStored = set.filter((x) => !same((before.Everyday || {})[x.m.field], x.stored)).map((x) => x.m.field);
    await t.check(set.length === PERSON_MENUS.length && !setProblems.length && !notStored.length && nextOn && before.HandleNextEpisode === true,
        'person page: every Advanced menu set to another value through the page and saved, "' + NEXT_EPISODE.label + '" shown on (' + set.length + ' fields stored, HandleNextEpisode ' + before.HandleNextEpisode + ')'
        + (setProblems.length ? ' — ' + setProblems.join('; ') : '') + (notStored.length ? ' — not stored: ' + notStored.join(', ') : ''), page);

    // 15b. The button: at the end of Advanced, outside the groups.
    await reloadPersonPage(t, page);
    await openEverything(page, false);
    const where = await page.evaluate(pgSectionButton, null, 'Advanced', SPEC.restore);
    await t.check(where.found && !where.inGroup && where.afterGroups && !where.later.length, 'person page: a "' + SPEC.restore + '" button at the end of Advanced, after every group and outside them'
        + (where.found ? ' (inside a group ' + where.inGroup + ', after the groups ' + where.afterGroups + (where.later.length ? ', controls after it: ' + where.later.join(', ') : '') + ')' : ' — ' + where.why), page);
    if (where.found) {
        // Cancel changes nothing.
        ctl = await personAdvanced(page);
        const dialogs0 = page.dialogs.length;
        await page.click(pgSectionButtonEl, { args: [null, 'Advanced', SPEC.restore], what: 'the "' + SPEC.restore + '" button' });
        const dlg = await waitDialog(page, 5000);
        const native = page.dialogs.length > dialogs0;
        await t.check(!!dlg && !native, 'person page: "' + SPEC.restore + '" asks for confirmation first, in the page (' + (dlg ? dlg.role + ' "' + dlg.title + '": ' + dlg.buttons.join(' / ') : (native ? 'a browser confirm() showed and was accepted' : 'no dialog')) + ')', page);
        if (dlg) {
            await answerDialog(page, false);
            await sleep(t.settle);
            const after = await personAdvanced(page);
            const moved = Object.keys(ctl).filter((f) => ctl[f] && (!after[f] || after[f].value !== ctl[f].value));
            const bar = await barOf(page, false);
            const doc = (await getMe(t)).Settings;
            await t.check(!moved.length && !bar.visible && same(doc, before), 'person page: Cancel in the confirmation changes nothing (no control moved, no save bar, nothing saved)'
                + (moved.length ? ' — moved: ' + moved.join(', ') : '') + (bar.visible ? ' — the save bar shows' : '') + (same(doc, before) ? '' : ' — saved: ' + changedPaths(before, doc).join(', ')), page);
        }
        // Confirm: every Advanced control back on its default, the page changed; Save stores exactly that.
        await page.click(pgSectionButtonEl, { args: [null, 'Advanced', SPEC.restore], what: 'the "' + SPEC.restore + '" button' });
        const dlg2 = await waitDialog(page, 5000);
        if (dlg2) {
            await answerDialog(page, true);
        }
        await sleep(t.settle);
        const now = await personAdvanced(page);
        const notDefault = PERSON_MENUS.concat([NEXT_EPISODE]).filter((m) => !showsValue(now[m.field], en, m, personDefault(me, m).value))
            .map((m) => m.field + ' shows ' + JSON.stringify(now[m.field] ? (now[m.field].text || now[m.field].value) : null) + ' (default ' + JSON.stringify(personDefault(me, m).value) + ')');
        const bar2 = await barOf(page, false);
        const unsaved = (await getMe(t)).Settings;
        await t.check(!!dlg2 && !notDefault.length && bar2.visible && same(unsaved, before), 'person page: confirming puts every Advanced menu and the switch back on its default and shows the save bar, without saving yet'
            + (notDefault.length ? ' — ' + notDefault.join('; ') : '') + (bar2.visible ? '' : ' — no save bar') + (same(unsaved, before) ? '' : ' — already saved: ' + changedPaths(before, unsaved).join(', ')), page);
        if (bar2.visible) {
            await savePersonPage(t, page);
        }
        const after = (await getMe(t)).Settings;
        const left = PERSON_RESTORED.filter((f) => (after.Everyday || {})[f] !== null && (after.Everyday || {})[f] !== undefined).map((f) => f + '=' + JSON.stringify(after.Everyday[f]));
        const stray = strayPaths(before, after, PERSON_RESTORED.map((f) => 'Everyday.' + f).concat(['HandleNextEpisode', 'JellyfinChanges']));
        await t.check(!left.length && after.HandleNextEpisode === false && !stray.length, 'person page: after Save the API shows exactly ' + PERSON_RESTORED.join(', ') + ' cleared and HandleNextEpisode false'
            + (left.length ? ' — still set: ' + left.join(', ') : '') + (after.HandleNextEpisode === false ? '' : ' — HandleNextEpisode ' + after.HandleNextEpisode) + (stray.length ? ' — also changed: ' + stray.join(', ') : ''), page);
        const keptOk = same(after.Everyday.Understood, before.Everyday.Understood) && same(after.Everyday.Reads, before.Everyday.Reads) && after.Enabled === before.Enabled
            && after.PerLibrary === before.PerLibrary && same(tableOf(after.Everyday), tableOf(before.Everyday)) && same(libraryTables(after), libraryTables(before)) && same(after.Overrides, before.Overrides);
        await t.check(keptOk, 'person page: My languages, Languages for subtitles, the on/off switch, the table (both modes) and the show and movie overrides are untouched (' + (after.Overrides || []).length + ' overrides; the page saves with keepOverrides)', page);
        await reloadPersonPage(t, page);
        await openEverything(page, false);
        const reloaded = await personAdvanced(page);
        const off = PERSON_MENUS.filter((m) => !showsValue(reloaded[m.field], en, m, personDefault(me, m).value)).map((m) => m.field);
        if (reloaded.HandleNextEpisode) {
            off.push('"' + NEXT_EPISODE.label + '" still shown');
        }
        await t.check(!off.length && !(await barOf(page, false)).visible, 'person page: after a reload every Advanced menu shows its default, "' + NEXT_EPISODE.label + '" is gone and there is no save bar' + (off.length ? ' — not: ' + off.join(', ') : ''), page);
    }

    // 15c. The admin page: Apps and Track recognition changed through the page and saved.
    const defaults = await t.api.get('/Babelfin/Admin/Defaults');
    if (defaults.status !== 200 || !defaults.body) {
        await t.check(false, 'GET Babelfin/Admin/Defaults answers (' + defaults.status + '; the admin Restore defaults checks are skipped)', null);
        return;
    }
    const shipped = defaults.body;
    const adminBase = withTable(t, neutral, { modeField: 'PerLibraryDefaults', perLibrary: false, everyday: [undefined, 'off'] });
    await putAdmin(t, adminBase, 'the top table on subtitles Off');
    await openAdminPage(t, page);
    await openEverything(page, true);
    const setA = [];
    const web = { label: ADMIN_MENUS.appPrefix + ' · Jellyfin Web' };
    const r1 = await page.evaluate(pgSetMenu, ADMIN, web, 'Trust', null);
    setA.push('Jellyfin Web Trust: ' + (r1.ok ? 'ok' : r1.reason));
    const nl = await page.evaluate(pgControl, ADMIN, { field: 'UnknownAppMode', label: ADMIN_MENUS.notListed.label }, null);
    const nlOther = nl ? (otherOptions(nl, [])[0] || {}).v : null;
    const r2 = nlOther ? await page.evaluate(pgSetMenu, ADMIN, { field: 'UnknownAppMode', label: ADMIN_MENUS.notListed.label }, nlOther, null) : { ok: false, reason: 'not found' };
    setA.push('Apps not listed ' + nlOther + ': ' + (r2.ok ? 'ok' : r2.reason));
    const fix = { label: { re: '^Fix tracks after playback starts' } };
    const fixNow = await page.evaluate(pgControl, ADMIN, fix, null);
    const r3 = fixNow ? await setSwitch(page, ADMIN, fix, fixNow.value !== 'true') : false;
    setA.push('Fix tracks after start: ' + (r3 ? 'ok' : 'not changed'));
    const compat = { label: "Also save choices as Jellyfin's remembered tracks" };
    const compatNow = await page.evaluate(pgControl, ADMIN, compat, null);
    const r4 = compatNow ? await setSwitch(page, ADMIN, compat, compatNow.value !== 'true') : false;
    setA.push('Also save: ' + (r4 ? 'ok' : 'not changed'));
    const kwLabel = ((await strings(t)).admin)['tracknames.PartialWords'] || 'Signs and songs';
    const r5 = await page.evaluate(pgAppendLine, ADMIN, kwLabel, 'e2e-signs');
    setA.push('Track names "' + kwLabel + '" + e2e-signs: ' + (r5.ok ? 'ok' : r5.reason));
    let r6 = { ok: false, reason: 'no "Add a code" button' };
    try {
        await page.click(pgToolButton, { args: [ADMIN, 'Advanced', 'Track recognition', '^Add a code$'], what: 'Add a code', timeout: 3000 });
        await sleep(300);
        r6 = await page.evaluate(pgFillLastRow, ADMIN, 'Advanced', 'Track recognition', ['e2exx', 'en']);
    } catch (e) {
        r6 = { ok: false, reason: e.message };
    }
    setA.push('Language code e2exx → en: ' + (r6.ok ? 'ok' : r6.reason));
    const r7 = await page.evaluate(pgSetMenu, ADMIN, { field: 'Everyday.UnlabelledAudio', label: ADMIN_MENUS.unlabelled.label }, 'Unknown', null);
    setA.push('Audio tracks without a language Unknown: ' + (r7.ok ? 'ok' : r7.reason));
    await sleep(t.settle);
    if ((await barOf(page, true)).visible) {
        await saveAdminPage(t, page);
    }
    const a1 = await getAdmin(t);
    const webRow = (a1.AppModes || []).find((x) => x.Client === 'Jellyfin Web') || {};
    const storedA = [String(webRow.Mode) === 'Trust', String(a1.UnknownAppMode) === String(nlOther), (a1.Keywords || {}).PartialWords && a1.Keywords.PartialWords.indexOf('e2e-signs') >= 0,
        (a1.LanguageAliases || []).some((x) => x.Raw === 'e2exx'), (a1.Everyday || {}).UnlabelledAudio === 'Unknown', !same(a1.CorrectAfterStart, adminBase.CorrectAfterStart) || !r3, a1.CompatibilityMode !== !!adminBase.CompatibilityMode || !r4];
    await t.check(setA.every((x) => / ok$/.test(x)) && storedA.every(Boolean), 'admin page: Apps (Jellyfin Web, Apps not listed, both switches) and Track recognition (a track-name word, a language code, audio without a language) changed through the page and saved — '
        + setA.join('; ') + (storedA.every(Boolean) ? '' : ' — not all stored: ' + JSON.stringify(storedA)), page);

    const whereA = await page.evaluate(pgSectionButton, ADMIN, 'Advanced', SPEC.restore);
    await t.check(whereA.found && !whereA.inGroup && whereA.afterGroups && !whereA.later.length, 'admin page: a "' + SPEC.restore + '" button at the end of Advanced, after Apps, New users and Track recognition, outside them'
        + (whereA.found ? ' (groups ' + whereA.groups.join(', ') + '; inside a group ' + whereA.inGroup + ', after the groups ' + whereA.afterGroups + (whereA.later.length ? ', controls after it: ' + whereA.later.join(', ') : '') + ')' : ' — ' + whereA.why), page);
    if (!whereA.found) {
        return;
    }
    // Cancel changes nothing.
    const controlsA = await page.evaluate(pgControls, ADMIN, null);
    const dialogsA = page.dialogs.length;
    await page.click(pgSectionButtonEl, { args: [ADMIN, 'Advanced', SPEC.restore], what: 'the admin "' + SPEC.restore + '" button' });
    const dlgA = await waitDialog(page, 5000);
    await t.check(!!dlgA && page.dialogs.length === dialogsA, 'admin page: "' + SPEC.restore + '" asks for confirmation first, in the page (' + (dlgA ? dlgA.role + ' "' + dlgA.title + '": ' + dlgA.buttons.join(' / ') : 'no dialog') + ')', page);
    if (dlgA) {
        await answerDialog(page, false);
        await sleep(t.settle);
        const after = await page.evaluate(pgControls, ADMIN, null);
        const moved = controlsA.filter((c) => { const x = after.find((y) => y.label === c.label && y.kind === c.kind); return !x || x.value !== c.value; }).map((c) => c.label);
        const kw = await page.evaluate(pgTextArea, ADMIN, kwLabel);
        await t.check(!moved.length && !(await barOf(page, true)).visible && same(await getAdmin(t), a1) && /e2e-signs/.test(kw || ''),
            'admin page: Cancel in the confirmation changes nothing (no control moved, the track-name word still there, no save bar, nothing saved)' + (moved.length ? ' — moved: ' + moved.join(', ') : ''), page);
    }
    // Confirm, then Save.
    await page.click(pgSectionButtonEl, { args: [ADMIN, 'Advanced', SPEC.restore], what: 'the admin "' + SPEC.restore + '" button' });
    const dlgB = await waitDialog(page, 5000);
    if (dlgB) {
        await answerDialog(page, true);
    }
    await sleep(t.settle);
    const barB = await barOf(page, true);
    const unsavedA = await getAdmin(t);
    const webNow = await page.evaluate(pgControl, ADMIN, web, null);
    const shippedWeb = String(((shipped.AppModes || []).find((x) => x.Client === 'Jellyfin Web') || {}).Mode);
    await t.check(!!dlgB && barB.visible && same(unsavedA, a1) && !!webNow && webNow.value === shippedWeb, 'admin page: confirming shows the shipped values (Jellyfin Web "' + (webNow && webNow.text) + '") and the save bar, without saving yet', page);
    if (barB.visible) {
        await saveAdminPage(t, page);
    }
    const a2 = await getAdmin(t);
    const p = [];
    const modeOf = (rows, client) => { const r = (rows || []).find((x) => String(x.Client).toLowerCase() === String(client).toLowerCase()); return r ? String(r.Mode) : null; };
    (shipped.AppModes || []).forEach((s) => { if (modeOf(a2.AppModes, s.Client) !== String(s.Mode)) { p.push(s.Client + ' ' + modeOf(a2.AppModes, s.Client) + ' (shipped ' + s.Mode + ')'); } });
    (a2.AppModes || []).filter((r) => !(shipped.AppModes || []).some((s) => String(s.Client).toLowerCase() === String(r.Client).toLowerCase()))
        .forEach((r) => { if (String(r.Mode) !== String(shipped.UnknownAppMode)) { p.push('seen-only ' + r.Client + ' ' + r.Mode + ' (want the shipped "Apps not listed" ' + shipped.UnknownAppMode + ')'); } });
    ['UnknownAppMode', 'CorrectAfterStart', 'CompatibilityMode', 'Keywords', 'LanguageAliases'].forEach((f) => { if (!same(a2[f], shipped[f])) { p.push(f + ' ' + canon(a2[f]).slice(0, 80) + ' (shipped ' + canon(shipped[f]).slice(0, 80) + ')'); } });
    ['UnlabelledAudio', 'UnlabelledAudioLanguage'].forEach((f) => { if (!same((a2.Everyday || {})[f], (shipped.Everyday || {})[f])) { p.push('Everyday.' + f + ' ' + JSON.stringify((a2.Everyday || {})[f])); } });
    const strayA = strayPaths(a1, a2, ADMIN_RESTORED);
    await t.check(!p.length && !strayA.length, 'admin page: after Save the API shows the Apps group and Track recognition back on the shipped values of Admin/Defaults (app rows, seen-only apps on the shipped "Apps not listed", the switches, keyword lists, language codes, audio without a language) and nothing else changed'
        + (p.length ? ' — ' + p.join('; ') : '') + (strayA.length ? ' — also changed: ' + strayA.join(', ') : ''), page);
}

// ───────────────────────────── 16. People table ─────────────────────────────

async function testPeopleTable(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    const viewerId = t.ids.viewerId;
    const userPath = '/Babelfin/Admin/Users/' + viewerId + '/Settings';
    const viewerSnapshot = clone((await t.api.get(userPath)).body);
    t.defer('second user\'s settings restored', async () => {
        await restorePersonSnapshot(t, t.api, userPath, viewerSnapshot, t.opts.viewer + "'s settings put back through the API");
    });
    await putAdmin(t, adminNeutral(kept.admin), 'neutral');
    // A third person, new: Babelfin seeds a new Jellyfin user's file at creation (languages only), so through the API this person has a
    // file with nothing in the table. A person without any file is shown by rewriting their GET Admin/Users row in the browser below.
    const name = 'rige2enofile';
    let fresh = ((await t.api.get('/Users')).body || []).find((u) => u.Name === name);
    if (!fresh) {
        const made = await t.api.post('/Users/New', { Name: name, Password: '' });
        fresh = made.status === 200 ? made.body : null;
        if (fresh) {
            t.defer('test user ' + name + ' removed', async () => {
                const r = await t.api.del('/Users/' + fresh.Id);
                await t.check(r.status === 204 || r.status === 200, 'the test user ' + name + ' removed again (DELETE /Users: ' + r.status + ')', null);
            });
        }
    }
    const fileBefore = fresh ? (((await t.api.get('/Babelfin/Admin/Users')).body || []).find((u) => compactId(u.UserId) === compactId(fresh.Id)) || {}).HasSettings : null;
    await t.check(!!fresh, 'a third Jellyfin user (' + name + ') is there, new (GET Admin/Users HasSettings ' + fileBefore + ': ' + (fileBefore ? 'seeded at creation' : 'no file yet') + ')', null);
    // The admin: "Same for every library" on English / Off. The second user: "Choose for each library".
    await putMe(t, withTable(t, kept.snapshot, { perLibrary: false, everyday: ['lang:en', 'off'] }), 'every library, English / Off');
    const pv = await t.api.put(userPath, withTable(t, viewerSnapshot, { perLibrary: true, libs: { Anime: ['lang:ja', 'lang:en'] } }));
    await t.check(pv.status === 200, t.opts.viewer + ': "' + SPEC.modePer + '" with an Anime choice stored through the API (' + pv.status + ')', null);

    const look = async () => {
        await openAdminPage(t, page);
        await sleep(500);
        return page.evaluate(pgPeopleTable, ADMIN);
    };
    let tbl = await look();
    let users = (await t.api.get('/Babelfin/Admin/Users')).body || [];
    const row = (id) => users.find((u) => compactId(u.UserId) === compactId(id)) || null;
    const adminRow = row(t.api.userId);
    const viewerRow = row(viewerId);
    const freshRow = fresh ? row(fresh.Id) : null;
    if (freshRow && freshRow.HasSettings && fileBefore === false) {
        t.note('opening the admin page created a Babelfin file for ' + name + ' (HasSettings false before, true after)');
    }
    // The server leaves null fields out of its JSON, so a missing Table is null.
    const hasFields = (r) => !!r && 'PerLibrary' in r && 'LibraryRows' in r && (!('Table' in r) || r.Table === null || typeof r.Table === 'object');
    await t.check(users.length > 0 && users.every(hasFields) && users.some((r) => 'Table' in r), 'GET Admin/Users rows carry PerLibrary, Table (left out when null) and LibraryRows (' + users.map((r) => r.Name + ': ' + Object.keys(r).filter((k) => /PerLibrary|Table|LibraryRows/.test(k)).join('/')).join('; ') + ')', null);
    await t.check(!!adminRow && adminRow.PerLibrary === false && same(adminRow.Table ? tableOf(adminRow.Table) : null, storedFor('lang:en', 'off')) && adminRow.LibraryRows === 0,
        'GET Admin/Users, ' + t.opts.user + ': PerLibrary false, Table English / Off, LibraryRows 0 (' + (adminRow ? adminRow.PerLibrary + ', ' + canon(adminRow.Table) + ', ' + adminRow.LibraryRows : 'no row') + ')', null);
    await t.check(!!viewerRow && viewerRow.PerLibrary === true && viewerRow.LibraryRows === 1,
        'GET Admin/Users, ' + t.opts.viewer + ': PerLibrary true, LibraryRows 1 (' + (viewerRow ? viewerRow.PerLibrary + ', ' + viewerRow.LibraryRows : 'no row') + ')', null);
    await t.check(!!freshRow && freshRow.PerLibrary === false && !freshRow.Table && freshRow.LibraryRows === 0,
        'GET Admin/Users, ' + name + ' (new): PerLibrary false, no Table, LibraryRows 0 (' + (freshRow ? freshRow.PerLibrary + ', ' + canon(freshRow.Table) + ', ' + freshRow.LibraryRows : 'no row') + ')', null);

    const heads = tbl.found ? tbl.headers : [];
    const headOk = heads.length === 4 && heads.slice(0, 3).join('|') === SPEC.peopleColumns.slice(0, 3).join('|') && (heads[3] === '' || heads[3] === SPEC.peopleColumns[3]);
    await t.check(tbl.found && headOk && tbl.rows.every((r) => r.cells.length === 4 && r.edit), 'People table: the columns ' + SPEC.peopleColumns.join(', ') + ', each row with an Edit button (headers: '
        + (heads.length ? heads.map((x) => '"' + x + '"').join(', ') : 'no table') + ')', page);
    const cellsOf = (id) => { const r = tbl.found ? tbl.rows.find((x) => compactId(x.user) === compactId(id)) : null; return r ? r.cells.slice(1, 3) : null; };
    const expect = [
        [t.opts.user + ' (' + SPEC.modeSame + ', English / Off)', t.api.userId, ['English', SPEC.subtitleLabels.off]],
        [t.opts.viewer + ' (' + SPEC.modePer + ')', viewerId, [SPEC.perLibraryWords, SPEC.perLibraryWords]],
        [name + ' (new, nothing chosen)', fresh && fresh.Id, [SPEC.defaultWord, SPEC.defaultWord]]
    ];
    for (const e of expect) {
        const got = e[1] ? cellsOf(e[1]) : null;
        await t.check(!!got && same(got, e[2]), 'People table, ' + e[0] + ': Audio "' + e[2][0] + '", Subtitles "' + e[2][1] + '" (shows ' + (got ? got.map((x) => '"' + x + '"').join(', ') : 'no row') + ')', page);
    }
    const mismatch = users.map((u) => { const got = cellsOf(u.UserId); const want = peopleWords(u); return got && same(got, want) ? null : u.Name + ' shows ' + JSON.stringify(got) + ', GET Admin/Users means ' + JSON.stringify(want); }).filter(Boolean);
    await t.check(tbl.found && !mismatch.length, 'People table: every row\'s words match GET Admin/Users (' + users.length + ' people)' + (mismatch.length ? ' — ' + mismatch.join('; ') : ''), page);
    const detectedInTable = tbl.found && tbl.rows.some((r) => r.text.indexOf(SPEC.detected) >= 0);
    await t.check(tbl.found && !detectedInTable && heads.indexOf('Understands') < 0 && heads.indexOf('Reads') < 0, 'People table: no Understands/Reads columns and no "' + SPEC.detected + '" marker in the rows', page);

    // A person without a Babelfin file: the GET Admin/Users answer is rewritten in the browser so the new person's row reads
    // HasSettings false (as for someone created before Babelfin was installed), and the admin's own row too, keeping its English / Off Table.
    if (fresh) {
        const stop = await page.interceptResponses('*/Babelfin/Admin/Users*', (text) => {
            const body = JSON.parse(text);
            if (!Array.isArray(body)) {
                return null;
            }
            body.forEach((u) => {
                if (compactId(u.UserId) === compactId(fresh.Id)) {
                    u.HasSettings = false;
                    u.UpdatedAt = null;
                    u.PerLibrary = false;
                    u.Table = null;
                    u.LibraryRows = 0;
                }
                if (compactId(u.UserId) === compactId(t.api.userId)) {
                    u.HasSettings = false;
                }
            });
            return JSON.stringify(body);
        });
        const nf = await look();
        const hits = await stop();
        const r = nf.found ? nf.rows.find((x) => compactId(x.user) === compactId(fresh.Id)) : null;
        const a = nf.found ? nf.rows.find((x) => compactId(x.user) === compactId(t.api.userId)) : null;
        const want = [SPEC.defaultWord, SPEC.defaultWord];
        await t.check(hits.rewritten > 0 && !!r && same(r.cells.slice(1, 3), want) && !!a && same(a.cells.slice(1, 3), want),
            'People table, a person without a Babelfin file (GET Admin/Users rewritten in the browser: HasSettings false): "Default" / "Default" for ' + name + ' (' + (r ? r.cells.slice(1, 3).join(' / ') : 'no row')
            + ') and for ' + t.opts.user + ' even with an English / Off Table in the row (' + (a ? a.cells.slice(1, 3).join(' / ') : 'no row') + ')' + (hits.rewritten ? '' : ' — the answer was not rewritten'), page);
    }

    // More of the words, one at a time on the admin's own row.
    const cases = [
        { table: ['', ''], want: [SPEC.defaultWord, SPEC.defaultWord] },
        { table: ['video', 'video'], want: [SPEC.videoDefault, SPEC.videoDefault] },
        { table: ['lang:ja', 'smart'], want: ['Japanese', SPEC.adminSmartWords] },
        { table: ['lang:fr', 'signs'], want: ['French', SPEC.subtitleLabels.signs] },
        { table: ['lang:de', 'lang:en'], want: ['German', 'English'] },
        { custom: true, want: ['Custom', 'Custom'] },
        { automatic: true, want: [SPEC.perLibraryWords, SPEC.perLibraryWords] }
    ];
    for (const c of cases) {
        let doc;
        let what;
        if (c.custom) {
            doc = withTable(t, kept.snapshot, { perLibrary: false });
            doc.Everyday.AudioChain = [{ Kind: 'Language', Language: 'ja' }, { Kind: 'Language', Language: 'en' }, { Kind: 'VideoDefault' }];
            doc.Everyday.WhenAudioUnderstood = 'Off';
            doc.Everyday.WhenAudioNotUnderstood = 'Full';
            what = 'a stored audio order and subtitle mix the table can\'t show';
        } else if (c.automatic) {
            doc = withTable(t, kept.snapshot, { perLibrary: null, everyday: ['lang:en', 'off'], libs: { Movies: ['lang:fr', undefined] } });
            what = 'automatic mode with a Movies choice stored (and English / Off for every library, which that mode ignores)';
        } else {
            doc = withTable(t, kept.snapshot, { perLibrary: false, everyday: c.table });
            what = '"' + SPEC.modeSame + '" with ' + c.table.map((v) => JSON.stringify(v)).join(' / ');
        }
        await putMe(t, doc, what);
        tbl = await look();
        users = (await t.api.get('/Babelfin/Admin/Users')).body || [];
        const got = cellsOf(t.api.userId);
        const api = row(t.api.userId);
        const fromApi = api ? peopleWords(api) : null;
        await t.check(!!got && same(got, c.want) && same(fromApi, c.want), 'People table, ' + t.opts.user + ' with ' + what + ': "' + c.want.join('" / "') + '" (shows ' + (got ? '"' + got.join('" / "') + '"' : 'no row')
            + '; GET Admin/Users: PerLibrary ' + (api && api.PerLibrary) + ', Table ' + canon(api && api.Table) + ')', page);
    }

    // The (detected) marker lives in the editor.
    const vNow = row(viewerId);
    if (vNow && (vNow.Assumed || []).length && !vNow.SeedConfirmed) {
        await openEditor(t, page, viewerId);
        const inEditor = await page.evaluate(function (sel, marker) { var e = document.querySelector(sel); return !!e && e.textContent.indexOf(marker) >= 0; }, EDITOR, SPEC.detected);
        await t.check(inEditor, 'the People editor of ' + t.opts.viewer + ' (languages detected, not confirmed) shows "' + SPEC.detected + '"', page);
    } else {
        t.note(t.opts.viewer + '\'s languages are confirmed or not detected; the editor\'s "' + SPEC.detected + '" marker is not checked');
    }
}

// ───────────────────────────── 17. New users: only My languages ─────────────────────────────

/** Adds a language through a group's "Add a language" search (types the name, picks the option with that language code). */
async function addLanguage(page, scope, section, group, name, code) {
    const focused = await page.evaluate(function (sc, sec, grp) {
        var h = window.__bfE2E;
        var outer = h.disclosure(sc, sec);
        var r = outer ? h.regionOf(outer) : null;
        var ctrl = r ? h.disclosure(r, grp) : null;
        var region = ctrl ? h.regionOf(ctrl) : null;
        var input = region && Array.prototype.find.call(region.querySelectorAll('input[role="combobox"], input[type="search"]'), h.vis);
        if (!input) {
            return false;
        }
        input.scrollIntoView({ block: 'center' });
        input.focus();
        return document.activeElement === input;
    }, scope, section, group);
    if (!focused) {
        return 'no language search in "' + group + '"';
    }
    await page.type(name);
    const optionEl = function (n, c) {
        return Array.prototype.find.call(document.querySelectorAll('[role="option"]'), function (o) {
            var r = o.getBoundingClientRect();
            var nm = o.querySelector('.bfui-picker-name');
            return r.width > 0 && (o.getAttribute('data-code') === c || (nm ? nm.textContent.trim() : o.textContent.trim()) === n);
        }) || null;
    };
    try {
        await page.waitFor(optionEl, { timeout: 5000, what: 'the option ' + name }, name, code);
    } catch (e) {
        return 'no option "' + name + '" (' + code + ') after typing it';
    }
    await page.click(optionEl, { args: [name, code], what: 'the option ' + name });
    await sleep(300);
    return null;
}

async function testNewUsers(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    const start = adminNeutral(kept.admin);
    start.NewUserTemplate = null;
    await putAdmin(t, start, 'no New users template');
    await openAdminPage(t, page);
    await setSection(page, ADMIN, 'Advanced', true);
    const g = await ensureGroup(page, ADMIN, 'Advanced', 'New users', true);
    await sleep(800);
    const look = await page.evaluate(function (scope) {
        var h = window.__bfE2E;
        var outer = h.disclosure(scope, 'Advanced');
        var r = outer ? h.regionOf(outer) : null;
        var ctrl = r ? h.disclosure(r, 'New users') : null;
        var region = ctrl ? h.regionOf(ctrl) : null;
        if (!region) {
            return { found: false };
        }
        return {
            found: true,
            tableSelects: Array.prototype.filter.call(region.querySelectorAll('select[aria-label]'), function (s) { return /^(Audio|Subtitles) for /.test(s.getAttribute('aria-label')); }).length,
            radiogroups: Array.prototype.map.call(region.querySelectorAll('[role="radiogroup"]'), h.nameOf),
            tables: Array.prototype.filter.call(region.querySelectorAll('table'), h.vis).length,
            selects: Array.prototype.filter.call(region.querySelectorAll('select'), h.vis).length,
            search: !!Array.prototype.find.call(region.querySelectorAll('input[role="combobox"], input[type="search"]'), h.vis),
            text: h.text(region).slice(0, 300)
        };
    }, ADMIN);
    await t.check(g.found && look.found && look.text.indexOf(SPEC.myLanguages) >= 0 && look.search, 'admin page, Advanced > New users: "' + SPEC.myLanguages + '" with a language search (' + (look.found ? '"' + look.text.slice(0, 160) + '"' : 'no group') + ')', page);
    await t.check(look.found && !look.tableSelects && !look.radiogroups.length && !look.tables && !look.selects,
        'admin page, New users: only My languages — no "Audio and subtitles" table, no mode control, no dropdown (' + (look.found ? look.tableSelects + ' table dropdowns, radiogroups [' + look.radiogroups.join(', ') + '], ' + look.tables + ' tables, ' + look.selects + ' dropdowns' : 'no group') + ')', page);

    const tableFieldsOf = (tpl) => {
        if (!tpl) {
            return [];
        }
        const p = [];
        SPEC.tableFields.forEach((f) => { if ((tpl.Everyday || {})[f] !== null && (tpl.Everyday || {})[f] !== undefined) { p.push('Everyday.' + f); } });
        (tpl.Libraries || []).forEach((l) => { SPEC.tableFields.forEach((f) => { if ((l.Settings || {})[f] !== null && (l.Settings || {})[f] !== undefined) { p.push('Libraries[' + (l.Name || l.LibraryId) + '].' + f); } }); });
        if (tpl.PerLibrary !== null && tpl.PerLibrary !== undefined) {
            p.push('PerLibrary=' + tpl.PerLibrary);
        }
        return p;
    };
    const understood = (a) => ((a.NewUserTemplate || {}).Everyday || {}).Understood || null;
    // Add French, then German: each Save stores the list and nothing of the table.
    const steps = [['French', 'fr', ['fr']], ['German', 'de', ['fr', 'de']]];
    for (const s of steps) {
        const a0 = await getAdmin(t);
        const why = await addLanguage(page, ADMIN, 'Advanced', 'New users', s[0], s[1]);
        await sleep(t.settle);
        const bar = await barOf(page, true);
        if (!why && bar.visible) {
            await saveAdminPage(t, page);
        }
        const a1 = await getAdmin(t);
        const tf = tableFieldsOf(a1.NewUserTemplate);
        const stray = strayPaths(a0, a1, ['NewUserTemplate']);
        await t.check(!why && bar.visible && same(understood(a1), s[2]) && !tf.length && !stray.length, 'New users: adding ' + s[0] + ' and saving stores NewUserTemplate.Everyday.Understood ' + JSON.stringify(s[2]) + ' and no table field (stored '
            + JSON.stringify(understood(a1)) + (tf.length ? '; table fields: ' + tf.join(', ') : '') + (stray.length ? '; also changed: ' + stray.join(', ') : '') + ')' + (why ? ' — ' + why : '') + (bar.visible ? '' : ' — no save bar'), page);
    }
    // Remove both: an empty list means "from their Jellyfin settings".
    const a2 = await getAdmin(t);
    let removed = 0;
    const removeEl = function (scope) {
        var h = window.__bfE2E;
        var outer = h.disclosure(scope, 'Advanced');
        var r = outer ? h.regionOf(outer) : null;
        var ctrl = r ? h.disclosure(r, 'New users') : null;
        var region = ctrl ? h.regionOf(ctrl) : null;
        return region ? (Array.prototype.find.call(region.querySelectorAll('button'), function (b) {
            return h.vis(b) && !b.disabled && (b.classList.contains('bfui-remove') || /^Remove/i.test(b.getAttribute('aria-label') || h.text(b)));
        }) || null) : null;
    };
    for (let i = 0; i < 2; i++) {
        try {
            await page.click(removeEl, { args: [ADMIN], what: 'Remove in My languages', timeout: 3000 });
            removed++;
            await sleep(300);
        } catch (e) {
            break;
        }
    }
    await sleep(t.settle);
    if ((await barOf(page, true)).visible) {
        await saveAdminPage(t, page);
    }
    const a3 = await getAdmin(t);
    const u3 = understood(a3);
    await t.check(removed === 2 && (!u3 || !u3.length) && !tableFieldsOf(a3.NewUserTemplate).length && !strayPaths(a2, a3, ['NewUserTemplate']).length,
        'New users: removing both languages and saving leaves no template languages (new people get theirs from Jellyfin) and no table field (' + removed + ' removed; stored ' + JSON.stringify(u3) + ', template ' + (a3.NewUserTemplate ? 'kept' : 'null') + ')', page);
}

// ───────────────────────────── 18. Wording ─────────────────────────────

async function testWording(t) {

    const page = t.page;
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    const old = SPEC.oldWording.map((w) => new RegExp('\\b' + w + '\\b', 'i'));
    const scan = (text) => old.filter((re) => re.test(text)).map((re) => re.source.replace(/\\b/g, ''));
    await openPersonPage(t, page);
    await openEverything(page, false);
    const person = await page.evaluate(pgAllWords, null);
    const pOld = scan(person);
    await t.check(!pOld.length && person.indexOf(SPEC.myLanguages) >= 0, 'person page (English, everything open): no ' + SPEC.oldWording.map((w) => '"' + w + '"').join(', ') + ', and "' + SPEC.myLanguages + '" shows'
        + (pOld.length ? ' — found: ' + pOld.join(', ') : ''), page);
    await openEditor(t, page, t.ids.viewerId);
    await openEverything(page, true);
    const admin = await page.evaluate(pgAllWords, ADMIN);
    const aOld = scan(admin);
    const editor = await page.evaluate(pgAllWords, EDITOR);
    const newUsers = await page.evaluate(function (scope) {
        var h = window.__bfE2E;
        var outer = h.disclosure(scope, 'Advanced');
        var r = outer ? h.regionOf(outer) : null;
        var ctrl = r ? h.disclosure(r, 'New users') : null;
        var region = ctrl ? h.regionOf(ctrl) : null;
        return region ? region.textContent : '';
    }, ADMIN);
    await t.check(!aOld.length && editor.indexOf(SPEC.myLanguages) >= 0 && newUsers.indexOf(SPEC.myLanguages) >= 0,
        'admin page (English, every section and group open, the People editor open): no ' + SPEC.oldWording.map((w) => '"' + w + '"').join(', ') + ', and "' + SPEC.myLanguages + '" shows in the People editor (' + (editor.indexOf(SPEC.myLanguages) >= 0) + ') and New users (' + (newUsers.indexOf(SPEC.myLanguages) >= 0) + ')'
        + (aOld.length ? ' — found: ' + aOld.join(', ') : ''), page);
    // The shipped English strings (some are shown only in messages the test doesn't trigger): listed, not checked.
    const s = await strings(t);
    const offending = [];
    [['i18n/en.json', s.person], ['i18n-admin/en.json', s.admin]].forEach((pair) => {
        Object.keys(pair[1] || {}).forEach((k) => { const v = String(pair[1][k]); if (scan(v).length) { offending.push(pair[0] + ' ' + k + ' = "' + v.slice(0, 60) + '"'); } });
    });
    if (offending.length) {
        t.note('English strings with the old wording (shown nowhere the test opened): ' + offending.join('; '));
    }

    // 18b. The override words are the expected ones, the retired ones are gone, and nothing in the new screens says "understand" or "read" about the person.
    await overrideWording(t, page);
}

/** Override wording: nobody "understands" or "reads" anything in the new screens (the dialog, the ⋮ entry, the overrides tables and their confirmations). */
const RETIRED_OVERRIDE_WORDING = [/\bunderstand\w*/i, /\bunderstood\b/i, /\breads?\b/i, /\bI (read|understand|prefer|use)\b/i];

async function overrideWording(t, page) {
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    const s = await strings(t);
    const en = s.person;
    const different = Object.keys(OVERRIDE_WORDS).filter((k) => apos(en[k]) !== OVERRIDE_WORDS[k]);
    await t.check(!different.length, 'the shipped English (Assets/i18n/en.json) has the expected ' + Object.keys(OVERRIDE_WORDS).length + ' words' + (different.length ? ' — different: ' + different.map((k) => k + ' = ' + JSON.stringify(en[k])).join('; ') : ''), null);
    const retired = Object.keys(en).filter((k) => /^titles\.(title|remembered\.)/.test(k) || /^languages\.info$/.test(k));
    await t.check(!retired.length, 'the retired person-page words (the old "Shows and movies" list: titles.title, titles.remembered.*; the My languages tip: languages.info) are gone from the shipped English' + (retired.length ? ' — still there: ' + retired.join(', ') : ''), null);
    const banned = Object.keys(OVERRIDE_WORDS).filter((k) => RETIRED_OVERRIDE_WORDING.some((re) => re.test(OVERRIDE_WORDS[k])));
    await t.check(!banned.length, 'none of the override words says "understand" or "read" about the person' + (banned.length ? ' — ' + banned.join(', ') : ''), null);

    // The screens as they show: the person page's table and its confirmation, the item dialog, the People editor's table and confirmation.
    const made = await createSectionOverrides(t, t.api);
    await putOverride(t, t.ids.episodeId, 'Title', layerFor('lang:en', undefined));
    const scanOf = (text) => RETIRED_OVERRIDE_WORDING.filter((re) => re.test(text)).map((re) => re.source);
    await openPersonPage(t, page);
    const sec = await setOvSection(page, '#bf-overrides', false, true);
    const personText = await page.evaluate(function () { var n = document.querySelector('#bf-overrides'); return n ? n.textContent.replace(/\s+/g, ' ').trim() : ''; });
    await page.click(pgOvElement, { args: ['#bf-overrides', false, 'all'], what: 'Remove all show and movie overrides' });
    const ask = await waitDlg(page, false, 5000);
    if (ask) {
        await page.click(pgDlgButton, { args: [false, 'Cancel'], what: 'Cancel' });
        await waitNoDlg(page, false, 4000);
    }
    await t.check(made.count === 6 && sec.rows.length === 7, 'set up: the person page lists the overrides (' + sec.rows.length + ' rows)', page);
    const dlg = await openItemMenuDialog(t, page, t.ids.seriesId);
    const dialogText = dlg ? [dlg.title, dlg.subtitle].concat(dlg.explains, dlg.fields.map((f) => f.label), dlg.fields.map((f) => f.options.filter((o) => !o.g).map((o) => o.l).join(' ')), dlg.burned ? [dlg.burned.label] : [], dlg.buttons.map((b) => b.text)).join(' ') : '';
    if (dlg) {
        await escapeBfDialog(page, null);
    }
    const hitsPerson = scanOf(personText + ' ' + (ask ? ask.title + ' ' + ask.message : ''));
    const hitsDialog = scanOf(dialogText);
    await t.check(!!ask && !hitsPerson.length, 'the person page\'s overrides table (help line, columns, notes, buttons) and its Remove all confirmation say nothing about what the person "understands" or "reads" (' + personText.length + ' characters scanned' + (hitsPerson.length ? ' — found: ' + hitsPerson.join(', ') : '') + ')', page);
    await t.check(!!dlg && !hitsDialog.length && dlg.explains.some((x) => apos(x) === overrideWord('dialog.narrower')), 'the item dialog (title, target, applies line, narrower-override note, labels, plain choices, burned-in row, buttons) says nothing about what the person "understands" or "reads" (' + dialogText.length + ' characters scanned' + (hitsDialog.length ? ' — found: ' + hitsDialog.join(', ') : '') + ')', page);

    const vapi = await viewerApi(t);
    if (vapi) {
        t.defer('second user signed out', () => endViewerSession(t));
        await keepViewer(t);
        await createSectionOverrides(t, vapi);
        await openEditor(t, page, t.ids.viewerId);
        await setOvSection(page, '#bf-group-overrides', false, true);
        const editorText = await page.evaluate(function () { var n = document.querySelector('#bf-group-overrides'); return n ? n.textContent.replace(/\s+/g, ' ').trim() : ''; });
        await page.click(pgOvElement, { args: ['#bf-group-overrides', false, 'all'], what: 'Remove all show and movie overrides' });
        const ask2 = await waitDlg(page, false, 5000);
        if (ask2) {
            await page.click(pgDlgButton, { args: [false, 'Cancel'], what: 'Cancel' });
            await waitNoDlg(page, false, 4000);
        }
        const hitsEditor = scanOf(editorText + ' ' + (ask2 ? ask2.title + ' ' + ask2.message : ''));
        await t.check(!!ask2 && !hitsEditor.length, 'the People editor\'s overrides table and its Remove all confirmation say nothing about what the person "understands" or "reads" (' + editorText.length + ' characters scanned' + (hitsEditor.length ? ' — found: ' + hitsEditor.join(', ') : '') + ')', page);
    } else {
        t.note('the second person could not be signed in through the API; the People editor\'s new words are not scanned');
    }
}

// ───────────────────────────── 19. Settings version 8 and the Jellyfin Desktop row ─────────────────────────────

async function testServerV8(t) {

    const page = t.page;
    const ping = await t.api.get('/Babelfin/Ping');
    await t.check(ping.status === 200 && ping.body && ping.body.version === SPEC.pluginVersion,
        'GET Babelfin/Ping: plugin ' + SPEC.pluginVersion + ' (' + JSON.stringify(ping.body) + ')');
    await t.check((await getMe(t)).Settings.SchemaVersion === SPEC.personSchemaVersion,
        'GET Me/Settings: person schema ' + SPEC.personSchemaVersion);
    const admin = await getAdmin(t);
    await t.check(admin.ConfigVersion === SPEC.configVersion, 'GET Admin/Settings: settings version ' + SPEC.configVersion + ' (ConfigVersion ' + admin.ConfigVersion + ')');
    const desk = (admin.AppModes || []).find((a) => a.Client === SPEC.desktop);
    await t.check(!!desk && String(desk.Mode) === SPEC.desktopMode, 'GET Admin/Settings: the app list has "' + SPEC.desktop + '" on ' + SPEC.desktopMode + ' (' + (desk ? canon(desk) : 'no row; clients: ' + (admin.AppModes || []).map((a) => a.Client).join(', ')) + ')');
    const shipped = await t.api.get('/Babelfin/Admin/Defaults');
    const sdesk = shipped.status === 200 ? ((shipped.body.AppModes || []).find((a) => a.Client === SPEC.desktop)) : null;
    await t.check(!!sdesk && String(sdesk.Mode) === SPEC.desktopMode && shipped.body.ConfigVersion === SPEC.configVersion,
        'GET Admin/Defaults: version ' + SPEC.configVersion + ' with "' + SPEC.desktop + '" on ' + SPEC.desktopMode + ' (' + shipped.status + (shipped.body ? ', version ' + shipped.body.ConfigVersion + ', ' + canon(sdesk) : '') + ')');
    const tpl = admin.NewUserTemplate;
    const tf = tpl ? SPEC.tableFields.filter((f) => (tpl.Everyday || {})[f] !== null && (tpl.Everyday || {})[f] !== undefined) : [];
    await t.check(!tpl || (!tf.length && !(tpl.Libraries || []).length && (tpl.PerLibrary === null || tpl.PerLibrary === undefined)),
        'GET Admin/Settings: the New users template has no table fields, library entries or mode (' + (tpl ? 'fields ' + (tf.join(', ') || 'none') + ', ' + (tpl.Libraries || []).length + ' library entries, PerLibrary ' + tpl.PerLibrary : 'no template') + ')');
    await openAdminPage(t, page);
    await setSection(page, ADMIN, 'Advanced', true);
    await ensureGroup(page, ADMIN, 'Advanced', 'Apps', true);
    let c = null;
    try {
        await page.waitFor(function (scope, l) { var h = window.__bfE2E; return !!h.findControl(scope, { label: l }); }, { timeout: 10000, what: 'the Jellyfin Desktop row' }, ADMIN, ADMIN_MENUS.appPrefix + ' · ' + SPEC.desktop);
        c = await page.evaluate(pgControl, ADMIN, { label: ADMIN_MENUS.appPrefix + ' · ' + SPEC.desktop }, null);
    } catch (e) {
        c = null;
    }
    await t.check(!!c && c.text === SPEC.desktopLabel, 'admin page, Apps: the "' + SPEC.desktop + '" row reads "' + SPEC.desktopLabel + '" (' + (c ? '"' + c.text + '"' : 'no row') + ')', page);
    await page.evaluate(pgClearBabelfinStorage);
}

// ───────────────────────────── 20. Alignment: text centred in every button ─────────────────────────────

/**
 * Every visible button under the roots (group headings left out: their title sits on the left by design), with the space left and right
 * of its text and above and below it, inside the content box (border and padding taken off). Rounded to 0.1 px.
 */
function pgButtonGaps(rootSel) {
    var out = [];
    var seen = new Set();
    var px = function (v) { return parseFloat(v) || 0; };
    var r1 = function (v) { return Math.round(v * 10) / 10; };
    Array.prototype.forEach.call(document.querySelectorAll(rootSel), function (root) {
        Array.prototype.forEach.call(root.querySelectorAll('button, [role="button"], [role="radio"], .bf-btn, .bfui-btn'), function (b) {
            if (seen.has(b) || b.closest('.bfui-disclosure-heading, summary') || b.matches('input')) {
                return;
            }
            seen.add(b);
            var r = b.getBoundingClientRect();
            var cs = getComputedStyle(b);
            if (!r.width || !r.height || cs.visibility === 'hidden' || (!b.offsetParent && cs.position !== 'fixed')) {
                return;
            }
            var range = document.createRange();
            range.selectNodeContents(b);
            var tr = range.getBoundingClientRect();
            if (!tr.width) {
                return;
            }
            var left = r.left + px(cs.borderLeftWidth) + px(cs.paddingLeft);
            var right = r.right - px(cs.borderRightWidth) - px(cs.paddingRight);
            var top = r.top + px(cs.borderTopWidth) + px(cs.paddingTop);
            var bottom = r.bottom - px(cs.borderBottomWidth) - px(cs.paddingBottom);
            out.push({
                text: (b.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 40) || (b.getAttribute('aria-label') || '?'),
                cls: typeof b.className === 'string' ? b.className.trim() : '',
                w: Math.round(r.width), h: Math.round(r.height), padX: r1(px(cs.paddingLeft)), padY: r1(px(cs.paddingTop)),
                l: r1(tr.left - left), r: r1(right - tr.right), t: r1(tr.top - top), b: r1(bottom - tr.bottom)
            });
        });
    });
    return out;
}

/** Checks the buttons in view: the text is centred across (within 2 px) and down (within 2 px). */
async function centredButtons(t, page, rootSel, where) {
    const gaps = await page.evaluate(pgButtonGaps, rootSel);
    gaps.forEach((g) => t.verbose(where + ': "' + g.text + '" [' + g.cls + '] ' + g.w + 'x' + g.h + ' left ' + g.l + ' right ' + g.r + ' top ' + g.t + ' bottom ' + g.b));
    const off = gaps.filter((g) => Math.abs(g.l - g.r) > 2 || Math.abs(g.t - g.b) > 2);
    await t.check(gaps.length > 0 && !off.length, where + ': the text of every button is centred (' + gaps.length + ' buttons'
        + (off.length ? '; off centre: ' + off.map((g) => '"' + g.text + '" ' + g.w + 'x' + g.h + ' left ' + g.l + '/right ' + g.r + ', top ' + g.t + '/bottom ' + g.b).join('; ') : '') + ')', page);
    // Jellyfin-styled buttons (class "raised") keep Jellyfin's padding, so they don't shrink to bare text.
    const raised = gaps.filter((g) => /(^|\s)raised(\s|$)/.test(g.cls));
    const bare = raised.filter((g) => g.padX < 8 || g.padY < 6 || g.h < 30);
    if (raised.length) {
        await t.check(!bare.length, where + ': Jellyfin-styled buttons have Jellyfin\'s padding (' + raised.map((g) => '"' + g.text + '" ' + g.w + 'x' + g.h + ' padding ' + g.padY + '/' + g.padX).join('; ') + ')', page);
    }
    if (t.opts.verbose) {
        await sleep(400);
        await page.screenshot(path.join(t.opts.shots, 'look-' + slug(where) + '.png'), true);
    }
}

async function testAlignment(t) {
    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    t.defer('viewport back to desktop', () => page.clearViewport());
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    // Show and movie overrides for the "Show and movie overrides" tables to list, with their Remove and Remove all buttons: a movie for the person
    // (its own title already ends in its year) and two titles for the second person's editor, who stands in for the People editor.
    const ov = await putOverride(t, t.ids.movieId, 'Title', { AudioChain: [{ Kind: 'Language', Language: 'de' }, { Kind: 'VideoDefault' }] });
    if (ov.status !== 200) {
        t.note('a show or movie override could not be stored (' + ov.status + ')');
    }
    await keepViewer(t);
    const vapi = await viewerApi(t);
    if (vapi) {
        t.defer('second user signed out', () => endViewerSession(t));
        await putOverride(t, t.ids.movieId, 'Title', layerFor('lang:de', 'off'), vapi);
        await putOverride(t, t.ids.seriesId, 'Series', layerFor('video', undefined), vapi);
    } else {
        t.note('the second person could not be signed in through the API: the People editor is checked without overrides to list');
    }
    for (const size of [null, [375, 812]]) {
        const at = size ? ' at ' + size[0] + 'x' + size[1] : '';
        if (size) {
            await page.setViewport(size[0], size[1], true);
        }
        await openPersonPage(t, page);
        await openEverything(page, false);
        await setOvSection(page, '#bf-overrides', false, true);
        await centredButtons(t, page, '#bf-app', 'person page' + at);
        if (!size && ov.status === 200) {
            // The test movie's own title already ends in its year: the table names it once.
            const sec = await page.evaluate(pgOvSection, '#bf-overrides', false);
            const row = sec.rows.find((r) => /Berlin Test/.test(r.title || '')) || {};
            await t.check(!!row.title && !/\((\d{4})\)\s*\(\1\)/.test(row.title), 'person page: Show and movie overrides names the movie with its year once (' + JSON.stringify(row.title || sec.rows.map((r) => r.title)) + ')', page);
        }
        // The confirmation of Remove all.
        await page.click(pgOvElement, { args: ['#bf-overrides', false, 'all'], what: 'Remove all show and movie overrides' });
        if (await waitDialog(page, 5000)) {
            await centredButtons(t, page, '[role="dialog"], [role="alertdialog"]', 'person page, Remove all confirmation' + at);
            await answerDialog(page, false);
        }
        // The save bar, and a confirmation dialog.
        const personMode = await modeInfo(page, null);
        const personTarget = personMode.checked === SPEC.modePer ? SPEC.modeSame : SPEC.modePer;
        await t.check(personMode.found && !!personMode.checked, 'person page: current table mode is known' + at, page);
        await clickMode(page, null, personTarget);
        const personDirty = await page.waitFor(pgSaveBarShown, { timeout: 5000, what: 'the person save bar after changing mode' }, false).then(() => true, () => false);
        await t.check(personDirty, 'person page: changing mode shows the save bar' + at, page);
        await centredButtons(t, page, '.bf-savebar', 'person page save bar' + at);
        await page.click(pgSectionButtonEl, { args: [null, 'Advanced', SPEC.restore], what: 'the "' + SPEC.restore + '" button' });
        if (await waitDialog(page, 5000)) {
            await centredButtons(t, page, '[role="dialog"], [role="alertdialog"]', 'person page dialog' + at);
            await answerDialog(page, false);
        }
        await openAdminPage(t, page);
        await openEverything(page, true);
        await centredButtons(t, page, ADMIN, 'admin page' + at);
        // The save bar after a change, and a confirmation dialog.
        const adminMode = await modeInfo(page, ADMIN);
        const adminTarget = adminMode.checked === SPEC.modePer ? SPEC.modeSame : SPEC.modePer;
        await t.check(adminMode.found && !!adminMode.checked, 'admin page: current table mode is known' + at, page);
        await clickMode(page, ADMIN, adminTarget);
        const adminDirty = await page.waitFor(function (scope) {
            var b = document.querySelector(scope + ' .bf-savebar');
            return !!b && !b.hidden && !b.classList.contains('hide') && getComputedStyle(b).display !== 'none' && b.getBoundingClientRect().height > 0;
        }, { timeout: 5000, what: 'the admin save bar after changing mode' }, ADMIN).then(() => true, () => false);
        await t.check(adminDirty, 'admin page: changing mode shows the save bar' + at, page);
        await centredButtons(t, page, ADMIN + ' .bf-savebar', 'admin page save bar' + at);
        await page.click(pgSectionButtonEl, { args: [ADMIN, 'Advanced', SPEC.restore], what: 'the admin "' + SPEC.restore + '" button' });
        if (await waitDialog(page, 5000)) {
            await centredButtons(t, page, '[role="dialog"], [role="alertdialog"]', 'admin page dialog' + at);
            await answerDialog(page, false);
        }
        await openEditor(t, page, t.ids.viewerId);
        await setOvSection(page, '#bf-group-overrides', false, true);
        await centredButtons(t, page, EDITOR, 'admin page, People editor' + at);
        await page.click(pgOvElement, { args: ['#bf-group-overrides', false, 'all'], what: 'Remove all show and movie overrides in the editor' });
        if (await waitDialog(page, 5000)) {
            await centredButtons(t, page, '[role="dialog"], [role="alertdialog"]', 'People editor, Remove all confirmation' + at);
            await answerDialog(page, false);
        }
        // The Babelfin dialog of the item page (its buttons: Remove override, Cancel, Save).
        const dlg = await openItemMenuDialog(t, page, t.ids.movieId);
        if (dlg) {
            await centredButtons(t, page, '.bfDialog', 'item dialog' + at);
            await escapeBfDialog(page, null);
        } else {
            await t.check(false, 'the movie page ⋮ entry opens the dialog' + at, page);
        }
    }
}

// ───────────────────────────── 21. Babelfin Settings at #/babelfin ─────────────────────────────

/**
 * The #/babelfin panel as the page shows it: the route, Jellyfin's header (the lowest visible .MuiAppBar-root / .skinHeader at the top),
 * the panel and its frame (same origin, so its document is readable), the titles, and whether "Page not found" text shows.
 */
function pgSettingsRoute() {
    var shown = function (n) {
        if (!n) { return null; }
        var r = n.getBoundingClientRect();
        var cs = getComputedStyle(n);
        return r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden' ? r : null;
    };
    var header = null;
    Array.prototype.forEach.call(document.querySelectorAll('.MuiAppBar-root, .skinHeader'), function (n) {
        var r = shown(n);
        if (r && r.top <= 1 && (!header || r.bottom > header.bottom)) {
            header = { top: Math.round(r.top), bottom: Math.round(r.bottom), cls: String(n.className).split(' ')[0] };
        }
    });
    var panel = document.querySelector('.bfSettingsPanel');
    var frame = panel ? panel.querySelector('iframe.bfSettingsFrame') : null;
    var doc = null;
    try { doc = frame ? frame.contentDocument : null; } catch (e) { doc = null; }
    var tableReady = !!doc && Array.prototype.some.call(doc.querySelectorAll('select[aria-label]'), function (s) {
        var r = s.getBoundingClientRect();
        return /^Audio for /.test(s.getAttribute('aria-label')) && r.width > 0;
    });
    var pr = panel ? panel.getBoundingClientRect() : null;
    var bar = shown(document.querySelector('.nowPlayingBar:not(.hide)'));
    var fallback = document.querySelector('#fallbackPage');
    var fallbackVisible = !!fallback && Array.prototype.some.call(fallback.querySelectorAll('h1, h2, p, a, button'), function (n) { return !!shown(n); });
    var titleEl = Array.prototype.find.call(document.querySelectorAll('.skinHeader .pageTitle'), function (n) { return !!shown(n); });
    var menu = document.getElementById('app-user-menu');
    return {
        hash: location.hash, panel: !!panel, frame: !!frame, tableReady: tableReady, header: header,
        panelTop: pr ? Math.round(pr.top) : null, panelBottom: pr ? Math.round(pr.bottom) : null, panelWidth: pr ? Math.round(pr.width) : null,
        innerHeight: window.innerHeight, innerWidth: window.innerWidth, barTop: bar ? Math.round(bar.top) : null,
        docScroll: document.documentElement.scrollHeight - window.innerHeight, fallbackVisible: fallbackVisible,
        title: document.title, legacyTitle: titleEl ? titleEl.textContent.trim() : null,
        embedded: doc ? doc.documentElement.classList.contains('bf-embedded') : null, back: doc ? !!doc.querySelector('.bf-back') : null,
        frameMessage: doc && doc.querySelector('.bf-message') ? doc.querySelector('.bf-message').textContent.replace(/\s+/g, ' ').trim() : null,
        parentAuthenticated: !!(window.ApiClient && typeof ApiClient.getCurrentUserId === 'function' && ApiClient.getCurrentUserId()),
        menuOpen: !!menu && !!shown(menu)
    };
}

/** Waits until #/babelfin shows the frame with the table drawn; { ok, r }. */
async function waitSettingsRoute(page, timeout) {
    const deadline = Date.now() + (timeout || 20000);
    let r = null;
    for (;;) {
        r = await page.evaluate(pgSettingsRoute).catch(() => null);
        if (r && r.hash === SPEC.route && r.tableReady) {
            await sleep(300);
            return { ok: true, r: (await page.evaluate(pgSettingsRoute).catch(() => null)) || r };
        }
        if (Date.now() > deadline) {
            return { ok: false, r: r };
        }
        await sleep(250);
    }
}

async function waitNoPanel(page, timeout) {
    const deadline = Date.now() + (timeout || 10000);
    for (;;) {
        if (await page.evaluate('!document.querySelector(".bfSettingsPanel")').catch(() => false)) {
            return true;
        }
        if (Date.now() > deadline) {
            return false;
        }
        await sleep(200);
    }
}

function routeText(r) {
    return r ? 'route ' + r.hash + ', panel ' + r.panel + ', table ' + r.tableReady + ', header ' + (r.header ? r.header.cls + ' to ' + r.header.bottom : 'none')
        + ', panel ' + r.panelTop + '-' + r.panelBottom + ' of ' + r.innerHeight
        + (!r.tableReady ? ', parent signed in ' + r.parentAuthenticated + ', frame message ' + JSON.stringify(r.frameMessage) : '') : 'nothing';
}

/** Checks for a shown route: under the header, down to the window's bottom (or the now-playing bar), no 404 text, titles, embedded mode. */
async function routeChecks(t, page, where, r, o) {
    const opts = o || {};
    if (!r) {
        return;
    }
    const bottom = r.barTop !== null ? r.barTop : r.innerHeight;
    await t.check(!!r.header && Math.abs(r.panelTop - r.header.bottom) <= 1 && Math.abs(r.panelBottom - bottom) <= 1 && r.panelWidth === r.innerWidth,
        where + ': the panel starts at the bottom of Jellyfin\'s header and fills the window below it (' + routeText(r) + ', width ' + r.panelWidth + ' of ' + r.innerWidth + ')', page);
    await t.check(!r.fallbackVisible && r.docScroll <= 1, where + ': no "Page not found" text shows and the page behind doesn\'t scroll (' + r.fallbackVisible + ', ' + r.docScroll + ')', page);
    await t.check(r.title === SPEC.menuText && (!opts.legacyTitle || r.legacyTitle === SPEC.menuText),
        where + ': the title reads "' + SPEC.menuText + '" (document "' + r.title + '"' + (r.legacyTitle !== null ? ', header "' + r.legacyTitle + '"' : '') + ')', page);
    await t.check(r.embedded === true && r.back === false, where + ': the page in the frame is in embedded mode, without its own back link (' + r.embedded + ', ' + r.back + ')', page);
    if (opts.menuClosed) {
        await t.check(!r.menuOpen, where + ': the avatar menu closed', page);
    }
}

/** Changes a visible subtitles dropdown in the frame (the browser's own change event); true when it changed. */
function pgFrameChange() {
    var f = document.querySelector('.bfSettingsFrame');
    var d = f && f.contentDocument;
    var s = d ? Array.prototype.find.call(d.querySelectorAll('select[aria-label]'), function (x) {
        var r = x.getBoundingClientRect();
        return /^Subtitles for /.test(x.getAttribute('aria-label') || '') && !x.disabled && r.width > 0 && r.height > 0;
    }) : null;
    var opt = s ? Array.prototype.find.call(s.options, function (o) { return !o.disabled && o.value !== s.value; }) : null;
    if (!opt) {
        return false;
    }
    s.value = opt.value;
    s.dispatchEvent(new f.contentWindow.Event('change', { bubbles: true }));
    return true;
}

function pgFrameDirty() {
    var f = document.querySelector('.bfSettingsFrame');
    var app = f && f.contentWindow ? f.contentWindow.BabelfinApp : null;
    return app ? app.hasChanges() : null;
}

/** The dialog open in the frame: { title, buttons }, or null. */
function pgFrameDialog() {
    var f = document.querySelector('.bfSettingsFrame');
    var d = f && f.contentDocument;
    var dlg = d ? Array.prototype.find.call(d.querySelectorAll('[role="dialog"], [role="alertdialog"]'), function (x) { var r = x.getBoundingClientRect(); return !x.hidden && r.width > 0; }) : null;
    if (!dlg) {
        return null;
    }
    var h = dlg.querySelector('h1, h2, h3');
    return { title: h ? h.textContent.trim() : '', buttons: Array.prototype.map.call(dlg.querySelectorAll('button'), function (b) { return b.textContent.trim(); }) };
}

/** Answers the frame's dialog: the confirming (danger or primary) button, or the other one. */
function pgFrameAnswer(confirm) {
    var f = document.querySelector('.bfSettingsFrame');
    var d = f && f.contentDocument;
    var dlg = d ? d.querySelector('[role="dialog"], [role="alertdialog"]') : null;
    if (!dlg) {
        return null;
    }
    var buttons = Array.prototype.slice.call(dlg.querySelectorAll('button'));
    var ok = buttons.find(function (b) { return /bfui-btn-(primary|danger)/.test(b.className); });
    var pick = confirm ? ok : buttons.find(function (b) { return b !== ok; });
    if (!pick) {
        return null;
    }
    pick.click();
    return pick.textContent.trim();
}

async function waitFrameDialog(page, timeout) {
    const deadline = Date.now() + (timeout || 10000);
    for (;;) {
        const d = await page.evaluate(pgFrameDialog).catch(() => null);
        if (d || Date.now() > deadline) {
            return d;
        }
        await sleep(200);
    }
}

async function testSettingsRoute(t) {
    const page = t.page;
    const layout = await currentLayout(page);
    t.defer('layout back to "' + layout + '"', () => setLayout(t, layout));
    t.defer('viewport back to desktop', () => page.clearViewport());
    const before = (await getMe(t)).Settings;
    for (const mode of ['', 'desktop-legacy', 'mobile-legacy', 'tv']) {
        const name = mode || 'modern';
        await setLayout(t, mode);
        await page.navigate(t.url('/web/' + SPEC.route));
        const got = await waitSettingsRoute(page, 30000);
        await t.check(got.ok, name + ': ' + SPEC.route + ' opened directly shows Babelfin Settings (' + routeText(got.r) + ')', page);
        await routeChecks(t, page, name, got.r, { legacyTitle: !!mode });
        if (t.opts.verbose) {
            await page.screenshot(path.join(t.opts.shots, 'look-route-' + name + '.png'));
        }
        await page.evaluate(function () { location.hash = '#/home'; });
        await t.check(await waitNoPanel(page, 10000), name + ': leaving without changes closes the panel at once', page);
    }
    await setLayout(t, '');

    // A reload on the route shows it again.
    await page.navigate(t.url('/web/' + SPEC.route));
    await waitSettingsRoute(page, 30000);
    await page.reload();
    await waitWebReady(t, page);
    const again = await waitSettingsRoute(page, 30000);
    await t.check(again.ok, 'a reload on ' + SPEC.route + ' shows Babelfin Settings again (' + routeText(again.r) + ')', page);

    // Unsaved changes: leaving asks with the page's own dialog; Cancel stays with the change, Discard leaves; nothing is saved.
    const changed = await page.evaluate(pgFrameChange);
    await sleep(t.settle);
    const dirty = await page.evaluate(pgFrameDirty);
    await t.check(changed && dirty === true, 'a change in the frame counts as unsaved (changed ' + changed + ', unsaved ' + dirty + ')', page);
    await page.evaluate(function () { location.hash = '#/home'; });
    const ask = await waitFrameDialog(page, 10000);
    const still = await page.evaluate(pgSettingsRoute);
    await t.check(!!ask && still.panel, 'leaving with unsaved changes keeps the settings on screen and asks first (' + (ask ? '"' + ask.title + '": ' + ask.buttons.join(' / ') : 'no dialog') + ')', page);
    if (ask) {
        await page.evaluate(pgFrameAnswer, false);
        const back = await waitSettingsRoute(page, 10000);
        const stillDirty = await page.evaluate(pgFrameDirty);
        await t.check(back.ok && stillDirty === true, 'Cancel goes back to ' + SPEC.route + ' with the change still there (' + (back.r && back.r.hash) + ', unsaved ' + stillDirty + ')', page);
        await page.evaluate(function () { location.hash = '#/home'; });
        const ask2 = await waitFrameDialog(page, 10000);
        if (ask2) {
            await page.evaluate(pgFrameAnswer, true);
        }
        const gone = await waitNoPanel(page, 10000);
        const hash = await page.evaluate('location.hash');
        await t.check(!!ask2 && gone && /^#\/home/.test(hash), 'Discard leaves: the panel closes and ' + hash + ' shows', page);
    }
    const after = (await getMe(t)).Settings;
    await t.check(same(after, before), 'leaving saved nothing' + (same(after, before) ? '' : ' (changed: ' + changedPaths(before, after).join(', ') + ')'), null);

    // Phone width.
    await page.setViewport(375, 812, true);
    await page.navigate(t.url('/web/' + SPEC.route));
    const phone = await waitSettingsRoute(page, 30000);
    await t.check(phone.ok, 'at 375x812 ' + SPEC.route + ' shows Babelfin Settings (' + routeText(phone.r) + ')', page);
    await routeChecks(t, page, '375x812', phone.r, {});
    await page.evaluate(function () { location.hash = '#/home'; });
    await waitNoPanel(page, 10000);
    await page.clearViewport();

    // /Babelfin/app opened directly is still the standalone page with its own way back.
    await openPersonPage(t, page);
    const standalone = await page.evaluate('!!document.querySelector(".bf-back") && !document.documentElement.classList.contains("bf-embedded")');
    await t.check(standalone, '/Babelfin/app opened directly still shows its own back link', page);
}

// ───────────────────────────── 22. Explanations ("i") on both pages ─────────────────────────────

/** Every infoTip under scope: its accessible name, its tip's text and state, the control it describes, and its size. */
function pgInfoTips(scope) {
    var root = scope ? document.querySelector(scope) : document;
    if (!root) {
        return [];
    }
    return Array.prototype.filter.call(root.querySelectorAll('button.bfui-info'), function (b) { return window.__bfE2E.vis(b); }).map(function (b) {
        var id = b.getAttribute('aria-describedby');
        var tip = id ? document.getElementById(id) : null;
        var row = b.closest('.bfui-row, .bfui-switch, .bf-row, .bf-check-line, .bf-languages-line');
        var control = row ? row.querySelector('select, input[type="checkbox"]') : null;
        var label = row ? row.querySelector('label') : null;
        var field = b.closest('[data-field]');
        var r = b.getBoundingClientRect();
        // How far the "i" sits from the end of the text it explains (the element just before its wrapper).
        var wrap = b.closest('.bfui-info-wrap');
        var before = wrap ? wrap.previousElementSibling : null;
        while (before && !window.__bfE2E.vis(before)) before = before.previousElementSibling;
        var gap = null;
        if (before) {
            var range = document.createRange();
            range.selectNodeContents(before);
            var tr = before.tagName === 'SELECT' ? before.getBoundingClientRect() : range.getBoundingClientRect();
            gap = tr.width > 0 ? Math.round(r.left - tr.right) : null;
        }
        return {
            gap: gap,
            name: b.getAttribute('aria-label'), id: id, tip: tip ? tip.textContent : null, hidden: tip ? tip.hidden : null,
            field: field ? field.getAttribute('data-field') : null,
            described: control ? (control.getAttribute('aria-describedby') || '').split(/\s+/).indexOf(id) >= 0 : null,
            labelText: label ? label.textContent.replace(/\s+/g, ' ').trim() : null,
            w: Math.round(r.width), h: Math.round(r.height)
        };
    });
}

/** The centre of the infoTip under scope whose explanation is text (scrolled into view), or null. */
function pgInfoCentre(scope, text) {
    var root = scope ? document.querySelector(scope) : document;
    var b = !root ? null : Array.prototype.find.call(root.querySelectorAll('button.bfui-info'), function (x) {
        var tip = document.getElementById(x.getAttribute('aria-describedby'));
        return tip && tip.textContent === text;
    });
    if (!b) {
        return null;
    }
    b.scrollIntoView({ block: 'center' });
    var r = b.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2, id: b.getAttribute('aria-describedby') };
}

/** A tip's state: shown, inside the window, and its button's aria-expanded. */
function pgTipState(id) {
    var tip = document.getElementById(id);
    if (!tip) {
        return null;
    }
    var r = tip.getBoundingClientRect();
    var b = document.querySelector('button.bfui-info[aria-describedby="' + id + '"]');
    return {
        shown: !tip.hidden && r.width > 0 && r.height > 0,
        inside: r.left >= 0 && r.top >= 0 && r.right <= window.innerWidth && r.bottom <= window.innerHeight,
        box: [Math.round(r.left), Math.round(r.top), Math.round(r.right), Math.round(r.bottom)], expanded: b ? b.getAttribute('aria-expanded') : null,
        onTop: tip.parentNode === document.body
    };
}

async function mouseTo(page, x, y) {
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x, y: y, button: 'none' });
    await sleep(200);
}

/** Hover, leave, click (pins), leave, Escape, click, click elsewhere: what the tip of the explanation `text` does. */
async function tipBehaviour(t, page, where, text, scope, neutral) {
    const c = await page.evaluate(pgInfoCentre, scope || null, text);
    // "Elsewhere": the centre of a plain line of text on the page (the top-left corner holds Jellyfin's own links on the admin page).
    const away = await page.evaluate(function (sel) {
        var n = document.querySelector(sel);
        var r = n ? n.getBoundingClientRect() : null;
        return r && r.width > 0 ? { x: r.left + Math.min(r.width / 2, 40), y: r.top + r.height / 2 } : { x: 2, y: 2 };
    }, neutral || '.bf-subtitle');
    if (!c) {
        await t.check(false, where + ': an "i" with the explanation "' + text.slice(0, 50) + '…" to try', page);
        return;
    }
    const steps = [];
    const state = async () => (await page.evaluate(pgTipState, c.id)) || { shown: false, inside: false, expanded: null, gone: true };
    await mouseTo(page, c.x, c.y);
    const hover = await state();
    if (t.opts.verbose) {
        await page.screenshot(path.join(t.opts.shots, 'look-tip-' + slug(where) + '.png'));
    }
    steps.push(['hover shows it inside the window, above the page (moved to the end of <body>)', hover.shown && hover.inside && hover.expanded === 'true' && hover.onTop, hover]);
    await mouseTo(page, 2, 2);
    const left = await state();
    steps.push(['leaving hides it', !left.shown && left.expanded === 'false', left]);
    await page.click(function (id) { return document.querySelector('button.bfui-info[aria-describedby="' + id + '"]'); }, { args: [c.id], what: 'the "i" button' });
    await mouseTo(page, 2, 2);
    const pinned = await state();
    steps.push(['a click keeps it after the pointer leaves', pinned.shown && pinned.inside, pinned]);
    const url = await page.evaluate('location.href');
    await page.press('Escape');
    await sleep(400);
    const esc = await state();
    steps.push(['Escape hides it', !esc.shown, esc]);
    const urlAfter = await page.evaluate('location.href');
    steps.push(['Escape stays on the page', urlAfter === url, urlAfter]);
    await page.click(function (id) { return document.querySelector('button.bfui-info[aria-describedby="' + id + '"]'); }, { args: [c.id], what: 'the "i" button' });
    await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: away.x, y: away.y, button: 'left', buttons: 1, clickCount: 1 });
    await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: away.x, y: away.y, button: 'left', buttons: 0, clickCount: 1 });
    await sleep(150);
    const outside = await state();
    steps.push(['a click elsewhere hides it', !outside.shown, outside]);
    const bad = steps.filter((x) => !x[1]);
    await t.check(!bad.length, where + ': the "i" ' + steps.map((x) => x[0]).join(', ')
        + (bad.length ? ' — not: ' + bad.map((x) => x[0] + ' ' + JSON.stringify(x[2])).join('; ') : ''), page);
}

async function testInfoTips(t) {
    const page = t.page;
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    t.defer('viewport back to desktop', () => page.clearViewport());
    const s = await strings(t);
    const en = s.person;
    const ae = s.admin;

    // The person page: every Advanced option, the switch and the My languages line.
    await openPersonPage(t, page);
    await openEverything(page, false);
    const tips = await page.evaluate(pgInfoTips, null);
    const problems = [];
    PERSON_MENUS.forEach((m) => {
        const want = en['field.' + m.field + '.info'];
        const hit = tips.filter((x) => x.field === m.field);
        if (hit.length !== 1) {
            problems.push(m.field + ': ' + hit.length + ' "i" buttons');
            return;
        }
        const x = hit[0];
        if (!want || x.tip !== want) {
            problems.push(m.field + ': explanation ' + JSON.stringify((x.tip || '').slice(0, 40)));
        }
        if (!x.described) {
            problems.push(m.field + ': the dropdown isn\'t described by it');
        }
        if (x.name !== 'More about ' + x.labelText || /\bi\b|More about/.test(x.labelText || '')) {
            problems.push(m.field + ': name "' + x.name + '", label "' + x.labelText + '"');
        }
        if (x.w < 24 || x.h < 24) {
            problems.push(m.field + ': ' + x.w + 'x' + x.h + ' px (under 24)');
        }
    });
    // The "My languages" line has no "i" any more (taken away on purpose), so only the on/off switch has one besides the options.
    [['advanced.enabled.info', 'the on/off switch']].forEach((pair) => {
        if (!tips.some((x) => x.tip === en[pair[0]] && x.hidden === true)) {
            problems.push(pair[1] + ': no "i" with ' + pair[0]);
        }
    });
    const farP = tips.filter((x) => x.gap === null || x.gap < -1 || x.gap > 16).map((x) => x.name + ' (' + x.gap + ' px)');
    await t.check(!farP.length, 'person page: every "i" sits right after the text it explains (at most 16 px away)' + (farP.length ? ' — not: ' + farP.join('; ') : ''), page);
    await t.check(!problems.length, 'person page: every Advanced option and the on/off switch have an "i" with their own explanation, '
        + 'described by it, a clean label and a 24 px target (' + tips.length + ' found)' + (problems.length ? ' — ' + problems.join('; ') : ''), page);
    // The "My languages" line and its editor carry no "i" and no explanation (the key languages.info is gone from the shipped English too).
    const langTips = () => page.evaluate(function () {
        var s = document.querySelector('.bf-languages');
        return { found: !!s, tips: s ? s.querySelectorAll('button.bfui-info').length : -1, described: s ? s.querySelectorAll('[aria-describedby]').length : -1, text: s ? s.textContent.replace(/\s+/g, ' ').trim().slice(0, 60) : '' };
    });
    const langClosed = await langTips();
    await t.check(langClosed.found && langClosed.tips === 0 && langClosed.described === 0 && en['languages.info'] === undefined,
        'person page: the "My languages" line has no "i" (no explanation button, nothing described by one, no languages.info in the shipped English: ' + JSON.stringify(langClosed) + ')', page);
    try {
        await page.click(function () {
            var s = document.querySelector('.bf-languages');
            return s && Array.prototype.find.call(s.querySelectorAll('button'), function (b) { return b.textContent.trim() === 'Change'; });
        }, { what: 'the Change button of My languages', timeout: 4000 });
        await sleep(400);
        const langOpen = await langTips();
        await t.check(langOpen.found && langOpen.tips === 0 && langOpen.described === 0, 'person page: with the My languages editor open there is still no "i" (' + langOpen.tips + ')', page);
        // The page again as it was, so nothing the editor may have marked is left open or unsaved.
        await reloadPersonPage(t, page);
        await openEverything(page, false);
    } catch (e) {
        t.note('the My languages editor could not be opened (' + e.message + '); the "i" check was made on the closed line only');
    }
    const formatBefore = await page.evaluate(pgControl, null, wantOf(PERSON_MENUS[1]), null);
    await tipBehaviour(t, page, 'person page', en['field.Format.info']);
    const formatAfter = await page.evaluate(pgControl, null, wantOf(PERSON_MENUS[1]), null);
    await t.check(!!formatBefore && !!formatAfter && formatBefore.value === formatAfter.value && !(await barOf(page, false)).visible,
        'person page: clicking the "i" changes nothing (the dropdown keeps ' + (formatAfter && formatAfter.value) + ', no save bar)', page);

    // Phone width: the explanation stays inside the window.
    await page.setViewport(375, 812, true);
    await reloadPersonPage(t, page);
    await openEverything(page, false);
    await tipBehaviour(t, page, 'person page at 375x812', en['field.UsesAudioDescription.info']);
    await page.clearViewport();

    // The admin page.
    await openAdminPage(t, page);
    await openEverything(page, true);
    const atips = await page.evaluate(pgInfoTips, ADMIN);
    const wanted = ['page.enabled.info', 'defaults.info', 'apps.mode.info', 'apps.rememberPrePlay.info', 'apps.notListed.info', 'option.correctAfterStart.info', 'option.compatibility.info',
        'template.info', 'field.UnlabelledAudio.info'];
    const missing = wanted.filter((k) => atips.filter((x) => x.tip === ae[k]).length !== 1);
    const small = atips.filter((x) => x.w < 24 || x.h < 24).map((x) => x.name);
    const unnamed = atips.filter((x) => !/^More about \S/.test(x.name || '')).map((x) => JSON.stringify(x.name));
    await t.check(!unnamed.length, 'admin page: every "i" is named "More about <setting>" for screen readers' + (unnamed.length ? ' — not: ' + unnamed.join(', ') : ''), page);
    const far = atips.filter((x) => x.gap === null || x.gap < -1 || x.gap > 16).map((x) => x.name + ' (' + x.gap + ' px)');
    await t.check(!far.length, 'admin page: every "i" sits right after the text it explains (at most 16 px away)' + (far.length ? ' — not: ' + far.join('; ') : ''), page);
    await t.check(!missing.length && !small.length, 'admin page: one "i" each for ' + wanted.join(', ') + ' (' + atips.length + ' found)'
        + (missing.length ? ' — missing or doubled: ' + missing.join(', ') : '') + (small.length ? ' — under 24 px: ' + small.join(', ') : ''), page);
    await tipBehaviour(t, page, 'admin page', ae['apps.mode.info'], ADMIN, ADMIN + ' .bf-lede');
    // Drawing the page again (Restore defaults, a reset or a reload of the data) doesn't add a second "i" to the static header.
    await reloadAdminPage(t, page);
    const again = await page.evaluate(pgInfoTips, ADMIN);
    await t.check(again.filter((x) => x.tip === ae['page.enabled.info']).length === 1, 'admin page: after a reload the Enabled switch still has one "i"', page);
}

// ───────────────────────────── 23. Reset all settings ─────────────────────────────

/** The settings compared between Admin/Settings and Admin/Defaults (what a reset puts back). */
function adminCore(c) {
    const a = c || {};
    return canon({
        Enabled: a.Enabled, ConfigVersion: a.ConfigVersion, Everyday: a.Everyday || {}, Libraries: a.Libraries || [], PerLibraryDefaults: a.PerLibraryDefaults === undefined ? null : a.PerLibraryDefaults,
        AppModes: (a.AppModes || []).map((m) => ({ Client: m.Client, Mode: String(m.Mode), RememberPrePlayChoices: m.RememberPrePlayChoices, SeenOnly: !!m.SeenOnly })), UnknownAppMode: String(a.UnknownAppMode), CorrectAfterStart: String(a.CorrectAfterStart),
        CompatibilityMode: !!a.CompatibilityMode, Keywords: a.Keywords || {}, LanguageAliases: a.LanguageAliases || [], NewUserTemplate: a.NewUserTemplate || null
    });
}

async function testResetAll(t) {

    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    // Something to reset: a library choice and a per-title choice for the person, an Apps change for the admin.
    const mine = withTable(t, kept.snapshot, { perLibrary: true, everyday: ['lang:en', 'off'], libs: { Anime: ['lang:ja', 'lang:en'] } });
    await putMe(t, mine, 'a library choice before the reset');
    const ov = await t.api.put('/Babelfin/Me/Overrides/' + compactId(t.ids.movieId), { AudioChain: [{ Kind: 'Language', Language: 'de' }, { Kind: 'VideoDefault' }] }, { scope: 'Title' });
    const admin0 = await getAdmin(t);
    const changedAdmin = clone(admin0);
    changedAdmin.UnknownAppMode = String(admin0.UnknownAppMode) === 'ReplaceUnlessManual' ? 'Trust' : 'ReplaceUnlessManual';
    await putAdmin(t, changedAdmin, 'an Apps change before the reset');
    const export0 = await t.api.get('/Babelfin/Admin/Export');
    // Whatever happens, everyone's settings come back from that file.
    t.defer('everyone\'s settings restored from the export', async () => {
        if (export0.status === 200 && export0.body) {
            await t.api.post('/Babelfin/Admin/Import', export0.body, { dryRun: 'false' });
        }
    });
    const before = (await getMe(t)).Settings;
    const adminBefore = await getAdmin(t);
    await t.check(ov.status === 200 && (before.Overrides || []).length > 0 && before.PerLibrary === true && export0.status === 200,
        'set up: the person has a per-title and a library choice, the admin an Apps change, and Admin/Export answers (' + ov.status + ', ' + export0.status + ')', null);

    // The button sits at the end of Back up and restore.
    await openAdminPage(t, page);
    await setSection(page, ADMIN, SPEC.backup, true);
    const where = await page.evaluate(function (scope, text) {
        var section = document.querySelector(scope + ' #bfBackup');
        var b = section ? Array.prototype.find.call(section.querySelectorAll('button'), function (x) { return x.textContent.trim() === text; }) : null;
        var all = section ? Array.prototype.slice.call(section.querySelectorAll('button, input')) : [];
        return { found: !!b, last: !!b && all.indexOf(b) === all.length - 1, danger: !!b && /button-delete/.test(b.className) };
    }, ADMIN, SPEC.resetButton);
    await t.check(where.found && where.last && where.danger, 'admin page: "' + SPEC.resetButton + '" is the last control in "' + SPEC.backup + '", styled as a danger button (' + JSON.stringify(where) + ')', page);
    if (!where.found) {
        return;
    }

    // Cancel changes nothing.
    await page.click(pgButton, { args: [ADMIN + ' #bfBackup', SPEC.resetButton], what: 'the "' + SPEC.resetButton + '" button' });
    const dlg = await waitDialog(page, 5000);
    if (dlg) {
        await answerDialog(page, false);
    }
    await sleep(t.settle);
    const afterCancel = (await getMe(t)).Settings;
    await t.check(!!dlg && same(afterCancel, before) && same(adminCore(await getAdmin(t)), adminCore(adminBefore)),
        'admin page: the button asks first (' + (dlg ? '"' + dlg.title + '": ' + dlg.buttons.join(' / ') : 'no dialog') + '), and Cancel changes nothing', page);

    // Reset.
    await page.click(pgButton, { args: [ADMIN + ' #bfBackup', SPEC.resetButton], what: 'the "' + SPEC.resetButton + '" button' });
    const dlg2 = await waitDialog(page, 5000);
    if (dlg2) {
        await answerDialog(page, true);
    }
    let done = null;
    try {
        done = await page.waitFor(function (scope, text) {
            var n = document.querySelector(scope + ' .bf-reset-done');
            return n && n.textContent.indexOf(text) >= 0 ? n.textContent : null;
        }, { timeout: 20000, what: 'the reset message' }, ADMIN, SPEC.resetDone);
    } catch (e) {
        done = null;
    }
    const path0 = done ? String(done).slice(String(done).indexOf(': ') + 2).trim() : '';
    await t.check(!!done && /babelfin-settings-before-reset-\d{8}-\d{6}(-\d+)?\.json$/.test(path0), 'admin page: after OK the page says the settings were reset and where the copy is (' + JSON.stringify(done) + ')', page);
    const me = (await getMe(t)).Settings;
    const adminAfter = await getAdmin(t);
    const shipped = await t.api.get('/Babelfin/Admin/Defaults');
    await t.check((me.Overrides || []).length === 0 && (me.Libraries || []).length === 0 && (me.PerLibrary === null || me.PerLibrary === undefined) && !me.HandleNextEpisode,
        'after the reset the person starts again: no per-title or library choices, no mode (' + (me.Overrides || []).length + ', ' + (me.Libraries || []).length + ', ' + me.PerLibrary + ')', null);
    await t.check(shipped.status === 200 && same(adminCore(adminAfter), adminCore(shipped.body)), 'after the reset Admin/Settings equals Admin/Defaults'
        + (same(adminCore(adminAfter), adminCore(shipped.body)) ? '' : ' (differs: ' + changedPaths(JSON.parse(adminCore(shipped.body)), JSON.parse(adminCore(adminAfter))).join(', ') + ')'), null);
    const peopleRow = await page.evaluate(function (scope, id) {
        var tr = document.querySelector(scope + ' [data-section="people"] tr[data-user="' + id + '"]');
        return tr ? Array.prototype.map.call(tr.querySelectorAll('td'), function (td) { return td.textContent.trim(); }) : null;
    }, ADMIN, compactId(t.ids.userId));
    await t.check(!!peopleRow && peopleRow.slice(0, 2).every((x) => x === SPEC.defaultWord), 'admin page: the People table shows the person on Default again without a reload (' + JSON.stringify(peopleRow) + ')', page);

    // The copy: on the server, the Export format, with the settings from before.
    let file = null;
    try {
        const info = await t.api.get('/System/Info');
        if (info.status !== 200 || !info.body.ProgramDataPath) throw new Error('Cannot resolve the disposable server data folder');
        const filename = path0.match(/babelfin-settings-before-reset-\d{8}-\d{6}(-\d+)?\.json$/);
        if (!filename) throw new Error('Invalid backup filename');
        file = JSON.parse(fs.readFileSync(path.join(info.body.ProgramDataPath, 'data', 'babelfin', 'backups', filename[0]), 'utf8'));
    } catch (e) {
        file = null;
    }
    // The export's people are person documents (camelCase, like the rest of Babelfin's files).
    const people = file ? (file.users || file.Users || []) : [];
    const mineInFile = people.find((u) => compactId(u.userId || u.UserId || '') === compactId(t.ids.userId)) || null;
    const fileOverrides = mineInFile ? (mineInFile.overrides || mineInFile.Overrides || []) : [];
    await t.check(!!file && !!mineInFile && fileOverrides.length === (before.Overrides || []).length && (mineInFile.perLibrary === true || mineInFile.PerLibrary === true),
        'the copy on the server is an export with the person\'s settings from before the reset (' + (file ? Object.keys(file).join(', ') : 'unreadable: ' + path0) + ')', null);

    // Restoring that file puts everything back.
    const restored = file ? await t.api.post('/Babelfin/Admin/Import', file, { dryRun: 'false' }) : { status: 0 };
    const meBack = (await getMe(t)).Settings;
    const adminBack = await getAdmin(t);
    await t.check(restored.status === 200 && (meBack.Overrides || []).length === (before.Overrides || []).length && meBack.PerLibrary === true
        && String(adminBack.UnknownAppMode) === String(adminBefore.UnknownAppMode),
        'restoring the copy puts the person\'s choices and the admin\'s Apps change back (' + restored.status + ')', null);
    const activity = await t.api.get('/Babelfin/Admin/Activity', { count: 50 });
    const texts = ((activity.body && (activity.body.Items || activity.body)) || []).map((x) => x.Text || x.text || '');
    await t.check(texts.some((x) => x === 'An admin reset all Babelfin settings'), 'Activity lists "An admin reset all Babelfin settings"', null);
}

// ───────────────────────────── Shared helpers for "Show and movie overrides" ─────────────────────────────

/**
 * The "Show and movie overrides" section under rootSel as it stands ('#bf-overrides' on the person page, '#bf-group-overrides' in the People
 * editor); inFrame reads it inside the settings frame of #/babelfin. Cells are { text, unset }.
 */
function pgOvSection(rootSel, inFrame) {
    var d = inFrame ? ((document.querySelector('.bfSettingsFrame') || {}).contentDocument || null) : document;
    var s = d ? d.querySelector(rootSel) : null;
    if (!s) { return { found: false }; }
    var txt = function (n) { return n ? (n.textContent || '').replace(/\s+/g, ' ').trim() : null; };
    var btn = s.querySelector('.bfui-disclosure-button');
    var body = s.querySelector('.bfui-disclosure-body');
    var table = s.querySelector('table.bfui-overrides-table');
    var cell = function (tr, cls) {
        var c = tr.querySelector('.bfui-overrides-' + cls);
        return c ? { text: txt(c), unset: c.classList.contains('bfui-overrides-unset'), label: c.getAttribute('data-label') } : null;
    };
    // :scope: the People editor sits inside the People table, whose own tbody would otherwise make the header row of this one match "tbody tr".
    var rows = table ? Array.prototype.map.call(table.querySelectorAll(':scope > tbody > tr'), function (tr) {
        var rm = tr.querySelector('.bfui-overrides-remove');
        return {
            html: tr.getAttribute('data-item') ? undefined : tr.outerHTML.slice(0, 300) + ' in ' + tr.parentNode.tagName + '/' + (tr.closest('table') ? tr.closest('table').className : '-'),
            item: tr.getAttribute('data-item'), scope: tr.getAttribute('data-scope'), title: txt(tr.querySelector('.bfui-overrides-title')), note: txt(tr.querySelector('.bfui-overrides-note')),
            library: cell(tr, 'library'), audio: cell(tr, 'audio'), subtitles: cell(tr, 'subtitles'),
            removeText: txt(rm), removeLabel: rm ? rm.getAttribute('aria-label') : null, removeDisabled: rm ? rm.disabled : null, removeCls: rm ? rm.className : null
        };
    }) : [];
    var all = s.querySelector('.bfui-overrides-remove-all');
    var help = body ? body.querySelector('.bf-overrides-help, .bf-hint, .bfui-help') : null;
    var r = s.getBoundingClientRect();
    return {
        found: true, title: txt(s.querySelector('.bfui-disclosure-title')), expanded: btn ? btn.getAttribute('aria-expanded') : null, bodyHidden: body ? body.hidden : null,
        heading: s.querySelector('.bfui-disclosure-heading') ? s.querySelector('.bfui-disclosure-heading').tagName : null, help: txt(help),
        headers: table ? Array.prototype.map.call(table.querySelectorAll('thead th'), txt) : [], tableLabel: table ? table.getAttribute('aria-label') : null, rows: rows,
        empty: txt(s.querySelector('.bfui-overrides-empty')), removeAll: all ? txt(all) : null, removeAllCls: all ? all.className : null, visible: r.width > 0 && r.height > 0,
        active: d.activeElement ? (d.activeElement.className || d.activeElement.tagName) : null
    };
}

/** The section's heading button, or one row's Remove button (by the row's item id and scope; the first row without), or Remove all. */
function pgOvElement(rootSel, inFrame, what, itemId, scope) {
    var d = inFrame ? ((document.querySelector('.bfSettingsFrame') || {}).contentDocument || null) : document;
    var s = d ? d.querySelector(rootSel) : null;
    if (!s) { return null; }
    if (what === 'heading') { return s.querySelector('.bfui-disclosure-button'); }
    if (what === 'all') { return s.querySelector('.bfui-overrides-remove-all'); }
    var table = s.querySelector('table.bfui-overrides-table');
    var rows = table ? Array.prototype.slice.call(table.querySelectorAll(':scope > tbody > tr')) : [];
    var row = itemId ? rows.find(function (tr) { return tr.getAttribute('data-item') === itemId && (!scope || tr.getAttribute('data-scope') === scope); }) : rows[0];
    return row ? row.querySelector('.bfui-overrides-remove') : null;
}

/** The toasts shown now in the page or in the settings frame. */
function pgToasts(inFrame) {
    var d = inFrame ? ((document.querySelector('.bfSettingsFrame') || {}).contentDocument || null) : document;
    return d ? Array.prototype.map.call(d.querySelectorAll('.bfui-toast, .bf-toast'), function (n) { return n.textContent.trim(); }) : [];
}

async function waitToast(page, text, inFrame, timeout) {
    try {
        await page.waitFor(function (want, frame) {
            var d = frame ? ((document.querySelector('.bfSettingsFrame') || {}).contentDocument || null) : document;
            return !!d && Array.prototype.some.call(d.querySelectorAll('.bfui-toast, .bf-toast'), function (n) { return n.textContent.trim() === want; });
        }, { timeout: timeout || 6000, what: 'the toast "' + text + '"' }, text, !!inFrame);
        return true;
    } catch (e) {
        return false;
    }
}

/** Opens or closes the section (a click on its heading) when it isn't in the wanted state; returns its state. */
async function setOvSection(page, rootSel, inFrame, open) {
    let s = await page.evaluate(pgOvSection, rootSel, !!inFrame);
    if (s.found && (s.expanded === 'true') !== open) {
        await page.click(pgOvElement, { args: [rootSel, !!inFrame, 'heading'], what: 'the heading of "Show and movie overrides"' });
        await sleep(400);
        s = await page.evaluate(pgOvSection, rootSel, !!inFrame);
    }
    return s;
}

/** Whether the save bar of the person page (or of the page in the settings frame) shows. */
function pgSaveBarShown(inFrame) {
    var d = inFrame ? ((document.querySelector('.bfSettingsFrame') || {}).contentDocument || null) : document;
    var b = d ? d.querySelector('.bf-savebar') : null;
    return !!b && !b.hidden && !b.classList.contains('hide') && getComputedStyle(b).display !== 'none' && b.getBoundingClientRect().height > 0;
}

/** Signs the second person in through the API (empty password, as the rig makes them) to act as them; null when that isn't possible. */
async function viewerApi(t) {
    if (t.viewerSession !== undefined) {
        return t.viewerSession;
    }
    t.viewerSession = null;
    try {
        const header = 'MediaBrowser Client="Babelfin E2E", Device="e2e viewer", DeviceId="babelfin-e2e-viewer", Version="1"';
        const r = await fetch(t.base + '/Users/AuthenticateByName', { method: 'POST', headers: { Authorization: header, 'Content-Type': 'application/json', Accept: 'application/json' }, body: JSON.stringify({ Username: t.opts.viewer, Pw: '' }) });
        if (r.status === 200) {
            const body = await r.json();
            t.viewerSession = new Api(t.base, body.AccessToken, body.User && body.User.Id);
        }
    } catch (e) {
        t.viewerSession = null;
    }
    return t.viewerSession;
}

async function endViewerSession(t) {
    if (t.viewerSession) {
        await t.viewerSession.post('/Sessions/Logout').catch(() => {});
    }
    t.viewerSession = undefined;
}

/** Snapshots the second person's settings (the admin's view), with an undo step that puts them back whole; returns the snapshot. */
async function keepViewer(t) {
    const path = '/Babelfin/Admin/Users/' + t.ids.viewerId + '/Settings';
    const snapshot = clone((await t.api.get(path)).body);
    t.defer('second user\'s settings restored', async () => {
        if (t.viewerSession) {
            await t.viewerSession.del(ME_OVERRIDES + '/All');
        }
        await restorePersonSnapshot(t, t.api, path, snapshot, t.opts.viewer + "'s settings put back through the API");
    });
    return snapshot;
}

/**
 * Stores an in-play track change after the first report and startup window. The generic test app has no pre-play-memory
 * opt-in, so a second PlaybackInfo request alone must not create this fixture. Returns the episode's streams.
 */
async function rememberTrackChange(t, api, episodeId) {
    const uid = api.userId;
    const original = await api.get('/Babelfin/Me/Settings');
    if (original.status !== 200 || !original.body.Settings) throw new Error('Cannot read the remembered-change person settings');
    // Neutral installs leave both tracks to Jellyfin; choose both temporarily so in-play changes can be remembered.
    const fixture = withTable(t, original.body.Settings, { perLibrary: false, everyday: ['lang:ja', 'lang:en'] });
    fixture.Enabled = true;
    fixture.Everyday.Memory = 'RestOfShow';
    fixture.Everyday.PrefersSdh = false;
    const setPerson = async doc => {
        const r = await api.put('/Babelfin/Me/Settings', doc, { keepOverrides: 'true' });
        if (r.status !== 200) throw new Error('Remembered-change person settings: ' + r.status);
    };
    let play = null;
    const post = async (route, body) => {
        const r = await api.post(route, body);
        if (r.status !== 200 && r.status !== 204) throw new Error(route + ': ' + r.status);
    };
    try {
        await setPerson(fixture);
        const first = await api.post('/Items/' + compactId(episodeId) + '/PlaybackInfo', { UserId: uid }, { userId: uid });
        const src = first.body && first.body.MediaSources && first.body.MediaSources[0];
        if (first.status !== 200 || !src) throw new Error('Remembered-change PlaybackInfo: ' + first.status);
        const other = (src.MediaStreams || []).find(s => s.Type === 'Audio' && s.Index !== src.DefaultAudioStreamIndex);
        if (!other || !(src.DefaultSubtitleStreamIndex >= 0)) throw new Error('Remembered-change fixture needs another audio track and active subtitles');
        const item = await api.get('/Users/' + compactId(uid) + '/Items/' + compactId(episodeId));
        if (item.status !== 200) throw new Error('Cannot read the remembered-change target: ' + item.status);
        const target = compactId(item.body.SeriesId || episodeId);
        const config = await getAdmin(t);
        play = {
            ItemId: compactId(episodeId), MediaSourceId: src.Id,
            AudioStreamIndex: src.DefaultAudioStreamIndex, SubtitleStreamIndex: src.DefaultSubtitleStreamIndex,
            PlaySessionId: first.body.PlaySessionId, PositionTicks: 0, CanSeek: true, IsPaused: false, PlayMethod: 'DirectPlay'
        };
        await post('/Sessions/Playing', play);
        await post('/Sessions/Playing/Progress', Object.assign({}, play, { EventName: 'TimeUpdate', PositionTicks: 1000000 }));
        await sleep((config.ManualChangeAfterSeconds ?? 5) * 1000 + 1500);
        await post('/Sessions/Playing/Progress', Object.assign({}, play, {
            AudioStreamIndex: other.Index, EventName: 'TimeUpdate', PositionTicks: 2000000
        }));
        // Off in the same packet as an audio switch can be the player's reset.
        // A separate, later report represents the deliberate subtitle-off fixture.
        await sleep(10500);
        await post('/Sessions/Playing/Progress', Object.assign({}, play, {
            AudioStreamIndex: other.Index, SubtitleStreamIndex: -1, EventName: 'TimeUpdate', PositionTicks: 120000000
        }));
        const stored = await waitStored(t, '/Babelfin/Admin/Users/' + compactId(uid) + '/Settings', body => {
            const doc = body.Settings || body;
            return (doc.Overrides || []).some(o => compactId(o.ItemId) === target && o.Manual && o.Manual.Audio && o.Manual.SubtitlesOff);
        });
        if (!stored) throw new Error('The in-play change was not remembered for the show');
        return { audio: other, streams: src.MediaStreams };
    } finally {
        try {
            if (play) await post('/Sessions/Playing/Stopped', Object.assign({}, play, { PositionTicks: 3000000 }));
        } finally {
            await setPerson(completePersonSnapshot(original.body.Settings));
        }
    }
}

// ───────────────────────────── 24. The ⋮ menu entry "Audio and subtitles (Babelfin)" ─────────────────────────────

/** The topmost action sheet as it stands: its rows, the Babelfin entry and how it compares with the sheet's own rows, and where the sheet is. */
function pgSheet() {
    var sheets = Array.prototype.slice.call(document.querySelectorAll('.actionSheet'));
    var s = sheets[sheets.length - 1];
    if (!s) { return null; }
    var r = s.getBoundingClientRect();
    var rows = Array.prototype.slice.call(s.querySelectorAll('.actionSheetMenuItem'));
    var id = function (n) { return String(n.getAttribute('data-id') || '').toLowerCase(); };
    var bf = rows.filter(function (n) { return n.classList.contains('bfSheetItem'); });
    var pl = rows.find(function (n) { return id(n) === 'addtoplaylist'; }) || null;
    var b = bf[0] || null;
    var tmpl = pl || rows.find(function (n) { return !n.classList.contains('bfSheetItem'); }) || null;
    var known = ['actionsheetMenuItemIcon', 'listItemIcon', 'listItemIcon-transparent', 'material-icons'];
    var cls = function (n, drop) { return n ? Array.prototype.filter.call(n.classList, function (c) { return drop.indexOf(c) < 0; }).sort().join(' ') : null; };
    var icon = function (n) { return n ? n.querySelector('.actionsheetMenuItemIcon') : null; };
    var textEl = function (n) { return n ? (n.querySelector('.actionSheetItemText') || n.querySelector('.listItemBodyText')) : null; };
    var iconName = function (n) { return n ? Array.prototype.filter.call(n.classList, function (c) { return known.indexOf(c) < 0; }).join(' ') : null; };
    var bi = icon(b);
    var ti = icon(tmpl);
    var glyph = function (n) { return n ? getComputedStyle(n, '::before').content : null; };
    var box = function (n) { if (!n) { return null; } var x = n.getBoundingClientRect(); return Math.round(x.width) + 'x' + Math.round(x.height); };
    var structure = function (n) { return n ? Array.prototype.map.call(n.children, function (c) { return c.tagName.toLowerCase() + '.' + cls(c, [iconName(c)]) + '>' + Array.prototype.map.call(c.children, function (g) { return g.tagName.toLowerCase() + '.' + cls(g, []); }).join('+'); }).join(' ') : null; };
    return {
        rect: [Math.round(r.left), Math.round(r.top), Math.round(r.right), Math.round(r.bottom)], vw: window.innerWidth, vh: window.innerHeight,
        fullscreen: s.classList.contains('actionsheet-fullscreen'), sheets: sheets.length, scrollable: s.scrollHeight > s.clientHeight + 1,
        ids: rows.map(id), bfCount: bf.length, anyById: document.querySelectorAll('[data-id="babelfin-override"]').length,
        hasPlaylist: !!pl, afterPlaylist: !!pl && !!b && pl.nextElementSibling === b, bfIndex: b ? rows.indexOf(b) : -1,
        bfText: b ? (textEl(b) ? textEl(b).textContent.trim() : b.textContent.trim()) : null, bfTag: b ? b.tagName : null, bfType: b ? b.getAttribute('type') : null, bfIs: b ? b.getAttribute('is') : null,
        bfCls: cls(b, ['bfSheetItem']), tmplCls: cls(tmpl, []), bfId: b ? b.getAttribute('data-id') : null,
        iconKnown: cls(bi, ['translate']), tmplIconKnown: cls(ti, [iconName(ti)]), iconName: iconName(bi), tmplIconName: iconName(ti),
        iconGlyph: glyph(bi), tmplGlyph: glyph(ti), iconBox: box(bi), tmplIconBox: box(ti), iconAria: bi ? bi.getAttribute('aria-hidden') : null, tmplIconAria: ti ? ti.getAttribute('aria-hidden') : null,
        textCls: cls(textEl(b), []), tmplTextCls: cls(textEl(tmpl), []), shape: structure(b), tmplShape: structure(tmpl),
        rowBox: box(b), tmplRowBox: box(tmpl), ids2: b ? b.querySelectorAll('[id]').length + (b.id ? 1 : 0) : 0
    };
}

/**
 * Where the page stands in its history (Navigation API): the current entry's id (the same entry again after a sheet opened and closed; the index alone would shift
 * once Chrome's 50-entry limit drops the oldest), its index and the length of the list, history.length, the URL, and a marker that a reload would lose.
 */
function pgNav() {
    var n = window.navigation;
    return { id: n ? n.currentEntry.id : null, idx: n ? n.currentEntry.index : null, len: n ? n.entries().length : null, hist: history.length, href: location.href, mark: window.__bfMark === 'm1' };
}

async function clickAt(page, x, y, button) {
    const b = button || 'left';
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x, y: y, button: 'none' });
    await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: x, y: y, button: b, buttons: b === 'right' ? 2 : 1, clickCount: 1 });
    await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: x, y: y, button: b, buttons: 0, clickCount: 1 });
}

/** The visible button (or link) named name by its aria-label, its title or its text: icon-only buttons such as Sort and Filter have no text. */
function pgNamedButton(name) {
    return Array.prototype.find.call(document.querySelectorAll('button, a, [role="button"]'), function (b) {
        var r = b.getBoundingClientRect();
        var label = b.getAttribute('aria-label') || b.getAttribute('title') || b.textContent.replace(/\s+/g, ' ').trim();
        return r.width > 0 && r.height > 0 && label === name && !b.closest('.hide');
    }) || null;
}

/** Closes a menu or popup of jellyfin-web with a click on the dimmed area (its Escape is "go back" for the page). */
async function closePopup(page) {
    await clickAt(page, 4, 400);
    await sleep(600);
}

/** Scrolls the first element found by the selector to the middle of the window (nothing happens when there is none). */
async function centerOn(page, selector) {
    await page.evaluate(function (sel) {
        var n = document.querySelector(sel);
        if (n && n.scrollIntoView) { n.scrollIntoView({ block: 'center', inline: 'nearest' }); }
    }, selector);
    await sleep(200);
}

/** A right-click on the middle of a card (or list row) found by the selector (scrolled into view first). */
async function rightClickOn(page, selector) {
    const p = await page.evaluate(function (sel) {
        var c = document.querySelector(sel);
        if (!c) { return null; }
        c.scrollIntoView({ block: 'center' });
        var inner = c.querySelector('.cardBox, .listItemImage, .listItemBody') || c;
        var r = inner.getBoundingClientRect();
        return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    }, selector);
    if (!p) {
        throw new Error('no card to right-click: ' + selector);
    }
    await clickAt(page, p.x, p.y, 'right');
}

/**
 * Waits for the action sheet; with wantRow until it has the Babelfin entry (null when it never does), else a moment longer than the entry
 * needs to appear (so "no entry" is a real answer). Returns the sheet as it stands once it has settled.
 */
async function waitSheet(page, wantRow, timeout) {
    const deadline = Date.now() + (timeout || 8000);
    let got = null;
    for (;;) {
        got = await page.evaluate(pgSheet).catch(() => null);
        if (got && (!wantRow || got.bfCount > 0)) {
            break;
        }
        if (Date.now() > deadline) {
            return wantRow ? null : got;
        }
        await sleep(120);
    }
    await sleep(wantRow ? 700 : 1800);
    return page.evaluate(pgSheet);
}

/** Closes the sheet the way a person does without choosing a row: a click on the dimmed area (jellyfin-web's own close). */
async function closeSheetByBackdrop(page) {
    await clickAt(page, 3, 3);
    try {
        await page.waitFor('!document.querySelector(".actionSheet")', { timeout: 4000, what: 'the sheet to close' });
        return true;
    } catch (e) {
        return false;
    }
}

/** The "same as the sheet's own rows" checks of the entry, for one sheet. */
async function sheetEntryChecks(t, page, where, sh, opts) {
    const o = opts || {};
    await t.check(!!sh && sh.bfCount === 1 && sh.anyById === 1 && sh.bfText === OVERRIDE_WORDS['client.menu.override'] && sh.sheets === 1,
        where + ': the sheet has exactly one "' + OVERRIDE_WORDS['client.menu.override'] + '" row (' + (sh ? sh.bfCount + ' with the class, ' + sh.anyById + ' by data-id, "' + sh.bfText + '"' : 'no entry') + ')', page);
    if (!sh || !sh.bfCount) {
        return;
    }
    await t.check(sh.hasPlaylist && sh.afterPlaylist, where + ': it comes right after "Add to playlist" (row ' + sh.bfIndex + ' of ' + sh.ids.length + ': ' + sh.ids.slice(Math.max(0, sh.bfIndex - 2), sh.bfIndex + 2).join(', ') + ')', page);
    await t.check(sh.bfCls === sh.tmplCls && sh.shape === sh.tmplShape && sh.bfTag === 'BUTTON' && sh.bfType === 'button' && sh.bfIs === 'emby-button' && sh.textCls === sh.tmplTextCls && sh.ids2 === 0,
        where + ': it is built like the sheet\'s own rows: a button with the same classes (' + sh.bfCls + '), the same text element (' + sh.textCls + '), no ids (' + sh.ids2 + ')', page);
    await t.check(sh.iconKnown === sh.tmplIconKnown && sh.iconName === 'translate' && !!sh.iconGlyph && sh.iconGlyph !== 'none' && sh.iconBox === sh.tmplIconBox && sh.iconAria === sh.tmplIconAria,
        where + ': its icon is the Material "translate" icon in the same box as the others (classes ' + sh.iconKnown + ' + ' + sh.iconName + '; the others use ' + sh.tmplIconName + '; ' + sh.iconBox + ' vs ' + sh.tmplIconBox + '; glyph ' + sh.iconGlyph + ')', page);
    await t.check(sh.rowBox.split('x')[1] === sh.tmplRowBox.split('x')[1],
        where + ': it is as high as the other rows (' + sh.rowBox + ' vs ' + sh.tmplRowBox + ')', page);
    if (!o.skipRect) {
        const inside = sh.rect[0] >= 0 && sh.rect[1] >= 0 && sh.rect[2] <= sh.vw && sh.rect[3] <= sh.vh;
        await t.check(inside, where + ': the sheet stays inside the window after the row is added (rect ' + sh.rect.join(',') + ' in ' + sh.vw + 'x' + sh.vh + (sh.fullscreen ? ', full-screen sheet' : '') + (sh.scrollable ? ', scrolls' : '') + ')', page);
    }
}

/**
 * One way of opening an item menu, end to end: open the sheet, check the entry, click it (the sheet closes cleanly and the dialog opens for the
 * right target), then Escape (or Save when s.save). s: { name, open() (opens the sheet), focus (selector that holds the focus after the dialog closes),
 * target: { id (the item the dialog is asked for), name, library, kind, scope (the override's scope) }, save: { audio, subtitles } (saves through the dialog
 * and checks the stored override), native (compare the close with the sheet's own), skipRect }.
 */
async function menuScenario(t, page, s) {
    const where = s.name;
    await page.evaluate(function () { window.__bfMark = 'm1'; });
    const nav0 = await page.evaluate(pgNav);
    // The card (or button) is brought to the middle of the window first, clear of Jellyfin's fixed header.
    const openIt = async () => {
        if (s.focus && !s.noCenter) {
            await centerOn(page, s.focus);
        }
        await s.open();
    };
    let nativeNav = null;
    if (s.native) {
        // jellyfin-web's own close first (a click on the dimmed area): where the page stands after that is what a clean close leaves.
        await openIt();
        const plain = await waitSheet(page, false, 8000);
        const closedNative = await closeSheetByBackdrop(page);
        await sleep(400);
        nativeNav = await page.evaluate(pgNav);
        await t.check(!!plain && closedNative && nativeNav.id === nav0.id && nativeNav.href === nav0.href,
            where + ': (reference) jellyfin-web\'s own close of its sheet leaves the history entry as it was (entry index ' + nav0.idx + ' → ' + nativeNav.idx + ', ' + nativeNav.len + ' entries)', page);
    }
    const err0 = errorMark(page);
    const req0 = page.requests.length;
    await openIt();
    const sh = await waitSheet(page, true, 9000);
    await sheetEntryChecks(t, page, where, sh, { skipRect: s.skipRect });
    if (!sh || !sh.bfCount) {
        await closeSheetByBackdrop(page);
        return null;
    }
    // The click: the row is jellyfin-web's own kind, but it must not reach jellyfin-web's handler (it would reject a promise).
    await page.click('.actionSheet .bfSheetItem', { what: 'the "' + OVERRIDE_WORDS['client.menu.override'] + '" row' });
    const dlg = await waitBfDialog(page, 20000).catch(() => null);
    const sheetGone = await page.evaluate('!document.querySelector(".actionSheet") && !document.querySelector(".dialogContainer .actionSheet")');
    const nav1 = await page.evaluate(pgNav);
    // The sheet's own entry may stay as one forward entry (as after jellyfin-web's own close); with a full list (Chrome keeps 50) it replaces the oldest.
    const bound = nativeNav ? nativeNav.len : nav0.len + 1;
    await t.check(!!dlg && sheetGone, where + ': clicking it closes the sheet and opens the dialog' + (dlg ? ' ("' + dlg.title + '")' : ' — no dialog'), page);
    // history.length keeps the one forward entry the sheet's own close leaves too: as long after Babelfin's close as after jellyfin-web's own (else at most one more than before).
    await t.check(nav1.id === nav0.id && nav1.href === nav0.href && nav1.mark && nav1.len <= bound && (!nativeNav || (nav1.len === nativeNav.len && nav1.hist === nativeNav.hist)) && nav1.hist <= nav0.hist + 1,
        where + ': the sheet left no stray history entry and nothing navigated (the page stands on the same history entry: ' + (nav1.id === nav0.id) + '; entry index ' + nav0.idx + ' → ' + nav1.idx + ', history.length ' + nav0.hist + ' → ' + nav1.hist + (nativeNav ? ' (as after jellyfin-web\'s own close: ' + nativeNav.hist + ')' : '') + ', ' + nav1.len + ' entries' + (nativeNav ? '' : ', at most ' + bound) + '; ' + (nav1.href === nav0.href ? 'same URL' : 'URL ' + nav1.href) + '; page not reloaded ' + nav1.mark + ')', page);
    const fresh = errorsSince(page, err0);
    await t.check(fresh.length === 0, where + ': no console error and no unhandled rejection from the click (' + fresh.length + (fresh.length ? ': ' + fresh.join(' | ') : '') + ')', page);
    if (!dlg) {
        return sh;
    }
    const asked = page.requests.slice(req0).filter((r) => r.method === 'GET' && /\/Babelfin\/Me\/Overrides\/Target\/[0-9a-f]{32}/.test(r.url)).map((r) => r.url.match(/Target\/([0-9a-f]{32})/)[1]);
    await t.check(asked.length === 1 && asked[0] === compactId(s.target.id) && dlg.target === s.target.name && apos(dlg.subtitle) === s.target.name + ' · ' + s.target.library
        && apos(dlg.explains[0] || '') === overrideWord('dialog.applies.' + s.target.kind, { library: s.target.library }),
        where + ': the dialog is for ' + (s.target.kind === 'movie' ? 'the movie itself' : 'the show') + ': it asked GET Me/Overrides/Target for the item (' + asked.join(',') + ') and shows "' + dlg.subtitle + '" / "' + (dlg.explains[0] || '') + '"', page);
    if (s.save) {
        const a = await pickInBfDialog(page, 'Audio', s.save.audio);
        const b = await pickInBfDialog(page, 'Subtitles', s.save.subtitles);
        await page.click(pgBfDialogButton, { args: ['Save'], what: 'Save in the dialog' });
        const closed = await waitNoBfDialog(page, 10000);
        const target = (await t.api.get(ME_OVERRIDES + '/Target/' + compactId(s.target.id))).body || {};
        const ov = target.Override;
        await t.check(a.ok && b.ok && closed && !!ov && ov.Scope === s.target.scope && compactId(target.TargetId) === compactId(s.target.targetId || s.target.id)
            && same(tableOf(ov.Settings), storedFor(s.save.audio, s.save.subtitles)),
            where + ': saving from the dialog stored the override for ' + (s.target.kind === 'movie' ? 'the movie' : 'the show') + ' (scope ' + (ov && ov.Scope) + ', ' + (ov ? canon(tableOf(ov.Settings)) : 'nothing stored') + ')', page);
        await deleteOverride(t, s.target.targetId || s.target.id, s.target.scope);
        return sh;
    }
    const esc = await escapeBfDialog(page, s.focus);
    await t.check(esc.closed && esc.focus.ok, where + ': Escape closes the dialog and focus goes back (' + (esc.closed ? 'closed' : 'still open') + ', focus on ' + esc.focus.active + ')', page);
    return sh;
}

/** Marks the first episode played (Next Up then offers the second) for a test; the undo puts the old state back. */
async function playedFirstEpisode(t) {
    const uid = t.api.userId;
    const response = await t.api.get('/Shows/' + t.ids.seriesId + '/Episodes', { userId: uid });
    if (response.status !== 200) { throw new Error('Cannot read the episode fixture: ' + response.status); }
    const episodes = (response.body && response.body.Items) || [];
    const snapshot = episodes.map((episode) => ({ id: episode.Id, data: clone(episode.UserData || {}) }));
    const update = async (id, data) => {
        const result = await t.api.post('/UserItems/' + id + '/UserData', data, { userId: uid });
        if (result.status !== 200) { throw new Error('Cannot update episode watch state: ' + result.status); }
        return result.body;
    };
    t.defer('episode watched and resume states restored', async () => {
        for (const old of snapshot) { await update(old.id, old.data); }
        const restored = await t.api.get('/Shows/' + t.ids.seriesId + '/Episodes', { userId: uid });
        for (const old of snapshot) {
            const actual = ((restored.body && restored.body.Items) || []).find((episode) => compactId(episode.Id) === compactId(old.id));
            if (!actual || !!actual.UserData.Played !== !!old.data.Played
                || (actual.UserData.PlaybackPositionTicks || 0) !== (old.data.PlaybackPositionTicks || 0)
                || (actual.UserData.PlayCount || 0) !== (old.data.PlayCount || 0)) {
                throw new Error('Episode watch/resume fixture was not restored: ' + old.id);
            }
        }
    });
    for (const old of snapshot) {
        const first = compactId(old.id) === compactId(t.ids.episodeId);
        await update(old.id, Object.assign({}, old.data, {
            Played: first, PlaybackPositionTicks: 0, PlayCount: first ? Math.max(1, old.data.PlayCount || 0) : 0,
            LastPlayedDate: first ? new Date().toISOString() : old.data.LastPlayedDate
        }));
    }
    const next = await t.api.get('/Shows/NextUp', { userId: uid, seriesId: t.ids.seriesId, limit: 1 });
    const item = next.body && next.body.Items && next.body.Items[0];
    if (next.status !== 200 || !item || compactId(item.Id) !== compactId(t.ids.episode2Id)) {
        throw new Error('Next Up fixture must offer episode 2 before checking its card (got ' + (item && item.IndexNumber) + ')');
    }
}

async function waitSelector(page, sel, timeout) {
    try {
        await page.waitForSelector(sel, { timeout: timeout || 20000 });
        return true;
    } catch (e) {
        return false;
    }
}

/** The in-dialog pick list of the TV layout (the dropdowns can't be driven by a remote): its title, items, groups, and what has the focus. */
function pgPicker() {
    var d = document.querySelector('.bfDialog');
    var p = d ? d.querySelector('.bfPicker') : null;
    if (!p) { return null; }
    var txt = function (n) { return n ? n.textContent.trim() : null; };
    var items = Array.prototype.slice.call(p.querySelectorAll('.bfPickerItem'));
    var cur = items.find(function (b) { return b.getAttribute('aria-pressed') === 'true'; }) || null;
    var a = document.activeElement;
    return {
        title: txt(p.querySelector('.bfPickerTitle')), items: items.map(txt), headings: Array.prototype.map.call(p.querySelectorAll('.bfPickerHeading'), txt),
        current: txt(cur), focusOnCurrent: !!cur && a === cur, back: txt(p.querySelector('.bfActions button')), picking: d.classList.contains('bfPicking'),
        dropdownsShown: Array.prototype.some.call(d.querySelectorAll(':scope > .bfField'), function (f) { var r = f.getBoundingClientRect(); return r.width > 0 && r.height > 0; })
    };
}

/** TV layout: Enter on each dropdown opens the list, a pick sets the dropdown, Escape and Back return to the dialog, Save stores the choice. */
async function tvDialogChecks(t, page, where, target, expectOptions) {
    const asPicked = [['Audio', 'German', 'lang:de'], ['Subtitles', 'Off', 'off']];
    for (const kind of ['Audio', 'Subtitles']) {
        await page.evaluate(function (label) { var s = document.querySelector('.bfDialog select[aria-label="' + label + '"]'); s.focus(); }, kind);
        await page.press('Enter');
        await sleep(500);
        const pk = await page.evaluate(pgPicker);
        const want = expectOptions[kind];
        await t.check(!!pk && pk.picking && pk.title === kind && same(pk.items, want.map((o) => o.l)) && pk.focusOnCurrent && !pk.dropdownsShown && pk.back === 'Cancel',
            where + ': Enter on the ' + kind + ' dropdown opens the in-dialog list (' + (pk ? pk.items.length + ' choices, "' + pk.current + '" has the focus, ' + pk.headings.join(' / ') : 'no list') + ')', page);
        if (kind === 'Subtitles') {
            // Escape goes back to the dialog, not out of it.
            await page.press('Escape');
            await sleep(400);
            const stillDlg = await page.evaluate('!!document.querySelector(".bfDialog") && !document.querySelector(".bfDialog .bfPicker")');
            await t.check(stillDlg, where + ': Escape in the list goes back to the dialog, which stays open', page);
            await page.evaluate(function (label) { var s = document.querySelector('.bfDialog select[aria-label="' + label + '"]'); s.focus(); }, kind);
            await page.press('Enter');
            await sleep(500);
        }
        const pickText = kind === 'Audio' ? asPicked[0][1] : asPicked[1][1];
        const focused = await page.evaluate(function (text) {
            var b = Array.prototype.find.call(document.querySelectorAll('.bfDialog .bfPickerItem'), function (x) { return x.textContent.trim() === text; });
            if (!b) { return false; }
            b.focus();
            return document.activeElement === b;
        }, pickText);
        await page.press('Enter');
        await sleep(500);
        const after = await page.evaluate(function (label) {
            var d = document.querySelector('.bfDialog');
            var s = d && d.querySelector('select[aria-label="' + label + '"]');
            return { open: !!(d && d.querySelector('.bfPicker')), value: s ? s.value : null, focusOnSelect: !!s && document.activeElement === s };
        }, kind);
        const wantValue = kind === 'Audio' ? asPicked[0][2] : asPicked[1][2];
        await t.check(focused && !after.open && after.value === wantValue && after.focusOnSelect, where + ': Enter on "' + pickText + '" in the list sets the ' + kind + ' dropdown to ' + wantValue + ' and returns to it (' + JSON.stringify(after) + ')', page);
    }
    await page.evaluate(function () {
        var d = document.querySelector('.bfDialog');
        var b = Array.prototype.find.call(d.querySelectorAll(':scope > .bfActions button'), function (x) { return x.textContent.trim() === 'Save'; });
        b.focus();
    });
    await page.press('Enter');
    const closed = await waitNoBfDialog(page, 10000);
    const stored = ((await t.api.get(ME_OVERRIDES + '/Target/' + compactId(target.id))).body || {}).Override;
    await t.check(closed && !!stored && same(tableOf(stored.Settings), storedFor('lang:de', 'off')), where + ': Save (Enter on the button) stored the choices picked from the lists (' + (stored ? canon(tableOf(stored.Settings)) : 'nothing stored') + ')', page);
    await deleteOverride(t, target.id, target.scope);
}

async function testMenuEntry(t) {
    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    const layout = await currentLayout(page);
    t.defer('layout back to "' + layout + '"', () => setLayout(t, layout));
    t.defer('viewport back to desktop', () => page.clearViewport());
    await setLayout(t, '');
    // Nothing chosen anywhere, so the dialog of any title is the plain one; the first episode played, so Next Up offers the second.
    await putMe(t, withTable(t, kept.snapshot, { perLibrary: null }), 'nothing chosen');
    await t.api.del(ME_OVERRIDES + '/All');
    await playedFirstEpisode(t);
    const ids = t.ids;
    const movieT = { id: ids.movieId, name: 'Berlin Test (2019)', library: 'Movies', kind: 'movie', scope: 'Title' };
    const showOf = (id) => ({ id: id, targetId: ids.seriesId, name: 'Astercoil Test', library: 'Anime', kind: 'show', scope: 'Series' });
    const card = (type, id) => HOME_PAGE + ' .card[data-type="' + type + '"][data-id="' + compactId(id) + '"]';
    const dcard = (type, id) => DETAIL_PAGE + ' .card[data-type="' + type + '"]' + (id ? '[data-id="' + compactId(id) + '"]' : '');
    const gotoHome = async () => {
        await page.navigate(t.url('/web/#/home'));
        await page.waitForSelector(HOME_PAGE + ' .card[data-type="Movie"]', { timeout: 30000 });
        await sleep(600);
    };
    const gotoItem = async (id) => {
        await page.navigate(t.url('/web/#/details?id=' + compactId(id)));
        await page.waitForSelector(DETAIL_PAGE + ' .btnMoreCommands', { timeout: 30000 });
        await sleep(800);
    };
    const menuBtn = (cardSel) => cardSel + ' button[data-action="menu"]';

    // 24a. Movie card on the home page: the full path, saving from the dialog, and jellyfin-web's own close as the reference.
    await gotoHome();
    await menuScenario(t, page, { name: 'movie card', native: true, open: () => page.click(menuBtn(card('Movie', ids.movieId))), focus: menuBtn(card('Movie', ids.movieId)), target: movieT, save: { audio: 'lang:de', subtitles: 'off' } });
    // 24b. Use the actual Anime library for a Series card. Native Latest Media can legitimately
    // represent the sample show by a single Episode when HidePlayedInLatest is on, even with GroupItems=true.
    await page.click(function (id) {
        const h = window.__bfE2E;
        return Array.from(document.querySelectorAll('a[href]')).find(a => h.vis(a) && a.getAttribute('href').toLowerCase().includes(id));
    }, { args: [compactId(ids.libraries.Anime)], what: 'the real Anime library link' });
    // A visible tab or cached Series card can still belong to the preceding Home page.
    // Commit the requested library route before reading its tabs or taking a history baseline.
    await page.waitFor(function (libraryId) {
        const parts = location.hash.split('?');
        const query = new URLSearchParams(parts[1] || '');
        if (parts[0] !== '#/tv' || query.get('topParentId') !== libraryId || query.get('collectionType') !== 'tvshows') { return false; }
        const h = window.__bfE2E;
        return Array.from(document.querySelectorAll('[data-role="page"]:not(.hide) .card[data-type="Series"], [role="tab"], .emby-tab-button')).some(h.vis);
    }, { timeout: 30000, what: 'the committed Anime library route and its page' }, compactId(ids.libraries.Anime));
    const showsTab = function () {
        const h = window.__bfE2E;
        return Array.from(document.querySelectorAll('[role="tab"], .emby-tab-button')).find(e => h.vis(e) && e.textContent.trim() === 'Shows');
    };
    if (await page.evaluate(showsTab)) await page.click(showsTab, { what: 'the Anime library Shows tab' });
    const seriesMenu = '[data-role="page"]:not(.hide) .card[data-type="Series"][data-id="' + compactId(ids.seriesId) + '"] button[data-action="menu"]';
    await page.waitForSelector(seriesMenu, { timeout: 30000 });
    t.note('Series-card fixture: the actual Anime library, independent of the native Home Latest Media item type; all menu/history/dialog assertions retained');
    await menuScenario(t, page, { name: 'series card (Anime library)', open: () => page.click(seriesMenu), focus: seriesMenu, target: showOf(ids.seriesId) });
    await menuScenario(t, page, { name: 'series card (Anime library), Save', open: () => page.click(seriesMenu), focus: seriesMenu, target: showOf(ids.seriesId), save: { audio: 'lang:ja', subtitles: 'lang:en' } });
    // 24c. An episode card (Next Up on the show's page) stands for its show, and saving stores the show's override.
    await gotoItem(ids.seriesId);
    const haveNext = await waitSelector(page, dcard('Episode', ids.episode2Id), 20000);
    await t.check(haveNext, 'the series page shows a Next Up episode card (the second episode, the first being played)', page);
    if (haveNext) {
        await menuScenario(t, page, { name: 'episode card (Next Up)', open: () => page.click(menuBtn(dcard('Episode', ids.episode2Id))), focus: menuBtn(dcard('Episode', ids.episode2Id)), target: showOf(ids.episode2Id), save: { audio: 'lang:ja', subtitles: 'lang:en' } });
    }
    // 24d. A season card stands for its show too.
    if (await waitSelector(page, dcard('Season', ids.seasonId), 5000)) {
        await menuScenario(t, page, { name: 'season card', open: () => page.click(menuBtn(dcard('Season', ids.seasonId))), focus: menuBtn(dcard('Season', ids.seasonId)), target: showOf(ids.seasonId) });
    } else {
        await t.check(false, 'the series page shows a season card', page);
    }
    // 24e. A list row of the season page (an episode).
    await gotoItem(ids.seasonId);
    const rowSel = DETAIL_PAGE + ' .listItem[data-type="Episode"][data-id="' + compactId(ids.episodeId) + '"]';
    const rowBtn = rowSel + ' [data-action="menu"], ' + rowSel + ' .btnCardOptions';
    if (await waitSelector(page, rowSel, 20000)) {
        await menuScenario(t, page, { name: 'episode list row', open: () => page.click(rowBtn), focus: rowSel, target: showOf(ids.episodeId) });
    } else {
        await t.check(false, 'the season page lists the episode', page);
    }
    // 24f. The item page's more button: season, episode, series, movie.
    for (const item of [[ids.seasonId, 'season page', showOf(ids.seasonId)], [ids.episodeId, 'episode page', showOf(ids.episodeId)], [ids.seriesId, 'series page', showOf(ids.seriesId)], [ids.movieId, 'movie page', movieT]]) {
        await gotoItem(item[0]);
        await menuScenario(t, page, { name: item[1] + ' "More" button', open: () => page.click(DETAIL_PAGE + ' .btnMoreCommands'), focus: DETAIL_PAGE + ' .btnMoreCommands', target: Object.assign({}, item[2], { id: item[0] }) });
    }
    // 24g. A right-click on a card (long-press is the same event): movie card on the home page, episode card on the series page.
    await gotoHome();
    await menuScenario(t, page, { name: 'right-click on a movie card', open: () => rightClickOn(page, card('Movie', ids.movieId)), focus: card('Movie', ids.movieId), target: movieT });
    await gotoItem(ids.seriesId);
    if (await waitSelector(page, dcard('Episode', ids.episode2Id), 20000)) {
        await menuScenario(t, page, { name: 'right-click on an episode card', open: () => rightClickOn(page, dcard('Episode', ids.episode2Id)), focus: dcard('Episode', ids.episode2Id), target: showOf(ids.episode2Id) });
    }

    // 24h. Sheets and menus that are not an item's get nothing.
    await gotoHome();
    await page.click(menuBtn(HOME_PAGE + ' .card[data-type="CollectionFolder"]'), { what: 'the ⋮ of a library tile' });
    const libSheet = await waitSheet(page, false, 8000);
    await t.check(!!libSheet && libSheet.ids.indexOf('addtoplaylist') >= 0 && libSheet.bfCount === 0 && libSheet.anyById === 0,
        'the ⋮ sheet of a library tile (not a show or a movie, though it has "Add to playlist") gets no entry (' + (libSheet ? libSheet.ids.join(', ') : 'no sheet') + ')', page);
    await closeSheetByBackdrop(page);
    const libs = ids.libraries;
    await page.navigate(t.url('/web/#/movies?topParentId=' + libs.Movies));
    await page.waitForSelector('#libraryPage:not(.hide) .card[data-type="Movie"], .libraryPage:not(.hide) .card[data-type="Movie"]', { timeout: 30000 }).catch(() => {});
    await sleep(1200);
    // A card's sheet, closed; then the Sort, Filter and View settings menus right after it (inside the 3 s a noted click counts for).
    const libCard = '.card[data-type="Movie"][data-id="' + compactId(ids.movieId) + '"]';
    const visibleCard = await page.evaluate(function (sel) {
        return Array.prototype.filter.call(document.querySelectorAll(sel + ' button[data-action="menu"]'), function (b) { var r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0; }).length;
    }, libCard);
    if (visibleCard) {
        await menuScenario(t, page, { name: 'movie card on the Movies library page', open: () => page.click(libCard + ' button[data-action="menu"]'), focus: libCard + ' button[data-action="menu"]', target: movieT });
        await page.click(libCard + ' button[data-action="menu"]', { what: 'the ⋮ of the movie card' });
        await waitSheet(page, true, 8000);
        await closeSheetByBackdrop(page);
    } else {
        t.note('the Movies library page showed no movie card with a ⋮ button; its card menu is not checked');
    }
    for (const label of ['Sort', 'Filter', 'View settings']) {
        let opened = true;
        try {
            await page.click(pgNamedButton, { args: [label], what: 'the "' + label + '" button', timeout: 4000 });
        } catch (e) {
            opened = false;
        }
        await sleep(1500);
        const state = await page.evaluate(function () {
            var visible = function (n) { var r = n.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
            var menu = Array.prototype.find.call(document.querySelectorAll('[role="menu"], .MuiMenu-list, .MuiPopover-root, .dialog, [role="dialog"]'), visible) || null;
            return {
                sheet: !!document.querySelector('.actionSheet'), entries: document.querySelectorAll('.bfSheetItem, [data-id="babelfin-override"]').length,
                text: Array.prototype.some.call(document.querySelectorAll('[role="menuitem"], [role="menu"] *, .MuiPopover-root *, .actionSheetMenuItem, .dialog *'), function (n) { return n.children.length === 0 && /\(Babelfin\)/.test(n.textContent); }),
                menu: menu ? menu.className.toString().slice(0, 40) : null
            };
        });
        await t.check(opened && state.entries === 0 && !state.text, 'the "' + label + '" menu gets no entry (' + (opened ? (state.sheet ? 'an action sheet' : (state.menu || 'a popup')) + ' showed, ' + state.entries + ' Babelfin rows' : 'no such button on the page') + ')', page);
        await closePopup(page);
    }
    await gotoHome();
    await page.click('button[aria-controls="app-user-menu"]', { what: 'the avatar button' });
    await page.waitForSelector('#app-user-menu .bfMenuItem', { timeout: 10000 });
    await sleep(1500);
    const userMenu = await page.evaluate(function () {
        var m = document.getElementById('app-user-menu');
        return { items: Array.prototype.map.call(m.querySelectorAll('[role="menuitem"]'), function (n) { return n.textContent.trim(); }), sheet: !!document.querySelector('.actionSheet'), entries: document.querySelectorAll('.bfSheetItem, [data-id="babelfin-override"]').length };
    });
    await t.check(userMenu.items.indexOf(OVERRIDE_WORDS['client.menu.override']) < 0 && userMenu.entries === 0 && !userMenu.sheet && userMenu.items.indexOf('Babelfin Settings') >= 0,
        'the user menu gets no "' + OVERRIDE_WORDS['client.menu.override'] + '" (its own "Babelfin Settings" is there: ' + userMenu.items.join(' | ') + ')', page);
    await closePopup(page);

    // 24i. A sheet near the right edge and the bottom of a small window: the row makes it taller, but it is moved back inside.
    await page.setViewport(460, 620, false);
    await gotoHome();
    const tight = await page.evaluate(function (sel) {
        var cards = Array.prototype.slice.call(document.querySelectorAll(sel));
        var best = null;
        cards.forEach(function (c) { var r = c.getBoundingClientRect(); if (r.width > 0 && (!best || r.right > best.right)) { best = { right: r.right, id: c.getAttribute('data-id') }; } });
        return best;
    }, HOME_PAGE + ' .card[data-type="Movie"]');
    if (tight) {
        const cardSel = HOME_PAGE + ' .card[data-type="Movie"][data-id="' + tight.id + '"]';
        const btn = await page.evaluate(function (sel) { var b = document.querySelector(sel + ' button[data-action="menu"]'); b.scrollIntoView({ block: 'end' }); var r = b.getBoundingClientRect(); return { right: Math.round(r.right), bottom: Math.round(r.bottom), vw: innerWidth, vh: innerHeight }; }, cardSel);
        t.verbose('tight window: the ⋮ button ends at ' + btn.right + ',' + btn.bottom + ' in ' + btn.vw + 'x' + btn.vh);
        const names = {};
        names[compactId(ids.movieId)] = 'Berlin Test (2019)';
        names[compactId(ids.harbourId)] = 'Harbour Test (2002)';
        await menuScenario(t, page, { name: 'movie card in the right-most column near the bottom of a 460x620 window', open: () => page.click(menuBtn(cardSel)), focus: menuBtn(cardSel), noCenter: true,
            target: { id: tight.id, name: names[tight.id], library: 'Movies', kind: 'movie', scope: 'Title' } });
    } else {
        t.note('no movie card in view in the 460x620 window');
    }
    await page.clearViewport();

    // 24j. The other layouts.
    await legacyLayoutScenarios(t, page, movieT);
    await tvLayoutScenarios(t, page, movieT, showOf);
}

/** Switches jellyfin-web's layout and shows the home page with its movie cards (the reload of a layout change can land on the page it was made from). */
async function homeInLayout(t, page, mode) {
    await setLayout(t, mode);
    await page.navigate(t.url('/web/#/home'));
    await page.waitForSelector(HOME_PAGE + ' .card[data-type="Movie"]', { timeout: 30000 });
    await sleep(800);
}

/** desktop-legacy has the ⋮ on cards, mobile-legacy only right-click and the item page's button: both get the entry. */
async function legacyLayoutScenarios(t, page, movieT) {
    const ids = t.ids;
    for (const mode of ['desktop-legacy', 'mobile-legacy']) {
        await homeInLayout(t, page, mode);
        const cardSel = HOME_PAGE + ' .card[data-type="Movie"][data-id="' + compactId(ids.movieId) + '"]';
        const btnSel = cardSel + ' button[data-action="menu"]';
        const hasBtn = await page.evaluate(function (sel) { var b = document.querySelector(sel); return !!b && b.getBoundingClientRect().width > 0; }, btnSel);
        if (hasBtn) {
            await menuScenario(t, page, { name: mode + ': ⋮ on a movie card', open: () => page.click(btnSel), focus: btnSel, target: movieT });
        } else {
            t.note(mode + ': cards have no ⋮ button here; the entry is checked through right-click and the item page');
        }
        await menuScenario(t, page, { name: mode + ': right-click on a movie card', open: () => rightClickOn(page, cardSel), focus: cardSel, target: movieT });
        await page.navigate(t.url('/web/#/details?id=' + compactId(ids.movieId)));
        await page.waitForSelector(DETAIL_PAGE + ' .btnMoreCommands', { timeout: 30000 });
        await sleep(800);
        await menuScenario(t, page, { name: mode + ': movie page "More" button', open: () => page.click(DETAIL_PAGE + ' .btnMoreCommands'), focus: DETAIL_PAGE + ' .btnMoreCommands', target: movieT });
    }
}

/** TV layout: a full-screen sheet gets the entry too, and the dialog's choices are picked from lists with Enter. */
async function tvLayoutScenarios(t, page, movieT, showOf) {
    const ids = t.ids;
    await homeInLayout(t, page, 'tv');
    const cardSel = HOME_PAGE + ' .card[data-type="Movie"][data-id="' + compactId(ids.movieId) + '"]';
    await menuScenario(t, page, { name: 'tv: right-click on a movie card (a full-screen sheet)', open: () => rightClickOn(page, cardSel), focus: cardSel, target: movieT });
    // The remote's menu key: jellyfin-web turns it into its "menu" command on the focused card.
    await menuScenario(t, page, {
        name: 'tv: the remote\'s menu key (ContextMenu) on a focused movie card',
        open: async () => {
            await page.evaluate(function (sel) { var c = document.querySelector(sel); c.focus(); }, cardSel);
            await page.press('ContextMenu');
        },
        focus: cardSel, target: movieT
    });
    await page.navigate(t.url('/web/#/details?id=' + compactId(ids.movieId)));
    await page.waitForSelector(DETAIL_PAGE + ' .btnMoreCommands', { timeout: 30000 });
    await sleep(800);
    await menuScenario(t, page, { name: 'tv: movie page "More" button', open: () => page.click(DETAIL_PAGE + ' .btnMoreCommands'), focus: DETAIL_PAGE + ' .btnMoreCommands', target: movieT });
    // The dialog: open it from the sheet row with the keyboard (Enter on the focused row), then use the lists.
    await page.click(DETAIL_PAGE + ' .btnMoreCommands');
    const sh = await waitSheet(page, true, 9000);
    let opened = null;
    if (sh && sh.bfCount) {
        await page.evaluate(function () { var b = document.querySelector('.actionSheet .bfSheetItem'); b.focus(); });
        await page.press('Enter');
        opened = await waitBfDialog(page, 20000).catch(() => null);
    }
    await t.check(!!opened && opened.title === 'Audio and subtitles', 'tv: Enter on the focused "' + OVERRIDE_WORDS['client.menu.override'] + '" row opens the dialog', page);
    if (opened) {
        const options = {};
        opened.fields.forEach((f) => { options[f.label] = f.options; });
        await tvDialogChecks(t, page, 'tv', movieT, options);
    }
    // The episode page's ⋮ entry opens the same whole-show dialog and supports the TV pick lists.
    const d2 = await openItemMenuDialog(t, page, ids.episodeId);
    await t.check(!!d2 && d2.target === 'Astercoil Test', 'tv: the episode page ⋮ entry opens the dialog for its show (' + (d2 && d2.target) + ')', page);
    if (d2) {
        const options = {};
        d2.fields.forEach((f) => { options[f.label] = f.options; });
        await tvDialogChecks(t, page, 'tv (show)', showOf(ids.episodeId), options);
    }
}

// ───────────────────────────── 25. Show and movie overrides on both pages ─────────────────────────────

/** The confirmation dialog showing in the page (or in the settings frame): title, message and buttons; null when there is none. */
function pgDlg(inFrame) {
    var d = inFrame ? ((document.querySelector('.bfSettingsFrame') || {}).contentDocument || null) : document;
    if (!d) { return null; }
    var boxes = Array.prototype.filter.call(d.querySelectorAll('[role="dialog"], [role="alertdialog"]'), function (b) { return b.getBoundingClientRect().width > 0 && !b.classList.contains('bfDialog'); });
    var box = boxes[boxes.length - 1];
    if (!box) { return null; }
    var txt = function (n) { return n ? n.textContent.replace(/\s+/g, ' ').trim() : ''; };
    return {
        role: box.getAttribute('role'), title: txt(box.querySelector('.bfui-dialog-title, h2')), message: txt(box.querySelector('.bfui-dialog-message')),
        buttons: Array.prototype.filter.call(box.querySelectorAll('button'), function (b) { return b.getBoundingClientRect().width > 0; }).map(function (b) { return { text: txt(b), danger: /bfui-btn-danger/.test(b.className) }; })
    };
}

function pgDlgButton(inFrame, text) {
    var d = inFrame ? ((document.querySelector('.bfSettingsFrame') || {}).contentDocument || null) : document;
    var boxes = d ? Array.prototype.filter.call(d.querySelectorAll('[role="dialog"], [role="alertdialog"]'), function (b) { return b.getBoundingClientRect().width > 0 && !b.classList.contains('bfDialog'); }) : [];
    var box = boxes[boxes.length - 1];
    return box ? (Array.prototype.find.call(box.querySelectorAll('button'), function (b) { return b.textContent.trim() === text; }) || null) : null;
}

async function waitDlg(page, inFrame, timeout) {
    try {
        await page.waitFor(pgDlg, { timeout: timeout || 5000, what: 'the confirmation dialog' }, !!inFrame);
        return await page.evaluate(pgDlg, !!inFrame);
    } catch (e) {
        return null;
    }
}

/** Waits until the dialog is gone. */
async function waitNoDlg(page, inFrame, timeout) {
    try {
        await page.waitFor('!(' + pgDlg.toString() + ')(' + (inFrame ? 'true' : 'false') + ')', { timeout: timeout || 5000, what: 'the confirmation dialog to close' });
        return true;
    } catch (e) {
        return false;
    }
}

/**
 * What the section lists for the rows test 25 stores, in the order it sorts them (library, then name): the test media's names and years are the rig's.
 * words: the audio/subtitle words of the two remembered track changes ({ dualAudioSubs, signsDefaultAudio, signsDefaultSubs }).
 */
function expectedOverrideRows(t, words, seasonWord) {
    const notSet = overrideWord('overrides.notSet');
    const id = (x) => compactId(x);
    return [
        { key: 'dualAudio', id: id(t.ids.seriesId), scope: 'Series', title: 'Astercoil Test (2041)', library: 'Anime', audio: ['Video default', false], subtitles: [words.dualAudioSubs, false], note: true },
        { key: 'season', id: id(t.ids.seasonId), scope: 'Season', title: 'Astercoil Test · Season 1 · ' + seasonWord, library: 'Anime', audio: [notSet, true], subtitles: ['Signs and songs only', false], note: false },
        { key: 'forcedOnly', id: id(t.ids.series['Quorim Test']), scope: 'Series', title: 'Quorim Test (2043)', library: 'Anime', audio: [notSet, true], subtitles: ['Japanese', false], note: false },
        { key: 'signsDefault', id: id(t.ids.series['Velmora Test']), scope: 'Series', title: 'Velmora Test (2042)', library: 'Anime', audio: [words.signsDefaultAudio, false], subtitles: [words.signsDefaultSubs, false], note: true },
        { key: 'berlin', id: id(t.ids.movieId), scope: 'Title', title: 'Berlin Test (2019)', library: 'Movies', audio: ['German', false], subtitles: ['Off', false], note: false },
        { key: 'plain', id: id(t.ids.series['Plain Test']), scope: 'Series', title: 'Plain Test (2020)', library: 'TV Shows', audio: ['Japanese', false], subtitles: [notSet, true], note: false }
    ];
}

/**
 * Gives a person six overrides, as their own ⋮ menus and players would: remembered track changes for Astercoil Test and Velmora Test (a manual
 * change during playback), then explicit settings for Astercoil Test (audio: video default), its first season (signs and songs), Quorim Test
 * (Japanese subtitles), Berlin Test (German, Off) and Plain Test (Japanese audio). Returns the words the remembered changes read as.
 */
async function createSectionOverrides(t, api) {
    const uid = api.userId;
    const lagoonId = t.ids.series['Velmora Test'];
    const signsDefaultEps = ((await api.get('/Shows/' + lagoonId + '/Episodes', { userId: uid })).body || {}).Items || [];
    const signsDefault = await rememberTrackChange(t, api, signsDefaultEps[0].Id);
    const dualAudio = await rememberTrackChange(t, api, t.ids.episode2Id);
    const puts = [
        [t.ids.seriesId, 'Series', layerFor('video', undefined)],
        [t.ids.seasonId, 'Season', layerFor(undefined, 'signs')],
        [t.ids.series['Quorim Test'], 'Series', layerFor(undefined, 'lang:ja')],
        [t.ids.movieId, 'Title', layerFor('lang:de', 'off')],
        [t.ids.series['Plain Test'], 'Series', layerFor('lang:ja', undefined)]
    ];
    const statuses = [];
    for (const p of puts) {
        statuses.push((await putOverride(t, p[0], p[1], p[2], api)).status);
    }
    const rows = (await overrideSummaries(t, api)) || [];
    const manualOf = (id) => ((rows.find((r) => compactId(r.ItemId) === compactId(id)) || {}).Manual) || null;
    const lm = manualOf(lagoonId);
    const dm = manualOf(t.ids.seriesId);
    const words = {
        signsDefaultAudio: lm && lm.Audio ? languageName2(lm.Audio.Language) : '?',
        signsDefaultSubs: lm && lm.SubtitlesOff ? 'Off' : '?',
        dualAudioSubs: dm && dm.SubtitlesOff ? 'Off' : '?'
    };
    return { statuses: statuses, words: words, remembered: !!signsDefault && !!dualAudio && !!lm && !!dm, count: rows.length };
}

/** The test's checks of one drawing of the section against the rows it should list. */
async function sectionRowChecks(t, page, o, sec, expected) {
    const rows = sec.rows;
    const keyOf = (r) => (r.item || '') + '|' + (r.scope || '');
    const gotKeys = rows.map(keyOf);
    const wantKeys = expected.map((e) => e.id + '|' + e.scope);
    const names = rows.map((r) => r.title);
    // Order: by library, then by the show's or movie's name; a show's own row and its season row may come either way round.
    const collator = new Intl.Collator('en');
    const sortName = (r) => String(r.title || '').split(' · ')[0].replace(/ \(\d{4}\)$/, '');
    let ordered = true;
    for (let i = 1; i < rows.length; i++) {
        const a = rows[i - 1];
        const b = rows[i];
        const c = collator.compare((a.library || {}).text || '', (b.library || {}).text || '');
        if (c > 0 || (c === 0 && collator.compare(sortName(a), sortName(b)) > 0)) {
            ordered = false;
        }
    }
    await t.check(same(gotKeys.slice().sort(), wantKeys.slice().sort()) && ordered, o.where + ': the table lists the ' + expected.length + ' overrides, sorted by library and then name ('
        + names.join(' | ') + ')' + (same(gotKeys.slice().sort(), wantKeys.slice().sort()) ? '' : ' — ids differ: ' + gotKeys.join(', ')
            + (rows.some((r) => !r.item) ? ' — a row without an item id: ' + JSON.stringify(rows.find((r) => !r.item)) : '')), page);
    // The show's own row and its season's row: the section's own comment says seasons come after the show.
    const iShow = rows.findIndex((r) => r.scope === 'Series' && /^Astercoil Test/.test(r.title));
    const iSeason = rows.findIndex((r) => r.scope === 'Season');
    if (iShow >= 0 && iSeason >= 0 && iSeason < iShow && !t.seasonOrderNoted) {
        t.seasonOrderNoted = true;
        t.note(o.where + ': the season row ("' + rows[iSeason].title + '") is listed before its show\'s own row ("' + rows[iShow].title + '"); ui.js says "a show\'s seasons after the show", UI only says sorted by library, then name');
    }
    const problems = [];
    expected.forEach((e) => {
        const r = rows.find((x) => x.item === e.id && x.scope === e.scope);
        if (!r) {
            problems.push(e.key + ': no row');
            return;
        }
        if (apos(r.title) !== e.title) { problems.push(e.key + ': name "' + r.title + '" (want "' + e.title + '")'); }
        if (!r.library || r.library.text !== e.library || r.library.unset) { problems.push(e.key + ': library ' + JSON.stringify(r.library)); }
        if (!r.audio || apos(r.audio.text) !== e.audio[0] || r.audio.unset !== e.audio[1]) { problems.push(e.key + ': audio ' + JSON.stringify(r.audio) + ' (want "' + e.audio[0] + '")'); }
        if (!r.subtitles || apos(r.subtitles.text) !== e.subtitles[0] || r.subtitles.unset !== e.subtitles[1]) { problems.push(e.key + ': subtitles ' + JSON.stringify(r.subtitles) + ' (want "' + e.subtitles[0] + '")'); }
        if ((r.note === overrideWord('overrides.remembered')) !== e.note || (r.note !== null && !e.note)) { problems.push(e.key + ': note ' + JSON.stringify(r.note)); }
        if (apos(r.removeLabel || '') !== overrideWord('overrides.removeFor', { name: e.title }) || r.removeText !== 'Remove' || r.removeDisabled) { problems.push(e.key + ': Remove ' + JSON.stringify([r.removeText, r.removeLabel, r.removeDisabled])); }
    });
    await t.check(!problems.length, o.where + ': every row has the item\'s name with its year, its library, its audio and subtitles in the table\'s words ("' + overrideWord('overrides.notSet') + '" for what it doesn\'t set), '
        + 'the remembered-change note where there is one, the scope word for the season, and a Remove named after it' + (problems.length ? ' — ' + problems.join('; ') : ''), page);
}

/**
 * The checks of one drawing of the section (person page, the same page in the settings frame, or the People editor). o: { where, root,
 * inFrame, admin, name (the person, for the admin's confirmation), storageKey, strings, load() (opens the page afresh, the section drawn),
 * listApi() (the stored overrides), barShown() (an unsaved-changes mark), setup() (stores the six overrides again; returns their words), seasonWord }.
 */
async function sectionBattery(t, page, o) {
    const root = o.root;
    const frame = !!o.inFrame;
    // The six overrides.
    const made = await o.setup();
    await t.check(made.statuses.every((s) => s === 200) && made.remembered && made.count === 6, o.where + ': set up through the API: six overrides, two of them remembered track changes (' + made.statuses.join(',') + '; ' + made.count + ' listed)', null);
    const expected = expectedOverrideRows(t, made.words, o.seasonWord);

    // A. Closed by default, remembered once opened.
    await page.evaluate(pgClearBabelfinStorage);
    await o.load();
    let sec = await page.evaluate(pgOvSection, root, frame);
    await t.check(sec.found && sec.expanded === 'false' && sec.bodyHidden === true && /^H[2-4]$/.test(sec.heading) && sec.visible,
        o.where + ': the section "' + overrideWord('overrides.title') + '" is there, closed by default (' + (sec.found ? 'aria-expanded ' + sec.expanded + ', body hidden ' + sec.bodyHidden + ', ' + sec.heading : 'not found') + ')', page);
    await t.check(sec.title === overrideWord('overrides.title') + ' (' + expected.length + ')', o.where + ': its title counts the overrides: "' + sec.title + '"', page);
    await setOvSection(page, root, frame, true);
    const stored1 = await page.evaluate(function (k) { try { return localStorage.getItem(k); } catch (e) { return null; } }, o.storageKey);
    await o.load();
    sec = await page.evaluate(pgOvSection, root, frame);
    await t.check(stored1 === '1' && sec.expanded === 'true' && sec.bodyHidden === false, o.where + ': opened, it stays open after a reload (localStorage ' + o.storageKey + ' = ' + stored1 + ', aria-expanded ' + sec.expanded + ')', page);
    await setOvSection(page, root, frame, false);
    const stored0 = await page.evaluate(function (k) { try { return localStorage.getItem(k); } catch (e) { return null; } }, o.storageKey);
    await o.load();
    sec = await page.evaluate(pgOvSection, root, frame);
    await t.check(stored0 === '0' && sec.expanded === 'false', o.where + ': closed again, it stays closed after a reload (' + o.storageKey + ' = ' + stored0 + ', aria-expanded ' + sec.expanded + ')', page);
    sec = await setOvSection(page, root, frame, true);

    // B. The help line, the table's columns and rows.
    const helpWant = o.admin ? o.strings['overrides.helpAdmin'] : overrideWord('overrides.help');
    await t.check(apos(sec.help || '') === apos(helpWant || '?') && sec.tableLabel === overrideWord('overrides.title'), o.where + ': the help line reads "' + String(helpWant).slice(0, 60) + '…" and the table is named "' + overrideWord('overrides.title') + '" (' + JSON.stringify((sec.help || '').slice(0, 50)) + ', ' + sec.tableLabel + ')', page);
    await t.check(same(sec.headers, [overrideWord('overrides.col.item'), overrideWord('overrides.col.library'), overrideWord('overrides.col.audio'), overrideWord('overrides.col.subtitles'), 'Remove']),
        o.where + ': the columns are Show or movie, Library, Audio, Subtitles and a Remove column (' + sec.headers.join(', ') + ')', page);
    await sectionRowChecks(t, page, o, sec, expected);
    await t.check(sec.removeAll === overrideWord('overrides.removeAll') && /bfui-btn-danger/.test(sec.removeAllCls || ''), o.where + ': under the table a danger button "' + overrideWord('overrides.removeAll') + '" (' + JSON.stringify(sec.removeAll) + ')', page);
    if (t.opts.verbose) {
        await page.screenshot(path.join(t.opts.shots, 'look-overrides-' + slug(o.where) + '.png'), true);
    }

    // C. Remove one: Quorim Test. At once: no save bar, the row gone, the API agrees, a toast, focus on the next row's Remove.
    const barBefore = await o.barShown();
    const forcedOnly = expected.find((e) => e.key === 'forcedOnly');
    const signsDefault = expected.find((e) => e.key === 'signsDefault');
    await page.click(pgOvElement, { args: [root, frame, 'row', forcedOnly.id, forcedOnly.scope], what: 'Remove for Quorim Test' });
    const toasted = await waitToast(page, overrideWord('overrides.removed'), frame, 6000);
    await sleep(500);
    sec = await page.evaluate(pgOvSection, root, frame);
    const apiAfter = (await o.listApi()) || [];
    const focusedRow = await page.evaluate(function (rootSel, frameFlag) {
        var d = frameFlag ? ((document.querySelector('.bfSettingsFrame') || {}).contentDocument || null) : document;
        var a = d ? d.activeElement : null;
        var tr = a && a.closest ? a.closest('tr') : null;
        return { row: tr ? tr.getAttribute('data-item') : null, cls: a ? String(a.className) : null };
    }, root, frame);
    await t.check(sec.rows.length === 5 && !sec.rows.some((r) => r.item === forcedOnly.id) && !apiAfter.some((r) => compactId(r.ItemId) === forcedOnly.id) && apiAfter.length === 5,
        o.where + ': Remove on Quorim Test took its row out and the API agrees (' + sec.rows.length + ' rows, ' + apiAfter.length + ' stored)', page);
    await t.check(toasted && sec.title === overrideWord('overrides.title') + ' (5)', o.where + ': a toast says "' + overrideWord('overrides.removed') + '" and the title counts 5 (toast ' + toasted + ', "' + sec.title + '")', page);
    await t.check(!(await o.barShown()) && barBefore === false, o.where + ': removing is not an unsaved change: no save bar' + (o.admin ? ' (the editor\'s Save stays off)' : ''), page);
    await t.check(focusedRow.row === signsDefault.id && /bfui-overrides-remove/.test(focusedRow.cls || ''), o.where + ': focus moved to the next row\'s Remove (' + JSON.stringify(focusedRow) + ')', page);
    const left = expected.filter((e) => e.key !== 'forcedOnly');
    const rowsLeft = left.every((e) => sec.rows.some((r) => r.item === e.id && r.scope === e.scope));
    await t.check(rowsLeft, o.where + ': the other five rows are untouched', page);

    // D. Remove all: it asks first; Cancel keeps everything, the confirmation removes everything.
    await page.click(pgOvElement, { args: [root, frame, 'all'], what: 'Remove all show and movie overrides' });
    const dlg = await waitDlg(page, frame, 5000);
    const askText = o.admin ? overrideWord('overrides.removeAll.confirmTextFor', { name: o.name }) : overrideWord('overrides.removeAll.confirmText');
    await t.check(!!dlg && apos(dlg.title) === overrideWord('overrides.removeAll.confirmTitle') && apos(dlg.message) === askText && same(dlg.buttons.map((b) => b.text), ['Cancel', 'Remove']) && dlg.buttons[1].danger && dlg.role === 'alertdialog',
        o.where + ': Remove all asks "' + overrideWord('overrides.removeAll.confirmTitle') + '" with "' + askText + '" (' + (dlg ? JSON.stringify([dlg.title, dlg.message, dlg.buttons.map((b) => b.text)]) : 'no dialog') + ')', page);
    if (dlg) {
        await page.click(pgDlgButton, { args: [frame, 'Cancel'], what: 'Cancel in the confirmation' });
        await waitNoDlg(page, frame, 4000);
    }
    sec = await page.evaluate(pgOvSection, root, frame);
    const keptApi = (await o.listApi()) || [];
    await t.check(sec.rows.length === 5 && keptApi.length === 5, o.where + ': Cancel keeps all five (' + sec.rows.length + ' rows, ' + keptApi.length + ' stored)', page);
    await page.click(pgOvElement, { args: [root, frame, 'all'], what: 'Remove all show and movie overrides' });
    await waitDlg(page, frame, 5000);
    await page.click(pgDlgButton, { args: [frame, 'Remove'], what: 'Remove in the confirmation' });
    const doneToast = await waitToast(page, overrideWord('overrides.removeAll.done'), frame, 6000);
    await sleep(500);
    sec = await page.evaluate(pgOvSection, root, frame);
    const emptyApi = (await o.listApi()) || [];
    await t.check(doneToast && emptyApi.length === 0, o.where + ': confirming removed every override (the toast says "' + overrideWord('overrides.removeAll.done') + '"; ' + emptyApi.length + ' stored)', page);
    await t.check(sec.found && sec.visible && sec.expanded === 'true' && sec.rows.length === 0 && sec.headers.length === 0 && apos(sec.empty || '') === overrideWord('overrides.empty') && sec.removeAll === null && sec.title === overrideWord('overrides.title') + ' (0)',
        o.where + ': the section stays, reading "' + overrideWord('overrides.empty') + '", no table and no Remove all, titled "(0)" (' + JSON.stringify([sec.empty, sec.rows.length, sec.removeAll, sec.title]) + ')', page);
    await t.check(!(await o.barShown()), o.where + ': still no unsaved changes', page);
    // After a reload it is still empty, and the section still shows (so people can find it).
    await o.load();
    sec = await setOvSection(page, root, frame, true);
    await t.check(sec.found && sec.rows.length === 0 && apos(sec.empty || '') === overrideWord('overrides.empty'), o.where + ': after a reload it still shows the empty line', page);
}

/** Overrides added through the API while the page is open, then the page saved: nothing lost, nothing back (person page, standalone). */
async function keepOverridesPerson(t, page, strings) {
    const where = 'person page, Save with show and movie overrides';
    const barOf2 = () => barOf(page, false);
    const root = '#bf-overrides';
    await putOverride(t, t.ids.series['Quorim Test'], 'Series', layerFor(undefined, 'lang:ja'));
    await putOverride(t, t.ids.movieId, 'Title', layerFor('lang:de', 'off'));
    // The server's side: with keepOverrides=true the stored overrides stay whatever the body holds; without it the body's list replaces them.
    const bodyNone = clone((await getMe(t)).Settings);
    bodyNone.Overrides = [];
    const keep1 = await t.api.put('/Babelfin/Me/Settings', bodyNone, { keepOverrides: 'true' });
    const keptRows = (await overrideSummaries(t)) || [];
    const keep2 = await t.api.put('/Babelfin/Me/Settings', bodyNone);
    const plainRows = (await overrideSummaries(t)) || [];
    await t.check(keep1.status === 200 && keptRows.length === 2 && keep2.status === 200 && plainRows.length === 0,
        'PUT Me/Settings?keepOverrides=true keeps the stored overrides whatever the body holds (' + keptRows.length + ' of 2 after it); without it the body\'s empty list replaces them (' + plainRows.length + ' left)', null);
    await putOverride(t, t.ids.series['Quorim Test'], 'Series', layerFor(undefined, 'lang:ja'));
    await putOverride(t, t.ids.movieId, 'Title', layerFor('lang:de', 'off'));
    await openPersonPage(t, page);
    await setOvSection(page, root, false, true);
    // An unsaved edit on the page, then an override added from a ⋮ menu (through the API) before Save.
    const pick1 = await page.evaluate(pgPick, null, SPEC.subtitlesFor(SPEC.allLibraries), 'off');
    await sleep(t.settle);
    await t.check(pick1.ok && (await barOf2()).visible, where + ': an unsaved edit (Subtitles for All libraries: Off) shows the save bar', page);
    const harbour = await putOverride(t, t.ids.harbourId, 'Title', layerFor('lang:fr', undefined));
    await savePersonPage(t, page);
    const me1 = (await getMe(t)).Settings;
    const sums1 = (await overrideSummaries(t)) || [];
    const mine = (id) => !!overrideOf(me1.Overrides, id);
    await t.check(harbour.status === 200 && mine(t.ids.harbourId) && mine(t.ids.series['Quorim Test']) && mine(t.ids.movieId) && sums1.length === 3 && same(tableOf(me1.Everyday), storedFor(undefined, 'off')),
        where + ': Save stored the edit and kept all three overrides, the one added meanwhile among them (' + sums1.length + ' stored; Everyday subtitles ' + canon(tableOf(me1.Everyday)) + ')', page);
    await sleep(800);
    const sec1 = await page.evaluate(pgOvSection, root, false);
    await t.check(sec1.rows.length === 3 && sec1.title === overrideWord('overrides.title') + ' (3)' && sec1.rows.some((r) => r.item === compactId(t.ids.harbourId)),
        where + ': the section shows the override added meanwhile after the save (' + sec1.rows.length + ' rows, "' + sec1.title + '")', page);
    // Remove one in the section, then edit and Save: it must not come back.
    await page.click(pgOvElement, { args: [root, false, 'row', compactId(t.ids.movieId), 'Title'], what: 'Remove for Berlin Test' });
    await waitToast(page, overrideWord('overrides.removed'), false, 6000);
    const pick2 = await page.evaluate(pgPick, null, SPEC.subtitlesFor(SPEC.allLibraries), 'signs');
    await sleep(t.settle);
    await t.check(pick2.ok && (await barOf2()).visible, where + ': after removing one, an edit (Signs and songs only) shows the save bar', page);
    await savePersonPage(t, page);
    const me2 = (await getMe(t)).Settings;
    const sums2 = (await overrideSummaries(t)) || [];
    await t.check(!overrideOf(me2.Overrides, t.ids.movieId) && !sums2.some((r) => compactId(r.ItemId) === compactId(t.ids.movieId)) && sums2.length === 2 && same(tableOf(me2.Everyday), storedFor(undefined, 'signs')),
        where + ': Save did not bring the removed override back (' + sums2.length + ' stored, Berlin Test ' + (overrideOf(me2.Overrides, t.ids.movieId) ? 'there' : 'gone') + '; Everyday subtitles ' + canon(tableOf(me2.Everyday)) + ')', page);
    const bar2 = await barOf2();
    await t.check(!bar2.visible, where + ': and the save bar is gone', page);
    // The request itself asks the server to keep them.
    const puts = page.requests.filter((r) => r.method === 'PUT' && /\/Babelfin\/Me\/Settings/.test(r.url));
    await t.check(puts.length >= 2 && puts.slice(-2).every((r) => /[?&]keepOverrides=true/.test(r.url)), where + ': the page\'s Save sends PUT Me/Settings?keepOverrides=true (' + puts.slice(-2).map((r) => r.url.split('/Babelfin/')[1]).join(', ') + ')', null);
    await t.api.del(ME_OVERRIDES + '/All');
}

/** The same in the People editor of the second person: Save (keepOverrides) keeps an override added meanwhile and doesn't bring one back. */
async function keepOverridesEditor(t, page, vapi) {
    const where = 'People editor, Save with show and movie overrides';
    const root = '#bf-group-overrides';
    const viewerOv = '/Babelfin/Admin/Users/' + t.ids.viewerId + '/Overrides';
    await putOverride(t, t.ids.series['Quorim Test'], 'Series', layerFor(undefined, 'lang:ja'), vapi);
    await putOverride(t, t.ids.movieId, 'Title', layerFor('lang:de', 'off'), vapi);
    // The admin's route keeps them the same way.
    const viewerDocPath = '/Babelfin/Admin/Users/' + t.ids.viewerId + '/Settings';
    // These checks edit Everyday subtitles, so establish that row before opening the editor.
    const viewerBody = withTable(t, (await t.api.get(viewerDocPath)).body, { perLibrary: false });
    viewerBody.Overrides = [];
    const akeep1 = await t.api.put(viewerDocPath, viewerBody, { keepOverrides: 'true' });
    const akept = (await t.api.get(viewerOv)).body || [];
    const akeep2 = await t.api.put(viewerDocPath, viewerBody);
    const aplain = (await t.api.get(viewerOv)).body || [];
    await t.check(akeep1.status === 200 && akept.length === 2 && akeep2.status === 200 && aplain.length === 0,
        'PUT Admin/Users/{id}/Settings?keepOverrides=true keeps the stored overrides whatever the body holds (' + akept.length + ' of 2 after it); without it the body\'s empty list replaces them (' + aplain.length + ' left)', null);
    await putOverride(t, t.ids.series['Quorim Test'], 'Series', layerFor(undefined, 'lang:ja'), vapi);
    await putOverride(t, t.ids.movieId, 'Title', layerFor('lang:de', 'off'), vapi);
    await openEditor(t, page, t.ids.viewerId);
    const editorMode = await waitMode(page, EDITOR, SPEC.modeSame, [SPEC.allLibraries]);
    await t.check(editorMode.ok, where + ': the editor starts with the All libraries row (' + modeText(editorMode) + ')', page);
    await setOvSection(page, root, false, true);
    const barOn = async () => ((await snap(page, EDITOR, null, null, EDITOR)).saveBar || {}).visible === true;
    const pick1 = await page.evaluate(pgPick, EDITOR, SPEC.subtitlesFor(SPEC.allLibraries), 'off');
    await sleep(t.settle);
    await t.check(pick1.ok && (await barOn()), where + ': an unsaved edit in the editor (Subtitles for All libraries: Off) turns its Save on', page);
    const harbour = await putOverride(t, t.ids.harbourId, 'Title', layerFor('lang:fr', undefined), vapi);
    await page.click(function (scope) {
        var host = document.querySelector(scope);
        return host && Array.prototype.find.call(host.querySelectorAll('button'), function (b) { return b.textContent.trim() === 'Save'; });
    }, { args: [EDITOR], what: 'Save in the People editor' });
    const doc1 = await waitStored(t, '/Babelfin/Admin/Users/' + t.ids.viewerId + '/Settings', (d) => same(tableOf(d.Everyday), storedFor(undefined, 'off')));
    const list1 = (await t.api.get(viewerOv)).body || [];
    await t.check(harbour.status === 200 && !!doc1 && list1.length === 3 && list1.some((r) => compactId(r.ItemId) === compactId(t.ids.harbourId)),
        where + ': Save stored the edit and kept all three overrides, the one added meanwhile among them (' + list1.length + ' stored)', page);
    await sleep(1200);
    // Saving redraws the people list: open the editor again for the next steps.
    await openEditor(t, page, t.ids.viewerId);
    const sec1 = await setOvSection(page, root, false, true);
    await t.check(sec1.rows.length === 3, where + ': the editor lists all three after the save (' + sec1.rows.length + ' rows)', page);
    await page.click(pgOvElement, { args: [root, false, 'row', compactId(t.ids.movieId), 'Title'], what: 'Remove for Berlin Test' });
    await waitToast(page, overrideWord('overrides.removed'), false, 6000);
    const pick2 = await page.evaluate(pgPick, EDITOR, SPEC.subtitlesFor(SPEC.allLibraries), 'signs');
    await sleep(t.settle);
    await t.check(pick2.ok && (await barOn()), where + ': after removing one, an edit turns Save on', page);
    await page.click(function (scope) {
        var host = document.querySelector(scope);
        return host && Array.prototype.find.call(host.querySelectorAll('button'), function (b) { return b.textContent.trim() === 'Save'; });
    }, { args: [EDITOR], what: 'Save in the People editor' });
    const doc2 = await waitStored(t, '/Babelfin/Admin/Users/' + t.ids.viewerId + '/Settings', (d) => same(tableOf(d.Everyday), storedFor(undefined, 'signs')));
    const list2 = (await t.api.get(viewerOv)).body || [];
    await t.check(!!doc2 && list2.length === 2 && !list2.some((r) => compactId(r.ItemId) === compactId(t.ids.movieId)),
        where + ': Save did not bring the removed override back (' + list2.length + ' stored)', page);
    const puts = page.requests.filter((r) => r.method === 'PUT' && /\/Babelfin\/Admin\/Users\/[0-9a-f]+\/Settings/.test(r.url));
    await t.check(puts.length >= 2 && puts.slice(-2).every((r) => /[?&]keepOverrides=true/.test(r.url)), where + ': the editor\'s Save sends PUT Admin/Users/{id}/Settings?keepOverrides=true (' + puts.slice(-2).map((r) => r.url.split('/Babelfin/')[1]).join(', ') + ')', null);
    await vapi.del(ME_OVERRIDES + '/All');
}

/** At phone width the table stacks, every cell says what it is and every Remove can be reached. */
function pgOvPhone(rootSel) {
    var s = document.querySelector(rootSel);
    var table = s ? s.querySelector('table.bfui-overrides-table') : null;
    if (!table) { return { found: false }; }
    var rows = Array.prototype.slice.call(table.querySelectorAll(':scope > tbody > tr'));
    var first = rows[0];
    var out = {
        found: true, rows: rows.length, trDisplay: first ? getComputedStyle(first).display : null, tdDisplay: first ? getComputedStyle(first.querySelector('td')).display : null,
        scrollW: document.documentElement.scrollWidth, innerW: window.innerWidth, buttons: [], labels: []
    };
    rows.forEach(function (tr) {
        ['library', 'audio', 'subtitles'].forEach(function (cls) {
            var c = tr.querySelector('.bfui-overrides-' + cls);
            var content = c ? getComputedStyle(c, '::before').content : null;
            out.labels.push({ cls: cls, want: c ? c.getAttribute('data-label') : null, content: content ? content.replace(/^["']|["']$/g, '') : null });
        });
        var b = tr.querySelector('.bfui-overrides-remove');
        if (b) {
            b.scrollIntoView({ block: 'center' });
            var r = b.getBoundingClientRect();
            var at = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
            out.buttons.push({ left: Math.round(r.left), right: Math.round(r.right), w: Math.round(r.width), h: Math.round(r.height), hit: !!at && (at === b || b.contains(at)), label: b.getAttribute('aria-label') });
        }
    });
    var all = s.querySelector('.bfui-overrides-remove-all');
    if (all) {
        all.scrollIntoView({ block: 'center' });
        var ar = all.getBoundingClientRect();
        var at2 = document.elementFromPoint(ar.left + ar.width / 2, ar.top + ar.height / 2);
        out.all = { left: Math.round(ar.left), right: Math.round(ar.right), hit: !!at2 && (at2 === all || all.contains(at2)) };
    }
    return out;
}

async function phoneOverridesChecks(t, page, where, root, count) {
    const r = await page.evaluate(pgOvPhone, root);
    const bad = [];
    if (!r.found) {
        bad.push('no table');
    } else {
        if (r.trDisplay !== 'block' || r.tdDisplay !== 'block') { bad.push('not stacked (tr ' + r.trDisplay + ', td ' + r.tdDisplay + ')'); }
        if (r.scrollW > r.innerW) { bad.push('horizontal scroll ' + r.scrollW + ' > ' + r.innerW); }
        r.labels.filter((x) => x.content !== x.want || !x.want).forEach((x) => bad.push('cell "' + x.cls + '" says ' + JSON.stringify(x.content) + ' (want ' + JSON.stringify(x.want) + ')'));
        if (r.buttons.length !== count) { bad.push(r.buttons.length + ' Remove buttons (want ' + count + ')'); }
        r.buttons.filter((b) => b.left < 0 || b.right > r.innerW || !b.hit || b.h < 24 || b.w < 24).forEach((b) => bad.push('Remove "' + b.label + '" at ' + b.left + '-' + b.right + ', reachable ' + b.hit + ', ' + b.w + 'x' + b.h));
        if (!r.all || r.all.left < 0 || r.all.right > r.innerW || !r.all.hit) { bad.push('Remove all ' + JSON.stringify(r.all)); }
    }
    await t.check(!bad.length, where + ': at 375x812 the table stacks, each cell says what it is, nothing scrolls sideways and all ' + count + ' Remove buttons and Remove all can be reached' + (bad.length ? ' — ' + bad.join('; ') : ''), page);
}

async function testOverridesSection(t) {
    const page = t.page;
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    t.defer('remembered open/closed states cleared', async () => { await page.evaluate(pgClearBabelfinStorage).catch(() => {}); });
    t.defer('viewport back to desktop', () => page.clearViewport());
    const layout = await currentLayout(page);
    const s = await strings(t);
    const seasonWord = s.person['titles.scope.season'];
    await t.check(!!seasonWord, 'the shipped English has the scope word for a season override ("' + seasonWord + '")', null);
    // The person: nothing chosen in the table, "Same for every library", so the page's own edits have the All libraries row.
    await putMe(t, withTable(t, kept.snapshot, { perLibrary: false }), 'every library, nothing chosen');
    await t.api.del(ME_OVERRIDES + '/All');

    // ── The person page, standalone ──
    const person = {
        where: 'person page', root: '#bf-overrides', inFrame: false, admin: false, storageKey: 'babelfin-app-group-overrides', strings: s.person, seasonWord: seasonWord,
        load: async () => { await openPersonPage(t, page); },
        listApi: () => overrideSummaries(t),
        barShown: async () => (await barOf(page, false)).visible,
        setup: async () => { await t.api.del(ME_OVERRIDES + '/All'); return createSectionOverrides(t, t.api); }
    };
    await sectionBattery(t, page, person);
    const personDeletes = page.requests.filter((r) => r.method === 'DELETE' && /\/Babelfin\/Me\/Overrides/.test(r.url)).map((r) => r.url.split('/Babelfin/')[1].replace(/[0-9a-f]{32}/g, '{id}'));
    await t.check(personDeletes.some((u) => /^Me\/Overrides\/\{id\}\?scope=Series$/.test(u)) && personDeletes.some((u) => /^Me\/Overrides\/All$/.test(u)) && !personDeletes.some((u) => /^Me\/Overrides(?:\?|$)/.test(u)),
        'the person page removed through DELETE Me/Overrides/{itemId}?scope=… (one row) and DELETE Me/Overrides/All (all) (' + uniq(personDeletes).join(', ') + ')', null);
    const myActivity = await t.api.get('/Babelfin/Me/Activity', { limit: 200 });
    const myTexts = ((myActivity.body && (myActivity.body.Items || myActivity.body)) || []).map((x) => x.Text || x.text || '');
    await t.check(myTexts.some((x) => x === 'You removed all your show and movie overrides') && myTexts.some((x) => /^You removed your settings for /.test(x)),
        'Activity says "You removed all your show and movie overrides" and "You removed your settings for …" (' + myActivity.status + ')', null);
    await keepOverridesPerson(t, page, s.person);

    // The not-in-a-library row: GET Me/Overrides rewritten in the browser so one entry's item is in no library (as after a library was removed).
    await t.api.del(ME_OVERRIDES + '/All');
    await putOverride(t, t.ids.movieId, 'Title', layerFor('lang:de', 'off'));
    await putOverride(t, t.ids.series['Plain Test'], 'Series', layerFor('lang:ja', undefined));
    const stop = await page.interceptResponses('http*/Babelfin/Me/Overrides', (text) => {
        const body = JSON.parse(text);
        if (!Array.isArray(body)) {
            return null;
        }
        body.forEach((r) => { if (compactId(r.ItemId) === compactId(t.ids.movieId)) { r.LibraryId = null; r.LibraryName = null; } });
        return JSON.stringify(body);
    });
    await openPersonPage(t, page);
    const gone = await setOvSection(page, '#bf-overrides', false, true);
    const hits = await stop();
    const last = gone.rows[gone.rows.length - 1] || {};
    await t.check(hits.rewritten > 0 && gone.rows.length === 2 && last.item === compactId(t.ids.movieId) && last.library && apos(last.library.text) === overrideWord('overrides.noLibrary') && last.library.unset,
        'person page: a row whose item is in no library is listed last, with "' + overrideWord('overrides.noLibrary') + '" (' + gone.rows.map((r) => r.title + ' / ' + (r.library && r.library.text)).join(' | ') + ')', page);
    await t.api.del(ME_OVERRIDES + '/All');

    // ── The same page inside Jellyfin, at #/babelfin ──
    t.defer('layout back to "' + layout + '"', () => setLayout(t, layout));
    await setLayout(t, '');
    const framed = {
        where: 'person page at #/babelfin', root: '#bf-overrides', inFrame: true, admin: false, storageKey: 'babelfin-app-group-overrides', strings: s.person, seasonWord: seasonWord,
        load: async () => {
            // Straight to the route when the tab is somewhere else; a reload when it is there already (a reload of a page that hasn't started would lose the route).
            if (/\/web\/#\/babelfin/i.test(page.url || '')) {
                await page.reload();
            } else {
                await page.navigate(t.url('/web/' + SPEC.route));
            }
            await waitWebReady(t, page);
            await page.waitFor(function () {
                var f = document.querySelector('.bfSettingsFrame');
                var d = f && f.contentDocument;
                return !!(d && d.querySelector('#bf-overrides') && d.querySelector('select[aria-label^="Audio for"]'));
            }, { timeout: 30000, what: 'the settings frame with its overrides section' });
            await sleep(400);
        },
        listApi: () => overrideSummaries(t),
        barShown: async () => page.evaluate(pgSaveBarShown, true),
        setup: async () => { await t.api.del(ME_OVERRIDES + '/All'); return createSectionOverrides(t, t.api); }
    };
    await sectionBattery(t, page, framed);

    // ── The People editor of the second person ──
    const vapi = await viewerApi(t);
    await t.check(!!vapi, 'signed ' + t.opts.viewer + ' in through the API, to give them overrides as their own ⋮ menu and player would', null);
    if (vapi) {
        t.defer('second user signed out', () => endViewerSession(t));
        await keepViewer(t);
        await page.evaluate(pgClearBabelfinStorage);
        const editor = {
            where: 'People editor (' + t.opts.viewer + ')', root: '#bf-group-overrides', inFrame: false, admin: true, name: t.opts.viewer, storageKey: 'babelfin.admin.open.group.overrides', strings: s.person, seasonWord: seasonWord,
            load: async () => { await openEditor(t, page, t.ids.viewerId); },
            listApi: async () => (await t.api.get('/Babelfin/Admin/Users/' + t.ids.viewerId + '/Overrides')).body,
            barShown: async () => ((await snap(page, EDITOR, null, null, EDITOR)).saveBar || {}).visible === true,
            setup: async () => { await vapi.del(ME_OVERRIDES + '/All'); return createSectionOverrides(t, vapi); }
        };
        await sectionBattery(t, page, editor);
        await keepOverridesEditor(t, page, vapi);
        // Remove and Remove all went to the admin routes.
        const adminDeletes = page.requests.filter((r) => r.method === 'DELETE' && /\/Babelfin\/Admin\/Users\/[0-9a-f]{32}\/Overrides/.test(r.url)).map((r) => r.url.split('/Babelfin/')[1].replace(/[0-9a-f]{32}/g, '{id}'));
        await t.check(adminDeletes.some((u) => /Overrides\/\{id\}\?scope=Series$/.test(u)) && adminDeletes.some((u) => /^Admin\/Users\/\{id\}\/Overrides\/All$/.test(u)) && !adminDeletes.some((u) => /^Admin\/Users\/\{id\}\/Overrides(?:\?|$)/.test(u)),
            'the editor removed through DELETE Admin/Users/{id}/Overrides/{itemId}?scope=… and DELETE Admin/Users/{id}/Overrides/All (' + uniq(adminDeletes).join(', ') + ')', null);
        const theirActivity = await vapi.get('/Babelfin/Me/Activity', { limit: 200 });
        const theirTexts = ((theirActivity.body && (theirActivity.body.Items || theirActivity.body)) || []).map((x) => x.Text || x.text || '');
        await t.check(theirTexts.some((x) => x === 'An admin removed all your show and movie overrides') && theirTexts.some((x) => /^An admin removed your override for /.test(x)),
            t.opts.viewer + '\'s own Activity says "An admin removed all your show and movie overrides" and "An admin removed your override for …" (' + theirActivity.status + ')', null);

        // ── Phone width: the person page and the editor ──
        await page.setViewport(375, 812, true);
        await vapi.del(ME_OVERRIDES + '/All');
        await createSectionOverrides(t, vapi);
        await openEditor(t, page, t.ids.viewerId);
        await setOvSection(page, '#bf-group-overrides', false, true);
        await phoneOverridesChecks(t, page, 'People editor', '#bf-group-overrides', 6);
        await t.api.del(ME_OVERRIDES + '/All');
        await createSectionOverrides(t, t.api);
        await openPersonPage(t, page);
        await setOvSection(page, '#bf-overrides', false, true);
        await phoneOverridesChecks(t, page, 'person page', '#bf-overrides', 6);
        await page.clearViewport();
    }
}

// ───────────────────────────── Runner ─────────────────────────────

// App settings, reset, upgrade-note and subtitle regressions. Mocks and settings fixtures are restricted to the local rig.
function requireLoopback(t) {
    const u = new URL(t.base);
    if (!['127.0.0.1', 'localhost', '[::1]'].includes(u.hostname) || !['http:', 'https:'].includes(u.protocol)) {
        throw new Error('Browser regression fixtures require a loopback rig URL');
    }
}

async function testPrePlayChoices(t) {
    requireLoopback(t);
    const page = t.page;
    const before = clone(await getAdmin(t));
    t.defer('admin settings restored', () => putAdmin(t, before));
    t.defer('remembered open/closed states cleared', () => page.evaluate(pgClearBabelfinStorage));
    const shipped = await t.api.get('/Babelfin/Admin/Defaults');
    if (!await t.check(shipped.status === 200 && Array.isArray(shipped.body && shipped.body.AppModes), 'shipped app rows are available')) return;
    const discovered = 'Babelfin discovered app fixture';
    const seed = clone(before);
    seed.AppModes = clone(shipped.body.AppModes);
    seed.AppModes.push({ Client: discovered, Mode: 'Trust', SeenOnly: true, Seen: true, LastSeen: '2026-01-01T00:00:00Z', RememberPrePlayChoices: false });
    await putAdmin(t, seed, 'shipped app flags and a discovered row');
    const apps = await t.api.get('/Babelfin/Admin/Apps');
    const webFamily = ['Jellyfin Web', 'Jellyfin Media Player', 'Jellyfin Desktop', 'Jellyfin Android'];
    const rows = apps.status === 200 && Array.isArray(apps.body) ? apps.body : [];
    await t.check(webFamily.every(name => rows.some(r => r.Client === name)) && rows.length > webFamily.length
        && rows.every(r => typeof r.RememberPrePlayChoices === 'boolean' && r.RememberPrePlayChoices === webFamily.includes(r.Client)),
        'Apps exposes boolean shipped defaults: web family true, every other app false (' + rows.length + ' rows)');
    const en = (await strings(t)).admin;
    const label = 'Remember choices made before playback';
    await t.check(en['apps.rememberPrePlay'] === label && !!en['apps.rememberPrePlay.info']
        && !RETIRED_OVERRIDE_WORDING.some(re => re.test(label + ' ' + en['apps.rememberPrePlay.info'])),
        'checkbox has plain English label and explanation, without personal understand/read wording');
    await openAdminPage(t, page);
    await openEverything(page, true);
    await page.waitFor(function (scope, count) {
        return document.querySelectorAll(scope + ' .bf-apps-table input[type="checkbox"]').length === count;
    }, { what: 'the app checkboxes' }, ADMIN, rows.length);
    const controls = await page.evaluate(pgControls, ADMIN, null);
    const wrong = rows.filter(row => {
        const hits = controls.filter(c => c.label === label + ' · ' + row.Client && c.kind === 'checkbox');
        return hits.length !== 1 || hits[0].value !== String(row.RememberPrePlayChoices) || hits[0].disabled;
    });
    const tips = await page.evaluate(pgInfoTips, ADMIN);
    await t.check(!wrong.length && tips.filter(x => x.tip === en['apps.rememberPrePlay.info']).length === 1,
        'every app has an accessible, enabled checkbox with its effective value, and the column has one info tip'
        + (wrong.length ? ' — wrong: ' + wrong.map(x => x.Client).join(', ') : ''), page);
    for (const client of ['Jellyfin Web', discovered]) {
        const storedBefore = await getAdmin(t);
        const checked = client === discovered;
        await page.click(pgControlClickEl, { args: [ADMIN, { label: label + ' · ' + client }, null], what: label + ' for ' + client });
        await t.check((await barOf(page, true)).visible, 'toggling ' + client + ' shows the save bar', page);
        await saveAdminPage(t, page);
        const after = await getAdmin(t);
        const saved = after.AppModes.find(r => r.Client === client);
        const expected = clone(storedBefore);
        const expectedRow = expected.AppModes.find(r => r.Client === client);
        expectedRow.RememberPrePlayChoices = checked;
        expectedRow.SeenOnly = false;
        await t.check(!!saved && saved.RememberPrePlayChoices === checked && saved.SeenOnly === false && same(after, expected),
            'saving ' + client + ' stores the flag and SeenOnly=false, preserving its mode, metadata and all unrelated settings'
            + (same(after, expected) ? '' : ' — changed: ' + changedPaths(expected, after).join(', ')), page);
        await reloadAdminPage(t, page);
        await openEverything(page, true);
        await page.waitFor(function (scope, label, checked) {
            const h = window.__bfE2E, c = h.findControl(scope, { label: label });
            return c && c.checked === checked;
        }, { what: 'the saved checkbox after reload' }, ADMIN, label + ' · ' + client, checked);
        await t.check(!(await barOf(page, true)).visible, '' + client + ' keeps its saved checkbox after reload without unsaved changes', page);
    }
}

async function mockResetAll(t, page, respond) {
    requireLoopback(t);
    const endpoint = new URL(t.url('/Babelfin/Admin/ResetAll'));
    requireLoopback({ base: endpoint.origin });
    return page.mockResponses(endpoint.href + '*', async request => {
        if (request.method !== 'POST') return null;
        const url = new URL(request.url);
        if (url.origin !== endpoint.origin || url.pathname.replace(/\/$/, '') !== endpoint.pathname) {
            throw new Error('Reset fixture received an unexpected mutation path');
        }
        const response = await respond(request);
        if (response == null) throw new Error('Reset fixture must fulfil every matching POST');
        return response;
    });
}

async function testResetPartial(t) {
    requireLoopback(t);
    const page = t.page;
    const adminBefore = await getAdmin(t);
    const personBefore = (await getMe(t)).Settings;
    const backup = 'reset-fixture/babelfin-settings-before-reset-20260101-000000.json';
    const failed = ['Rig failed person A', 'Rig failed person B'];
    const resetRequests = [];
    const stop = await mockResetAll(t, page, request => {
        resetRequests.push(request);
        return { body: { BackupPath: backup, People: 1, FailedNames: failed } };
    });
    t.defer('mock removed', stop);
    t.defer('remembered open/closed states cleared', () => page.evaluate(pgClearBabelfinStorage));
    await openAdminPage(t, page);
    await setSection(page, ADMIN, SPEC.backup, true);
    await page.click(pgButton, { args: [ADMIN + ' #bfBackup', SPEC.resetButton], what: 'the mocked Reset all settings button' });
    const dlg = await waitDialog(page, 5000);
    if (dlg) await answerDialog(page, true);
    const result = await page.waitFor(function (scope) {
        const n = document.querySelector(scope + ' .bf-reset-done [role="alert"]');
        return n && { text: n.textContent, error: n.classList.contains('bf-alert-error') };
    }, { what: 'partial-reset error summary' }, ADMIN);
    await t.check(!!dlg && result.error && result.text.startsWith('Reset with problems:') && failed.every(n => result.text.includes(n))
        && result.text.includes(backup) && !result.text.includes(SPEC.resetDone),
        'partial reset names each failure and the backup in an alert, without claiming success (' + result.text + ')', page);
    const intercepted = await stop();
    await t.check(intercepted.fulfilled === 1 && resetRequests.length === 1 && new URL(resetRequests[0].url).searchParams.get('allowPartial') === 'true' && same(adminBefore, await getAdmin(t)) && same(personBefore, (await getMe(t)).Settings),
        'failure is mocked only in the browser on loopback; no reset reaches the server and settings are unchanged');
}

async function testMigrationNote(t) {
    requireLoopback(t);
    const page = t.page;
    let notices = [];
    let acknowledgements = 0;
    const stopRead = await page.interceptResponses(t.url('/Babelfin/Admin/Migrations'), (body, request) => request.method === 'GET' ? JSON.stringify(notices) : null);
    t.defer('migration response rewrite removed', stopRead);
    // Request-stage fulfilment acknowledges only the synthetic note, protecting the rig's real upgrade history.
    const stopAck = await page.mockResponses(t.url('/Babelfin/Admin/Migrations/Acknowledge'), request => {
        if (request.method !== 'POST') return null;
        acknowledgements += 1;
        notices = [];
        return { status: 204, body: '' };
    });
    t.defer('acknowledgement mock removed', stopAck);
    await openAdminPage(t, page);
    await page.waitFor(function () { return performance.getEntriesByType('resource').some(e => /\/Babelfin\/Admin\/Migrations$/.test(e.name)); }, { what: 'empty migration response' });
    await sleep(t.settle);
    await t.check(await page.evaluate(function () { return !document.querySelector('#bfMigrationNotice'); }),
        'no upgrade-note panel when no settings changed', page);
    const change = 'Moved a hidden collection choice into the saved retired settings.';
    notices = [{ Name: 'Rig migration person', Changes: [change] }];
    await reloadAdminPage(t, page);
    const text = await page.waitFor(function () { const n = document.querySelector('#bfMigrationNotice'); return n && n.textContent; }, { what: 'nonempty upgrade note' });
    await t.check(text.includes('Settings updated') && text.includes('Rig migration person') && text.includes(change)
        && !RETIRED_OVERRIDE_WORDING.some(re => re.test(text)), 'nonempty upgrade note names the person and change, without personal understand/read wording', page);
    const personPage = await t.newPage();
    await openPersonPage(t, personPage);
    await t.check(await personPage.evaluate(function () { return !document.querySelector('#bfMigrationNotice'); }),
        'upgrade commentary appears only on the admin page', personPage);
    await page.click(pgButton, { args: ['#bfMigrationNotice', 'Dismiss'], what: 'Dismiss the synthetic upgrade note' });
    await page.waitFor(function () { return !document.querySelector('#bfMigrationNotice'); }, { what: 'the dismissed upgrade note' });
    await t.check(acknowledgements === 1, 'Dismiss acknowledges the note once and removes its panel', page);
    await reloadAdminPage(t, page);
    await page.waitFor(function () { return performance.getEntriesByType('resource').some(e => /\/Babelfin\/Admin\/Migrations$/.test(e.name)); }, { what: 'acknowledged migration response' });
    await sleep(t.settle);
    await t.check(await page.evaluate(function () { return !document.querySelector('#bfMigrationNotice'); }),
        'dismissed note stays absent after reloading the acknowledged empty response', page);
    const reads = await stopRead(), acks = await stopAck();
    await t.check(reads.rewritten === 3 && acks.fulfilled === 1, 'all three note loads and the synthetic acknowledgement used the intended fixtures');
}

async function testSmartSubtitleRecompute(t) {
    requireLoopback(t);
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    const uid = compactId(t.api.userId);
    const user = await t.api.get('/Users/' + uid);
    if (user.status !== 200 || !user.body.Configuration) throw new Error('cannot read the Jellyfin user configuration');
    const jf = clone(user.body.Configuration);
    t.defer('Jellyfin subtitle preferences restored', async () => {
        const r = await t.api.post('/Users/' + uid + '/Configuration', jf);
        if (r.status !== 200 && r.status !== 204) throw new Error('Restore Jellyfin user configuration: ' + r.status);
    });
    const configured = await t.api.post('/Users/' + uid + '/Configuration', Object.assign({}, jf, {
        SubtitleMode: 'Smart', SubtitleLanguagePreference: 'eng', RememberAudioSelections: false, RememberSubtitleSelections: false
    }));
    if (configured.status !== 200 && configured.status !== 204) throw new Error('cannot set Smart subtitles: ' + configured.status);
    await putAdmin(t, adminNeutral(kept.admin), 'neutral server table');
    const mine = withTable(t, kept.snapshot, { perLibrary: false, everyday: ['lang:de', 'video'] });
    mine.Enabled = true;
    mine.Everyday.PrefersSdh = false;
    await putMe(t, mine, 'German audio, Video default subtitles');
    const removed = await t.api.del(ME_OVERRIDES + '/All');
    if (removed.status !== 204) throw new Error('cannot clear fixture overrides: ' + removed.status);
    const request = () => t.api.post('/Items/' + compactId(t.ids.movieId) + '/PlaybackInfo', { UserId: uid }, { userId: uid });
    const german = await request();
    const de = german.body && german.body.MediaSources && german.body.MediaSources[0];
    const streams = de && de.MediaStreams || [];
    const englishAudio = streams.find(s => s.Type === 'Audio' && /^(en|eng)$/i.test(s.Language));
    const germanAudio = streams.find(s => s.Type === 'Audio' && /^(de|deu|ger)$/i.test(s.Language));
    const englishSub = streams.find(s => s.Type === 'Subtitle' && /^(en|eng)$/i.test(s.Language) && !s.IsForced && !s.IsHearingImpaired);
    await t.check(german.status === 200 && !!de && !!germanAudio && !!englishAudio && !!englishSub
        && de.DefaultAudioStreamIndex === germanAudio.Index && de.DefaultSubtitleStreamIndex === englishSub.Index,
        'baseline PlaybackInfo: German audio with full English subtitles under Jellyfin Smart mode');
    Object.assign(mine.Everyday, storedFor('lang:en', 'video'));
    // Keep the deliberately empty fixture empty on the next settings save.
    mine.Overrides = [];
    await putMe(t, mine, 'English audio, Video default subtitles');
    const english = await request();
    const en = english.body && english.body.MediaSources && english.body.MediaSources[0];
    await t.check(english.status === 200 && !!en && !!englishAudio && en.DefaultAudioStreamIndex === englishAudio.Index
        && (en.DefaultSubtitleStreamIndex === -1 || en.DefaultSubtitleStreamIndex == null),
        'PlaybackInfo recomputes Smart subtitles for the selected English audio: subtitles off, instead of retaining the German-audio English subtitle');
}

// Audio, genre and saved-choice checks. All writes and failure fixtures stay on the isolated loopback rig.
const FIXTURE_GENRES = ['Babelfin browser alpha', 'Babelfin browser beta'];
const NO_CHOICES_MESSAGE = 'No audio or subtitle preferences have been set yet.';

async function eventually(read, accept, what) {
    const until = Date.now() + 12000;
    let last;
    do {
        last = await read();
        if (accept(last)) return last;
        await sleep(100);
    } while (Date.now() < until);
    throw new Error('timeout: ' + what + '; last=' + JSON.stringify(last).slice(0, 500));
}

function genreRule(name, audio, subtitles) {
    return { Id: '', Name: name, Enabled: true, Match: 'All',
        Conditions: [{ Kind: 'Genre', Values: [name], Negate: false, IsRegex: false }], Settings: layerFor(audio, subtitles) };
}

function genreRules(doc) {
    return (doc.SmartRules || []).filter(r => r.Enabled && r.Conditions && r.Conditions.length === 1
        && r.Conditions[0].Kind === 'Genre' && !r.Conditions[0].Negate && !r.Conditions[0].IsRegex && r.Conditions[0].Values.length === 1);
}

function neutralPerson(doc) {
    return Object.assign(clone(doc), { Enabled: true, PerLibrary: false, HandleNextEpisode: false,
        Everyday: { Understood: ['en'], Reads: ['en'] }, Libraries: [], SmartRules: [], Overrides: [], BurnedIn: [] });
}

async function readPerson(t, id) {
    const r = await t.api.get('/Babelfin/Admin/Users/' + compactId(id) + '/Settings');
    if (r.status !== 200) throw new Error('person settings: ' + r.status);
    return r.body.Settings || r.body;
}

function editorContexts(t, page) {
    return [
        { name: 'person', scope: '.bf-table-section', load: () => openPersonPage(t, page), read: async () => (await getMe(t)).Settings,
            save: () => savePersonPage(t, page), viewer: t.api.userId },
        { name: 'admin defaults', scope: LIBS, load: () => openAdminPage(t, page), read: () => getAdmin(t),
            save: () => saveAdminPage(t, page), viewer: t.api.userId },
        { name: 'People editor', scope: EDITOR, load: () => openEditor(t, page, t.ids.viewerId), read: () => readPerson(t, t.ids.viewerId),
            save: () => page.click(pgButton, { args: [EDITOR, 'Save'], what: 'Save the People editor' }), viewer: t.ids.viewerId }
    ];
}

async function metadataFixture(t, id, changes) {
    const r = await t.api.get('/Users/' + compactId(t.api.userId) + '/Items/' + compactId(id));
    if (r.status !== 200) throw new Error('cannot snapshot item metadata: ' + r.status);
    const original = clone(r.body);
    const save = async body => {
        const result = await t.api.post('/Items/' + compactId(id), body);
        if (result.status !== 200 && result.status !== 204) throw new Error('item metadata save: ' + result.status);
    };
    const genresChanged = Object.prototype.hasOwnProperty.call(changes, 'Genres');
    const read = () => t.api.get('/Users/' + compactId(t.api.userId) + '/Items/' + compactId(id));
    t.defer('metadata restored for ' + compactId(id), async () => {
        await save(original);
        if (genresChanged) {
            const restored = await read();
            await t.check(restored.status === 200 && same(restored.body.Genres || [], original.Genres || []),
                'fixture: original genres restored for ' + original.Name);
        }
    });
    await save(Object.assign(clone(original), changes));
    if (genresChanged) {
        await eventually(read, r => r.status === 200 && same(r.body.Genres || [], changes.Genres), 'fixture item genres saved');
    }
    return original;
}

async function genreFixture(t, names = FIXTURE_GENRES) {
    await metadataFixture(t, t.ids.seriesId, { Genres: names.slice() });
    await metadataFixture(t, t.ids.episodeId, { Genres: names.slice() });
    await metadataFixture(t, t.ids.movieId, { Genres: [names[1]] });
    // Metadata updates save genre names without indexing new named Genre items. Register and locally
    // refresh those items so the real /Genres catalogue includes both names; no metadata/image fetching.
    for (const name of names) {
        const r = await t.api.get('/Genres/' + encodeURIComponent(name), { UserId: t.api.userId });
        if (r.status !== 200) throw new Error('Cannot register the fixture genre: ' + r.status);
        const refreshed = await t.api.post('/Items/' + compactId(r.body.Id) + '/Refresh', undefined,
            { metadataRefreshMode: 'None', imageRefreshMode: 'None', replaceAllMetadata: 'false', replaceAllImages: 'false' });
        if (refreshed.status !== 200 && refreshed.status !== 204) throw new Error('Cannot index the fixture genre: ' + refreshed.status);
    }
    await eventually(() => t.api.get('/Genres', { UserId: t.api.userId }), r => r.status === 200
        && names.every(name => (r.body?.Items || []).some(item => item.Name === name)), 'the registered fixture genres are visible');
}

async function pickChoice(t, page, scope, label, value) {
    const picked = await page.evaluate(pgPick, scope, label, value);
    if (!picked.ok) throw new Error('' + label + ': ' + picked.reason);
    await sleep(150);
}

function pgAudioChoice(scope, label) {
    const h = window.__bfE2E, root = h.root(scope);
    const sel = root && Array.from(root.querySelectorAll('select')).find(e => e.getAttribute('aria-label') === label && h.vis(e));
    if (!sel) return null;
    const line = sel.closest('.bfui-choice-line') || sel.parentElement;
    const ids = (sel.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean);
    return { value: sel.value, options: Array.from(sel.options).map(o => ({ value: o.value, text: o.textContent.trim(), disabled: o.disabled })),
        description: ids.map(id => document.getElementById(id)?.textContent || '').join(' '),
        tips: Array.from(line.querySelectorAll('button.bfui-info')).filter(h.vis).map(b => ({ name: b.getAttribute('aria-label'),
            text: document.getElementById(b.getAttribute('aria-describedby'))?.textContent || '' })) };
}

function pgGenres(scope) {
    const h = window.__bfE2E, root = h.root(scope), panel = root && root.querySelector('.bfui-genres');
    if (!panel) return null;
    const picker = panel.querySelector('.bfui-genres-picker select');
    return { count: root.querySelectorAll('.bfui-genres').length, title: panel.getAttribute('aria-label'),
        rows: Array.from(panel.querySelectorAll('tbody > tr')).map(tr => ({ name: tr.querySelector('th')?.textContent.trim(),
            values: Array.from(tr.querySelectorAll('select')).map(sel => sel.value),
            actions: Array.from(tr.querySelectorAll('[data-action]')).map(b => ({ action: b.dataset.action, disabled: b.disabled, label: b.getAttribute('aria-label') })) })),
        choices: picker && h.vis(picker) ? Array.from(picker.options).map(o => o.value).filter(Boolean) : null,
        afterTable: !!panel.previousElementSibling && !!panel.previousElementSibling.querySelector('select[aria-label^="Audio for "]') };
}

function pgGenreAction(scope, action, name) {
    const root = window.__bfE2E.root(scope), panel = root && root.querySelector('.bfui-genres');
    if (!panel) return null;
    if (action === 'add') return panel.querySelector(':scope > button');
    if (action === 'confirm') return panel.querySelector('.bfui-genres-picker button');
    if (action === 'cancel') return Array.from(panel.querySelectorAll('.bfui-genres-picker button')).find(b => b.textContent.trim() === 'Cancel');
    const row = Array.from(panel.querySelectorAll('tbody > tr')).find(r => r.querySelector('th')?.textContent.trim() === name);
    return row && row.querySelector('[data-action="' + action + '"]');
}

async function openGenrePicker(page, scope) {
    await page.click(pgGenreAction, { args: [scope, 'add'], what: 'Add a genre' });
    return page.waitFor(pgGenres, { what: 'the visible genre picker' }, scope).then(async () => {
        return eventually(() => page.evaluate(pgGenres, scope), state => !!state && Array.isArray(state.choices), 'genre catalogue');
    });
}

function genreRequests(page, from) {
    return page.requests.slice(from).filter(r => r.method === 'GET' && new URL(r.url).pathname.toLowerCase().endsWith('/genres'));
}

async function addGenre(t, page, context, name) {
    const from = page.requests.length;
    const picker = await openGenrePicker(page, context.scope);
    await t.check(picker.choices.includes(name), '' + context.name + ': visible server genres include ' + name, page);
    if (!picker.choices.includes(name)) throw new Error('Fixture genre is not visible in /Genres: ' + name);
    const requests = genreRequests(page, from);
    await t.check((context.genreFetched || requests.length === 1) && requests.every(r => {
        const query = new URL(r.url).searchParams;
        return compactId(query.get('UserId') || query.get('userId')) === compactId(context.viewer);
    }), '' + context.name + ': /Genres is scoped to the edited viewer with UserId (cached catalogue allowed)', page);
    context.genreFetched = true;
    await pickChoice(t, page, context.scope + ' .bfui-genres', 'Choose a genre', name);
    await page.click(pgGenreAction, { args: [context.scope, 'confirm'], what: 'Confirm Add a genre' });
}

async function testAudioChoices(t) {
    requireLoopback(t);
    const page = t.page, kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    const viewer = await keepViewer(t);
    const admin = adminNeutral(kept.admin);
    admin.SmartRules = [];
    await putAdmin(t, admin);
    await putMe(t, neutralPerson(kept.snapshot));
    const person = neutralPerson(viewer);
    const vr = await t.api.put('/Babelfin/Admin/Users/' + t.ids.viewerId + '/Settings', person);
    if (vr.status !== 200) throw new Error('Cannot prepare People editor: ' + vr.status);
    await t.check((await getMe(t)).Settings.SchemaVersion === SPEC.personSchemaVersion && (await getAdmin(t)).ConfigVersion === SPEC.configVersion,
        'person schema 3 and config 8 are current');
    const en = (await strings(t)).person;
    for (const context of editorContexts(t, page)) {
        await context.load();
        for (const [value, label] of Object.entries(SPEC.audioChoices)) {
            const before = await context.read();
            await pickChoice(t, page, context.scope, SPEC.audioFor(SPEC.allLibraries), value);
            const shown = await page.evaluate(pgAudioChoice, context.scope, SPEC.audioFor(SPEC.allLibraries));
            const help = value === 'original' ? en['ui.table.audioOriginal.info'] : null;
            await t.check(!!shown && shown.value === value && shown.options.filter(o => o.value === value && o.text === label && !o.disabled).length === 1
                && (value === 'mine' ? shown.tips.length === 0 && !shown.description : shown.tips.length === 1 && shown.tips[0].name === 'More about ' + label && shown.tips[0].text === help && shown.description === help),
                '' + context.name + ': ' + label + ' has one native option and the required tooltip behavior', page);
            await context.save();
            const after = await eventually(context.read, doc => same(doc.Everyday.AudioChain, storedFor(value).AudioChain), 'saved new audio chain');
            await t.check(same(after.Everyday.AudioChain, storedFor(value).AudioChain) && !after.Everyday.ForeignTitles
                && same(tableOf(after.Everyday), Object.assign(tableOf(before.Everyday), storedFor(value))) && same(after.SmartRules, before.SmartRules),
                '' + context.name + ': Save stores exactly the two-step ' + value + ' + VideoDefault chain and preserves subtitle/genre choices', page);
            await context.load();
            await t.check((await page.evaluate(pgAudioChoice, context.scope, SPEC.audioFor(SPEC.allLibraries)))?.value === value,
                '' + context.name + ': ' + label + ' survives reload as its own option', page);
        }
    }
    await openPersonPage(t, page);
    await ensureMode(page, null, SPEC.modePer, libraryNames(kept.me));
    for (const value of ['mine', 'original']) {
        await pickChoice(t, page, '.bf-table-section', SPEC.audioFor('Anime'), value);
        await savePersonPage(t, page);
        const after = (await getMe(t)).Settings;
        await t.check(same(libraryLayer(after, t.ids.libraries.Anime)?.AudioChain, storedFor(value).AudioChain),
            'a per-library row stores the two-step ' + value + ' chain', page);
        await reloadPersonPage(t, page);
        await t.check((await page.evaluate(pgAudioChoice, '.bf-table-section', SPEC.audioFor('Anime')))?.value === value,
            'per-library ' + value + ' survives reload', page);
    }
    for (const value of ['mine', 'original']) {
        const dialog = await openItemMenuDialog(t, page, t.ids.movieId);
        if (!dialog) throw new Error('item dialog is missing');
        await pickChoice(t, page, '.bfDialog', 'Audio', value);
        const selected = await page.evaluate(pgAudioChoice, '.bfDialog', 'Audio');
        await t.check(value === 'mine' ? selected.tips.length === 0 && !selected.description : selected.tips.length === 1, 'item dialog: My languages has no tooltip; original language retains its explanation', page);
        await page.click(pgBfDialogButton, { args: ['Save'], what: 'Save the item audio choice' });
        if (!await waitNoBfDialog(page, 10000)) throw new Error('item save did not close');
        const saved = (await getMe(t)).Settings.Overrides.find(o => compactId(o.ItemId) === compactId(t.ids.movieId));
        await t.check(!!saved && same(saved.Settings.AudioChain, storedFor(value).AudioChain),
            'the item dialog shares the new ' + value + ' option and saves the same chain', page);
    }
}

async function testGenreEditor(t) {
    requireLoopback(t);
    const page = t.page, kept = await keepPersonAndAdmin(t), viewer = await keepViewer(t);
    await putMe(t, neutralPerson(kept.snapshot));
    const admin = adminNeutral(kept.admin);
    admin.SmartRules = [];
    await putAdmin(t, admin);
    const vr = await t.api.put('/Babelfin/Admin/Users/' + t.ids.viewerId + '/Settings', neutralPerson(viewer));
    if (vr.status !== 200) throw new Error('Cannot prepare genre editor viewer: ' + vr.status);
    await genreFixture(t);
    for (const context of editorContexts(t, page)) {
        await context.load();
        const initial = await page.evaluate(pgGenres, context.scope);
        await t.check(initial && initial.count === 1 && initial.title === 'Genres' && initial.afterTable && initial.rows.length === 0,
            '' + context.name + ': one genre editor directly follows the library table, with no preset row', page);
        for (const name of FIXTURE_GENRES) await addGenre(t, page, context, name);
        await pickChoice(t, page, context.scope + ' .bfui-genres', SPEC.audioFor(FIXTURE_GENRES[0]), 'mine');
        await pickChoice(t, page, context.scope + ' .bfui-genres', SPEC.subtitlesFor(FIXTURE_GENRES[0]), 'lang:en');
        await pickChoice(t, page, context.scope + ' .bfui-genres', SPEC.audioFor(FIXTURE_GENRES[1]), 'original');
        await page.click(pgGenreAction, { args: [context.scope, 'down', FIXTURE_GENRES[0]], what: 'Move the first genre down' });
        let state = await page.evaluate(pgGenres, context.scope);
        await t.check(state.rows.map(r => r.name).join('|') === [FIXTURE_GENRES[1], FIXTURE_GENRES[0]].join('|')
            && state.rows[0].actions.find(a => a.action === 'up').disabled && state.rows[1].actions.find(a => a.action === 'down').disabled,
            '' + context.name + ': reorder changes row priority and disables unavailable moves', page);
        await context.save();
        await eventually(context.read, doc => genreRules(doc).map(r => r.Conditions[0].Values[0]).join('|') === [FIXTURE_GENRES[1], FIXTURE_GENRES[0]].join('|'), 'genre order saved');
        const saved = await context.read(), rows = genreRules(saved);
        await t.check(same(tableOf(rows[0].Settings), storedFor('original')) && same(tableOf(rows[1].Settings), storedFor('mine', 'lang:en')),
            '' + context.name + ': genre audio and subtitles persist independently in the reordered SmartRules', page);
        await context.load();
        state = await page.evaluate(pgGenres, context.scope);
        await t.check(state.rows.map(r => r.name).join('|') === [FIXTURE_GENRES[1], FIXTURE_GENRES[0]].join('|') && state.rows[1].values.join('|') === 'mine|lang:en',
            '' + context.name + ': row order and both choices survive reload', page);
        const picker = await openGenrePicker(page, context.scope);
        await t.check(FIXTURE_GENRES.every(name => !picker.choices.includes(name)), '' + context.name + ': the picker excludes genres already in the table', page);
        await page.click(pgGenreAction, { args: [context.scope, 'cancel'], what: 'Cancel the genre picker' });
        await page.click(pgGenreAction, { args: [context.scope, 'remove', FIXTURE_GENRES[1]], what: 'Remove a genre' });
        await context.save();
        await eventually(context.read, doc => genreRules(doc).length === 1 && genreRules(doc)[0].Conditions[0].Values[0] === FIXTURE_GENRES[0], 'genre removal saved');
        await context.load();
        await t.check((await page.evaluate(pgGenres, context.scope)).rows.length === 1, '' + context.name + ': Remove persists after reload', page);
    }
    await openPersonPage(t, page);
    await ensureMode(page, null, SPEC.modePer, libraryNames(kept.me));
    await t.check((await page.evaluate(pgGenres, '.bf-table-section')).count === 1,
        'choosing per-library mode keeps one person genre editor under the library rows', page);
    await openAdminPage(t, page);
    await openEverything(page, true);
    const noTemplate = await page.evaluate(function (scope) {
        const h = window.__bfE2E, outer = h.disclosure(scope, 'Advanced'), region = outer && h.regionOf(outer);
        const group = region && h.disclosure(region, 'New users'), content = group && h.regionOf(group);
        return content && !content.querySelector('.bfui-genres') && !content.textContent.includes('Add a genre');
    }, ADMIN);
    await t.check(noTemplate, 'New users has no genre editor or Add a genre control', page);
    // A limited viewer can see Movies only. The admin still sees the hidden Anime genre.
    await metadataFixture(t, t.ids.seriesId, { Genres: [FIXTURE_GENRES[0]] });
    await metadataFixture(t, t.ids.episodeId, { Genres: [FIXTURE_GENRES[0]] });
    const user = await t.api.get('/Users/' + compactId(t.ids.viewerId));
    if (user.status !== 200 || !user.body.Policy) throw new Error('Cannot snapshot limited-viewer policy');
    const policy = clone(user.body.Policy), limited = Object.assign(clone(policy), { EnableAllFolders: false, EnabledFolders: [t.ids.libraries.Movies] });
    const putPolicy = async body => {
        const r = await t.api.post('/Users/' + compactId(t.ids.viewerId) + '/Policy', body);
        if (r.status !== 200 && r.status !== 204) throw new Error('viewer policy save: ' + r.status);
    };
    t.defer('viewer library visibility restored', () => putPolicy(policy));
    await putPolicy(limited);
    const limitedDoc = await readPerson(t, t.ids.viewerId);
    limitedDoc.SmartRules = [];
    const cleared = await t.api.put('/Babelfin/Admin/Users/' + t.ids.viewerId + '/Settings', limitedDoc);
    if (cleared.status !== 200) throw new Error('Cannot clear the limited-viewer genre fixture');
    await openEditor(t, page, t.ids.viewerId);
    const from = page.requests.length, picker = await openGenrePicker(page, EDITOR);
    const requests = genreRequests(page, from);
    await t.check(requests.length === 1 && requests.every(r => compactId(new URL(r.url).searchParams.get('UserId')) === compactId(t.ids.viewerId))
        && picker.choices.includes(FIXTURE_GENRES[1]) && !picker.choices.includes(FIXTURE_GENRES[0]),
        'limited-viewer People picker requests the edited person and omits the genre from the hidden Anime library', page);
    await page.click(pgGenreAction, { args: [EDITOR, 'cancel'], what: 'Cancel the limited-viewer genre picker' });
}

function neutralAdmin(doc) {
    const out = adminNeutral(doc);
    out.Enabled = true;
    out.Everyday = {};
    out.Libraries = [];
    out.PerLibraryDefaults = false;
    out.SmartRules = [];
    return out;
}

async function expectChoice(t, item, audio, subtitle, what) {
    const off = n => n == null || n < 0 ? -1 : n;
    const result = await choiceOf(t, item, body => body && body[0] && body[0].AudioIndex === audio && off(body[0].SubtitleIndex) === off(subtitle));
    const row = result.body && result.body[0];
    await t.check(result.status === 200 && !!row && row.AudioIndex === audio && off(row.SubtitleIndex) === off(subtitle),
        what + ' (' + choiceText(result) + ')');
    return row;
}

async function testGenrePrecedence(t) {
    requireLoopback(t);
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    await genreFixture(t);
    await t.api.del(ME_OVERRIDES + '/All');
    const item = await t.api.get('/Users/' + compactId(t.api.userId) + '/Items/' + compactId(t.ids.episodeId));
    const streams = item.body?.MediaSources?.[0]?.MediaStreams || [];
    const en = streams.find(s => s.Type === 'Audio' && /^(en|eng)$/i.test(s.Language));
    const ja = streams.find(s => s.Type === 'Audio' && /^(ja|jpn)$/i.test(s.Language));
    const full = streams.find(s => s.Type === 'Subtitle' && /^(en|eng)$/i.test(s.Language) && !s.IsForced && !s.IsHearingImpaired);
    if (!en || !ja || !full) throw new Error('The precedence fixture needs English/Japanese audio and full English subtitles');
    const admin = neutralAdmin(kept.admin), person = neutralPerson(kept.snapshot);
    Object.assign(admin.Everyday, layerFor('lang:en', 'off'));
    admin.SmartRules = [genreRule(FIXTURE_GENRES[0], 'lang:ja', 'lang:en')];
    await putAdmin(t, admin);
    await putMe(t, person);
    await expectChoice(t, t.ids.episodeId, ja.Index, full.Index, 'admin genre beats the admin library/default row');
    Object.assign(person.Everyday, layerFor('lang:en', 'off'));
    await putMe(t, person);
    await expectChoice(t, t.ids.episodeId, en.Index, -1, 'the person every-library row beats the admin genre');
    person.SmartRules = [genreRule(FIXTURE_GENRES[0].toUpperCase(), 'lang:ja', undefined)];
    await putMe(t, person);
    await expectChoice(t, t.ids.episodeId, ja.Index, -1, 'personal genre matching ignores case; its audio wins while subtitles inherit the person row');
    await putOverride(t, t.ids.seriesId, 'Series', layerFor('lang:en', undefined));
    await expectChoice(t, t.ids.episodeId, en.Index, -1, 'explicit show audio beats the personal genre');
    await putOverride(t, t.ids.seriesId, 'Series', layerFor(undefined, 'lang:en'));
    await expectChoice(t, t.ids.episodeId, ja.Index, full.Index, 'show subtitles win independently while audio still comes from the person genre');
    await deleteOverride(t, t.ids.seriesId, 'Series');
    person.SmartRules = [genreRule(FIXTURE_GENRES[1], 'lang:en', undefined), genreRule(FIXTURE_GENRES[0], 'lang:ja', undefined)];
    await putMe(t, person);
    await expectChoice(t, t.ids.episodeId, en.Index, -1, 'the first of two matching personal genres wins');
    person.SmartRules.reverse();
    await putMe(t, person);
    await expectChoice(t, t.ids.episodeId, ja.Index, -1, 'reversing the matching genre rows changes the decision');
    person.PerLibrary = true;
    person.Libraries = [{ LibraryId: compactId(t.ids.libraries.Anime), Settings: layerFor('lang:en', 'off') }];
    await putMe(t, person);
    await expectChoice(t, t.ids.episodeId, ja.Index, -1, 'personal genre audio beats the person library audio, preserving that library subtitle field');
    await rememberTrackChange(t, t.api, t.ids.episodeId);
    await expectChoice(t, t.ids.episodeId, en.Index, -1, 'an in-play remembered show choice also beats the genre rows');
}

async function testOriginalAndLanguages(t) {
    requireLoopback(t);
    const page = t.page, kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    await t.api.del(ME_OVERRIDES + '/All');
    await putAdmin(t, neutralAdmin(kept.admin));
    const person = neutralPerson(kept.snapshot);
    Object.assign(person.Everyday, layerFor('mine', 'off'));
    const movie = await t.api.get('/Users/' + compactId(t.api.userId) + '/Items/' + compactId(t.ids.movieId));
    const streams = movie.body?.MediaSources?.[0]?.MediaStreams || [];
    const de = streams.find(s => s.Type === 'Audio' && /^(de|deu|ger)$/i.test(s.Language));
    const en = streams.find(s => s.Type === 'Audio' && /^(en|eng)$/i.test(s.Language));
    if (!de || !en) throw new Error('The ordered-language fixture requires the German/English movie');
    for (const [languages, expected] of [[['de', 'en'], de.Index], [['en', 'de'], en.Index]]) {
        person.Everyday.Understood = languages;
        await putMe(t, person);
        await expectChoice(t, t.ids.movieId, expected, -1, 'My languages order ' + languages.join(', ') + ' is the playback order');
    }
    Object.assign(person.Everyday, layerFor('video', 'off'));
    await putMe(t, person);
    const movieDefault = await t.api.get('/Babelfin/Me/Choice', { itemId: t.ids.movieId });
    if (movieDefault.status !== 200 || !movieDefault.body?.[0]) throw new Error('Cannot read the movie Video default reference');
    Object.assign(person.Everyday, layerFor('mine', 'off'), { Understood: ['fr'] });
    await putMe(t, person);
    await expectChoice(t, t.ids.movieId, movieDefault.body[0].AudioIndex, -1, 'no available My languages track falls back to Video default');
    Object.assign(person.Everyday, layerFor('video', 'off'), { Understood: ['en'] });
    await putMe(t, person);
    const native = await t.api.get('/Babelfin/Me/Choice', { itemId: t.ids.episodeId });
    if (native.status !== 200 || !native.body?.[0]) throw new Error('Cannot read the episode Video default reference');
    const episode = await t.api.get('/Users/' + compactId(t.api.userId) + '/Items/' + compactId(t.ids.episodeId));
    const epStreams = episode.body?.MediaSources?.[0]?.MediaStreams || [];
    const ja = epStreams.find(s => s.Type === 'Audio' && /^(ja|jpn)$/i.test(s.Language));
    if (!ja) throw new Error('The original-language fixture requires Japanese audio');
    await metadataFixture(t, t.ids.seriesId, { OriginalLanguage: 'ja' });
    await metadataFixture(t, t.ids.episodeId, { OriginalLanguage: 'ja' });
    Object.assign(person.Everyday, layerFor('original', 'off'));
    await putMe(t, person);
    await expectChoice(t, t.ids.episodeId, ja.Index, -1, 'original-language audio comes from title metadata');
    await metadataFixture(t, t.ids.seriesId, { OriginalLanguage: 'fr' });
    await metadataFixture(t, t.ids.episodeId, { OriginalLanguage: 'fr' });
    await expectChoice(t, t.ids.episodeId, native.body[0].AudioIndex, -1, 'a metadata language absent from the streams falls back to Video default');
    await metadataFixture(t, t.ids.seriesId, { OriginalLanguage: null });
    await metadataFixture(t, t.ids.episodeId, { OriginalLanguage: null });
    await expectChoice(t, t.ids.episodeId, native.body[0].AudioIndex, -1,
        'missing metadata falls back to Video default instead of guessing Japanese from the Anime library or track language');
    const explanation = await t.api.get('/Babelfin/Me/Explain', { itemId: t.ids.episodeId });
    await t.check(explanation.status === 200 && /original language missing from metadata/i.test(JSON.stringify(explanation.body)),
        'the explanation states that original-language metadata is missing');
    await openAdminPage(t, page);
    const tools = await toolsTitle(page);
    await setSection(page, ADMIN, tools, true);
    await ensureGroup(page, ADMIN, tools, 'Check a title', true);
    const checked = await checkTitle(t, page, tools, t.opts.user, 'Astercoil', 'Astercoil Test\\W.*\\bS0?1\\s*E0?1\\b', null);
    const text = await page.evaluate(function (scope, section) {
        const h = window.__bfE2E, outer = h.disclosure(scope, section), container = outer && h.regionOf(outer);
        const group = container && h.disclosure(container, 'Check a title'), region = group && h.regionOf(group);
        return region ? region.textContent : '';
    }, ADMIN, tools);
    await t.check(checked.ok && /original language missing from metadata/i.test(text),
        'Check a title visibly explains the missing original-language metadata', page);
}

function pgChoiceStatus(scope, text) {
    const h = window.__bfE2E, root = h.root(scope);
    if (!root) return { total: 0, visible: 0 };
    const lines = Array.from(root.querySelectorAll('[role="status"]')).filter(n => n.textContent.trim() === text);
    return { total: lines.length, visible: lines.filter(h.vis).length };
}

async function choiceStatusFixtures(t, page, admin) {
    const endpoint = t.url('/Babelfin/' + (admin ? 'Admin' : 'Me') + '/ChoiceStatus');
    const scope = admin ? ADMIN : null;
    let pending = [];
    const stop = await page.mockResponses(endpoint, request => request.method === 'GET' ? new Promise(resolve => pending.push(resolve)) : null);
    t.defer('status fixture removed', async () => {
        pending.splice(0).forEach(resolve => resolve({ body: { HasChoices: true } }));
        await stop();
    });
    const load = admin ? () => openAdminPage(t, page) : () => openPersonPage(t, page);
    let count = 0;
    for (const answer of [{ body: { HasChoices: false } }, { body: { HasChoices: true } }, { status: 503, body: { Message: 'browser failure fixture' } },
        { status: 404, body: { Message: 'Optional endpoint absent in fixture' } }, { body: {} }]) {
        await load();
        await eventually(async () => pending.length, n => n === 1, 'pending choice-status request');
        await t.check((await page.evaluate(pgChoiceStatus, scope, NO_CHOICES_MESSAGE)).visible === 0,
            '' + (admin ? 'admin' : 'person') + ': no no-preferences claim while the response is delayed', page);
        const failure = answer.status >= 400 ? { owner: page, url: endpoint, status: answer.status, from: Date.now(), until: Infinity } : null;
        if (failure) t.expectedMockFailures.push(failure);
        pending.shift()(answer);
        count += 1;
        await sleep(t.settle);
        const state = await page.evaluate(pgChoiceStatus, scope, NO_CHOICES_MESSAGE);
        if (failure) failure.until = Date.now();
        const shouldShow = !answer.status && answer.body.HasChoices === false;
        await t.check(state.visible === (shouldShow ? 1 : 0), '' + (admin ? 'admin' : 'person') + ': '
            + (answer.status ? 'mock HTTP ' + answer.status + ' leaves the status unknown and hides the line'
                : shouldShow ? 'successful HasChoices=false shows the exact no-preferences line' : 'HasChoices=true or a malformed response never claims no preferences'), page);
    }
    const stats = await stop();
    await t.check(stats.fulfilled === count, 'every status result came from the intended loopback-only browser fixture');
}

async function testChoiceStatus(t) {
    requireLoopback(t);
    const page = t.page, kept = await keepPersonAndAdmin(t), viewer = await keepViewer(t);
    await protectOverrides(t, kept);
    const exportBefore = await t.api.get('/Babelfin/Admin/Export');
    if (exportBefore.status !== 200) throw new Error('cannot snapshot the rig settings');
    t.defer('complete rig settings restored', async () => {
        const r = await t.api.post('/Babelfin/Admin/Import', exportBefore.body, { dryRun: 'false' });
        if (r.status !== 200 || !(r.body.Applied ?? r.body.applied)) throw new Error('restore failed');
    });
    await t.api.del(ME_OVERRIDES + '/All');
    await putAdmin(t, neutralAdmin(kept.admin));
    const person = neutralPerson(kept.snapshot);
    await putMe(t, person);
    const vr = await t.api.put('/Babelfin/Admin/Users/' + t.ids.viewerId + '/Settings', neutralPerson(viewer));
    if (vr.status !== 200) throw new Error('Cannot prepare viewer');
    const expect = async (mine, all, why) => {
        const me = await t.api.get('/Babelfin/Me/ChoiceStatus'), admin = await t.api.get('/Babelfin/Admin/ChoiceStatus');
        await t.check(me.status === 200 && admin.status === 200 && me.body.HasChoices === mine && admin.body.HasChoices === all,
            'saved status: ' + why + ' (person ' + JSON.stringify(me.body) + ', admin ' + JSON.stringify(admin.body) + ')');
    };
    await expect(false, false, 'language lists alone leave both profiles neutral');
    Object.assign(person.Everyday, layerFor('video', 'video'));
    await putMe(t, person);
    await expect(false, false, 'explicit Video default stays neutral');
    await openPersonPage(t, page);
    await eventually(() => page.evaluate(pgChoiceStatus, null, NO_CHOICES_MESSAGE), state => state.visible === 1, 'successful neutral status line');
    await pickChoice(t, page, '.bf-table-section', SPEC.audioFor(SPEC.allLibraries), 'lang:ja');
    await t.check((await page.evaluate(pgChoiceStatus, null, NO_CHOICES_MESSAGE)).visible === 1,
        'an unsaved choice does not masquerade as a saved preference', page);
    await savePersonPage(t, page);
    await expect(true, true, 'a saved personal choice counts for the person and the administrator');
    await eventually(() => page.evaluate(pgChoiceStatus, null, NO_CHOICES_MESSAGE), state => state.visible === 0, 'status hidden after save');
    const chosenGenre = neutralPerson(kept.snapshot);
    chosenGenre.SmartRules = [genreRule(FIXTURE_GENRES[0], 'mine', undefined)];
    await putMe(t, chosenGenre);
    await expect(true, true, 'an active personal genre choice counts');
    chosenGenre.Enabled = false;
    await putMe(t, chosenGenre);
    await expect(true, true, 'saved preferences still exist when Babelfin is off for the person');
    const switchedOff = neutralAdmin(kept.admin);
    switchedOff.Enabled = false;
    await putAdmin(t, switchedOff);
    await expect(true, true, 'saved preferences still exist when Babelfin is off for the server');
    await putAdmin(t, neutralAdmin(kept.admin));
    chosenGenre.Enabled = true;
    chosenGenre.SmartRules[0].Enabled = false;
    await putMe(t, chosenGenre);
    await expect(false, false, 'a disabled genre row does not count as an active choice');
    const orphanId = 'b6b60000000040008000000000000099';
    const orphan = neutralPerson(kept.snapshot);
    orphan.PerLibrary = true;
    orphan.Libraries = [{ LibraryId: orphanId, Settings: layerFor('lang:ja', 'off') }];
    const imported = await t.api.post('/Babelfin/Admin/Import', { SchemaVersion: 1, Users: [orphan] }, { dryRun: 'false' });
    if (imported.status !== 200 || !(imported.body.Applied ?? imported.body.applied)) throw new Error('Cannot seed the historical removed-library fixture');
    await expect(false, false, 'a personal row for a removed library is not an applicable choice');
    const orphanAdmin = neutralAdmin(kept.admin);
    orphanAdmin.PerLibraryDefaults = true;
    orphanAdmin.Libraries = [{ LibraryId: orphanId, Settings: layerFor('lang:ja', 'off') }];
    await putAdmin(t, orphanAdmin);
    await expect(false, false, 'admin defaults for a removed library do not claim active preferences');
    await choiceStatusFixtures(t, page, false);
    await choiceStatusFixtures(t, page, true);
}

async function testGenreMigration(t) {
    requireLoopback(t);
    const page = t.page;
    const admin = clone(await getAdmin(t));
    t.defer('genre import report restored', () => putAdmin(t, admin));
    const created = await t.api.post('/Users/New', { Name: 'riglegacygenre', Password: '' });
    if (created.status !== 200 || !created.body.Id) throw new Error('Cannot create the isolated legacy-genre person: ' + created.status);
    const uid = compactId(created.body.Id);
    let original;
    const importPerson = async doc => {
        const r = await t.api.post('/Babelfin/Admin/Import', { SchemaVersion: 1, Users: [doc] }, { dryRun: 'false' });
        if (r.status !== 200 || !(r.body.Applied ?? r.body.applied)) throw new Error('legacy-person import failed: ' + JSON.stringify(r.body));
    };
    t.defer('migration person removed', async () => {
        try {
            // Clear the fixture's migration notes before deleting the Jellyfin account: its plugin file can outlive it.
            if (original) await importPerson(original);
        } finally {
            const r = await t.api.del('/Users/' + uid);
            if (r.status !== 200 && r.status !== 204) throw new Error('Cannot remove the migration fixture user: ' + r.status);
        }
    });
    original = await readPerson(t, uid);
    const empty = neutralPerson(original);
    empty.SchemaVersion = 2;
    empty.MigrationNotes = [];
    await importPerson(empty);
    let loaded = await readPerson(t, uid);
    await t.check(loaded.SchemaVersion === 3 && (loaded.MigrationNotes || []).length === 0,
        'an empty schema-2 person upgrades without a migration note');
    const legacy = neutralPerson(original), rule = genreRule(FIXTURE_GENRES[0], 'lang:ja', 'smart');
    rule.Id = 'b6b60000000040008000000000000001';
    rule.Name = 'Archived genre choices';
    rule.Conditions[0].Values = FIXTURE_GENRES.slice();
    rule.Settings.AvoidCommentary = true;
    legacy.SchemaVersion = 2;
    legacy.MigrationNotes = [];
    legacy.Retired = Object.assign({}, legacy.Retired, { GenreRules: [clone(rule)], Rules: [clone(rule)] });
    await importPerson(legacy);
    loaded = await readPerson(t, uid);
    const active = genreRules(loaded);
    await t.check(loaded.SchemaVersion === 3 && active.length === 2 && active.map(r => r.Conditions[0].Values[0]).join('|') === FIXTURE_GENRES.join('|')
        && active.every(r => same(tableOf(r.Settings), storedFor('lang:ja', 'smart')) && r.Settings.AvoidCommentary == null)
        && loaded.Retired.GenreRules.some(r => r.Settings.AvoidCommentary === true),
        'legacy OR-list genres become two visible rows; audio/subtitle choices survive and the complete original stays archived');
    const stable = doc => ({ schema: doc.SchemaVersion, rules: doc.SmartRules, retired: doc.Retired, notes: doc.MigrationNotes });
    const first = clone(stable(loaded));
    await importPerson(loaded);
    const again = await readPerson(t, uid);
    await t.check(same(first, stable(again)), 'reading and importing the upgraded person again adds no rows, IDs, archive copies or notes');
    await openEditor(t, page, uid);
    const shown = await page.evaluate(pgGenres, EDITOR);
    await t.check(shown?.rows.length === 2 && shown.rows.every(r => r.values.join('|') === 'lang:ja|smart'),
        'migrated genre rows and their choices appear in the real People editor', page);
}

async function testSettingsScreenshots(t) {
    requireLoopback(t);
    const page = t.page;
    const account = await t.api.get('/Users/Me'), users = await t.api.get('/Babelfin/Admin/Users'), allUsers = await t.api.get('/Users');
    if (account.status !== 200 || !/^rig/i.test(account.body.Name || '') || users.status !== 200 || !Array.isArray(users.body)
        || !users.body.every(u => /^rig/i.test(u.Name || u.UserName || ''))
        || allUsers.status !== 200 || !Array.isArray(allUsers.body) || !allUsers.body.length
        || !allUsers.body.every(u => /^rig/i.test(u.Name || ''))) {
        throw new Error('Settings screenshots require only synthetic rig accounts');
    }
    if (page.interceptors.length || page.mockResponders.length) throw new Error('Settings screenshots require real unmodified responses');
    if (!t.ids.viewerId || compactId(t.ids.viewerId) === compactId(t.api.userId)) {
        throw new Error('Settings screenshots require distinct synthetic admin and viewer accounts');
    }
    const reviewPeople = [{ id: compactId(t.api.userId), name: 'Alex' }, { id: compactId(t.ids.viewerId), name: 'Sam' }];
    for (const person of reviewPeople) {
        const user = await t.api.get('/Users/' + person.id);
        if (user.status !== 200 || compactId(user.body?.Id) !== person.id || !/^rig/i.test(user.body?.Name || '') || !user.body.Configuration) {
            throw new Error('Cannot snapshot a synthetic screenshot account and its configuration');
        }
        person.original = clone(user.body);
        // UserController.UpdateUser accepts a UserDto at POST /Users?userId=... and also saves Configuration.
        // Preserve the complete original DTO so a name-only fixture cannot reset playback preferences.
        person.save = async body => {
            const r = await t.api.post('/Users', body, { userId: person.id });
            if (r.status !== 200 && r.status !== 204) throw new Error('Cannot update the screenshot account: ' + r.status);
        };
    }
    // Register first: Jellyfin preferences are restored last, after Babelfin settings can adjust remember flags.
    for (const person of reviewPeople) {
        t.defer('screenshot Jellyfin account and preferences restored: ' + person.original.Name, async () => {
            await person.save(person.original);
            const restored = await t.api.get('/Users/' + person.id);
            await t.check(restored.status === 200 && restored.body.Name === person.original.Name
                && same(restored.body.Configuration, person.original.Configuration) && same(restored.body.Policy, person.original.Policy),
                'screenshot fixture restores the original account name, configuration and policy: ' + person.original.Name);
        });
    }
    const config = await t.api.get('/System/Configuration'), publicInfo = await t.api.get('/System/Info/Public');
    if (config.status !== 200 || publicInfo.status !== 200) throw new Error('Cannot snapshot the isolated screenshot server configuration');
    const originalConfig = clone(config.body), originalName = publicInfo.body.ServerName || '';
    const displayName = 'Babelfin Test Server';
    t.defer('screenshot server configuration restored', async () => {
        const restored = await t.api.post('/System/Configuration', originalConfig);
        const now = await t.api.get('/System/Configuration');
        await t.check((restored.status === 200 || restored.status === 204) && now.status === 200 && now.body.ServerName === originalConfig.ServerName,
            'screenshot fixture restores the original loopback server display name');
    });
    const named = await t.api.post('/System/Configuration', Object.assign(clone(originalConfig), { ServerName: displayName }));
    if (named.status !== 200 && named.status !== 204) throw new Error('Cannot set the synthetic screenshot server display name');
    await eventually(async () => {
        const current = await t.api.get('/System/Info/Public');
        return current.status === 200 && current.body.ServerName === displayName;
    }, Boolean, 'the synthetic server display name is applied');
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    const viewer = await keepViewer(t);
    for (const person of reviewPeople) {
        // LIFO undo restores the name before Babelfin settings are saved under that user's current name.
        t.defer('screenshot account name restored: ' + person.original.Name, () => person.save(person.original));
        await person.save(Object.assign(clone(person.original), { Name: person.name }));
        await eventually(() => t.api.get('/Users/' + person.id), r => r.status === 200 && r.body.Name === person.name
            && same(r.body.Configuration, person.original.Configuration) && same(r.body.Policy, person.original.Policy),
            'the real screenshot account is named ' + person.name + ' with its preferences preserved');
    }
    await eventually(() => t.api.get('/Babelfin/Admin/Users'), r => r.status === 200 && Array.isArray(r.body)
        && reviewPeople.every(person => r.body.some(u => compactId(u.UserId) === person.id && u.Name === person.name)),
        'the real People API shows Alex and Sam');
    const reviewGenres = ['Animation', 'Science Fiction'];
    await t.api.del(ME_OVERRIDES + '/All');
    await genreFixture(t, reviewGenres);
    const person = neutralPerson(kept.snapshot);
    Object.assign(person.Everyday, layerFor('mine', 'smart'), { Understood: ['en', 'ja'] });
    person.SmartRules = [genreRule(reviewGenres[0], 'original', 'lang:en'), genreRule(reviewGenres[1], 'mine', 'off')];
    await putMe(t, person);
    const admin = neutralAdmin(kept.admin);
    admin.PerLibraryDefaults = true;
    admin.Libraries = [
        { LibraryId: compactId(t.ids.libraries.Anime), Settings: layerFor('original', 'smart') },
        { LibraryId: compactId(t.ids.libraries.Movies), Settings: layerFor('mine', 'off') }
    ];
    admin.SmartRules = [genreRule(reviewGenres[0], 'original', undefined), genreRule(reviewGenres[1], 'mine', 'lang:en')];
    admin.LastUpgradeNote = '';
    await putAdmin(t, admin);
    const edited = neutralPerson(viewer);
    Object.assign(edited.Everyday, layerFor('original', 'lang:en'));
    edited.SmartRules = [genreRule(reviewGenres[0], 'mine', 'smart'), genreRule(reviewGenres[1], 'original', 'off')];
    const prepared = await t.api.put('/Babelfin/Admin/Users/' + t.ids.viewerId + '/Settings', edited);
    if (prepared.status !== 200) throw new Error('Cannot prepare the People editor Settings screenshot');
    const layout = await currentLayout(page);
    t.defer('Settings screenshot layout restored', () => setLayout(t, layout));
    t.defer('Settings screenshot viewport restored', () => page.clearViewport());
    await setLayout(t, '');
    const capture = async (name, full, scope) => {
        if (full) await page.evaluate(function () { window.scrollTo(0, 0); });
        await page.evaluate(async function () { if (document.fonts) await document.fonts.ready; return true; });
        await sleep(200);
        const region = await page.evaluate(function (selector, privateName, safeName, fullPage) {
            const root = selector ? document.querySelector(selector) : document.body;
            if (!root) throw new Error('Screenshot region is missing');
            const rect = root.getBoundingClientRect(), heading = selector && root.querySelector('.bf-head');
            const top = heading ? Math.max(rect.top, heading.getBoundingClientRect().top) : rect.top;
            const clip = selector ? { x: Math.max(0, rect.left + scrollX), y: Math.max(0, top + scrollY),
                width: Math.ceil(rect.width), height: Math.ceil(rect.bottom - top), scale: 1 } : null;
            const bounds = clip || (fullPage ? { x: 0, y: 0,
                width: Math.max(document.documentElement.scrollWidth, document.body.scrollWidth),
                height: Math.min(16000, Math.max(document.documentElement.scrollHeight, document.body.scrollHeight)) }
                : { x: scrollX, y: scrollY, width: innerWidth, height: innerHeight });
            let unsafe = /MediaBrowser\s+Token=|Bearer\s+[a-z0-9._-]{12,}|AccessToken\s*[:=]|api_key=/i.test(root.innerText);
            if (privateName && privateName.toLowerCase() !== safeName.toLowerCase()) {
                const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                    if (!node.textContent.toLowerCase().includes(privateName.toLowerCase())) continue;
                    const range = document.createRange(); range.selectNodeContents(node);
                    const box = range.getBoundingClientRect();
                    if (box.width && box.height && box.right + scrollX > bounds.x && box.left + scrollX < bounds.x + bounds.width
                        && box.bottom + scrollY > bounds.y && box.top + scrollY < bounds.y + bounds.height) unsafe = true;
                }
            }
            return { clip, unsafe };
        }, scope || null, originalName, displayName, full);
        if (region.unsafe) throw new Error('Refusing to capture credentials or a private server display name');
        const output = path.join(t.opts.screenshotsDir, name + '.png');
        let saved;
        if (region.clip) {
            // Capture the actual panel/dialog pixels directly; the native app header lies outside these bounds.
            const shot = await page.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip: region.clip });
            fs.mkdirSync(path.dirname(output), { recursive: true });
            fs.writeFileSync(output, Buffer.from(shot.data, 'base64'));
            saved = output;
        } else saved = await page.screenshot(output, full);
        await t.check(!!saved, 'real CDP Settings screenshot saved: ' + output, page);
    };
    const genresVisible = function (scope, names) {
        const rows = Array.from(document.querySelectorAll(scope + ' .bfui-genres tbody tr th'));
        return rows.length === names.length && rows.every((row, i) => row.textContent.trim() === names[i]);
    };
    for (const viewport of [{ name: 'desktop', width: 1280, mobile: false }, { name: '375', width: 375, mobile: true }]) {
        await page.setViewport(viewport.width, 900, viewport.mobile);
        await openPersonPage(t, page);
        await page.waitFor(genresVisible, { what: 'the saved Animation and Science Fiction person genre rows' }, '.bf-table-section', reviewGenres);
        await capture('person-settings-genres-' + viewport.name, true);
        await openEditor(t, page, t.ids.viewerId);
        await page.waitFor(genresVisible, { what: 'the saved Animation and Science Fiction admin genre rows' }, LIBS, reviewGenres);
        await page.waitFor(genresVisible, { what: 'the saved Animation and Science Fiction People editor genre rows' }, EDITOR, reviewGenres);
        await page.waitFor(function (scope, people) {
            return people.every(person => {
                const row = document.querySelector(scope + ' [data-section="people"] tr[data-user="' + person.id + '"] th');
                return row && row.textContent.trim() === person.name;
            });
        }, { what: 'the real Alex and Sam People rows' }, ADMIN, reviewPeople.map(({ id, name }) => ({ id, name })));
        await capture('admin-defaults-people-' + viewport.name, true, ADMIN + ' .content-primary');
        const dialog = await openItemMenuDialog(t, page, t.ids.seriesId);
        if (!dialog) throw new Error('The real override dialog did not open for the screenshot');
        if (!dialog.burned || dialog.burned.label !== overrideWord('dialog.burnedIn.show') || dialog.burned.aria !== overrideWord('dialog.burnedIn.show')) {
            throw new Error('The Settings screenshot requires the current whole-show burned-in wording');
        }
        await pickChoice(t, page, '.bfDialog', 'Audio', 'original');
        await capture('override-dialog-' + viewport.name, false, viewport.mobile ? null : '.bfDialog');
        await escapeBfDialog(page, null);
    }
    t.note('Settings PNGs are staged outside the public tree: ' + t.opts.screenshotsDir);
}


/** The checked rows in Jellyfin's native player track menu, without choosing or changing a track. */
function pgPlayerTrackMenu() {
    const h = window.__bfE2E;
    const sheet = Array.from(document.querySelectorAll('.actionSheet')).find(h.vis);
    if (!sheet) return null;
    const rows = Array.from(sheet.querySelectorAll('.actionSheetMenuItem')).filter(h.vis);
    if (!rows.length) return null;
    return rows.map(row => {
        const check = row.querySelector('.material-icons.check');
        return { index: Number(row.getAttribute('data-id')), text: row.textContent.trim(),
            selected: !!check && h.vis(check) && getComputedStyle(check).visibility !== 'hidden' };
    });
}

async function testForcedOnlySubtitles(t) {
    requireLoopback(t);
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    const uid = compactId(t.api.userId), seriesId = t.ids.series['Quorim Test'];
    if (!seriesId || !t.ids.libraries.Anime) throw new Error('The forced-only regression needs the generated Quorim Test in Anime');
    const episodes = await t.api.get('/Shows/' + compactId(seriesId) + '/Episodes', { userId: uid });
    const episode = (episodes.body?.Items || []).find(e => e.ParentIndexNumber === 1 && e.IndexNumber === 1);
    if (episodes.status !== 200 || !episode) throw new Error('Cannot find the forced-only episode fixture');
    const id = compactId(episode.Id);
    const account = await t.api.get('/Users/' + uid);
    const item = await t.api.get('/Users/' + uid + '/Items/' + id);
    if (account.status !== 200 || !account.body.Configuration || item.status !== 200 || !item.body.UserData) {
        throw new Error('Cannot snapshot the playback configuration and episode watch state');
    }
    const configuration = clone(account.body.Configuration), userData = clone(item.body.UserData);
    t.defer('forced-only Jellyfin playback configuration restored', async () => {
        const r = await t.api.post('/Users/' + uid + '/Configuration', configuration);
        const actual = await t.api.get('/Users/' + uid);
        await t.check((r.status === 200 || r.status === 204) && actual.status === 200 && same(actual.body.Configuration, configuration),
            'Jellyfin playback configuration is restored after the forced-only regression');
    });
    t.defer('forced-only episode watch state restored', async () => {
        // Jellyfin's UserData update keeps the existing LastPlayedDate when null is supplied.
        // Its native mark-unplayed route clears that timestamp before the complete snapshot is reapplied.
        if (userData.LastPlayedDate == null) {
            const cleared = await t.api.del('/UserPlayedItems/' + id, { userId: uid });
            if (cleared.status !== 200 && cleared.status !== 204) throw new Error('Cannot clear the fixture last-played timestamp: ' + cleared.status);
        }
        const r = await t.api.post('/UserItems/' + id + '/UserData', userData, { userId: uid });
        const actual = await t.api.get('/Users/' + uid + '/Items/' + id);
        const fields = ['Played', 'PlayCount', 'PlaybackPositionTicks', 'LastPlayedDate', 'AudioStreamIndex', 'SubtitleStreamIndex'];
        const changed = fields.filter(key => !same(actual.body?.UserData?.[key], userData[key]));
        await t.check(r.status === 200 && actual.status === 200 && !changed.length,
            'Quorim Test watched, resume and remembered-track state is restored'
            + (changed.length ? ' (differs: ' + changed.map(key => key + ': ' + canon(userData[key]) + ' -> ' + canon(actual.body?.UserData?.[key])).join(', ') + ')' : ''));
    });
    const configured = await t.api.post('/Users/' + uid + '/Configuration', Object.assign({}, configuration, {
        SubtitleMode: 'None', SubtitleLanguagePreference: 'eng', AudioLanguagePreference: 'eng', PlayDefaultAudioTrack: true,
        RememberAudioSelections: false, RememberSubtitleSelections: false, EnableNextEpisodeAutoPlay: false
    }));
    if (configured.status !== 200 && configured.status !== 204) throw new Error('Cannot configure the Jellyfin playback fixture');
    const admin = neutralAdmin(kept.admin);
    admin.DryRun = false;
    admin.CompatibilityMode = false;
    // Correcting the player later would hide a failure in the initial PlaybackInfo response.
    admin.CorrectAfterStart = 'Off';
    admin.UnknownAppMode = 'ReplaceUnlessManual';
    admin.AppModes = (admin.AppModes || []).map(row => Object.assign({}, row, { Mode: 'ReplaceUnlessManual', RememberPrePlayChoices: false }));
    await putAdmin(t, admin, 'initial playback choices without later correction');
    const person = neutralPerson(kept.snapshot);
    person.PerLibrary = true;
    person.Libraries = [{ LibraryId: compactId(t.ids.libraries.Anime), Settings: layerFor('lang:ja', 'lang:en') }];
    person.Everyday.Memory = 'DontRemember';
    person.Everyday.PrefersSdh = false;
    person.Everyday.IfNothingMatches = 'ShowNothing';
    await putMe(t, person, 'Anime uses Japanese audio and English full subtitles');
    const cleared = await t.api.del(ME_OVERRIDES + '/All');
    if (cleared.status !== 204) throw new Error('Cannot clear the forced-only fixture overrides: ' + cleared.status);
    const response = await t.api.post('/Items/' + id + '/PlaybackInfo', { UserId: uid }, { userId: uid });
    const source = response.body?.MediaSources?.[0], streams = source?.MediaStreams || [];
    const subtitles = streams.filter(s => s.Type === 'Subtitle');
    const english = streams.find(s => s.Type === 'Audio' && /^(en|eng)$/i.test(s.Language));
    const japanese = streams.find(s => s.Type === 'Audio' && /^(ja|jpn)$/i.test(s.Language));
    const subtitle = subtitles[0], video = streams.find(s => s.Type === 'Video');
    const shape = response.status === 200 && subtitles.length === 1 && subtitle.Index === 0 && subtitle.IsForced && subtitle.IsDefault
        && /^(en|eng)$/i.test(subtitle.Language) && /^ass$/i.test(subtitle.Codec) && video?.Index === 1
        && english?.Index === 2 && english.IsDefault && japanese?.Index === 3 && source.RunTimeTicks >= 150000000;
    if (!await t.check(shape, 'Quorim Test has only forced/default English ASS at index 0, video 1, default English audio 2 and Japanese audio 3, lasting at least 15 seconds')) return;
    await t.check(source.DefaultAudioStreamIndex === japanese.Index && source.DefaultSubtitleStreamIndex === 0,
        'PlaybackInfo selects Japanese audio 3 and English subtitle 0 for the Anime full-subtitle request ('
        + source.DefaultAudioStreamIndex + '/' + source.DefaultSubtitleStreamIndex + ')');

    const layout = await currentLayout(t.page);
    t.defer('forced-only browser layout restored', () => setLayout(t, layout));
    await setLayout(t, '');
    const player = await t.newPage();
    if (player.interceptors.length || player.mockResponders.length) throw new Error('The player regression requires unmodified Jellyfin responses');
    // Closing the real player must finish before restoring watch state or settings.
    t.defer('forced-only browser playback stopped', async () => {
        try {
            await player.navigate(t.url('/web/#/home'));
            await eventually(() => t.api.get('/Sessions'), r => r.status === 200
                && !r.body.some(session => compactId(session.NowPlayingItem?.Id || '') === id), 'the browser playback session has stopped');
        } finally {
            await t.chrome.closePage(player);
        }
    });
    await player.navigate(t.url('/web/#/details?id=' + id));
    await player.waitForSelector(DETAIL_PAGE + ' .btnPlay', { timeout: 30000 });
    await player.click(DETAIL_PAGE + ' .btnPlay', { what: 'Play Quorim Test in the real browser player' });
    await player.waitFor(function () {
        const video = Array.from(document.querySelectorAll('video')).find(v => v.readyState >= 2 && !v.paused && v.currentTime > 0);
        return video && { currentTime: video.currentTime, width: video.videoWidth, height: video.videoHeight };
    }, { timeout: 30000, what: 'Quorim Test video actually playing' });
    await player.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 220, y: 220, button: 'none' });
    await player.click('#videoOsdPage .btnPause', { what: 'Pause the playing fixture while inspecting its selected tracks' });
    await player.waitFor(function () { return Array.from(document.querySelectorAll('video')).some(v => v.readyState >= 2 && v.paused && v.currentTime > 0); },
        { what: 'the real player paused after playback started' });
    await t.check(player.requests.some(r => r.method === 'POST' && new URL(r.url).pathname.toLowerCase() === '/items/' + id + '/playbackinfo'),
        'The browser player requested actual Jellyfin PlaybackInfo for Quorim Test', player);
    for (const track of [{ button: '.btnAudio', name: 'audio', expected: japanese.Index }, { button: '.btnSubtitles', name: 'subtitles', expected: 0 }]) {
        await player.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 240 + track.expected, y: 220, button: 'none' });
        await player.click('#videoOsdPage ' + track.button, { what: 'Open the native player ' + track.name + ' menu' });
        const menu = await player.waitFor(pgPlayerTrackMenu, { timeout: 8000, what: 'the native player ' + track.name + ' track entries' });
        const selected = menu.filter(row => row.selected);
        await t.check(selected.length === 1 && selected[0].index === track.expected,
            'The real player ' + track.name + ' menu checks index ' + track.expected + ' ('
            + menu.map(row => row.index + (row.selected ? ' selected' : '') + ': ' + row.text).join('; ') + ')', player);
        if (!await closeSheetByBackdrop(player)) throw new Error('Cannot close the native player track menu');
    }
    const playing = await eventually(() => t.api.get('/Sessions'), r => r.status === 200
        && r.body.some(session => compactId(session.NowPlayingItem?.Id || '') === id
            && session.PlayState?.AudioStreamIndex === japanese.Index && session.PlayState?.SubtitleStreamIndex === 0),
        'the browser reports Japanese audio and subtitle index zero to Jellyfin');
    await t.check(playing.status === 200, 'Jellyfin receives the browser player selection: Japanese audio 3 and English subtitle 0', player);
}


async function testBurnedFlagRemoval(t) {
    requireLoopback(t);
    const kept = await keepPersonAndAdmin(t);
    await protectOverrides(t, kept);
    const page = t.page;
    const targets = [
        { id: t.ids.episodeId, scope: 'Title' },
        { id: t.ids.seasonId, scope: 'Season' },
        { id: t.ids.seriesId, scope: 'Series' }
    ];
    for (const target of targets) {
        if (!target.id) throw new Error('Missing burned-in removal fixture: ' + target.scope);
        const doc = clone(kept.snapshot);
        doc.BurnedIn = [{ ItemId: compactId(target.id), Scope: target.scope, Languages: ['en'], Name: 'Burned-in removal fixture' }];
        await putMe(t, doc, 'seed a stored ' + target.scope + ' burned-in flag');
        await openPersonPage(t, page);
        await openEverything(page, false);
        const value = doc.Everyday?.Memory === 'TitleOnly' ? 'RestOfShow' : 'TitleOnly';
        const edit = await page.evaluate(pgSetMenu, null, { field: 'Memory', label: 'Remember track changes for' }, value, null);
        if (!edit.ok) throw new Error('Cannot make an unrelated pending edit: ' + edit.reason);
        const requestStart = page.requests.length;
        await page.click('.bf-burned .bf-item button', { what: 'Remove the stored ' + target.scope + ' burned-in flag' });
        await page.waitFor(function () {
            return !document.querySelector('.bf-burned .bf-item button');
        }, { timeout: 10000, what: 'the deleted burned-in row to disappear' });
        const immediate = (await getMe(t)).Settings;
        const deletes = page.requests.slice(requestStart).filter(r => r.method === 'DELETE' && /\/Babelfin\/Me\/BurnedIn\/[0-9a-f]{32}/.test(r.url));
        await t.check(deletes.length === 1 && new URL(deletes[0].url).searchParams.get('scope') === target.scope && !immediate.BurnedIn?.length,
            target.scope + ': Remove sends exactly one scoped DELETE and removes the stored flag before Save', page);
        await t.check((await barOf(page, false)).visible && immediate.Everyday?.Memory === doc.Everyday?.Memory,
            target.scope + ': removing a stored flag keeps the unrelated edit unsaved', page);
        if (target.scope === 'Season') {
            await page.click(pgButton, { args: ['.bf-savebar', 'Discard'], what: 'Discard the unrelated edit after removing the season flag' });
            if (await waitDialog(page, 800)) await page.click(pgDialogButton, { args: ['Discard'], what: 'Confirm Discard' });
        } else {
            await savePersonPage(t, page);
        }
        await reloadPersonPage(t, page);
        const final = (await getMe(t)).Settings;
        await t.check(!final.BurnedIn?.length && !await page.evaluate(function () { return !!document.querySelector('.bf-burned .bf-item button'); }),
            target.scope + ': Save/Discard and reload never bring the removed flag back', page);
        if (target.scope !== 'Season') await t.check((final.Everyday?.Memory || 'RestOfShow') === value,
            target.scope + ': Save still persists the unrelated edited field', page);
    }
}


async function testAdminLeaveGuard(t, options = {}) {
    requireLoopback(t);
    const before = await getAdmin(t);
    const page = options.page || t.page;
    if (options.back) { await page.navigate(t.url('/web/#/home')); await waitWebReady(t, page); }
    await openAdminPage(t, page);
    if (options.withoutNavigation) await t.check(!await page.evaluate('!!window.navigation'), 'the legacy-navigation case runs without the Navigation API', page);
    await openEverything(page, true);
    const menu = await page.evaluate(pgControl, ADMIN, { field: 'UnknownAppMode', label: ADMIN_MENUS.notListed.label }, null);
    const other = menu && otherOptions(menu, [])[0];
    if (!other) throw new Error('No alternate app mode for the unsaved admin edit');
    const changed = await page.evaluate(pgSetMenu, ADMIN, { field: 'UnknownAppMode', label: ADMIN_MENUS.notListed.label }, other.v, null);
    if (!changed.ok) throw new Error('Cannot edit the app mode: ' + changed.reason);
    const originalHash = await page.evaluate('location.hash');
    const requestStart = page.requests.length;
    const destination = options.sidebar ? '#/dashboard' : '#/home';
    const navigate = async () => {
        if (options.sidebar) {
            await page.click(function () {
                return Array.from(document.querySelectorAll('[aria-labelledby="server-subheader"] a[href]')).find(function (a) {
                    const rect = a.getBoundingClientRect();
                    return /#\/dashboard\/?$/.test(a.href) && rect.width > 0 && rect.height > 0 && !a.closest('.hide');
                }) || null;
            }, { what: 'the visible Jellyfin dashboard sidebar link' });
            return;
        }
        return page.evaluate(function (back, repeated) {
        if (back) { history.back(); }
        else { location.hash = '#/home'; if (repeated) { location.hash = '#/dashboard'; } }
    }, !!options.back, !!options.repeated);
    };
    await navigate();
    const cancel = await waitDialog(page, 5000);
    await t.check(!!cancel, options.sidebar ? 'clicking the actual sidebar link prompts before the router tears down the page' : 'an unsaved admin edit prompts before browser hash navigation', page);
    if (!cancel) return;
    await answerDialog(page, false);
    await sleep(400);
    const still = await page.evaluate(pgControl, ADMIN, { field: 'UnknownAppMode', label: ADMIN_MENUS.notListed.label }, null);
    await t.check(await page.evaluate('location.hash') === originalHash && still?.value === other.v && (await barOf(page, true)).visible,
        'Cancel stays on the admin page with the unsaved edit intact', page);
    const tools = await toolsTitle(page);
    await ensureGroup(page, ADMIN, tools, SPEC.toolsGroups[0], true);
    const person = await page.evaluate(pgToolPickPerson, ADMIN, tools, SPEC.toolsGroups[0], t.opts.user);
    const focused = person.ok && await page.evaluate(pgToolFocusSearch, ADMIN, tools, SPEC.toolsGroups[0]);
    if (!focused?.ok) throw new Error('Cannot focus Check a title search');
    await page.type('Astercoil');
    await sleep(700);
    await page.press('Escape');
    await sleep(300);
    await t.check(await page.evaluate('location.hash') === originalHash && !await waitDialog(page, 300),
        'Escape inside Check a title stays on the admin page without opening a leave prompt', page);
    await navigate();
    const discard = await waitDialog(page, 5000);
    await t.check(!!discard, 'the pending admin edit still prompts on a later navigation attempt', page);
    if (!discard) return;
    await answerDialog(page, true);
    await page.waitFor(function (target) { return location.hash === target; }, { timeout: 10000, what: 'Discard to finish the requested navigation' }, destination);
    await sleep(500);
    await t.check(await page.evaluate('location.hash') === destination, 'Discard resumes only the first requested destination', page);
    const writes = page.requests.slice(requestStart).filter(r => r.method === 'PUT' && /\/Babelfin\/Admin\/Settings(?:\?|$)/.test(r.url));
    await t.check(!writes.length && (await getAdmin(t)).UnknownAppMode === before.UnknownAppMode,
        'Discard leaves without writing the pending administrator setting', page);
}


async function testLegacyAdminNavigation(t) {
    for (const back of [false, true]) {
        const page = await t.newPage();
        try {
            await page.send('Page.addScriptToEvaluateOnNewDocument', {
                source: "Object.defineProperty(window, 'navigation', { configurable: true, value: undefined });"
            });
            await testAdminLeaveGuard(t, { page, withoutNavigation: true, back });
        } finally { await t.chrome.closePage(page); }
    }
}

async function testSidebarAdminNavigation(t) {
    await testAdminLeaveGuard(t, { sidebar: true });
}

async function testRepeatedAdminNavigation(t) {
    const page = await t.newPage();
    try { await testAdminLeaveGuard(t, { page, repeated: true }); }
    finally { await t.chrome.closePage(page); }
}


/** Actual competing administrator/person HTTP saves, with browser conflict decisions. */
async function testConcurrentFieldChoices(t) {
    const page = t.page, kept = await keepPersonAndAdmin(t);
    const person = neutralPerson(kept.snapshot);
    person.PerLibrary = false;
    person.Everyday = Object.assign({}, person.Everyday, layerFor('lang:ja', 'lang:en'));
    await putMe(t, person, 'concurrent-save fixture');
    await openPersonPage(t, page);
    await waitMode(page, null, SPEC.modeSame, [SPEC.allLibraries]);
    const observed = [];
    const stop = await page.mockResponses(t.url('/Babelfin/Me/Settings') + '*', request => {
        if (request.method === 'PUT') {
            const payload = JSON.parse(request.body);
            const expected = payload.ExpectedValues;
            observed.push({
                body: Object.prototype.hasOwnProperty.call(payload, 'ExpectedValues'),
                legacyHeader: Object.keys(request.headers || {}).some(key => key.toLowerCase() === 'x-babelfin-expected'),
                exactFields: !!expected && typeof expected === 'object' && !Array.isArray(expected)
                    && same(Object.keys(expected).sort(), (new URL(request.url).searchParams.get('fields') || '').split(',').sort())
            });
        }
        return null; // Observe only; the actual server decides every response.
    });
    t.defer('stop edited-field expectation observation', stop);
    const audio = SPEC.audioFor(SPEC.allLibraries), subtitles = SPEC.subtitlesFor(SPEC.allLibraries);
    async function changeElsewhere(language) {
        const result = await t.api.put('/Babelfin/Admin/Users/' + compactId(t.api.userId) + '/Settings?fields=everyday.subtitleLanguages',
            { Everyday: { SubtitleLanguages: [language] } });
        if (result.status !== 200) throw new Error('The competing administrator save failed: ' + result.status);
    }
    async function conflict() {
        const mark = page.badResponses.length, from = Date.now();
        await page.click(pgButton, { args: ['.bf-savebar', 'Save'], what: 'Save a deliberately conflicting draft' });
        await page.waitFor(function () { return !!document.querySelector('.bf-conflict [data-conflict="reload"]'); },
            { timeout: 10000, what: 'the real field-conflict response and its choices' });
        const failures = page.badResponses.slice(mark).filter(row => row.status === 409 && /\/Babelfin\/Me\/Settings(?:\?|$)/.test(row.url));
        await t.check(failures.length === 1, 'one actual competing-field save returns 409', page);
        failures.forEach(row => t.expectedMockFailures.push({ owner: page, url: row.url, status: 409, from, until: Date.now() + 250 }));
    }
    await pickChoice(t, page, '.bf-table-section', audio, 'lang:fr');
    await pickChoice(t, page, '.bf-table-section', subtitles, 'lang:de');
    await changeElsewhere('it');
    await conflict();
    const latest = (await getMe(t)).Settings;
    await t.check(same(latest.Everyday.SubtitleLanguages, ['it']) && latest.Everyday.AudioChain.some(step => step.Language === 'ja'),
        'a conflicting person save changes neither the administrator subtitle nor the unrelated audio');
    await page.click('.bf-conflict [data-conflict="reload"]', { what: 'Reload only the conflicting subtitle field' });
    await page.waitFor(function () { return !document.querySelector('.bf-conflict'); }, { what: 'the conflict reload finishes' });
    const after = await snap(page, null);
    await t.check(byLabel(after, subtitles)?.value === 'lang:it' && byLabel(after, audio)?.value === 'lang:fr',
        'Reload shows the latest subtitles and retains the unrelated unsaved audio', page);
    await savePersonPage(t, page);
    const saved = (await getMe(t)).Settings;
    await t.check(same(saved.Everyday.SubtitleLanguages, ['it']) && saved.Everyday.AudioChain.some(step => step.Language === 'fr'),
        'saving the retained audio uses the refreshed baseline and preserves administrator subtitles');
    await pickChoice(t, page, '.bf-table-section', subtitles, 'lang:de');
    await changeElsewhere('en');
    await conflict();
    await page.click('.bf-conflict [data-conflict="keep"]', { what: 'Keep the person subtitle choice explicitly' });
    await page.waitFor(function () {
        const bar = document.querySelector('.bf-savebar');
        return !document.querySelector('.bf-conflict') && bar && (bar.hidden || bar.classList.contains('hide') || getComputedStyle(bar).display === 'none');
    }, { timeout: 15000, what: 'the explicitly forced save finishes' });
    await t.check(same((await getMe(t)).Settings.Everyday.SubtitleLanguages, ['de']), 'Keep mine explicitly replaces the competing subtitle choice');
    await pickChoice(t, page, '.bf-table-section', subtitles, 'lang:it');
    await savePersonPage(t, page);
    await t.check(same(observed.map(row => row.body), [true, true, true, false, true]),
        'normal saves send original values in the body; Keep mine omits them once; protection resumes afterward (' + JSON.stringify(observed) + ')');
    await t.check(observed.every(row => !row.legacyHeader && (!row.body || row.exactFields)),
        'body expectations cover exactly the edited fields and use no legacy header');
}

const TESTS = [
    { n: 1, id: 'signin', title: 'Sign in through the real login page', run: testSignIn, noSignIn: true },
    { n: 2, id: 'menu', title: 'Modern layout: avatar menu', run: testModernMenu },
    { n: 3, id: 'legacy', title: 'Legacy layouts: desktop-legacy and tv', run: testLegacyLayouts },
    { n: 4, id: 'mode', title: 'Person page: "Same for every library" / "Choose for each library"', run: testPersonMode },
    { n: 5, id: 'table', title: 'Person page: the dropdowns in each mode, keyboard sweeps', run: testPersonTable },
    { n: 6, id: 'defaults', title: 'Default labels', run: testDefaultLabels },
    { n: 7, id: 'effect', title: 'Server effect of each mode: Me/Choice on the test media', run: testServerEffect },
    { n: 8, id: 'advanced', title: 'Advanced, Troubleshooting, Back up and restore: collapsible groups, removed controls, Start over', run: testAdvanced },
    { n: 9, id: 'admin', title: 'Admin page: section order, Defaults for everyone and People tables in each mode', run: testAdminPage },
    { n: 10, id: 'tools', title: 'Troubleshooting: Check a title, Activity; Back up and restore; no Coverage', run: testTools },
    { n: 11, id: 'item', title: 'Item pages: ⋮ dialog', run: testItemPages },
    { n: 12, id: 'phone', title: 'Phone width (375x812)', run: testPhoneWidth },
    { n: 14, id: 'menus', title: 'Plain option menus on both pages; defaults from AdvancedDefaults and Admin/Defaults; picking the default clears', run: testPlainMenus },
    { n: 15, id: 'restore', title: 'Restore defaults on both pages, with Cancel', run: testRestoreDefaults },
    { n: 16, id: 'people', title: 'People table: Name, Audio, Subtitles, Edit from GET Admin/Users', run: testPeopleTable },
    { n: 17, id: 'newusers', title: 'New users: only My languages, no template table fields', run: testNewUsers },
    { n: 18, id: 'wording', title: 'Wording: "My languages"', run: testWording },
    { n: 19, id: 'v8', aliases: ['v7', 'v6'], title: 'Settings version 8 and the Jellyfin Desktop row', run: testServerV8 },
    { n: 20, id: 'alignment', title: 'Alignment: the text of every button centred, desktop and phone width', run: testAlignment },
    { n: 21, id: 'route', title: 'Babelfin Settings at #/babelfin under Jellyfin\'s header, every layout; unsaved changes asked about', run: testSettingsRoute },
    { n: 22, id: 'info', title: 'An explanation ("i") for every option on both pages: text, wiring, hover, click, Escape', run: testInfoTips },
    { n: 23, id: 'reset', title: 'Reset all settings: Cancel, reset, the backup file, and restoring it', run: testResetAll },
    { n: 24, id: 'itemmenu', title: '⋮ menu: "Audio and subtitles (Babelfin)" in the item menus of every layout and the dialog it opens', run: testMenuEntry },
    { n: 25, id: 'overrides', title: 'Show and movie overrides: the section on the person page, at #/babelfin and in the People editor', run: testOverridesSection },
    { n: 26, id: 'preplay-memory', title: 'Per-app pre-play memory: shipped values, accessible checkbox, save and reload', run: testPrePlayChoices },
    { n: 27, id: 'partial-reset', title: 'Partial reset failure UI (loopback browser mock)', run: testResetPartial },
    { n: 28, id: 'upgrade-note', title: 'One-time admin migration note (loopback browser fixtures)', run: testMigrationNote },
    { n: 29, id: 'smart-subtitles', title: 'PlaybackInfo recomputes Jellyfin Smart subtitles for the selected audio', run: testSmartSubtitleRecompute },
    { n: 30, id: 'audio-choices', title: 'Two explicit audio choices in person, library, admin, People and item tables', run: testAudioChoices },
    { n: 31, id: 'genres', title: 'Genre editors: visible catalogue, add/remove/order/save, limited viewer, no New users editor', run: testGenreEditor },
    { n: 32, id: 'genre-precedence', title: 'Fieldwise genre precedence and first-match ordering through real playback decisions', run: testGenrePrecedence },
    { n: 33, id: 'original-audio', title: 'Ordered languages and metadata-only original audio, fallbacks and explanation', run: testOriginalAndLanguages },
    { n: 34, id: 'choice-status', title: 'Saved-choice status accuracy, delayed results, HTTP failure and missing optional endpoints', run: testChoiceStatus },
    { n: 35, id: 'genre-migration', title: 'Legacy genre activation is idempotent and visible in the People editor', run: testGenreMigration },
    { n: 36, id: 'screenshots-dir', title: 'Sanitized real desktop/375 screenshots of Settings controls', run: testSettingsScreenshots },
    { n: 37, id: 'forced-only', title: 'Forced/default English subtitle at index zero with Japanese audio in PlaybackInfo and the real player', run: testForcedOnlySubtitles },
    { n: 38, id: 'burned-removal', title: 'Settings removes stored title, season and show burned-in flags without losing other edits', run: testBurnedFlagRemoval },
    { n: 39, id: 'admin-leave', title: 'Admin unsaved edits survive Cancel; Discard and Check a title Escape use the real router', run: testAdminLeaveGuard },
    { n: 40, id: 'legacy-admin-leave', title: 'Admin hash and Back protection without the Navigation API', run: testLegacyAdminNavigation },
    { n: 41, id: 'repeated-admin-leave', title: 'Multiple native navigation attempts resume only the first destination', run: testRepeatedAdminNavigation },
    { n: 42, id: 'sidebar-admin-leave', title: 'Real dashboard sidebar clicks preserve unsaved settings on Cancel', run: testSidebarAdminNavigation },
    { n: 43, id: 'field-conflicts', title: 'Real concurrent edited-field saves, Reload and Keep mine', run: testConcurrentFieldChoices },
    { n: 13, id: 'hygiene', title: 'Hygiene: console, exceptions, HTTP errors, no Coverage requests', run: testHygiene, noSignIn: true, last: true }
];

function selected(test, opts) {
    const match = (list) => list.some((x) => String(test.n) === x || test.id === x || (test.aliases || []).includes(x));
    if (opts.skip.length && match(opts.skip)) {
        return false;
    }
    return !opts.only || match(opts.only) || test.last;
}

async function runTest(t, test) {
    t.test = test;

    t.deferred = [];
    const started = Date.now();
    console.log('');
    console.log('== ' + test.n + '. ' + test.title);
    try {
        if (!test.noSignIn) {
            await ensureSignedIn(t);
        }
        await test.run(t);
    } catch (e) {
        await t.check(false, 'test stopped: ' + (e && e.message ? e.message : e));
    } finally {
        const undo = t.deferred.reverse();
        t.deferred = [];
        for (const d of undo) {
            try {
                await d.fn();
            } catch (e) {
                await t.check(false, 'undo "' + d.what + '" failed: ' + e.message, null);
            }
        }
        await t.page.clearViewport().catch(() => {});
        console.log('   (' + test.n + ' took ' + Math.round((Date.now() - started) / 1000) + ' s)');
    }
}

async function main() {
    let opts;
    try {
        opts = parseArgs(process.argv.slice(2));
    } catch (e) {
        console.error(e.message);
        return 2;
    }
    if (opts.help || !opts.base) {
        console.log('usage: node tools/rig/e2e/run.js --base http://127.0.0.1:8098 [--only 4,7] [--skip 4] [--headed] [--serial] [--verbose] [--settle 800] [--quick-sweeps] [--screenshots-dir <screenshots-folder>]');
        return opts.help ? 0 : 2;
    }
    fs.mkdirSync(opts.shots, { recursive: true });
    fs.readdirSync(opts.shots).filter((f) => /^e2e-.*\.png$/.test(f)).forEach((f) => { try { fs.unlinkSync(path.join(opts.shots, f)); } catch (e) { /* in use */ } });
    const started = Date.now();
    console.log('e2e: ' + opts.base + ', Chrome ' + (opts.headed ? 'headed' : 'headless') + ', settle ' + opts.settle + ' ms, screenshots in ' + opts.shots);
    try {
        const health = await fetch(opts.base + '/health');
        const text = await health.text();
        if (health.status !== 200) {
            throw new Error(health.status + ' ' + text);
        }
    } catch (e) {
        console.log('FAIL [-] Jellyfin answers at ' + opts.base + ' (' + e.message + ')');
        console.log('FAILURES: 1');
        return 1;
    }
    let chrome = null;
    try {
        chrome = await Chrome.launch({ headless: !opts.headed });
        const page = await chrome.firstPage();
        await installHelpers(page);
        const t = new Context(chrome, page, opts);
        for (const test of TESTS.filter((x) => selected(x, opts))) {
            await runTest(t, test);
        }
    } catch (e) {
        results.push({ ok: false, test: 0, what: 'runner: ' + e.message });
        console.log('FAIL [-] the run stopped: ' + (e && e.stack ? e.stack : e));
    } finally {
        if (chrome) {
            await chrome.close();
            if (chrome.profileRemoved === false) {
                console.log('NOTE the temporary Chrome profile could not be deleted: ' + chrome.profile);
            }
        }
    }
    const failed = results.filter((r) => !r.ok);
    console.log('');
    if (failed.length) {
        console.log('Failed checks (' + failed.length + '):');
        failed.forEach((r) => console.log('  [' + r.test + '] ' + r.what.split('\n')[0].slice(0, 220)));
    }
    console.log('e2e: ' + results.length + ' checks in ' + Math.round((Date.now() - started) / 1000) + ' s; ' + failed.length + ' failed');
    console.log(failed.length === 0 ? 'ALL PASS' : 'FAILURES: ' + failed.length);
    return failed.length === 0 ? 0 : 1;
}

if (require.main === module) {
    main().then((code) => { process.exitCode = code; }, (e) => { console.error(e); process.exitCode = 1; });
}

module.exports = { storedFor, canon, changedPaths, strayPaths, flatten, requireLoopback, mockResetAll };
