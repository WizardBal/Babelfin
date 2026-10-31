// Maintained admin UI smoke suite. Run ../run.py for both null modes and validators.
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');
const { Document, Event, parseInto } = require('./fakedom');

const { getAssetsPath, verifySemanticTarget } = require('../shared/target');
const ASSETS = getAssetsPath();
const ROOT = path.resolve(ASSETS, '..') + path.sep;
verifySemanticTarget(ASSETS, Document);
const BASE = 'http://srv/jf';
const ALL_REQUESTS = []; // every request of every page booted in this run (no Coverage request may be among them)

let failures = 0;
let passes = 0;
const sections = [];
function ok(cond, what, detail) {
    if (cond) {
        passes++;
    } else {
        failures++;
        console.log('FAIL: ' + what + (detail !== undefined ? '\n      ' + (typeof detail === 'string' ? detail : JSON.stringify(detail)) : ''));
    }
}
function eq(actual, expected, what) {
    ok(JSON.stringify(actual) === JSON.stringify(expected), what, { actual, expected });
}
function section(name) {
    sections.push({ name, start: passes + failures, failStart: failures });
}
const clone = (v) => JSON.parse(JSON.stringify(v));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/* ---------- fake server data ---------- */

const LIB = { anime: 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1', movies: 'b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2', shows: 'c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3', german: 'd4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4', mixed: 'e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5' };
const USER = { anna: '11111111111111111111111111111111', ben: '22222222222222222222222222222222', cleo: '33333333333333333333333333333333' };
// The shows and movies Anna has overrides for, what the server knows about them (name, type, year, library) and a third one for the tests that add one.
const OVR = { sable: 'a'.repeat(32), lantern: 'b'.repeat(32), glassHarbor: 'c'.repeat(32), bluey: 'd'.repeat(32) };
const OVR_ITEMS = [
    { Id: OVR.sable, Name: 'Sable Voyage', Type: 'Series', ProductionYear: null, Library: 'anime' },
    { Id: OVR.lantern, Name: 'Étoile Lantern', Type: 'Movie', ProductionYear: 2001, Library: 'movies' },
    { Id: OVR.glassHarbor, Name: 'Glass Harbor', Type: 'Series', ProductionYear: 2021, Library: 'shows' },
    { Id: OVR.bluey, Name: 'Bluey', Type: 'Series', ProductionYear: null, Library: 'anime' }
];
const LIB_NAMES = { anime: 'Anime', movies: 'Movies', shows: 'Shows', german: 'Deutsche Filme', mixed: 'Mixed' };
/** An OverrideSummary the way GET Admin/Users/{id}/Overrides builds it from a stored ItemOverride. */
function summarizeOverride(stored) {
    const item = OVR_ITEMS.find((x) => x.Id === stored.ItemId) || null;
    return {
        ItemId: stored.ItemId, Scope: stored.Scope, Name: item ? item.Name : stored.Name, Type: item ? item.Type : (stored.Scope === 'Series' ? 'Series' : 'Movie'),
        ProductionYear: item ? item.ProductionYear : null, SeriesName: null, LibraryId: item ? LIB[item.Library] : null, LibraryName: item ? LIB_NAMES[item.Library] : null,
        Settings: stored.Settings, Manual: stored.Manual, UpdatedAt: stored.UpdatedAt
    };
}
const UPGRADE_NOTE = 'Settings upgraded from Babelfin 0.2: per-app modes for the track numbers apps send. Your settings were kept.';
const EXTRAS = ['pt-BR', 'pt-PT', 'zh-Hans', 'zh-Hant', 'es-419', 'fr-CA', 'yue', 'fil'];

/** The settings as 0.4 left them (opinions set during testing, the original-language rule on). */
function oldConfig() {
    return {
        ConfigVersion: 3,
        Enabled: true,
        DryRun: false,
        AppModes: [
            { Client: 'Jellyfin Web', Mode: 'ReplaceOnAutoplay', Seen: true, LastSeen: '2026-10-18T10:00:00Z' },
            { Client: 'Jellyfin Media Player', Mode: 'ReplaceOnAutoplay', Seen: false, LastSeen: null },
            { Client: 'Android TV', Mode: 'ReplaceUnlessManual', Seen: true, LastSeen: '2026-10-17T10:00:00Z' }
        ],
        UnknownAppMode: 'Trust',
        CorrectAfterStart: 'On',
        CompatibilityMode: false,
        ManualChangeAfterSeconds: 5,
        ServedWindowMinutes: 30,
        AutoplayWindowSeconds: 120,
        ActivityRetentionDays: 90,
        SendImportantEventsToJellyfinActivity: true,
        Everyday: { AudioChain: [{ Kind: 'OriginalLanguage' }, { Kind: 'Language', Language: 'en' }, { Kind: 'FileDefault' }], ForeignTitles: null, Understood: null },
        Libraries: [
            { LibraryId: LIB.shows, Name: 'Shows', OriginalAudio: 'Yes', Settings: { UnlabelledAudio: 'Unknown' } },
            { LibraryId: LIB.german, Name: 'Deutsche Filme', OriginalAudio: 'No', Settings: { AudioChain: [{ Kind: 'Language', Language: 'de' }, { Kind: 'OriginalLanguage' }, { Kind: 'FileDefault' }] } },
            { LibraryId: LIB.mixed, Name: 'Mixed', OriginalAudio: 'Auto', Settings: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Off' } }
        ],
        SmartRules: [{ Id: 'r1', Name: 'Kids', Enabled: true, Match: 'All', Conditions: [{ Kind: 'Genre', Values: ['Kids'], Negate: false, IsRegex: false }], Settings: { WhenAudioUnderstood: 'Off' } }],
        Titles: [{ ItemId: 'f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0', Scope: 'Series', Name: 'Sable Voyage', Settings: { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'OriginalLanguage' }, { Kind: 'FileDefault' }], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['en'] } }],
        BurnedIn: [{ ItemId: '9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a', Scope: 'Title', Name: 'Old Movie', Languages: ['en'], SetAt: '2026-10-01T00:00:00Z', SetBy: USER.ben }],
        NewUserTemplate: null,
        Starter: { Enabled: true, OriginalAudioLibraryWords: ['anime', 'アニメ'], OriginalAudioGenres: ['Anime'], GenreSharePercent: 50, UsualOriginalLanguagePercent: 60, GenreRule: true, ForeignTitles: 'DubInMyLanguage' },
        Keywords: { PartialWords: ['signs', 'forced'], SdhWords: ['sdh'], FullWords: ['full', 'dialogue'], CommentaryWords: ['commentary'], AudioDescriptionWords: ['audio description'] },
        LanguageAliases: [{ Raw: 'jap', Code: 'ja' }],
        LibraryFactsRefreshDays: 1,
        LastUpgradeNote: UPGRADE_NOTE
    };
}

/**
 * The settings the page loads: the version-4 table clean-up, PerLibraryDefaults null (automatic). The parts this page no
 * longer edits (rules, show and movie defaults, burned-in approvals, the original-language rule, timing, test mode, IfNothingMatches, a
 * library entry's other settings) are deliberately left filled in, so the tests can prove a save never touches them.
 */
function initialConfig() {
    const c = oldConfig();
    c.ConfigVersion = 5;
    c.PerLibraryDefaults = null;
    c.Everyday = { AudioChain: null, ForeignTitles: null, Understood: null };
    c.Libraries = [
        { LibraryId: LIB.shows, Name: 'Shows', OriginalAudio: 'Yes', Settings: { UnlabelledAudio: 'Unknown' } },
        { LibraryId: LIB.german, Name: 'Deutsche Filme', OriginalAudio: 'No', Settings: {} },
        { LibraryId: LIB.mixed, Name: 'Mixed', OriginalAudio: 'Auto', Settings: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Off' } }
    ];
    c.Starter.Enabled = false;
    c.Everyday.IfNothingMatches = 'PartialInMyLanguage';
    c.DryRun = true;
    c.LastUpgradeNote = '';
    return c;
}
/** The same without any library table choice (so the automatic mode is "Same for every library"). */
function sameConfig() {
    const c = initialConfig();
    c.Libraries = c.Libraries.filter((l) => l.LibraryId !== LIB.mixed);
    return c;
}
// The fields the page no longer edits: every save must leave them exactly as they were.
const UNTOUCHED = ['SmartRules', 'Titles', 'BurnedIn', 'Starter', 'DryRun', 'ManualChangeAfterSeconds', 'ServedWindowMinutes', 'AutoplayWindowSeconds', 'ActivityRetentionDays', 'LibraryFactsRefreshDays', 'SendImportantEventsToJellyfinActivity'];
const TITLE_ROWS = (n) => Array.from({ length: n }, (_, i) => ({
    ItemId: 'e' + String(i).padStart(31, '0'), Name: 'Episode ' + (i + 1), SeriesName: 'Sable Voyage', SeasonNumber: 1 + Math.floor(i / 30), EpisodeNumber: 1 + (i % 30),
    SourceId: 's' + i, Outcome: i % 3 === 0 ? 'PassedThrough' : (i % 3 === 1 ? 'Applied' : 'None'),
    AudioIndex: i % 5 === 4 ? null : 1, AudioName: 'Japanese', AudioLanguage: 'ja',
    SubtitleIndex: i % 4 === 3 ? null : 3, SubtitleName: 'English', SubtitleLanguage: 'en', SubtitleKind: i % 2 ? 'full' : 'partial', SubtitleCodec: 'ass',
    SubtitlesWanted: i % 8 === 3, OriginalLanguage: 'ja', Flags: ['partial-chosen', 'fallback-used'],
    Explanation: i % 3 === 1 ? "Japanese audio: the first language in this person's order that the file has." : null
}));

function libraries() {
    return [
        { Id: LIB.anime, Name: 'Anime', CollectionType: 'tvshows', OriginalAudio: 'Auto', IsOriginalAudio: false, UsualOriginalLanguage: 'ja', Facts: { ComputedAt: '2026-10-19T00:00:00Z', DetectedOriginalAudio: true, NameMatchesWordList: true } },
        { Id: LIB.movies, Name: 'Movies', CollectionType: 'movies', OriginalAudio: 'Auto', IsOriginalAudio: false, UsualOriginalLanguage: null, Facts: { ComputedAt: '2026-10-19T00:00:00Z', DetectedOriginalAudio: false } },
        { Id: LIB.shows, Name: 'Shows', CollectionType: 'tvshows', OriginalAudio: 'Yes', IsOriginalAudio: true, UsualOriginalLanguage: null, Facts: { ComputedAt: '2026-10-19T00:00:00Z', DetectedOriginalAudio: false } },
        { Id: LIB.german, Name: 'Deutsche Filme', CollectionType: 'movies', OriginalAudio: 'No', IsOriginalAudio: false, UsualOriginalLanguage: 'de', Facts: null },
        { Id: LIB.mixed, Name: 'Mixed', CollectionType: null, OriginalAudio: 'Auto', IsOriginalAudio: false, UsualOriginalLanguage: null, Facts: null }
    ];
}

function personDocs() {
    return {
        [USER.anna]: {
            SchemaVersion: 1, UserId: USER.anna, UserName: 'Anna', Enabled: true, UseEverydayEverywhere: false, HandleNextEpisode: false,
            Seed: { SeededAt: '2026-10-01T00:00:00Z', Assumed: [{ Field: 'Understood', Language: 'en', From: 'server' }], Confirmed: false, FromTemplate: false },
            Everyday: { Understood: ['en'], Reads: ['en'] },
            Libraries: [{ LibraryId: LIB.anime, Name: 'Anime', Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' } }],
            SmartRules: [],
            // Two show and movie overrides: a show with its own audio and subtitles, and a movie that only has a remembered track change.
            Overrides: [
                { ItemId: OVR.sable, Scope: 'Series', Name: 'Sable Voyage', Settings: { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['en'] }, Manual: null, UpdatedAt: '2026-10-02T00:00:00Z' },
                { ItemId: OVR.lantern, Scope: 'Title', Name: 'Étoile Lantern', Settings: {}, Manual: { Audio: { Language: 'fr', Kind: 'normal' }, SubtitlesOff: true, At: '2026-10-03T00:00:00Z', Client: 'Jellyfin Web' }, UpdatedAt: '2026-10-03T00:00:00Z' }
            ],
            CollectionOrder: [],
            BurnedIn: [{ ItemId: '8b8b8b8b8b8b8b8b8b8b8b8b8b8b8b8b', Scope: 'Title', Name: 'Hard Subs Show', Languages: ['ja'], SetAt: '2026-10-02T00:00:00Z', SetBy: USER.anna }],
            Locks: { WhenAudioNotUnderstood: 'Full' },
            UpdatedAt: '2026-10-10T00:00:00Z'
        },
        [USER.ben]: {
            SchemaVersion: 1, UserId: USER.ben, UserName: 'Ben', Enabled: true, UseEverydayEverywhere: false, HandleNextEpisode: false,
            Seed: { SeededAt: '2026-10-01T00:00:00Z', Assumed: [], Confirmed: true, FromTemplate: false },
            Everyday: { Understood: ['de', 'en'], Reads: ['de'] },
            Libraries: [], SmartRules: [], Overrides: [], CollectionOrder: [], BurnedIn: [], Locks: {},
            UpdatedAt: '2026-10-11T00:00:00Z'
        }
    };
}

/** GET Admin/Defaults: a fresh configuration with the shipped values (PluginConfiguration's own defaults after the upgrade). */
function shippedConfig() {
    return {
        ConfigVersion: 6, PerLibraryDefaults: null, Enabled: true, DryRun: false,
        AppModes: [
            { Client: 'Jellyfin Web', Mode: 'ReplaceOnAutoplay', Seen: false, LastSeen: null },
            { Client: 'Jellyfin Media Player', Mode: 'ReplaceOnAutoplay', Seen: false, LastSeen: null },
            { Client: 'Jellyfin Desktop', Mode: 'ReplaceOnAutoplay', Seen: false, LastSeen: null },
            { Client: 'Jellyfin Android', Mode: 'ReplaceOnAutoplay', Seen: false, LastSeen: null },
            { Client: 'Android TV', Mode: 'ReplaceUnlessManual', Seen: false, LastSeen: null },
            { Client: 'Streamyfin', Mode: 'ReplaceUnlessManual', Seen: false, LastSeen: null },
            { Client: 'Swiftfin', Mode: 'Trust', Seen: false, LastSeen: null }
        ],
        UnknownAppMode: 'Trust', CorrectAfterStart: 'Auto', CompatibilityMode: false,
        ManualChangeAfterSeconds: 5, ServedWindowMinutes: 30, AutoplayWindowSeconds: 120, ActivityRetentionDays: 90, SendImportantEventsToJellyfinActivity: null,
        Everyday: {}, Libraries: [], SmartRules: [], Titles: [], BurnedIn: [], NewUserTemplate: null,
        Starter: { Enabled: false, OriginalAudioLibraryWords: ['anime'], OriginalAudioGenres: ['Anime'], GenreSharePercent: 50, UsualOriginalLanguagePercent: 60, GenreRule: true, ForeignTitles: 'DubInMyLanguage' },
        Keywords: { PartialWords: ['signs', 'songs', 'forced'], SdhWords: ['sdh', 'hearing impaired'], FullWords: ['full', 'dialogue'], CommentaryWords: ['commentary'], AudioDescriptionWords: ['audio description', 'descriptive'] },
        LanguageAliases: [], LibraryFactsRefreshDays: 1, LastUpgradeNote: ''
    };
}
const TABLE_FIELDS = ['AudioChain', 'ForeignTitles', 'WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'SubtitleLanguages'];
const hasTableFields = (layer) => !!layer && TABLE_FIELDS.some((f) => layer[f] !== null && layer[f] !== undefined);
/** A GET Admin/Users row as the server builds it from a person's file (TableModes.PersonPerLibrary, TableOnly, the library count). */
function userRow(id, name, doc, extra) {
    const table = doc && hasTableFields(doc.Everyday) ? Object.fromEntries(TABLE_FIELDS.map((f) => [f, doc.Everyday[f] === undefined ? null : doc.Everyday[f]])) : null;
    const perLibrary = !!doc && (doc.PerLibrary === true || (doc.PerLibrary !== false && (doc.Libraries || []).some((l) => l && hasTableFields(l.Settings))));
    return Object.assign({
        UserId: id, Name: name, HasSettings: !!doc, Enabled: !doc || doc.Enabled !== false, UseEverydayEverywhere: false,
        Assumed: doc && doc.Seed && doc.Seed.Confirmed === false ? (doc.Seed.Assumed || []) : [], SeedConfirmed: !!(doc && doc.Seed && doc.Seed.Confirmed), UpdatedAt: doc ? (doc.UpdatedAt || null) : null,
        PerLibrary: perLibrary, Table: table, LibraryRows: doc ? (doc.Libraries || []).filter((l) => l && hasTableFields(l.Settings)).length : 0
    }, extra || {});
}
/** A copy without null members (what a server that leaves nulls out of its JSON sends). */
function dropNulls(value) {
    if (Array.isArray(value)) { return value.map(dropNulls); }
    if (value && typeof value === 'object') {
        const out = {};
        Object.keys(value).forEach((k) => { if (value[k] !== null && value[k] !== undefined) { out[k] = dropNulls(value[k]); } });
        return out;
    }
    return value;
}

const VIDEO_ROW = { AudioChain: [{ Kind: 'VideoDefault' }], WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault', SubtitleLanguages: [], AudioFromLibrary: false, SubtitlesFromLibrary: false };

// POST Admin/ResetAll.
const RESET_FILE = 'reset-example.json';
const RESET_PATH = 'backups/' + RESET_FILE;
const RESET_FAILED = "The previous settings couldn't be saved, so nothing was reset.";
/** A person's file right after Reset all settings: a fresh seed, no Locks, HandleNextEpisode off. */
function freshSeed(id, name) {
    return {
        SchemaVersion: 1, UserId: id, UserName: name, Enabled: true, UseEverydayEverywhere: false, HandleNextEpisode: false,
        Seed: { SeededAt: '2026-10-21T12:00:00Z', Assumed: [{ Field: 'Understood', Language: 'en', From: 'server' }], Confirmed: false, FromTemplate: false },
        Everyday: { Understood: ['en'], Reads: ['en'] }, Libraries: [], SmartRules: [], Overrides: [], CollectionOrder: [], BurnedIn: [], Locks: {}, UpdatedAt: '2026-10-21T12:00:00Z'
    };
}

// The fields query merges only named values, including explicit nulls and atomic arrays.
function checkExpectedBody(body, fields, headers, state) {
    const unconditional = state.expectUnconditionalSettingsPut === true;
    state.expectUnconditionalSettingsPut = false;
    if (unconditional) {
        ok(!Object.prototype.hasOwnProperty.call(body, 'ExpectedValues'), 'explicit Keep mine omits expectations once');
    } else {
        ok(!!body.ExpectedValues && !Array.isArray(body.ExpectedValues) && typeof body.ExpectedValues === 'object', 'save carries body expectations');
        eq(Object.keys(body.ExpectedValues || {}).sort(), (fields || '').split(',').sort(), 'expectations name exactly the edited fields');
    }
    ok(!Object.keys(headers || {}).some(key => key.toLowerCase() === 'x-babelfin-expected'), 'save avoids the legacy expectation header');
}
function applyIntent(stored, body, fields) {
    if (fields === null) { const saved = clone(body); delete saved.ExpectedValues; return saved; }
    const saved = clone(stored);
    for (const field of fields.split(',')) {
        if (field.startsWith('libraries.')) {
            const parts = field.split('.'), id = parts[1];
            if (!/^[0-9a-f]{32}$/.test(id) || /^0+$/.test(id) || !(parts.length === 4 && parts[2] === 'settings' || parts.length === 3 && parts[2] === 'originalAudio')) { throw new Error('Invalid library leaf: ' + field); }
            const sameId = row => String(row.LibraryId).replace(/-/g, '').toLowerCase() === id;
            const from = (body.Libraries || []).find(sameId);
            saved.Libraries = saved.Libraries || [];
            let row = saved.Libraries.find(sameId);
            if (!row && !from) { continue; }
            if (!row) { row = { LibraryId: id, Name: from.Name, Settings: {} }; saved.Libraries.push(row); }
            if (parts.length === 3) { row.OriginalAudio = from && from.OriginalAudio || 'Auto'; }
            else {
                const key = parts[3][0].toUpperCase() + parts[3].slice(1);
                row.Settings = row.Settings || {};
                row.Settings[key] = from && from.Settings && from.Settings[key] !== undefined ? clone(from.Settings[key]) : null;
            }
            continue;
        }
        if (field === 'newUserTemplate.everyday.understood') {
            const value = body.NewUserTemplate && body.NewUserTemplate.Everyday && body.NewUserTemplate.Everyday.Understood;
            if (saved.NewUserTemplate == null && value == null) { continue; }
            saved.NewUserTemplate = saved.NewUserTemplate || {};
            saved.NewUserTemplate.Everyday = saved.NewUserTemplate.Everyday || {};
            saved.NewUserTemplate.Everyday.Understood = value === undefined ? null : clone(value);
            // The backend collapses a default-only template to null, while retaining every hidden nondefault value.
            if (value == null) {
                const rest = clone(saved.NewUserTemplate);
                for (const key of ['SchemaVersion', 'UserId', 'UserName']) delete rest[key];
                if (rest.Enabled === true) delete rest.Enabled;
                for (const key of ['UseEverydayEverywhere', 'HandleNextEpisode']) if (rest[key] === false) delete rest[key];
                const meaningful = value => value != null && (Array.isArray(value) ? value.length > 0 : typeof value === 'object' ? Object.values(value).some(meaningful) : true);
                if (!meaningful(rest)) saved.NewUserTemplate = null;
            }
            continue;
        }
        if (!/^[a-z][a-zA-Z]*(\.[a-z][a-zA-Z]*)?$/.test(field)) { throw new Error('Invalid field intent: ' + field); }
        const keys = field.split('.').map(key => key[0].toUpperCase() + key.slice(1));
        if (keys.length === 1 && ['Everyday', 'Locks', 'Seed', 'Keywords', 'Starter'].includes(keys[0])) { throw new Error('Field intent requires a leaf: ' + field); }
        let target = saved, source = body;
        for (const key of keys.slice(0, -1)) { target = target[key] = target[key] || {}; source = source && source[key]; }
        const key = keys[keys.length - 1]; target[key] = source && source[key] !== undefined ? clone(source[key]) : null;
    }
    return saved;
}

function createServer(opts) {
    // controls: choiceStatus is an object/null or async (state) => answer; state.choiceStatus is mutable for Save refreshes.
    // genres is a catalogue or async (viewerId, state) => catalogue. opts.fail can fail the optional status endpoint without blocking boot.
    const state = {
        choiceStatus: opts.choiceStatus === undefined ? { HasChoices: true } : opts.choiceStatus,
        config: opts.config ? opts.config() : initialConfig(),
        docs: personDocs(),
        puts: [],
        userPuts: [],
        requests: [],
        status: { version: '0.19.0.0', wrapped: opts.wrapped !== false, staticCalls: 98765, playbackCalls: 4321, applied: 777, decisionsCached: 55, lastUpgradeNote: UPGRADE_NOTE },
        simPosts: [],
        jobs: {},
        activityQueries: [],
        itemQueries: []
    };
    // The rows follow the stored files, like the server's (a save in the editor shows in the People table after the reload).
    const usersList = () => (opts.users ? opts.users(state) : [
        userRow(USER.anna, 'Anna', state.docs[USER.anna]),
        userRow(USER.ben, 'Ben', state.docs[USER.ben]),
        userRow(USER.cleo, 'Cleo', state.docs[USER.cleo])
    ]);
    const languages = [
        { Code: 'en', Name: 'English', Base: 'en' }, { Code: 'ja', Name: 'Japanese', Base: 'ja' }, { Code: 'de', Name: 'German', Base: 'de' },
        { Code: 'fr', Name: 'French', Base: 'fr' }, { Code: 'es', Name: 'Spanish', Base: 'es' }, { Code: 'pt-BR', Name: 'Portuguese (Brazil)', Base: 'pt' },
        { Code: 'en-GB', Name: 'English (United Kingdom)', Base: 'en' }, { Code: 'ace', Name: 'Achinese', Base: 'ace' }
    ];

    function respond(status, raw, headers) {
        // BF_OMIT_NULLS=1 runs the whole suite against a server that leaves every null member out of its JSON.
        const omit = opts.omitNulls !== undefined ? opts.omitNulls : process.env.BF_OMIT_NULLS === '1';
        const body = omit && raw && typeof raw === 'object' ? dropNulls(raw) : raw;
        const text = body === undefined || body === null ? '' : (typeof body === 'string' ? body : JSON.stringify(body));
        return {
            ok: status >= 200 && status < 300,
            status: status,
            headers: { get: (k) => (headers && headers[k]) || null },
            text: () => Promise.resolve(text),
            json: () => Promise.resolve(JSON.parse(text)),
            blob: () => Promise.resolve({ size: text.length, text: async () => text })
        };
    }

    async function handle(url, init) {
        const method = (init && init.method) || 'GET';
        const u = new URL(url);
        const p = u.pathname.replace(/^\/jf\//, '');
        const body = init && init.body ? JSON.parse(init.body) : undefined;
        if (method === 'PUT' && (p === 'Babelfin/Admin/Settings' || /^Babelfin\/Admin\/Users\/[0-9a-f]+\/Settings$/.test(p))) {
            checkExpectedBody(body, u.searchParams.get('fields'), init.headers, state);
        }
        state.requests.push(method + ' ' + p + (u.search || ''));
        ALL_REQUESTS.push(method + ' ' + p + (u.search || ''));
        let m;
        if (p.startsWith('Babelfin/assets/')) {
            const file = ASSETS + p.slice('Babelfin/assets/'.length);
            return fs.existsSync(file) ? respond(200, fs.readFileSync(file, 'utf8')) : respond(404, null);
        }
        if (p === 'Babelfin/Admin/ChoiceStatus' && method === 'GET') {
            const failure = opts.fail && opts.fail[method + ' ' + p];
            if (failure) {
                const f = typeof failure === 'object' ? failure : { status: failure };
                if (!f.sticky) { delete opts.fail[method + ' ' + p]; }
                return respond(f.status, { Message: 'RAW STATUS ERROR: must stay hidden' });
            }
            const control = state.choiceStatus;
            return respond(200, typeof control === 'function' ? await control(state) : control);
        }
        if (p === 'Genres' && method === 'GET') {
            const viewer = u.searchParams.get('UserId');
            if (!['admin', ...Object.values(USER)].includes(viewer)) { throw new Error('Genres must target current admin or edited person'); }
            return respond(200, typeof opts.genres === 'function' ? await opts.genres(viewer, state) : opts.genres || { Items: [{ Name: 'Anime' }, { Name: 'Drama' }, { Name: 'Kids' }] });
        }
        if (p === 'Babelfin/Admin/Status') { return respond(200, state.status); }
        if (p === 'Babelfin/Admin/Settings' && method === 'GET') { return respond(200, clone(state.config)); }
        if (p === 'Babelfin/Admin/Settings' && method === 'PUT') {
            state.puts.push(clone(body));
            state.config = applyIntent(state.config, body, u.searchParams.get('fields'));
            return respond(200, clone(state.config));
        }
        if (p === 'Babelfin/Languages') { return respond(200, languages); }
        if (p === 'Babelfin/Admin/Libraries') {
            const libs = libraries();
            libs.forEach((l) => {
                const row = state.config.Libraries.find((r) => r.LibraryId === l.Id);
                l.OriginalAudio = row ? row.OriginalAudio : 'Auto';
            });
            return respond(200, libs);
        }
        // The server leaves null members out of this answer: Table is missing when nothing is set.
        if (p === 'Babelfin/Admin/Users') { return respond(200, opts.keepNulls ? usersList() : usersList().map(dropNulls)); }
        if (p === 'Babelfin/Admin/Defaults' && method === 'GET') {
            state.shippedCalls = (state.shippedCalls || 0) + 1;
            if (opts.noShipped) { return respond(404, null); }
            return respond(200, opts.shipped ? opts.shipped() : shippedConfig());
        }
        if (/^Babelfin\/Admin\/Users\/([0-9a-f]+)\/Defaults$/.test(p) && method === 'GET') {
            state.defaultsCalls = (state.defaultsCalls || 0) + 1;
            const all = opts.userDefaults ? clone(opts.userDefaults) : Object.assign(clone(VIDEO_ROW), { Understood: ['en'], Reads: ['en'] });
            return respond(200, { AllLibraries: all, Libraries: clone(opts.userLibraryDefaults || {}) });
        }
        // Failures to order (opts.fail: { 'GET Babelfin/Admin/Users/<id>/Overrides': 500 } answers that status once; { status, sticky: true } every time).
        const failure = opts.fail && opts.fail[method + ' ' + p];
        if (failure) {
            const f = typeof failure === 'object' ? failure : { status: failure };
            if (!f.sticky) { delete opts.fail[method + ' ' + p]; }
            return respond(f.status, { Message: 'failed' });
        }
        //: the person's show and movie overrides as summaries; DELETE …/Overrides/{itemId}?scope= removes one (404 when it is gone
        // already); DELETE …/Overrides - and, as ASP.NET routes it, …/Overrides/ with no id - removes them all. opts.summaries(userId, state) replaces the list.
        if ((m = /^Babelfin\/Admin\/Users\/([0-9a-f]+)\/Overrides(\/([^/]*))?$/.exec(p))) {
            const id = m[1];
            const doc = state.docs[id] || null;
            const storedList = doc ? (doc.Overrides || []) : [];
            if (method === 'GET' && m[2] === undefined) {
                state.overrideGets = (state.overrideGets || 0) + 1;
                return respond(200, opts.summaries ? opts.summaries(id, state) : storedList.map(summarizeOverride));
            }
            if (method === 'DELETE') {
                state.deletes = (state.deletes || []).concat([method + ' ' + p + (u.search || '')]);
                if (m[2] === undefined || m[3] === '') { return respond(405, { Message: 'Use the explicit bulk route' }); }
                if (m[3] === 'All') {
                    state.removedAll = (state.removedAll || 0) + 1;
                    if (doc) { doc.Overrides = []; }
                    return respond(204, null);
                }
                const scope = u.searchParams.get('scope');
                const keep = storedList.filter((x) => !(x.ItemId === m[3] && x.Scope === scope));
                if (keep.length === storedList.length) { return respond(404, { Message: 'gone' }); }
                doc.Overrides = keep;
                return respond(204, null);
            }
        }
        if ((m = /^Babelfin\/Admin\/Users\/([0-9a-f]+)\/Settings$/.exec(p))) {
            const id = m[1];
            if (method === 'GET') {
                if (!state.docs[id]) {
                    state.docs[id] = { SchemaVersion: 1, UserId: id, UserName: 'Cleo', Enabled: true, Everyday: { Understood: ['fr'], Reads: ['fr'] }, Libraries: [], SmartRules: [], Overrides: [], CollectionOrder: [], BurnedIn: [], Locks: {}, Seed: { Assumed: [], Confirmed: true } };
                }
                const doc = clone(state.docs[id]);
                if (opts.defaultsField) {
                    doc.Defaults = { AllLibraries: { AudioChain: [{ Kind: 'OriginalLanguage' }], WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: null, Understood: ['en'], Reads: ['en'] }, Libraries: {} };
                }
                return respond(200, doc);
            }
            if (method === 'PUT') {
                state.userPuts.push({ id: id, body: clone(body), query: Object.fromEntries(u.searchParams) });
                // keepOverrides=true: the stored overrides replace whatever the body has.
                const saved = applyIntent(state.docs[id], body, u.searchParams.get('fields'));
                if (u.searchParams.get('keepOverrides') === 'true') { saved.Overrides = clone((state.docs[id] && state.docs[id].Overrides) || []); }
                state.docs[id] = saved;
                return respond(200, clone(saved));
            }
        }
        if (p === 'UserViews') {
            const id = u.searchParams.get('userId');
            const all = libraries().map((l) => ({ Id: l.Id, Name: l.Name }));
            return respond(200, { Items: id === USER.ben ? all.filter((l) => l.Id !== LIB.anime) : all });
        }
        if (p === 'Babelfin/Admin/Apps') { return respond(200, state.config.AppModes.map((a) => ({ Client: a.Client, Mode: a.Mode, Seen: a.Seen, LastSeen: a.LastSeen }))); }
        if (p === 'Babelfin/Admin/Activity') {
            const limit = Number(u.searchParams.get('limit') || 100);
            const userId = u.searchParams.get('userId');
            state.activityQueries.push({ limit: limit, userId: userId, kind: u.searchParams.get('kind') });
            const count = opts.activityCount === undefined ? 130 : opts.activityCount;
            const all = Array.from({ length: count }, (_, i) => ({
                At: new Date(Date.UTC(2026, 9, 19, 8, 0, 0) - i * 60000).toISOString(), Kind: i % 2 ? 'override-created' : 'settings-changed',
                UserId: i % 2 ? USER.anna : (i % 4 === 0 ? null : USER.ben), ItemId: null, Text: 'Entry ' + (i + 1)
            }));
            const list = all.filter((e) => !userId || e.UserId === userId);
            return respond(200, list.slice(0, limit));
        }
        if (p === 'Babelfin/Admin/Simulate' && method === 'POST') {
            state.simPosts.push(clone(body));
            if (state.simBusy) { return respond(409, { Message: 'A simulation is already running.', JobId: 'busyjob' }); }
            const id = 'job' + state.simPosts.length;
            state.jobs[id] = { polls: 0, rows: opts.simRows ? opts.simRows(body) : TITLE_ROWS(120) };
            return respond(202, { JobId: id });
        }
        if ((m = /^Babelfin\/Admin\/Simulate\/([^/]+)$/.exec(p)) && method === 'GET') {
            const job = state.jobs[decodeURIComponent(m[1])];
            if (!job || state.simExpired) { return respond(404, { Message: 'Unknown job.' }); }
            job.polls++;
            if (job.polls < 2) { return respond(200, { JobId: m[1], Progress: 40, Done: false, Cancelled: false, Error: null, Total: job.rows.length, Errors: 0, Rows: [], Counts: null }); }
            return respond(200, { JobId: m[1], UserName: 'Anna', ItemName: 'Sable Voyage', Progress: 100, Done: true, Cancelled: false, Error: null, Total: job.rows.length, Errors: 0, Rows: job.rows, Counts: { Files: job.rows.length, Decided: 80 } });
        }
        if (p === 'Babelfin/Admin/Libraries/Refresh') { return respond(202, null); }
        if (p === 'Items') {
            state.itemQueries.push(Object.fromEntries(u.searchParams));
            return respond(200, { Items: [{ Id: 'abababababababababababababababab', Name: 'Copper Kite', Type: 'Movie', ProductionYear: 2001 }, { Id: 'cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd', Name: 'Sable Voyage', Type: 'Series' }] });
        }
        if ((m = /^Babelfin\/Admin\/Users\/([0-9a-f]+)\/CopyTo\/([0-9a-f]+)$/.exec(p))) { return respond(200, { SourceUserId: m[1], TargetUserId: m[2], SkippedItemIds: [] }); }
        //: a copy of the settings first (its file and path in the answer), then the shipped server settings and a fresh
        // seed for everyone; opts.resetFails: the copy can't be written, so nothing changes and the answer is 500.
        if (p === 'Babelfin/Admin/ResetAll' && method === 'POST') {
            state.resetCalls = (state.resetCalls || 0) + 1;
            if (opts.resetFails) { return respond(500, { Message: RESET_FAILED }); }
            state.config = shippedConfig();
            state.docs = { [USER.anna]: freshSeed(USER.anna, 'Anna'), [USER.ben]: freshSeed(USER.ben, 'Ben') };
            return respond(200, { BackupFile: RESET_FILE, BackupPath: RESET_PATH, People: 2 });
        }
        return respond(404, { Message: 'not found: ' + p });
    }
    const server = { state: state, handle: null };
    // The next PUT Admin/Settings answers 400 with these errors.
    server.handle = function (url, init) {
        if (state._reject && init && init.method === 'PUT' && String(url).indexOf('Babelfin/Admin/Settings') >= 0) {
            const errors = state._reject;
            state._reject = null;
            checkExpectedBody(JSON.parse(init.body), new URL(url).searchParams.get('fields'), init.headers, state);
            state.puts.push(JSON.parse(init.body));
            return {
                ok: false, status: 400, headers: { get: () => null },
                text: () => Promise.resolve(JSON.stringify({ Message: 'The settings are not valid.', Errors: errors })),
                json: () => Promise.resolve({ Message: 'The settings are not valid.', Errors: errors })
            };
        }
        return handle(url, init);
    };
    return server;
}

/* ---------- boot the page ---------- */

async function boot(opts) {
    opts = opts || {};
    const server = createServer(opts);
    const document = new Document();
    const html = fs.readFileSync(ROOT + 'Configuration/configPage.html', 'utf8');
    const bodyHtml = html.slice(html.indexOf('<body>') + 6, html.lastIndexOf('</body>'));
    parseInto(document, bodyHtml, document.body);
    const consoleErrors = [];
    const objectCreateCalls = [];
    const origCreate = document.createElement.bind(document);
    document.createElement = function (tag, options) {
        if (options && typeof options === 'object') {
            objectCreateCalls.push(tag);
        }
        return origCreate(tag, options);
    };
    const storage = new Map(Object.entries(opts.storage || {}));
    const ctx = {
        document: document,
        navigator: { language: opts.lang || 'en-US' },
        console: {
            log: () => {},
            info: () => {},
            debug: () => {},
            warn: (...a) => { consoleErrors.push('warn: ' + a.map(String).join(' ')); },
            error: (...a) => { consoleErrors.push('error: ' + a.map((x) => (x && x.stack) || String(x)).join(' ')); }
        },
        setTimeout: setTimeout,
        clearTimeout: clearTimeout,
        setInterval: setInterval,
        clearInterval: clearInterval,
        URL: URL,
        Intl: Intl,
        localStorage: { getItem: (k) => (storage.has(k) ? storage.get(k) : null), setItem: (k, v) => storage.set(k, String(v)), removeItem: (k) => storage.delete(k) },
        confirm: () => true,
        FileReader: function () {},
        fetch: (url, init) => Promise.resolve().then(() => server.handle(String(url), init)),
        ApiClient: {
            getUrl: (p, query) => {
                let url = BASE + '/' + String(p).replace(/^\//, '');
                if (query) {
                    const qs = Object.keys(query).filter((k) => query[k] !== undefined && query[k] !== null).map((k) => encodeURIComponent(k) + '=' + encodeURIComponent(query[k])).join('&');
                    if (qs) { url += '?' + qs; }
                }
                return url;
            },
            accessToken: () => 'token',
            getCurrentUserId: () => 'admin',
            getJSON: (url) => Promise.resolve().then(() => server.handle(String(url), { method: 'GET' })).then((r) => r.json())
        },
        Dashboard: { showLoadingMsg: () => {}, hideLoadingMsg: () => {}, processPluginConfigurationUpdateResult: () => {} },
        Event: Event,
        // The window's own events (BabelfinUI.infoTip follows scrolling and resizing while a tip shows) and its size.
        innerWidth: 1280,
        innerHeight: 800,
        windowListeners: {},
        addEventListener(type, fn) { (this.windowListeners[type] = this.windowListeners[type] || []).push(fn); },
        removeEventListener(type, fn) { const l = this.windowListeners[type] || []; const i = l.indexOf(fn); if (i >= 0) { l.splice(i, 1); } }
    };
    ctx.window = ctx;
    ctx.self = ctx;
    vm.createContext(ctx);
    // <script src> elements load their file from Assets when inserted.
    document._onInsert = (node) => {
        if (node.nodeType === 1 && node.localName === 'script' && node.getAttribute('src')) {
            setTimeout(() => {
                const u = new URL(node.getAttribute('src'));
                const file = ASSETS + u.pathname.replace(/^\/jf\/Babelfin\/assets\//, '');
                try {
                    // opts.patchScript(name, text) → text: a script as the page gets it (to switch one of its guards off).
                    const text = fs.readFileSync(file, 'utf8');
                    vm.runInContext(opts.patchScript ? opts.patchScript(path.basename(file), text) : text, ctx, { filename: path.basename(file) });
                    if (opts.afterScript) { opts.afterScript(path.basename(file), ctx); }
                    if (node.onload) { node.onload(); }
                } catch (e) {
                    consoleErrors.push('script ' + file + ': ' + e.stack);
                    if (node.onerror) { node.onerror(e); }
                }
            }, 0);
        }
    };
    const page = document.querySelector('#babelfinConfigPage');
    const inline = page.querySelectorAll('script').find((s) => !s.getAttribute('src'));
    vm.runInContext(inline.textContent, ctx, { filename: 'configPage-inline.js' });
    page.dispatchEvent(new Event('pageshow'));
    const env = { server: server, document: document, page: page, ctx: ctx, consoleErrors: consoleErrors, objectCreateCalls: objectCreateCalls, storage: storage };
    await waitFor(opts.ready ? () => opts.ready(page) : () => page.querySelector('#bfLibraries table') && page.querySelector('#bfPeople table'), 'page rendered');
    await settle();
    return env;
}

async function settle(n) {
    for (let i = 0; i < (n || 20); i++) {
        await sleep(1);
    }
}

async function waitFor(fn, what, ms) {
    const end = Date.now() + (ms || 4000);
    while (Date.now() < end) {
        if (fn()) { return true; }
        await sleep(5);
    }
    throw new Error('timed out waiting for ' + what);
}

/* ---------- page helpers ---------- */

function isHidden(el) {
    for (let n = el; n && n.nodeType === 1; n = n.parentNode) {
        if (n.classList.contains('hide') || n.hasAttribute('hidden')) { return true; }
        if (n.localName === 'details' && !n.open && n.parentNode && n !== el && !(el.closest('summary') && el.closest('details') === n)) {
            const summary = n.querySelector('summary');
            if (!summary || !summary.contains(el)) { return true; }
        }
    }
    return false;
}

/** Every visible string: text nodes, option texts, optgroup labels, placeholders, titles and aria-labels. skip(el) leaves a subtree out. */
function visibleStrings(root, skip) {
    const out = [];
    const visit = (n) => {
        if (n.nodeType === 3) {
            const s = n.data.trim();
            if (s) { out.push({ text: s, node: n.parentNode }); }
            return;
        }
        if (n.nodeType !== 1) { return; }
        if (['script', 'style', 'datalist'].indexOf(n.localName) >= 0) { return; }
        if (isHidden(n)) { return; }
        if (skip && skip(n)) { return; }
        ['placeholder', 'title', 'aria-label'].forEach((a) => { if (n.getAttribute(a)) { out.push({ text: n.getAttribute(a), node: n }); } });
        if (n.localName === 'optgroup' && n.getAttribute('label')) { out.push({ text: n.getAttribute('label'), node: n }); }
        n.childNodes.forEach(visit);
    };
    visit(root);
    return out;
}

/** A row of the Libraries table by its name. */
function libRow(env, name) {
    return env.page.querySelector('#bfLibraries').querySelectorAll('.bfui-table-wrap tr.bfui-table-row').find((tr) => {
        const th = tr.querySelector('th');
        return th && th.childNodes[0] && th.childNodes[0].nodeType === 3 && th.childNodes[0].data === name;
    });
}
function cells(tr) {
    return [tr.querySelector('.bfui-table-audio select'), tr.querySelector('.bfui-table-subtitles select')];
}
function libSelect(env, name, kind) {
    const tr = libRow(env, name);
    return tr ? cells(tr)[kind === 'audio' ? 0 : 1] : null;
}

function choose(select, value) {
    select.value = value;
    if (select.value !== value) { throw new Error('no option ' + value + ' in ' + select.options.map((o) => o.value).join('|')); }
    select.dispatchEvent(new Event('change', { bubbles: true }));
}

function optionTexts(select) {
    return select.options.map((o) => o.textContent);
}
function hasOption(select, value) {
    return select.options.some((o) => o.value === value);
}
function optionText(select, value) {
    const o = select.options.find((x) => x.value === value);
    return o ? o.textContent : null;
}
function groupsOf(select) {
    const out = [];
    select.children.forEach((c) => {
        if (c.localName === 'optgroup') {
            out.push({ label: c.getAttribute('label'), values: c.children.map((x) => x.value) });
        } else if (out.length && out[out.length - 1].label === null) {
            out[out.length - 1].values.push(c.value);
        } else {
            out.push({ label: null, values: [c.value] });
        }
    });
    return out;
}
function saveBarHidden(env) {
    return isHidden(env.page.querySelector('#bfSaveBar'));
}

async function save(env) {
    const before = env.server.state.puts.length;
    env.page.querySelector('#bfSave').click();
    await waitFor(() => env.server.state.puts.length > before, 'PUT Admin/Settings');
    await settle(30);
    return env.server.state.puts[env.server.state.puts.length - 1];
}

/** The paths where two JSON values differ (null and a missing member count as the same). */
function diff(a, b, base, out) {
    out = out || [];
    base = base || '';
    const norm = (v) => (v === undefined ? null : v);
    a = norm(a);
    b = norm(b);
    if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') {
        if (JSON.stringify(a) !== JSON.stringify(b)) { out.push(base || '(root)'); }
        return out;
    }
    if (Array.isArray(a) !== Array.isArray(b)) { out.push(base); return out; }
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    for (const k of keys) {
        // The PUT boundary checks this request-only metadata independently.
        if (!base && k === 'ExpectedValues') { continue; }
        diff(a[k], b[k], Array.isArray(a) ? base + '[' + k + ']' : (base ? base + '.' : '') + k, out);
    }
    return out;
}

const nz = (v) => (v === undefined ? null : v);
/** Whatsays a choice stores. */
function wanted(kind, value) {
    const code = value.indexOf('lang:') === 0 ? value.slice(5) : null;
    if (kind === 'audio') {
        if (!value) { return { AudioChain: null, ForeignTitles: null }; }
        if (value === 'video') { return { AudioChain: [{ Kind: 'VideoDefault' }], ForeignTitles: null }; }
        if (value === 'mine' || value === 'original') { return { AudioChain: [{ Kind: value === 'mine' ? 'MyLanguages' : 'OriginalLanguage' }, { Kind: 'VideoDefault' }], ForeignTitles: null }; }
        return { AudioChain: [{ Kind: 'Language', Language: code }, { Kind: 'VideoDefault' }], ForeignTitles: null };
    }
    const table = { '': [null, null, null], video: ['VideoDefault', 'VideoDefault', null], off: ['Off', 'Off', null], smart: ['PartialOnly', 'Full', null], signs: ['PartialOnly', 'PartialOnly', null] };
    const v = table[value] || ['Full', 'Full', [code]];
    return { WhenAudioUnderstood: v[0], WhenAudioNotUnderstood: v[1], SubtitleLanguages: v[2] };
}
function storedIn(layer, kind) {
    const fields = kind === 'audio' ? ['AudioChain', 'ForeignTitles'] : ['WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'SubtitleLanguages'];
    const out = {};
    fields.forEach((f) => { out[f] = nz(layer ? layer[f] : null); });
    return out;
}
function libEntry(config, id) {
    return (config.Libraries || []).find((l) => l.LibraryId === id) || null;
}

/* ---------- helpers ---------- */

/** The mode control of the table inside root: { group, same, each, mode }. */
function modeOf(root) {
    const group = root.querySelector('[role="radiogroup"]');
    const radios = group ? group.querySelectorAll('[role="radio"]') : [];
    const same = radios.find((r) => r.getAttribute('data-mode') === 'same') || null;
    const each = radios.find((r) => r.getAttribute('data-mode') === 'each') || null;
    const mode = same && same.getAttribute('aria-checked') === 'true' ? 'same' : (each && each.getAttribute('aria-checked') === 'true' ? 'each' : null);
    return { group, radios, same, each, mode };
}
function key(el, name) {
    const e = new Event('keydown', { bubbles: true });
    e.key = name;
    el.dispatchEvent(e);
    return e;
}
function rowNamesIn(root) {
    return root.querySelectorAll('.bfui-table-wrap tr.bfui-table-row').map((tr) => tr.querySelector('th').childNodes[0].data);
}
/** A collapsible group of Advanced or Troubleshooting by id: { element, button, body, open }. */
function agroup(env, id) {
    const element = env.page.querySelector('#bf-group-' + id);
    if (!element) { return null; }
    const button = element.querySelector('.bfui-disclosure-button');
    const body = element.querySelector('.bfui-disclosure-body');
    return { element, button, body, open: !!button && button.getAttribute('aria-expanded') === 'true' && !body.hidden };
}
async function openFold(env, name) {
    const fold = env.page.querySelector('details[data-fold="' + name + '"]');
    if (!fold.open) {
        fold.open = true;
        await settle(40);
    }
    return fold;
}
async function openGroup(env, id) {
    const g = agroup(env, id);
    if (!g.open) {
        g.button.click();
        await settle(30);
    }
    return agroup(env, id);
}
function untouched(before, after) {
    return UNTOUCHED.concat(['Everyday.IfNothingMatches']).filter((k) => {
        const get = (o) => k.split('.').reduce((x, p) => (x ? x[p] : undefined), o);
        return JSON.stringify(get(before)) !== JSON.stringify(get(after));
    });
}

/** A node's text without its BabelfinUI.infoTips (their "i" and their explanation), trimmed. */
function plainText(node) {
    const out = [];
    const visit = (n) => {
        if (n.nodeType === 3) { out.push(n.data); return; }
        if (n.nodeType === 1 && n.classList.contains('bfui-info-wrap')) { return; }
        n.childNodes.forEach(visit);
    };
    visit(node);
    return out.join('').trim();
}

/* ---------- helpers ---------- */

/**
 * The BabelfinUI.infoTip inside a node: { wrap, button, tip }. The tip is found by the button's aria-describedby: while it shows it sits at
 * the end of <body>, and it goes back into its wrap when hidden.
 */
function tipParts(node) {
    const wrap = node ? node.querySelector('.bfui-info-wrap') : null;
    const button = wrap ? wrap.querySelector('button.bfui-info') : null;
    const tip = button ? button.ownerDocument.getElementById(button.getAttribute('aria-describedby')) : null;
    return { wrap, button, tip };
}
function describedBy(el) {
    return (el.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean);
}
function tipShown(p) {
    return !!p.tip && !p.tip.hidden && p.button.getAttribute('aria-expanded') === 'true' && p.tip.parentNode === p.button.ownerDocument.body;
}
function tipHidden(p) {
    return !!p.tip && p.tip.hidden && p.button.getAttribute('aria-expanded') === 'false' && p.tip.parentNode === p.wrap;
}
/** Dispatches an event (not bubbling unless extra.bubbles); returns it. */
function fire(el, type, extra) {
    const e = new Event(type, { bubbles: !!(extra && extra.bubbles) });
    Object.assign(e, extra || {});
    el.dispatchEvent(e);
    return e;
}

/* ---------- helpers ---------- */

/**
 * The menu problemsforbids outside the "Audio and subtitles" tables: an option starting with "Default (" (or the page
 * language's own "Default (" form, given as prefix), and a dropdown listing the same label twice. Every select of the page counts.
 */
function menuProblems(env, prefix) {
    const out = [];
    env.page.querySelectorAll('select').filter((s) => !s.closest('.bfui-table, section.bfui-genres')).forEach((s) => {
        const texts = s.options.map((o) => o.textContent.trim());
        const name = s.getAttribute('aria-label') || s.id || '?';
        texts.forEach((x, i) => {
            if (/^Default \(/.test(x) || (prefix && x.indexOf(prefix) === 0)) { out.push(name + ': "' + x + '"'); }
            if (texts.indexOf(x) !== i) { out.push(name + ' lists "' + x + '" twice'); }
        });
    });
    return out;
}
/** Answers the open BabelfinUI.confirm dialog. */
async function dialogAnswer(env, yes) {
    await settle(5);
    const box = env.document.querySelector('.bfui-dialog');
    ok(!!box, 'a confirmation dialog is open');
    if (!box) { return null; }
    const text = box.textContent;
    box.querySelectorAll('button')[yes ? 1 : 0].click();
    await settle(10);
    return text;
}
/** Adds a language through a BabelfinUI.languageList's picker inside root. */
async function addLanguage(root, query, code) {
    const input = root.querySelector('.bfui-picker input');
    ok(!!input, 'the language list has a search box');
    input.value = query;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle(2);
    const li = root.querySelectorAll('li').find((x) => x.getAttribute('data-code') === code);
    ok(!!li, 'the picker offers ' + code);
    if (li) { li.click(); }
    await settle(5);
}
/** The People row cells (Audio, Subtitles) by name. */
function peopleCells(env, name) {
    const tr = env.page.querySelector('#bfPeople table').querySelectorAll('tbody tr').find((r) => r.querySelector('th') && r.querySelector('th').textContent === name);
    return tr ? tr.querySelectorAll('td').slice(0, 2).map((td) => td.textContent) : null;
}

/* ---------- the tests ---------- */

const BANNED = ['inherit', 'layer', 'seed', 'assumed', 'dry run', 'version', 'upgrade', 'milestone', 'lookups', 'wrapped', 'we ', 'when needed', 'other language…', 'fallback', 'coverage', 'simulat', 'coming soon'];
const VIDEO_HELP = "Video default: the track the video file marks as default (or the viewer's Jellyfin playback settings, if they set any).";
const SMART_HELP = "When the audio isn't in the viewer's languages: full subtitles when the audio isn't in one of the viewer's languages, otherwise only signs and songs.";
const SMART = "When the audio isn't in the viewer's languages";
const LIB_ROWS = ['Anime', 'Movies', 'Shows', 'Deutsche Filme', 'Mixed'];
const SAME = 'Same for every library';
const EACH = 'Choose for each library';
const PER_LIBRARY = 'Default (set by the server for each library)';

async function main() {
    const en = JSON.parse(fs.readFileSync(ASSETS + 'i18n-admin/en.json', 'utf8'));
    const enUi = JSON.parse(fs.readFileSync(ASSETS + 'i18n/en.json', 'utf8'));

    // 0. The English strings themselves.
    section('0 en.json wording');
    Object.keys(en).forEach((k) => {
        if (typeof en[k] !== 'string') { return; }
        const low = en[k].toLowerCase();
        BANNED.forEach((b) => ok(low.indexOf(b) < 0, 'en.json ' + k + ' has no "' + b + '"', en[k]));
        ok(en[k].indexOf('!') < 0, 'en.json ' + k + ' has no exclamation mark');
    });

    const env = await boot({});
    const { page, server } = env;
    const dash = page.querySelector('.bf-dash');
    const libs = () => page.querySelector('#bfLibraries');

    // 1. Visible sections: header, Libraries, People, then collapsed Advanced and Troubleshooting; nothing else.
    section('1 page shape');
    const visibleChildren = dash.children.filter((c) => !isHidden(c));
    const shape = visibleChildren.map((c) => c.localName + (c.getAttribute('data-section') ? '[' + c.getAttribute('data-section') + ']' : c.getAttribute('data-fold') ? '[' + c.getAttribute('data-fold') + (c.open ? ',open' : ',closed') + ']' : c.className ? '.' + c.className.split(' ')[0] : ''));
    eq(shape, ['div.bf-head', 'section[libraries]', 'section[people]', 'details[advanced,closed]', 'details[tools,closed]', 'details[backup,closed]'], 'visible sections are exactly header, Defaults for everyone, People, Advanced, Troubleshooting and Back up and restore (closed)');
    const head = page.querySelector('.bf-head');
    eq(head.children.filter((c) => !isHidden(c)).map((c) => c.localName + ':' + plainText(c)), ['h2:Babelfin', 'p:' + en['page.lede'], 'div:' + en['page.enabled']], 'header shows only the name, the lede and Enabled (with its infoTip)');
    ok(page.querySelector('#bfEnabled').checked === true, 'Enabled reflects the configuration');
    ok(saveBarHidden(env), 'the Save bar is hidden while nothing changed');
    eq(page.querySelectorAll('h3').filter((h) => !h.closest('section.bfui-genres')).map((x) => plainText(x)), ['Defaults for everyone', 'People', 'Advanced', 'Troubleshooting', 'Back up and restore'], 'section titles: Defaults for everyone, People, Advanced, Troubleshooting, Back up and restore');
    eq(page.querySelector('section[data-section="libraries"] .bf-section-lede').textContent, 'People who leave a row on Default get these. Changes apply right away.', 'the line under Defaults for everyone');
    eq(dash.querySelectorAll('section[data-section], details[data-fold]').map((x) => x.getAttribute('data-section') || x.getAttribute('data-fold')).slice(-1), ['backup'], 'Back up and restore is the last section');
    ok(server.state.puts.length === 0 && server.state.userPuts.length === 0, 'nothing is saved on load');
    ok(!server.state.requests.some((r) => /Activity|Simulate|Coverage/.test(r)), 'no Activity, Simulate or Coverage request on load');

    // 2. The Libraries table: the mode control, the admin's wording, neutral defaults, every language in the dropdown.
    section('2 Libraries table shape');
    {
        const box = libs();
        const control = modeOf(box);
        ok(!!control.group, 'the Libraries card has a radiogroup');
        eq([control.group.getAttribute('aria-label'), control.radios.map((r) => r.textContent)], ['Audio and subtitles', [SAME, EACH]], 'labelled "Audio and subtitles", with the two choices');
        eq(control.mode, 'each', 'PerLibraryDefaults null and Mixed has a table choice: automatic per library');
        eq(rowNamesIn(box), LIB_ROWS, 'one row per library, no All libraries row');
        eq(box.querySelectorAll('.bfui-table-wrap .bfui-table thead th').map((x) => x.textContent), ['Library', 'Audio', 'Subtitles'], 'library table columns');
        eq(box.querySelectorAll('.bfui-table-help').map((p) => p.textContent), [VIDEO_HELP, SMART_HELP], 'the two help lines, in the admin wording');
        LIB_ROWS.forEach((name) => {
            const [a, s] = cells(libRow(env, name));
            eq([a.getAttribute('aria-label'), s.getAttribute('aria-label')], ['Audio for ' + name, 'Subtitles for ' + name], name + ': aria-labels');
            eq([optionTexts(a)[0], optionTexts(s)[0]], ['Video default', 'Video default'], name + ': every default on the admin page is "Video default"');
            ok(!hasOption(a, 'video') && !hasOption(s, 'video'), name + ': no explicit Video default');
            [a, s].forEach((sel) => ok(sel.options.every((o) => /^(|video|mine|original|off|smart|signs|custom|lang:[A-Za-z0-9-]+)$/.test(o.value)), name + ': option values follow the settings contract'));
        });
        eq(LIB_ROWS.map((n) => cells(libRow(env, n)).map((s) => s.value).join('/')), ['/', '/', '/', '/', '/custom'], 'values: nothing set, Mixed subtitles Custom');
        control.same.click();
        await settle(5);
        eq([modeOf(box).mode, rowNamesIn(box)], ['same', ['All libraries']], 'Same for every library: one row');
        const all = cells(libRow(env, 'All libraries'));
        eq([optionTexts(all[0])[0], optionTexts(all[1])[0]], ['Video default', 'Video default'], 'All libraries: "Video default"');
        const langs = ['en', 'ja', 'de', 'fr', 'es'].concat(EXTRAS);
        const names = (c) => new Intl.DisplayNames(['en-US'], { type: 'language' }).of(c);
        const collator = new Intl.Collator('en-US');
        const sorted = langs.slice().sort((x, y) => collator.compare(names(x), names(y))).map((c) => 'lang:' + c);
        eq(groupsOf(all[0]), [{ label: null, values: ['', 'mine', 'original'] }, { label: 'All languages', values: sorted }], 'audio: Default, the two choices, then All languages (no My languages group for admin)');
        eq(groupsOf(all[1]), [{ label: null, values: ['', 'off', 'smart', 'signs'] }, { label: 'All languages', values: sorted }], 'subtitles: Default, Off, smart, signs, All languages');
        eq(optionTexts(all[1]).slice(0, 4), ['Video default', 'Off', SMART, 'Signs and songs only'], 'subtitle labels in the admin wording');
        const texts = page.querySelectorAll('#bfLibraries option').map((o) => o.textContent);
        ['Original language', 'Other language…', 'When needed', PER_LIBRARY].forEach((bad) => ok(texts.indexOf(bad) < 0, 'no option labelled "' + bad + '"'));
        ok(!page.querySelector('#bfLibraries input'), 'no search box in the table');
        ok(!saveBarHidden(env), 'picking a mode is a change');
        page.querySelector('#bfDiscard').click();
        await settle(20);
        eq([modeOf(libs()).mode, saveBarHidden(env)], ['each', true], 'Cancel puts the automatic mode back');
    }

    // 3. The mode control: mouse and keyboard, the saved field, both modes' values kept, automatic detection.
    section('3 Libraries mode');
    {
        let control = modeOf(libs());
        eq([control.same.getAttribute('tabindex'), control.each.getAttribute('tabindex')], ['-1', '0'], 'roving tab order');
        // Keyboard.
        control.each.focus();
        let e = key(control.each, 'ArrowLeft');
        control = modeOf(libs());
        ok(e.defaultPrevented, 'ArrowLeft is handled');
        eq([control.mode, env.document.activeElement === control.same, rowNamesIn(libs())], ['same', true, ['All libraries']], 'ArrowLeft picks and focuses Same');
        key(control.same, 'ArrowLeft');
        control = modeOf(libs());
        eq([control.mode, env.document.activeElement === control.each], ['each', true], 'ArrowLeft wraps');
        key(control.each, 'Home');
        eq(modeOf(libs()).mode, 'same', 'Home picks Same');
        key(modeOf(libs()).same, 'End');
        eq(modeOf(libs()).mode, 'each', 'End picks Choose for each library');
        e = key(modeOf(libs()).each, 'ArrowDown');
        ok(e.defaultPrevented && modeOf(libs()).mode === 'same', 'ArrowDown wraps to Same');
        key(modeOf(libs()).same, 'ArrowUp');
        eq(modeOf(libs()).mode, 'each', 'ArrowUp wraps to Choose for each library');
        e = key(modeOf(libs()).each, 'Tab');
        ok(!e.defaultPrevented && modeOf(libs()).mode === 'each', 'Tab is left to the page');
        key(modeOf(libs()).each, 'ArrowRight');
        eq(modeOf(libs()).mode, 'same', 'ArrowRight wraps to Same');
        // Same: set All libraries; switch; set a library; switch back; the values of both modes stay.
        const before = clone(server.state.config);
        choose(libSelect(env, 'All libraries', 'audio'), 'lang:ja');
        modeOf(libs()).each.click();
        eq(libSelect(env, 'Movies', 'audio').value, '', 'the library rows are untouched by the All libraries choice');
        eq(optionTexts(libSelect(env, 'Movies', 'audio'))[0], 'Video default', 'and their Default stays "Video default" (no live composition)');
        choose(libSelect(env, 'Movies', 'subtitles'), 'off');
        modeOf(libs()).same.click();
        eq(libSelect(env, 'All libraries', 'audio').value, 'lang:ja', 'back to Same: the All libraries choice is still there');
        modeOf(libs()).each.click();
        eq(libSelect(env, 'Movies', 'subtitles').value, 'off', 'back to per library: the Movies choice is still there');
        modeOf(libs()).same.click();
        let body = await save(env);
        const moviesAt = before.Libraries.findIndex((l) => l.LibraryId === LIB.movies);
        eq(diff(before, body).sort(), ['Everyday.AudioChain', 'Libraries[' + before.Libraries.length + ']', 'PerLibraryDefaults'].sort(), 'the save stores PerLibraryDefaults and the edited fields only');
        ok(moviesAt < 0, 'Movies had no entry, so a new one was added');
        eq([body.PerLibraryDefaults, storedIn(body.Everyday, 'audio'), storedIn(libEntry(body, LIB.movies).Settings, 'subtitles')], [false, wanted('audio', 'lang:ja'), wanted('subtitles', 'off')], 'the mode last picked and both choices saved');
        eq(untouched(before, body), [], 'rules, show and movie defaults, burned-in approvals, the original-language rule, timing, test mode and IfNothingMatches untouched');
        await settle(20);
        eq([modeOf(libs()).mode, rowNamesIn(libs())], ['same', ['All libraries']], 'after saving: Same');
        // Back to per library: only PerLibraryDefaults changes.
        const b2 = clone(server.state.config);
        modeOf(libs()).each.click();
        body = await save(env);
        eq(diff(b2, body), ['PerLibraryDefaults'], 'switching back saves only PerLibraryDefaults');
        eq(body.PerLibraryDefaults, true, 'true saved');
        // Clean up: back to Default everywhere and automatic is not reachable again (picked modes stay picked).
        choose(libSelect(env, 'Movies', 'subtitles'), '');
        modeOf(libs()).same.click();
        choose(libSelect(env, 'All libraries', 'audio'), '');
        body = await save(env);
        eq([body.PerLibraryDefaults, nz(body.Everyday.AudioChain), libEntry(body, LIB.movies) && storedIn(libEntry(body, LIB.movies).Settings, 'subtitles')], [false, null, wanted('subtitles', '')], 'both choices back to Default');
        // Automatic detection, both ways, and a stored value winning.
        for (const [label, config, mode] of [
            ['null, Mixed has a table choice', initialConfig, 'each'],
            ['null, no library table choice', sameConfig, 'same'],
            ['false with a library table choice', () => { const c = initialConfig(); c.PerLibraryDefaults = false; return c; }, 'same'],
            ['true with no library table choice', () => { const c = sameConfig(); c.PerLibraryDefaults = true; return c; }, 'each']
        ]) {
            const e2 = await boot({ config });
            eq(modeOf(e2.page.querySelector('#bfLibraries')).mode, mode, 'admin table, ' + label + ' → ' + mode);
            ok(saveBarHidden(e2), label + ': nothing to save on load');
        }
        // Automatic per library: the last library choice going back to Default stores the mode, so the rows never flip.
        const e3 = await boot({ config: initialConfig });
        const b3 = clone(e3.server.state.config);
        choose(libSelect(e3, 'Mixed', 'subtitles'), '');
        eq(modeOf(e3.page.querySelector('#bfLibraries')).mode, 'each', 'still per library after the last library choice goes');
        const body3 = await save(e3);
        eq(diff(b3, body3).sort(), ['Libraries[2].Settings.WhenAudioNotUnderstood', 'Libraries[2].Settings.WhenAudioUnderstood', 'PerLibraryDefaults'], 'saved: the Mixed fields and PerLibraryDefaults true');
        eq(body3.PerLibraryDefaults, true, 'PerLibraryDefaults true');
    }

    // 4. Every option of both dropdowns, on the All libraries row (Same) and on library rows (per library), saved through the Save bar.
    section('4 every option round trip');
    const roundTrip = async (name, kind, values) => {
        const libId = name === 'All libraries' ? null : libraries().find((l) => l.Name === name).Id;
        let count = 0;
        for (const value of values) {
            const before = clone(server.state.config);
            const sel = libSelect(env, name, kind);
            choose(sel, value);
            eq(sel.value, value, name + ' ' + kind + ' ' + (value || 'Default') + ': the dropdown shows the choice');
            ok(!saveBarHidden(env), name + ' ' + kind + ' ' + (value || 'Default') + ': the Save bar shows');
            const body = await save(env);
            const layer = libId ? (libEntry(body, libId) || {}).Settings : body.Everyday;
            eq(storedIn(layer, kind), wanted(kind, value), name + ' ' + kind + ' ' + (value || 'Default') + ': saved fields');
            const changed = diff(before, body);
            const fields = kind === 'audio' ? ['AudioChain', 'ForeignTitles'] : ['WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'SubtitleLanguages'];
            let allowed;
            if (!libId) {
                allowed = (p) => fields.some((f) => p === 'Everyday.' + f || p.indexOf('Everyday.' + f + '[') === 0 || p.indexOf('Everyday.' + f + '.') === 0);
            } else {
                const at = before.Libraries.findIndex((l) => l.LibraryId === libId);
                allowed = at < 0
                    ? (p) => p === 'Libraries[' + before.Libraries.length + ']'
                    : (p) => fields.some((f) => p.indexOf('Libraries[' + at + '].Settings.' + f) === 0);
            }
            ok(changed.length > 0 && changed.every(allowed), name + ' ' + kind + ' ' + (value || 'Default') + ': only this cell changed', changed);
            if (libId) {
                const entryBefore = libEntry(before, libId);
                const entryAfter = libEntry(body, libId);
                ok(!!entryAfter, name + ': the entry is there');
                if (entryBefore) {
                    eq([entryAfter.OriginalAudio, entryAfter.Name], [entryBefore.OriginalAudio, entryBefore.Name], name + ' ' + (value || 'Default') + ': OriginalAudio and name kept');
                    Object.keys(entryBefore.Settings || {}).filter((f) => fields.indexOf(f) < 0).forEach((f) => eq(nz(entryAfter.Settings[f]), nz(entryBefore.Settings[f]), name + ': other setting ' + f + ' kept'));
                } else {
                    eq([entryAfter.OriginalAudio, entryAfter.Name], ['Auto', name], name + ': a new entry is Automatic, with the name');
                }
            }
            eq(libSelect(env, name, kind).value, value, name + ' ' + kind + ' ' + (value || 'Default') + ': shows again after the save');
            ok(saveBarHidden(env), 'Save bar hidden after saving');
            count++;
        }
        return count;
    };
    {
        const opts = (name, kind) => libSelect(env, name, kind).options.map((o) => o.value).filter((v) => v !== '' && v !== 'custom');
        let total = 0;
        eq(modeOf(libs()).mode, 'same', 'starting in Same');
        total += await roundTrip('All libraries', 'audio', opts('All libraries', 'audio').concat(['']));
        total += await roundTrip('All libraries', 'subtitles', opts('All libraries', 'subtitles').concat(['']));
        modeOf(libs()).each.click();
        await save(env);
        for (const name of ['Movies', 'Shows', 'Deutsche Filme']) {
            ok(!hasOption(libSelect(env, name, 'audio'), 'video'), name + ': no explicit Video default (the default already is the video default)');
            total += await roundTrip(name, 'audio', opts(name, 'audio').concat(['']));
            total += await roundTrip(name, 'subtitles', opts(name, 'subtitles').concat(['']));
        }
        console.log('admin table round trips: ' + total);
        const cfg = server.state.config;
        eq([libEntry(cfg, LIB.shows).OriginalAudio, libEntry(cfg, LIB.shows).Settings.UnlabelledAudio], ['Yes', 'Unknown'], 'Shows (Yes) survives every edit with its other setting');
        eq(libEntry(cfg, LIB.german).OriginalAudio, 'No', 'Deutsche Filme (No) survives every edit');
        ok(LIB_ROWS.every((n) => cells(libRow(env, n)).every((s) => optionTexts(s)[0] === 'Video default')), 'everything back to Video default');
        eq(untouched(initialConfig(), cfg), [], 'after every round trip the fields the page no longer edits are untouched');
    }

    // 5. Repeated changes and Cancel.
    section('5 repeated changes and Cancel');
    {
        const sel = libSelect(env, 'Deutsche Filme', 'subtitles');
        const others = () => LIB_ROWS.map((n) => cells(libRow(env, n)).map((s) => s.value).join('/'));
        const before = others();
        for (const v of ['lang:de', 'off', 'lang:de', '']) {
            choose(sel, v);
            ok(libSelect(env, 'Deutsche Filme', 'subtitles') === sel && sel.value === v, 'Deutsche Filme subtitles → ' + (v || 'Default') + ': same dropdown, shows the choice');
            eq(others().filter((x, i) => LIB_ROWS[i] !== 'Deutsche Filme'), before.filter((x, i) => LIB_ROWS[i] !== 'Deutsche Filme'), 'other rows keep their values');
        }
        choose(libSelect(env, 'Anime', 'audio'), 'lang:ja');
        modeOf(libs()).same.click();
        ok(!saveBarHidden(env), 'changes pending');
        page.querySelector('#bfDiscard').click();
        await settle(20);
        ok(saveBarHidden(env), 'Cancel hides the Save bar');
        eq([modeOf(libs()).mode, libSelect(env, 'Anime', 'audio').value], ['each', ''], 'Cancel puts the mode and the table back');
        const puts = server.state.puts.length;
        await settle(10);
        eq(server.state.puts.length, puts, 'Cancel saves nothing');
    }

    // 6. Saving reads the settings fresh: a change made elsewhere since the page loaded is kept.
    section('6 fresh save');
    {
        server.state.config.DryRun = false;
        server.state.config.Keywords.SdhWords = ['sdh', 'cc'];
        server.state.config.Libraries.push({ LibraryId: 'f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9', Name: 'Added elsewhere', OriginalAudio: 'Yes', Settings: {} });
        const before = clone(server.state.config);
        choose(libSelect(env, 'Movies', 'audio'), 'lang:es');
        const body = await save(env);
        ok(body.DryRun === false && JSON.stringify(body.Keywords.SdhWords) === JSON.stringify(['sdh', 'cc']), 'changes made elsewhere are kept');
        ok(!!libEntry(body, 'f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9'), 'an entry added elsewhere is kept');
        eq(diff(before, body).every((p) => /^Libraries\[\d+\]/.test(p)), true, 'only the Movies entry changed');
        choose(libSelect(env, 'Movies', 'audio'), '');
        await save(env);
        server.state.config.DryRun = true;
    }

    // 7. Header Enabled: only Enabled changes.
    section('7 Enabled');
    {
        const before = clone(server.state.config);
        page.querySelector('#bfEnabled').click();
        const body = await save(env);
        eq([diff(before, body), body.Enabled], [['Enabled'], false], 'Enabled saves alone');
        page.querySelector('#bfEnabled').click();
        await save(env);
    }

    // 8. A validation error shows next to its row (or next to the table when the row isn't shown in this mode).
    section('8 validation errors');
    {
        const at = server.state.config.Libraries.findIndex((l) => l.LibraryId === LIB.shows);
        server.state._reject = [{ Path: 'libraries[' + at + '].settings.audioChain', Message: 'is not valid here' }];
        choose(libSelect(env, 'Shows', 'audio'), 'lang:ja');
        page.querySelector('#bfSave').click();
        await settle(40);
        const err = libRow(env, 'Shows').querySelector('.bf-field-error');
        ok(err && !isHidden(err) && err.textContent.indexOf('is not valid here') >= 0, 'a validation error appears next to the library row', err && err.textContent);
        eq(libSelect(env, 'Shows', 'audio').value, 'lang:ja', 'the choice stays while the error shows');
        page.querySelector('#bfDiscard').click();
        await settle(20);
        modeOf(libs()).same.click();
        server.state._reject = [{ Path: 'everyday.whenAudioUnderstood', Message: 'bad subtitles' }];
        choose(libSelect(env, 'All libraries', 'subtitles'), 'off');
        page.querySelector('#bfSave').click();
        await settle(40);
        const err2 = libRow(env, 'All libraries').querySelector('.bf-field-error');
        ok(err2 && !isHidden(err2) && err2.textContent.indexOf('bad subtitles') >= 0, 'an Everyday error appears on the All libraries row', err2 && err2.textContent);
        page.querySelector('#bfDiscard').click();
        await settle(20);
        ok(saveBarHidden(env) && modeOf(libs()).mode === 'each', 'Cancel discards the changes');
    }

    // 9. People: the editor has the person's two-mode table with their Defaults; saves store PerLibrary and the edits only.
    section('9 People');
    const prow = (e, name) => e.page.querySelector('#bfPeople table').querySelectorAll('tbody tr').find((tr) => tr.querySelector('th') && tr.querySelector('th').textContent === name);
    const editor = (e) => e.page.querySelector('.bf-editor-row');
    const esel = (e, rowName, kind) => {
        const tr = editor(e).querySelectorAll('.bfui-table-wrap tr.bfui-table-row').find((r) => r.querySelector('th').textContent === rowName);
        return tr ? tr.querySelector('.bfui-table-' + kind + ' select') : null;
    };
    const editorSave = async (e) => {
        const old = editor(e);
        const b = old.querySelectorAll('button').find((x) => x.textContent === 'Save');
        const n = e.server.state.userPuts.length;
        ok(!b.disabled, 'Save is enabled');
        b.click();
        await waitFor(() => e.server.state.userPuts.length > n, 'PUT person');
        await waitFor(() => !b.classList.contains('bf-busy') && b.disabled, 'person save completed');
        ok(editor(e) === old && !!old.querySelector('.bfui-table'), 'Save preserves the live editor and its input handlers');
        await settle(5);
        return e.server.state.userPuts[e.server.state.userPuts.length - 1];
    };
    const people = page.querySelector('#bfPeople table');
    eq(people.querySelectorAll('thead th').map((x) => x.textContent), ['Name', 'Audio', 'Subtitles', 'Edit'], 'people columns: Name, Audio, Subtitles, Edit');
    ok(!!people.querySelector('thead th .bf-sr'), 'the Edit header is for screen readers (every row\'s button reads Edit)');
    ok(people.querySelectorAll('tbody tr').every((tr) => tr.textContent.indexOf('(detected)') < 0), 'no (detected) marker in the table');
    eq(['Anna', 'Ben', 'Cleo'].map((n) => prow(env, n).querySelectorAll('td').slice(0, 2).map((td) => td.textContent)), [['Set per library', 'Set per library'], ['Default', 'Default'], ['Default', 'Default']],
        'Anna chooses per library, Ben set nothing, Cleo has no file');
    ok(!server.state.requests.some((r) => /^GET Babelfin\/Admin\/Users\/[0-9a-f]+\/Settings/.test(r)), 'the People table reads GET Admin/Users only (no person file is opened)');
    prow(env, 'Anna').querySelector('button').click();
    await waitFor(() => page.querySelector('.bf-editor-row .bf-person-name'), 'person editor');
    await settle(10);
    {
        const ed = editor(env);
        const etext = ed.textContent;
        ok(etext.indexOf('Babelfin chooses tracks for this person') >= 0, 'editor has the on/off switch');
        ok(etext.indexOf('My languages') >= 0 && etext.indexOf('Languages understood') < 0, 'editor has "My languages"');
        ok(ed.querySelector('.bf-person-head .bf-detected') && ed.querySelector('.bf-person-head .bf-detected').textContent === '(detected)', 'the (detected) marker shows in the editor');
        ok(ed.querySelectorAll('button, label').every((b) => b.textContent.trim() !== 'Lock'), 'no lock toggles');
        ok(etext.indexOf('Copy settings to…') >= 0, 'editor has Copy settings to…');
        const control = modeOf(ed);
        eq([control.group && control.group.getAttribute('aria-label'), control.radios.map((r) => r.textContent)], ['Audio and subtitles', [SAME, EACH]], 'the editor has the mode control');
        eq(control.mode, 'each', 'Anna has an Anime choice: automatic per library');
        eq(rowNamesIn(ed), LIB_ROWS, 'Anna\'s rows: her libraries');
        eq(esel(env, 'Anime', 'subtitles').value, 'off', 'Anna\'s Anime subtitles (Off) show');
        eq(groupsOf(esel(env, 'Anime', 'audio')).map((g) => g.label), [null, 'My languages', 'All languages'], 'the person table has My languages');
        eq(optionText(esel(env, 'Anime', 'subtitles'), 'smart'), "When the audio isn't in my languages", 'the person wording');
        eq(ed.querySelectorAll('.bfui-table-help').map((p) => p.textContent), [enUi['ui.table.videoDefaultHelp'], enUi['ui.table.smartHelp']], 'person help lines');
        // Switch to Same, set All libraries, save: PerLibrary false + the Everyday fields; the Anime entry and Locks untouched.
        control.same.click();
        await settle(5);
        eq(rowNamesIn(editor(env)), ['All libraries'], 'Same: the All libraries row');
        choose(esel(env, 'All libraries', 'audio'), 'lang:ja');
        let put = await editorSave(env);
        const annaBefore = personDocs()[USER.anna];
        eq(diff(annaBefore, put.body).sort(), ['Everyday.AudioChain', 'PerLibrary'], 'person save: exactly PerLibrary and the edited field');
        eq([put.body.PerLibrary, put.body.Libraries, put.body.Locks], [false, annaBefore.Libraries, annaBefore.Locks], 'PerLibrary false; the Anime entry and Locks untouched');
        eq(modeOf(editor(env)).mode, 'same', 'the editor shows Same after saving');
        eq(prow(env, 'Anna').querySelectorAll('td').slice(0, 2).map((td) => td.textContent), ['Japanese', 'Default'], 'the People row follows the save: Japanese, Default');
        // Per library again (keyboard): the Anime entry is still there.
        key(modeOf(editor(env)).same, 'ArrowRight');
        eq([modeOf(editor(env)).mode, esel(env, 'Anime', 'subtitles').value], ['each', 'off'], 'keyboard: per library again, the Anime choice still there');
        choose(esel(env, 'Movies', 'audio'), 'lang:fil');
        put = await editorSave(env);
        const b1 = server.state.userPuts[server.state.userPuts.length - 2].body;
        eq(diff(b1, put.body).sort(), ['Libraries[1]', 'PerLibrary'], 'per library: PerLibrary and the new Movies entry');
        eq(put.body.Everyday.AudioChain, [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }], 'the All libraries choice is kept while per library');
        // Every option of a library row through the editor.
        await waitFor(() => esel(env, 'Movies', 'audio'), 'editor table');
        let count = 0;
        for (const value of ['off', 'smart', 'lang:zh-Hant', 'lang:en', '']) {
            const before = clone(server.state.docs[USER.anna]);
            choose(esel(env, 'Movies', 'subtitles'), value);
            put = await editorSave(env);
            const changed = diff(before, put.body);
            ok(changed.length > 0 && changed.every((p) => p.indexOf('Libraries[1]') === 0), 'Anna Movies subtitles ' + (value || 'Default') + ': only this cell changed', changed);
            eq(storedIn((put.body.Libraries.find((l) => l.LibraryId === LIB.movies) || {}).Settings, 'subtitles'), wanted('subtitles', value), 'Anna Movies subtitles ' + (value || 'Default') + ': saved fields');
            eq(esel(env, 'Movies', 'subtitles').value, value, 'Anna Movies subtitles ' + (value || 'Default') + ': shows again after saving');
            count++;
        }
        console.log('person editor round trips: ' + count);
        ok(server.state.puts.filter((b) => b && b.Enabled === undefined).length === 0, 'person saves do not touch Admin/Settings');
    }
    {
        // The editor's Default labels come from Admin/Users/{id}/Defaults: a library row reads its own row, All libraries what they agree on.
        const MINE_ROW = { AudioChain: [{ Kind: 'MyLanguages' }], WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: [], Understood: ['de', 'en'], Reads: ['de'], AudioFromLibrary: false, SubtitlesFromLibrary: false };
        const JA_ROW = Object.assign({}, MINE_ROW, { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }], AudioFromLibrary: true });
        const e2 = await boot({ userDefaults: MINE_ROW, userLibraryDefaults: { [LIB.movies]: JA_ROW } });
        prow(e2, 'Ben').querySelector('button').click();
        await waitFor(() => e2.page.querySelector('.bf-editor-row .bf-person-name'), 'editor 2');
        await settle(10);
        eq(modeOf(editor(e2)).mode, 'same', 'Ben has no library choices: Same');
        eq(rowNamesIn(editor(e2)).indexOf('Anime'), -1, 'Ben can\'t see Anime');
        eq([optionTexts(esel(e2, 'All libraries', 'audio'))[0], optionTexts(esel(e2, 'All libraries', 'subtitles'))[0]], [PER_LIBRARY, 'Default (' + "When the audio isn't in my languages" + ')'], 'All libraries: audio differs between libraries (Japanese in Movies), subtitles agree');
        ok(hasOption(esel(e2, 'All libraries', 'audio'), 'video'), 'Video default offered with the per-library default');
        choose(esel(e2, 'All libraries', 'audio'), 'lang:fr');
        modeOf(editor(e2)).each.click();
        await settle(5);
        eq([optionTexts(esel(e2, 'Movies', 'audio'))[0], optionTexts(esel(e2, 'Shows', 'audio'))[0]], ['Default (Japanese)', 'Default (German)'], 'library rows: their own Defaults rows (not the unsaved French)');
        const e3 = await boot({ userDefaults: MINE_ROW });
        prow(e3, 'Ben').querySelector('button').click();
        await waitFor(() => e3.page.querySelector('.bf-editor-row .bf-person-name'), 'editor 3');
        await settle(10);
        eq(optionTexts(esel(e3, 'All libraries', 'audio'))[0], 'Default (German)', 'every library agrees: Default (German)');
    }

    // 10. Advanced: three collapsible groups, closed by default and remembered; the removed sections are gone.
    section('10 Advanced');
    const adv = await openFold(env, 'advanced');
    ok(env.storage.get('babelfin.admin.open.advanced') === '1', 'Advanced remembers it is open');
    {
        const body = page.querySelector('#bfAdvanced');
        eq(body.children.map((c) => c.id || c.className), ['bf-group-apps', 'bf-group-newUsers', 'bf-group-recognition', 'bf-restore'], 'groups: Apps, New users, Track recognition, then Restore defaults');
        eq(body.querySelector('.bf-restore button').textContent, 'Restore defaults', 'the Restore defaults button');
        const titles = { apps: 'Apps', newUsers: 'New users', recognition: 'Track recognition' };
        for (const id of Object.keys(titles)) {
            const g = agroup(env, id);
            eq([g.button.parentNode.localName, g.button.textContent, g.button.getAttribute('aria-expanded'), g.body.hidden, g.button.getAttribute('aria-controls') === g.body.id], ['h4', titles[id], 'false', true, true], id + ': a heading button, closed by default');
            g.button.click();
            eq([agroup(env, id).open, env.storage.get('babelfin.admin.open.group.' + id)], [true, '1'], id + ': opens and remembers');
            g.button.click();
            eq([agroup(env, id).open, env.storage.get('babelfin.admin.open.group.' + id)], [false, '0'], id + ': closes and remembers');
        }
        const apps = (await openGroup(env, 'apps')).body.textContent;
        ['Jellyfin Web', 'Apps not listed', 'Fix tracks after playback starts', 'Also save choices as Jellyfin'].forEach((s) => ok(apps.indexOf(s) >= 0, 'Apps has "' + s + '"'));
        const nu = (await openGroup(env, 'newUsers')).body;
        ok(nu.textContent.indexOf('New users start with') >= 0 && nu.textContent.indexOf('My languages') >= 0, 'New users: "New users start with" and My languages');
        ok(!nu.querySelector('.bfui-table') && !nu.querySelector('[role="radiogroup"]'), 'New users has no table');
        const rec = (await openGroup(env, 'recognition')).body.textContent;
        ['Track names', 'Signs and songs', 'Commentary', 'Language codes', 'Audio tracks with no language'].forEach((s) => ok(rec.indexOf(s) >= 0, 'Track recognition has "' + s + '"'));
        const advText = body.textContent;
        ['Rules for everyone', 'Show and movie defaults', 'Burned-in subtitles reported by people', 'Original-language libraries', 'Timing', 'Test mode', 'If there are no full subtitles',
            'Days to keep the activity log', 'Add a rule', 'Hard Subs Show', 'Sable Voyage', 'Kids'].forEach((s) => ok(advText.indexOf(s) < 0, 'Advanced has no "' + s + '"'));
        ok(!body.querySelector('.bfui-rule') && !body.querySelector('tr[data-library]') && body.querySelectorAll('input[type="number"]').length === 0, 'no rule editor, per-library original-language choice, or number field');
        // Remembered across a new page show.
        const e2 = await boot({ storage: { 'babelfin.admin.open.advanced': '1', 'babelfin.admin.open.group.recognition': '1' } });
        await settle(40);
        eq(['apps', 'newUsers', 'recognition'].map((id) => agroup(e2, id).open), [false, false, true], 'a new page opens the remembered group only');
        // Broken storage: everything still works.
        const e3 = await boot({});
        e3.ctx.localStorage.getItem = () => { throw new Error('blocked'); };
        e3.ctx.localStorage.setItem = () => { throw new Error('blocked'); };
        await openFold(e3, 'advanced');
        await openGroup(e3, 'apps');
        ok(agroup(e3, 'apps').open && agroup(e3, 'newUsers') && !agroup(e3, 'newUsers').open, 'blocked storage: groups open and close');
        void adv;
    }

    // 11. Advanced saves only what changed; the removed sections' stored values are untouched.
    section('11 Advanced saves');
    {
        await settle(20);
        const before = clone(server.state.config);
        const appsBody = agroup(env, 'apps').body;
        const appSel = appsBody.querySelector('table select');
        choose(appSel, 'Trust');
        const compat = appsBody.querySelectorAll('input[type="checkbox"]').find((i) => i.parentNode.textContent.indexOf('Also save choices') >= 0);
        compat.click();
        const correct = appsBody.querySelectorAll('input[type="checkbox"]').find((i) => i.parentNode.textContent.indexOf('Fix tracks after playback starts') >= 0);
        ok(correct.checked, 'Fix tracks after playback starts reflects "On"');
        const rec = agroup(env, 'recognition').body;
        const unl = rec.querySelectorAll('.bf-row').find((r) => r.textContent.indexOf('Audio tracks with no language') >= 0).querySelector('select');
        eq([optionTexts(unl), unl.value], [['Leave it unknown', "The title's original language", "The title's original language, when it's the only audio track", 'A specific language…'], 'OriginalWhenOnlyTrack'], 'unlabelled: plain options in the words of the settings contract, the shipped (built-in) rule selected');
        choose(unl, 'Unknown');
        const area = rec.querySelectorAll('.bf-row').find((r) => r.textContent.indexOf('Commentary') >= 0).querySelector('textarea');
        area.value = 'commentary\ndirector';
        area.dispatchEvent(new Event('input', { bubbles: true }));
        const body = await save(env);
        eq(diff(before, body).sort(), ['AppModes[0].Mode', 'AppModes[0].SeenOnly', 'CompatibilityMode', 'Everyday.UnlabelledAudio', 'Keywords.CommentaryWords[1]'], 'Advanced save changes only the touched fields');
        ok(body.CorrectAfterStart === 'On', 'Correct-after-start "On" left as it was');
        eq(untouched(before, body), [], 'the removed sections\' values are untouched');
        await settle(20);
        ok(agroup(env, 'apps').open && agroup(env, 'recognition').open, 'the open groups stay open after the save re-renders Advanced');
        // Language codes.
        const codes = agroup(env, 'recognition').body;
        codes.querySelectorAll('button').find((b) => b.textContent === 'Add a code').click();
        const inputs = codes.querySelectorAll('.bf-codes input');
        inputs[inputs.length - 2].value = 'jpn';
        inputs[inputs.length - 2].dispatchEvent(new Event('input', { bubbles: true }));
        inputs[inputs.length - 1].value = 'ja';
        inputs[inputs.length - 1].dispatchEvent(new Event('input', { bubbles: true }));
        const b2 = clone(server.state.config);
        const body2 = await save(env);
        eq(diff(b2, body2), ['LanguageAliases[1]'], 'a new language code saves alone');
        // An error inside a closed group opens the group.
        await settle(20);
        agroup(env, 'recognition').button.click();
        ok(!agroup(env, 'recognition').open, 'Track recognition closed');
        server.state._reject = [{ Path: 'keywords.sdhWords', Message: 'too many words' }];
        const sdh = agroup(env, 'recognition').body.querySelectorAll('.bf-row').find((r) => r.textContent.indexOf('SDH') >= 0).querySelector('textarea');
        sdh.value = 'sdh\nhoh';
        sdh.dispatchEvent(new Event('input', { bubbles: true }));
        page.querySelector('#bfSave').click();
        await settle(40);
        ok(agroup(env, 'recognition').open, 'an error inside Track recognition opens it');
        ok(agroup(env, 'recognition').body.textContent.indexOf('too many words') >= 0, 'the error shows next to the field');
        page.querySelector('#bfDiscard').click();
        await settle(30);
    }

    // 12. New users start with: only My languages, no table; the template's languages are the only thing written.
    section('12 New users');
    {
        await openGroup(env, 'newUsers');
        const nu = () => agroup(env, 'newUsers').body;
        ok(!nu().querySelector('[role="radiogroup"]') && !nu().querySelector('.bfui-table') && !nu().querySelector('select'), 'no table, no mode control, no dropdown');
        eq(nu().querySelector('.bf-row-label').textContent, 'My languages', 'the list is "My languages"');
        ok(nu().textContent.indexOf('From their Jellyfin settings') >= 0, 'an empty list says the languages come from each person\'s Jellyfin settings');
        // Add Japanese: only NewUserTemplate changes, with the languages.
        let before = clone(server.state.config);
        await addLanguage(nu(), 'japa', 'ja');
        ok(!saveBarHidden(env), 'adding a language is a change');
        let body = await save(env);
        eq(diff(before, body), ['NewUserTemplate'], 'only the template changes');
        eq([body.NewUserTemplate.Everyday.Understood, body.NewUserTemplate.Libraries, nz(body.NewUserTemplate.PerLibrary)], [['ja'], [], null], 'the template holds the languages only');
        eq(untouched(before, body), [], 'the removed sections\' values are untouched');
        await settle(20);
        ok(agroup(env, 'newUsers').body.textContent.indexOf('Japanese') >= 0, 'after saving: Japanese listed');
        // Remove it again: the template is blank, so none is stored.
        before = clone(server.state.config);
        agroup(env, 'newUsers').body.querySelector('.bfui-remove').click();
        await settle(5);
        body = await save(env);
        eq([diff(before, body), nz(body.NewUserTemplate)], [['NewUserTemplate'], null], 'an empty template is stored as none');
        // A template from before the version 6 clean-up: its other fields are kept, only the languages change.
        const old = { SchemaVersion: 1, Enabled: true, PerLibrary: true, Everyday: { Understood: ['de'], AudioChain: [{ Kind: 'Language', Language: 'de' }, { Kind: 'VideoDefault' }] }, Libraries: [{ LibraryId: LIB.movies, Name: 'Movies', Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' } }], SmartRules: [], Overrides: [], CollectionOrder: [], BurnedIn: [], Locks: {} };
        const e2 = await boot({ config: () => { const c = initialConfig(); c.NewUserTemplate = clone(old); return c; }, storage: { 'babelfin.admin.open.advanced': '1', 'babelfin.admin.open.group.newUsers': '1' } });
        await settle(40);
        const n2 = agroup(e2, 'newUsers').body;
        ok(n2.textContent.indexOf('German') >= 0 && !n2.querySelector('.bfui-table'), 'an older template: its languages, no table');
        ok(saveBarHidden(e2), 'nothing to save on load');
        const b2 = clone(e2.server.state.config);
        await addLanguage(n2, 'fren', 'fr');
        const body2 = await save(e2);
        eq(diff(b2, body2), ['NewUserTemplate.Everyday.Understood[1]'], 'only the languages change');
        eq([body2.NewUserTemplate.Everyday.AudioChain, body2.NewUserTemplate.Libraries, body2.NewUserTemplate.PerLibrary], [old.Everyday.AudioChain, old.Libraries, true], 'the rest of the old template is left as it was');
    }

    // 13. Troubleshooting: Check a title, Activity, Back up and restore, each collapsible; no Coverage, no library-wide run.
    section('13 Troubleshooting');
    {
        const tools = await openFold(env, 'tools');
        ok(env.storage.get('babelfin.admin.open.tools') === '1', 'Troubleshooting remembers it is open');
        const body = page.querySelector('#bfTools');
        eq(body.children.map((c) => c.id), ['bf-group-check', 'bf-group-activity'], 'groups: Check a title, Activity');
        eq(['check', 'activity'].map((id) => [agroup(env, id).button.textContent, agroup(env, id).open]), [['Check a title', false], ['Activity', false]], 'titles, all closed');
        ok(!body.querySelector('input[type="file"]') && body.textContent.indexOf('Download all settings') < 0, 'Back up and restore is not in Troubleshooting');
        const text = body.textContent;
        ['Coverage', 'Preview', 'Download CSV', 'Count', 'Show only', 'Or only a show', 'Choose a library'].forEach((s) => ok(text.indexOf(s) < 0, 'no "' + s + '"'));
        ok(!server.state.requests.some((r) => /Admin\/Activity/.test(r)), 'Activity is not loaded while its group is closed');
        void tools;

        // Activity: the newest 50, a person filter, Show more.
        const act = await openGroup(env, 'activity');
        await waitFor(() => act.body.querySelector('tbody tr'), 'activity rows');
        await settle(10);
        eq(server.state.activityQueries.slice(-1)[0], { limit: 50, userId: null, kind: null }, 'opening Activity asks for the newest 50');
        eq(act.body.querySelectorAll('tbody tr').length, 50, '50 entries shown');
        eq(act.body.querySelectorAll('thead th').map((th) => th.textContent), ['When', 'Kind', 'Person', 'What happened'], 'Activity columns');
        const more = act.body.querySelectorAll('button').find((b) => b.textContent === 'Show more');
        ok(!!more && !isHidden(more), 'Show more is offered');
        more.click();
        await settle(20);
        eq([server.state.activityQueries.slice(-1)[0].limit, act.body.querySelectorAll('tbody tr').length], [100, 100], 'Show more asks for 100 and shows them');
        more.click();
        await settle(20);
        eq([server.state.activityQueries.slice(-1)[0].limit, act.body.querySelectorAll('tbody tr').length], [150, 130], 'all 130 shown');
        ok(isHidden(more), 'Show more goes when everything is shown');
        const filter = act.body.querySelector('select');
        eq(filter.options.map((o) => o.textContent), ['Everyone', 'Anna', 'Ben', 'Cleo'], 'person filter: Everyone, then each person');
        choose(filter, USER.anna);
        await settle(20);
        eq(server.state.activityQueries.slice(-1)[0], { limit: 50, userId: USER.anna, kind: null }, 'the filter asks for that person, from the newest 50 again');
        ok(act.body.querySelectorAll('tbody tr').every((tr) => tr.textContent.indexOf('Anna') >= 0), 'only Anna\'s entries');
        ok(!act.body.querySelectorAll('select').some((s) => s.options.some((o) => o.textContent === 'Everything')), 'no kind filter');

        // Check a title.
        const chk = await openGroup(env, 'check');
        await waitFor(() => chk.body.querySelector('select'), 'check person select');
        ok(!chk.body.querySelector('input[type="search"]') && chk.body.textContent.indexOf(en['check.choosePerson']) >= 0, 'no search until a person is chosen');
        const person = chk.body.querySelector('select');
        eq(person.options.map((o) => o.textContent), ['Choose a person', 'Anna', 'Ben', 'Cleo'], 'the person list');
        choose(person, USER.anna);
        await settle(5);
        const search = chk.body.querySelector('input[type="search"]');
        ok(!!search, 'the search shows once a person is chosen');
        eq(chk.body.querySelector('label[for="' + search.id + '"]').textContent, 'Show, season, episode or movie', 'search label');
        search.value = 'fri';
        search.dispatchEvent(new Event('input', { bubbles: true }));
        await waitFor(() => chk.body.querySelector('.bfui-result'), 'search results', 2000);
        const q = server.state.itemQueries.slice(-1)[0];
        eq([q.userId, q.includeItemTypes, q.searchTerm], [USER.anna, 'Series,Season,Episode,Movie', 'fri'], 'BabelfinUI.itemSearch searches as the chosen person, for shows, seasons, episodes and movies');
        chk.body.querySelectorAll('.bfui-result').find((b) => b.textContent.indexOf('Sable Voyage') >= 0).click();
        await settle(5);
        eq(server.state.simPosts.slice(-1)[0], { UserId: USER.anna, ItemId: 'cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd' }, 'POST Admin/Simulate { UserId, ItemId }');
        ok(chk.body.textContent.indexOf('Checking Sable Voyage…') >= 0, 'a status line while it runs');
        await waitFor(() => chk.body.querySelector('.bf-check-table'), 'check results', 5000);
        await settle(5);
        const gets = server.state.requests.filter((r) => /^GET Babelfin\/Admin\/Simulate\/job/.test(r));
        ok(gets.length >= 2, 'GET Admin/Simulate/{jobId} polled until done (' + gets.length + ' polls)');
        const tbl = chk.body.querySelector('.bf-check-table');
        eq(tbl.querySelectorAll('thead th').map((th) => th.textContent), ['Title', 'Audio', 'Subtitles', 'Why'], 'columns: Title, Audio, Subtitles, Why');
        eq(tbl.querySelectorAll('tbody tr').length, 50, 'at most 50 rows');
        ok(chk.body.textContent.indexOf('Showing 50 of 120') >= 0, '"Showing 50 of 120"');
        const rows = TITLE_ROWS(120);
        const first = tbl.querySelectorAll('tbody tr');
        eq(first.slice(0, 4).map((tr) => tr.querySelectorAll('td').map((td) => td.querySelector('summary') ? td.querySelector('summary').textContent : td.textContent)),
            [['Sable Voyage S01E01 · Episode 1', 'Japanese', 'English · signs and songs', 'Video default'],
                ['Sable Voyage S01E02 · Episode 2', 'Japanese', 'English · full', 'Chosen by Babelfin'],
                ['Sable Voyage S01E03 · Episode 3', 'Japanese', 'English · signs and songs', 'Video default'],
                ['Sable Voyage S01E04 · Episode 4', 'Japanese', 'None found', 'Video default']], 'rows: title, audio, subtitles, and Chosen by Babelfin / Video default');
        eq(first[7].querySelectorAll('td')[2].textContent, 'Off', 'no subtitles and none wanted: Off');
        eq(first.map((tr) => tr.getAttribute('data-outcome')), rows.slice(0, 50).map((r) => (r.Outcome === 'Applied' ? 'chosen' : 'video')), 'Applied → Chosen by Babelfin; PassedThrough (and no decision) → Video default');
        const why = first[1].querySelector('details.bf-why');
        ok(!!why && !why.open && why.querySelector('div').textContent === rows[1].Explanation, 'the explanation sits in a closed "why" disclosure');
        eq(first[4].querySelectorAll('td')[1].textContent, '—', 'no audio track: a dash');
        ok(!chk.body.querySelector('input[type="checkbox"]') && !chk.body.querySelector('.bf-counts') && chk.body.textContent.indexOf('Files') < 0, 'no counters and no filters');
        ok(!chk.body.querySelectorAll('button').some((b) => /CSV|Show more|Stop/.test(b.textContent)), 'no CSV, no Show more, no Stop');
        ok(!chk.body.querySelectorAll('select').some((s) => s.options.some((o) => /library/i.test(o.textContent))), 'no library-wide run');
        // A short title: no "Showing" line.
        const e2 = await boot({ simRows: () => TITLE_ROWS(3), storage: { 'babelfin.admin.open.tools': '1', 'babelfin.admin.open.group.check': '1' } });
        await settle(40);
        const c2 = agroup(e2, 'check').body;
        await waitFor(() => c2.querySelector('select'), 'person select 2');
        choose(c2.querySelector('select'), USER.ben);
        const s2 = c2.querySelector('input[type="search"]');
        s2.value = 'spirited';
        s2.dispatchEvent(new Event('input', { bubbles: true }));
        await waitFor(() => c2.querySelector('.bfui-result'), 'results 2', 2000);
        c2.querySelector('.bfui-result').click();
        await waitFor(() => c2.querySelector('.bf-check-table'), 'check results 2', 5000);
        eq([c2.querySelectorAll('.bf-check-table tbody tr').length, c2.textContent.indexOf('Showing') < 0], [3, true], '3 rows, no "Showing" line');
        eq(e2.server.state.simPosts.slice(-1)[0], { UserId: USER.ben, ItemId: 'abababababababababababababababab' }, 'as Ben, for the movie');
        // Busy and expired.
        e2.server.state.simBusy = true;
        c2.querySelector('input[type="search"]').value = 'spirited';
        c2.querySelector('input[type="search"]').dispatchEvent(new Event('input', { bubbles: true }));
        await waitFor(() => c2.querySelector('.bfui-result'), 'results 3', 2000);
        c2.querySelector('.bfui-result').click();
        await settle(20);
        ok(c2.textContent.indexOf(en['check.busy']) >= 0, '409: "Another check is running."');
        e2.server.state.simBusy = false;
        e2.server.state.simExpired = true;
        c2.querySelector('input[type="search"]').value = 'spirited';
        c2.querySelector('input[type="search"]').dispatchEvent(new Event('input', { bubbles: true }));
        await waitFor(() => c2.querySelector('.bfui-result'), 'results 4', 2000);
        c2.querySelector('.bfui-result').click();
        await settle(20);
        ok(c2.textContent.indexOf(en['check.expired']) >= 0, '404: "These results are gone."');
        // Changing the person clears the results and the search.
        choose(c2.querySelector('select'), '');
        ok(!c2.querySelector('.bf-check-table') && !c2.querySelector('input[type="search"]'), 'no person: no search, no results');
        // Leaving the page stops polling.
        const e3 = await boot({ storage: { 'babelfin.admin.open.tools': '1', 'babelfin.admin.open.group.check': '1' } });
        await settle(40);
        const c3 = agroup(e3, 'check').body;
        await waitFor(() => c3.querySelector('select'), 'person select 3');
        choose(c3.querySelector('select'), USER.anna);
        c3.querySelector('input[type="search"]').value = 'fri';
        c3.querySelector('input[type="search"]').dispatchEvent(new Event('input', { bubbles: true }));
        await waitFor(() => c3.querySelector('.bfui-result'), 'results 5', 2000);
        c3.querySelectorAll('.bfui-result')[1].click();
        await settle(5);
        e3.page.dispatchEvent(new Event('pagehide'));
        await settle(20);
        const polls = e3.server.state.requests.filter((r) => /Simulate\/job/.test(r)).length;
        await sleep(1300);
        eq(e3.server.state.requests.filter((r) => /Simulate\/job/.test(r)).length, polls, 'pagehide stops the polling');

        // Back up and restore: its own collapsible section, last; the download and the restore with its preview.
        const bkFold = page.querySelector('details[data-fold="backup"]');
        ok(!bkFold.open && !page.querySelector('#bfBackup').firstChild, 'Back up and restore starts closed and empty');
        await openFold(env, 'backup');
        ok(env.storage.get('babelfin.admin.open.backup') === '1', 'Back up and restore remembers it is open');
        const bk = page.querySelector('#bfBackup');
        ok(bk.textContent.indexOf('Download all settings') >= 0 && !!bk.querySelector('input[type="file"]') && bk.textContent.indexOf('Restore from a file') >= 0, 'Back up and restore: download and restore');
        ok(!bk.querySelector('.bfui-disclosure'), 'no group inside it: the section itself folds');
        // Remembered.
        const e4 = await boot({ storage: { 'babelfin.admin.open.tools': '1', 'babelfin.admin.open.group.activity': '1', 'babelfin.admin.open.backup': '1' } });
        await settle(40);
        eq(['check', 'activity'].map((id) => agroup(e4, id).open), [false, true], 'Troubleshooting groups remember their state');
        ok(e4.page.querySelector('details[data-fold="backup"]').open && e4.page.querySelector('#bfBackup').textContent.indexOf('Download all settings') >= 0, 'Back up and restore reopens as remembered');
        const e5 = await boot({ storage: { 'babelfin.admin.open.tools': '1' } });
        await settle(40);
        ok(!e5.server.state.requests.some((r) => /Admin\/Activity/.test(r)), 'a closed Activity group loads nothing');
    }

    // 14. Visible strings: no banned words (data values left out); no key shows; then every admin language.
    section('14 wording and languages');
    {
        for (const id of ['apps', 'newUsers', 'recognition', 'check', 'activity']) {
            await openGroup(env, id);
        }
        await openFold(env, 'backup');
        await settle(20);
        const data = ['Jellyfin Media Player', 'Jellyfin Web', 'Android TV', 'Anna', 'Ben', 'Cleo', 'Anime', 'Movies', 'Shows', 'Deutsche Filme', 'Mixed', 'Sable Voyage', 'Kids',
            'signs', 'forced', 'sdh', 'full', 'dialogue', 'commentary', 'director', 'audio description', 'jap', 'jpn', 'Copper Kite', 'cc', 'hoh'];
        const strip = (s) => data.reduce((acc, d) => acc.split(d).join(' '), s);
        const all = visibleStrings(dash, (n) => n.classList && n.classList.contains('bf-activity'));
        const bad = [];
        all.forEach((x) => {
            const low = strip(x.text).toLowerCase();
            BANNED.forEach((b) => { if (low.indexOf(b) >= 0) { bad.push(b + ' in "' + x.text + '"'); } });
        });
        eq(bad, [], 'no visible string (this page\'s or ui.js\'s) uses a banned word');
        const rawKey = /(^|\W)(field|enum|common|people|libraries|audio|apps|option|check|activity|backup|template|recognition|tracknames|codes|ui|unlabelled|kind)\.[A-Za-z]/;
        eq(all.filter((x) => rawKey.test(x.text)).map((x) => x.text), [], 'no untranslated key shows');
        ok(all.every((x) => x.text.indexOf('!') < 0), 'no exclamation marks');
        ok(env.objectCreateCalls.length === 0, 'document.createElement is never called with the { is } options form', env.objectCreateCalls);
        eq(env.consoleErrors, [], 'no console errors or warnings');
        // Every admin language: the page renders with every group open and no raw key.
        const langs = fs.readdirSync(ASSETS + 'i18n-admin').filter((f) => f.endsWith('.json')).map((f) => f.slice(0, -5));
        eq(langs.length, 16, '16 admin translation files');
        const groupStore = {};
        ['advanced', 'tools', 'backup'].forEach((k) => { groupStore['babelfin.admin.open.' + k] = '1'; });
        ['apps', 'newUsers', 'recognition', 'check', 'activity'].forEach((k) => { groupStore['babelfin.admin.open.group.' + k] = '1'; });
        for (const lang of langs) {
            const table = JSON.parse(fs.readFileSync(ASSETS + 'i18n-admin/' + lang + '.json', 'utf8'));
            const ui = JSON.parse(fs.readFileSync(ASSETS + 'i18n/' + lang + '.json', 'utf8'));
            const e = await boot({ lang, storage: groupStore });
            await settle(60);
            await waitFor(() => e.page.querySelector('.bf-activity tbody tr'), lang + ' activity', 3000);
            e.page.querySelector('#bfPeople tbody tr button').click();
            await waitFor(() => e.page.querySelector('.bf-editor-row .bfui-table'), lang + ' editor');
            await settle(10);
            const strings = visibleStrings(e.page.querySelector('.bf-dash'), null).map((x) => x.text);
            eq(strings.filter((s) => rawKey.test(s)), [], lang + ': no untranslated key shows');
            eq(e.page.querySelector('details[data-fold="tools"] summary h3').textContent, table['tools.title'], lang + ': the Troubleshooting title');
            eq(['apps', 'newUsers', 'recognition', 'check', 'activity'].map((id) => agroup(e, id).button.textContent),
                [table['apps.title'], table['template.group'], table['recognition.title'], table['check.title'], table['activity.title']], lang + ': group titles');
            eq([e.page.querySelector('details[data-fold="backup"] summary h3').textContent, plainText(e.page.querySelector('section[data-section="libraries"] h3')), e.page.querySelector('section[data-section="libraries"] .bf-section-lede').textContent],
                [table['backup.title'], table['defaults.title'], table['defaults.help']], lang + ': Back up and restore, Defaults for everyone and its line');
            eq(e.page.querySelector('#bfRestoreDefaults').textContent, table['advanced.restore'], lang + ': Restore defaults');
            eq(e.page.querySelector('#bfPeople thead').querySelectorAll('th').map((th) => th.textContent), [table['people.name'], table['libraries.audio'], table['libraries.subtitles'], table['common.edit']], lang + ': People columns');
            eq(menuProblems(e, ui['ui.defaultValue'].split('{value}')[0]), [], lang + ': no "Default (" option and no repeated label outside the table');
            eq(e.page.querySelector('.bf-editor-row .bf-row-label').textContent, ui['languages.title'], lang + ': My languages in the editor');
            // the editor's overrides section speaks the language too (its words are the person page's files').
            const og = e.page.querySelector('.bf-editor-row #bf-group-overrides');
            og.querySelector('.bfui-disclosure-button').click();
            await settle(10);
            eq(og.querySelector('.bfui-disclosure-title').textContent, ui['overrides.title'] + ' (2)', lang + ': the overrides title');
            eq(og.querySelector('.bf-hint').textContent, ui['overrides.helpAdmin'], lang + ': the admin help line');
            eq(og.querySelectorAll('thead th').map((th) => th.textContent), [ui['overrides.col.item'], ui['overrides.col.library'], ui['overrides.col.audio'], ui['overrides.col.subtitles'], ui['common.remove']], lang + ': the overrides columns');
            eq(og.querySelector('.bfui-overrides-remove-all').textContent, ui['overrides.removeAll'], lang + ': Remove all show and movie overrides');
            eq(og.querySelector('.bfui-overrides-note').textContent, ui['overrides.remembered'], lang + ': the remembered track change note');
            eq(og.querySelectorAll('.bfui-overrides-remove').map((b) => b.textContent), [ui['common.remove'], ui['common.remove']], lang + ': Remove');
            eq(visibleStrings(og, null).map((x) => x.text).filter((s) => /(^|\W)(overrides|titles|ui|common)\.[A-Za-z]/.test(s)), [], lang + ': no key shows in it');
            eq(modeOf(e.page.querySelector('#bfLibraries')).radios.map((r) => r.textContent), [ui['ui.table.mode.same'], ui['ui.table.mode.each']], lang + ': the mode labels');
            eq(e.consoleErrors, [], lang + ': no console errors');
        }
    }

    // 15. A second page show: Babelfin didn't start, Advanced re-opens from storage, a Defaults field in the person answer is passed through.
    section('15 second show');
    {
        let seenDefaults = null;
        const env2 = await boot({
            wrapped: false,
            defaultsField: true,
            userDefaults: { AudioChain: [{ Kind: 'MyLanguages' }], WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: [], Understood: ['en'], Reads: ['en'], AudioFromLibrary: false, SubtitlesFromLibrary: false },
            storage: { 'babelfin.admin.open.advanced': '1' },
            afterScript: (name, ctx) => {
                if (name === 'ui.js' && ctx.BabelfinUI && ctx.BabelfinUI.libraryTable) {
                    const real = ctx.BabelfinUI;
                    ctx.BabelfinUI = Object.assign({}, real, { libraryTable: (c, o) => { if (o.audience !== 'admin') { seenDefaults = o.defaults; } return real.libraryTable(c, o); } });
                }
            }
        });
        ok(!isHidden(env2.page.querySelector('#bfNotStarted')) && env2.page.querySelector('#bfNotStarted').textContent === "Babelfin couldn't start. Check the server log.", 'the start error line shows when wrapped is false');
        ok(env2.page.querySelector('details[data-fold="advanced"]').open, 'Advanced opens as remembered');
        prow(env2, 'Ben').querySelector('button').click();
        await waitFor(() => env2.page.querySelector('.bf-editor-row .bf-person-name'), 'editor 2');
        await settle(10);
        ok(seenDefaults && seenDefaults.AllLibraries && seenDefaults.AllLibraries.AudioChain[0].Kind === 'MyLanguages', 'the defaults come from Admin/Users/{id}/Defaults', seenDefaults);
        const benAll = env2.page.querySelector('.bf-editor-row tr.bfui-table-row .bfui-table-audio select');
        eq([optionTexts(benAll)[0], hasOption(benAll, 'video')], ['Default (German)', true], 'an opinionated default reads "Default (…)" and offers Video default');
        ok(env2.consoleErrors.length === 0, 'no console errors on the second show', env2.consoleErrors);
    }

    // 16. An older ui.js: without libraryTable the sections say the files are out of date; without disclosure the groups are plain sections.
    section('16 old ui.js');
    {
        const env3 = await boot({
            ready: (p) => p.querySelector('#bfPeople table') && p.querySelector('#bfLibraries').textContent.indexOf(en['people.needsUpdate']) >= 0,
            afterScript: (name, ctx) => { if (name === 'ui.js') { const copy = Object.assign({}, ctx.BabelfinUI); delete copy.libraryTable; delete copy.disclosure; delete copy.itemSearch; ctx.BabelfinUI = copy; } },
            storage: { 'babelfin.admin.open.advanced': '1', 'babelfin.admin.open.tools': '1' }
        });
        await settle(40);
        ok(env3.page.querySelector('#bfLibraries').textContent.indexOf(en['people.needsUpdate']) >= 0, 'missing libraryTable: the Libraries section shows the reload line');
        eq(env3.page.querySelectorAll('#bfAdvanced .bf-group-title').map((x) => x.textContent), ['Apps', 'New users', 'Track recognition'], 'missing disclosure: plain titled groups');
        eq(env3.page.querySelectorAll('#bfTools .bf-group-title').map((x) => x.textContent), ['Check a title', 'Activity'], 'Troubleshooting too');
        ok(!!env3.page.querySelector('#bfAdvanced .bf-restore button'), 'Restore defaults is there without them');
        eq([peopleCells(env3, 'Anna'), peopleCells(env3, 'Ben')], [['Set per library', 'Set per library'], ['Default', 'Default']], 'the People words work without them');
        const c = env3.page.querySelector('#bfTools .bf-group-body select');
        choose(c, USER.anna);
        ok(env3.page.querySelector('#bfTools').textContent.indexOf(en['people.needsUpdate']) >= 0, 'missing itemSearch: Check a title says the files are out of date');
        env3.page.querySelector('#bfEnabled').click();
        const body = await save(env3);
        eq(body.Enabled, false, 'the rest of the page still saves');
    }

    // 18. People columns: every table value in words, Set per library, no file, nothing set; missing members count as null.
    section('18 People columns');
    {
        const SMART_THEIR = "When the audio isn't in their languages";
        const VIDEO = [{ Kind: 'VideoDefault' }];
        const lang = (c) => [{ Kind: 'Language', Language: c }, { Kind: 'VideoDefault' }];
        const people = [
            ['Dana', { Table: { AudioChain: VIDEO, WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' } }, ['Video default', 'Off']],
            ['Eli', { Table: { AudioChain: lang('ja'), WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' } }, ['Japanese', SMART_THEIR]],
            ['Fay', { Table: { WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'PartialOnly' } }, ['Default', 'Signs and songs only']],
            ['Gus', { Table: { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'Language', Language: 'en' }], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['en'] } }, ['Custom', 'English']],
            ['Hal', { Table: { AudioChain: lang('pt-BR'), WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' } }, ['Brazilian Portuguese', 'Video default']],
            ['Ida', { Table: { ForeignTitles: 'OriginalWithSubtitles', WhenAudioUnderstood: 'Off' } }, ['Custom', 'Custom']],
            ['Jo', { Table: { AudioChain: [{ Kind: 'MyLanguages' }], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full' } }, ['Custom', 'Custom']],
            ['Kai', { Table: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['fil'] } }, ['Default', 'Filipino']],
            ['Liv', { PerLibrary: true, LibraryRows: 2, Table: { AudioChain: lang('ja') } }, ['Set per library', 'Set per library']],
            ['Mia', { PerLibrary: true, LibraryRows: 0, Table: null }, ['Set per library', 'Set per library']],
            ['Ned', { HasSettings: false, Table: null }, ['Default', 'Default']],
            ['Oda', { Table: null }, ['Default', 'Default']],
            ['Pam', { Table: { AudioChain: null, ForeignTitles: null, WhenAudioUnderstood: null, WhenAudioNotUnderstood: null, SubtitleLanguages: null } }, ['Default', 'Default']]
        ];
        const ids = people.map((x, i) => String(i + 10).padStart(32, '7'));
        const rows = () => people.map(([name, extra], i) => Object.assign({ UserId: ids[i], Name: name, HasSettings: true, Enabled: true, UseEverydayEverywhere: false, Assumed: [], SeedConfirmed: true, UpdatedAt: null, PerLibrary: false, Table: null, LibraryRows: 0 }, clone(extra)));
        for (const [label, opts] of [['nulls in the JSON', { keepNulls: true }], ['nulls left out', {}], ['every null left out, and no PerLibrary or LibraryRows', { users: () => rows().map((r) => { const c = dropNulls(r); if (c.PerLibrary === false) { delete c.PerLibrary; } delete c.LibraryRows; return c; }) }]]) {
            const e = await boot(Object.assign({ users: rows }, opts));
            people.forEach(([name, , want]) => eq(peopleCells(e, name), want, label + ': ' + name));
            const tr = e.page.querySelector('#bfPeople tbody tr');
            eq(tr.querySelectorAll('td').slice(0, 2).map((td) => td.getAttribute('data-label')), ['Audio', 'Subtitles'], label + ': the cells are labelled for phones');
            ok(!e.server.state.requests.some((r) => /Users\/[0-9a-f]+\/Settings/.test(r)), label + ': no person file read for the table');
            eq(e.consoleErrors, [], label + ': no console errors');
        }
        // The (detected) marker shows in the editor only.
        ok(prow(env, 'Anna').textContent.indexOf('(detected)') < 0, 'Anna\'s row has no (detected) marker');
        // Words in another language come from the files.
        const de = await boot({ users: rows, lang: 'de' });
        const deUi = JSON.parse(fs.readFileSync(ASSETS + 'i18n/de.json', 'utf8'));
        const deAdmin = JSON.parse(fs.readFileSync(ASSETS + 'i18n-admin/de.json', 'utf8'));
        eq(peopleCells(de, 'Dana'), [deUi['ui.table.videoDefault'], deUi['ui.table.off']], 'German: Video default, Off');
        eq(peopleCells(de, 'Eli')[1], deAdmin['people.smart'], 'German: the smart choice');
        eq(peopleCells(de, 'Liv'), [deAdmin['people.perLibrary'], deAdmin['people.perLibrary']], 'German: Set per library');
        eq(peopleCells(de, 'Ned'), [deUi['ui.default'], deUi['ui.default']], 'German: Default');
    }

    // 19. Restore defaults: the shipped Apps and Track recognition values after a confirmation, saved only with Save.
    section('19 Restore defaults');
    {
        const SEEN = '2026-10-18T10:00:00Z';
        const custom = () => {
            const c = initialConfig();
            c.AppModes = [
                { Client: 'Jellyfin Web', Mode: 'Trust', Seen: true, LastSeen: SEEN },
                { Client: 'Android TV', Mode: 'ReplaceOnAutoplay', Seen: true, LastSeen: SEEN },
                { Client: 'VLC', Mode: 'ReplaceUnlessManual', Seen: true, LastSeen: SEEN },
                { Client: 'Swiftfin', Mode: 'Trust', Seen: false, LastSeen: null }
            ];
            c.UnknownAppMode = 'ReplaceUnlessManual';
            c.CorrectAfterStart = 'Off';
            c.CompatibilityMode = true;
            c.Keywords = { PartialWords: ['signs'], SdhWords: ['sdh', 'hoh'], FullWords: ['full'], CommentaryWords: ['commentary', 'director'], AudioDescriptionWords: ['ad'] };
            c.LanguageAliases = [{ Raw: 'jap', Code: 'ja' }, { Raw: 'eng', Code: 'en' }];
            c.Everyday.UnlabelledAudio = 'SpecificLanguage';
            c.Everyday.UnlabelledAudioLanguage = 'de';
            c.Everyday.AudioChain = [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }];
            c.PerLibraryDefaults = false;
            c.NewUserTemplate = { SchemaVersion: 1, Enabled: true, Everyday: { Understood: ['fr'] }, Libraries: [], SmartRules: [], Overrides: [], CollectionOrder: [], BurnedIn: [], Locks: {} };
            return c;
        };
        const store = { 'babelfin.admin.open.advanced': '1', 'babelfin.admin.open.group.apps': '1', 'babelfin.admin.open.group.recognition': '1', 'babelfin.admin.open.group.newUsers': '1' };
        const e = await boot({ config: custom, storage: store });
        await waitFor(() => e.page.querySelector('.bf-apps-table'), 'apps table');
        await settle(20);
        const shipped = shippedConfig();
        const appSel = (client) => e.page.querySelector('.bf-apps-table').querySelectorAll('tbody tr').find((tr) => tr.querySelector('th span').textContent === client).querySelector('select');
        const rec = () => agroup(e, 'recognition').body;
        const area = (k) => rec().querySelectorAll('.bf-row').find((r) => r.querySelector('.bf-row-label').textContent === { PartialWords: 'Signs and songs', SdhWords: 'SDH', FullWords: 'Full', CommentaryWords: 'Commentary', AudioDescriptionWords: 'Audio description' }[k]).querySelector('textarea');
        const unl = () => rec().querySelectorAll('.bf-row').find((r) => r.textContent.indexOf('Audio tracks with no language') >= 0).querySelector('select');
        const boxes = () => agroup(e, 'apps').body.querySelectorAll('input[type="checkbox"]');
        const shownNow = () => ({
            apps: ['Jellyfin Web', 'Android TV', 'VLC', 'Swiftfin'].map(appSel).map((s) => s.value),
            unknown: agroup(e, 'apps').body.querySelectorAll('.bf-row').find((r) => r.textContent.indexOf('Apps not listed') >= 0).querySelector('select').value,
            checks: boxes().map((b) => b.checked),
            words: KEYWORDS.map((k) => area(k).value),
            codes: rec().querySelectorAll('.bf-codes tbody tr').length,
            unlabelled: unl().value
        });
        const KEYWORDS = ['PartialWords', 'SdhWords', 'FullWords', 'CommentaryWords', 'AudioDescriptionWords'];
        const loaded = shownNow();
        eq(loaded, { apps: ['Trust', 'ReplaceOnAutoplay', 'ReplaceUnlessManual', 'Trust'], unknown: 'ReplaceUnlessManual', checks: [true, false, false, false, false, true], words: ['signs', 'sdh\nhoh', 'full', 'commentary\ndirector', 'ad'], codes: 2, unlabelled: 'lang:de' }, 'the stored values show');
        const before = clone(e.server.state.config);
        const button = e.page.querySelector('#bfRestoreDefaults');
        ok(!!button && button.textContent === 'Restore defaults', 'the button');
        button.click();
        const text = await dialogAnswer(e, false);
        ok(/Apps and Track recognition go back to their defaults/.test(text || ''), 'it asks first, naming what goes back', text);
        await settle(10);
        eq([shownNow(), saveBarHidden(e)], [loaded, true], 'Cancel changes nothing');
        button.click();
        await dialogAnswer(e, true);
        await settle(20);
        eq(shownNow(), {
            apps: ['ReplaceOnAutoplay', 'ReplaceUnlessManual', 'Trust', 'Trust'], unknown: 'Trust', checks: [true, false, false, false, true, false],
            words: KEYWORDS.map((k) => shipped.Keywords[k].join('\n')), codes: 0, unlabelled: 'OriginalWhenOnlyTrack'
        }, 'the shipped values show: listed apps from the shipped list, VLC (seen only) on the shipped "Apps not listed" mode');
        ok(!saveBarHidden(e), 'the page is marked changed');
        eq(e.server.state.puts.length, 0, 'nothing is sent before Save');
        eq(e.server.state.shippedCalls, 1, 'GET Admin/Defaults was read once, when the page was shown');
        const body = await save(e);
        const allowed = ['AppModes[0].Mode', 'AppModes[1].Mode', 'AppModes[2].Mode', 'AppModes[0].SeenOnly', 'AppModes[1].SeenOnly', 'AppModes[2].SeenOnly', 'AppModes[0].RememberPrePlayChoices', 'AppModes[1].RememberPrePlayChoices', 'AppModes[2].RememberPrePlayChoices', 'AppModes[3].RememberPrePlayChoices', 'UnknownAppMode', 'CorrectAfterStart', 'CompatibilityMode', 'Keywords.', 'LanguageAliases', 'Everyday.UnlabelledAudio', 'Everyday.UnlabelledAudioLanguage'];
        const changed = diff(before, body);
        eq(changed.filter((p) => !allowed.some((a) => p === a || p.indexOf(a) === 0)), [], 'the save changes only the restored fields', changed);
        eq([body.AppModes.map((a) => [a.Client, a.Mode, a.Seen, a.LastSeen]), body.UnknownAppMode, body.CorrectAfterStart, body.CompatibilityMode, body.Keywords, body.LanguageAliases, nz(body.Everyday.UnlabelledAudio), nz(body.Everyday.UnlabelledAudioLanguage)],
            [[['Jellyfin Web', 'ReplaceOnAutoplay', true, SEEN], ['Android TV', 'ReplaceUnlessManual', true, SEEN], ['VLC', 'Trust', true, SEEN], ['Swiftfin', 'Trust', false, null]], 'Trust', 'Auto', false, shipped.Keywords, [], null, null],
            'the shipped values are saved (the apps keep their seen dates)');
        eq([body.Everyday.AudioChain, body.PerLibraryDefaults, body.Libraries, body.NewUserTemplate, body.Enabled], [before.Everyday.AudioChain, false, before.Libraries, before.NewUserTemplate, true], 'Defaults for everyone, New users and Enabled are untouched');
        eq(untouched(before, body), [], 'the fields the page no longer edits are untouched');
        await settle(20);
        ok(saveBarHidden(e), 'nothing left to save');
        // Again: already the shipped values, so nothing to save.
        e.page.querySelector('#bfRestoreDefaults').click();
        await dialogAnswer(e, true);
        await settle(20);
        ok(saveBarHidden(e), 'restoring the shipped values again changes nothing');
        // An edit after Restore defaults is what gets saved.
        const e2 = await boot({ config: custom, storage: store });
        await waitFor(() => e2.page.querySelector('.bf-apps-table'), 'apps table 2');
        await settle(20);
        e2.page.querySelector('#bfRestoreDefaults').click();
        await dialogAnswer(e2, true);
        await settle(20);
        const sdh = agroup(e2, 'recognition').body.querySelectorAll('.bf-row').find((r) => r.querySelector('.bf-row-label').textContent === 'SDH').querySelector('textarea');
        sdh.value = 'sdh\ncc';
        sdh.dispatchEvent(new Event('input', { bubbles: true }));
        const b2 = await save(e2);
        eq([b2.Keywords.SdhWords, b2.Keywords.CommentaryWords], [['sdh', 'cc'], shipped.Keywords.CommentaryWords], 'an edit after Restore defaults wins for its field');
        // Discard after Restore defaults: everything as stored.
        const e3 = await boot({ config: custom, storage: store });
        await waitFor(() => e3.page.querySelector('.bf-apps-table'), 'apps table 3');
        await settle(20);
        e3.page.querySelector('#bfRestoreDefaults').click();
        await dialogAnswer(e3, true);
        await settle(20);
        e3.page.querySelector('#bfDiscard').click();
        await settle(30);
        ok(saveBarHidden(e3) && e3.server.state.puts.length === 0, 'Cancel after Restore defaults saves nothing');
        eq(agroup(e3, 'recognition').body.querySelectorAll('.bf-codes tbody tr').length, 2, 'and shows the stored language codes again');
        // GET Admin/Defaults unavailable: the built-in rule shows, Restore defaults says so and changes nothing.
        const e4 = await boot({ config: custom, storage: store, noShipped: true });
        await waitFor(() => e4.page.querySelector('.bf-apps-table'), 'apps table 4');
        await settle(20);
        e4.page.querySelector('#bfRestoreDefaults').click();
        await dialogAnswer(e4, true);
        await settle(20);
        ok(e4.page.querySelector('.bf-restore .bf-alert').textContent.indexOf('Not available on this server.') >= 0, 'Admin/Defaults missing: the error shows beside the button');
        ok(saveBarHidden(e4), 'and nothing changes');
        eq(e4.consoleErrors.filter((x) => x.indexOf('shipped settings') < 0), [], 'only the expected warning');
        // The selected value comes from Admin/Defaults: a shipped rule shows when nothing is stored, and Restore defaults saves it.
        const shippedOriginal = () => { const c = shippedConfig(); c.Everyday = { UnlabelledAudio: 'OriginalLanguage' }; return c; };
        const e5 = await boot({ storage: store, shipped: shippedOriginal });
        await settle(40);
        const u5 = () => agroup(e5, 'recognition').body.querySelectorAll('.bf-row').find((r) => r.textContent.indexOf('Audio tracks with no language') >= 0).querySelector('select');
        eq(u5().value, 'OriginalLanguage', 'nothing stored: the shipped rule is selected');
        choose(u5(), 'Unknown');
        ok(!saveBarHidden(e5), 'another rule is a change');
        choose(u5(), 'OriginalWhenOnlyTrack');
        let b5 = await save(e5);
        eq(b5.Everyday.UnlabelledAudio, 'OriginalWhenOnlyTrack', 'a rule other than the shipped one is stored');
        await settle(20);
        e5.page.querySelector('#bfRestoreDefaults').click();
        await dialogAnswer(e5, true);
        await settle(20);
        eq(u5().value, 'OriginalLanguage', 'Restore defaults selects the shipped rule');
        b5 = await save(e5);
        eq([b5.Everyday.UnlabelledAudio, nz(b5.Everyday.UnlabelledAudioLanguage)], ['OriginalLanguage', null], 'and saves what the shipped settings store');
    }

    // 20. Plain menus: every section and group open, the editor open: no "Default (" option, no label twice outside the
    // table; "Audio tracks with no language" selects the shipped rule and picking it clears the stored one.
    section('20 plain menus');
    {
        const open = {};
        ['advanced', 'tools', 'backup'].forEach((k) => { open['babelfin.admin.open.' + k] = '1'; });
        ['apps', 'newUsers', 'recognition', 'check', 'activity'].forEach((k) => { open['babelfin.admin.open.group.' + k] = '1'; });
        const e = await boot({ storage: open, config: () => { const c = initialConfig(); c.Everyday.UnlabelledAudio = 'Unknown'; return c; } });
        await settle(60);
        await waitFor(() => e.page.querySelector('.bf-apps-table'), 'apps');
        e.page.querySelector('#bfPeople tbody tr button').click();
        await waitFor(() => e.page.querySelector('.bf-editor-row .bfui-table'), 'editor');
        const chk = agroup(e, 'check').body;
        await waitFor(() => chk.querySelector('select'), 'check person select');
        await settle(10);
        const outside = e.page.querySelectorAll('select').filter((s) => !s.closest('.bfui-table, section.bfui-genres'));
        ok(outside.length >= 7, 'the dropdowns outside the tables are there (' + outside.length + ')');
        eq(menuProblems(e), [], 'no "Default (" option and no label twice outside the tables');
        ok(e.page.querySelectorAll('.bfui-table select').every((s) => s.options[0].value === ''), 'the tables keep their Default entry');
        const unlRow = () => agroup(e, 'recognition').body.querySelectorAll('.bf-row').find((r) => r.textContent.indexOf('Audio tracks with no language') >= 0);
        eq(unlRow().querySelector('select').value, 'Unknown', 'a stored rule is selected');
        const before = clone(e.server.state.config);
        choose(unlRow().querySelector('select'), 'OriginalWhenOnlyTrack');
        const body = await save(e);
        eq([diff(before, body), nz(body.Everyday.UnlabelledAudio)], [['Everyday.UnlabelledAudio'], null], 'picking the shipped rule clears the stored one');
        await settle(20);
        eq(unlRow().querySelector('select').value, 'OriginalWhenOnlyTrack', 'and it stays selected');
        // A specific language: picked through the chooser, stored with its language.
        choose(unlRow().querySelector('select'), '__pick');
        await settle(5);
        await addLanguage(unlRow(), 'germ', 'de');
        eq(unlRow().querySelector('select').value, 'lang:de', 'a specific language shows in the dropdown');
        eq(menuProblems(e), [], 'still no Default option or repeated label');
        const b2 = await save(e);
        eq([b2.Everyday.UnlabelledAudio, b2.Everyday.UnlabelledAudioLanguage], ['SpecificLanguage', 'de'], 'saved as a specific language');
        // The Apps dropdowns list the modes once each.
        await settle(20);
        eq(optionTexts(e.page.querySelector('.bf-apps-table select')), ['Keep the app\'s choice', 'Use Babelfin\'s choice when playing the next episode', 'Use Babelfin\'s choice unless changed by hand'], 'an app\'s dropdown: the three modes');
    }

    // 21. Wording: "My languages" for Understood everywhere, nothing left of "Languages understood" or "Understands".
    section('21 My languages wording');
    {
        for (const dir of ['i18n-admin', 'i18n']) {
            for (const f of fs.readdirSync(ASSETS + dir).filter((x) => x.endsWith('.json'))) {
                const table = JSON.parse(fs.readFileSync(ASSETS + dir + '/' + f, 'utf8'));
                ['field.Understood', 'people.understands', 'people.reads', 'languages.understood', 'common.default', 'libraries.title'].forEach((k) => ok(!(k in table), dir + '/' + f + ': no ' + k));
            }
        }
        const enAll = Object.values(en).concat(Object.values(enUi)).filter((v) => typeof v === 'string');
        eq(enAll.filter((v) => /languages understood|languages I understand|\bunderstands\b/i.test(v)), [], 'no old wording in the English files');
        const strings = visibleStrings(page.querySelector('.bf-dash'), null).map((x) => x.text);
        eq(strings.filter((v) => /languages understood|languages I understand|\bunderstands\b/i.test(v)), [], 'no old wording on the page');
        ok(strings.indexOf('Defaults for everyone') >= 0, '"Defaults for everyone" shows');
    }

    // ----- -----

    // 22. infoTips: Enabled, the "Defaults for everyone" title, the Apps table's mode header, "Apps not listed", both Apps checkboxes, "New
    // users start with" and "Audio tracks with no language". A row's label keeps only its text, the tip beside it in .bf-row-head; one tip
    // each, also after the page draws itself again; the tips behave as on the person page.
    section('22 infoTips');
    {
        const open = { 'babelfin.admin.open.advanced': '1', 'babelfin.admin.open.group.apps': '1', 'babelfin.admin.open.group.newUsers': '1', 'babelfin.admin.open.group.recognition': '1' };
        const e = await boot({ storage: open });
        await waitFor(() => e.page.querySelector('.bf-apps-table') && agroup(e, 'newUsers').body.querySelector('h5.bf-sub-title'), 'Advanced groups');
        await settle(20);
        const p = e.page;
        const d = e.document;
        const expectTip = (label, node, name, infoKey, control) => {
            const t = tipParts(node);
            ok(!!t.button && !!t.tip, label + ': has an infoTip');
            if (!t.button || !t.tip) { return t; }
            eq(node.querySelectorAll('.bfui-info').length, 1, label + ': exactly one');
            eq([t.button.getAttribute('type'), t.button.getAttribute('aria-describedby'), t.button.getAttribute('aria-expanded'), t.tip.getAttribute('role'), t.tip.hidden, t.tip.parentNode === t.wrap],
                ['button', t.tip.id, 'false', 'tooltip', true, true], label + ': an "i" button described by its hidden tooltip');
            eq(t.tip.textContent, en[infoKey], label + ': the tip says ' + infoKey);
            eq(t.button.getAttribute('aria-label'), 'More about ' + name, label + ': the button is named "More about ' + name + '"');
            if (control) { ok(describedBy(control).indexOf(t.tip.id) >= 0, label + ': the control is described by the tip too', control.getAttribute('aria-describedby')); }
            return t;
        };
        const rowShape = (label, r, control) => {
            const head = r.querySelector('.bf-row-head');
            const lab = head ? head.querySelector('.bf-row-label') : null;
            const wrap = head ? head.querySelector('.bfui-info-wrap') : null;
            ok(!!head && head.parentNode === r && !!lab && lab.parentNode === head && !!wrap && wrap.parentNode === head, label + ': the label and the tip side by side in .bf-row-head');
            if (lab) {
                eq([lab.localName, lab.textContent, lab.getAttribute('for')], ['label', label, control.id], label + ': a <label> holding only its text, naming the control');
            }
        };
        // Enabled, in the page's static header.
        const enabled = p.querySelector('#bfEnabled');
        const line = enabled.parentNode.parentNode;
        ok(line.classList.contains('bf-check-line') && line.parentNode === p.querySelector('.bf-head') && enabled.parentNode.classList.contains('checkboxContainer'), 'Enabled: its label sits in a .bf-check-line in the header');
        const tEnabled = expectTip('Enabled', line, en['page.enabled'], 'page.enabled.info', enabled);
        ok(!!tEnabled.wrap && tEnabled.wrap.classList.contains('bf-enabled-info') && tEnabled.wrap.parentNode === line && !enabled.parentNode.contains(tEnabled.wrap), 'Enabled: the tip sits beside its label, not inside it');
        eq(enabled.parentNode.textContent, en['page.enabled'], 'Enabled: the label holds only its text');
        // The "Defaults for everyone" title.
        const title = p.querySelector('#bfDefaultsTitle');
        //: the heading holds only its text (its accessible name); the tip sits beside it in .bf-title-line.
        ok(title.parentNode.classList.contains('bf-title-line') && title.textContent === en['defaults.title'] && !title.querySelector('.bfui-info'), 'Defaults for everyone: the title keeps only its text, the tip beside it');
        expectTip('Defaults for everyone', title.parentNode, en['defaults.title'], 'defaults.info');
        // The Apps table's mode header.
        const ths = p.querySelector('.bf-apps-table').querySelectorAll('thead th');
        eq(ths.map((th) => plainText(th)), [en['apps.app'], en['apps.mode'], en['apps.rememberPrePlay']], 'the Apps table headers keep their text');
        eq(ths[0].querySelectorAll('.bfui-info').length, 0, 'the App column has no tip');
        expectTip('the Apps mode header', ths[1], en['apps.mode'], 'apps.mode.info');
        expectTip('pre-play memory header', ths[2], en['apps.rememberPrePlay'], 'apps.rememberPrePlay.info');
        ok(['Trust', 'ReplaceOnAutoplay', 'ReplaceUnlessManual'].every((m) => en['apps.mode.info'].indexOf(en['apps.mode.' + m]) >= 0), 'the mode header\'s tip explains all three modes');
        // Apps not listed.
        const apps = agroup(e, 'apps').body;
        const notListed = apps.querySelectorAll('.bf-row').find((r) => r.querySelector('select') && plainText(r).indexOf(en['apps.notListed']) === 0);
        rowShape(en['apps.notListed'], notListed, notListed.querySelector('select'));
        expectTip(en['apps.notListed'], notListed, en['apps.notListed'], 'apps.notListed.info', notListed.querySelector('select'));
        // Both checkboxes.
        const checkInput = (key) => apps.querySelectorAll('input[type="checkbox"]').find((i) => i.parentNode.textContent === en[key]);
        for (const key of ['option.correctAfterStart', 'option.compatibility']) {
            const input = checkInput(key);
            ok(!!input, en[key] + ': the checkbox, its label holding only its text');
            if (!input) { continue; }
            const chk = input.parentNode.parentNode;
            ok(chk.classList.contains('bf-check-line') && input.parentNode.classList.contains('checkboxContainer'), en[key] + ': the label and the tip in a .bf-check-line');
            const t = expectTip(en[key], chk, en[key], key + '.info', input);
            ok(!!t.wrap && t.wrap.parentNode === chk && !input.parentNode.contains(t.wrap), en[key] + ': the tip is outside the label');
        }
        // New users start with.
        const nuTitle = agroup(e, 'newUsers').body.querySelector('h5.bf-sub-title');
        ok(nuTitle.parentNode.classList.contains('bf-title-line') && nuTitle.textContent === en['template.title'] && !nuTitle.querySelector('.bfui-info'), 'New users start with: the title keeps only its text, the tip beside it');
        expectTip(en['template.title'], nuTitle.parentNode, en['template.title'], 'template.info');
        // Audio tracks with no language.
        const rec = agroup(e, 'recognition').body;
        const unl = rec.querySelectorAll('.bf-row').find((r) => r.querySelector('select') && plainText(r).indexOf(en['field.UnlabelledAudio']) === 0);
        rowShape(en['field.UnlabelledAudio'], unl, unl.querySelector('select'));
        expectTip(en['field.UnlabelledAudio'], unl, en['field.UnlabelledAudio'], 'field.UnlabelledAudio.info', unl.querySelector('select'));
        // In all.
        const buttons = p.querySelectorAll('.bfui-info').filter((button) => !button.closest('.bfui-table-cell, section.bfui-genres'));
        eq(buttons.length, 9, 'nine infoTips on the page');
        eq(new Set(buttons.map((b) => b.getAttribute('aria-describedby'))).size, 9, 'each with its own tip');
        eq(p.querySelectorAll('.bf-row-label').filter((l) => l.querySelector('.bfui-info-wrap, .bfui-info, .bfui-tip')).length, 0, 'no .bf-row-label on the page holds a tip');
        eq(p.querySelectorAll('.bf-row').filter((r) => r.querySelector('.bfui-info-wrap') && !r.querySelector('.bf-row-head')).length, 0, 'every row with a tip has a .bf-row-head');
        // Help lines in Track recognition.
        const h5s = rec.querySelectorAll('h5.bf-sub-title');
        const codesTitle = h5s.find((x) => x.textContent === en['codes.title']);
        ok(!!codesTitle && !!codesTitle.nextElementSibling && codesTitle.nextElementSibling.classList.contains('bf-hint') && codesTitle.nextElementSibling.textContent === en['codes.help'], 'Language codes: a help line (codes.help) under its title');
        const namesTitle = h5s.find((x) => x.textContent === en['tracknames.title']);
        ok(!!namesTitle && !!namesTitle.nextElementSibling && namesTitle.nextElementSibling.textContent === en['tracknames.help'], 'Track names: its help line (tracknames.help)');
        // Behaviour.
        for (const [label, node] of [['Enabled', line], ['Defaults for everyone', title.parentNode], ['the Apps mode header', ths[1]], ['Apps not listed', notListed], ['New users start with', nuTitle.parentNode], ['Audio tracks with no language', unl]]) {
            const t = tipParts(node);
            fire(t.button, 'mouseenter');
            ok(tipShown(t) && d.body.lastChild === t.tip, label + ': the mouse over the "i" shows the tip, at the end of <body>');
            fire(t.button, 'mouseleave');
            ok(tipHidden(t), label + ': the mouse leaving hides it, back in its wrap');
            fire(t.button, 'focus');
            ok(tipShown(t), label + ': keyboard focus shows it');
            fire(t.button, 'blur');
            ok(tipHidden(t), label + ': blur hides it');
        }
        const wasOn = enabled.checked;
        tEnabled.button.click();
        ok(tipShown(tEnabled), 'a click pins the Enabled tip');
        eq([enabled.checked, saveBarHidden(e)], [wasOn, true], 'and leaves Enabled as it was, with nothing to save');
        fire(tEnabled.button, 'mouseleave');
        ok(tipShown(tEnabled), 'pinned: it stays after the mouse leaves');
        const esc = fire(d, 'keydown', { key: 'Escape', bubbles: true });
        ok(tipHidden(tEnabled) && esc.defaultPrevented, 'Escape hides it (and is consumed)');
        const compat = checkInput('option.compatibility');
        const tCompat = tipParts(compat.parentNode.parentNode);
        const compatWas = compat.checked;
        tCompat.button.click();
        eq([tipShown(tCompat), compat.checked, saveBarHidden(e)], [true, compatWas, true], 'a click on a checkbox\'s tip shows it and leaves the box as it was');
        const tNu = tipParts(nuTitle.parentNode);
        fire(tNu.button, 'mouseenter');
        ok(tipShown(tNu) && tipHidden(tCompat), 'opening another tip closes the pinned one');
        fire(tNu.button, 'mouseleave');
        tCompat.button.click();
        fire(p.querySelector('.bf-head h2'), 'pointerdown', { bubbles: true });
        ok(tipHidden(tCompat), 'a press elsewhere hides it');
        eq(d.body.children.filter((c) => c.classList.contains('bfui-tip')).length, 0, 'no tip left on <body>');
        // Drawn again: still one tip each in the static header.
        const once = () => [p.querySelectorAll('.bf-enabled-info').length, p.querySelector('#bfDefaultsTitle').parentNode.querySelectorAll('.bfui-info-wrap').length, p.querySelector('.bf-head').querySelectorAll('.bf-check-line').length,
            describedBy(enabled).filter((id) => /^bfui-tip/.test(id)).length];
        enabled.click();
        await save(e);
        eq(once(), [1, 1, 1, 1], 'after a save: one Enabled tip, one title tip, Enabled described by one tip');
        enabled.click();
        p.querySelector('#bfDiscard').click();
        await settle(20);
        eq(once(), [1, 1, 1, 1], 'after Cancel: still one each');
        p.dispatchEvent(new Event('pagehide'));
        p.dispatchEvent(new Event('pageshow'));
        await settle(60);
        await waitFor(() => p.querySelector('.bf-apps-table'), 'Advanced again');
        await settle(20);
        eq(once(), [1, 1, 1, 1], 'after the page is shown again: still one each');
        eq(p.querySelectorAll('.bfui-info').filter(button => !button.closest('.bfui-table-cell, section.bfui-genres')).length, 9, 'and nine unchanged admin tips outside table choices');
        eq(e.consoleErrors, [], 'no console errors');
    }

    // 23. Audio tracks with no language: the label and option words of the settings contract, the rare option only when stored or shipped.
    section('23 Audio tracks with no language');
    {
        const BASE_WORDS = ['Leave it unknown', "The title's original language", "The title's original language, when it's the only audio track"];
        const USUAL = "The library's usual original language";
        eq([en['field.UnlabelledAudio']].concat(['Unknown', 'OriginalLanguage', 'OriginalWhenOnlyTrack', 'LibraryUsualOriginalLanguage'].map((k) => en['unlabelled.' + k])), ['Audio tracks with no language'].concat(BASE_WORDS, [USUAL]),
            'i18n-admin/en.json has the label and the words of the settings contract');
        const store = { 'babelfin.admin.open.advanced': '1', 'babelfin.admin.open.group.recognition': '1' };
        const rowOf = (e) => agroup(e, 'recognition').body.querySelectorAll('.bf-row').find((r) => r.querySelector('select') && plainText(r).indexOf('Audio tracks with no language') === 0);
        const withRule = (rule, code) => () => { const c = initialConfig(); c.Everyday.UnlabelledAudio = rule; c.Everyday.UnlabelledAudioLanguage = code || null; return c; };
        const shippedUsual = () => { const c = shippedConfig(); c.Everyday = { UnlabelledAudio: 'LibraryUsualOriginalLanguage' }; return c; };
        for (const [label, opts, words, value] of [
            ['nothing stored', {}, BASE_WORDS.concat(['A specific language…']), 'OriginalWhenOnlyTrack'],
            ['Unknown stored', { config: withRule('Unknown') }, BASE_WORDS.concat(['A specific language…']), 'Unknown'],
            ['the library\'s usual original language stored', { config: withRule('LibraryUsualOriginalLanguage') }, BASE_WORDS.concat([USUAL, 'A specific language…']), 'LibraryUsualOriginalLanguage'],
            ['the library\'s usual original language shipped', { shipped: shippedUsual }, BASE_WORDS.concat([USUAL, 'A specific language…']), 'LibraryUsualOriginalLanguage'],
            ['a specific language stored', { config: withRule('SpecificLanguage', 'ja') }, BASE_WORDS.concat(['Japanese', 'A specific language…']), 'lang:ja']
        ]) {
            const e = await boot(Object.assign({ storage: store }, opts));
            await waitFor(() => rowOf(e), label + ': the row');
            await settle(10);
            const sel = rowOf(e).querySelector('select');
            eq([optionTexts(sel), sel.value], [words, value], label + ': the options in order, the value selected');
            eq(rowOf(e).querySelector('.bf-row-label').textContent, 'Audio tracks with no language', label + ': the label');
        }
    }

    // 24. Reset all settings: last in Back up and restore; a confirmation; POST Admin/ResetAll; the backup's path; the page drawn again with
    // the fresh settings. An error answer shows in the block and changes nothing.
    section('24 Reset all settings');
    {
        const store = { 'babelfin.admin.open.backup': '1', 'babelfin.admin.open.advanced': '1', 'babelfin.admin.open.group.apps': '1', 'babelfin.admin.open.group.recognition': '1' };
        const custom = () => { const c = initialConfig(); c.Enabled = false; c.UnknownAppMode = 'ReplaceUnlessManual'; c.Keywords.SdhWords = ['sdh', 'hoh']; return c; };
        const ready = (e) => e.page.querySelector('#bfBackup .bf-reset-title') && e.page.querySelector('.bf-apps-table');
        const e = await boot({ config: custom, storage: store });
        await waitFor(() => ready(e), 'Back up and restore');
        await settle(20);
        const p = e.page;
        const d = e.document;
        const bk = p.querySelector('#bfBackup');
        const block = () => {
            const kids = bk.children;
            const at = kids.indexOf(bk.querySelector('h5.bf-reset-title'));
            return at < 0 ? [] : kids.slice(at);
        };
        const resetButton = () => (block()[2] ? block()[2].querySelector('button') : null);
        eq(block().map((c) => c.localName + '.' + c.className), ['h5.bf-sub-title bf-reset-title', 'p.bf-hint', 'div.bf-actions', 'div.bf-alert bf-alert-error hide', 'div.bf-reset-done'],
            'the block: a title, a help line, the button, a place for an error and one for the result, last in Back up and restore');
        const fileRow = bk.querySelector('input[type="file"]').closest('.bf-row');
        ok(bk.children.indexOf(fileRow) >= 0 && bk.children.indexOf(fileRow) < bk.children.indexOf(block()[0]), 'it comes after Restore from a file');
        eq([block()[0].textContent, block()[1].textContent], [en['reset.title'], en['reset.help']], 'the title and the help line (reset.title, reset.help)');
        ok(/download all settings first/i.test(en['reset.help']), 'the help line recommends downloading all settings first');
        const btn = resetButton();
        const exportButton = bk.querySelectorAll('button').find((b) => b.textContent === en['backup.export']);
        eq([btn.localName, btn.getAttribute('type'), btn.getAttribute('is'), btn.textContent, btn.firstElementChild && btn.firstElementChild.localName], ['button', 'button', 'emby-button', en['reset.button'], 'span'], 'the button: an emby-button with its text in a span');
        eq(['raised', 'emby-button', 'button-delete'].map((c) => btn.classList.contains(c)), [true, true, true], 'classes raised and emby-button (Jellyfin\'s centred button style, like the other buttons) and button-delete (the danger style)');
        ok(!!exportButton && ['raised', 'emby-button'].every((c) => exportButton.classList.contains(c)), 'the same base classes as Download all settings');
        // Cancel.
        const before = clone(e.server.state.config);
        let n = e.server.state.requests.length;
        btn.click();
        await settle(5);
        const box = d.querySelector('.bfui-dialog');
        ok(!!box, 'the button asks first');
        if (box) {
            eq([box.querySelector('.bfui-dialog-title').textContent, box.querySelector('.bfui-dialog-message').textContent, box.getAttribute('role')], [en['reset.confirmTitle'], en['reset.confirmText'], 'alertdialog'],
                'the confirmation: reset.confirmTitle and reset.confirmText, as a warning');
            eq(box.querySelectorAll('button').map((b) => [b.textContent, b.classList.contains('bfui-btn-danger')]), [['Cancel', false], [en['reset.ok'], true]], 'its buttons: Cancel, and "Reset" as the danger button');
        }
        await dialogAnswer(e, false);
        eq([e.server.state.requests.slice(n).filter((r) => /ResetAll/.test(r)), e.server.state.resetCalls || 0], [[], 0], 'Cancel sends nothing');
        eq([diff(before, e.server.state.config), block()[4].textContent, p.querySelector('#bfEnabled').checked, peopleCells(e, 'Anna')], [[], '', false, ['Set per library', 'Set per library']], 'and changes nothing on the page');
        // OK, with an unsaved change on the page.
        const moviesAudio = libSelect(e, 'Movies', 'audio');
        ok(!!moviesAudio, 'the page is still as loaded (per library) before OK');
        if (moviesAudio) { choose(moviesAudio, 'lang:ja'); }
        ok(!saveBarHidden(e), 'an unsaved change before the reset');
        n = e.server.state.requests.length;
        const puts = e.server.state.puts.length;
        resetButton().click();
        await dialogAnswer(e, true);
        await waitFor(() => block()[4] && block()[4].textContent, 'the reset result');
        await settle(40);
        const after = e.server.state.requests.slice(n);
        eq(after.filter((r) => /ResetAll/.test(r)), ['POST Babelfin/Admin/ResetAll?allowPartial=true'], 'OK sends POST Babelfin/Admin/ResetAll, once');
        const at = after.indexOf('POST Babelfin/Admin/ResetAll?allowPartial=true');
        ok(after.indexOf('GET Babelfin/Admin/Settings', at) > at, 'then reads Admin/Settings again', after);
        ok(after.indexOf('GET Babelfin/Admin/Users', at) > at, 'and Admin/Users, for the People table', after);
        eq(block()[4].textContent, en['reset.done'].split('{path}').join(en['backup.relativePath'].split('{path}').join(RESET_PATH)), 'the result names where the copy of the previous settings is (reset.done with the path)');
        ok(!!block()[4].querySelector('.bf-alert-ok') && block()[3].classList.contains('hide'), 'shown as a success, no error');
        eq(e.server.state.puts.length, puts, 'no PUT Admin/Settings: the server did the reset');
        eq(p.querySelector('#bfEnabled').checked, true, 'Enabled shows the shipped value');
        const allRow = libRow(e, 'All libraries');
        eq([modeOf(p.querySelector('#bfLibraries')).mode, rowNamesIn(p.querySelector('#bfLibraries')), allRow ? cells(allRow).map((s) => s.value) : null], ['same', ['All libraries'], ['', '']],
            'Defaults for everyone shows the shipped table (Same for every library, nothing set)');
        ok(saveBarHidden(e), 'the unsaved change went with the old settings: nothing to save');
        eq([peopleCells(e, 'Anna'), peopleCells(e, 'Ben')], [['Default', 'Default'], ['Default', 'Default']], 'the People table is drawn again: Anna starts over');
        const appsNotListed = agroup(e, 'apps').body.querySelectorAll('.bf-row').find((r) => r.querySelector('select') && plainText(r).indexOf(en['apps.notListed']) === 0);
        const sdhWords = agroup(e, 'recognition').body.querySelectorAll('.bf-row').find((r) => r.querySelector('.bf-row-label') && r.querySelector('.bf-row-label').textContent === 'SDH').querySelector('textarea');
        eq([appsNotListed.querySelector('select').value, sdhWords.value], ['Trust', shippedConfig().Keywords.SdhWords.join('\n')], 'Advanced is drawn again with the shipped values');
        eq([p.querySelectorAll('.bf-enabled-info').length, p.querySelector('#bfDefaultsTitle').parentNode.querySelectorAll('.bfui-info-wrap').length], [1, 1], 'still one tip on Enabled and one on the title');
        ok(!resetButton().disabled, 'the button is ready again');
        eq(e.consoleErrors, [], 'no console errors');
        // An error answer.
        const e2 = await boot({ config: custom, storage: store, resetFails: true });
        await waitFor(() => ready(e2), 'Back up and restore 2');
        await settle(20);
        const bk2 = e2.page.querySelector('#bfBackup');
        const block2 = () => bk2.children.slice(bk2.children.indexOf(bk2.querySelector('h5.bf-reset-title')));
        const before2 = clone(e2.server.state.config);
        choose(libSelect(e2, 'Movies', 'audio'), 'lang:ja');
        const n2 = e2.server.state.requests.length;
        block2()[2].querySelector('button').click();
        await dialogAnswer(e2, true);
        await settle(40);
        const after2 = e2.server.state.requests.slice(n2);
        eq(after2.filter((r) => /ResetAll/.test(r)), ['POST Babelfin/Admin/ResetAll?allowPartial=true'], 'error: the request was sent');
        ok(!block2()[3].classList.contains('hide') && block2()[3].textContent.indexOf(RESET_FAILED) >= 0, 'error: the server\'s message shows in the block', block2()[3].textContent);
        eq(block2()[4].textContent, '', 'error: no success line');
        ok(after2.indexOf('GET Babelfin/Admin/Settings') < 0 && after2.indexOf('GET Babelfin/Admin/Users') < 0, 'error: nothing is read again', after2);
        eq([diff(before2, e2.server.state.config), libSelect(e2, 'Movies', 'audio').value, saveBarHidden(e2), e2.page.querySelector('#bfEnabled').checked, peopleCells(e2, 'Anna')],
            [[], 'lang:ja', false, false, ['Set per library', 'Set per library']], 'error: the settings and the page are left as they were, the unsaved change too');
        ok(!block2()[2].querySelector('button').disabled, 'error: the button is ready again');
        block2()[2].querySelector('button').click();
        await dialogAnswer(e2, false);
        ok(block2()[3].classList.contains('hide') && !block2()[3].textContent, 'trying again clears the old error');
    }

    // 17. No Coverage request was made by any page in this run.
/* >>> admin */
    // ----- the People editor's "Show and movie overrides" section -----

    const ovGroupOf = (e) => editor(e).querySelector('#bf-group-overrides');
    const ovButtonOf = (e) => ovGroupOf(e).querySelector('.bfui-disclosure-button');
    const ovTitleOf = (e) => ovButtonOf(e).querySelector('.bfui-disclosure-title').textContent;
    const ovIsOpen = (e) => ovButtonOf(e).getAttribute('aria-expanded') === 'true' && !ovGroupOf(e).querySelector('.bfui-disclosure-body').hidden;
    const ovOpenIt = async (e) => {
        if (!ovIsOpen(e)) { ovButtonOf(e).click(); await settle(5); }
        return ovGroupOf(e);
    };
    const ovRowsOf = (e) => ovGroupOf(e).querySelectorAll('.bfui-overrides-row').map((tr) => ({
        tr,
        item: tr.getAttribute('data-item'),
        name: tr.querySelector('.bfui-overrides-title').textContent,
        note: tr.querySelector('.bfui-overrides-note') ? tr.querySelector('.bfui-overrides-note').textContent : null,
        library: tr.querySelector('.bfui-overrides-library').textContent,
        audio: tr.querySelector('.bfui-overrides-audio').textContent,
        subtitles: tr.querySelector('.bfui-overrides-subtitles').textContent,
        remove: tr.querySelector('.bfui-overrides-remove')
    }));
    const ovNamesOf = (e) => ovRowsOf(e).map((r) => r.name);
    const saveButtonOf = (e) => editor(e).querySelectorAll('button').find((x) => x.textContent === 'Save');
    const copyButtonOf = (e) => editor(e).querySelectorAll('button').find((x) => x.textContent === 'Copy settings to…');
    const openEditorOf = async (e, who) => {
        prow(e, who).querySelector('button').click();
        await waitFor(() => e.page.querySelector('.bf-editor-row .bf-person-name') && e.page.querySelector('.bf-editor-row .bf-person-name').textContent === who && editor(e).querySelector('#bf-group-overrides'), 'editor of ' + who);
        await settle(10);
        return editor(e);
    };
    const deletesOf = (e) => e.server.state.deletes || [];
    const gets = (e, who) => e.server.state.requests.filter((r) => r === 'GET Babelfin/Admin/Users/' + who + '/Overrides').length;
    const toastsOf = (e) => e.document.querySelectorAll('.bfui-toast').map((x) => x.textContent);
    const errorBoxOf = (e) => {
        const box = editor(e).querySelector('.bf-alert-error');
        return box && !isHidden(box) ? box.textContent : null;
    };
    const idsOf = (list) => list.map((x) => x.ItemId[0]);
    /** Answers the open confirmation (yes: its second button); returns what it said. */
    async function confirmWords(e, yes) {
        await settle(5);
        const box = e.document.querySelector('.bfui-dialog');
        if (!box) { return null; }
        const said = { title: box.querySelector('.bfui-dialog-title').textContent, message: box.querySelector('.bfui-dialog-message').textContent, buttons: box.querySelectorAll('button').map((b) => b.textContent) };
        box.querySelectorAll('button')[yes ? 1 : 0].click();
        await settle(15);
        return said;
    }
    const SABLE = ['Sable Voyage', 'Anime', 'Japanese', 'English'];
    const LANTERN = ['Étoile Lantern (2001)', 'Movies', 'French', 'Off'];

    // 25. The People editor lists the person's show and movie overrides after their table: a collapsible section titled with its
    // count, closed by default and remembered, read when the editor opens, with the admin's help line and the same table as the person's page.
    section('25 People: overrides section');
    {
        const e = await boot({});
        ok(!e.server.state.requests.some((r) => /Overrides/.test(r)), 'the People table asks for no overrides (they are read when an editor opens)');
        const ed = await openEditorOf(e, 'Anna');
        eq(e.server.state.overrideGets, 1, 'opening Anna\'s editor reads GET Admin/Users/{id}/Overrides once');
        eq(gets(e, USER.anna), 1, 'for her');
        const order = ed.firstElementChild.children.map((c) => c.className.split(' ')[0]);
        const at = (name) => order.indexOf(name);
        ok(at('bf-person-table') >= 0 && at('bf-person-overrides') === at('bf-person-table') + 1 && at('bf-person-overrides') < at('bf-actions'), 'the section follows the person\'s table, before the Save buttons', order);
        const group = ovGroupOf(e);
        eq([group.tagName, group.className.indexOf('bf-group') >= 0, group.querySelector('.bfui-disclosure-heading').tagName], ['SECTION', true, 'H4'], 'a collapsible group like the others in the page');
        eq(ovTitleOf(e), 'Show and movie overrides (2)', 'titled "Show and movie overrides (N)"');
        eq(ovTitleOf(e), enUi['overrides.title'] + ' (2)', 'from overrides.title (the person page\'s words)');
        eq([ovButtonOf(e).getAttribute('aria-expanded'), group.querySelector('.bfui-disclosure-body').hidden, group.querySelector('.bfui-disclosure-body').childNodes.length], ['false', true, 0], 'closed by default; the table is built when it is first opened');
        ok(saveButtonOf(e).disabled && !copyButtonOf(e).disabled, 'nothing to save');
        ovButtonOf(e).click();
        await settle(5);
        eq([ovIsOpen(e), e.storage.get('babelfin.admin.open.group.overrides')], [true, '1'], 'opens and remembers (babelfin.admin.open.group.overrides)');
        const help = ovGroupOf(e).querySelector('.bf-hint');
        eq([help.textContent, help.textContent.indexOf('this person') >= 0], [enUi['overrides.helpAdmin'], true], 'the admin\'s help line (overrides.helpAdmin)');
        eq(ovRowsOf(e).map((r) => [r.name, r.library, r.audio, r.subtitles]), [SABLE, LANTERN], 'the rows: name, library, audio and subtitles in the table\'s words');
        eq(ovRowsOf(e).map((r) => r.note), [null, 'Remembered from a track change'], 'and the remembered track change noted');
        eq(ovGroupOf(e).querySelectorAll('thead th').map((th) => th.textContent), ['Show or movie', 'Library', 'Audio', 'Subtitles', 'Remove'], 'columns');
        eq(ovRowsOf(e).map((r) => r.remove.getAttribute('aria-label')), ['Remove the override for Sable Voyage', 'Remove the override for Étoile Lantern (2001)'], 'a Remove per row, named for its item');
        eq([ovGroupOf(e).querySelector('.bfui-overrides-remove-all').textContent, ovGroupOf(e).querySelector('.bfui-overrides-remove-all').classList.contains('bfui-btn-danger')], ['Remove all show and movie overrides', true], 'Remove all under the table');
        eq(ovGroupOf(e).querySelectorAll('select, input, label').length, 0, 'no field in the section: the rows are plain text');
        ovButtonOf(e).click();
        await settle(5);
        eq([ovIsOpen(e), e.storage.get('babelfin.admin.open.group.overrides')], [false, '0'], 'closes and remembers');
        // Another person: none, and the editor says so; a person with no file yet.
        const ben = await openEditorOf(e, 'Ben');
        eq([ovTitleOf(e), ovIsOpen(e)], ['Show and movie overrides (0)', false], 'Ben has none: (0)');
        await ovOpenIt(e);
        eq([ben.querySelector('.bfui-overrides-empty').textContent, !!ben.querySelector('.bfui-overrides-remove-all'), !!ben.querySelector('.bfui-overrides-table')], ['No show or movie overrides yet.', false, false], 'the section still shows, with its line, and no Remove all');
        await openEditorOf(e, 'Cleo');
        eq(ovTitleOf(e), 'Show and movie overrides (0)', 'a person with no file: (0)');
        eq(e.server.state.overrideGets, 3, 'one read per editor opened');
        eq(e.consoleErrors, [], 'no console errors');
        // A new page opens the section as it was left.
        const open = await boot({ storage: { 'babelfin.admin.open.group.overrides': '1' } });
        await openEditorOf(open, 'Anna');
        eq([ovIsOpen(open), ovNamesOf(open)], [true, ['Sable Voyage', 'Étoile Lantern (2001)']], 'a new page opens it when it was left open');
        // The words are the person\'s own: the table is the one the editor's table uses (Default ... audience person).
        eq(ovGroupOf(open).querySelector('table').getAttribute('aria-label'), 'Show and movie overrides', 'the table is labelled');
    }

    // 26. Removing one override from the editor: at once (DELETE Admin/Users/{id}/Overrides/{itemId}?scope=), no confirmation, nothing to save, the
    // editor's own edits untouched; the Save that follows keeps the stored overrides.
    section('26 People: removing one override');
    {
        const e = await boot({ storage: { 'babelfin.admin.open.group.overrides': '1' } });
        const ed = await openEditorOf(e, 'Anna');
        const sel = esel(e, 'Anime', 'audio');
        choose(sel, 'lang:fr');
        ok(!saveButtonOf(e).disabled && copyButtonOf(e).disabled, 'an edit not saved yet: Save on, Copy settings to… off');
        const putsBefore = e.server.state.userPuts.length;
        ovRowsOf(e)[0].remove.click();
        await settle(20);
        ok(!e.document.querySelector('.bfui-dialog'), 'no confirmation');
        eq(deletesOf(e), ['DELETE Babelfin/Admin/Users/' + USER.anna + '/Overrides/' + OVR.sable + '?scope=Series'], 'one DELETE, with the item and its scope');
        eq(toastsOf(e), ['Override removed.'], 'a toast says so (overrides.removed)');
        eq([ovTitleOf(e), ovNamesOf(e), gets(e, USER.anna)], ['Show and movie overrides (1)', ['Étoile Lantern (2001)'], 2], 'the count and the rows follow; the list was read again');
        eq([e.server.state.userPuts.length - putsBefore, sel.value], [0, 'lang:fr'], 'nothing was saved and the unsaved edit is still shown');
        ok(!saveButtonOf(e).disabled, 'Save is on only because of the edit');
        ok(e.document.activeElement === ovRowsOf(e)[0].remove, 'focus moved to the next row\'s Remove');
        // Save: keepOverrides, the removed override does not come back.
        const put = await editorSave(e);
        eq(put.query, { keepOverrides: 'true', fields: 'libraries.' + LIB.anime + '.settings.audioChain' }, 'Save sends PUT …/Settings?keepOverrides=true');
        eq([idsOf(put.body.Overrides), idsOf(e.server.state.docs[USER.anna].Overrides)], [['b'], ['b']], 'without the removed override; the server still has the other');
        eq(put.body.Overrides.map((o) => o.Name), ['Étoile Lantern'], 'the saved list is the other one, as the server holds it');
        eq([ovTitleOf(e), ovIsOpen(e), ovNamesOf(e)], ['Show and movie overrides (1)', true, ['Étoile Lantern (2001)']], 'the reopened editor shows the section open with one row');
        // Nothing else changed: no save at all, and the editor closes without asking.
        ovRowsOf(e)[0].remove.click();
        await settle(20);
        eq([ovTitleOf(e), saveButtonOf(e).disabled, copyButtonOf(e).disabled, !!editor(e).querySelector('.bfui-overrides-empty')], ['Show and movie overrides (0)', true, false, true], 'removing the last one: (0), the empty line, nothing to save (Save off, Copy on)');
        ok(e.document.activeElement === ovButtonOf(e), 'focus went to the section\'s heading');
        eq(e.server.state.userPuts.length - putsBefore, 1, 'still just the one save');
        editor(e).querySelectorAll('button').find((b) => b.textContent === 'Close').click();
        await settle(10);
        ok(!e.document.querySelector('.bfui-dialog') && !editor(e), 'Close does not ask: a removal leaves nothing unsaved');
        eq(idsOf(e.server.state.docs[USER.anna].Overrides), [], 'the server has none left');
        eq(e.consoleErrors, [], 'no console errors');
    }
    {
        // Remove all: the confirmation names the person; Cancel; OK; the editor's own edits stay; Save keeps none.
        const e = await boot({ storage: { 'babelfin.admin.open.group.overrides': '1' } });
        const ed = await openEditorOf(e, 'Anna');
        choose(esel(e, 'Anime', 'audio'), 'lang:fr');
        ed.querySelector('.bfui-overrides-remove-all').click();
        let said = await confirmWords(e, false);
        eq(said, { title: 'Remove all show and movie overrides?', message: "Every show and movie goes back to Anna's library choices. This can't be undone.", buttons: ['Cancel', 'Remove'] }, 'it asks first and names the person (overrides.removeAll.confirmTextFor)');
        eq([deletesOf(e), ovNamesOf(e).length], [[], 2], 'Cancel sends nothing');
        ed.querySelector('.bfui-overrides-remove-all').click();
        said = await confirmWords(e, true);
        eq(deletesOf(e), ['DELETE Babelfin/Admin/Users/' + USER.anna + '/Overrides/All'], 'OK: DELETE Admin/Users/{id}/Overrides, nothing else');
        eq(e.server.state.removedAll, 1, 'the server dropped them all');
        eq(toastsOf(e), ['All show and movie overrides were removed.'], 'a toast says so (overrides.removeAll.done)');
        eq([ovTitleOf(e), !!ed.querySelector('.bfui-overrides-empty'), !!ed.querySelector('.bfui-overrides-remove-all'), e.document.activeElement === ovButtonOf(e)], ['Show and movie overrides (0)', true, false, true], '(0), the empty line, no Remove all, focus on the section\'s heading');
        eq([esel(e, 'Anime', 'audio').value, saveButtonOf(e).disabled], ['lang:fr', false], 'the unsaved edit is still there');
        const put = await editorSave(e);
        eq([put.query, put.body.Overrides, e.server.state.docs[USER.anna].Overrides], [{ keepOverrides: 'true', fields: 'libraries.' + LIB.anime + '.settings.audioChain' }, [], []], 'Save afterwards: none comes back');
        // The name in the confirmation is the one the row of the People table shows.
        e.server.state.docs[USER.ben].Overrides = [{ ItemId: OVR.glassHarbor, Scope: 'Series', Name: 'Glass Harbor', Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' }, Manual: null, UpdatedAt: '2026-10-04T00:00:00Z' }];
        const ben = await openEditorOf(e, 'Ben');
        eq([ovTitleOf(e), ovIsOpen(e)], ['Show and movie overrides (1)', true], 'Ben now has one');
        ben.querySelector('.bfui-overrides-remove-all').click();
        said = await confirmWords(e, false);
        eq(said && said.message, "Every show and movie goes back to Ben's library choices. This can't be undone.", 'the confirmation names Ben');
    }
    {
        // Remove all with nothing else changed: nothing is left to save (the editor's baseline lost them too), and Close doesn't ask.
        const e = await boot({ storage: { 'babelfin.admin.open.group.overrides': '1' } });
        const ed = await openEditorOf(e, 'Anna');
        ok(saveButtonOf(e).disabled && !copyButtonOf(e).disabled, 'nothing to save to begin with');
        ed.querySelector('.bfui-overrides-remove-all').click();
        await confirmWords(e, true);
        eq([ovTitleOf(e), saveButtonOf(e).disabled, copyButtonOf(e).disabled, e.server.state.requests.filter((r) => /^PUT /.test(r)).length], ['Show and movie overrides (0)', true, false, 0], 'Remove all alone: Save stays off, Copy settings to… on, no PUT');
        ed.querySelectorAll('button').find((b) => b.textContent === 'Close').click();
        await settle(10);
        ok(!e.document.querySelector('.bfui-dialog') && !editor(e), 'Close does not ask');
    }

    // 27. Failures: a removal that fails, one that was already done, a list that can't be read, rows without a usable id.
    section('27 People: overrides failures');
    {
        const opts = { fail: {}, storage: { 'babelfin.admin.open.group.overrides': '1' } };
        const e = await boot(opts);
        const ed = await openEditorOf(e, 'Anna');
        opts.fail['DELETE Babelfin/Admin/Users/' + USER.anna + '/Overrides/' + OVR.sable] = 500;
        ovRowsOf(e)[0].remove.click();
        await settle(20);
        eq([ovNamesOf(e), ovTitleOf(e), errorBoxOf(e), e.document.querySelectorAll('.bfui-toast-error').length], [['Sable Voyage', 'Étoile Lantern (2001)'], 'Show and movie overrides (2)', 'failed', 0], '500: the rows and the count stay; the editor\'s message shows what went wrong');
        ok(ovRowsOf(e).every((r) => !r.remove.disabled) && !ed.querySelector('.bfui-overrides-remove-all').disabled, 'and the buttons are on again');
        eq([idsOf(e.server.state.docs[USER.anna].Overrides), saveButtonOf(e).disabled], [['a', 'b'], true], 'the server still has both; nothing to save');
        ovRowsOf(e)[0].remove.click();
        await settle(20);
        eq([ovNamesOf(e), errorBoxOf(e)], [['Étoile Lantern (2001)'], null], 'trying again works and clears the message');
        // A failing Remove all.
        opts.fail['DELETE Babelfin/Admin/Users/' + USER.anna + '/Overrides/All'] = 500;
        ed.querySelector('.bfui-overrides-remove-all').click();
        await confirmWords(e, true);
        eq([ovNamesOf(e), errorBoxOf(e), e.server.state.removedAll || 0], [['Étoile Lantern (2001)'], 'failed', 0], 'a failed Remove all: the rows stay and the message shows');
        // Gone already (removed on another device): the row goes without an error.
        e.server.state.docs[USER.anna].Overrides = [];
        ovRowsOf(e)[0].remove.click();
        await settle(20);
        eq([ovNamesOf(e), errorBoxOf(e), ovTitleOf(e)], [[], null, 'Show and movie overrides (0)'], '404: the row is gone, no error is shown');
        // The list can't be read again after a removal that worked: the removal stands.
        const opts2 = { fail: {}, storage: { 'babelfin.admin.open.group.overrides': '1' } };
        const e2 = await boot(opts2);
        await openEditorOf(e2, 'Anna');
        opts2.fail['GET Babelfin/Admin/Users/' + USER.anna + '/Overrides'] = 500;
        ovRowsOf(e2)[0].remove.click();
        await settle(20);
        eq([ovNamesOf(e2), ovTitleOf(e2), errorBoxOf(e2)], [['Étoile Lantern (2001)'], 'Show and movie overrides (1)', null], 'the list could not be read again after a removal: the removal stands, no error');
        eq(e2.consoleErrors, [], 'and no console error');
    }
    {
        // The list can't be read when the editor opens: the editor works, the section says so and can try again.
        const opts = { fail: { ['GET Babelfin/Admin/Users/' + USER.anna + '/Overrides']: 500 }, storage: { 'babelfin.admin.open.group.overrides': '1' } };
        const e = await boot(opts);
        const ed = await openEditorOf(e, 'Anna');
        eq(ovTitleOf(e), 'Show and movie overrides', 'no count while the list is not loaded');
        ok(!!ed.querySelector('.bfui-table') && !!ed.querySelector('.bf-person-head'), 'the editor works');
        const body = ovGroupOf(e).querySelector('.bfui-disclosure-body');
        eq(body.querySelector('.bf-hint').textContent, enUi['overrides.helpAdmin'], 'the help line is there');
        ok(!!body.querySelector('.bf-alert-error') && !isHidden(body.querySelector('.bf-alert-error')) && body.querySelector('.bf-alert-error').textContent === 'failed', 'with the problem');
        ok(!body.querySelector('.bfui-overrides-table') && !body.querySelector('.bfui-overrides-remove-all'), 'no table and no Remove all');
        eq(e.consoleErrors.length, 1, 'one console warning for it');
        ok(/the show and movie overrides could not be loaded/.test(e.consoleErrors[0]), 'that says so', e.consoleErrors);
        const refresh = body.querySelectorAll('button').find((b) => b.textContent === 'Refresh');
        ok(!!refresh, 'a Refresh button');
        refresh.click();
        await settle(20);
        eq([ovTitleOf(e), ovNamesOf(e)], ['Show and movie overrides (2)', ['Sable Voyage', 'Étoile Lantern (2001)']], 'Refresh loads the list');
        ok(!ovGroupOf(e).querySelector('.bf-alert-error') && saveButtonOf(e).disabled, 'the message is gone; nothing to save');
        // A server without the route (404) and an answer that is not a list.
        const old = await boot({ fail: { ['GET Babelfin/Admin/Users/' + USER.anna + '/Overrides']: { status: 404, sticky: true } } });
        await openEditorOf(old, 'Anna');
        eq(ovTitleOf(old), 'Show and movie overrides', 'a server without the route: no count, the editor opens');
        const odd = await boot({ summaries: () => ({ Message: 'what' }) });
        await openEditorOf(odd, 'Anna');
        eq(ovTitleOf(odd), 'Show and movie overrides (0)', 'an answer that is not a list: none');
        const none = await boot({ summaries: () => null });
        await openEditorOf(none, 'Anna');
        eq(ovTitleOf(none), 'Show and movie overrides (0)', 'an empty answer: none');
    }
    {
        // Rows without a usable id: drawn, disabled, no request; with ui.js's own guard off the page refuses (DELETE …/Overrides/ removes every override).
        const odd = (id, name) => Object.assign(summarizeOverride({ ItemId: OVR.sable, Scope: 'Title', Name: name, Settings: {}, Manual: null }), { ItemId: id, Name: name });
        const rows = () => [odd('', 'Empty id'), odd(null, 'No id'), odd('xyz', 'Short id'), Object.assign(summarizeOverride({ ItemId: OVR.bluey, Scope: 'Series', Name: 'Bluey', Settings: {}, Manual: null }), { Name: 'Good one' })];
        const e = await boot({ summaries: rows, storage: { 'babelfin.admin.open.group.overrides': '1' } });
        await openEditorOf(e, 'Anna');
        eq(ovRowsOf(e).map((r) => [r.name, r.remove.disabled]), [['Empty id', true], ['Good one', false], ['No id', true], ['Short id', true]], 'a Remove is off for a row without a real id');
        ovRowsOf(e).filter((r) => r.remove.disabled).forEach((r) => r.remove.click());
        await settle(10);
        eq([deletesOf(e), e.server.state.removedAll || 0], [[], 0], 'clicking the disabled ones sends nothing');
        let patched = false;
        const needle = 'return /^[0-9a-f]{32}$/.test(compactId(row.ItemId));';
        const bare = await boot({ summaries: rows, storage: { 'babelfin.admin.open.group.overrides': '1' }, patchScript: (name, text) => {
            if (name !== 'ui.js') { return text; }
            patched = text.indexOf(needle) >= 0;
            return text.replace(needle, 'return true;');
        } });
        ok(patched, 'the test switched ui.js\'s guard off (its text is where it was)');
        await openEditorOf(bare, 'Anna');
        ok(ovRowsOf(bare).every((r) => !r.remove.disabled), 'with the guard off every Remove is on');
        ovRowsOf(bare).filter((r) => r.name !== 'Good one').forEach((r) => r.remove.click());
        await settle(20);
        eq([deletesOf(bare), bare.server.state.removedAll || 0, ovNamesOf(bare).length, errorBoxOf(bare) !== null], [[], 0, 4, true], 'the page itself refuses a row without an id: no DELETE, the rows stay, the problem is shown');
    }

    // 28. The words follow My languages as the editor holds it (saved or not), and the translations.
    section('28 People: overrides words');
    {
        const e = await boot({ storage: { 'babelfin.admin.open.group.overrides': '1' } });
        e.server.state.docs[USER.anna].Overrides.push({ ItemId: OVR.glassHarbor, Scope: 'Series', Name: 'Glass Harbor', Settings: { AudioChain: [{ Kind: 'MyLanguages' }, { Kind: 'FileDefault' }], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full' }, Manual: null, UpdatedAt: '2026-10-04T00:00:00Z' });
        const ed = await openEditorOf(e, 'Anna');
        const glassHarbor = () => ovRowsOf(e).find((r) => r.name === 'Glass Harbor (2021)');
        eq([glassHarbor().library, glassHarbor().audio, glassHarbor().subtitles], ['Shows', 'English', 'English'], 'one language: that language, for audio and for subtitles');
        await addLanguage(ed.querySelector('.bf-row-control'), 'span', 'es');
        eq([glassHarbor().audio, glassHarbor().subtitles], ['Custom', 'English'], 'My languages English and Spanish: "My languages first" is Custom');
        ok(!saveButtonOf(e).disabled, 'an unsaved edit');
        ed.querySelector('.bf-row-control').querySelectorAll('.bfui-remove')[0].click();
        await settle(10);
        eq(glassHarbor().audio, 'Spanish', 'English taken out: Spanish');
        eq(deletesOf(e), [], 'no request for any of it');
        ed.querySelectorAll('button').find((b) => b.textContent === 'Close').click();
        await confirmWords(e, true);
        const again = await openEditorOf(e, 'Anna');
        eq(ovRowsOf(e).find((r) => r.name === 'Glass Harbor (2021)').audio, 'English', 'discarded and reopened: English again');
    }

    // 29. Wording: nothing the admin page shows describes the person with "understand" or "read" ("what the person understands" and
    // the like). The exceptions describe track titles, a file, and stored settings.
    section('29 no "understand" or "read" about the person');
    {
        const RULE = /\b(understand|understands|understanding|understood|read|reads|reading|reader|readers|readable)\b/i;
        const ALLOWED = [
            ['i18n-admin/en.json', 'tracknames.help', 'Babelfin reads track titles to tell what kind of track each one is. For example, a subtitle track titled "English Signs" is signs and songs, and "English SDH" is for the deaf and hard of hearing. Add the words your files use, one per line.'],
            ['i18n-admin/en.json', 'backup.readError', "The file couldn't be read."],
            ['i18n-admin/en.json', 'backup.storageBlocked', "Settings for {name} can't be read. Repair those settings or remove the affected people from the file before importing anything."]
        ];
        const hits = [];
        for (const file of ['i18n/en.json', 'i18n-admin/en.json']) {
            const table = JSON.parse(fs.readFileSync(ASSETS + file, 'utf8'));
            Object.keys(table).forEach((k) => {
                if (typeof table[k] === 'string' && RULE.test(table[k]) && !ALLOWED.some((a) => a[0] === file && a[1] === k && a[2] === table[k])) { hits.push(file + ' ' + k + ': ' + table[k]); }
            });
        }
        eq(hits, [], 'only the approved English strings use "understand" or "read"');
        eq(ALLOWED.filter(([file, key, value]) => JSON.parse(fs.readFileSync(ASSETS + file, 'utf8'))[key] !== value), [], 'the allowed strings remain the approved sentences');
        ['I understand English', 'Languages you understand', 'Languages I read', 'the languages they read', 'Treat audio in an unknown language as understood', 'Reads'].forEach((s) => ok(RULE.test(s), 'the rule catches "' + s + '"'));
        ['Languages for subtitles', 'Already', 'Spread', 'The file couldn\'t be loaded'].forEach((s) => ok(!RULE.test(s), 'the rule leaves "' + s + '" alone'));
        // Everything the page shows with every fold open and a person's editor open (its overrides section and language list too).
        const store = { 'babelfin.admin.open.advanced': '1', 'babelfin.admin.open.tools': '1', 'babelfin.admin.open.backup': '1', 'babelfin.admin.open.group.overrides': '1' };
        ['apps', 'newUsers', 'recognition', 'check', 'activity'].forEach((k) => { store['babelfin.admin.open.group.' + k] = '1'; });
        const e = await boot({ storage: store });
        await settle(60);
        await openEditorOf(e, 'Anna');
        const strings = visibleStrings(e.page.querySelector('.bf-dash'), null).map((x) => x.text)
            .concat(e.page.querySelectorAll('option').map((o) => o.textContent))
            .concat(e.page.querySelectorAll('.bfui-tip').map((x) => x.textContent))
            .concat(e.page.querySelectorAll('[aria-label]').map((x) => x.getAttribute('aria-label')));
        ok(strings.length > 300, 'the scan saw the page (' + strings.length + ' strings)');
        ok(strings.some((s) => s === 'Show and movie overrides (2)') && strings.some((s) => s === 'Defaults for everyone') && strings.some((s) => s === 'My languages'), 'it included the overrides section, Defaults for everyone and My languages');
        eq(strings.filter((s) => RULE.test(s) && !ALLOWED.some((a) => a[2] === s)), [], 'nothing the page shows says "understand" or "read" (but the two allowed sentences)');
        eq(strings.filter((s) => ALLOWED.some((a) => a[2] === s)).length >= 1, true, 'the allowed Track names sentence is on the page, about Babelfin');
        // Dialogs and toasts.
        const said = [];
        const ask = async (click) => {
            click();
            await settle(10);
            const box = e.document.querySelector('.bfui-dialog');
            if (box) { said.push(box.textContent); box.querySelectorAll('button')[0].click(); }
            await settle(10);
        };
        await ask(() => editor(e).querySelector('.bfui-overrides-remove-all').click());
        await ask(() => e.page.querySelector('#bfRestoreDefaults').click());
        await ask(() => e.page.querySelectorAll('#bfBackup button').find((b) => b.textContent === en['reset.button']).click());
        ok(said.length === 3, 'three dialogs were read (' + said.length + ')', said.length);
        eq(said.filter((s) => RULE.test(s)), [], 'no dialog says "understand" or "read"');
        ovRowsOf(e)[0].remove.click();
        await settle(20);
        eq(toastsOf(e).filter((s) => RULE.test(s)), [], 'nor does a toast');
        // The page's hard-coded sentences.
        const literals = [];
        for (const f of ['dashboard.js']) {
            const code = fs.readFileSync(ASSETS + f, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"\\])\/\/[^\n]*/g, '$1');
            for (const m of code.matchAll(/'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"/g)) {
                const text = m[1] !== undefined ? m[1] : m[2];
                if (/\s/.test(text) && RULE.test(text)) { literals.push(f + ': ' + text.slice(0, 80)); }
            }
        }
        eq(literals, [], 'dashboard.js has no sentence in a string literal that says "understand" or "read"');
    }
/* <<< admin */

    section('30 shared component contracts');
    await require('../shared/contracts')({ UI: env.ctx.BabelfinUI, document: env.document, Event, t: Object.assign(key => enUi[key] || key, { language: 'en' }), check: ok, eq });

    section('31 admin genre defaults save and merge');
    {
        const e = await boot({ config: () => {
            const config = initialConfig();
            config.SmartRules.push({ Id: 'anime', Name: 'Anime', Enabled: true, Match: 'All', Conditions: [{ Kind: 'Genre', Values: ['Anime'], Negate: false, IsRegex: false }], Settings: {} });
            return config;
        } }), state = e.server.state;
        const genres = () => e.page.querySelector('#bfLibraries section.bfui-genres');
        const rows = () => genres().querySelector('tbody').children;
        const add = () => genres().querySelectorAll('button').find(b => b.parentNode === genres() && b.textContent === enUi['ui.genres.add']);
        ok(e.page.querySelector('#bfLibraries .bfui-table-wrap').nextElementSibling === genres(), 'admin genre defaults sit directly below the library table');
        eq([state.requests.filter(r => /^GET Genres/.test(r)).length, rows().map(r => r.querySelector('th').textContent)], [0, ['Kids', 'Anime']], 'admin catalogue starts lazy and displays stored genre defaults');
        add().click(); await settle();
        eq(state.requests.filter(r => /^GET Genres/.test(r)), ['GET Genres?UserId=admin'], 'admin defaults catalogue uses the current administrator');
        const box = genres().querySelector('.bfui-genres-picker'), picker = box.querySelector('select');
        eq([picker.value, box.querySelector('button').disabled], ['', true], 'admin genre catalogue selects nothing automatically');
        choose(picker, 'Drama'); box.querySelector('button').click();
        choose(rows()[2].querySelectorAll('select')[0], 'original');
        choose(rows()[2].querySelectorAll('select')[1], 'off');
        // The current merge contract reorders surviving original rows and appends new distinct genres.
        // Section 31b additionally requires Save to honor an explicit Move-up of a newly added row.
        rows()[1].querySelector('[data-action=up]').click();
        eq(state.puts.length, 0, 'admin genre changes make no write before Save');
        ok(!saveBarHidden(e), 'admin genre changes use the existing save bar');
        // A fresh GET at save time must retain concurrent settings, including fields in a visible rule.
        state.config.SmartRules[0].Settings.FutureField = 'concurrent';
        state.config.FutureTopLevel = true;
        const hidden = { Id: 'hidden', Enabled: false, Name: 'Hidden rule', FutureField: 42 };
        state.config.SmartRules.push(hidden);
        const saved = await save(e), visible = saved.SmartRules.filter(e.ctx.BabelfinUI.isGenreRow);
        eq(visible.map(r => r.Name), ['Anime', 'Kids', 'Drama'], 'admin Save preserves explicit survivor priority and appends the new distinct genre');
        eq(visible.find(r => r.Name === 'Drama').Settings, { AudioChain: [{ Kind: 'OriginalLanguage' }, { Kind: 'VideoDefault' }], WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' }, 'admin genre Save persists both columns exactly');
        eq([visible.find(r => r.Name === 'Kids').Settings.FutureField, saved.FutureTopLevel, saved.SmartRules.find(r => r.Id === 'hidden')], ['concurrent', true, hidden], 'admin genre Save preserves fresh fields and unrelated rules');
        eq(dropNulls([saved.Everyday, saved.Libraries]), dropNulls([initialConfig().Everyday, initialConfig().Libraries]), 'genre-only admin Save preserves library defaults (null and omitted fields are equivalent)');
        choose(rows().find(row => row.querySelector('th').textContent === 'Drama').querySelector('select'), 'mine');
        e.page.querySelector('#bfDiscard').click(); await settle();
        eq([state.puts.length, saveBarHidden(e), rows().find(row => row.querySelector('th').textContent === 'Drama').querySelector('select').value], [1, true, 'original'], 'Cancel restores saved admin genre defaults without a write');
        await openFold(e, 'advanced');
        const newUsers = await openGroup(e, 'newUsers');
        eq(newUsers.body.querySelector('section.bfui-genres'), null, 'New users keeps only its existing language control');
        ok(!state.requests.some(r => /^(POST|PUT|DELETE) Genres/.test(r)), 'genre catalogue is read-only');
    }

    section('31b newly added genre priority survives Save');
    {
        const e = await boot({});
        const genres = () => e.page.querySelector('#bfLibraries section.bfui-genres');
        const rows = () => genres().querySelector('tbody').children;
        eq(rows().map(r => r.querySelector('th').textContent), ['Kids'], 'new-row priority fixture begins with existing Kids');
        genres().querySelectorAll('button').find(b => b.parentNode === genres() && b.textContent === enUi['ui.genres.add']).click();
        await settle();
        const pickerBox = genres().querySelector('.bfui-genres-picker');
        choose(pickerBox.querySelector('select'), 'Drama'); pickerBox.querySelector('button').click();
        rows()[1].querySelector('[data-action=up]').click();
        eq(rows().map(r => r.querySelector('th').textContent), ['Drama', 'Kids'], 'Move up explicitly puts the newly added Drama before Kids');
        eq(e.server.state.puts.length, 0, 'new-row priority remains unsaved until Save');
        const saved = await save(e);
        eq(saved.SmartRules.filter(e.ctx.BabelfinUI.isGenreRow).map(r => r.Name), ['Drama', 'Kids'], 'Save must retain the explicitly moved newly added genre priority');
        ok(saveBarHidden(e), 'new-row priority Save completes through the existing save bar');
    }

    section('32 People genres target the edited person');
    {
        const e = await boot({});
        prow(e, 'Anna').querySelector('button').click();
        await waitFor(() => editor(e) && editor(e).querySelector('section.bfui-genres'), 'People genre editor');
        const genres = () => editor(e).querySelector('section.bfui-genres');
        ok(editor(e).querySelector('.bfui-table-wrap').nextElementSibling === genres(), 'People genres sit directly below the person library table');
        eq(e.server.state.requests.filter(r => /^GET Genres/.test(r)), [], 'People catalogue is lazy');
        genres().querySelectorAll('button').find(b => b.parentNode === genres() && b.textContent === enUi['ui.genres.add']).click();
        await settle();
        eq(e.server.state.requests.filter(r => /^GET Genres/.test(r)), ['GET Genres?UserId=' + USER.anna], 'People catalogue uses the edited person rather than the administrator');
        const pickerBox = genres().querySelector('.bfui-genres-picker');
        choose(pickerBox.querySelector('select'), 'Anime'); pickerBox.querySelector('button').click();
        choose(genres().querySelector('tbody select'), 'mine');
        eq(e.server.state.userPuts.length, 0, 'People genre changes wait for the existing editor Save');
        const put = await editorSave(e);
        eq([put.id, put.query], [USER.anna, { keepOverrides: 'true', fields: 'smartRules' }], 'People genre Save targets the edited person and keeps overrides');
        eq(put.body.SmartRules[0].Settings.AudioChain, [{ Kind: 'MyLanguages' }, { Kind: 'VideoDefault' }], 'People genre semantic audio choice persists exactly');
        const before = personDocs()[USER.anna];
        eq(dropNulls([put.body.Everyday, put.body.Libraries, put.body.Overrides, put.body.Locks]), dropNulls([before.Everyday, before.Libraries, before.Overrides, before.Locks]), 'People genre Save preserves unrelated person settings (null and omitted fields are equivalent)');
        eq(genres().querySelector('tbody select').value, 'mine', 'People genre choice reloads after Save');
        for (const mode of ['same', 'each']) {
            modeOf(editor(e))[mode].click();
            for (const value of ['mine', 'original']) {
                choose(esel(e, mode === 'same' ? 'All libraries' : 'Movies', 'audio'), value);
                const saved = await editorSave(e);
                const layer = mode === 'same' ? saved.body.Everyday : saved.body.Libraries.find(l => l.LibraryId === LIB.movies).Settings;
                eq(storedIn(layer, 'audio'), wanted('audio', value), 'People library ' + mode + ' ' + value + ' saves the exact chain');
                eq(esel(e, mode === 'same' ? 'All libraries' : 'Movies', 'audio').value, value, 'People library semantic choice survives Save/reopen');
            }
        }
    }

    section('33 optional admin choice-status endpoint');
    {
        let resolve;
        const pending = new Promise(done => { resolve = done; });
        const e = await boot({ choiceStatus: () => pending });
        const status = () => e.page.querySelector('#bfChoiceStatus');
        ok(!!e.page.querySelector('#bfLibraries .bfui-table-wrap') && status().hidden, 'pending optional admin status leaves the page usable and silent');
        eq(e.server.state.requests.filter(r => /ChoiceStatus/.test(r)), ['GET Babelfin/Admin/ChoiceStatus'], 'admin uses the exact optional status endpoint');
        resolve({ HasChoices: false }); await settle();
        eq([status().hidden, status().textContent], [false, en['page.noChoices']], 'saved HasChoices false shows the localized admin status');
        choose(libSelect(e, 'Movies', 'audio'), 'mine');
        ok(!status().hidden, 'unsaved admin choices do not change saved-choice status');
        e.server.state.choiceStatus = { HasChoices: true }; await save(e);
        ok(status().hidden, 'admin Save refreshes status from the server');
        eq(e.page.querySelectorAll('#bfChoiceStatus').length, 1, 'admin rerender replaces its previous status node');
        for (const answer of [{}, null, { HasChoices: 'false' }]) {
            const unknown = await boot({ choiceStatus: answer });
            ok(unknown.page.querySelector('#bfChoiceStatus').hidden && !!unknown.page.querySelector('#bfLibraries table'), 'unknown optional admin status stays silent and nonblocking');
        }
        for (const code of [401, 403, 404, 500]) {
            const bad = await boot({ fail: { 'GET Babelfin/Admin/ChoiceStatus': { status: code, sticky: true } } });
            ok(!!bad.page.querySelector('#bfLibraries table') && bad.page.querySelector('#bfChoiceStatus').hidden, code + ' optional admin status leaves the page usable');
            ok(!visibleStrings(bad.page).some(s => s.text.includes('RAW STATUS ERROR')), code + ' admin status exposes no raw error');
        }
    }

    section('17 no Coverage');
    eq(ALL_REQUESTS.filter((r) => /coverage/i.test(r)), [], 'no request to Admin/Coverage, ever (' + ALL_REQUESTS.length + ' requests checked)');
    const js = fs.readFileSync(ASSETS + 'dashboard.js', 'utf8');
    ok(!/Coverage/i.test(js), 'dashboard.js has no Coverage code');

    console.log('\nby section:');
    sections.forEach((s, i) => {
        const end = i + 1 < sections.length ? sections[i + 1].start : passes + failures;
        const failEnd = i + 1 < sections.length ? sections[i + 1].failStart : failures;
        console.log('  ' + s.name + ': ' + (end - s.start) + ' checks' + (failEnd - s.failStart ? ', ' + (failEnd - s.failStart) + ' failed' : ''));
    });
    console.log('\n' + passes + ' passed, ' + failures + ' failed');
    process.exit(failures ? 1 : 0);
}

main().catch((e) => {
    console.log('CRASH: ' + (e && e.stack));
    process.exit(2);
});
