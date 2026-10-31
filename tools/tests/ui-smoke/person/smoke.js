'use strict';
/* Maintained person UI smoke suite. Run ../run.py for both null modes and validators.
   Library choices have independent storage assertions and single-choice parity checks. */
const vm = require('vm');
const fs = require('fs');
const path = require('path');
const { Document, Event } = require('./fakedom');

const { getAssetsPath, verifySemanticTarget } = require('../shared/target');
const ASSETS = getAssetsPath();
verifySemanticTarget(ASSETS, Document);
const REAL_LANGUAGES = require('../shared/languages');
const ANIME = '11111111-1111-1111-1111-111111111111';
const MOVIES = '22222222-2222-2222-2222-222222222222';
const N = (id) => id.replace(/-/g, '');
const clone = (v) => JSON.parse(JSON.stringify(v));

let failures = 0;
let passes = 0;
const sections = [];
function check(cond, label, extra) {
    if (cond) {
        passes++;
    } else {
        failures++;
        console.log('FAIL: ' + label + (extra !== undefined ? '  →  ' + JSON.stringify(extra) : ''));
    }
}
function eq(actual, expected, label) {
    check(JSON.stringify(actual) === JSON.stringify(expected), label, { actual, expected });
}
function section(name) {
    sections.push({ name, start: passes + failures, failStart: failures });
}

const LANGUAGES = [
    { Code: 'en', Name: 'English', Base: 'en' },
    { Code: 'en-US', Name: 'English (United States)', Base: 'en', Region: 'US' },
    { Code: 'ja', Name: 'Japanese', Base: 'ja' },
    { Code: 'es', Name: 'Spanish', Base: 'es' },
    { Code: 'fr', Name: 'French', Base: 'fr' },
    { Code: 'de', Name: 'German', Base: 'de' },
    { Code: 'pt-BR', Name: 'Portuguese (Brazil)', Base: 'pt', Region: 'BR' },
    { Code: 'yue', Name: 'Chinese (Cantonese)', Base: 'zh', Variety: 'yue' },
    { Code: 'ace', Name: 'Achinese', Base: 'ace' }
];
const EXTRAS = ['pt-BR', 'pt-PT', 'zh-Hans', 'zh-Hant', 'es-419', 'fr-CA', 'yue', 'fil'];

// The server's built-in defaults leave audio and subtitles to the video.
const PROFILE = {
    Understood: ['en'], Reads: ['en'], UsesAudioDescription: false, PrefersSdh: false,
    AudioChain: [{ Kind: 'VideoDefault' }],
    AvoidCommentary: true, TieBreaks: ['DirectPlayable', 'MoreChannels', 'Lossless', 'IsOriginalFlag', 'DefaultFlag', 'LowestIndex'],
    UnlabelledAudio: 'OriginalWhenOnlyTrack', UnlabelledAudioLanguage: null, OriginalSource: 'MetadataThenFlag',
    WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault', UnknownAudioCountsAsUnderstood: false,
    SubtitleLanguages: [], Sdh: 'Neutral', Format: 'PreferText', Embedded: 'Neutral', IfNothingMatches: 'ShowNothing'
};

function row(chain, extra) {
    return Object.assign({
        AudioChain: chain, WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: [],
        Understood: ['en'], Reads: ['en'], AudioFromLibrary: false, SubtitlesFromLibrary: false
    }, extra || {});
}
const VIDEO = [{ Kind: 'VideoDefault' }];
const MINE = [{ Kind: 'MyLanguages' }, { Kind: 'OriginalLanguage' }, { Kind: 'FileDefault' }];
const ORIGINAL = [{ Kind: 'OriginalLanguage' }, { Kind: 'LibraryUsualOriginalLanguage' }, { Kind: 'FileDefault' }];
const videoRow = (extra) => row(VIDEO, Object.assign({ WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' }, extra || {}));
const NEUTRAL = () => ({ AllLibraries: videoRow(), Libraries: { [N(ANIME)]: videoRow(), [N(MOVIES)]: videoRow() } });
// An admin with explicit defaults: English first everywhere, the original language in Anime, subtitles when needed.
const OPINIONS = (o) => ({
    AllLibraries: row(MINE),
    Libraries: { [N(ANIME)]: row(ORIGINAL, o && o.animeFromLibrary ? { AudioFromLibrary: true } : {}), [N(MOVIES)]: row(MINE, o && o.moviesSubtitlesFromLibrary ? { SubtitlesFromLibrary: true, WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' } : {}) }
});

function sample(over) {
    const o = over || {};
    return {
        Settings: Object.assign({
            SchemaVersion: 1, UserId: 'u1', UserName: 'pat', Enabled: true, UseEverydayEverywhere: false, HandleNextEpisode: false,
            JellyfinChanges: null,
            Seed: { SeededAt: '2026-10-01T00:00:00Z', Assumed: [{ Field: 'Understood', Language: 'en', From: 'server' }], Confirmed: false, FromTemplate: false },
            Everyday: { Understood: ['en'], Reads: ['en'] },
            Libraries: [],
            SmartRules: [{ Id: 'r1', Name: 'Kids', Enabled: true, Match: 'All', Conditions: [{ Kind: 'Genre', Values: ['Kids'], Negate: false, IsRegex: false }], Settings: { WhenAudioUnderstood: 'Off' } }],
            Overrides: [
                { ItemId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', Scope: 'Series', Name: 'Sable Voyage', Settings: { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'FileDefault' }], SubtitleLanguages: ['en'], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full' }, Manual: null, UpdatedAt: '2026-10-02T00:00:00Z' },
                { ItemId: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', Scope: 'Title', Name: 'Étoile Lantern', Settings: {}, Manual: { Audio: { Language: 'fr', Kind: 'normal' }, SubtitlesOff: true, At: '2026-10-03T00:00:00Z', Client: 'Jellyfin Web' }, UpdatedAt: '2026-10-03T00:00:00Z' }
            ],
            CollectionOrder: [],
            BurnedIn: [{ ItemId: 'cccccccccccccccccccccccccccccccc', Scope: 'Title', Name: 'Old Film', Languages: ['de'], SetAt: '2026-10-01T00:00:00Z', SetBy: 'u1' }],
            Locks: {},
            UpdatedAt: '2026-10-03T00:00:00Z'
        }, o.settings || {}),
        Inherited: { Profile: o.profile || PROFILE, Sources: {}, PersonEnabled: true, Assumed: [], LibraryIsOriginalAudio: false, LibraryUsualOriginalLanguage: null, BurnedInLanguages: [], Manual: null },
        Libraries: [
            { Id: N(ANIME), Name: 'Anime', CollectionType: 'tvshows', IsOriginalAudio: false, UsualOriginalLanguage: 'ja' },
            { Id: N(MOVIES), Name: 'Movies', CollectionType: 'movies', IsOriginalAudio: false, UsualOriginalLanguage: null }
        ],
        Assumed: o.assumed !== undefined ? o.assumed : [{ Field: 'Understood', Language: 'en', From: 'server' }],
        Defaults: o.defaults !== undefined ? o.defaults : NEUTRAL(),
        // What every setting is without the person's own everyday fields (their languages kept). null: an older server that sends none.
        AdvancedDefaults: o.advanced === null ? undefined : { Profile: o.advanced || PROFILE, Sources: {}, PersonEnabled: true, Assumed: [], LibraryIsOriginalAudio: false, LibraryUsualOriginalLanguage: null, BurnedInLanguages: [], Manual: null }
    };
}
// An admin who changed every Advanced option's server default (and an unlabelled audio rule with a specific language).
const ADMIN_PROFILE = Object.assign({}, PROFILE, {
    IfNothingMatches: 'FullInAnyBackupLanguage', Format: 'TextOnly', Embedded: 'PreferExternal', Sdh: 'Avoid', PrefersSdh: true,
    UnlabelledAudio: 'SpecificLanguage', UnlabelledAudioLanguage: 'ja', UsesAudioDescription: true, AvoidCommentary: false, UnknownAudioCountsAsUnderstood: true
});
// The Advanced dropdowns by field, in page order: their label.
const ADVANCED_FIELDS = {
    IfNothingMatches: 'When there are no full subtitles in your languages',
    Format: 'Text or picture subtitles',
    Embedded: 'Embedded or separate subtitle files',
    PrefersSdh: 'Always show subtitles',
    Sdh: 'Subtitles for the deaf and hard of hearing (SDH)',
    UnknownAudioCountsAsUnderstood: "When the audio's language is unknown",
    UsesAudioDescription: 'Audio description tracks',
    AvoidCommentary: 'Commentary tracks',
    Memory: 'Remember track changes for'
};
const ADVANCED_GROUPS = { subtitles: ['IfNothingMatches', 'Format', 'Embedded', 'PrefersSdh', 'Sdh', 'UnknownAudioCountsAsUnderstood'], audio: ['UsesAudioDescription', 'AvoidCommentary'], trackChanges: ['Memory'] };
// Each dropdown's options in order, [stored value, words]; rare: offered only while stored or the default.
const ADVANCED_OPTIONS = {
    IfNothingMatches: [['ShowNothing', 'Show no subtitles'], ['PartialInMyLanguage', 'Show signs and songs subtitles']],
    Format: [['PreferText', 'Prefer text subtitles'], ['TextOnly', 'Only text subtitles'], ['Neutral', 'No preference']],
    Embedded: [['Neutral', 'No preference'], ['PreferEmbedded', 'Prefer embedded subtitles'], ['PreferExternal', 'Prefer separate files']],
    PrefersSdh: [['false', 'No'], ['true', 'Yes']],
    Sdh: [['Neutral', 'No preference'], ['Prefer', 'Prefer'], ['Avoid', 'Avoid']],
    UnknownAudioCountsAsUnderstood: [['false', 'Show subtitles'], ['true', "Don't show subtitles"]],
    UsesAudioDescription: [['false', 'Skip them'], ['true', 'Prefer them']],
    AvoidCommentary: [['true', 'Skip them'], ['false', "Don't skip them"]],
    Memory: [['RestOfShow', 'The rest of the show'], ['Season', 'This season'], ['TitleOnly', 'This episode or movie'], ['DontRemember', "Don't remember"]]
};
const RARE_OPTION = ['FullInAnyBackupLanguage', 'Show full subtitles in another of your languages'];
// The value each profile above gives as the dropdown shows it.
const shown = (profile, field) => (field === 'Memory' ? 'RestOfShow' : String(profile[field]));
/** What a dropdown shows: the stored value, else the profile's; the SDH menu shows Prefer while "Always show subtitles" is Yes. */
const onPage = (profile, stored, field) => {
    const s = stored || {};
    const always = s.PrefersSdh !== undefined && s.PrefersSdh !== null ? s.PrefersSdh : profile.PrefersSdh;
    if (field === 'Sdh' && always === true) { return 'Prefer'; }
    return s[field] !== undefined && s[field] !== null ? String(s[field]) : shown(profile, field);
};
// The fields "Restore defaults" clears, HandleNextEpisode (back on) aside.
const RESTORED = ['IfNothingMatches', 'Format', 'Embedded', 'Sdh', 'PrefersSdh', 'UnlabelledAudio', 'UnlabelledAudioLanguage', 'UsesAudioDescription', 'AvoidCommentary', 'UnknownAudioCountsAsUnderstood', 'Memory'];
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
const quiet = (settings) => Object.assign({ Overrides: [], BurnedIn: [] }, settings || {});
// Both libraries default to English audio (they agree), subtitles when the audio isn't understood.
const AGREE = () => ({ AllLibraries: row(MINE), Libraries: { [N(ANIME)]: row(MINE), [N(MOVIES)]: row(MINE) } });
// Each library has a table choice, so a null PerLibrary means "Choose for each library".
const ANIME_ENTRY = () => ({ LibraryId: N(ANIME), Name: 'Anime', Settings: { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }] } });

const ITEMS = [
    { Id: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', Name: 'Sable Voyage', Type: 'Series' },
    { Id: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', Name: 'Étoile Lantern', Type: 'Movie', ProductionYear: 2001 },
    { Id: 'cccccccccccccccccccccccccccccccc', Name: 'Old Film', Type: 'Movie' },
    { Id: 'dddddddddddddddddddddddddddddddd', Name: 'Cinder Cub', Type: 'Series' },
    { Id: 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', Name: 'Glass Harbor', Type: 'Series', ProductionYear: 2021 }
];

async function settle() {
    for (let i = 0; i < 60; i++) {
        await new Promise((r) => setImmediate(r));
    }
}

// Test the server both with explicit nulls and with omitted optional members.
const OMIT_NULLS = process.env.BF_OMIT_NULLS === '1';

// Where the fake server puts an item: a show is in Anime, a movie in Movies (summaries of GET Me/Overrides).
const LIBRARY_OF = { Series: ['Anime', ANIME], Season: ['Anime', ANIME], Episode: ['Anime', ANIME], Movie: ['Movies', MOVIES] };
/** An OverrideSummary the way the server builds it from a stored ItemOverride: the item's current name, type, year and library. */
function summarize(stored) {
    const item = ITEMS.find((x) => x.Id === stored.ItemId) || null;
    const type = item ? item.Type : (stored.Scope === 'Series' ? 'Series' : 'Movie');
    const library = item ? (LIBRARY_OF[type] || [null, null]) : [null, null];
    return {
        ItemId: stored.ItemId, Scope: stored.Scope, Name: item ? item.Name : stored.Name, Type: type, ProductionYear: item && item.ProductionYear ? item.ProductionYear : null,
        SeriesName: null, LibraryId: library[1] ? N(library[1]) : null, LibraryName: library[0], Settings: stored.Settings, Manual: stored.Manual, UpdatedAt: stored.UpdatedAt
    };
}

/**
 * The fake server and a page on it (the person page: i18n.js, ui.js, app.js on a fake DOM). response: the answer of GET Me/Settings.
 * Options: lang, storage (localStorage entries), signedOut, brokenStorage, omitNulls, search, frame, languages, onPut(body) → { status, body },
 *   summaries (the answer of GET Me/Overrides: an array, or a function of the env; default: one summary per stored override),
 *   fail ({ 'GET /Babelfin/Me/Overrides': { status, sticky } }: the next such request, or every one when sticky, answers that status),
 *   patchUi(source) → source (ui.js as the page gets it, to switch one of its guards off).
 * Additional inputs: choiceStatus is a saved-status object/null or an async function (env) => answer; genres is a catalogue or async loader.
 * The mutable env.choiceStatus and env.fail controls exercise later refreshes and deferred/failed optional status requests. ping overrides Ping.
 * The env: document, store, requests, current (the server's answer; its Settings.Overrides are the stored overrides), removedAll (how many
 *   times the route that removes every override was hit), fail (the same map, changeable while the page runs), sandbox, UI, doc(), main().
 */
async function makeEnv(response, opts) {
    const o = opts || {};
    const document = new Document();
    const appRoot = document.createElement('div');
    appRoot.id = 'bf-app';
    appRoot.className = 'bf-app';
    document.body.appendChild(appRoot);
    const store = new Map(Object.entries(Object.assign({
        jellyfin_credentials: JSON.stringify({ Servers: [{ AccessToken: 'token', UserId: 'u1', DateLastAccessed: 5, ManualAddress: 'http://server.test' }] })
    }, o.storage || {})));
    if (o.signedOut) {
        store.delete('jellyfin_credentials');
    }
    const localStorage = o.brokenStorage ? {
        getItem: (k) => { if (k === 'jellyfin_credentials') { return store.get(k); } throw new Error('blocked'); },
        setItem: () => { throw new Error('blocked'); },
        removeItem: () => { throw new Error('blocked'); }
    } : {
        getItem: (k) => (store.has(k) ? store.get(k) : null),
        setItem: (k, v) => store.set(k, String(v)),
        removeItem: (k) => store.delete(k)
    };
    const requests = [];
    const env = { document, store, requests, current: clone(response), removedAll: 0, fail: Object.assign({}, o.fail || {}), choiceStatus: o.choiceStatus === undefined ? { HasChoices: true } : o.choiceStatus };
    async function fetch(url, init) {
        const i = init || {};
        const u = new URL(url, 'http://server.test');
        const method = (i.method || 'GET').toUpperCase();
        const body = i.body ? JSON.parse(i.body) : undefined;
        if (method === 'PUT' && u.pathname === '/Babelfin/Me/Settings') {
            // The wire contract carries conflict expectations separately from settings.
            check(!!body.ExpectedValues && !Array.isArray(body.ExpectedValues) && typeof body.ExpectedValues === 'object', 'save carries body expectations');
            eq(Object.keys(body.ExpectedValues || {}).sort(), (u.searchParams.get('fields') || '').split(',').sort(), 'expectations name exactly the edited fields');
            check(!Object.keys(i.headers || {}).some(key => key.toLowerCase() === 'x-babelfin-expected'), 'save avoids the legacy expectation header');
        }
        requests.push({ method, path: u.pathname, query: Object.fromEntries(u.searchParams), body });
        const omit = o.omitNulls !== undefined ? o.omitNulls : OMIT_NULLS;
        const reply = (status, raw) => {
            const data = omit && raw && typeof raw === 'object' && u.pathname.indexOf('/Babelfin/assets/') !== 0 ? dropNulls(raw) : raw;
            return { ok: status >= 200 && status < 300, status, text: async () => (data === undefined ? '' : JSON.stringify(data)), json: async () => clone(data) };
        };
        if (u.pathname.indexOf('/Babelfin/assets/i18n/') === 0) {
            const file = ASSETS + 'i18n/' + decodeURIComponent(path.basename(u.pathname));
            return fs.existsSync(file) ? reply(200, JSON.parse(fs.readFileSync(file, 'utf8'))) : reply(404);
        }
        const failure = env.fail[method + ' ' + u.pathname];
        if (failure) {
            if (!failure.sticky) { delete env.fail[method + ' ' + u.pathname]; }
            return reply(failure.status, { Message: failure.message || 'failed' });
        }
        if (u.pathname === '/Babelfin/Me/ChoiceStatus' && method === 'GET') {
            const control = env.choiceStatus;
            const answer = typeof control === 'function' ? await control(env) : control;
            return reply(200, answer);
        }
        if (u.pathname === '/Genres' && method === 'GET') {
            if (u.searchParams.get('UserId') !== 'u1') { throw new Error('Genres must target the active person'); }
            return reply(200, typeof o.genres === 'function' ? await o.genres(env) : o.genres || { Items: [{ Name: 'Anime' }, { Name: 'Drama' }, { Name: 'Kids' }] });
        }
        if (u.pathname === '/Babelfin/Ping') {
            return reply(200, o.ping || { version: '0.0.0.0', enabled: true, dryRun: false });
        }
        if (u.pathname === '/Babelfin/Languages') {
            return reply(200, o.languages || LANGUAGES);
        }
        if (u.pathname === '/Babelfin/Me/Settings') {
            if (method === 'GET') {
                return reply(200, env.current);
            }
            if (method === 'PUT') {
                if (o.onPut) {
                    const r = o.onPut(body);
                    if (r) {
                        return reply(r.status, r.body);
                    }
                }
                // Field intent applies nullable leaves and atomic arrays to the latest stored copy.
                let next = clone(body);
                delete next.ExpectedValues;
                if (u.searchParams.has('fields')) {
                    next = clone(env.current.Settings);
                    for (const field of u.searchParams.get('fields').split(',')) {
                        if (field.startsWith('libraries.')) {
                            const [, id, , leaf] = field.split('.'), key = leaf[0].toUpperCase() + leaf.slice(1);
                            const from = body.Libraries.find(row => N(row.LibraryId).toLowerCase() === id);
                            let row = next.Libraries.find(row => N(row.LibraryId).toLowerCase() === id);
                            if (!row && !from) continue;
                            if (!row) { row = { LibraryId: id, Name: from.Name, Settings: {} }; next.Libraries.push(row); }
                            const value = from && from.Settings && from.Settings[key];
                            (row.Settings ||= {})[key] = value === undefined ? null : clone(value);
                            continue;
                        }
                        const keys = field.split('.').map(key => key[0].toUpperCase() + key.slice(1));
                        let target = next, source = body;
                        for (const key of keys.slice(0, -1)) { target = target[key] ||= {}; source = source && source[key]; }
                        const key = keys[keys.length - 1], value = source && source[key];
                        target[key] = value === undefined ? null : clone(value);
                    }
                }
                // keepOverrides=true also keeps the stored flags regardless of the body's copy.
                if (u.searchParams.get('keepOverrides') === 'true') {
                    next.Overrides = clone((env.current.Settings && env.current.Settings.Overrides) || []);
                    next.BurnedIn = clone((env.current.Settings && env.current.Settings.BurnedIn) || []);
                }
                env.current = Object.assign(clone(env.current), { Settings: next });
                return reply(200, env.current);
            }
        }
        if (u.pathname === '/Items') {
            const ids = (u.searchParams.get('ids') || '').split(',');
            return reply(200, { Items: ITEMS.filter((x) => ids.indexOf(x.Id) >= 0) });
        }
        //: the person's overrides as summaries; DELETE …/Overrides/{itemId}?scope= removes one (404 when it is gone already),
        // DELETE …/Overrides (and, as ASP.NET routes it, …/Overrides/ with no id) removes every one.
        if (u.pathname === '/Babelfin/Me/Overrides' && method === 'GET') {
            const rows = typeof o.summaries === 'function' ? o.summaries(env) : (o.summaries || ((env.current.Settings && env.current.Settings.Overrides) || []).map(summarize));
            return reply(200, rows);
        }
        const oneOverride = u.pathname.indexOf('/Babelfin/Me/Overrides/') === 0 ? [u.pathname, u.pathname.slice('/Babelfin/Me/Overrides/'.length)] : null;
        if ((u.pathname === '/Babelfin/Me/Overrides' || oneOverride) && method === 'DELETE') {
            const stored = (env.current.Settings && env.current.Settings.Overrides) || [];
            const id = oneOverride ? decodeURIComponent(oneOverride[1]) : '';
            if (!id) { return reply(405, { Message: 'Use the explicit bulk route' }); }
            if (id === 'All') {
                env.removedAll++;
                env.current.Settings.Overrides = [];
                return reply(204);
            }
            const keep = stored.filter((x) => !(x.ItemId === id && x.Scope === u.searchParams.get('scope')));
            if (keep.length === stored.length) { return reply(404, { Message: 'gone' }); }
            env.current.Settings.Overrides = keep;
            return reply(204);
        }
        if (u.pathname.indexOf('/Babelfin/Me/BurnedIn/') === 0 && method === 'DELETE') {
            const id = u.pathname.slice('/Babelfin/Me/BurnedIn/'.length).replace(/-/g, '').toLowerCase();
            env.current.Settings.BurnedIn = env.current.Settings.BurnedIn.filter(flag =>
                !(N(flag.ItemId).toLowerCase() === id && flag.Scope === u.searchParams.get('scope')));
            return reply(204);
        }
        if (u.pathname === '/Babelfin/Me/Reset' && method === 'POST') {
            env.current = Object.assign(clone(env.current), { Settings: Object.assign(clone(env.current.Settings), { Everyday: { Understood: ['en'], Reads: ['en'] }, Libraries: [], SmartRules: [], Overrides: [], BurnedIn: [], PerLibrary: null }) });
            return reply(204);
        }
        return reply(404, { Message: 'not found' });
    }
    const sandbox = {
        document, localStorage, fetch, console, setTimeout, clearTimeout, setInterval, clearInterval, Promise, URL, Intl,
        navigator: { language: o.lang || 'en-US' },
        location: { pathname: '/Babelfin/app', origin: 'http://server.test', href: 'http://server.test/Babelfin/app' + (o.search || ''), search: o.search || '' },
        history: { length: 1, back() {} },
        scrollY: 0,
        scrollTo() {},
        innerWidth: 1280,
        innerHeight: 800,
        crypto: require('crypto').webcrypto,
        AbortController,
        Blob: class { constructor(parts) { this.parts = parts; } },
        listeners: {},
        addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); },
        removeEventListener(type, fn) { const l = this.listeners[type] || []; const i = l.indexOf(fn); if (i >= 0) { l.splice(i, 1); } }
    };
    sandbox.window = sandbox;
    // The top window: this one when the page is opened on its own; another one inside a frame (o.frame), or one that can't be read
    // (o.frame === 'cross-origin', as from a frame on another origin).
    if (o.frame === 'cross-origin') {
        Object.defineProperty(sandbox, 'top', { get() { throw new Error('SecurityError: cross-origin frame'); } });
    } else {
        sandbox.top = o.frame ? { name: 'jellyfin-web' } : sandbox;
    }
    document.defaultView = sandbox;
    vm.createContext(sandbox);
    // The table's settings object is the page's live document (state.doc): capture it to read unsaved edits.
    const captured = { settings: null, renders: 0 };
    for (const f of ['i18n.js', 'ui.js', 'app.js']) {
        const source = fs.readFileSync(ASSETS + f, 'utf8');
        vm.runInContext(f === 'ui.js' && o.patchUi ? o.patchUi(source) : source, sandbox, { filename: f });
        if (f === 'ui.js') {
            const real = sandbox.BabelfinUI;
            sandbox.BabelfinUI = Object.assign({}, real, {
                libraryTable: function (c, opt) { captured.settings = opt.settings; captured.renders++; return real.libraryTable(c, opt); }
            });
        }
    }
    await settle();
    env.sandbox = sandbox;
    env.UI = sandbox.BabelfinUI;
    env.doc = () => captured.settings;
    env.tableRenders = () => captured.renders;
    env.main = () => document.querySelector('main');
    return env;
}

/* >>> helpers */
/** ui.js on its own (a vm on the fake DOM; no page): the current file, or explicit test source text. */
function loadUi(source) {
    const document = new Document();
    const sandbox = {
        document, console, setTimeout, clearTimeout, setInterval, clearInterval, Promise, Intl, navigator: { language: 'en-US' },
        localStorage: { getItem() { return null; }, setItem() {} }, listeners: {}, addEventListener() {}, removeEventListener() {}
    };
    sandbox.window = sandbox;
    document.defaultView = sandbox;
    vm.createContext(sandbox);
    vm.runInContext(source || fs.readFileSync(ASSETS + 'ui.js', 'utf8'), sandbox, { filename: 'ui.js' });
    return { UI: sandbox.BabelfinUI, document, sandbox };
}
/** A translator over a strings table, the way i18n.js builds one: t(key, params), t.has(key), t.language. */
function translator(table, language) {
    const t = (key, params) => {
        let s = Object.prototype.hasOwnProperty.call(table, key) ? table[key] : key;
        Object.keys(params || {}).forEach((k) => { s = s.split('{' + k + '}').join(String(params[k])); });
        return s;
    };
    t.language = language || 'en';
    t.has = (k) => Object.prototype.hasOwnProperty.call(table, k);
    return t;
}
/* <<< helpers */

// ----- DOM helpers -----

function change(select, value) {
    select.value = value;
    check(select.value === value, 'option "' + value + '" exists in ' + (select.getAttribute('aria-label') || select.id), select.options.map((x) => x.value));
    select.dispatchEvent(new Event('change', { bubbles: true }));
}
function firstOption(select) {
    return select.options[0] ? select.options[0].textContent : null;
}
/** The mode control of the table: { group, same, each, mode ('same' | 'each' from aria-checked) }. */
function modeControl(document) {
    const group = document.querySelector('.bfui-table-wrap [role=radiogroup]');
    const radios = group ? group.querySelectorAll('[role=radio]') : [];
    const same = radios.find((r) => r.dataset.mode === 'same') || null;
    const each = radios.find((r) => r.dataset.mode === 'each') || null;
    const mode = same && same.getAttribute('aria-checked') === 'true' ? 'same' : (each && each.getAttribute('aria-checked') === 'true' ? 'each' : null);
    return { group, radios, same, each, mode };
}
function pickMode(document, mode) {
    const control = modeControl(document);
    control[mode].click();
    return modeControl(document);
}
function rowNames(document) {
    return document.querySelectorAll('.bfui-table-wrap .bfui-table-row').map((r) => r.querySelector('th').textContent);
}
/** Dispatches a keydown on an element; returns the event (defaultPrevented tells whether the page handled it). */
function key(el, name) {
    const e = new Event('keydown', { bubbles: true, key: name });
    el.dispatchEvent(e);
    return e;
}
/** An Advanced group by id: { element, button, body, open }. */
function group(document, id) {
    const element = document.getElementById('bf-group-' + id);
    if (!element) { return null; }
    const button = element.querySelector('.bfui-disclosure-button');
    const body = element.querySelector('.bfui-disclosure-body');
    return { element, button, body, open: button.getAttribute('aria-expanded') === 'true' && !body.hidden };
}
const GROUPS = ['reads', 'subtitles', 'audio', 'trackChanges', 'burnedIn', 'reset'];
function openAll(document) {
    document.getElementById('bf-advanced').open = true;
    GROUPS.forEach((id) => { const g = group(document, id); if (g && !g.open && !g.element.hidden) { g.button.click(); } });
}
function cellSelect(document, rowKey, kind) {
    const tr = document.querySelectorAll('.bfui-table-wrap .bfui-table-row').find((r) => r.dataset.row === rowKey);
    return tr ? tr.querySelector('.bfui-table-' + kind + ' select') : null;
}
function tableSelects(document) {
    return document.querySelectorAll('.bfui-table-wrap .bfui-table select');
}
/** The select's options in order: [{ label of the group or null, values }] (consecutive options outside a group form one entry). */
function groupsOf(select) {
    const out = [];
    select.children.forEach((c) => {
        if (c.tagName === 'OPTGROUP') {
            out.push({ label: c.getAttribute('label'), values: c.children.map((x) => x.value) });
        } else if (out.length && out[out.length - 1].label === null) {
            out[out.length - 1].values.push(c.value);
        } else {
            out.push({ label: null, values: [c.value] });
        }
    });
    return out;
}
function selectedOption(select) {
    return select.options[select.selectedIndex] || null;
}
function selectedGroup(select) {
    const o = selectedOption(select);
    return o && o.parentNode.tagName === 'OPTGROUP' ? o.parentNode.getAttribute('label') : null;
}
function optionText(select, value) {
    const o = select.options.find((x) => x.value === value);
    return o ? o.textContent : null;
}
function hasOption(select, value) {
    return select.options.some((o) => o.value === value);
}
function rowByText(document, text) {
    return document.querySelectorAll('.bfui-row').find((r) => r.querySelector('.bfui-row-label').textContent.indexOf(text) >= 0);
}
function hiddenUp(node) {
    for (let n = node; n && n.nodeType === 1; n = n.parentNode) {
        if (n.hidden) { return true; }
        if (n.tagName === 'DETAILS' && !n.open && n.parentNode && !(n.firstElementChild && n.firstElementChild.contains(node))) {
            return true;
        }
    }
    return false;
}
function connected(document, node) {
    return document.documentElement.contains(node);
}
/** A main-view section's own class (bf-languages, bf-table-section, bf-overrides, bf-advanced). */
function sectionName(c) {
    return c.className.split(/\s+/).filter((x) => /^bf-/.test(x) && x !== 'bf-section')[0] || c.className;
}

// ----- Show and movie overrides -----
const ovSection = (d) => d.getElementById('bf-overrides');
const ovButton = (d) => (ovSection(d) ? ovSection(d).querySelector('.bfui-disclosure-button') : null);
const ovTitle = (d) => ovButton(d).querySelector('.bfui-disclosure-title').textContent;
const ovOpen = (d) => ovButton(d).getAttribute('aria-expanded') === 'true' && !ovSection(d).querySelector('.bfui-disclosure-body').hidden;
function openOverrides(d) {
    if (!ovOpen(d)) { ovButton(d).click(); }
    return ovSection(d);
}
/** The table's rows as the person sees them: name (title line), note (second line), library, audio, subtitles, and the Remove button. */
function ovRows(d) {
    return ovSection(d).querySelectorAll('.bfui-overrides-row').map((tr) => ({
        tr,
        item: tr.getAttribute('data-item'),
        scope: tr.getAttribute('data-scope'),
        name: tr.querySelector('.bfui-overrides-title').textContent,
        note: tr.querySelector('.bfui-overrides-note') ? tr.querySelector('.bfui-overrides-note').textContent : null,
        library: tr.querySelector('.bfui-overrides-library').textContent,
        audio: tr.querySelector('.bfui-overrides-audio').textContent,
        subtitles: tr.querySelector('.bfui-overrides-subtitles').textContent,
        remove: tr.querySelector('.bfui-overrides-remove')
    }));
}
const ovNames = (d) => ovRows(d).map((r) => r.name);
const ovRow = (d, text) => ovRows(d).find((r) => r.name.indexOf(text) >= 0);
function deletes(env) {
    return env.requests.filter((r) => r.method === 'DELETE');
}
function listGets(env) {
    return env.requests.filter((r) => r.method === 'GET' && r.path === '/Babelfin/Me/Overrides').length;
}
// Visible strings: text, plus option texts, optgroup labels, aria-labels, placeholders and titles of visible elements.
function visibleStrings(root, includeClosed) {
    const out = [];
    const walk = (node) => {
        node.childNodes.forEach((c) => {
            if (c.nodeType === 3) {
                if (c.data.trim()) { out.push(c.data.trim()); }
                return;
            }
            if (c.nodeType !== 1 || c.hidden || c.tagName === 'STYLE' || c.tagName === 'SCRIPT') { return; }
            ['aria-label', 'placeholder', 'title'].forEach((a) => { if (c.getAttribute(a)) { out.push(c.getAttribute(a)); } });
            if (c.tagName === 'OPTGROUP' && c.getAttribute('label')) { out.push(c.getAttribute('label')); }
            if (c.tagName === 'DETAILS' && !c.open && !includeClosed) {
                const summary = c.firstElementChild;
                if (summary) { walk(summary); }
                return;
            }
            walk(c);
        });
    };
    walk(root);
    return out;
}
function settingsDiff(a, b, prefix, out) {
    const list = out || [];
    const norm = (v) => (v === undefined ? null : v);
    const x = norm(a);
    const y = norm(b);
    if (x && y && typeof x === 'object' && typeof y === 'object' && !Array.isArray(x) && !Array.isArray(y)) {
        // Compare setting changes; the PUT boundary checks this one transport member above.
        new Set(Object.keys(x).concat(Object.keys(y))).forEach((k) => {
            if (!prefix && k === 'ExpectedValues') { return; }
            settingsDiff(x[k], y[k], prefix ? prefix + '.' + k : k, list);
        });
    } else {
        // Null/missing and JSON object key order are semantically equal, including
        // objects inside arrays. Preserve array order and every non-null leaf.
        const ordered = value => Array.isArray(value) ? value.map(ordered)
            : value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(key => [key, ordered(value[key])])) : value;
        if (JSON.stringify(ordered(dropNulls(x))) !== JSON.stringify(ordered(dropNulls(y)))) { list.push(prefix); }
    }
    return list;
}
function lastPut(env) {
    return env.requests.filter((r) => r.method === 'PUT' && r.path === '/Babelfin/Me/Settings').pop();
}
function putCount(env) {
    return env.requests.filter((r) => r.method === 'PUT' && r.path === '/Babelfin/Me/Settings').length;
}
function saveBarHidden(env) {
    const bar = env.document.querySelector('.bf-savebar');
    return !bar || bar.hidden;
}
async function clickSave(env) {
    const button = env.document.querySelector('.bf-savebar .bfui-btn-primary');
    check(!!button && !button.disabled, 'save button available');
    if (button) { button.click(); }
    await settle();
}
/** All table selects by aria-label → value. */
function tableValues(document) {
    const out = {};
    tableSelects(document).forEach((s) => { out[s.getAttribute('aria-label')] = s.value; });
    return out;
}
function layerOf(doc, key) {
    if (key === 'all') { return doc.Everyday || {}; }
    const entry = (doc.Libraries || []).find((l) => N(l.LibraryId) === key);
    return entry ? (entry.Settings || {}) : null;
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
function stored(doc, key, kind) {
    const layer = layerOf(doc, key) || {};
    const fields = kind === 'audio' ? ['AudioChain', 'ForeignTitles'] : ['WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'SubtitleLanguages'];
    const out = {};
    fields.forEach((f) => { out[f] = nz(layer[f]); });
    return out;
}
function displayName(code, locale) {
    try {
        const n = new Intl.DisplayNames([locale], { type: 'language', fallback: 'none' }).of(code);
        if (n) { return n; }
    } catch (e) {
        // fall through
    }
    return null;
}
/** The "All languages" codessks for, without the given "My languages" codes, sorted by name in the locale. */
function expectedAll(list, extraCodes, mine, locale) {
    const codes = [];
    const add = (c) => { if (!codes.some((x) => x.toLowerCase() === c.toLowerCase())) { codes.push(c); } };
    list.filter((l) => l.Code === l.Base && /^[a-z]{2}$/i.test(l.Base)).forEach((l) => add(l.Code));
    EXTRAS.forEach(add);
    (extraCodes || []).forEach(add);
    const server = (c) => (list.find((l) => l.Code.toLowerCase() === c.toLowerCase()) || {}).Name;
    const shown = new Map(codes.map((c) => [c, displayName(c, locale) || server(c) || c]));
    // Same rule as the page: codes the browser names alike get the server's own names (else "(code)").
    const clashes = () => {
        const counts = new Map();
        shown.forEach((n) => counts.set(n.toLowerCase(), (counts.get(n.toLowerCase()) || 0) + 1));
        return counts;
    };
    let counts = clashes();
    shown.forEach((n, c) => {
        const own = server(c);
        if (counts.get(n.toLowerCase()) > 1 && own && own.toLowerCase() !== n.toLowerCase()) { shown.set(c, own); }
    });
    counts = clashes();
    shown.forEach((n, c) => { if (counts.get(n.toLowerCase()) > 1) { shown.set(c, n + ' (' + c + ')'); } });
    const name = (c) => shown.get(c);
    const collator = new Intl.Collator(locale);
    return codes.filter((c) => !mine.some((m) => m.toLowerCase() === c.toLowerCase()))
        .sort((a, b) => collator.compare(name(a), name(b)) || collator.compare(a, b));
}
async function confirmDialog(env, ok) {
    await settle();
    const button = env.document.querySelector(ok ? '.bfui-dialog .bfui-btn-danger, .bfui-dialog .bfui-btn-primary' : '.bfui-dialog .bfui-btn:not(.bfui-btn-danger):not(.bfui-btn-primary)');
    check(!!button, 'confirmation dialog button (' + (ok ? 'OK' : 'Cancel') + ')');
    if (button) { button.click(); }
    await settle();
}

// ----- Tests -----

(async function () {
    const en = JSON.parse(fs.readFileSync(ASSETS + 'i18n/en.json', 'utf8'));
    const VALID = /^(|video|mine|original|off|smart|signs|custom|lang:[A-Za-z0-9-]+)$/;
    const VIDEO_HELP = 'Video default: the track the video file marks as default (or your Jellyfin playback settings, if you set any).';
    const SMART_HELP = "When the audio isn't in my languages: full subtitles when the audio isn't in a language listed under My languages, otherwise only signs and songs.";
    const SMART = "When the audio isn't in my languages";
    const PER_LIBRARY = 'Default (set by the server for each library)';
    const SAME = 'Same for every library';
    const EACH = 'Choose for each library';
    const each = (settings) => quiet(Object.assign({ PerLibrary: true }, settings || {}));

    // 1. Main view structure, Advanced closed, detected line, shows and movies, the two help lines, the mode control.
    section('1 main view');
    {
        const env = await makeEnv(sample());
        const { document } = env;
        eq(document.querySelector('h1').textContent, 'Babelfin Settings', 'header title');
        eq(document.querySelector('.bf-subtitle').textContent, 'Choose the audio and subtitles that play for you in each library.', 'header line');
        eq(document.title, 'Babelfin Settings', 'document title');
        const kids = env.main().children.filter((c) => !c.hidden);
        eq(kids.map(sectionName), ['bf-languages', 'bf-table-section', 'bf-overrides', 'bf-advanced'], 'main view sections: My languages, Audio and subtitles, Show and movie overrides, Advanced');
        check(kids.every((c) => c.classList.contains('bf-section')), 'every section is a bf-section');
        check(!document.querySelector('.bf-titles'), 'the old Shows and movies list is gone');
        const advanced = document.getElementById('bf-advanced');
        check(advanced.tagName === 'DETAILS' && !advanced.open, 'Advanced is collapsed by default');
        eq(document.querySelector('.bf-languages-summary').textContent, 'My languages: English', 'my languages line');
        check(!!document.querySelector('.bf-languages-line button'), 'Change button');
        check(document.querySelector('.bf-languages-editor').hidden, 'language editor starts closed');
        const detected = document.getElementById('bf-detected');
        check(!!detected && !detected.hidden, 'detected line shown when not confirmed and assumed is non-empty');
        check(detected && /Detected from the server's language\. Change them or confirm\./.test(detected.textContent) && detected.querySelector('button').textContent === 'Confirm', 'detected line text and Confirm');
        // The mode control sits at the top of the card, before the table.
        const card = document.querySelector('.bf-table-section');
        const control = modeControl(document);
        check(!!control.group, 'the card has a radiogroup');
        eq(control.group.getAttribute('aria-label'), 'Audio and subtitles', 'the radiogroup is labelled "Audio and subtitles"');
        eq(control.radios.map((r) => [r.tagName, r.getAttribute('role'), r.getAttribute('type'), r.textContent]), [['BUTTON', 'radio', 'button', SAME], ['BUTTON', 'radio', 'button', EACH]], 'two radios: Same for every library, Choose for each library');
        const wrap = card.querySelector('.bfui-table-wrap');
        check(wrap.children[0] === control.group && wrap.children[1].tagName === 'TABLE', 'the mode control comes first, then the table');
        check(card.children[0].tagName === 'H2' && card.children[0].textContent === 'Audio and subtitles', 'the card title stays');
        eq(control.mode, 'same', 'nothing stored and no library choices: Same for every library');
        eq(document.querySelectorAll('.bfui-table-wrap .bfui-table thead th').map((th) => th.textContent), ['Library', 'Audio', 'Subtitles'], 'table columns');
        eq(rowNames(document), ['All libraries'], 'one row, All libraries');
        eq(document.querySelectorAll('.bfui-table-help').map((p) => p.textContent), [VIDEO_HELP, SMART_HELP], 'the two help lines under the table');
        eq([en['ui.table.videoDefaultHelp'], en['ui.table.smartHelp'], en['ui.table.mode'], en['ui.table.mode.same'], en['ui.table.mode.each'], en['ui.table.defaultPerLibrary']],
            [VIDEO_HELP, SMART_HELP, 'Audio and subtitles', SAME, EACH, PER_LIBRARY], 'strings in en.json');
        eq([ovTitle(document), ovOpen(document), ovSection(document).querySelector('.bfui-disclosure-body').hidden], ['Show and movie overrides (2)', false, true], 'the overrides section: titled with its count, closed');
        check(saveBarHidden(env), 'save bar hidden without changes');
        const visible = visibleStrings(env.main(), false).join(' | ');
        check(visible.indexOf('Remember track changes for') < 0 && visible.indexOf('Languages for subtitles') < 0, 'advanced content not visible while closed');
        advanced.open = true;
        eq(env.store.get('babelfin-app-advanced'), '1', 'Advanced open state stored');
        advanced.open = false;
        eq(env.store.get('babelfin-app-advanced'), '0', 'Advanced closed state stored');
    }
    {
        const env = await makeEnv(sample(), { storage: { 'babelfin-app-advanced': '1' } });
        check(env.document.getElementById('bf-advanced').open, 'Advanced reopens from storage');
        const broken = await makeEnv(sample(), { brokenStorage: true });
        check(!!broken.main() && !broken.document.getElementById('bf-advanced').open, 'page works with blocked storage');
        check(GROUPS.every((id) => { const g = group(broken.document, id); return !g || !g.open; }), 'blocked storage: every group closed');
        broken.document.getElementById('bf-advanced').open = true;
        group(broken.document, 'subtitles').button.click();
        check(group(broken.document, 'subtitles').open, 'blocked storage: a group still opens');
    }
    {
        const confirmed = await makeEnv(sample({ settings: { Seed: { Confirmed: true, Assumed: [] }, Overrides: [] }, assumed: [] }));
        check(!confirmed.document.getElementById('bf-detected'), 'no detected line when confirmed');
        eq([ovTitle(confirmed.document), confirmed.main().children.filter((c) => !c.hidden).map(sectionName)], ['Show and movie overrides (0)', ['bf-languages', 'bf-table-section', 'bf-overrides', 'bf-advanced']], 'no overrides: the section still shows, with (0)');
        const noAssumed = await makeEnv(sample({ assumed: [] }));
        check(!noAssumed.document.getElementById('bf-detected'), 'no detected line when Assumed is empty');
        const fromUi = await makeEnv(sample({ assumed: [{ Field: 'Reads', Language: 'en', From: 'ui' }] }));
        check(/display language/.test(fromUi.document.getElementById('bf-detected').textContent), 'detected line names the display language');
    }

    // 2. The mode control by mouse: rows, aria-checked, roving tabindex, the stored field, the save bar.
    section('2 mode control: mouse');
    {
        const env = await makeEnv(sample({ settings: quiet() }));
        const d = env.document;
        let control = modeControl(d);
        eq([control.same.getAttribute('aria-checked'), control.each.getAttribute('aria-checked')], ['true', 'false'], 'Same is checked');
        eq([control.same.getAttribute('tabindex'), control.each.getAttribute('tabindex')], ['0', '-1'], 'only the checked radio is in the tab order');
        const renders = env.tableRenders();
        control = pickMode(d, 'each');
        eq(control.mode, 'each', 'clicking Choose for each library checks it');
        eq([control.same.getAttribute('aria-checked'), control.each.getAttribute('aria-checked')], ['false', 'true'], 'aria-checked follows');
        eq([control.same.getAttribute('tabindex'), control.each.getAttribute('tabindex')], ['-1', '0'], 'tab order follows');
        eq(rowNames(d), ['Anime', 'Movies'], 'one row per library, no All libraries row');
        eq(['all', N(ANIME), N(MOVIES)].map((k) => !!cellSelect(d, k, 'audio')), [false, true, true], 'All libraries row gone');
        eq(env.doc().PerLibrary, true, 'PerLibrary stored as true');
        check(!saveBarHidden(env), 'the page is marked changed');
        eq(env.tableRenders(), renders, 'the same table re-draws its rows (it is not rebuilt by the page)');
        check(d.querySelectorAll('.bfui-table-help').length === 2, 'help lines stay in both modes');
        control = pickMode(d, 'each');
        eq([control.mode, env.doc().PerLibrary, rowNames(d)], ['each', true, ['Anime', 'Movies']], 'clicking the checked radio changes nothing');
        control = pickMode(d, 'same');
        eq([control.mode, env.doc().PerLibrary, rowNames(d)], ['same', false, ['All libraries']], 'back to Same: false is stored, the All libraries row is back');
        check(!saveBarHidden(env), 'false differs from the stored null, so there is something to save');
        await clickSave(env);
        eq(settingsDiff(sample({ settings: quiet() }).Settings, lastPut(env).body, ''), ['PerLibrary'], 'the save stores only PerLibrary');
        eq(lastPut(env).body.PerLibrary, false, 'PerLibrary false saved');
        eq(modeControl(d).mode, 'same', 'still Same after saving');
        // Nothing stored and nothing picked: a table edit saves without PerLibrary.
        const plain = await makeEnv(sample({ settings: quiet() }));
        change(cellSelect(plain.document, 'all', 'audio'), 'lang:ja');
        await clickSave(plain);
        eq(settingsDiff(sample({ settings: quiet() }).Settings, lastPut(plain).body, ''), ['Everyday.AudioChain'], 'without picking a mode, PerLibrary is not written');
        eq(nz(lastPut(plain).body.PerLibrary), null, 'PerLibrary stays null');
    }

    // 3. The mode control by keyboard (the radio group keys): Right/Down and Left/Up (wrapping), Home/End, Space/Enter; Tab and other keys are left to the page.
    section('3 mode control: keyboard');
    {
        const env = await makeEnv(sample({ settings: quiet() }));
        const d = env.document;
        let control = modeControl(d);
        control.same.focus();
        let e = key(control.same, 'ArrowRight');
        control = modeControl(d);
        check(e.defaultPrevented, 'ArrowRight is handled');
        eq([control.mode, d.activeElement === control.each, env.doc().PerLibrary], ['each', true, true], 'ArrowRight picks and focuses Choose for each library');
        eq(rowNames(d), ['Anime', 'Movies'], 'rows follow the keyboard');
        e = key(control.each, 'ArrowRight');
        control = modeControl(d);
        eq([control.mode, d.activeElement === control.same, env.doc().PerLibrary], ['same', true, false], 'ArrowRight on the last one wraps to the first');
        key(control.same, 'ArrowLeft');
        control = modeControl(d);
        eq([control.mode, d.activeElement === control.each], ['each', true], 'ArrowLeft wraps the other way');
        key(control.each, 'ArrowLeft');
        control = modeControl(d);
        eq([control.mode, d.activeElement === control.same], ['same', true], 'ArrowLeft goes back');
        key(control.same, 'End');
        control = modeControl(d);
        eq([control.mode, d.activeElement === control.each], ['each', true], 'End picks the last');
        key(control.each, 'Home');
        control = modeControl(d);
        eq([control.mode, d.activeElement === control.same], ['same', true], 'Home picks the first');
        key(control.same, 'ArrowDown');
        control = modeControl(d);
        eq([control.mode, d.activeElement === control.each], ['each', true], 'ArrowDown picks and focuses the next choice');
        key(control.each, 'ArrowDown');
        control = modeControl(d);
        eq([control.mode, d.activeElement === control.same], ['same', true], 'ArrowDown wraps');
        key(control.same, 'ArrowUp');
        control = modeControl(d);
        eq([control.mode, d.activeElement === control.each], ['each', true], 'ArrowUp wraps the other way');
        key(control.each, 'ArrowUp');
        control = modeControl(d);
        eq([control.mode, d.activeElement === control.same], ['same', true], 'ArrowUp goes back');
        for (const name of ['Tab', 'a', 'Escape', 'PageDown']) {
            e = key(control.same, name);
            check(!e.defaultPrevented, name + ' is left to the page');
            eq(modeControl(d).mode, 'same', name + ' changes nothing');
        }
        // Space and Enter pick the focused radio (a radio that is focused but not checked, as after a mouse-down elsewhere).
        control.each.focus();
        e = key(control.each, ' ');
        check(e.defaultPrevented, 'Space is handled');
        eq(modeControl(d).mode, 'each', 'Space picks the focused radio');
        control = modeControl(d);
        control.same.focus();
        key(control.same, 'Enter');
        eq([modeControl(d).mode, env.doc().PerLibrary], ['same', false], 'Enter picks the focused radio');
        key(modeControl(d).same, 'Enter');
        eq(modeControl(d).mode, 'same', 'Enter on the checked radio keeps it');
        // Right-to-left page: Left and Right swap.
        const rtl = await makeEnv(sample({ settings: quiet() }));
        rtl.document.documentElement.setAttribute('dir', 'rtl');
        const rc = modeControl(rtl.document);
        key(rc.same, 'ArrowLeft');
        eq(modeControl(rtl.document).mode, 'each', 'right-to-left: ArrowLeft moves to the next choice');
        key(modeControl(rtl.document).each, 'ArrowRight');
        eq(modeControl(rtl.document).mode, 'same', 'right-to-left: ArrowRight moves back');
    }

    // 4. Switching modes back and forth keeps the other mode's values; the save stores exactly PerLibrary plus the edited fields.
    section('4 switching keeps both modes');
    {
        const original = sample({ settings: quiet() });
        const env = await makeEnv(clone(original));
        const d = env.document;
        change(cellSelect(d, 'all', 'audio'), 'lang:ja');
        change(cellSelect(d, 'all', 'subtitles'), 'lang:en');
        pickMode(d, 'each');
        eq([cellSelect(d, N(ANIME), 'audio').value, cellSelect(d, N(MOVIES), 'subtitles').value], ['', ''], 'library rows start on Default');
        change(cellSelect(d, N(ANIME), 'subtitles'), 'off');
        change(cellSelect(d, N(MOVIES), 'audio'), 'lang:fr');
        eq(stored(env.doc(), 'all', 'audio'), wanted('audio', 'lang:ja'), 'the All libraries audio stays stored while choosing per library');
        eq(stored(env.doc(), 'all', 'subtitles'), wanted('subtitles', 'lang:en'), 'the All libraries subtitles stay stored');
        pickMode(d, 'same');
        eq([cellSelect(d, 'all', 'audio').value, cellSelect(d, 'all', 'subtitles').value], ['lang:ja', 'lang:en'], 'back to Same: the All libraries row shows its values again');
        eq(stored(env.doc(), N(ANIME), 'subtitles'), wanted('subtitles', 'off'), 'the library choices stay stored');
        eq(stored(env.doc(), N(MOVIES), 'audio'), wanted('audio', 'lang:fr'), 'the Movies choice stays stored');
        pickMode(d, 'each');
        eq([cellSelect(d, N(ANIME), 'subtitles').value, cellSelect(d, N(MOVIES), 'audio').value], ['off', 'lang:fr'], 'per library again: the library rows show their values');
        await clickSave(env);
        const body = lastPut(env).body;
        eq(settingsDiff(original.Settings, body, '').sort(), ['Everyday.AudioChain', 'Everyday.SubtitleLanguages', 'Everyday.WhenAudioNotUnderstood', 'Everyday.WhenAudioUnderstood', 'Libraries', 'PerLibrary'],
            'the save stores PerLibrary and the edited fields, nothing else');
        eq(body.PerLibrary, true, 'the last mode picked is saved');
        eq(body.Libraries, [
            { LibraryId: N(ANIME), Name: 'Anime', Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' } },
            { LibraryId: N(MOVIES), Name: 'Movies', Settings: { AudioChain: [{ Kind: 'Language', Language: 'fr' }, { Kind: 'VideoDefault' }] } }
        ], 'both library entries saved');
        eq(stored(body, 'all', 'audio'), wanted('audio', 'lang:ja'), 'the All libraries values are saved too (stored, not in effect)');
        // A full reload shows the saved mode and both sets of values.
        const reload = await makeEnv(clone(env.current));
        eq([modeControl(reload.document).mode, rowNames(reload.document)], ['each', ['Anime', 'Movies']], 'after a reload: Choose for each library');
        pickMode(reload.document, 'same');
        eq(cellSelect(reload.document, 'all', 'audio').value, 'lang:ja', 'after a reload, the All libraries values are still there');
        // Picking Same and saving: the library entries are kept.
        await clickSave(reload);
        const body2 = lastPut(reload).body;
        eq(settingsDiff(body, body2, ''), ['PerLibrary'], 'switching to Same and saving changes only PerLibrary');
        eq(body2.Libraries.length, 2, 'the library entries are kept');
        // Edit in Same mode after a switch: exactly PerLibrary and the edited fields.
        const env3 = await makeEnv(sample({ settings: quiet({ PerLibrary: true, Libraries: [ANIME_ENTRY()] }) }));
        pickMode(env3.document, 'same');
        change(cellSelect(env3.document, 'all', 'subtitles'), 'off');
        await clickSave(env3);
        eq(settingsDiff(sample({ settings: quiet({ PerLibrary: true, Libraries: [ANIME_ENTRY()] }) }).Settings, lastPut(env3).body, '').sort(),
            ['Everyday.WhenAudioNotUnderstood', 'Everyday.WhenAudioUnderstood', 'PerLibrary'], 'Same + one edit: PerLibrary and the two subtitle fields');
        eq(lastPut(env3).body.Libraries, [ANIME_ENTRY()], 'the Anime entry is untouched');
    }

    // 5. Automatic mode when PerLibrary is null, both ways, and what a stored value overrides.
    section('5 automatic mode');
    {
        const cases = [
            ['null, no library entries', quiet(), 'same'],
            ['null, a library entry with a table field', quiet({ Libraries: [ANIME_ENTRY()] }), 'each'],
            ['null, subtitles only in a library', quiet({ Libraries: [{ LibraryId: N(MOVIES), Name: 'Movies', Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' } }] }), 'each'],
            ['null, a library entry with other settings only', quiet({ Libraries: [{ LibraryId: N(ANIME), Name: 'Anime', Settings: { Memory: 'Season', UnlabelledAudio: 'Unknown' } }] }), 'same'],
            ['null, an empty library entry', quiet({ Libraries: [{ LibraryId: N(ANIME), Name: 'Anime', Settings: {} }] }), 'same'],
            ['null, Everyday set and no library choices', quiet({ Everyday: { Understood: ['en'], Reads: ['en'], AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }] } }), 'same'],
            ['false with library choices', quiet({ PerLibrary: false, Libraries: [ANIME_ENTRY()] }), 'same'],
            ['true without library choices', quiet({ PerLibrary: true }), 'each']
        ];
        for (const [label, settings, mode] of cases) {
            const env = await makeEnv(sample({ settings }));
            eq(modeControl(env.document).mode, mode, label + ' → ' + mode);
            eq(rowNames(env.document), mode === 'each' ? ['Anime', 'Movies'] : ['All libraries'], label + ': rows');
            check(saveBarHidden(env), label + ': showing the mode changes nothing');
            eq(env.doc().PerLibrary === undefined ? null : env.doc().PerLibrary, settings.PerLibrary === undefined ? null : settings.PerLibrary, label + ': the stored value is untouched');
        }
        eq(cellSelect((await makeEnv(sample({ settings: quiet({ Libraries: [ANIME_ENTRY()] }) }))).document, N(ANIME), 'audio').value, 'lang:ja', 'automatic per library shows the library choice');
        // The shared rule.
        const UI = (await makeEnv(sample())).UI;
        eq([UI.automaticPerLibrary({ Libraries: [ANIME_ENTRY()] }), UI.automaticPerLibrary({ Libraries: [] }), UI.automaticPerLibrary({}), UI.automaticPerLibrary({ Libraries: [{ Settings: { ForeignTitles: 'DubInMyLanguage' } }] }), UI.automaticPerLibrary({ Libraries: [{ Settings: { SubtitleLanguages: [] } }] })],
            [true, false, false, true, true], 'automaticPerLibrary: any of the five table fields in a library entry');
        // Automatic per library: emptying the last library choice keeps the mode (PerLibrary true is stored), so the rows never flip.
        const env = await makeEnv(sample({ settings: quiet({ Libraries: [ANIME_ENTRY()] }) }));
        const d = env.document;
        change(cellSelect(d, N(ANIME), 'audio'), '');
        eq([modeControl(d).mode, rowNames(d)], ['each', ['Anime', 'Movies']], 'the rows stay after the last library choice goes back to Default');
        eq(env.doc().PerLibrary, true, 'the mode shown is stored, so the next load shows it too');
        await clickSave(env);
        eq(settingsDiff(sample({ settings: quiet({ Libraries: [ANIME_ENTRY()] }) }).Settings, lastPut(env).body, '').sort(), ['Libraries', 'PerLibrary'], 'saved: the entry and PerLibrary');
        const reload = await makeEnv(clone(env.current));
        eq(modeControl(reload.document).mode, 'each', 'after a reload: still per library');
        // Automatic Same: an All libraries edit never stores a mode.
        const same = await makeEnv(sample({ settings: quiet() }));
        change(cellSelect(same.document, 'all', 'audio'), 'lang:ja');
        change(cellSelect(same.document, 'all', 'audio'), '');
        eq(nz(same.doc().PerLibrary), null, 'automatic Same: editing All libraries stores no mode');
        check(saveBarHidden(same), 'and nothing is left to save');
    }

    // 6. Default labels: library rows read their own Defaults row; All libraries reads what every library agrees on, else the per-library line.
    section('6 Default labels');
    {
        // Differ (audio: Original language in Anime, English in Movies) and agree (subtitles: smart everywhere).
        const env = await makeEnv(sample({ defaults: OPINIONS(), settings: quiet() }));
        const d = env.document;
        eq(firstOption(cellSelect(d, 'all', 'audio')), PER_LIBRARY, 'audio differs between libraries: "Default (set by the server for each library)"');
        check(hasOption(cellSelect(d, 'all', 'audio'), 'video'), 'and Video default is offered');
        eq(firstOption(cellSelect(d, 'all', 'subtitles')), 'Default (' + SMART + ')', 'subtitles agree: Default (When the audio isn\'t in my languages)');
        // No live composition: the All libraries choice changes nothing else, and library rows read their own rows.
        change(cellSelect(d, 'all', 'audio'), 'lang:ja');
        eq(firstOption(cellSelect(d, 'all', 'audio')), PER_LIBRARY, 'the label stays after a choice');
        pickMode(d, 'each');
        eq([firstOption(cellSelect(d, N(ANIME), 'audio')), firstOption(cellSelect(d, N(MOVIES), 'audio'))], ['Default (Original language)', 'Default (English)'], 'library rows: straight from their Defaults rows, not from the unsaved All libraries choice');
        eq([firstOption(cellSelect(d, N(ANIME), 'subtitles')), firstOption(cellSelect(d, N(MOVIES), 'subtitles'))], ['Default (' + SMART + ')', 'Default (' + SMART + ')'], 'library subtitles from their rows');
        check(hasOption(cellSelect(d, N(ANIME), 'audio'), 'video') && hasOption(cellSelect(d, N(MOVIES), 'subtitles'), 'video'), 'library rows offer Video default when their default is something else');
        change(cellSelect(d, N(MOVIES), 'audio'), 'lang:de');
        eq(firstOption(cellSelect(d, N(ANIME), 'audio')), 'Default (Original language)', 'another library\'s choice changes nothing');
        // Agree.
        const agree = await makeEnv(sample({ defaults: AGREE(), settings: quiet() }));
        eq([firstOption(cellSelect(agree.document, 'all', 'audio')), firstOption(cellSelect(agree.document, 'all', 'subtitles'))], ['Default (English)', 'Default (' + SMART + ')'], 'every library agrees: Default (English)');
        // Neutral everywhere: plain "Video default", no explicit option.
        const neutral = await makeEnv(sample({ settings: quiet() }));
        eq([firstOption(cellSelect(neutral.document, 'all', 'audio')), hasOption(cellSelect(neutral.document, 'all', 'audio'), 'video')], ['Video default', false], 'every library on the video default: "Video default"');
        // Video default in one library only: differ.
        const mixed = NEUTRAL();
        mixed.Libraries[N(MOVIES)] = row([{ Kind: 'Language', Language: 'en' }, { Kind: 'VideoDefault' }], { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' });
        const mixedEnv = await makeEnv(sample({ defaults: mixed, settings: quiet() }));
        eq([firstOption(cellSelect(mixedEnv.document, 'all', 'audio')), firstOption(cellSelect(mixedEnv.document, 'all', 'subtitles'))], [PER_LIBRARY, PER_LIBRARY], 'video default in one library, English in another: differ');
        pickMode(mixedEnv.document, 'each');
        eq([N(ANIME), N(MOVIES)].map((k) => [firstOption(cellSelect(mixedEnv.document, k, 'audio')), firstOption(cellSelect(mixedEnv.document, k, 'subtitles'))]), [['Video default', 'Video default'], ['Default (English)', 'Default (Off)']], 'each library row reads its own default');
        // A library missing from Defaults.Libraries reads Defaults.AllLibraries; no libraries at all: Defaults.AllLibraries.
        const partial = { AllLibraries: row(MINE), Libraries: { [N(ANIME)]: row(MINE) } };
        const partialEnv = await makeEnv(sample({ defaults: partial, settings: quiet() }));
        eq(firstOption(cellSelect(partialEnv.document, 'all', 'audio')), 'Default (English)', 'a library without its own row counts as the All libraries row');
        const none = sample({ defaults: AGREE(), settings: quiet() });
        none.Libraries = [];
        const noneEnv = await makeEnv(none);
        eq(firstOption(cellSelect(noneEnv.document, 'all', 'audio')), 'Default (English)', 'no libraries: Defaults.AllLibraries');
        pickMode(noneEnv.document, 'each');
        eq(rowNames(noneEnv.document), [], 'no libraries, per library: no rows');
        const empty = noneEnv.document.querySelector('.bfui-table-none');
        check(!!empty && !empty.hidden && empty.textContent === 'No libraries.', 'no libraries, per library: "No libraries."');
        pickMode(noneEnv.document, 'same');
        check(noneEnv.document.querySelector('.bfui-table-none').hidden, 'the line goes with Same');
        // My languages first: named after the live first understood language.
        const live = await makeEnv(sample({ defaults: AGREE(), settings: quiet() }));
        const ld = live.document;
        ld.querySelector('.bf-languages-line button').click();
        const input = ld.querySelector('.bf-languages-editor .bfui-picker input');
        input.value = 'span';
        input.dispatchEvent(new Event('input'));
        ld.querySelector('.bf-languages-editor').querySelectorAll('li').find((li) => li.dataset.code === 'es').click();
        await settle();
        ld.querySelector('.bf-languages-editor').querySelectorAll('.bfui-up')[1].click();
        eq(ld.querySelector('.bf-languages-summary').textContent, 'My languages: Spanish, English', 'summary follows the editor');
        eq(firstOption(cellSelect(ld, 'all', 'audio')), 'Default (Spanish)', 'My languages default uses the live first language');
        eq(groupsOf(cellSelect(ld, 'all', 'audio'))[1].values, ['lang:es', 'lang:en'], 'My languages group follows the list');
        check(ld.getElementById('bf-detected').hidden, 'changing languages hides the detected line');
    }

    // 7. Neutral defaults and the option lists of the settings contract, in both modes.
    section('7 neutral defaults and option lists');
    for (const mode of ['same', 'each']) {
        const env = await makeEnv(sample({ settings: mode === 'each' ? each() : quiet() }));
        const d = env.document;
        const keys = mode === 'each' ? [N(ANIME), N(MOVIES)] : ['all'];
        for (const k of keys) {
            for (const kind of ['audio', 'subtitles']) {
                const s = cellSelect(d, k, kind);
                eq(firstOption(s), 'Video default', mode + ' ' + k + ' ' + kind + ': neutral default reads "Video default"');
                check(!hasOption(s, 'video'), mode + ' ' + k + ' ' + kind + ': no explicit Video default option when the default already is the video default');
                eq(s.value, '', mode + ' ' + k + ' ' + kind + ': nothing stored shows the Default option');
                check(s.options.every((o) => VALID.test(o.value)), mode + ' ' + k + ' ' + kind + ': every option value follows the settings contract', s.options.map((o) => o.value).filter((v) => !VALID.test(v)));
            }
        }
        eq(keys.map((k) => [cellSelect(d, k, 'audio').getAttribute('aria-label'), cellSelect(d, k, 'subtitles').getAttribute('aria-label')]),
            mode === 'each' ? [['Audio for Anime', 'Subtitles for Anime'], ['Audio for Movies', 'Subtitles for Movies']] : [['Audio for All libraries', 'Subtitles for All libraries']], mode + ': aria-labels');
        const allExpected = expectedAll(LANGUAGES, [], ['en'], 'en-US').map((c) => 'lang:' + c);
        eq(groupsOf(cellSelect(d, keys[0], 'audio')), [{ label: null, values: ['', 'mine', 'original'] }, { label: 'My languages', values: ['lang:en'] }, { label: 'All languages', values: allExpected }], mode + ': audio options');
        eq(groupsOf(cellSelect(d, keys[0], 'subtitles')), [{ label: null, values: ['', 'off', 'smart', 'signs'] }, { label: 'My languages', values: ['lang:en'] }, { label: 'All languages', values: allExpected }], mode + ': subtitle options');
        const subs = cellSelect(d, keys[0], 'subtitles');
        eq(subs.options.slice(0, 4).map((o) => o.textContent), ['Video default', 'Off', SMART, 'Signs and songs only'], mode + ': subtitle option labels');
        eq([optionText(subs, 'lang:pt-BR'), optionText(subs, 'lang:yue'), optionText(subs, 'lang:fil')], ['Brazilian Portuguese', 'Cantonese', 'Filipino'], mode + ': pt-BR, yue and fil named');
        check(!hasOption(subs, 'lang:en-US') && !hasOption(subs, 'lang:ace'), mode + ': regional forms and three-letter languages are not listed');
        check(EXTRAS.every((c) => hasOption(subs, 'lang:' + c)), mode + ': the extra forms are listed');
        const texts = d.querySelectorAll('select option').map((o) => o.textContent);
        ['Original language', 'Other language…', 'When needed'].forEach((bad) => check(texts.indexOf(bad) < 0, mode + ': no option labelled "' + bad + '"'));
        check(!d.querySelector('.bfui-table-wrap input') && !d.querySelector('.bfui-table-wrap .bfui-picker'), mode + ': no search box in the table');
        if (mode === 'same') {
            // Every dropdown on the page: table ones read "Video default"; Advanced ones list plain options, the default selected.
            openAll(d);
            const report = env.main().querySelectorAll('select').filter((s) => !s.closest('section.bfui-genres')).map((s) => ({ select: s, label: s.getAttribute('aria-label') || (d.querySelector('label[for=' + s.id + ']') || { textContent: '?' }).textContent, first: firstOption(s), table: !!s.closest('.bfui-table') }));
            report.forEach((r) => {
                if (r.table) {
                    eq(r.first, 'Video default', 'table dropdown ' + r.label + ' starts with Video default');
                } else {
                    check(r.select.options.every((o) => o.value !== '' && !/^Default\b/.test(o.textContent)), 'no Default option in ' + r.label, r.select.options.map((o) => o.textContent));
                    const field = Object.keys(ADVANCED_FIELDS).find((f) => ADVANCED_FIELDS[f] === r.label);
                    eq(r.select.value, field ? shown(PROFILE, field) : '?', r.label + ': the default is selected');
                }
            });
            eq(report.length, 11, 'dropdowns on the page: 2 in the table, 9 in Advanced');
        }
    }

    // 8. The language list rule against the server's real table, sorting in the UI language, stored codes added.
    section('8 language list');
    {
        const settings = quiet({ Everyday: { Understood: ['en', 'ja'], Reads: ['en'], AudioChain: [{ Kind: 'Language', Language: 'tlh' }, { Kind: 'VideoDefault' }] } });
        const env = await makeEnv(sample({ settings }), { languages: REAL_LANGUAGES });
        const audio = cellSelect(env.document, 'all', 'audio');
        const groups = groupsOf(audio);
        eq(groups.map((g) => g.label), [null, 'My languages', 'All languages'], 'groups with the real list');
        eq(groups[1].values, ['lang:en', 'lang:ja'], 'My languages in Understood order');
        const all = groups[2].values.map((v) => v.slice(5));
        const want = expectedAll(REAL_LANGUAGES, ['tlh'], ['en', 'ja'], 'en-US');
        eq(all, want, 'All languages: plain two-letter languages, the extra forms and the stored code, sorted by name');
        const labels = [...audio.querySelectorAll('optgroup option')].map((o) => o.textContent.trim().toLowerCase());
        check(new Set(labels).size === labels.length, 'no two language options read the same (tl/fil, ak/tw)', labels.filter((l, i) => labels.indexOf(l) !== i));
        eq([optionText(audio, 'lang:tl'), optionText(audio, 'lang:fil')], ['Tagalog', 'Filipino'], 'Tagalog and Filipino keep the server\'s names');
        check(all.length >= 180 && all.length <= 200, 'about 185 languages (' + all.length + ')');
        ['en-US', 'en-GB', 'zh-Hant-HK', 'sr-Latn', 'cmn', 'nan', 'es-ES', 'ace', 'cnr'].forEach((c) => check(all.indexOf(c) < 0, c + ' is not listed'));
        eq(audio.value, 'lang:tlh', 'a stored code missing from the list shows and stays selected');
        const names = audio.options.filter((o) => o.parentNode.getAttribute && o.parentNode.getAttribute('label') === 'All languages').map((o) => o.textContent);
        const collator = new Intl.Collator('en-US');
        check(names.every((n, i) => i === 0 || collator.compare(names[i - 1], n) <= 0), 'All languages sorted by display name');
        const subs = cellSelect(env.document, 'all', 'subtitles');
        eq(groupsOf(subs)[1].values, ['lang:en', 'lang:ja'], 'subtitles: My languages = Reads, then Understood');
        check(!groupsOf(subs)[2].values.some((v) => v === 'lang:en' || v === 'lang:ja'), 'subtitles: All languages leaves out My languages');
        // Per library: the same list.
        pickMode(env.document, 'each');
        eq(groupsOf(cellSelect(env.document, N(ANIME), 'audio'))[2].values.map((v) => v.slice(5)), want, 'per library: the same list (the stored code included)');

        // German UI: names and order in German, labels from de.json.
        const de = JSON.parse(fs.readFileSync(ASSETS + 'i18n/de.json', 'utf8'));
        const envDe = await makeEnv(sample({ settings: quiet() }), { languages: REAL_LANGUAGES, lang: 'de' });
        const deAudio = cellSelect(envDe.document, 'all', 'audio');
        eq(firstOption(deAudio), de['ui.table.videoDefault'], 'German: Video default label');
        eq(optionText(deAudio, 'lang:en'), 'Englisch', 'German: English named in German');
        eq(optionText(deAudio, 'lang:de'), 'Deutsch', 'German: German named in German');
        const deAll = groupsOf(deAudio).find((g) => g.label === de['ui.table.allLanguages']);
        check(!!deAll, 'German: All languages group label');
        eq(deAll ? deAll.values.map((v) => v.slice(5)) : null, expectedAll(REAL_LANGUAGES, [], ['en'], 'de'), 'German: sorted by German names');
        eq(groupsOf(deAudio)[1].label, de['ui.table.myLanguages'], 'German: My languages group label');
        eq(modeControl(envDe.document).radios.map((r) => r.textContent), [de['ui.table.mode.same'], de['ui.table.mode.each']], 'German: mode labels');
    }

    // 9. Every option of both dropdowns, on the All libraries row (Same) and on a library row (per library), saved and read back.
    section('9 every option round trip');
    for (const key of ['all', N(ANIME)]) {
        for (const kind of ['audio', 'subtitles']) {
            // Defaults with opinions, so "Video default" is offered as an explicit option too.
            const env = await makeEnv(sample({ defaults: OPINIONS(), settings: key === 'all' ? quiet() : each() }));
            const values = cellSelect(env.document, key, kind).options.map((o) => o.value).filter((v) => v !== '');
            check(values.indexOf('video') === 0, key + ' ' + kind + ': Video default offered when the default is something else', values.slice(0, 3));
            check(values.indexOf('custom') < 0, key + ' ' + kind + ': no Custom without a stored value the table can not show');
            let previous = clone(env.current.Settings);
            let count = 0;
            for (const value of values.concat([''])) {
                const select = cellSelect(env.document, key, kind);
                change(select, value);
                eq(select.value, value, key + ' ' + kind + ' ' + value + ': select shows the choice');
                eq(stored(env.doc(), key, kind), wanted(kind, value), key + ' ' + kind + ' ' + value + ': stored fields before saving');
                await clickSave(env);
                const put = lastPut(env);
                eq(stored(put.body, key, kind), wanted(kind, value), key + ' ' + kind + ' ' + value + ': saved fields');
                const changed = settingsDiff(previous, put.body, '');
                const allowed = key === 'all' ? ['Everyday.'] : ['Libraries'];
                check(changed.length > 0 && changed.every((p) => allowed.some((a) => p.indexOf(a) === 0)), key + ' ' + kind + ' ' + value + ': only this cell changes', changed);
                if (key !== 'all') {
                    const entry = put.body.Libraries.find((l) => l.LibraryId === key);
                    if (value) {
                        check(!!entry && entry.Name === 'Anime', 'library entry stored with its name', entry);
                    } else {
                        check(!entry, 'library entry dropped when its settings are empty');
                    }
                    eq(put.body.PerLibrary, true, 'PerLibrary stays true');
                }
                eq(cellSelect(env.document, key, kind).value, value, key + ' ' + kind + ' ' + value + ': shows again after saving');
                previous = clone(put.body);
                count++;
            }
            const reload = await makeEnv(clone(env.current));
            eq(cellSelect(reload.document, key, kind).value, '', key + ' ' + kind + ': Default after a reload');
            console.log('round trips ' + key + ' ' + kind + ': ' + count);
        }
    }
    {
        // Reload for each kind of value (fresh page from the stored document).
        for (const [key, kind, value] of [['all', 'audio', 'video'], ['all', 'audio', 'mine'], ['all', 'audio', 'original'], [N(ANIME), 'audio', 'mine'], [N(ANIME), 'audio', 'original'], ['all', 'audio', 'lang:ja'], ['all', 'audio', 'lang:en'], [N(ANIME), 'audio', 'lang:pt-BR'],
            ['all', 'subtitles', 'video'], ['all', 'subtitles', 'off'], ['all', 'subtitles', 'smart'], ['all', 'subtitles', 'signs'], [N(MOVIES), 'subtitles', 'lang:fil'], [N(ANIME), 'subtitles', 'lang:en']]) {
            const env = await makeEnv(sample({ defaults: OPINIONS(), settings: key === 'all' ? quiet() : each() }));
            change(cellSelect(env.document, key, kind), value);
            await clickSave(env);
            const reload = await makeEnv(clone(env.current), {});
            eq(cellSelect(reload.document, key, kind).value, value, key + ' ' + kind + ' ' + value + ': shows after a full reload');
            check(saveBarHidden(reload), 'nothing to save after a reload');
        }
    }

    // 10. A language from "All languages", then one from "My languages".
    section('10 All languages then My languages');
    {
        const env = await makeEnv(sample({ settings: quiet() }));
        const d = env.document;
        const audio = cellSelect(d, 'all', 'audio');
        change(audio, 'lang:ja');
        eq([audio.value, selectedGroup(audio), selectedOption(audio).textContent], ['lang:ja', 'All languages', 'Japanese'], 'audio: Japanese picked from All languages');
        change(audio, 'lang:en');
        eq([audio.value, selectedGroup(audio), selectedOption(audio).textContent], ['lang:en', 'My languages', 'English'], 'audio: English picked from My languages');
        eq(stored(env.doc(), 'all', 'audio'), wanted('audio', 'lang:en'), 'audio: English stored');
        pickMode(d, 'each');
        const subs = cellSelect(d, N(MOVIES), 'subtitles');
        change(subs, 'lang:fr');
        eq([subs.value, selectedGroup(subs)], ['lang:fr', 'All languages'], 'subtitles: French from All languages');
        change(subs, 'lang:en');
        eq([subs.value, selectedGroup(subs)], ['lang:en', 'My languages'], 'subtitles: English from My languages');
        await clickSave(env);
        const body = lastPut(env).body;
        eq(stored(body, 'all', 'audio'), wanted('audio', 'lang:en'), 'saved: All libraries audio English');
        eq(stored(body, N(MOVIES), 'subtitles'), wanted('subtitles', 'lang:en'), 'saved: Movies subtitles English');
        eq(cellSelect(d, N(MOVIES), 'subtitles').value, 'lang:en', 'Movies subtitles still English after saving');
    }

    // 11. The same dropdown changed several times in a row: no snap-back, nothing re-renders, other dropdowns keep their values.
    section('11 repeated changes');
    for (const [key, kind, seq, defaults] of [
        ['all', 'audio', ['lang:ja', 'lang:fr', 'lang:ja', ''], NEUTRAL()],
        ['all', 'subtitles', ['off', 'lang:de', 'off', ''], NEUTRAL()],
        [N(ANIME), 'audio', ['lang:es', 'lang:en', 'lang:es', ''], NEUTRAL()],
        [N(MOVIES), 'subtitles', ['smart', 'signs', 'smart', ''], NEUTRAL()],
        ['all', 'audio', ['video', 'lang:zh-Hant', 'video', ''], OPINIONS()],
        [N(ANIME), 'subtitles', ['video', 'lang:yue', 'video', ''], OPINIONS()]
    ]) {
        const settings = key === 'all' ? quiet() : each();
        const env = await makeEnv(sample({ defaults, settings }));
        const d = env.document;
        const select = cellSelect(d, key, kind);
        const label = select.getAttribute('aria-label');
        const before = tableValues(d);
        const renders = env.tableRenders();
        for (const value of seq) {
            change(select, value);
            const tag = label + ' → ' + (value || 'Default');
            check(connected(d, select) && cellSelect(d, key, kind) === select, tag + ': the same dropdown is still on the page');
            eq(select.value, value, tag + ': the dropdown shows the choice');
            eq(stored(env.doc(), key, kind), wanted(kind, value), tag + ': stored fields');
            const now = tableValues(d);
            const others = Object.keys(before).filter((k) => k !== label);
            eq(others.map((k) => now[k]), others.map((k) => before[k]), tag + ': every other dropdown keeps its value');
            await settle();
            eq(select.value, value, tag + ': still showing the choice a moment later');
        }
        eq(env.tableRenders(), renders, label + ': the table was not rebuilt by the changes');
        check(saveBarHidden(env), label + ': back to Default leaves nothing to save');
        eq(settingsDiff(sample({ settings }).Settings, env.doc(), ''), [], label + ': the document is back to what was loaded');
    }

    // 12. Discard puts everything back, the mode included.
    section('12 discard');
    {
        const original = clone(sample({ settings: quiet({ Libraries: [{ LibraryId: N(MOVIES), Name: 'Movies', Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' } }] }) }));
        const env = await makeEnv(clone(original));
        const d = env.document;
        eq(modeControl(d).mode, 'each', 'a library choice: automatic per library');
        const values = tableValues(d);
        change(cellSelect(d, N(ANIME), 'audio'), 'lang:fr');
        change(cellSelect(d, N(MOVIES), 'subtitles'), 'smart');
        pickMode(d, 'same');
        change(cellSelect(d, 'all', 'audio'), 'lang:ja');
        d.getElementById('bf-advanced').open = true;
        change(rowByText(d, 'Remember track changes for').querySelector('select'), 'Season');
        check(!saveBarHidden(env), 'save bar shows after the changes');
        const discardButton = () => d.querySelectorAll('.bf-savebar .bfui-btn').find((b) => b.textContent === 'Discard');
        discardButton().click();
        await confirmDialog(env, false);
        eq([modeControl(d).mode, cellSelect(d, 'all', 'audio').value], ['same', 'lang:ja'], 'Cancel in the discard dialog keeps the changes');
        discardButton().click();
        await confirmDialog(env, true);
        eq(modeControl(d).mode, 'each', 'Discard puts the mode back');
        eq(tableValues(d), values, 'Discard puts every table dropdown back');
        eq([rowByText(d, 'Remember track changes for').querySelector('select').value, nz(env.doc().Everyday.Memory)], ['RestOfShow', null], 'Discard puts Advanced back (the default selected, nothing stored)');
        eq(settingsDiff(original.Settings, env.doc(), ''), [], 'Discard restores the document exactly');
        check(saveBarHidden(env), 'save bar hidden after Discard');
        eq(putCount(env), 0, 'Discard sends nothing');
        change(cellSelect(d, N(ANIME), 'audio'), 'lang:de');
        eq(stored(env.doc(), N(ANIME), 'audio'), wanted('audio', 'lang:de'), 'the table edits the restored document');
    }

    // 13. Stored codes missing from the list still show and stay selected.
    section('13 stored codes missing from the list');
    {
        const settings = quiet({
            Everyday: { Understood: ['en'], Reads: ['en'], AudioChain: [{ Kind: 'Language', Language: 'tlh' }, { Kind: 'VideoDefault' }], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['nan'] },
            Libraries: [
                { LibraryId: N(ANIME), Name: 'Anime', Settings: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['PT-br'] } },
                { LibraryId: N(MOVIES), Name: 'Movies', Settings: { AudioChain: [{ Kind: 'Language', Language: 'qaa' }, { Kind: 'VideoDefault' }] } }
            ]
        });
        const env = await makeEnv(sample({ settings }));
        const d = env.document;
        eq(modeControl(d).mode, 'each', 'library choices: per library');
        eq(cellSelect(d, N(ANIME), 'subtitles').value, 'lang:pt-BR', 'a stored code in other letter case selects the listed language');
        eq(cellSelect(d, N(MOVIES), 'audio').value, 'lang:qaa', 'a code no table knows shows under its code');
        eq(optionText(cellSelect(d, N(MOVIES), 'audio'), 'lang:qaa'), 'qaa', 'it is labelled with the code');
        pickMode(d, 'same');
        eq(cellSelect(d, 'all', 'audio').value, 'lang:tlh', 'audio code missing from the list is selected');
        check(!!optionText(cellSelect(d, 'all', 'audio'), 'lang:tlh'), 'it has a name');
        eq(cellSelect(d, 'all', 'subtitles').value, 'lang:nan', 'subtitle code missing from the list is selected');
        pickMode(d, 'each');
        change(cellSelect(d, N(ANIME), 'audio'), 'lang:ja');
        await clickSave(env);
        const body = lastPut(env).body;
        eq(body.Everyday.AudioChain, settings.Everyday.AudioChain, 'stored audio code untouched');
        eq(body.Everyday.SubtitleLanguages, ['nan'], 'stored subtitle code untouched');
        eq(body.Libraries.find((l) => l.LibraryId === N(ANIME)).Settings.SubtitleLanguages, ['PT-br'], 'stored letter case untouched');
        eq(settingsDiff(sample({ settings }).Settings, body, '').sort(), ['Libraries', 'PerLibrary'], 'the save: the Anime audio and the mode picked');
        const reload = await makeEnv(clone(env.current));
        eq([cellSelect(reload.document, N(ANIME), 'audio').value, cellSelect(reload.document, N(MOVIES), 'audio').value], ['lang:ja', 'lang:qaa'], 'still selected after a reload');
    }

    // 14. Custom: shown for values the table can't show, kept untouched, and still offered after picking something else.
    section('14 custom');
    {
        const customChain = [{ Kind: 'FileDefault' }, { Kind: 'MyLanguages' }];
        const settings = quiet({ Everyday: { Understood: ['en'], Reads: ['en'], AudioChain: customChain, WhenAudioUnderstood: 'Full' } });
        const env = await makeEnv(sample({ settings }));
        const d = env.document;
        eq(cellSelect(d, 'all', 'audio').value, 'custom', 'custom chain shows Custom');
        eq(cellSelect(d, 'all', 'subtitles').value, 'custom', 'custom subtitles show Custom');
        eq(optionText(cellSelect(d, 'all', 'audio'), 'custom'), 'Custom', 'Custom label');
        d.getElementById('bf-advanced').open = true;
        change(rowByText(d, 'Remember track changes for').querySelector('select'), 'Season');
        await clickSave(env);
        eq(lastPut(env).body.Everyday.AudioChain, customChain, 'Custom audio left untouched on save');
        eq(lastPut(env).body.Everyday.WhenAudioUnderstood, 'Full', 'Custom subtitles left untouched on save');
        const audio = cellSelect(d, 'all', 'audio');
        change(audio, 'lang:ja');
        check(hasOption(audio, 'custom'), 'Custom still offered after picking another option');
        change(audio, '');
        check(hasOption(audio, 'custom'), 'Custom still offered after picking Default');
        change(audio, 'custom');
        eq(env.doc().Everyday.AudioChain, customChain, 'Custom puts the stored order back');
        check(saveBarHidden(env), 'nothing to save after going back to Custom');
        // Custom survives switching modes.
        pickMode(d, 'each');
        check(!hasOption(cellSelect(d, N(ANIME), 'audio'), 'custom'), 'no Custom on rows without one');
        pickMode(d, 'same');
        eq(cellSelect(d, 'all', 'audio').value, 'custom', 'Custom shows again after switching back');
        // A library's custom subtitles: kept when the audio of the same row changes.
        const lib = quiet({ Libraries: [{ LibraryId: N(ANIME), Name: 'Anime', Settings: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Off', Memory: 'Season' } }] });
        const env2 = await makeEnv(sample({ settings: lib }));
        eq(modeControl(env2.document).mode, 'each', 'a library subtitle mix: per library');
        eq(cellSelect(env2.document, N(ANIME), 'subtitles').value, 'custom', 'library custom mix shows Custom');
        change(cellSelect(env2.document, N(ANIME), 'audio'), 'lang:ja');
        await clickSave(env2);
        eq(lastPut(env2).body.Libraries, [{ LibraryId: N(ANIME), Name: 'Anime', Settings: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Off', Memory: 'Season', AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }] } }], 'library custom subtitles and other settings kept');
        change(cellSelect(env2.document, N(ANIME), 'audio'), '');
        await clickSave(env2);
        eq(lastPut(env2).body.Libraries, [{ LibraryId: N(ANIME), Name: 'Anime', Settings: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Off', Memory: 'Season', AudioChain: null } }], 'entry kept with its other settings and an explicit null for the cleared leaf');
        eq(lastPut(env2).query.fields, 'libraries.' + N(ANIME) + '.settings.audioChain', 'only the named library audio leaf is cleared');
    }

    // 15. Defaults that leave the track to the file count as the video default; the fallback when the server sends no Defaults.
    section('15 video default resolution and fallbacks');
    {
        const defaults = { AllLibraries: row([{ Kind: 'FileDefault' }]), Libraries: { [N(ANIME)]: row([{ Kind: 'FirstTrack' }, { Kind: 'MyLanguages' }]), [N(MOVIES)]: row([], { WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' }) } };
        const env = await makeEnv(sample({ defaults, settings: each() }));
        const d = env.document;
        eq([firstOption(cellSelect(d, N(ANIME), 'audio')), firstOption(cellSelect(d, N(MOVIES), 'audio'))], ['Video default', 'Video default'], 'FirstTrack and an empty order read "Video default"');
        eq(firstOption(cellSelect(d, N(MOVIES), 'subtitles')), 'Video default', 'VideoDefault subtitles read "Video default"');
        pickMode(d, 'same');
        eq(firstOption(cellSelect(d, 'all', 'audio')), 'Video default', 'every library leaves the audio to the video: "Video default"');
        check(!hasOption(cellSelect(d, 'all', 'audio'), 'video'), 'no explicit Video default then');
        eq(firstOption(cellSelect(d, 'all', 'subtitles')), PER_LIBRARY, 'subtitles differ (smart in Anime, video default in Movies)');

        const fallback = await makeEnv(sample({ defaults: null }));
        eq([firstOption(cellSelect(fallback.document, 'all', 'audio')), firstOption(cellSelect(fallback.document, 'all', 'subtitles'))], ['Video default', 'Video default'], 'no Defaults: the neutral Inherited.Profile');
        const oldProfile = Object.assign({}, PROFILE, { AudioChain: MINE, WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' });
        const fallback2 = await makeEnv(sample({ defaults: null, profile: oldProfile, settings: each() }));
        eq([firstOption(cellSelect(fallback2.document, N(ANIME), 'audio')), firstOption(cellSelect(fallback2.document, N(ANIME), 'subtitles'))], ['Default (English)', 'Default (' + SMART + ')'], 'no Defaults: an opinionated Inherited.Profile');
        const none = await makeEnv(Object.assign(sample({ defaults: null }), { Inherited: null }));
        eq([firstOption(cellSelect(none.document, 'all', 'audio')), hasOption(cellSelect(none.document, 'all', 'audio'), 'video')], ['Default', true], 'nothing known: plain Default, Video default offered');
    }

    // 16. Stored values from earlier versions and other screens.
    section('16 reading stored values');
    {
        const env = await makeEnv(sample({ settings: quiet() }));
        const UI = env.UI;
        const en2 = (k, p) => { let s = en[k] || k; Object.keys(p || {}).forEach((x) => { s = s.split('{' + x + '}').join(p[x]); }); return s; };
        const reread = (settings, key, kind) => {
            const box = env.document.createElement('div');
            UI.libraryTable(box, { settings: clone(settings), defaults: NEUTRAL(), libraries: sample().Libraries, languages: LANGUAGES, translate: en2 });
            return box.querySelectorAll('.bfui-table-wrap .bfui-table-row').find((r) => r.dataset.row === key).querySelector('.bfui-table-' + kind + ' select').value;
        };
        const audio = (everyday) => reread({ Everyday: Object.assign({ Understood: ['en'], Reads: ['en'] }, everyday), Libraries: [] }, 'all', 'audio');
        const subs = (everyday) => reread({ Everyday: Object.assign({ Understood: ['en'], Reads: ['en'] }, everyday), Libraries: [] }, 'all', 'subtitles');
        eq(audio({}), '', 'nothing set → Default');
        eq(audio({ AudioChain: [{ Kind: 'VideoDefault' }] }), 'video', '[VideoDefault] → Video default');
        eq(audio({ AudioChain: [{ Kind: 'VideoDefault' }, { Kind: 'Language', Language: 'ja' }] }), 'video', 'VideoDefault first → Video default (later steps never apply)');
        eq(audio({ AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }] }), 'lang:ja', '[ja, VideoDefault] → Japanese');
        eq(audio({ AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'OriginalLanguage' }, { Kind: 'MyLanguages' }, { Kind: 'FileDefault' }] }), 'lang:ja', '0.4 order [ja, Original, Mine, FileDefault] → Japanese');
        eq(audio({ AudioChain: [{ Kind: 'Language', Language: 'de' }] }), 'lang:de', 'a lone language → that language');
        eq(audio({ AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'Language', Language: 'en' }] }), 'custom', 'two languages in order → Custom');
        eq(audio({ AudioChain: [{ Kind: 'Language', Language: 'ja', TitleContains: 'JP' }] }), 'custom', 'language with track-name text → Custom');
        eq(audio({ AudioChain: [{ Kind: 'OriginalLanguage' }, { Kind: 'FileDefault' }] }), 'custom', 'original language first → Custom (no such option)');
        eq(audio({ ForeignTitles: 'OriginalWithSubtitles' }), 'custom', 'ForeignTitles original → Custom');
        eq(audio({ AudioChain: MINE }), 'lang:en', 'My languages first with one understood language → that language');
        eq(audio({ Understood: ['en', 'es'], AudioChain: MINE }), 'custom', 'My languages first with two understood languages → Custom');
        eq(audio({ ForeignTitles: 'DubInMyLanguage' }), 'lang:en', 'dub with one understood language → that language');
        eq(audio({ AudioChain: [{ Kind: 'FileDefault' }] }), 'custom', '[FileDefault] stored → Custom');
        eq(audio({ AudioChain: [] }), 'custom', 'an empty order → Custom');
        eq(subs({ WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' }), 'video', 'VideoDefault/VideoDefault → Video default');
        eq(subs({ WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' }), 'off', 'Off/Off → Off');
        eq(subs({ WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' }), 'smart', 'PartialOnly/Full → when the audio isn\'t in my languages');
        eq(subs({ WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'PartialOnly' }), 'signs', 'PartialOnly/PartialOnly → signs');
        eq(subs({ WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full' }), 'lang:en', 'Full/Full without languages, one reading language → that language');
        eq(subs({ Reads: ['en', 'es'], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full' }), 'custom', 'Full/Full without languages, two reading languages → Custom');
        eq(subs({ WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off', SubtitleLanguages: [] }), 'custom', 'Off/Off with a language list → Custom');
        eq(subs({ WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['ja'] }), 'lang:ja', 'Full/Full/[ja] → Japanese');
        eq(subs({ WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['ja', 'en'] }), 'custom', 'two subtitle languages → Custom');
        eq(subs({ WhenAudioUnderstood: 'Off' }), 'custom', 'partial subtitle settings → Custom');
        eq(subs({ WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'Full' }), 'custom', 'mixed VideoDefault → Custom');
        eq([UI.tableAudioValue({ AudioChain: VIDEO }, []), UI.tableSubtitleValue({ WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' }, [])], ['video', 'smart'], 'exported readers');
        const layer = {};
        UI.writeTableAudio(layer, 'lang:ja');
        UI.writeTableSubtitles(layer, 'smart');
        eq(layer, { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }], WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' }, 'exported writers');
        // The component with another mode field (the admin's PerLibraryDefaults) writes that field, not PerLibrary.
        const s2 = { Everyday: {}, Libraries: [] };
        const box = env.document.createElement('div');
        UI.libraryTable(box, { settings: s2, modeField: 'PerLibraryDefaults', defaults: NEUTRAL(), libraries: sample().Libraries, languages: LANGUAGES, translate: en2, audience: 'admin' });
        box.querySelectorAll('[role=radio]').find((r) => r.dataset.mode === 'each').click();
        eq([s2.PerLibraryDefaults, s2.PerLibrary], [true, undefined], 'modeField PerLibraryDefaults: that field is written');
    }

    // 17. Save sends the document with only the changed fields.
    section('17 save sends only what changed');
    {
        const env = await makeEnv(sample());
        const original = clone(sample().Settings);
        change(cellSelect(env.document, 'all', 'subtitles'), 'off');
        check(!saveBarHidden(env), 'save bar appears after a change');
        await clickSave(env);
        eq(settingsDiff(original, lastPut(env).body, '').sort(), ['Everyday.WhenAudioNotUnderstood', 'Everyday.WhenAudioUnderstood'], 'PUT changes only the subtitle fields');
        check(saveBarHidden(env), 'save bar hidden after saving');

        const env2 = await makeEnv(sample());
        change(rowByText(env2.document, 'Remember track changes for').querySelector('select'), 'Season');
        await clickSave(env2);
        eq(settingsDiff(original, lastPut(env2).body, ''), ['Everyday.Memory'], 'PUT changes only Memory');

        const env3 = await makeEnv(sample());
        env3.document.querySelector('#bf-detected button').click();
        await settle();
        eq(settingsDiff(original, lastPut(env3).body, ''), ['Seed.Confirmed'], 'Confirm saves only Seed.Confirmed');
        check(!env3.document.getElementById('bf-detected'), 'detected line gone after Confirm');

        const env4 = await makeEnv(sample({ settings: { PerLibrary: true } }));
        change(cellSelect(env4.document, N(MOVIES), 'audio'), 'lang:es-419');
        await clickSave(env4);
        eq(settingsDiff(sample({ settings: { PerLibrary: true } }).Settings, lastPut(env4).body, ''), ['Libraries'], 'a library cell adds only its entry');
        eq(lastPut(env4).body.Libraries, [{ LibraryId: N(MOVIES), Name: 'Movies', Settings: { AudioChain: [{ Kind: 'Language', Language: 'es-419' }, { Kind: 'VideoDefault' }] } }], 'the new entry');
    }

    // 18. Errors next to the controls; an error inside a closed group opens Advanced and the group.
    section('18 errors');
    {
        const errors = [
            { Path: 'everyday.whenAudioUnderstood', Message: 'Bad subtitles' },
            { Path: 'everyday.memory', Message: 'Bad memory' },
            { Path: 'everyday.reads[0]', Message: 'Bad reading language' },
            { Path: 'libraries[0].settings.audioChain[0].language', Message: 'Unknown language' },
            { Path: 'smartRules[0].conditions[0].values[0]', Message: 'Bad value' },
            { Path: 'somewhere', Message: 'Other problem' }
        ];
        const env = await makeEnv(sample({ settings: { PerLibrary: true } }), { onPut: () => ({ status: 400, body: { Message: 'The settings are not valid.', Errors: errors } }) });
        const d = env.document;
        change(cellSelect(d, N(ANIME), 'audio'), 'lang:ja');
        check(!d.getElementById('bf-advanced').open && !group(d, 'trackChanges').open, 'Advanced and Track changes start closed');
        await clickSave(env);
        const cellError = (key, kind) => cellSelect(d, key, kind).closest('td').querySelector('.bfui-field-error').textContent;
        eq(cellError(N(ANIME), 'audio'), 'Unknown language', 'error next to the library audio');
        eq(cellSelect(d, N(ANIME), 'audio').value, 'lang:ja', 'the choice stays selected while the error shows');
        eq(rowByText(d, 'Remember track changes for').querySelector('.bfui-field-error').textContent, 'Bad memory', 'error next to Memory');
        check(d.getElementById('bf-advanced').open, 'Advanced opens for an error inside it');
        check(group(d, 'trackChanges').open, 'the Track changes group opens for its error');
        check(group(d, 'reads').open, 'the Languages for subtitles group opens for its error');
        check(!group(d, 'subtitles').open && !group(d, 'audio').open, 'groups without errors stay closed');
        eq(d.querySelectorAll('.bf-savebar-errors li').map((li) => li.textContent), ['Bad subtitles', 'Bad value', 'Other problem'],
            'errors without a control here (the hidden All libraries row, rules) go to the save bar, without their path');
        change(cellSelect(d, N(ANIME), 'audio'), 'lang:fr');
        check(cellSelect(d, N(ANIME), 'audio').closest('td').querySelector('.bfui-field-error').hidden, 'changing the cell clears its error');
        // Same mode: the Everyday error lands on the All libraries row.
        const env2 = await makeEnv(sample({ settings: quiet() }), { onPut: () => ({ status: 400, body: { Message: 'x', Errors: [errors[0]] } }) });
        change(cellSelect(env2.document, 'all', 'subtitles'), 'off');
        await clickSave(env2);
        eq(cellSelect(env2.document, 'all', 'subtitles').closest('td').querySelector('.bfui-field-error').textContent, 'Bad subtitles', 'Same: error next to All libraries subtitles');
    }

    // 19. Remembered track change: a row of the overrides section with its second line; "Remove" takes the whole override
    // (explicit settings and the remembered change) at once - no confirmation, no manual=true - and leaves nothing to save.
    section('19 remembered track change');
    {
        const env = await makeEnv(sample());
        const d = env.document;
        openOverrides(d);
        const lantern = ovRow(d, 'Étoile Lantern');
        eq([lantern.note, lantern.audio, lantern.subtitles], ['Remembered from a track change', 'French', 'Off'], 'the remembered track change: a second line, its audio and its subtitles');
        lantern.remove.click();
        await settle();
        check(!d.querySelector('.bfui-dialog'), 'no confirmation for one row');
        eq(deletes(env).map((r) => [r.path, r.query]), [['/Babelfin/Me/Overrides/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', { scope: 'Title' }]], 'DELETE with the scope, and no manual=true');
        check(saveBarHidden(env), 'nothing to save after forgetting a track change');
        eq(ovNames(d), ['Sable Voyage'], 'the row is gone');
    }

    // 20. Advanced: the switch on top, then the groups of the settings contract, each a heading button that opens, closes and remembers.
    section('20 Advanced groups');
    {
        const env = await makeEnv(sample());
        const d = env.document;
        const body = d.querySelector('.bf-advanced-body');
        const top = body.children[0];
        check(top.classList.contains('bf-advanced-top') && top.querySelector('.bfui-switch-label').textContent === 'Let Babelfin choose your audio and subtitles' && !!top.querySelector('input[type=checkbox]'), '"Let Babelfin choose your audio and subtitles" sits first, outside any group');
        check(!top.closest('.bfui-disclosure'), 'the switch is not inside a group');
        const ids = body.children.slice(1).map((c) => c.id || c.className);
        eq(ids, GROUPS.map((id) => 'bf-group-' + id).concat(['bf-actions bf-advanced-restore']), 'groups in order: Languages for subtitles, Subtitles, Audio, Track changes, Burned-in subtitles, Start over, then Restore defaults');
        eq(body.children[body.children.length - 1].querySelector('button').textContent, 'Restore defaults', 'the last thing in Advanced is the Restore defaults button');
        const expected = {
            reads: ['Languages for subtitles', []],
            subtitles: ['Subtitles', ['When there are no full subtitles in your languages', 'Text or picture subtitles', 'Embedded or separate subtitle files', 'Always show subtitles', 'Subtitles for the deaf and hard of hearing (SDH)', "When the audio's language is unknown"]],
            audio: ['Audio', ['Audio description tracks', 'Commentary tracks']],
            trackChanges: ['Track changes', ['Remember track changes for']],
            burnedIn: ['Burned-in subtitles', []],
            reset: ['Start over', []]
        };
        for (const id of GROUPS) {
            const g = group(d, id);
            const heading = g.button.parentNode;
            eq([heading.tagName, g.button.tagName, g.button.getAttribute('type'), g.button.textContent], ['H3', 'BUTTON', 'button', expected[id][0]], id + ': a heading button with the group title');
            eq([g.button.getAttribute('aria-expanded'), g.body.hidden], ['false', true], id + ': closed by default');
            eq(g.button.getAttribute('aria-controls'), g.body.id, id + ': aria-controls names the body');
            eq(g.body.querySelectorAll('.bfui-row-label').map((l) => l.textContent), expected[id][1], id + ': its controls');
            g.button.click();
            eq([group(d, id).button.getAttribute('aria-expanded'), group(d, id).body.hidden, env.store.get('babelfin-app-group-' + id)], ['true', false, '1'], id + ': opens and remembers');
            g.button.click();
            eq([group(d, id).button.getAttribute('aria-expanded'), group(d, id).body.hidden, env.store.get('babelfin-app-group-' + id)], ['false', true, '0'], id + ': closes and remembers');
        }
        check(!!group(d, 'reads').body.querySelector('.bfui-list, .bfui-ordered') && /In order of preference/.test(group(d, 'reads').body.textContent), 'Languages for subtitles: the ordered list and its help line');
        // Track changes: a help line saying what a remembered change does, and the next-episode switch for everyone, on unless stored off.
        check(/instead of your library choice/.test(group(d, 'trackChanges').body.querySelector('.bf-group-help').textContent), 'Track changes: the help line explains it outranks the library choice');
        const nextEpisode = (doc) => group(doc, 'trackChanges').body.querySelectorAll('.bfui-switch').find((s) => s.querySelector('.bfui-switch-label') && s.querySelector('.bfui-switch-label').textContent === en['advanced.nextEpisode']);
        check(!!nextEpisode(d) && !nextEpisode(d).querySelector('input[type=checkbox]').checked, 'Track changes: the switch is shown off for a person who turned it off');
        {
            const off = await makeEnv(sample());
            const input = nextEpisode(off.document) ? nextEpisode(off.document).querySelector('input[type=checkbox]') : null;
            if (input) {
                input.click();
                await clickSave(off);
                eq(lastPut(off).body.HandleNextEpisode, true, 'Track changes: switching it on saves true');
            }
        }
        {
            const unset = sample();
            delete unset.Settings.HandleNextEpisode;
            const sw = nextEpisode((await makeEnv(unset)).document);
            check(!!sw && sw.querySelector('input[type=checkbox]').checked, 'Track changes: the switch shows on when the document has no value (the default)');
        }
        {
            const on = await makeEnv(sample({ settings: { HandleNextEpisode: true } }));
            const sw = nextEpisode(on.document);
            check(!!sw, 'Track changes: shown when it is on');
            const input = sw ? sw.querySelector('input[type=checkbox]') : null;
            check(!!input && input.checked, 'Track changes: the switch shows on');
            const swTip = sw ? sw.querySelector('.bfui-tip') : null;
            eq(swTip ? [sw.querySelectorAll('.bfui-info').length, swTip.textContent, sw.querySelector('.bfui-info').getAttribute('aria-label'), (input.getAttribute('aria-describedby') || '').split(' ').indexOf(swTip.id) >= 0] : null,
                [1, en['advanced.nextEpisode.help'], 'More about ' + en['advanced.nextEpisode'], true], 'Track changes: the switch explains itself with an infoTip');
            if (input) {
                input.click();
                await clickSave(on);
                eq(lastPut(on).body.HandleNextEpisode, false, 'Track changes: switching it off saves false');
                const after = nextEpisode(on.document);
                check(!!after && !after.querySelector('input[type=checkbox]').checked, 'Track changes: still shown, off, once saved off');
            }
        }
        check(/Old Film/.test(group(d, 'burnedIn').body.textContent), 'Burned-in subtitles lists the flag');
        check(!!group(d, 'reset').body.querySelector('.bfui-btn-danger') && group(d, 'reset').body.querySelector('.bfui-btn-danger').textContent === 'Remove all my settings', 'Start over: Remove all my settings');
        // Remembered: a new page opens the groups stored as open, and only those.
        group(d, 'audio').button.click();
        group(d, 'reset').button.click();
        const again = await makeEnv(sample(), { storage: Object.fromEntries(env.store) });
        eq(GROUPS.map((id) => group(again.document, id).open), GROUPS.map((id) => id === 'audio' || id === 'reset'), 'a new page opens the remembered groups');
        // Saving re-renders the page; the open groups stay open.
        again.document.getElementById('bf-advanced').open = true;
        change(rowByText(again.document, 'Remember track changes for').querySelector('select'), 'Season');
        await clickSave(again);
        eq(GROUPS.map((id) => group(again.document, id).open), GROUPS.map((id) => id === 'audio' || id === 'reset'), 'groups keep their state after a save');
        // Burned-in subtitles: only when there are flags; removing the last one hides the group.
        const noFlags = await makeEnv(sample({ settings: quiet() }));
        check(group(noFlags.document, 'burnedIn').element.hidden, 'no flags: no Burned-in subtitles group');
        const burned = group(d, 'burnedIn');
        burned.button.click();
        burned.body.querySelector('.bf-item button').click();
        await settle();
        check(group(d, 'burnedIn').element.hidden, 'removing the last flag hides the group');
        check(saveBarHidden(env), 'the immediate DELETE leaves no unsaved change');
        eq(env.current.Settings.BurnedIn, [], 'the flag is removed on the server immediately');
        const removedFlag = env.requests.filter(q => q.method === 'DELETE' && q.path.indexOf('/Me/BurnedIn/') >= 0);
        eq(removedFlag.map(q => [q.path, q.query]), [['/Babelfin/Me/BurnedIn/cccccccccccccccccccccccccccccccc', { scope: 'Title' }]], 'Remove uses the explicit scoped endpoint');
        // Reset: confirmation, POST Me/Reset, the page reloads.
        const resetEnv = await makeEnv(sample());
        resetEnv.document.getElementById('bf-advanced').open = true;
        group(resetEnv.document, 'reset').button.click();
        group(resetEnv.document, 'reset').body.querySelector('.bfui-btn-danger').click();
        await settle();
        check(!!resetEnv.document.querySelector('.bfui-dialog'), 'Reset asks first');
        await confirmDialog(resetEnv, false);
        check(!resetEnv.requests.some((r) => r.path === '/Babelfin/Me/Reset'), 'Cancel sends nothing');
        group(resetEnv.document, 'reset').body.querySelector('.bfui-btn-danger').click();
        await confirmDialog(resetEnv, true);
        check(resetEnv.requests.some((r) => r.method === 'POST' && r.path === '/Babelfin/Me/Reset'), 'OK sends POST Me/Reset');
        eq([ovTitle(resetEnv.document), listGets(resetEnv)], ['Show and movie overrides (0)', 2], 'the page shows the reset settings: the override list is read again and is empty');
        // The switch on top still works.
        const sw = await makeEnv(sample());
        sw.document.querySelector('.bf-advanced-top input[type=checkbox]').click();
        await clickSave(sw);
        eq(settingsDiff(sample().Settings, lastPut(sw).body, ''), ['Enabled'], 'Let Babelfin choose your audio and subtitles saves Enabled only');
        check(/Babelfin is off for you/.test(sw.document.querySelector('.bf-status').textContent), 'the header says Babelfin is off');
    }

    // 21. Controls the settings contract removes are gone; their stored values survive any save untouched.
    section('21 removed controls keep their values');
    {
        const kept = {
            UseEverydayEverywhere: true,
            SmartRules: [{ Id: 'r1', Name: 'Kids', Enabled: true, Match: 'Any', Conditions: [{ Kind: 'Genre', Values: ['Kids'], Negate: true, IsRegex: false }], Settings: { WhenAudioUnderstood: 'Off', TieBreaks: ['Lossless'] } }],
            CollectionOrder: ['eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'],
            Locks: { Sdh: 'Avoid' },
            JellyfinChanges: { RememberAudio: true },
            Everyday: {
                Understood: ['en'], Reads: ['en'], TieBreaks: ['Lossless', 'DirectPlayable'], OriginalSource: 'FlagThenMetadata', ForeignTitles: 'OriginalWithSubtitles',
                SubtitleLanguages: null, UnknownAudioCountsAsUnderstood: true,
                // Left from before only the admin set it: no control here, kept as stored.
                UnlabelledAudio: 'SpecificLanguage', UnlabelledAudioLanguage: 'de'
            }
        };
        const original = sample({ settings: quiet(kept) });
        const env = await makeEnv(clone(original));
        const d = env.document;
        openAll(d);
        await settle();
        const strings = visibleStrings(d.body, true).concat(d.querySelectorAll('option').map((o) => o.textContent));
        ['Rules', 'Accessibility', 'More', 'Add a rule'].forEach((s) => check(strings.indexOf(s) < 0, '"' + s + '" is not on the page'));
        ['Use my settings everywhere', 'Export settings', 'Import settings', 'If several tracks match', 'How the original language is found',
            "Titles in a language I don't understand", 'Download missing subtitles', 'Export, import and reset', 'Rules change settings',
            'Audio tracks without a language', 'Audio tracks with no language', 'Choose a language'].forEach((s) => {
            check(!strings.some((x) => x.indexOf(s) >= 0), '"' + s + '" is not on the page');
        });
        check(!d.querySelector('.bf-rule') && !d.querySelector('.bfui-rule') && !d.querySelector('input[type=file]'), 'no rule editor and no file input');
        check(!d.querySelector('#bf-advanced .bfui-chooser') && !d.querySelector('[data-field=UnlabelledAudio]'), 'no "Audio tracks without a language" control and no language chooser in Advanced');
        eq(cellSelect(d, 'all', 'audio').value, 'custom', 'the stored ForeignTitles shows as Custom in the table');
        // Every visible control changed and saved: the removed fields are byte for byte what was loaded.
        change(rowByText(d, 'Remember track changes for').querySelector('select'), 'Season');
        change(rowByText(d, 'Text or picture subtitles').querySelector('select'), 'TextOnly');
        change(rowByText(d, 'Commentary tracks').querySelector('select'), 'false');
        pickMode(d, 'each');
        change(cellSelect(d, N(ANIME), 'subtitles'), 'off');
        await clickSave(env);
        const body = lastPut(env).body;
        eq(settingsDiff(original.Settings, body, '').sort(), ['Everyday.AvoidCommentary', 'Everyday.Format', 'Everyday.Memory', 'Libraries', 'PerLibrary'], 'only the edited fields change');
        ['UseEverydayEverywhere', 'SmartRules', 'CollectionOrder', 'Locks', 'JellyfinChanges'].forEach((k) => eq(body[k], kept[k], k + ' kept'));
        ['TieBreaks', 'OriginalSource', 'ForeignTitles', 'UnknownAudioCountsAsUnderstood', 'UnlabelledAudio', 'UnlabelledAudioLanguage'].forEach((k) => eq(body.Everyday[k], kept.Everyday[k], 'Everyday.' + k + ' kept'));
        // A second save after a reload keeps them too.
        const reload = await makeEnv(clone(env.current));
        reload.document.getElementById('bf-advanced').open = true;
        change(rowByText(reload.document, 'Remember track changes for').querySelector('select'), 'RestOfShow');
        await clickSave(reload);
        eq(settingsDiff(body, lastPut(reload).body, ''), ['Everyday.Memory'], 'a second save changes only Memory');
        eq(lastPut(reload).body.SmartRules, kept.SmartRules, 'rules still there after a second save');
        eq([lastPut(reload).body.Everyday.UnlabelledAudio, lastPut(reload).body.Everyday.UnlabelledAudioLanguage], ['SpecificLanguage', 'de'], 'a stored "Audio tracks without a language" (a specific language) survives a second save');
        // Errors for the removed parts go to the save bar.
        check(!d.querySelector('.bf-rules'), 'no rules list');
    }

    // 22. Show and movie overrides: Video default choices in the table's words; the table keeps its values when My languages change.
    section('22 summaries and language changes');
    {
        const settings = quiet({ Overrides: [{ ItemId: 'dddddddddddddddddddddddddddddddd', Scope: 'Series', Name: 'Cinder Cub', Settings: { AudioChain: VIDEO, WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' }, Manual: null }, { ItemId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', Scope: 'Series', Name: 'Sable Voyage', Settings: { WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' }, Manual: null }] });
        const env = await makeEnv(sample({ settings }));
        await settle();
        openOverrides(env.document);
        eq(ovRows(env.document).map((r) => [r.name, r.audio, r.subtitles]), [['Cinder Cub', 'Video default', 'Video default'], ['Sable Voyage', "Library's choice", "When the audio isn't in my languages"]],
            'words for the video default and the smart choice (a column the override leaves alone reads "Library\'s choice")');
        const env2 = await makeEnv(sample({ settings: each() }));
        const d = env2.document;
        change(cellSelect(d, N(MOVIES), 'audio'), 'lang:ja');
        change(cellSelect(d, N(ANIME), 'subtitles'), 'lang:fr');
        d.querySelector('.bf-languages-line button').click();
        const input = d.querySelector('.bf-languages-editor .bfui-picker input');
        input.value = 'germ';
        input.dispatchEvent(new Event('input'));
        d.querySelector('.bf-languages-editor').querySelectorAll('li').find((li) => li.dataset.code === 'de').click();
        await settle();
        eq([cellSelect(d, N(MOVIES), 'audio').value, cellSelect(d, N(ANIME), 'subtitles').value, modeControl(d).mode], ['lang:ja', 'lang:fr', 'each'], 'choices and mode kept after adding a language');
        eq(groupsOf(cellSelect(d, N(MOVIES), 'audio'))[1].values, ['lang:en', 'lang:de'], 'My languages group updated');
    }

    // 23. Every translation: labels come from the file, no key shows, the mode control and the table work, every group opens.
    section('23 translations');
    {
        const files = fs.readdirSync(ASSETS + 'i18n').filter((f) => f.endsWith('.json')).map((f) => f.slice(0, -5));
        eq(files.length, 16, '16 translation files');
        for (const lang of files) {
            const table = JSON.parse(fs.readFileSync(ASSETS + 'i18n/' + lang + '.json', 'utf8'));
            const env = await makeEnv(sample({ defaults: OPINIONS(), settings: quiet() }), { lang });
            const d = env.document;
            openAll(d);
            const all = () => visibleStrings(d.body, true).concat(d.querySelectorAll('option').map((o) => o.textContent));
            const rawKey = /(^|\s)(ui|app|field|enum|titles|languages|advanced|file|rules|table|burned|common|restore|startOver)\.[A-Za-z]/;
            eq(all().filter((s) => rawKey.test(s)), [], lang + ': no untranslated key shows (Same)');
            eq(GROUPS.map((id) => group(d, id).element.hidden ? 'hidden' : group(d, id).open), [true, true, true, true, 'hidden', true], lang + ': every group opens (no burned-in flags: that group is hidden)');
            const control = modeControl(d);
            eq([control.group.getAttribute('aria-label'), control.same.textContent, control.each.textContent], [table['ui.table.mode'], table['ui.table.mode.same'], table['ui.table.mode.each']], lang + ': mode control labels');
            eq(firstOption(cellSelect(d, 'all', 'audio')), table['ui.table.defaultPerLibrary'], lang + ': the per-library Default label');
            eq(d.querySelectorAll('.bfui-table-help').map((p) => p.textContent), [table['ui.table.videoDefaultHelp'], table['ui.table.smartHelp']], lang + ': help lines');
            const subs = cellSelect(d, 'all', 'subtitles');
            eq([optionText(subs, 'video'), optionText(subs, 'smart')], [table['ui.table.videoDefault'], table['ui.table.smart']], lang + ': option labels');
            check(table['ui.table.smartHelp'].indexOf(table['ui.table.smart']) === 0, lang + ': the help line starts with the option label');
            change(subs, 'smart');
            eq(stored(env.doc(), 'all', 'subtitles'), wanted('subtitles', 'smart'), lang + ': the table edits');
            key(control.same, 'ArrowRight');
            eq([modeControl(d).mode, rowNames(d)], ['each', ['Anime', 'Movies']], lang + ': the mode switches');
            eq(all().filter((s) => rawKey.test(s)), [], lang + ': no untranslated key shows (per library)');
            eq(cellSelect(d, N(ANIME), 'audio').getAttribute('aria-label'), table['ui.table.audioFor'].split('{name}').join('Anime'), lang + ': aria-label from the file');
            // Every option and both switches have an infoTip with words, not a key; the My languages line has none.
            const tips = d.querySelectorAll('.bfui-tip').filter((tip) => !tip.closest('.bfui-table-cell, section.bfui-genres'));
            eq(tips.length, 11, lang + ': 11 infoTips (9 options, the on/off switch, the next-episode switch; none on My languages)');
            eq(d.querySelectorAll('.bf-languages .bfui-info').length, 0, lang + ': no infoTip on the My languages line');
            eq(tips.filter((x) => !x.textContent.trim() || rawKey.test(x.textContent)).map((x) => x.textContent), [], lang + ': every tip has words, not a key');
            eq(d.querySelectorAll('.bfui-info').filter((x) => !x.getAttribute('aria-label') || rawKey.test(x.getAttribute('aria-label'))).length, 0, lang + ': every "i" button has a name');
        }
    }

    // 24. Wording: no forbidden words in any visible string or in en.json, the banned-word scan in both modes with every group open.
    section('24 wording');
    {
        const banned = ['inherit', 'layer', 'seed', 'assumed', 'dry run', 'version', 'upgrade', 'milestone', 'babelfin filled', 'lookups', 'when needed', 'other language…', 'fallback', 'provenance', 'decorator', 'wrapped', 'cache', 'enum', 'scope', 'tie-break', 'coming soon', 'migrat'];
        const files = fs.readdirSync(ASSETS + 'i18n').filter((f) => f.endsWith('.json'));
        const newKeys = ['ui.table.mode', 'ui.table.mode.same', 'ui.table.mode.each', 'ui.table.defaultPerLibrary', 'ui.table.noLibraries', 'advanced.subtitles', 'advanced.audio', 'advanced.trackChanges',
            'advanced.startOver', 'startOver.button', 'startOver.confirm.title', 'startOver.confirm.text', 'startOver.confirm.ok', 'startOver.done', 'restore.button', 'restore.confirm.title', 'restore.confirm.text', 'restore.confirm.ok'];
        for (const f of files) {
            const table = JSON.parse(fs.readFileSync(ASSETS + 'i18n/' + f, 'utf8'));
            newKeys.forEach((k) => check(typeof table[k] === 'string' && table[k].length > 0 && table[k].indexOf('!') < 0, f + ' ' + k + ' is a plain string'));
            if (f !== 'en.json') {
                const same = ['ui.table.mode.same', 'ui.table.mode.each', 'ui.table.defaultPerLibrary', 'advanced.trackChanges', 'advanced.startOver', 'startOver.button', 'startOver.confirm.title',
                    'restore.button', 'restore.confirm.title', 'restore.confirm.text'].filter((k) => table[k] === en[k]);
                eq(same, [], f + ': the new strings are translated');
                check(table._machineTranslated === true, f + ': still marked machine translated');
            }
        }
        for (const mode of ['same', 'each']) {
            const env = await makeEnv(sample({ defaults: OPINIONS(), settings: mode === 'each' ? each() : undefined }));
            openAll(env.document);
            await settle();
            const strings = visibleStrings(env.document.body, true).concat(env.document.querySelectorAll('option').map((o) => o.textContent)).concat(Object.values(en).filter((v) => typeof v === 'string'));
            const hits = strings.filter((s) => banned.some((w) => s.toLowerCase().indexOf(w) >= 0) || /(^|[^a-z])we /i.test(s));
            eq(hits, [], mode + ': no forbidden words');
            eq(strings.filter((s) => /!/.test(s)), [], mode + ': no exclamation marks');
            eq(strings.filter((s) => /[0-9a-f]{32}/i.test(s) || /[\\/][\w-]+[\\/]/.test(s)), [], mode + ': no ids or paths shown');
            eq(strings.filter((s) => /^note:/i.test(s)), [], mode + ': no "Note:" prefixes');
            console.log('strings scanned (' + mode + '): ' + strings.length);
        }
    }

    // ----- Everyday preferences and inherited defaults -----

    const advSelect = (d, field) => {
        const r = rowByText(d, ADVANCED_FIELDS[field]);
        return r ? r.querySelector('select') : null;
    };
    /** Every <select> on the page outside the "Audio and subtitles" table. */
    const outsideTable = (d) => d.querySelectorAll('select').filter(s => !s.closest('.bfui-table, section.bfui-genres'));
    /** The menu problemsforbids: a "Default (" option, an empty-valued option, a label listed twice. */
    const menuProblems = (select, label) => {
        const texts = select.options.map((o) => o.textContent.trim());
        const out = [];
        texts.forEach((x, i) => {
            if (/^Default \(/.test(x) || x === 'Default') { out.push(label + ': option "' + x + '"'); }
            if (texts.indexOf(x) !== i) { out.push(label + ': "' + x + '" listed twice'); }
        });
        if (select.options.some((o) => o.value === '')) { out.push(label + ': an option stands for nothing'); }
        return out;
    };
    // 25. Plain option menus: no "Default (…)" entry anywhere outside the table, no label twice, the default selected.
    section('25 plain option menus');
    for (const [name, profile, settings] of [
        ['built-in defaults', PROFILE, undefined],
        ['an admin who changed every default', ADMIN_PROFILE, undefined],
        ['stored values', ADMIN_PROFILE, { Everyday: { Understood: ['en'], Reads: ['en'], Format: 'Neutral', IfNothingMatches: 'ShowNothing', PrefersSdh: false, Memory: 'Season', UnlabelledAudio: 'Unknown' } }]
    ]) {
        const env = await makeEnv(sample({ advanced: profile, settings }));
        const d = env.document;
        openAll(d);
        await settle();
        const selects = outsideTable(d);
        eq(selects.length, 9, name + ': nine dropdowns outside the table, all in Advanced');
        check(selects.every((s) => !!s.closest('#bf-advanced')), name + ': every one of them is in Advanced');
        eq(selects.flatMap((s) => menuProblems(s, s.id)), [], name + ': no "Default (…)", no empty option, no label twice');
        const stored = (settings && settings.Everyday) || {};
        for (const field of Object.keys(ADVANCED_FIELDS)) {
            const s = advSelect(d, field);
            const want = onPage(profile, stored, field);
            const byAlways = field === 'Sdh' && want === 'Prefer' && stored.Sdh === undefined && shown(profile, 'Sdh') !== 'Prefer';
            eq(s.value, want, name + ': ' + field + ' shows ' + (byAlways ? 'Prefer (set by "Always show subtitles")' : stored[field] !== undefined ? 'the stored value' : 'the AdvancedDefaults value'));
            check(!!selectedOption(s) && selectedOption(s).textContent.trim().length > 0, name + ': ' + field + ' has a visible selected option');
        }
        // "Full subtitles in another language I read" is offered only while it is stored or the default.
        eq(hasOption(advSelect(d, 'IfNothingMatches'), 'FullInAnyBackupLanguage'), profile === ADMIN_PROFILE, name + ': the rare IfNothingMatches option only when it is the default or stored');
        // "Audio tracks without a language" is the admin's alone now: no row and no language chooser, whatever is stored.
        check(!rowByText(d, 'Audio tracks without a language') && !d.querySelector('#bf-advanced .bfui-chooser'), name + ': no "Audio tracks without a language" row or language chooser');
        check(saveBarHidden(env), name + ': showing the defaults changes nothing');
        eq(settingsDiff(sample({ settings }).Settings, env.doc(), ''), [], name + ': the document is untouched');
    }
    {
        // AdvancedDefaults wins over Inherited (which holds the person's own values) and over the built-in values.
        const inheritedOwn = Object.assign({}, PROFILE, { Format: 'Neutral', AvoidCommentary: true });
        const r = sample({ advanced: ADMIN_PROFILE, profile: inheritedOwn });
        const env = await makeEnv(r);
        eq([advSelect(env.document, 'Format').value, advSelect(env.document, 'AvoidCommentary').value], ['TextOnly', 'false'], 'unset fields read AdvancedDefaults, not Inherited');
        // A locked field shows the admin's value, disabled, still without a Default entry.
        const locked = await makeEnv(sample({ advanced: ADMIN_PROFILE, settings: { Locks: { Sdh: 'Prefer' } } }));
        const sdh = advSelect(locked.document, 'Sdh');
        eq([sdh.value, sdh.disabled, menuProblems(sdh, 'Sdh')], ['Prefer', true, []], 'a locked field: the admin\'s value, disabled, plain options');
        check(/Set by the server admin/.test(rowByText(locked.document, ADVANCED_FIELDS.Sdh).textContent), 'the lock line shows');
        // An older server without AdvancedDefaults: unset fields read Inherited, set fields the built-in values.
        const old = await makeEnv(sample({ advanced: null, profile: Object.assign({}, PROFILE, { Format: 'TextOnly' }), settings: { Everyday: { Understood: ['en'], Reads: ['en'], Embedded: 'PreferEmbedded' } } }));
        eq([advSelect(old.document, 'Format').value, advSelect(old.document, 'Embedded').value, advSelect(old.document, 'Memory').value], ['TextOnly', 'PreferEmbedded', 'RestOfShow'], 'no AdvancedDefaults: Inherited for unset fields, the stored value otherwise');
        change(advSelect(old.document, 'Embedded'), 'Neutral');
        eq(nz(old.doc().Everyday.Embedded), null, 'no AdvancedDefaults: the built-in value clears a field the person had set');
        eq(outsideTable(old.document).flatMap((s) => menuProblems(s, s.id)), [], 'no AdvancedDefaults: still plain menus');
        // Missing members count as null (a server that leaves nulls out of its JSON).
        const missing = await makeEnv(sample({ advanced: ADMIN_PROFILE }), { omitNulls: true });
        eq(Object.keys(ADVANCED_FIELDS).map((f) => advSelect(missing.document, f).value), Object.keys(ADVANCED_FIELDS).map((f) => onPage(ADMIN_PROFILE, null, f)), 'nulls left out of the JSON: the same selections');
        const partial = Object.assign({}, ADMIN_PROFILE);
        delete partial.IfNothingMatches;
        delete partial.UnlabelledAudioLanguage;
        const gaps = await makeEnv(sample({ advanced: partial }), { omitNulls: true });
        eq([advSelect(gaps.document, 'IfNothingMatches').value, hasOption(advSelect(gaps.document, 'IfNothingMatches'), 'FullInAnyBackupLanguage')], ['ShowNothing', false], 'a member missing from AdvancedDefaults: the built-in value');
        check(saveBarHidden(gaps), 'and nothing to save');
    }

    // 26. Picking the value that equals the default clears the stored field; anything else is stored (every Advanced dropdown).
    section('26 picking the default clears the field');
    for (const field of Object.keys(ADVANCED_FIELDS)) {
        const bool = typeof PROFILE[field] === 'boolean';
        // The SDH menu waits while "Always show subtitles" is Yes, so it is tried with that default off.
        const profile = field === 'Sdh' ? Object.assign({}, ADMIN_PROFILE, { PrefersSdh: false }) : ADMIN_PROFILE;
        const env = await makeEnv(sample({ advanced: profile, settings: quiet() }));
        const d = env.document;
        const s = advSelect(d, field);
        const def = shown(profile, field);
        const other = s.options.map((o) => o.value).find((v) => v !== def);
        change(s, other);
        eq(env.doc().Everyday[field], bool ? other === 'true' : other, field + ': another value is stored (' + other + ')');
        check(!saveBarHidden(env), field + ': the page is changed');
        change(s, def);
        eq(nz(env.doc().Everyday[field]), null, field + ': picking the default clears the field');
        check(saveBarHidden(env), field + ': back to nothing to save');
        eq(s.value, def, field + ': the default stays selected');
        // Stored, then back to the default and saved: the field is gone from the saved document.
        const storedValue = bool ? other === 'true' : other;
        const loaded = sample({ advanced: profile, settings: quiet({ Everyday: { Understood: ['en'], Reads: ['en'], [field]: storedValue } }) });
        const env2 = await makeEnv(loaded);
        eq(advSelect(env2.document, field).value, String(other), field + ': a stored value shows');
        change(advSelect(env2.document, field), def);
        await clickSave(env2);
        eq(settingsDiff(loaded.Settings, lastPut(env2).body, ''), ['Everyday.' + field], field + ': the save only clears the field');
        check(!(field in lastPut(env2).body.Everyday) || lastPut(env2).body.Everyday[field] === null, field + ': not in the saved document');
        eq(advSelect(env2.document, field).value, def, field + ': the default shows after saving');
    }
    {
        // A leftover "Audio tracks without a language": no control, so nothing asks for its
        // language and Save goes through with the stored rule as it was.
        const leftover = quiet({ Everyday: { Understood: ['en'], Reads: ['en'], UnlabelledAudio: 'SpecificLanguage' } });
        const env = await makeEnv(sample({ settings: leftover }));
        check(saveBarHidden(env), 'a leftover specific-language rule without its language: nothing to save on load');
        change(advSelect(env.document, 'Format'), 'TextOnly');
        await clickSave(env);
        eq(putCount(env), 1, 'a leftover rule without its language does not hold up Save');
        eq(settingsDiff(sample({ settings: leftover }).Settings, lastPut(env).body, ''), ['Everyday.Format'], 'the save changes only the edited field; the leftover rule is sent as stored');
        check(!/Choose a language/.test(env.document.getElementById('bf-advanced').textContent), 'and nothing asks for a language');
    }

    // 27. Restore defaults: confirmation first, exactly the the settings contract fields, a change to save (nothing sent until Save).
    section('27 Restore defaults');
    {
        const everyday = {
            Understood: ['en', 'ja'], Reads: ['en'], AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }], WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off',
            IfNothingMatches: 'PartialInMyLanguage', Format: 'Neutral', Embedded: 'PreferEmbedded', Sdh: 'Prefer', PrefersSdh: true, UnlabelledAudio: 'SpecificLanguage', UnlabelledAudioLanguage: 'de',
            UsesAudioDescription: true, AvoidCommentary: false, UnknownAudioCountsAsUnderstood: true, Memory: 'TitleOnly',
            TieBreaks: ['Lossless'], OriginalSource: 'FlagThenMetadata', ForeignTitles: null
        };
        const loaded = sample({
            settings: {
                Enabled: false, HandleNextEpisode: false, PerLibrary: true, Everyday: everyday,
                Libraries: [{ LibraryId: N(ANIME), Name: 'Anime', Settings: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['en'], Memory: 'Season', Format: 'TextOnly' } }],
                Locks: { Sdh: 'Avoid' }
            }
        });
        const env = await makeEnv(clone(loaded));
        const d = env.document;
        openAll(d);
        const restore = () => d.getElementById('bf-restore-defaults');
        check(!!restore() && restore().textContent === 'Restore defaults' && !restore().closest('.bfui-disclosure'), 'Restore defaults sits at the end of Advanced, outside the groups');
        restore().click();
        await settle();
        const dialog = d.querySelector('.bfui-dialog');
        check(!!dialog && /Restore defaults\?/.test(dialog.textContent) && /Subtitles, Audio and Track changes/.test(dialog.textContent), 'it asks first, naming what goes back');
        await confirmDialog(env, false);
        eq(settingsDiff(loaded.Settings, env.doc(), ''), [], 'Cancel changes nothing');
        check(saveBarHidden(env), 'Cancel: nothing to save');
        restore().click();
        await confirmDialog(env, true);
        const restoredPaths = RESTORED.map((f) => 'Everyday.' + f).concat(['HandleNextEpisode']).sort();
        eq(settingsDiff(loaded.Settings, env.doc(), '').sort(), restoredPaths, 'exactly the the settings contract fields change');
        RESTORED.forEach((f) => eq(nz(env.doc().Everyday[f]), null, f + ' cleared'));
        eq(env.doc().HandleNextEpisode, true, 'the next-episode switch back on');
        eq(dropNulls([env.doc().Everyday.Understood, env.doc().Everyday.Reads, env.doc().Enabled, env.doc().PerLibrary, env.doc().Locks, env.doc().Libraries, env.doc().Overrides, env.doc().BurnedIn, env.doc().SmartRules]),
            dropNulls([everyday.Understood, everyday.Reads, false, true, { Sdh: 'Avoid' }, loaded.Settings.Libraries, loaded.Settings.Overrides, loaded.Settings.BurnedIn, loaded.Settings.SmartRules]),
            'My languages, Languages for subtitles, the on/off switch, the mode, locks, library entries (their own options too), shows and movies, flags and rules untouched');
        eq(['AudioChain', 'WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'TieBreaks', 'OriginalSource'].map((f) => env.doc().Everyday[f]), [everyday.AudioChain, 'Off', 'Off', ['Lossless'], 'FlagThenMetadata'], 'the table and the options without a control here untouched');
        check(!saveBarHidden(env), 'the page is marked changed');
        eq(putCount(env), 0, 'nothing is sent before Save');
        check(d.getElementById('bf-advanced').open && d.activeElement === restore(), 'Advanced stays open and focus returns to the button');
        eq(Object.keys(ADVANCED_FIELDS).filter((f) => f !== 'Sdh').map((f) => advSelect(d, f).value), Object.keys(ADVANCED_FIELDS).filter((f) => f !== 'Sdh').map((f) => shown(PROFILE, f)), 'every dropdown shows its default again');
        eq([advSelect(d, 'Sdh').value, advSelect(d, 'Sdh').disabled], ['Avoid', true], 'the locked field still shows the admin\'s value');
        eq(d.querySelector('.bf-advanced-top input[type=checkbox]').checked, false, 'Let Babelfin choose your audio and subtitles is still off');
        const switches = group(d, 'trackChanges').body.querySelectorAll('input[type=checkbox]');
        eq(switches.map((i) => i.checked), [true], 'the next-episode switch shows on');
        eq([cellSelect(d, N(ANIME), 'subtitles').value, modeControl(d).mode], ['lang:en', 'each'], 'the table is untouched');
        await clickSave(env);
        eq(putCount(env), 1, 'Save sends it');
        const body = lastPut(env).body;
        eq(settingsDiff(loaded.Settings, body, '').sort(), restoredPaths, 'the saved document differs in exactly those fields');
        // Discard after Restore defaults puts everything back.
        const env2 = await makeEnv(clone(loaded));
        env2.document.getElementById('bf-advanced').open = true;
        env2.document.getElementById('bf-restore-defaults').click();
        await confirmDialog(env2, true);
        env2.document.querySelectorAll('.bf-savebar .bfui-btn').find((b) => b.textContent === 'Discard').click();
        await confirmDialog(env2, true);
        eq(settingsDiff(loaded.Settings, env2.doc(), ''), [], 'Discard after Restore defaults puts every field back');
        eq(advSelect(env2.document, 'Memory').value, 'TitleOnly', 'and the dropdowns show the stored values again');
        // Nothing to restore: no change.
        const env3 = await makeEnv(sample({ settings: quiet({ HandleNextEpisode: true }) }));
        env3.document.getElementById('bf-restore-defaults').click();
        await confirmDialog(env3, true);
        check(saveBarHidden(env3), 'nothing stored: Restore defaults leaves nothing to save');
        // Blocked storage: Advanced and its open groups stay open through the re-render.
        const env4 = await makeEnv(clone(loaded), { brokenStorage: true });
        env4.document.getElementById('bf-advanced').open = true;
        group(env4.document, 'audio').button.click();
        env4.document.getElementById('bf-restore-defaults').click();
        await confirmDialog(env4, true);
        check(env4.document.getElementById('bf-advanced').open && group(env4.document, 'audio').open, 'blocked storage: Advanced and the open group stay open');
    }

    // 28. Start over: the last group, confirmation, POST Me/Reset, the page shows the result.
    section('28 Start over');
    {
        const env = await makeEnv(sample());
        const d = env.document;
        d.getElementById('bf-advanced').open = true;
        const groups = d.querySelector('.bf-advanced-body').querySelectorAll('.bf-group');
        eq(groups[groups.length - 1].id, 'bf-group-reset', 'Start over is the last group');
        eq(group(d, 'reset').button.textContent, 'Start over', 'titled Start over');
        group(d, 'reset').button.click();
        const button = group(d, 'reset').body.querySelector('.bfui-btn-danger');
        eq(button.textContent, 'Remove all my settings', 'its button');
        button.click();
        await settle();
        const dialog = d.querySelector('.bfui-dialog');
        check(!!dialog && /Remove all your settings\?/.test(dialog.textContent) && /This can't be undone/.test(dialog.textContent), 'it asks first');
        eq(dialog.querySelector('.bfui-btn-danger').textContent, 'Remove', 'the confirming button');
        await confirmDialog(env, false);
        check(!env.requests.some((r) => r.path === '/Babelfin/Me/Reset'), 'Cancel sends nothing');
        group(d, 'reset').body.querySelector('.bfui-btn-danger').click();
        await confirmDialog(env, true);
        check(env.requests.some((r) => r.method === 'POST' && r.path === '/Babelfin/Me/Reset'), 'OK sends POST Me/Reset');
        check(/Your settings were reset/.test(d.querySelector('.bfui-toasts').textContent), 'a toast says so');
        eq(putCount(env), 0, 'no PUT');
        eq([ovTitle(d), openOverrides(d) && ovRows(d).length, !!d.querySelector('.bfui-overrides-empty'), env.current.Settings.Overrides.length], ['Show and movie overrides (0)', 0, true, 0], 'the page shows the cleared settings: no overrides left, the section says so');
    }

    // 29. "My languages" everywhere for Understood; "Languages I read" is now "Languages for subtitles".
    section('29 My languages wording');
    {
        const env = await makeEnv(sample());
        const d = env.document;
        d.querySelector('.bf-languages-line button').click();
        openAll(d);
        await settle();
        const editor = d.querySelector('.bf-languages-editor');
        const labelled = editor.querySelectorAll('[aria-label]').map((x) => x.getAttribute('aria-label')).concat(editor.querySelectorAll('.bfui-field-label, label').map((x) => x.textContent));
        check(labelled.some((x) => /^My languages\b/.test(x)), 'the list is labelled "My languages"', labelled);
        eq(d.querySelector('.bf-languages').getAttribute('aria-label'), 'My languages', 'the section is "My languages"');
        const strings = visibleStrings(d.body, true).concat(d.querySelectorAll('option').map((o) => o.textContent));
        eq(strings.filter((x) => /languages I understand|languages understood|\bunderstands\b/i.test(x)), [], 'no "Languages I understand", "Languages understood" or "Understands" on the page');
        check(strings.indexOf('Languages for subtitles') >= 0 && strings.indexOf('Languages I read') < 0, '"Languages I read" is now "Languages for subtitles"');
        for (const f of fs.readdirSync(ASSETS + 'i18n').filter((x) => x.endsWith('.json'))) {
            const table = JSON.parse(fs.readFileSync(ASSETS + 'i18n/' + f, 'utf8'));
            check(!('languages.understood' in table), f + ': no separate "languages I understand" string');
            if (f === 'en.json') {
                eq(Object.keys(table).filter((k) => typeof table[k] === 'string' && /languages I understand|languages understood|\bunderstands\b/i.test(table[k])), [], 'en.json: none of the old wordings');
            }
        }
    }

    // 30. Every section and group open: no option outside the table starts with "Default (", no dropdown repeats a label (both modes, every language).
    section('30 no Default option outside the table');
    for (const lang of fs.readdirSync(ASSETS + 'i18n').filter((f) => f.endsWith('.json')).map((f) => f.slice(0, -5))) {
        const table = JSON.parse(fs.readFileSync(ASSETS + 'i18n/' + lang + '.json', 'utf8'));
        const defaultWord = table['ui.default'];
        const defaultPrefix = table['ui.defaultValue'].split('{value}')[0];
        for (const [profile, settings] of [[PROFILE, quiet()], [ADMIN_PROFILE, each()]]) {
            const env = await makeEnv(sample({ advanced: profile, defaults: OPINIONS(), settings }), { lang });
            const d = env.document;
            openAll(d);
            d.querySelector('.bf-languages-line button').click();
            await settle();
            const selects = outsideTable(d);
            check(selects.length === 9, lang + ': the nine Advanced dropdowns are there', selects.length);
            const bad = [];
            selects.forEach((s) => {
                const texts = s.options.map((o) => o.textContent.trim());
                texts.forEach((x, i) => {
                    if (x.indexOf(defaultPrefix) === 0 || x === defaultWord || /^Default \(/.test(x)) { bad.push(s.id + ' "' + x + '"'); }
                    if (texts.indexOf(x) !== i) { bad.push(s.id + ' repeats "' + x + '"'); }
                });
            });
            eq(bad, [], lang + (profile === ADMIN_PROFILE ? ' (admin defaults)' : '') + ': no Default option and no repeated label outside the table');
            eq(d.getElementById('bf-restore-defaults').textContent, table['restore.button'], lang + ': the Restore defaults label');
            eq(group(d, 'reset').button.textContent, table['advanced.startOver'], lang + ': the Start over title');
            // The table is the one place a Default entry lives.
            check(d.querySelectorAll('.bfui-table-wrap .bfui-table select').every((s) => s.options[0].value === ''), lang + ': every table dropdown still starts with its Default entry');
        }
    }

    // ----- Inline explanations and item-dialog integration -----

    /**
     * The infoTip inside a node: { wrap, button, tip }. The tip is found by the button's aria-describedby: while it shows it sits at the end
     * of <body> (so nothing on the page draws over it), and it goes back into its wrap when hidden.
     */
    const tipParts = (node) => {
        const wrap = node ? node.querySelector('.bfui-info-wrap') : null;
        const button = wrap ? wrap.querySelector('button.bfui-info') : null;
        const tip = button ? button.ownerDocument.getElementById(button.getAttribute('aria-describedby')) : null;
        return { wrap, button, tip };
    };
    const describedBy = (el) => (el.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean);
    const tipShown = (p) => !!p.tip && !p.tip.hidden && p.button.getAttribute('aria-expanded') === 'true' && p.tip.parentNode === p.button.ownerDocument.body;
    const tipHidden = (p) => !!p.tip && p.tip.hidden && p.button.getAttribute('aria-expanded') === 'false' && p.tip.parentNode === p.wrap;
    /** Dispatches an event (not bubbling unless asked); returns it. */
    const fire = (el, type, extra) => {
        const e = new Event(type, Object.assign({ bubbles: false }, extra || {}));
        el.dispatchEvent(e);
        return e;
    };
    const FIRST_PERSON = /(^|[^A-Za-z])(I|I'm|[Mm]y|[Mm]e|[Mm]ine)([^A-Za-z]|$)/;

    // 31. Advanced names and option words: labels in the groups' order, each dropdown's options and words, no first-person
    // labels, no help lines left on the options (their explanation is the infoTip), the retired keys gone from en.json.
    section('31 Advanced names and options');
    {
        const env = await makeEnv(sample());
        const d = env.document;
        openAll(d);
        eq(d.querySelector('.bf-advanced-top .bfui-switch-label').textContent, 'Let Babelfin choose your audio and subtitles', 'the top switch reads "Let Babelfin choose your audio and subtitles"');
        eq(group(d, 'reads').button.textContent, 'Languages for subtitles', 'the reading languages group is "Languages for subtitles"');
        eq(group(d, 'reads').body.querySelector('.bf-group-help').textContent, en['field.Reads.help'], 'with its help line (field.Reads.help)');
        for (const id of Object.keys(ADVANCED_GROUPS)) {
            const body = group(d, id).body;
            eq(body.querySelectorAll('.bfui-row-label').map((l) => l.textContent), ADVANCED_GROUPS[id].map((f) => ADVANCED_FIELDS[f]), id + ': the labels of the settings contract, in order');
            eq(body.querySelectorAll('select').map((s) => s.closest('.bfui-row').getAttribute('data-field')), ADVANCED_GROUPS[id], id + ': the fields behind them');
        }
        eq(outsideTable(d).length, 9, 'nine dropdowns in Advanced: Subtitles 6, Audio 2, Track changes 1');
        for (const field of Object.keys(ADVANCED_FIELDS)) {
            eq(advSelect(d, field).options.map((o) => [o.value, o.textContent]), ADVANCED_OPTIONS[field], field + ': its options and their words, in order');
            eq(en['field.' + field + '.label'], ADVANCED_FIELDS[field], field + ': the label comes from field.' + field + '.label');
        }
        for (const field of ['UsesAudioDescription', 'AvoidCommentary', 'UnknownAudioCountsAsUnderstood']) {
            eq(['true', 'false'].map((v) => optionText(advSelect(d, field), v)), [en['field.' + field + '.true'], en['field.' + field + '.false']], field + ': Yes/No shown in its own words (field.' + field + '.true / .false)');
        }
        eq(['true', 'false'].map((v) => optionText(advSelect(d, 'PrefersSdh'), v)), [en['ui.yes'], en['ui.no']], 'Always show subtitles: Yes and No');
        eq(outsideTable(d).filter((s) => s.closest('.bfui-row').children.some((c) => c.classList.contains('bfui-help'))).map((s) => s.closest('.bfui-row').getAttribute('data-field')), [],
            'no option carries a help line any more (nothing locked, Always show subtitles off)');
        const words = [d.querySelector('.bf-advanced-top .bfui-switch-label').textContent]
            .concat(d.querySelectorAll('#bf-advanced .bfui-disclosure-title').map((x) => x.textContent))
            .concat(d.querySelectorAll('#bf-advanced .bfui-row-label').map((x) => x.textContent))
            .concat(outsideTable(d).flatMap((s) => s.options.map((o) => o.textContent)));
        eq(words.filter((w) => FIRST_PERSON.test(w)), [], 'no first-person label, group title or option word in Advanced');
        // "Show full subtitles in another of your languages": last, and only while it is the default or stored.
        const rare = await makeEnv(sample({ advanced: ADMIN_PROFILE }));
        eq(advSelect(rare.document, 'IfNothingMatches').options.map((o) => [o.value, o.textContent]), ADVANCED_OPTIONS.IfNothingMatches.concat([RARE_OPTION]), 'the rare option when it is the default: last, in its words');
        eq(advSelect(rare.document, 'IfNothingMatches').value, 'FullInAnyBackupLanguage', 'and selected');
        const rareStored = await makeEnv(sample({ settings: { Everyday: { Understood: ['en'], Reads: ['en'], IfNothingMatches: 'FullInAnyBackupLanguage' } } }));
        eq([hasOption(advSelect(rareStored.document, 'IfNothingMatches'), 'FullInAnyBackupLanguage'), advSelect(rareStored.document, 'IfNothingMatches').value], [true, 'FullInAnyBackupLanguage'], 'the rare option when it is stored');
        // en.json.
        eq(['field.PrefersSdh.help', 'field.Format.help', 'field.UnlabelledAudio.label', 'ui.unlabelledLanguage', 'app.chooseLanguage'].filter((k) => k in en), [], 'the retired keys are gone from en.json');
        // The override dialog replaces the per-track item dialog and its "Keep the current setting" row.
        eq([en['dialog.custom'], Object.keys(en).filter((k) => typeof en[k] === 'string' && /Keep (my|the) current setting/.test(en[k]))], [undefined, []], 'no "Keep my current setting" or "Keep the current setting": the per-track dialog is gone');
        eq(Object.keys(ADVANCED_FIELDS).filter((f) => typeof en['field.' + f + '.info'] !== 'string' || !en['field.' + f + '.info']), [], 'every Advanced option has its explanation in en.json');
    }

    // 32. infoTips: one per option, the switch and the My languages line; the label keeps only its text; hover and focus show
    // the tip, a click pins it until Escape, a press elsewhere or another click; one tip at a time; nothing changes the settings.
    section('32 infoTips');
    {
        const env = await makeEnv(sample());
        const d = env.document;
        openAll(d);
        const ids = [];
        for (const field of Object.keys(ADVANCED_FIELDS)) {
            const s = advSelect(d, field);
            const row = s.closest('.bfui-row');
            const head = row.querySelector('.bfui-row-head');
            const label = head ? head.querySelector('label.bfui-row-label') : null;
            const p = tipParts(row);
            eq(row.querySelectorAll('.bfui-info').length, 1, field + ': exactly one infoTip');
            check(!!head && !!label && !!p.wrap && p.wrap.parentNode === head && label.parentNode === head, field + ': the tip sits beside the label in .bfui-row-head');
            if (!p.button || !p.tip || !label) { continue; }
            eq(label.textContent, ADVANCED_FIELDS[field], field + ': the <label> holds only its text');
            check(!label.contains(p.wrap), field + ': the tip is not inside the <label>');
            eq(label.getAttribute('for'), s.id, field + ': the <label> names the dropdown');
            eq([p.button.tagName, p.button.getAttribute('type'), p.button.getAttribute('aria-label'), p.button.getAttribute('aria-describedby'), p.button.getAttribute('aria-expanded'), p.button.textContent],
                ['BUTTON', 'button', 'More about ' + ADVANCED_FIELDS[field], p.tip.id, 'false', 'i'], field + ': the "i" button: "More about …", described by the tip, collapsed');
            eq([p.tip.tagName, p.tip.getAttribute('role'), p.tip.hidden, p.tip.textContent], ['SPAN', 'tooltip', true, en['field.' + field + '.info']], field + ': the tip: role tooltip, hidden, field.' + field + '.info');
            check(p.tip.parentNode === p.wrap && p.wrap.firstChild === p.button, field + ': while hidden the tip sits in its wrap, after the button');
            check(describedBy(s).indexOf(p.tip.id) >= 0, field + ': the dropdown is described by the tip too', s.getAttribute('aria-describedby'));
            ids.push(p.tip.id);
        }
        // The on/off switch.
        const top = d.querySelector('.bf-advanced-top');
        const tp = tipParts(top);
        const input = top.querySelector('input[type=checkbox]');
        eq(top.querySelectorAll('.bfui-info').length, 1, 'the top switch: one infoTip');
        eq([tp.button.getAttribute('aria-label'), tp.button.getAttribute('aria-describedby'), tp.tip.getAttribute('role'), tp.tip.hidden, tp.tip.textContent],
            ['More about Let Babelfin choose your audio and subtitles', tp.tip.id, 'tooltip', true, en['advanced.enabled.info']], 'the top switch: its infoTip (advanced.enabled.info)');
        check(describedBy(input).indexOf(tp.tip.id) >= 0, 'the switch is described by the tip');
        check(top.querySelector('.bfui-switch-label').textContent === 'Let Babelfin choose your audio and subtitles' && !top.querySelector('.bfui-switch-label').contains(tp.wrap), 'the switch label holds only its text; the tip sits outside it');
        // The My languages line has no infoTip any more (removed on purpose): no "i", no tip text, no languages.info key.
        const line = d.querySelector('.bf-languages-line');
        eq([line.querySelectorAll('.bfui-info').length, line.querySelectorAll('.bfui-info-wrap').length, line.querySelectorAll('.bfui-tip').length, 'languages.info' in en], [0, 0, 0, false], 'My languages: no infoTip, no tip text, no languages.info in en.json');
        eq(d.querySelector('.bf-languages').querySelectorAll('.bfui-info').length, 0, 'the whole My languages section has no "i"');
        eq(line.querySelector('.bf-languages-summary').textContent, 'My languages: English', 'the summary keeps its text');
        eq(line.querySelector('.bf-languages-summary').nextSibling, null, 'and nothing follows it in its line');
        ids.push(tp.tip.id);
        // The next-episode switch in Track changes.
        const next = group(d, 'trackChanges').body.querySelector('.bfui-switch');
        const np = tipParts(next);
        eq([next.querySelectorAll('.bfui-info').length, np.button.getAttribute('aria-label'), np.tip.getAttribute('role'), np.tip.hidden, np.tip.textContent],
            [1, 'More about ' + en['advanced.nextEpisode'], 'tooltip', true, en['advanced.nextEpisode.help']], 'the next-episode switch: one infoTip (advanced.nextEpisode.help)');
        ids.push(np.tip.id);
        eq(new Set(ids).size, ids.length, 'every tip has its own id');
        check(ids.every((id) => d.querySelectorAll('[id=' + id + ']').length === 1), 'and each id is on one element only');
        // Every tip: the mouse and keyboard focus show it, leaving hides it.
        const all = Object.keys(ADVANCED_FIELDS).map((f) => [f, tipParts(advSelect(d, f).closest('.bfui-row'))]).concat([['the top switch', tp], ['the next-episode switch', np]]);
        eq(d.querySelectorAll('.bfui-info').filter(button => !button.closest('.bfui-choice-line')).length, all.length, 'outside the new audio-choice tips, the page has exactly ' + all.length + ' "i" buttons: 9 options and the two switches');
        eq(d.querySelectorAll('.bfui-choice-line .bfui-info').length, 0, 'default library and genre audio cells have no attached option tip');
        for (const [name, p] of all) {
            fire(p.button, 'mouseenter');
            check(tipShown(p), name + ': the mouse over the "i" shows the tip (aria-expanded true)');
            check(d.body.lastChild === p.tip && !p.wrap.contains(p.tip), name + ': while shown the tip is moved to the end of <body>');
            fire(p.button, 'mouseleave');
            check(tipHidden(p), name + ': the mouse leaving hides it (back in its wrap)');
            fire(p.button, 'focus');
            check(tipShown(p), name + ': keyboard focus shows it');
            fire(p.button, 'blur');
            check(tipHidden(p), name + ': leaving the button hides it');
        }
        // A click pins it; the click itself changes nothing.
        const before = clone(env.doc());
        const fmt = all.find(([n]) => n === 'Format')[1];
        const formatSelect = advSelect(d, 'Format');
        const formatValue = formatSelect.value;
        let changes = 0;
        formatSelect.addEventListener('change', () => { changes++; });
        const docListeners = () => ['pointerdown', 'keydown'].map((k) => (d.listeners[k] || []).length).join('/');
        const idle = docListeners();
        const click = new Event('click', { bubbles: true });
        fmt.button.dispatchEvent(click);
        check(tipShown(fmt), 'a click shows the tip');
        check(click.defaultPrevented && click.propagationStopped, 'the click stops at the "i" (it never reaches a label or the dropdown)');
        eq([formatSelect.value, changes, settingsDiff(before, env.doc(), ''), saveBarHidden(env)], [formatValue, 0, [], true], 'the click leaves the dropdown and the settings as they were');
        check(/^\d+px$/.test(fmt.tip.style.left || '') && /^\d+px$/.test(fmt.tip.style.top || ''), 'the shown tip is placed (left and top in px)', fmt.tip.style);
        fire(fmt.button, 'mouseleave');
        check(tipShown(fmt), 'pinned: it stays after the mouse leaves');
        const enter = fire(d, 'keydown', { key: 'Enter', bubbles: true });
        check(tipShown(fmt) && !enter.defaultPrevented, 'pinned: another key leaves it (and is left to the page)');
        const escape = fire(d, 'keydown', { key: 'Escape', bubbles: true });
        check(tipHidden(fmt), 'Escape hides a pinned tip');
        check(escape.defaultPrevented && escape.propagationStopped, 'that Escape is consumed (jellyfin-web would take it as "go back")');
        const idleEscape = fire(d, 'keydown', { key: 'Escape', bubbles: true });
        check(!idleEscape.defaultPrevented && !idleEscape.propagationStopped, 'Escape with no tip showing is left to the page');
        eq(docListeners(), idle, 'a hidden tip leaves no document listener behind');
        fmt.button.click();
        check(tipShown(fmt), 'a click pins it again');
        fire(fmt.button, 'pointerdown', { bubbles: true });
        check(tipShown(fmt), 'a press on its own "i" is not a press elsewhere');
        fire(d.querySelector('h1'), 'pointerdown', { bubbles: true });
        check(tipHidden(fmt), 'a press elsewhere on the page hides it');
        fmt.button.click();
        fmt.button.click();
        check(tipHidden(fmt), 'another click on the "i" hides it');
        // One at a time.
        const mem = all.find(([n]) => n === 'Memory')[1];
        fmt.button.click();
        fire(mem.button, 'mouseenter');
        check(tipShown(mem) && tipHidden(fmt), 'opening one tip closes the one that was open');
        fire(mem.button, 'mouseleave');
        check(tipHidden(mem) && tipHidden(fmt), 'and leaving it leaves none open');
        mem.button.click();
        fire(tp.button, 'focus');
        check(tipShown(tp) && tipHidden(mem), 'one at a time across the page (Track changes, then the switch)');
        fire(tp.button, 'blur');
        // The switch's tip: a click does not flip the switch.
        const was = input.checked;
        tp.button.click();
        eq([tipShown(tp), input.checked], [true, was], 'a click on the switch\'s "i" shows the tip and leaves the switch alone');
        fire(d, 'keydown', { key: 'Escape', bubbles: true });
        check(tipHidden(tp), 'Escape hides it');
        eq(docListeners(), idle, 'no document listener left behind');
        eq(settingsDiff(before, env.doc(), ''), [], 'trying every tip changed no setting');
        check(saveBarHidden(env), 'and left nothing to save');
        eq(changes, 0, 'no dropdown change event was sent');
        // A tip still showing when its button leaves the page (here: Save draws the page again) goes away within the 400 ms check.
        const env2 = await makeEnv(sample());
        const d2 = env2.document;
        openAll(d2);
        const pinnedTip = tipParts(advSelect(d2, 'Memory').closest('.bfui-row'));
        pinnedTip.button.click();
        check(tipShown(pinnedTip), 'a pinned tip');
        change(advSelect(d2, 'Format'), 'TextOnly');
        await clickSave(env2);
        check(!pinnedTip.button.isConnected, 'Save drew the page again (the old "i" is gone)');
        await new Promise((r) => setTimeout(r, 550));
        check(pinnedTip.tip.hidden && pinnedTip.tip.parentNode === pinnedTip.wrap && !d2.body.contains(pinnedTip.tip), 'the orphaned tip is hidden and taken off <body>');
        eq(d2.querySelectorAll('.bfui-tip').filter((x) => !x.hidden).length, 0, 'no tip left showing');
        eq([(d2.listeners.pointerdown || []).length, (d2.listeners.keydown || []).length], [0, 1], 'and no tip listener left on the document (only the page\'s own Ctrl+S)');
    }

    // 33. "Always show subtitles" decides SDH: while it is Yes (stored or the default) the SDH menu is disabled, shows Prefer
    // and says why; No shows the SDH menu's own value again; the stored Sdh never changes; an admin lock on SDH keeps it disabled either way.
    section('33 Always show subtitles and SDH');
    {
        const LINE = en['field.Sdh.byAlways'];
        eq(LINE, 'Set by "Always show subtitles", which picks SDH subtitles.', 'the line under the SDH menu (field.Sdh.byAlways)');
        const sdhRow = (d) => advSelect(d, 'Sdh').closest('.bfui-row');
        const shownSdh = (d) => [advSelect(d, 'PrefersSdh').value, advSelect(d, 'Sdh').value, advSelect(d, 'Sdh').disabled, sdhRow(d).querySelector('.bfui-row-extra').textContent];
        const env = await makeEnv(sample({ settings: quiet() }));
        const d = env.document;
        eq(shownSdh(d), ['false', 'Neutral', false, ''], 'No (the default): the SDH menu shows its own value, enabled, no line');
        change(advSelect(d, 'PrefersSdh'), 'true');
        eq(shownSdh(d), ['true', 'Prefer', true, LINE], 'Yes: the SDH menu is disabled, shows Prefer, and the line says why');
        check(sdhRow(d).querySelector('.bfui-row-extra .bfui-help') !== null, 'the line sits in the SDH row');
        eq([env.doc().Everyday.PrefersSdh, nz(env.doc().Everyday.Sdh)], [true, null], 'only PrefersSdh is stored; Sdh untouched');
        change(advSelect(d, 'PrefersSdh'), 'false');
        eq(shownSdh(d), ['false', 'Neutral', false, ''], 'No again: the SDH menu shows its own value, enabled, the line gone');
        eq([nz(env.doc().Everyday.PrefersSdh), nz(env.doc().Everyday.Sdh)], [null, null], 'back to the default: nothing stored');
        check(saveBarHidden(env), 'and nothing to save');
        change(advSelect(d, 'Sdh'), 'Avoid');
        change(advSelect(d, 'PrefersSdh'), 'true');
        eq(shownSdh(d), ['true', 'Prefer', true, LINE], 'an SDH choice, then Yes: Prefer, disabled');
        eq(env.doc().Everyday.Sdh, 'Avoid', 'the SDH choice is kept underneath');
        await clickSave(env);
        eq(settingsDiff(sample({ settings: quiet() }).Settings, lastPut(env).body, '').sort(), ['Everyday.PrefersSdh', 'Everyday.Sdh'], 'saved: the two fields chosen');
        eq([lastPut(env).body.Everyday.PrefersSdh, lastPut(env).body.Everyday.Sdh], [true, 'Avoid'], 'PrefersSdh true and Sdh Avoid (not Prefer)');
        eq(shownSdh(d), ['true', 'Prefer', true, LINE], 'after saving: still Prefer, disabled');
        change(advSelect(d, 'PrefersSdh'), 'false');
        eq(shownSdh(d), ['false', 'Avoid', false, ''], 'No: the stored SDH choice (Avoid) shows again');
        await clickSave(env);
        eq(settingsDiff(sample({ settings: quiet() }).Settings, lastPut(env).body, ''), ['Everyday.Sdh'], 'saved No: PrefersSdh cleared (the default), Sdh still Avoid');
        // Stored Yes with a stored SDH choice.
        const stored = sample({ settings: quiet({ Everyday: { Understood: ['en'], Reads: ['en'], PrefersSdh: true, Sdh: 'Avoid' } }) });
        const env2 = await makeEnv(clone(stored));
        const d2 = env2.document;
        eq(shownSdh(d2), ['true', 'Prefer', true, LINE], 'stored Yes: Prefer, disabled, with the line');
        check(saveBarHidden(env2), 'showing Prefer is not a change');
        change(advSelect(d2, 'Format'), 'TextOnly');
        await clickSave(env2);
        eq(settingsDiff(stored.Settings, lastPut(env2).body, ''), ['Everyday.Format'], 'saving another option leaves PrefersSdh and Sdh as stored');
        eq(lastPut(env2).body.Everyday.Sdh, 'Avoid', 'the stored Sdh is still Avoid');
        change(advSelect(d2, 'PrefersSdh'), 'false');
        eq(shownSdh(d2), ['false', 'Avoid', false, ''], 'unsaved No: Avoid shows, enabled');
        d2.querySelectorAll('.bf-savebar .bfui-btn').find((b) => b.textContent === 'Discard').click();
        await confirmDialog(env2, true);
        eq(shownSdh(d2), ['true', 'Prefer', true, LINE], 'Discard: Prefer and disabled again');
        // Yes by default (the admin's).
        const yes = Object.assign({}, PROFILE, { PrefersSdh: true, Sdh: 'Avoid' });
        const env3 = await makeEnv(sample({ advanced: yes, settings: quiet() }));
        const d3 = env3.document;
        eq(shownSdh(d3), ['true', 'Prefer', true, LINE], 'Yes by default: Prefer, disabled, with the line');
        check(saveBarHidden(env3), 'nothing to save');
        change(advSelect(d3, 'PrefersSdh'), 'false');
        eq(shownSdh(d3), ['false', 'Avoid', false, ''], 'No: the SDH default (Avoid) shows, enabled');
        eq([env3.doc().Everyday.PrefersSdh, nz(env3.doc().Everyday.Sdh)], [false, null], 'No is stored (it is not the default); Sdh untouched');
        await clickSave(env3);
        eq(settingsDiff(sample({ advanced: yes, settings: quiet() }).Settings, lastPut(env3).body, ''), ['Everyday.PrefersSdh'], 'saved: PrefersSdh false only');
        change(advSelect(d3, 'PrefersSdh'), 'true');
        eq([shownSdh(d3), nz(env3.doc().Everyday.PrefersSdh)], [['true', 'Prefer', true, LINE], null], 'back to Yes (the default): Prefer, disabled, the field cleared');
        // An admin lock on SDH.
        const env4 = await makeEnv(sample({ settings: quiet({ Locks: { Sdh: 'Avoid' } }) }));
        const d4 = env4.document;
        eq(shownSdh(d4), ['false', 'Avoid', true, ''], 'SDH locked, No: the admin\'s value, disabled, no Always line');
        check(/Set by the server admin/.test(sdhRow(d4).textContent), 'the lock line shows');
        change(advSelect(d4, 'PrefersSdh'), 'true');
        eq(shownSdh(d4), ['true', 'Prefer', true, LINE], 'SDH locked, Yes: Prefer, disabled, with the line');
        change(advSelect(d4, 'PrefersSdh'), 'false');
        eq(shownSdh(d4), ['false', 'Avoid', true, ''], 'SDH locked, No again: still disabled, the admin\'s value');
        eq([env4.doc().Locks, nz(env4.doc().Everyday.Sdh)], [{ Sdh: 'Avoid' }, null], 'the lock and the stored Sdh untouched');
        eq([sdhRow(d4).querySelector('.bfui-row-label').textContent, sdhRow(d4).querySelectorAll('.bfui-info').length], [ADVANCED_FIELDS.Sdh, 1], 'a locked row: the label still holds only its text (the padlock has none), one infoTip');
        // An admin lock on "Always show subtitles".
        const env5 = await makeEnv(sample({ settings: quiet({ Locks: { PrefersSdh: true } }) }));
        eq([advSelect(env5.document, 'PrefersSdh').disabled].concat(shownSdh(env5.document)), [true, 'true', 'Prefer', true, LINE], 'Always show subtitles locked to Yes: both menus disabled, SDH shows Prefer with the line');
        // Restore defaults.
        const env6 = await makeEnv(clone(stored));
        env6.document.getElementById('bf-advanced').open = true;
        env6.document.getElementById('bf-restore-defaults').click();
        await confirmDialog(env6, true);
        eq(shownSdh(env6.document), ['false', 'Neutral', false, ''], 'Restore defaults: No, and the SDH menu free with its default');
    }

    // 34. Embedded mode: in a frame or with ?embedded=1 there is no back link, <html> is marked bf-embedded and the sign-in
    // link opens in the top window; window.BabelfinApp tells client.js about unsaved changes and asks before leaving.
    section('34 embedded mode');
    {
        const back = (env) => env.document.querySelector('header a.bf-back');
        const marked = (env) => env.document.documentElement.classList.contains('bf-embedded');
        const alone = await makeEnv(sample());
        check(!!back(alone) && back(alone).getAttribute('href') === '/web/' && back(alone).textContent === '← Jellyfin', 'opened on its own: the "← Jellyfin" link is there');
        check(!marked(alone), 'opened on its own: <html> is not marked bf-embedded');
        for (const [label, opts] of [['?embedded=1', { search: '?embedded=1' }], ['?embedded=1 after another parameter', { search: '?lang=de&embedded=1' }], ['in a frame', { frame: true }], ['in a frame on another origin', { frame: 'cross-origin' }]]) {
            const env = await makeEnv(sample(), opts);
            check(!back(env), label + ': no back link');
            check(marked(env), label + ': <html> has class bf-embedded');
            eq([env.document.querySelector('h1').textContent, env.document.querySelector('header .bf-subtitle').textContent], ['Babelfin Settings', 'Choose the audio and subtitles that play for you in each library.'], label + ': the title and the line under it');
            check(!!env.document.getElementById('bf-advanced') && !!cellSelect(env.document, 'all', 'audio'), label + ': the page works as usual');
        }
        for (const search of ['?embedded=0', '?embedded=10', '?notembedded=1']) {
            const env = await makeEnv(sample(), { search });
            check(!!back(env) && !marked(env), search + ': not embedded');
        }
        // Signed out.
        const out = await makeEnv(sample(), { signedOut: true });
        const link = out.document.querySelector('a.bf-link-button');
        check(!!link && link.getAttribute('href') === '/web/' && link.textContent === 'Go to Jellyfin' && out.document.querySelector('.bf-message h2').textContent === 'Open this from Jellyfin', 'signed out: the sign-in message and link');
        eq([link && link.getAttribute('target'), !!back(out)], [null, true], 'signed out on its own: the link opens in the same window; the back link is there');
        check(!out.requests.some((r) => r.path === '/Babelfin/Me/Settings'), 'signed out: no settings request');
        for (const [label, opts] of [['in a frame', { frame: true }], ['with ?embedded=1', { search: '?embedded=1' }]]) {
            const env = await makeEnv(sample(), Object.assign({ signedOut: true }, opts));
            const a = env.document.querySelector('a.bf-link-button');
            eq([a && a.getAttribute('target'), !!back(env), marked(env)], ['_top', false, true], 'signed out ' + label + ': the sign-in link opens in the top window (target _top), no back link');
            eq(env.sandbox.BabelfinApp && env.sandbox.BabelfinApp.hasChanges(), false, 'signed out ' + label + ': hasChanges() false');
        }
        // window.BabelfinApp.
        const env = await makeEnv(sample({ settings: quiet() }), { frame: true });
        const app = env.sandbox.BabelfinApp;
        check(!!app && typeof app.hasChanges === 'function' && typeof app.confirmLeave === 'function', 'window.BabelfinApp has hasChanges() and confirmLeave()');
        check(!!app && Object.isFrozen(app), 'and other scripts can\'t replace them');
        eq(app.hasChanges(), false, 'hasChanges(): false when nothing changed');
        let answer = null;
        const first = app.confirmLeave();
        check(!!first && typeof first.then === 'function', 'confirmLeave() returns a promise');
        first.then((v) => { answer = v; });
        await new Promise((r) => setImmediate(r));
        eq([answer, !!env.document.querySelector('.bfui-dialog')], [true, false], 'confirmLeave() with nothing changed: true at once, no dialog');
        change(cellSelect(env.document, 'all', 'subtitles'), 'off');
        eq(app.hasChanges(), true, 'hasChanges(): true after a change');
        answer = null;
        app.confirmLeave().then((v) => { answer = v; });
        await settle();
        const dialog = env.document.querySelector('.bfui-dialog');
        check(!!dialog && dialog.querySelector('.bfui-dialog-title').textContent === 'Discard your changes?' && !!dialog.querySelector('.bfui-btn-danger') && dialog.querySelector('.bfui-btn-danger').textContent === 'Discard',
            'with changes: the page\'s own Discard confirmation opens', dialog && dialog.textContent);
        eq(answer, null, 'confirmLeave() waits for the answer');
        await confirmDialog(env, false);
        eq([answer, app.hasChanges(), cellSelect(env.document, 'all', 'subtitles').value, putCount(env)], [false, true, 'off', 0], 'Cancel: false, the changes stay, nothing saved');
        app.confirmLeave().then((v) => { answer = v; });
        await confirmDialog(env, true);
        eq([answer, putCount(env)], [true, 0], 'Discard: true, nothing saved');
        await clickSave(env);
        eq(app.hasChanges(), false, 'hasChanges(): false again after saving');
        answer = null;
        app.confirmLeave().then((v) => { answer = v; });
        await new Promise((r) => setImmediate(r));
        eq([answer, !!env.document.querySelector('.bfui-dialog')], [true, false], 'after saving: confirmLeave() true at once');
        check(typeof alone.sandbox.BabelfinApp.hasChanges === 'function', 'opened on its own, BabelfinApp is there too');
    }

/* >>> ui */
    // ----- Shared UI components on their own -----

    const T = translator(en);
    const serSelect = (select) => select.children.map((c) => (c.tagName === 'OPTGROUP'
        ? { g: c.getAttribute('label'), o: c.children.map((x) => x.value + '=' + x.textContent) }
        : c.value + '=' + c.textContent)).concat(['@' + select.value]);
    const IDS = { anime: N(ANIME), movies: N(MOVIES) };
    const JA_VIDEO = [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }];
    /** A layer-like row (the shape of Defaults.AllLibraries): audio order, the two subtitle modes, languages. */
    const lrow = (chain, understood, notUnderstood, languages, extra) => Object.assign({ AudioChain: chain, WhenAudioUnderstood: understood, WhenAudioNotUnderstood: notUnderstood, SubtitleLanguages: languages || [], Understood: ['en'], Reads: ['en'] }, extra || {});

    // Each option has an independent expected document mutation.
    section('35 library table persistence contracts');
    {
        const UI = loadUi().UI;
        const canonical = (value) => Array.isArray(value) ? value.map(canonical)
            : value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonical(value[key])])) : value;
        const contracts = {
            audio: {
                '': {},
                video: { AudioChain: [{ Kind: 'VideoDefault' }] },
                mine: { AudioChain: [{ Kind: 'MyLanguages' }, { Kind: 'VideoDefault' }] },
                original: { AudioChain: [{ Kind: 'OriginalLanguage' }, { Kind: 'VideoDefault' }] },
                'lang:ja': { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }] }
            },
            subtitles: {
                '': {},
                video: { WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' },
                off: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' },
                smart: { WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' },
                signs: { WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'PartialOnly' },
                'lang:ja': { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['ja'] }
            }
        };
        const fields = { audio: ['AudioChain', 'ForeignTitles'], subtitles: ['WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'SubtitleLanguages'] };
        const libs = [{ Id: IDS.anime, Name: 'Anime' }, { Id: IDS.movies, Name: 'Movies' }];
        const defaults = { AllLibraries: lrow(JA_VIDEO, 'Off', 'Off'), Libraries: { [IDS.anime]: lrow(JA_VIDEO, 'Off', 'Off'), [IDS.movies]: lrow(JA_VIDEO, 'Off', 'Off') } };
        for (const audience of ['person', 'admin']) for (const each of [false, true]) {
            for (const rowIndex of (each ? [0, 1] : [0])) for (const kind of ['audio', 'subtitles']) {
                for (const [value, written] of Object.entries(contracts[kind])) {
                    const layer = (memory) => ({ Understood: ['en'], Reads: ['en'], Memory: memory, Sdh: 'Prefer',
                        AudioChain: [{ Kind: 'Language', Language: 'fr' }, { Kind: 'VideoDefault' }], ForeignTitles: 'DubInMyLanguage',
                        WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off', SubtitleLanguages: ['de'] });
                    const doc = { PerLibrary: each, Everyday: layer('RestOfShow'),
                        Libraries: libs.map((l) => ({ LibraryId: l.Id, Name: l.Name, Settings: layer('ThisSeason') })),
                        SmartRules: [{ Name: 'unchanged', Then: { Format: 'TextOnly' } }], Overrides: [] };
                    const expected = clone(doc);
                    const target = each ? expected.Libraries[rowIndex].Settings : expected.Everyday;
                    fields[kind].forEach((field) => { delete target[field]; });
                    Object.assign(target, written);
                    let events = 0;
                    const view = UI.libraryTable(null, { settings: doc, defaults, libraries: libs, languages: REAL_LANGUAGES,
                        translate: T, uiLanguage: 'en', audience, onChange: () => { events++; } });
                    const select = view.element.querySelectorAll('.bfui-table-' + kind + ' select')[rowIndex];
                    const label = [audience, each ? 'per library' : 'all libraries', rowIndex, kind, value || 'inherit'].join(' / ');
                    check(select.options.some((o) => o.value === value), label + ': choice is available');
                    select.value = value;
                    select.dispatchEvent(new Event('change', { bubbles: true }));
                    eq(canonical(clone(doc)), canonical(expected), label + ': exactly the selected fields changed');
                    eq(events, 1, label + ': one change notification');
                    view.refresh();
                    eq(view.element.querySelectorAll('.bfui-table-' + kind + ' select')[rowIndex].value, value, label + ': selection survives refresh');
                }
            }
        }
    }

    // 36. BabelfinUI.tableChoice: one dropdown with exactly the table's choices for one column.
    section('36 tableChoice');
    {
        const UI = loadUi().UI;
        const layers = [null, lrow(VIDEO, 'VideoDefault', 'VideoDefault'), lrow(MINE, 'PartialOnly', 'Full'), lrow([{ Kind: 'OriginalLanguage' }, { Kind: 'FileDefault' }], 'Full', 'Full', ['de']), lrow(JA_VIDEO, 'Off', 'Off'), lrow([], 'PartialOnly', 'PartialOnly'), lrow(MINE, 'Full', 'Full', [])];
        const minds = [[], ['en'], ['ja', 'en'], ['tl']];
        const readsLists = [undefined, ['de'], ['en', 'fr']];
        const valuesToTry = ['', 'video', 'mine', 'original', 'lang:ja', 'off', 'smart', 'signs', 'custom', 'lang:zz', 'lang:EN'];
        // The same options, words and selection as the "All libraries" cell of the table, for both audiences, every Default text, both columns.
        let compared = 0;
        for (const audience of ['person', 'admin']) {
            for (const layer of layers) {
                for (const mine of minds) {
                    for (const reads of readsLists) {
                        for (const kind of ['audio', 'subtitles']) {
                            for (const value of valuesToTry) {
                                if ((kind === 'audio' && ['off', 'smart', 'signs'].indexOf(value) >= 0) || (kind === 'subtitles' && ['mine', 'original'].indexOf(value) >= 0)) { continue; }
                                const everyday = { Understood: mine.slice(), Reads: (reads || mine).slice() };
                                if (value && value !== 'custom') {
                                    if (kind === 'audio') { UI.writeTableAudio(everyday, value); } else { UI.writeTableSubtitles(everyday, value); }
                                }
                                if (value === 'custom') {
                                    if (kind === 'audio') { everyday.AudioChain = [{ Kind: 'OriginalLanguage' }]; } else { everyday.WhenAudioUnderstood = 'Off'; everyday.WhenAudioNotUnderstood = 'Full'; }
                                }
                                const defaults = layer ? { AllLibraries: Object.assign({}, layer, { Understood: mine, Reads: reads || mine }), Libraries: {} } : null;
                                const table = UI.libraryTable(null, { settings: { PerLibrary: false, Everyday: everyday, Libraries: [] }, defaults, libraries: [], languages: REAL_LANGUAGES, translate: T, uiLanguage: 'en', audience });
                                const cell = table.element.querySelector('.bfui-table-' + kind + ' select');
                                const choice = UI.tableChoice({ t: T, kind, audience, languages: REAL_LANGUAGES, uiLanguage: 'en', mine, reads, defaultLayer: layer, value, label: 'x' });
                                compared++;
                                check(JSON.stringify(serSelect(cell)) === JSON.stringify(serSelect(choice.select)), 'tableChoice = the table cell: ' + [audience, JSON.stringify(layer && layer.AudioChain), layer && layer.WhenAudioUnderstood, mine, reads, kind, value].join(' '),
                                    { table: serSelect(cell).filter((x) => typeof x === 'string'), choice: serSelect(choice.select).filter((x) => typeof x === 'string') });
                                check(choice.getValue() === cell.value, 'tableChoice value = the cell value for "' + value + '"', [choice.getValue(), cell.value]);
                            }
                        }
                    }
                }
            }
        }
        console.log('tableChoice compared with the table cell: ' + compared);
        // Default text and option values against the table, for the layers that decide the Default text.
        const cases = [
            ['video default', lrow(VIDEO, 'VideoDefault', 'VideoDefault')],
            ['ja then video; off', lrow(JA_VIDEO, 'Off', 'Off')],
            ['my languages; smart', lrow(MINE, 'PartialOnly', 'Full')],
            ['original; signs', lrow([{ Kind: 'OriginalLanguage' }], 'PartialOnly', 'PartialOnly')],
            ['file default; full with reads', lrow([{ Kind: 'FileDefault' }], 'Full', 'Full', [], { Reads: ['de'] })],
            ['full with languages', lrow(VIDEO, 'Full', 'Full', ['fil'])],
            ['nothing', {}]
        ];
        for (const [label, defaults] of cases) {
            const host = loadUi();
            const box = host.document.createElement('div');
            host.document.body.appendChild(box);
            const settings = { Everyday: { Understood: defaults.Understood || ['en'], Reads: defaults.Reads || ['en'] }, Libraries: [], PerLibrary: false };
            host.UI.libraryTable(box, { settings, defaults: { AllLibraries: defaults, Libraries: {} }, libraries: [], languages: REAL_LANGUAGES, translate: T, uiLanguage: 'en' });
            const [audioCell, subtitleCell] = box.querySelectorAll('select');
            const a = host.UI.tableChoice({ t: T, kind: 'audio', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: settings.Everyday.Understood, defaultLayer: defaults, value: '', label: 'Audio' });
            const s = host.UI.tableChoice({ t: T, kind: 'subtitles', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: settings.Everyday.Understood, reads: settings.Everyday.Reads, defaultLayer: defaults, value: '', label: 'Subtitles' });
            eq(a.select.options[0].textContent, audioCell.options[0].textContent, 'Default text of the audio choice like the table: ' + label);
            eq(s.select.options[0].textContent, subtitleCell.options[0].textContent, 'Default text of the subtitles choice like the table: ' + label);
            eq(a.select.options.map((o) => o.value), audioCell.options.map((o) => o.value), 'audio option values like the table: ' + label);
            eq(s.select.options.map((o) => o.value), subtitleCell.options.map((o) => o.value), 'subtitle option values like the table: ' + label);
            eq(a.select.getAttribute('aria-label'), 'Audio', 'aria-label (label): ' + label);
        }
        // Behaviour.
        const e = loadUi();
        let changed = null;
        const c = e.UI.tableChoice({ t: T, kind: 'audio', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: ['en'], defaultLayer: lrow(JA_VIDEO, 'PartialOnly', 'Full'), value: 'video', onChange: (v) => { changed = v; } });
        eq([c.getValue(), c.select.value], ['video', 'video'], 'tableChoice starts on the value it is given');
        eq(c.select.options.map((o) => o.textContent).slice(0, 5), ['Default (Japanese)', 'Video default', en['ui.table.audioMyLanguages'], en['ui.table.audioOriginal'], 'English'], 'Default (Japanese), Video default, audio choices, then My languages group');
        eq(groupsOf(c.select).map((g) => g.label), [null, 'My languages', 'All languages'], 'the same groups as the table');
        c.setValue('lang:fr');
        eq([c.getValue(), changed], ['lang:fr', null], 'setValue selects without calling onChange');
        c.setValue('custom');
        check(c.getValue() === 'custom' && hasOption(c.select, 'custom'), 'a Custom value is offered');
        c.select.value = 'lang:de';
        c.select.dispatchEvent(new Event('change', { bubbles: true }));
        eq([changed, c.getValue()], ['lang:de', 'lang:de'], 'picking calls onChange with the table value');
        check(hasOption(c.select, 'custom'), 'Custom stays offered once it was the value');
        const plain = e.UI.tableChoice({ t: T, kind: 'audio', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: ['en'], defaultLayer: lrow(VIDEO, 'VideoDefault', 'VideoDefault'), value: '' });
        check(!hasOption(plain.select, 'custom'), 'no Custom option while the value is not Custom');
        c.setValue('lang:xx');
        eq(c.getValue(), 'lang:xx', 'setValue with a language that is not listed adds it');
        c.setValue(null);
        eq(c.getValue(), '', 'setValue(null) is Default');
        check(c.element.classList.contains('bfui') && c.element.contains(c.select), 'element is a bfui wrapper holding the dropdown');
        const unknown = e.UI.tableChoice({ t: T, kind: 'subtitles', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: [], defaultLayer: null, value: 'lang:sv' });
        eq([unknown.getValue(), unknown.select.options[0].textContent], ['lang:sv', 'Default'], 'a stored language that is not listed is kept; with no default layer the first option reads plain Default');
        // Written values come back as the same table value (writeTableAudio / writeTableSubtitles with tableAudioValue / tableSubtitleValue).
        for (const value of ['', 'video', 'lang:ja', 'lang:fr']) {
            const layer = {};
            e.UI.writeTableAudio(layer, value);
            eq(e.UI.tableAudioValue(layer, ['en']), value, 'audio value "' + value + '" survives writeTableAudio / tableAudioValue');
        }
        for (const value of ['', 'video', 'off', 'smart', 'signs', 'lang:ja']) {
            const layer = {};
            e.UI.writeTableSubtitles(layer, value);
            eq(e.UI.tableSubtitleValue(layer, ['en']), value, 'subtitle value "' + value + '" survives writeTableSubtitles / tableSubtitleValue');
        }
        // The admin audience words the Default without anyone's languages and has no My languages group.
        const adm = e.UI.tableChoice({ t: T, kind: 'subtitles', audience: 'admin', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: ['en'], reads: ['fr'], defaultLayer: lrow(VIDEO, 'PartialOnly', 'Full'), value: '' });
        check(adm.select.options.some((o) => o.textContent === "When the audio isn't in the viewer's languages"), 'admin audience: the viewer\'s words');
        check(!adm.select.querySelector('optgroup[label="My languages"]'), 'admin audience: no My languages group');
        eq(adm.select.options[0].textContent, "Default (When the audio isn't in the viewer's languages)", 'admin audience: the Default text names the viewer\'s languages in general');
        const adm2 = e.UI.tableChoice({ t: T, kind: 'audio', audience: 'admin', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: ['ja'], defaultLayer: lrow(MINE, 'PartialOnly', 'Full'), value: '' });
        const adm3 = e.UI.tableChoice({ t: T, kind: 'audio', audience: 'admin', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: ['de', 'es'], defaultLayer: lrow(MINE, 'PartialOnly', 'Full'), value: '' });
        eq(adm2.select.options[0].textContent, adm3.select.options[0].textContent, 'admin audience: the Default text does not depend on the languages given');
        // The languages given decide the person's Default text and groups.
        const p1 = e.UI.tableChoice({ t: T, kind: 'audio', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: ['ja'], defaultLayer: lrow(MINE, 'PartialOnly', 'Full'), value: '' });
        const p2 = e.UI.tableChoice({ t: T, kind: 'audio', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: ['de'], defaultLayer: lrow(MINE, 'PartialOnly', 'Full'), value: '' });
        eq([p1.select.options[0].textContent, p2.select.options[0].textContent], ['Default (Japanese)', 'Default (German)'], 'person audience: the Default text follows My languages');
        // Subtitles: Languages for subtitles are listed before My languages.
        const sub = e.UI.tableChoice({ t: T, kind: 'subtitles', languages: REAL_LANGUAGES, uiLanguage: 'en', mine: ['ja'], reads: ['de'], defaultLayer: null, value: '' });
        eq(groupsOf(sub.select)[1].values, ['lang:de', 'lang:ja'], 'subtitles: Languages for subtitles first, then My languages');
        // The page's translations: German words come out in German.
        const de = JSON.parse(fs.readFileSync(ASSETS + 'i18n/de.json', 'utf8'));
        const gc = e.UI.tableChoice({ t: translator(de, 'de'), kind: 'subtitles', languages: REAL_LANGUAGES, uiLanguage: 'de', mine: ['en'], defaultLayer: null, value: 'smart' });
        eq([optionText(gc.select, 'off'), optionText(gc.select, 'smart'), gc.getValue()], [de['ui.table.off'], de['ui.table.smart'], 'smart'], 'tableChoice speaks the translator\'s language');
    }

    // 37. BabelfinUI.overridesTable on its own: columns, words, order, empty state, removal and its focus, Remove all and
    // its confirmation, rows without a usable id, unique row keys, the audiences.
    section('37 overridesTable');
    const OV = (id, scope, name, extra) => Object.assign({ ItemId: id.repeat(32), Scope: scope, Name: name, Type: scope === 'Series' ? 'Series' : 'Movie', ProductionYear: null, SeriesName: null, LibraryId: '1', LibraryName: 'Movies', Settings: {}, Manual: null }, extra || {});
    const OV_ROWS = () => [
        OV('a', 'Series', 'Sable Voyage', { ProductionYear: 2023, LibraryName: 'Anime', Settings: { AudioChain: JA_VIDEO, WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['en'] } }),
        OV('b', 'Title', 'Étoile Lantern', { ProductionYear: 2001, Manual: { Audio: { Language: 'fr', Kind: 'Commentary' }, SubtitlesOff: true } }),
        OV('c', 'Season', 'Season 2', { Type: 'Season', SeriesName: 'Copper Horizon', LibraryName: 'Anime', Settings: { WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' } }),
        OV('d', 'Title', 'Gone Film', { Type: null, LibraryId: null, LibraryName: null, Settings: { AudioChain: VIDEO, Sdh: 'Prefer' } }),
        OV('e', 'Title', 'Pilot', { Type: 'Episode', SeriesName: 'Cinder Cub', LibraryName: 'Anime', Settings: { AudioChain: [{ Kind: 'OriginalLanguage' }] }, Manual: { Subtitle: { Language: 'en', Kind: 'Sdh' } } }),
        OV('f', 'Series', 'Glass Harbor', { ProductionYear: 2021, LibraryName: 'Animation', Settings: { AudioChain: [{ Kind: 'MyLanguages' }, { Kind: 'FileDefault' }] } })
    ];
    /** An overridesTable inside a collapsible section (so "the section's heading button" exists), with callbacks that record their calls. */
    function mountOverrides(options) {
        const host = loadUi();
        const { document } = host;
        const section = document.createElement('section');
        section.className = 'bfui-disclosure';
        const heading = document.createElement('button');
        heading.className = 'bfui-disclosure-button';
        section.appendChild(heading);
        const box = document.createElement('div');
        section.appendChild(box);
        document.body.appendChild(section);
        const m = { UI: host.UI, document, section, heading, box, calls: [], resolvers: [] };
        const o = Object.assign({
            t: T, rows: OV_ROWS(), languages: REAL_LANGUAGES, uiLanguage: 'en', mine: ['en'], reads: ['en'],
            onRemove: (row) => { m.calls.push('remove:' + row.Name); return new Promise((resolve, reject) => { m.resolvers.push({ resolve, reject }); }); },
            onRemoveAll: () => { m.calls.push('removeAll'); return Promise.resolve(); },
            onError: (error) => { m.calls.push('error:' + (error && (error.status || error.message))); }
        }, options || {});
        m.view = host.UI.overridesTable(box, o);
        m.rows = () => box.querySelectorAll('.bfui-overrides-row').map((tr) => ({
            tr, name: tr.querySelector('.bfui-overrides-title').textContent, note: tr.querySelector('.bfui-overrides-note') ? tr.querySelector('.bfui-overrides-note').textContent : null,
            library: tr.querySelector('.bfui-overrides-library').textContent, audio: tr.querySelector('.bfui-overrides-audio').textContent,
            subtitles: tr.querySelector('.bfui-overrides-subtitles').textContent, remove: tr.querySelector('.bfui-overrides-remove')
        }));
        m.names = () => m.rows().map((r) => r.name);
        m.removeOf = (name) => m.rows().find((r) => r.name === name).remove;
        m.removeAllButton = () => box.querySelector('.bfui-overrides-remove-all');
        m.answer = async (ok) => {
            await settle();
            const dialog = document.querySelector('.bfui-dialog');
            const info = dialog ? { title: dialog.querySelector('.bfui-dialog-title').textContent, message: dialog.querySelector('.bfui-dialog-message').textContent, buttons: dialog.querySelectorAll('button').map((b) => b.textContent), role: dialog.getAttribute('role') } : null;
            if (dialog) { dialog.querySelectorAll('button')[ok ? 1 : 0].click(); }
            await settle();
            return info;
        };
        return m;
    }
    {
        // Shape and words.
        const m = mountOverrides();
        const { box } = m;
        eq(box.firstChild.tagName + '.' + box.firstChild.className, 'DIV.bfui bfui-overrides', 'the component is a bfui block');
        check(m.view.element === box.firstChild && typeof m.view.refresh === 'function', 'overridesTable returns { element, refresh }');
        const table = box.querySelector('table');
        eq([table.className, table.getAttribute('aria-label')], ['bfui-table bfui-overrides-table', 'Show and movie overrides'], 'a table named by overrides.title');
        eq(box.querySelectorAll('thead th').map((th) => th.textContent), ['Show or movie', 'Library', 'Audio', 'Subtitles', 'Remove'], 'columns: Show or movie, Library, Audio, Subtitles, and a Remove column for screen readers');
        eq(box.querySelectorAll('thead th').map((th) => th.getAttribute('scope')), ['col', 'col', 'col', 'col', 'col'], 'every header is a column header');
        eq(box.querySelectorAll('thead th').slice(-1)[0].querySelector('.bfui-sr').textContent, 'Remove', 'the last header is screen-reader text');
        eq(m.rows().map((r) => [r.name, r.note, r.library, r.audio, r.subtitles]), [
            ['Glass Harbor (2021)', null, 'Animation', 'English', "Library's choice"],
            ['Cinder Cub · Pilot · For this episode', 'Remembered from a track change', 'Anime', 'Custom', 'English SDH subtitles'],
            ['Copper Horizon · Season 2 · For this season', null, 'Anime', "Library's choice", "When the audio isn't in my languages"],
            ['Sable Voyage (2023)', null, 'Anime', 'Japanese', 'English'],
            ['Étoile Lantern (2001)', 'Remembered from a track change', 'Movies', 'French commentary', 'Off'],
            ['Gone Film', null, 'Not in a library', 'Video default', "Library's choice"]
        ], 'rows sorted by library then name (a season or an episode after its show\'s name), no library last; the words of the table; the remembered note');
        eq(m.rows().map((r) => [r.tr.getAttribute('data-item'), r.tr.getAttribute('data-scope')]), [['f'.repeat(32), 'Series'], ['e'.repeat(32), 'Title'], ['c'.repeat(32), 'Season'], ['a'.repeat(32), 'Series'], ['b'.repeat(32), 'Title'], ['d'.repeat(32), 'Title']], 'each row names its item and scope');
        const first = m.rows()[0].tr;
        eq([first.firstChild.tagName, first.firstChild.getAttribute('scope'), first.firstChild.className], ['TH', 'row', 'bfui-overrides-name'], 'the name is the row header');
        eq(first.querySelectorAll('td').map((td) => td.getAttribute('data-label')), ['Library', 'Audio', 'Subtitles', null], 'narrow screens stack the cells under their labels (data-label)');
        eq(m.rows().map((r) => r.tr.querySelector('.bfui-overrides-subtitles').classList.contains('bfui-overrides-unset')), [true, false, false, false, false, true], 'a column the override leaves alone is marked unset');
        eq(m.rows().map((r) => r.tr.querySelector('.bfui-overrides-library').classList.contains('bfui-overrides-unset')), [false, false, false, false, false, true], 'and so is "Not in a library"');
        eq(m.rows().map((r) => [r.remove.tagName, r.remove.getAttribute('type'), r.remove.textContent, r.remove.getAttribute('aria-label'), r.remove.disabled]), m.names().map((n) => ['BUTTON', 'button', 'Remove', 'Remove the override for ' + n, false]), 'a Remove button per row, named for its item');
        const all = m.removeAllButton();
        eq([all.tagName, all.getAttribute('type'), all.textContent, all.classList.contains('bfui-btn-danger'), all.disabled, all.parentNode.className, table.nextSibling === all.parentNode], ['BUTTON', 'button', 'Remove all show and movie overrides', true, false, 'bfui-overrides-actions', true], 'Remove all: a danger button under the table');
        eq(box.querySelectorAll('label, h1, h2, h3, h4, h5, h6, input, select, textarea').length, 0, 'no label, heading or field inside the component');
        eq(box.querySelectorAll('th button').length, 0, 'no button inside a header');
        eq(box.querySelectorAll('.bfui-overrides-empty').length, 0, 'no empty line while there are rows');
        check(m.rows().every((r) => !r.tr.hidden), 'every row is shown');
    }
    {
        // Words per column: the override's own settings first, then a remembered track change; the person's or the admin's table words.
        const words = (extra, options) => {
            const m = mountOverrides(Object.assign({ rows: [OV('a', 'Title', 'X', extra)] }, options || {}));
            const r = m.rows()[0];
            return [r.audio, r.subtitles, r.note];
        };
        const NO = "Library's choice";
        eq(words({ Settings: null }), [NO, NO, null], 'settings null: the library\'s choice in both columns');
        eq(words({ Settings: {} }), [NO, NO, null], 'settings empty: the library\'s choice in both columns');
        eq(words({ Settings: { Sdh: 'Prefer', Format: 'TextOnly' } }), [NO, NO, null], 'options the table has no word for are not shown');
        eq(words({ Settings: { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'FileDefault' }] } })[0], 'Japanese', 'an audio language');
        eq(words({ Settings: { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'Language', Language: 'en' }] } })[0], 'Custom', 'two languages in order: Custom');
        eq(words({ Settings: { AudioChain: [{ Kind: 'VideoDefault' }] } })[0], 'Video default', 'audio: Video default');
        eq(words({ Settings: { ForeignTitles: 'DubInMyLanguage' } })[0], 'English', 'a dub in my language: the one language of My languages');
        eq(words({ Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' } })[1], 'Off', 'subtitles: Off');
        eq(words({ Settings: { WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' } })[1], 'Video default', 'subtitles: Video default');
        eq(words({ Settings: { WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'PartialOnly' } })[1], 'Signs and songs only', 'subtitles: signs and songs only');
        eq(words({ Settings: { WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' } })[1], "When the audio isn't in my languages", 'subtitles: when the audio isn\'t in my languages');
        eq(words({ Settings: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['fr'] } })[1], 'French', 'subtitles: a language');
        eq(words({ Settings: { WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full' } })[1], 'English', 'full subtitles with no language of their own: the one language of Languages for subtitles');
        eq(words({ Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Full' } })[1], 'Custom', 'a mix the table has no word for: Custom');
        eq(words({ Settings: { SubtitleLanguages: ['fr', 'de'], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full' } })[1], 'Custom', 'two subtitle languages: Custom');
        eq(words({ Settings: { AudioChain: [{ Kind: 'Language', Language: 'zz' }] } })[0], 'zz', 'an unknown language code is shown as it is');
        // Remembered track changes (Manual): their language, "commentary", "audio description", "signs and songs", "SDH", "Off".
        eq(words({ Manual: { Audio: { Language: 'fr', Kind: 'normal' } } }), ['French', NO, 'Remembered from a track change'], 'a remembered audio track');
        eq(words({ Manual: { Audio: { Language: 'fr', Kind: 'Commentary' } } })[0], 'French commentary', 'a remembered commentary track');
        eq(words({ Manual: { Audio: { Language: 'fr', Kind: 'ad' } } })[0], 'French audio description', 'a remembered audio description track');
        eq(words({ Manual: { Audio: { Kind: 'ad' } } })[0], 'Unknown language audio description', 'a remembered track with no language');
        eq(words({ Manual: { Audio: { Variety: 'yue', Language: 'zh', Kind: 'normal' } } })[0], 'Cantonese', 'the variety wins over the base language');
        eq(words({ Manual: { SubtitlesOff: true } })[1], 'Off', 'remembered: subtitles off');
        eq(words({ Manual: { Subtitle: { Language: 'es', Kind: 'sdh' } } })[1], 'Spanish SDH subtitles', 'remembered SDH subtitles');
        eq(words({ Manual: { Subtitle: { Language: 'es', Kind: 'partial' } } })[1], 'Spanish signs and songs', 'remembered signs and songs subtitles');
        eq(words({ Manual: { Subtitle: { Language: 'es', Kind: 'full' } } })[1], 'Spanish', 'remembered full subtitles');
        eq(words({ Manual: { SubtitlesOff: true, Subtitle: { Language: 'es', Kind: 'full' } } })[1], 'Off', 'remembered: "off" beats a subtitle track');
        eq(words({ Settings: { AudioChain: JA_VIDEO }, Manual: { Audio: { Language: 'fr', Kind: 'normal' } } }).slice(0, 1), ['Japanese'], 'the override\'s own audio wins over a remembered track');
        eq(words({ Settings: { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' }, Manual: { SubtitlesOff: false, Subtitle: { Language: 'es', Kind: 'full' } } })[1], 'Off', 'the override\'s own subtitles win over a remembered one');
        eq(words({ Manual: { At: '2026-10-03T00:00:00Z', Client: 'Jellyfin Web' } }), [NO, NO, 'Remembered from a track change'], 'a remembered change with no track still gets its note');
        // The words follow the languages (arrays, or functions asked at each drawing).
        const live = { mine: ['en'], reads: ['en'] };
        const lm = mountOverrides({ rows: [OV('a', 'Title', 'X', { Settings: { AudioChain: [{ Kind: 'MyLanguages' }, { Kind: 'FileDefault' }], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full' } })], mine: () => live.mine, reads: () => live.reads });
        eq([lm.rows()[0].audio, lm.rows()[0].subtitles], ['English', 'English'], 'with one language each: that language');
        live.mine = ['ja', 'en'];
        live.reads = ['de'];
        lm.view.refresh();
        eq([lm.rows()[0].audio, lm.rows()[0].subtitles], ['Custom', 'German'], 'refresh() with no rows draws the same rows with the new languages');
        live.mine = ['fr'];
        lm.view.refresh();
        eq(lm.rows()[0].audio, 'French', 'and again');
        // The admin audience words the table's choices for "the viewer".
        const admin = mountOverrides({ rows: [OV('a', 'Title', 'X', { Settings: { WhenAudioUnderstood: 'PartialOnly', WhenAudioNotUnderstood: 'Full' } })], audience: 'admin' });
        eq(admin.rows()[0].subtitles, "When the audio isn't in the viewer's languages", 'the admin audience: "the viewer\'s languages"');
        // Names.
        const names = (rows) => mountOverrides({ rows }).names();
        eq(names([OV('a', 'Series', 'Berlin Test (2019)', { ProductionYear: 2019 })]), ['Berlin Test (2019)'], 'a name that already ends in its year is not given the year twice');
        eq(names([OV('a', 'Series', 'Sable Voyage', { ProductionYear: 2023 }), OV('b', 'Title', 'Pilot', { Type: 'Episode', SeriesName: 'Cinder Cub' }), OV('c', 'Collection', 'Clockwork Guild', { Type: 'BoxSet' }), OV('d', 'Title', 'Sunrise', { Type: 'Episode' })]),
            ['Cinder Cub · Pilot · For this episode', 'Clockwork Guild · For this collection', 'Sable Voyage (2023)', 'Sunrise · For this episode'], 'names: year after a show or movie, show before a season or episode, a scope word for a season, an episode or a collection');
        eq(names([OV('a', 'Title', 'Only', { Name: '' }), OV('b', 'Title', 'Other', { Name: null })]).length, 2, 'rows with no name are still drawn');
        eq(names([OV('a', 'Title', 'Movie', { Scope: 'Title', Type: 'Movie', ProductionYear: 1999 })]), ['Movie (1999)'], 'a movie\'s title override needs no scope word');
    }
    {
        // Order: library, then name; no library last; a show before its seasons; independent of the order the rows come in.
        const rows = [
            OV('a', 'Series', 'Zeta', { LibraryName: 'Movies' }), OV('b', 'Series', 'élan', { LibraryName: 'Movies' }), OV('c', 'Series', 'Alpha', { LibraryName: 'Movies' }),
            OV('d', 'Season', 'Season 2', { Type: 'Season', SeriesName: 'Copper Horizon', LibraryName: 'Anime' }), OV('e', 'Series', 'Copper Horizon', { LibraryName: 'Anime' }),
            OV('f', 'Title', 'Nowhere B', { LibraryName: null }), OV('0', 'Title', 'Nowhere A', { LibraryName: '' }), OV('1', 'Series', 'Anything', { LibraryName: 'Zoo' })
        ];
        const want = ['Copper Horizon', 'Copper Horizon · Season 2 · For this season', 'Alpha', 'élan', 'Zeta', 'Anything', 'Nowhere A', 'Nowhere B'];
        eq(mountOverrides({ rows }).names(), want, 'sorted by library (Anime, Movies, Zoo), then name without regard to accents; seasons after their show; rows in no library last');
        eq(mountOverrides({ rows: rows.slice().reverse() }).names(), want, 'the same order whatever order the rows come in');
        const de = mountOverrides({ rows, t: translator(JSON.parse(fs.readFileSync(ASSETS + 'i18n/de.json', 'utf8')), 'de'), uiLanguage: 'de' });
        eq(de.names().length, 8, 'sorting in another language draws every row');
    }
    {
        // Empty state, refresh.
        const m = mountOverrides({ rows: [] });
        eq([m.box.textContent, m.box.querySelector('p').className, !!m.box.querySelector('table'), !!m.removeAllButton()], ['No show or movie overrides yet.', 'bfui-empty bfui-overrides-empty', false, false], 'no rows: the line says so, no table and no Remove all');
        m.view.refresh(OV_ROWS());
        eq([m.names().length, !!m.removeAllButton(), !!m.box.querySelector('.bfui-overrides-empty')], [6, true, false], 'refresh(rows) draws the table');
        m.view.refresh([]);
        eq([!!m.box.querySelector('table'), m.box.querySelector('.bfui-overrides-empty').textContent], [false, 'No show or movie overrides yet.'], 'refresh([]) goes back to the line');
        m.view.refresh(null);
        check(!!m.box.querySelector('.bfui-overrides-empty'), 'refresh(null) counts as no rows');
        const noRows = loadUi().UI.overridesTable(null, { t: T });
        eq(noRows.element.textContent, 'No show or movie overrides yet.', 'no rows option at all: the empty line');
        const rowsIn = OV_ROWS();
        const copy = mountOverrides({ rows: rowsIn });
        rowsIn.length = 0;
        eq(copy.names().length, 6, 'the component keeps its own copy of the rows list');
    }
    {
        // Remove one: busy, ignoring other clicks, then the row goes and focus moves.
        const m = mountOverrides();
        const names = m.names();
        m.removeOf(names[1]).focus();
        m.removeOf(names[1]).click();
        m.removeOf(names[1]).click();
        m.removeOf(names[2]).click();
        // A click that reaches a handler although the button is off (a script, or an event sent straight to it) is ignored too.
        m.removeOf(names[2]).dispatchEvent(new Event('click', { bubbles: true }));
        m.removeOf(names[1]).dispatchEvent(new Event('click', { bubbles: true }));
        eq(m.calls, ['remove:Pilot'], 'while a removal is on its way, other clicks do nothing (onRemove gets the row of Cinder Cub · Pilot, once)');
        check(m.rows().every((r) => r.remove.disabled) && m.removeAllButton().disabled, 'every Remove and Remove all are off while busy');
        eq(m.names(), names, 'the row stays until the removal answers');
        m.resolvers[0].resolve();
        await settle();
        eq(m.names(), names.filter((n) => n !== names[1]), 'the row goes when the removal succeeds');
        check(m.rows().every((r) => !r.remove.disabled) && !m.removeAllButton().disabled, 'buttons are on again');
        check(m.document.activeElement === m.rows()[1].remove, 'focus goes to the next row\'s Remove');
        // The last row: focus goes to the previous one.
        const last = m.rows().length - 1;
        m.rows()[last].remove.click();
        m.resolvers[1].resolve();
        await settle();
        check(m.document.activeElement === m.rows()[m.rows().length - 1].remove, 'removing the last row: focus goes to the previous row\'s Remove');
        // The first row: focus goes to what was second.
        const second = m.rows()[1].name;
        m.rows()[0].remove.click();
        m.resolvers[2].resolve();
        await settle();
        eq([m.rows()[0].name, m.document.activeElement === m.rows()[0].remove], [second, true], 'removing the first row: focus goes to the row that took its place');
        // A rejection leaves the rows and goes to onError.
        const before = m.names();
        m.rows()[0].remove.click();
        m.resolvers[3].reject(Object.assign(new Error('boom'), { status: 500 }));
        await settle();
        eq([m.names(), m.calls[m.calls.length - 1]], [before, 'error:500'], 'a failed removal leaves the rows and reports to onError');
        check(m.rows().every((r) => !r.remove.disabled), 'and the buttons are on again');
        // Everything gone: focus goes to the section\'s heading button.
        while (m.rows().length) {
            m.rows()[0].remove.click();
            m.resolvers[m.resolvers.length - 1].resolve();
            await settle();
        }
        check(m.document.activeElement === m.heading, 'no row left: focus goes to the section\'s heading button');
        eq(m.box.querySelector('.bfui-overrides-empty').textContent, 'No show or movie overrides yet.', 'and the line says so');
        check(!m.removeAllButton(), 'Remove all goes with the last row');
        // onRemove that throws at once, and no onError at all.
        const t1 = mountOverrides({ onRemove: () => { throw new Error('sync'); } });
        t1.rows()[0].remove.click();
        await settle();
        eq([t1.names().length, t1.calls.slice(-1)[0]], [6, 'error:sync'], 'onRemove that throws: the rows stay, onError hears it');
        const t2 = mountOverrides({ onError: null, onRemove: () => Promise.reject(new Error('x')) });
        t2.rows()[0].remove.click();
        await settle();
        eq(t2.names().length, 6, 'a failed removal with no onError: the rows stay and nothing is thrown');
        // Without a section around it, focus goes to the table itself.
        const lone = loadUi();
        const loneBox = lone.document.createElement('div');
        lone.document.body.appendChild(loneBox);
        const loneView = lone.UI.overridesTable(loneBox, { t: T, rows: [OV('a', 'Title', 'Solo')], onRemove: async () => {}, onRemoveAll: async () => {} });
        loneBox.querySelector('.bfui-overrides-remove').click();
        await settle();
        check(lone.document.activeElement === loneView.element && loneView.element.getAttribute('tabindex') === '-1', 'with no section around it, focus goes to the component itself');
    }
    {
        // A redraw for another reason keeps focus where it was.
        const m = mountOverrides();
        const rows = OV_ROWS();
        m.rows()[2].remove.focus();
        const name = m.rows()[2].name;
        m.view.refresh(rows);
        check(m.document.activeElement === m.rows().find((r) => r.name === name).remove, 'refresh(rows): focus stays on the same row\'s Remove');
        m.removeAllButton().focus();
        m.view.refresh();
        check(m.document.activeElement === m.removeAllButton(), 'refresh(): focus stays on Remove all');
        m.document.body.focus();
        m.view.refresh();
        check(m.document.activeElement === m.document.body, 'a redraw takes no focus when none was inside');
    }
    {
        // Remove all: the confirmation, Cancel, OK, a failure, a refresh while it runs.
        const m = mountOverrides({ confirmText: 'Everyone goes back to the library choice.' });
        m.removeAllButton().click();
        let info = await m.answer(false);
        eq(info, { title: 'Remove all show and movie overrides?', message: 'Everyone goes back to the library choice.', buttons: ['Cancel', 'Remove'], role: 'alertdialog' }, 'Remove all asks first: its title, the given text, Cancel and Remove, a danger dialog');
        eq([m.calls, m.names().length], [[], 6], 'Cancel does nothing');
        const plain = mountOverrides({});
        plain.removeAllButton().click();
        info = await plain.answer(false);
        eq(info.message, "Every show and movie goes back to its library choice. This can't be undone.", 'with no text given the person\'s text is used');
        m.removeAllButton().click();
        await settle();
        m.document.dispatchEvent(new Event('keydown', { bubbles: true, key: 'Escape' }));
        await settle();
        eq([!!m.document.querySelector('.bfui-dialog'), m.calls], [false, []], 'Escape closes the confirmation without removing');
        m.removeAllButton().click();
        await m.answer(true);
        eq([m.calls, m.names().length, !!m.box.querySelector('.bfui-overrides-empty'), m.document.activeElement === m.heading], [['removeAll'], 0, true, true], 'OK: onRemoveAll is called, every row goes, focus goes to the section\'s heading');
        // A failure leaves the rows.
        const f = mountOverrides({ onRemoveAll: () => Promise.reject(Object.assign(new Error('x'), { status: 500 })) });
        f.removeAllButton().click();
        await f.answer(true);
        eq([f.names().length, f.calls.slice(-1)[0], f.rows().every((r) => !r.remove.disabled) && !f.removeAllButton().disabled], [6, 'error:500', true], 'a failed Remove all: the rows stay, onError hears it, the buttons are on again');
        // A browser drops focus from a button that is switched off (the callback's blur stands for that): after a failure it is back on Remove all, as a failed row Remove puts it back.
        const ff = mountOverrides({ onRemoveAll: () => { ff.document.activeElement.blur(); return Promise.reject(Object.assign(new Error('x'), { status: 500 })); } });
        ff.removeAllButton().focus();
        ff.removeAllButton().click();
        await ff.answer(true);
        check(ff.document.activeElement === ff.removeAllButton() && ff.names().length === 6, 'a failed Remove all: focus is back on the Remove all button, the rows stay');
        // Buttons are off while it runs.
        let release = null;
        const g = mountOverrides({ onRemoveAll: () => new Promise((resolve) => { release = resolve; }) });
        g.removeAllButton().click();
        await g.answer(true);
        check(g.rows().every((r) => r.remove.disabled) && g.removeAllButton().disabled, 'while Remove all runs, every button is off');
        g.removeAllButton().click();
        g.removeAllButton().dispatchEvent(new Event('click', { bubbles: true }));
        await settle();
        check(!g.document.querySelector('.bfui-dialog'), 'and a second Remove all asks nothing (not even when the click reaches the handler of the disabled button)');
        release();
        await settle();
        eq(g.names().length, 0, 'Remove all finished');
        // A list the callback has just refreshed is not emptied again.
        let holder = null;
        const h = mountOverrides({ onRemoveAll: () => { holder.view.refresh([OV_ROWS()[0]]); return Promise.resolve(); } });
        holder = h;
        h.removeAllButton().click();
        await h.answer(true);
        eq(h.names(), ['Sable Voyage (2023)'], 'refresh(rows) from inside onRemoveAll wins: the fresh rows stay');
    }
    {
        // Rows without a usable id: drawn, their Remove disabled and out of the way, never an onRemove call.
        const good = (c, n) => OV(c, 'Title', n);
        const bad = (id, n) => OV('0', 'Title', n, { ItemId: id });
        const rows = [bad('', 'AAA empty 1'), bad('', 'AAB empty 2'), bad(null, 'AAC null'), bad('xyz', 'AAD short'), bad('12345678-1234-1234-1234-12345678901', 'AAE 31 hex'), good('b', 'Bravo'), good('c', 'Charlie'), good('d', 'Delta')];
        const m = mountOverrides({ rows });
        check(m.rows().length === 8, 'all eight rows are drawn');
        ['AAA empty 1', 'AAB empty 2', 'AAC null', 'AAD short', 'AAE 31 hex'].forEach((n) => check(m.removeOf(n).disabled === true, 'a disabled Remove: ' + n));
        ['Bravo', 'Charlie', 'Delta'].forEach((n) => check(m.removeOf(n).disabled === false, 'an enabled Remove: ' + n));
        ['AAA empty 1', 'AAC null', 'AAE 31 hex'].forEach((n) => { const b = m.removeOf(n); if (b.onclick) { b.onclick(); } b.click(); });
        await settle();
        eq(m.calls, [], 'onRemove is never called for a bad id, not even by a script that calls the handler');
        m.removeOf('Bravo').click();
        m.resolvers[0].reject(Object.assign(new Error('x'), { status: 500 }));
        await settle();
        check(m.removeOf('AAA empty 1').disabled && m.removeOf('AAE 31 hex').disabled && !m.removeOf('Bravo').disabled && !m.removeOf('Charlie').disabled, 'after a failed removal the bad rows are still disabled and the good ones are on');
        m.removeOf('Charlie').focus();
        m.removeOf('Charlie').click();
        check(['Bravo', 'Delta', 'AAA empty 1'].every((n) => m.removeOf(n).disabled), 'all off while busy');
        m.resolvers[1].resolve();
        await settle();
        eq(m.names().length, 7, 'only Charlie went');
        check(m.document.activeElement === m.removeOf('Delta'), 'focus goes to the next row that can be removed');
        m.removeOf('Delta').click();
        m.resolvers[2].resolve();
        await settle();
        check(m.document.activeElement === m.removeOf('Bravo'), 'the last removable row: focus goes to the previous removable row, never to a disabled one');
        m.removeOf('Bravo').click();
        m.resolvers[3].resolve();
        await settle();
        check(m.rows().length === 5 && m.document.activeElement === m.heading, 'no removable row left: focus goes to the section\'s heading; the five bad rows stay', [m.rows().length]);
        check(!!m.removeAllButton(), 'Remove all is still offered');
        m.view.refresh(rows);
        m.view.refresh();
        eq(m.rows().length, 8, 'refresh draws all the rows again');
        const only = mountOverrides({ rows: [bad('', 'x'), bad('', 'y')] });
        check(only.rows().length === 2 && only.rows().every((r) => r.remove.disabled), 'a list of nothing but bad rows: two rows, both disabled');
        const dashed = mountOverrides({ rows: [bad('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'dashed')] });
        dashed.rows()[0].remove.click();
        dashed.resolvers[0].resolve();
        await settle();
        eq([dashed.calls, dashed.rows().length], [['remove:dashed'], 0], 'a dashed upper-case GUID counts as a usable id and its row goes');
        // Every row keeps its own key even when ids repeat: the same id in two scopes are two overrides; an exact repeat is drawn too.
        const keys = mountOverrides({ rows: [OV('a', 'Series', 'Show'), OV('a', 'Season', 'S2', { Type: 'Season', SeriesName: 'Show' }), OV('a', 'Series', 'Show again')] });
        eq(keys.rows().length, 3, 'rows that share an id (two scopes, or an exact repeat) are all drawn');
        keys.removeOf('Show · S2 · For this season').click();
        check(keys.rows().length === 3 && keys.rows().every((r) => r.remove.disabled), 'while a removal runs every Remove is off, also both rows that share an id and a scope (each has a key of its own)');
        keys.resolvers[0].resolve();
        await settle();
        eq(keys.names().sort(), ['Show', 'Show again'], 'removing one scope leaves the same item\'s other scope');
        eq(keys.calls, ['remove:S2'], 'and asks only for that row');
    }
/* <<< ui */

/* >>> page */
    // ----- The person page's "Show and movie overrides" section -----

    const A_ID = 'a'.repeat(32);
    const B_ID = 'b'.repeat(32);
    const C_ID = 'c'.repeat(32);
    const D_ID = 'd'.repeat(32);
    const E_ID = 'e'.repeat(32);
    /** A stored ItemOverride (what Settings.Overrides holds). */
    const storedOv = (id, scope, name, settings, manual) => ({ ItemId: id, Scope: scope, Name: name, Settings: settings || {}, Manual: manual || null, UpdatedAt: '2026-10-04T00:00:00Z' });
    const toasts = (d) => d.querySelectorAll('.bfui-toast').map((x) => x.textContent);
    const errorToasts = (d) => d.querySelectorAll('.bfui-toast-error').map((x) => x.textContent);
    const keyed = (env) => env.requests.filter((r) => r.method === 'PUT' && r.path === '/Babelfin/Me/Settings');
    const serverIds = (env) => env.current.Settings.Overrides.map((x) => x.ItemId[0]);
    const bodyIds = (put) => put.body.Overrides.map((x) => x.ItemId[0]);
    /** Answers the open confirmation dialog (true: its danger/OK button, false: Cancel); returns what it said. */
    async function answerDialog(env, yes) {
        await settle();
        const dialog = env.document.querySelector('.bfui-dialog');
        if (!dialog) { return null; }
        const said = { title: dialog.querySelector('.bfui-dialog-title').textContent, message: dialog.querySelector('.bfui-dialog-message').textContent, buttons: dialog.querySelectorAll('button').map((b) => b.textContent) };
        dialog.querySelectorAll('button')[yes ? 1 : 0].click();
        await settle();
        return said;
    }

    // 38. The section itself: between "Audio and subtitles" and Advanced, titled with its count, closed by default, built when first
    // opened, remembered, with the help line and the table; it replaces the old "Shows and movies" list.
    section('38 overrides section');
    {
        const env = await makeEnv(sample());
        const d = env.document;
        const main = env.main();
        eq(main.children.filter((c) => !c.hidden).map(sectionName), ['bf-languages', 'bf-table-section', 'bf-overrides', 'bf-advanced'], 'order: My languages, Audio and subtitles, Show and movie overrides, Advanced');
        const sec = ovSection(d);
        const heading = sec.querySelector('.bfui-disclosure-heading');
        const button = ovButton(d);
        const body = sec.querySelector('.bfui-disclosure-body');
        eq([sec.tagName, sec.parentNode === main, heading.tagName, button.parentNode === heading, button.tagName, button.getAttribute('type')], ['SECTION', true, 'H2', true, 'BUTTON', 'button'], 'a section with an h2 holding the heading button');
        eq([button.getAttribute('aria-expanded'), button.getAttribute('aria-controls') === body.id && !!body.id, body.hidden], ['false', true, true], 'closed by default; the button controls the body');
        eq(ovTitle(d), 'Show and movie overrides (2)', 'titled "Show and movie overrides (N)"');
        eq(button.querySelector('.bfui-disclosure-title').textContent, en['overrides.title'] + ' (2)', 'built from overrides.title');
        eq(sec.classList.contains('bf-section') && sec.classList.contains('bf-overrides'), true, 'it is a bf-section like the others');
        eq(listGets(env), 1, 'GET Me/Overrides is read once, on load');
        check(!d.querySelector('.bfui-overrides') && body.childNodes.length === 0, 'the table is built the first time the section is opened');
        check(saveBarHidden(env) && env.sandbox.BabelfinApp.hasChanges() === false, 'nothing to save');
        check(visibleStrings(env.main(), false).indexOf('Show and movie overrides (2)') >= 0 && visibleStrings(env.main(), false).join('|').indexOf('Choices for single shows') < 0, 'closed: only its title is visible');
        button.click();
        eq([button.getAttribute('aria-expanded'), body.hidden, env.store.get('babelfin-app-group-overrides')], ['true', false, '1'], 'opens and remembers (babelfin-app-group-overrides)');
        eq(listGets(env), 1, 'opening reads nothing again');
        const help = sec.querySelector('.bf-overrides-help');
        eq([help.textContent, help.classList.contains('bfui-help'), body.children[0] === help], [en['overrides.help'], true, true], 'the help line comes first (overrides.help)');
        check(help.textContent.indexOf('⋮') >= 0 && help.textContent.indexOf('Audio and subtitles (Babelfin)') >= 0, 'and names the ⋮ menu entry');
        eq(ovRows(d).map((r) => [r.name, r.note, r.library, r.audio, r.subtitles]), [
            ['Sable Voyage', null, 'Anime', 'Japanese', 'English'],
            ['Étoile Lantern (2001)', 'Remembered from a track change', 'Movies', 'French', 'Off']
        ], 'the person\'s overrides: name, library, audio and subtitles in the table\'s words; the remembered change noted');
        eq(sec.querySelectorAll('thead th').map((th) => th.textContent), ['Show or movie', 'Library', 'Audio', 'Subtitles', 'Remove'], 'columns');
        eq(sec.querySelector('.bfui-overrides-remove-all').textContent, 'Remove all show and movie overrides', 'Remove all under the table');
        eq(sec.querySelector('.bfui-overrides-remove-all').classList.contains('bfui-btn-danger'), true, 'a danger button');
        check(!!sec.querySelector('.bfui-overrides-actions .bfui-overrides-remove-all'), 'the Remove all button sits in the table\'s actions line');
        button.click();
        eq([button.getAttribute('aria-expanded'), body.hidden, env.store.get('babelfin-app-group-overrides')], ['false', true, '0'], 'closes and remembers');
        check(visibleStrings(env.main(), false).join('|').indexOf('Choices for single shows') < 0, 'closed again: only the title is visible');
        // The older list is gone from the page, and the burned-in group is the only list left that has Remove buttons of its own.
        eq(d.querySelectorAll('*').filter((x) => /bf-titles/.test(x.className)).length, 0, 'no "Shows and movies" list (.bf-titles)');
        // Remembered: a new page opens as stored; a stored "0" stays closed; blocked storage keeps it in memory.
        const open = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        eq([ovOpen(open.document), ovRows(open.document).length], [true, 2], 'a new page opens it when it was left open');
        const shut = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '0' } });
        eq(ovOpen(shut.document), false, 'and keeps it closed when it was left closed');
        const broken = await makeEnv(sample(), { brokenStorage: true });
        eq(ovOpen(broken.document), false, 'blocked storage: closed by default');
        ovButton(broken.document).click();
        eq([ovOpen(broken.document), ovRows(broken.document).length], [true, 2], 'blocked storage: it still opens');
        change(cellSelect(broken.document, 'all', 'audio'), 'lang:ja');
        await clickSave(broken);
        eq([ovOpen(broken.document), ovTitle(broken.document)], [true, 'Show and movie overrides (2)'], 'blocked storage: it stays open after a save draws the page again');
        // The open section stays open through every redraw (Save), and the stored state wins on a new page.
        const again = await makeEnv(sample());
        openOverrides(again.document);
        change(cellSelect(again.document, 'all', 'audio'), 'lang:ja');
        await clickSave(again);
        eq([ovOpen(again.document), ovRows(again.document).length, again.store.get('babelfin-app-group-overrides')], [true, 2, '1'], 'a save draws the page again and the section stays open with its rows');
        // Its rows are not part of the document: the page works the same without ever opening it.
        const never = await makeEnv(sample());
        change(cellSelect(never.document, 'all', 'audio'), 'lang:ja');
        await clickSave(never);
        eq(settingsDiff(sample().Settings, lastPut(never).body, ''), ['Everyday.AudioChain'], 'a save without ever opening the section stores only the edit');
    }

    // 39. Removing one override: at once, DELETE Me/Overrides/{id}?scope=…, no confirmation, no save bar; the page's copy and
    // its baseline lose it; the next Save keeps the stored overrides (keepOverrides) so nothing comes back.
    section('39 removing one override');
    {
        const env = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        const d = env.document;
        change(cellSelect(d, 'all', 'audio'), 'lang:ja');
        check(env.sandbox.BabelfinApp.hasChanges(), 'an edit not saved yet');
        const sable = ovRow(d, 'Sable Voyage');
        eq(sable.remove.getAttribute('aria-label'), 'Remove the override for Sable Voyage', 'the button names its item');
        const before = env.requests.length;
        sable.remove.click();
        await settle();
        check(!d.querySelector('.bfui-dialog'), 'no confirmation');
        const sent = env.requests.slice(before);
        eq(sent.filter((r) => r.method !== 'GET').map((r) => [r.method, r.path, r.query, r.body]), [['DELETE', '/Babelfin/Me/Overrides/' + A_ID, { scope: 'Series' }, undefined]], 'one DELETE with the item and its scope, no body, no manual=true');
        eq(sent.filter((r) => r.method === 'GET').map((r) => r.path), ['/Babelfin/Me/Overrides', '/Babelfin/Me/ChoiceStatus'], 'the list and saved-choice status are read again');
        eq(toasts(d), ['Override removed.'], 'a toast says so (overrides.removed)');
        eq([ovTitle(d), ovNames(d)], ['Show and movie overrides (1)', ['Étoile Lantern (2001)']], 'the count and the rows follow');
        eq(serverIds(env), ['b'], 'the server holds only the other override');
        eq(env.doc().Overrides.map((x) => x.ItemId[0]), ['b'], 'the page\'s copy of the document dropped it');
        check(env.sandbox.BabelfinApp.hasChanges() === true && cellSelect(d, 'all', 'audio').value === 'lang:ja', 'the unsaved edit is still there and still the only change');
        check(d.activeElement === ovRows(d)[0].remove, 'focus moved to the next row\'s Remove');
        // Save: keepOverrides, the removed one does not come back.
        await clickSave(env);
        const put = lastPut(env);
        eq([put.query, bodyIds(put), serverIds(env)], [{ keepOverrides: 'true', fields: 'everyday.audioChain' }, ['b'], ['b']], 'Save sends PUT Me/Settings?keepOverrides=true without the removed override; the server still has one');
        eq(settingsDiff(sample().Settings, put.body, '').sort(), ['Everyday.AudioChain', 'Overrides'], 'and nothing else differs from what was loaded');
        check(env.sandbox.BabelfinApp.hasChanges() === false && saveBarHidden(env), 'saved: nothing left to save');
        eq([ovOpen(d), ovTitle(d), ovNames(d), listGets(env)], [true, 'Show and movie overrides (1)', ['Étoile Lantern (2001)'], 3], 'after the save the section is open with its rows (the list was read after the removal and after the save)');
        eq(toasts(d).slice(-1), ['Saved'], 'and the save says Saved');
    }
    {
        // Nothing else changed: no save bar at all, and Discard keeps the removal.
        const env = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        const d = env.document;
        ovRow(d, 'Étoile Lantern').remove.click();
        await settle();
        check(saveBarHidden(env) && env.sandbox.BabelfinApp.hasChanges() === false, 'removing alone leaves nothing to save');
        eq(deletes(env).map((r) => [r.path, r.query]), [['/Babelfin/Me/Overrides/' + B_ID, { scope: 'Title' }]], 'a remembered track change goes with its row: DELETE with scope Title');
        check(d.activeElement === ovRows(d)[0].remove, 'it was the last row, so focus goes to the previous one');
        change(cellSelect(d, 'all', 'audio'), 'lang:ja');
        d.querySelectorAll('.bf-savebar .bfui-btn').find((b) => b.textContent === 'Discard').click();
        await confirmDialog(env, true);
        eq([env.sandbox.BabelfinApp.hasChanges(), ovNames(d), env.doc().Overrides.map((x) => x.ItemId[0])], [false, ['Sable Voyage'], ['a']], 'Discard puts the edit back and keeps the removal (the baseline lost it too)');
        // Both removed one after the other: the last one's focus goes to the section, the count to 0.
        ovRow(d, 'Sable Voyage').remove.click();
        await settle();
        eq([ovTitle(d), d.activeElement === ovButton(d), !!d.querySelector('.bfui-overrides-empty'), d.querySelector('.bfui-overrides-empty') && d.querySelector('.bfui-overrides-empty').textContent], ['Show and movie overrides (0)', true, true, 'No show or movie overrides yet.'], 'the last row removed: (0), focus on the section\'s heading, the empty line');
        check(!d.querySelector('.bfui-overrides-remove-all') && saveBarHidden(env), 'no Remove all without rows; still nothing to save');
        eq(serverIds(env), [], 'the server has none');
        // The section still shows, and a Save keeps it that way.
        change(cellSelect(d, 'all', 'audio'), 'lang:ja');
        await clickSave(env);
        eq([lastPut(env).query, lastPut(env).body.Overrides, ovTitle(d), ovOpen(d)], [{ keepOverrides: 'true', fields: 'everyday.audioChain' }, [], 'Show and movie overrides (0)', true], 'a save with none left: still keepOverrides, none sent, the section stays');
    }
    {
        // One item with two overrides (two scopes): each row goes on its own, by its scope; the other stays in the list and in the document.
        const settings = quiet({ Overrides: [storedOv(A_ID, 'Series', 'Sable Voyage', { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }] }), storedOv(A_ID, 'Title', 'Sable Voyage', { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' })] });
        const env = await makeEnv(sample({ settings }), { storage: { 'babelfin-app-group-overrides': '1' } });
        const d = env.document;
        eq(ovRows(d).map((r) => [r.scope, r.audio, r.subtitles]), [['Series', 'Japanese', "Library's choice"], ['Title', "Library's choice", 'Off']], 'two overrides for one item: two rows, told apart by scope');
        ovRows(d)[0].remove.click();
        await settle();
        eq(deletes(env).map((r) => [r.path, r.query]), [['/Babelfin/Me/Overrides/' + A_ID, { scope: 'Series' }]], 'removing one sends its own scope');
        eq([ovRows(d).map((r) => r.scope), env.doc().Overrides.map((x) => x.Scope), serverIds(env), ovTitle(d)], [['Title'], ['Title'], ['a'], 'Show and movie overrides (1)'], 'and only that scope goes, from the list, the document and the server');
    }
    {
        // Two clicks on one Remove send one DELETE; the other row's button waits.
        const env = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        const d = env.document;
        const [first, second] = ovRows(d);
        first.remove.click();
        first.remove.click();
        second.remove.click();
        await settle();
        eq(deletes(env).length, 1, 'clicks while a removal is on its way send nothing more');
        eq(ovNames(d), ['Étoile Lantern (2001)'], 'one row went');
    }
    {
        // The server says it is already gone (removed on another device): the row goes all the same.
        const env = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        const d = env.document;
        env.current.Settings.Overrides = env.current.Settings.Overrides.filter((x) => x.ItemId !== A_ID);
        ovRow(d, 'Sable Voyage').remove.click();
        await settle();
        eq(deletes(env).map((r) => r.path), ['/Babelfin/Me/Overrides/' + A_ID], 'the DELETE was sent (and the server answered 404)');
        eq([ovNames(d), errorToasts(d), env.doc().Overrides.map((x) => x.ItemId[0]), ovTitle(d)], [['Étoile Lantern (2001)'], [], ['b'], 'Show and movie overrides (1)'], '404: the row is gone all the same, no error is shown, the page\'s copy dropped it');
        // A server error leaves everything as it was.
        const bad = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        bad.fail['DELETE /Babelfin/Me/Overrides/' + A_ID] = { status: 500 };
        ovRow(bad.document, 'Sable Voyage').remove.click();
        await settle();
        eq([ovNames(bad.document), ovTitle(bad.document), errorToasts(bad.document), bad.doc().Overrides.length, bad.sandbox.BabelfinApp.hasChanges()], [['Sable Voyage', 'Étoile Lantern (2001)'], 'Show and movie overrides (2)', ["That didn't work: failed"], 2, false], '500: the rows, the count and the document stay; an error toast; nothing to save');
        check(ovRows(bad.document).every((r) => !r.remove.disabled), 'and the buttons are on again');
        eq(serverIds(bad), ['a', 'b'], 'the server still has both');
        ovRow(bad.document, 'Sable Voyage').remove.click();
        await settle();
        eq([ovNames(bad.document), serverIds(bad)], [['Étoile Lantern (2001)'], ['b']], 'trying again works');
        // Signed out meanwhile: the sign-in page.
        const out = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        out.fail['DELETE /Babelfin/Me/Overrides/' + A_ID] = { status: 401 };
        ovRow(out.document, 'Sable Voyage').remove.click();
        await settle();
        eq(out.document.querySelector('.bf-message h2') && out.document.querySelector('.bf-message h2').textContent, 'Open this from Jellyfin', '401: the page asks to sign in again');
    }

    // 40. Remove all: under the table, after a confirmation, DELETE Me/Overrides/All; the page's copy and baseline are emptied.
    section('40 remove all overrides');
    {
        const env = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        const d = env.document;
        change(cellSelect(d, 'all', 'subtitles'), 'off');
        d.querySelector('.bfui-overrides-remove-all').click();
        let said = await answerDialog(env, false);
        eq(said, { title: 'Remove all show and movie overrides?', message: "Every show and movie goes back to its library choice. This can't be undone.", buttons: ['Cancel', 'Remove'] }, 'it asks first: the person\'s text, Cancel and Remove');
        eq([deletes(env).length, serverIds(env), ovNames(d).length], [0, ['a', 'b'], 2], 'Cancel sends nothing');
        check(d.querySelector('.bfui-overrides-remove-all') !== null, 'and Remove all is still there');
        d.querySelector('.bfui-overrides-remove-all').click();
        said = await answerDialog(env, true);
        eq(deletes(env).map((r) => [r.path, r.query]), [['/Babelfin/Me/Overrides/All', {}]], 'OK: DELETE Me/Overrides/All, and nothing else is deleted');
        eq([env.removedAll, serverIds(env)], [1, []], 'the server dropped every override');
        eq(toasts(d).slice(-1), ['All show and movie overrides were removed.'], 'a toast says so (overrides.removeAll.done)');
        eq([ovTitle(d), !!d.querySelector('.bfui-overrides-empty'), !!d.querySelector('.bfui-overrides-remove-all'), d.activeElement === ovButton(d)], ['Show and movie overrides (0)', true, false, true], '(0), the empty line, no Remove all, focus on the section\'s heading');
        eq([env.doc().Overrides, env.sandbox.BabelfinApp.hasChanges()], [[], true], 'the page\'s copy is empty and the unsaved edit is still the only change');
        eq(cellSelect(d, 'all', 'subtitles').value, 'off', 'the edit is still shown');
        await clickSave(env);
        eq([lastPut(env).query, lastPut(env).body.Overrides, serverIds(env), env.sandbox.BabelfinApp.hasChanges()], [{ keepOverrides: 'true', fields: 'everyday.whenAudioNotUnderstood,everyday.whenAudioUnderstood' }, [], [], false], 'Save afterwards: none comes back, and the page is clean');
        // The same with nothing unsaved: no save bar at any time.
        const quietEnv = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        quietEnv.document.querySelector('.bfui-overrides-remove-all').click();
        await answerDialog(quietEnv, true);
        check(saveBarHidden(quietEnv) && quietEnv.sandbox.BabelfinApp.hasChanges() === false, 'Remove all alone leaves nothing to save');
        // A failure leaves the rows.
        const bad = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        bad.fail['DELETE /Babelfin/Me/Overrides/All'] = { status: 500 };
        bad.document.querySelector('.bfui-overrides-remove-all').click();
        await answerDialog(bad, true);
        eq([ovNames(bad.document).length, ovTitle(bad.document), errorToasts(bad.document), serverIds(bad), bad.doc().Overrides.length], [2, 'Show and movie overrides (2)', ["That didn't work: failed"], ['a', 'b'], 2], 'a failed Remove all: the rows, the count and the document stay; an error toast');
        check(!bad.document.querySelector('.bfui-overrides-remove-all').disabled && ovRows(bad.document).every((r) => !r.remove.disabled), 'and the buttons are on again');
        // Overrides added from the ⋮ menu while the page was open: the list is read again after the removal, so it shows.
        const late = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        late.current.Settings.Overrides.push(storedOv(D_ID, 'Series', 'Cinder Cub', { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' }));
        ovRow(late.document, 'Sable Voyage').remove.click();
        await settle();
        eq([ovNames(late.document), ovTitle(late.document), deletes(late).map((r) => r.path)], [['Cinder Cub', 'Étoile Lantern (2001)'], 'Show and movie overrides (2)', ['/Babelfin/Me/Overrides/' + A_ID]], 'an override added meanwhile (from the ⋮ menu) shows up after a removal, because the list is read again');
    }

    // 41. Every save keeps the stored overrides: PUT Me/Settings?keepOverrides=true however the save starts.
    section('41 save keeps overrides');
    {
        const env = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        const d = env.document;
        // An override added from the ⋮ menu after the page loaded.
        env.current.Settings.Overrides.push(storedOv(D_ID, 'Series', 'Cinder Cub', { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' }));
        change(cellSelect(d, 'all', 'audio'), 'lang:ja');
        await clickSave(env);
        eq([lastPut(env).query, bodyIds(lastPut(env)), serverIds(env)], [{ keepOverrides: 'true', fields: 'everyday.audioChain' }, ['a', 'b'], ['a', 'b', 'd']], 'the body only holds what the page knew; the server keeps all three');
        eq([ovTitle(d), ovNames(d)], ['Show and movie overrides (3)', ['Cinder Cub', 'Sable Voyage', 'Étoile Lantern (2001)']], 'after the save the page shows the added override too');
        eq(env.doc().Overrides.map((x) => x.ItemId[0]), ['a', 'b', 'd'], 'and holds it in its document (the answer of the save)');
        check(env.sandbox.BabelfinApp.hasChanges() === false, 'nothing left to save');
        // Ctrl+S saves the same way.
        change(cellSelect(d, 'all', 'subtitles'), 'off');
        const ctrlS = new Event('keydown', { bubbles: true, key: 's', ctrlKey: true });
        d.dispatchEvent(ctrlS);
        await settle();
        eq([ctrlS.defaultPrevented, keyed(env).length, lastPut(env).query], [true, 2, { keepOverrides: 'true', fields: 'everyday.whenAudioNotUnderstood,everyday.whenAudioUnderstood' }], 'Ctrl+S: keepOverrides too');
        // The detected-languages "Confirm" button saves the same way.
        const confirm = await makeEnv(sample());
        confirm.document.getElementById('bf-detected').querySelector('button').click();
        await settle();
        eq([keyed(confirm).length, keyed(confirm)[0].query, keyed(confirm)[0].body.Seed.Confirmed], [1, { keepOverrides: 'true', fields: 'seed.confirmed' }, true], 'Confirm (detected languages): keepOverrides too');
        // Every PUT of this suite so far used it.
        const odd = await makeEnv(sample({ settings: quiet() }));
        change(cellSelect(odd.document, 'all', 'audio'), 'lang:ja');
        await clickSave(odd);
        eq([lastPut(odd).query, lastPut(odd).body.Overrides], [{ keepOverrides: 'true', fields: 'everyday.audioChain' }, []], 'with no overrides at all the save still says keepOverrides (an empty list is sent)');
        // A refused save (400) keeps the section as it is and the page dirty.
        const refused = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' }, onPut: () => ({ status: 400, body: { Message: 'x', Errors: [{ Path: 'everyday.whenAudioUnderstood', Message: 'Bad subtitles' }] } }) });
        change(cellSelect(refused.document, 'all', 'subtitles'), 'off');
        await clickSave(refused);
        eq([ovOpen(refused.document), ovNames(refused.document).length, refused.sandbox.BabelfinApp.hasChanges()], [true, 2, true], 'a refused save leaves the section and the page as they were');
        // Restore defaults and Start over.
        const restore = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        restore.document.getElementById('bf-advanced').open = true;
        change(rowByText(restore.document, 'Remember track changes for').querySelector('select'), 'Season');
        restore.document.getElementById('bf-restore-defaults').click();
        await confirmDialog(restore, true);
        eq([ovOpen(restore.document), ovNames(restore.document).length, restore.doc().Overrides.length], [true, 2, 2], 'Restore defaults leaves the section and its overrides alone');
        const over = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
        over.document.getElementById('bf-advanced').open = true;
        group(over.document, 'reset').button.click();
        group(over.document, 'reset').body.querySelector('.bfui-btn-danger').click();
        await confirmDialog(over, true);
        eq([ovTitle(over.document), ovOpen(over.document), listGets(over)], ['Show and movie overrides (0)', true, 2], 'Start over: the page loads again, the list is read again and is empty');
    }

    // 42. The list can't be loaded, or is odd.
    section('42 overrides list: failures');
    {
        const env = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' }, fail: { 'GET /Babelfin/Me/Overrides': { status: 500, sticky: true } } });
        const d = env.document;
        eq(ovTitle(d), 'Show and movie overrides', 'the list failed to load: no count in the title');
        check(!!d.querySelector('.bf-languages-line') && !!cellSelect(d, 'all', 'audio') && !!d.getElementById('bf-advanced'), 'the rest of the page works');
        const body = ovSection(d).querySelector('.bfui-disclosure-body');
        eq(body.children[0].textContent, en['overrides.help'], 'the help line is still there');
        const error = body.querySelector('.bfui-field-error');
        eq([error.textContent, error.getAttribute('role')], ['Something went wrong.', 'alert'], 'and a line saying it went wrong');
        const retry = body.querySelector('.bf-actions button');
        eq(retry.textContent, 'Try again', 'with a Try again button');
        check(!d.querySelector('.bfui-overrides-table') && !d.querySelector('.bfui-overrides-remove-all'), 'no table, no Remove all');
        retry.click();
        await settle();
        eq([ovTitle(d), !!body.querySelector('.bfui-field-error'), ovNames(d).length], ['Show and movie overrides', true, 0], 'Try again while the list still fails: the message stays');
        delete env.fail['GET /Babelfin/Me/Overrides'];
        ovSection(d).querySelector('.bfui-disclosure-body').querySelector('.bf-actions button').click();
        await settle();
        eq([ovTitle(d), ovNames(d)], ['Show and movie overrides (2)', ['Sable Voyage', 'Étoile Lantern (2001)']], 'Try again once it works: the count and the table');
        check(!ovSection(d).querySelector('.bfui-field-error') && env.sandbox.BabelfinApp.hasChanges() === false, 'the message is gone and nothing is unsaved');
        // A save draws the page again and loads the list.
        const later = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' }, fail: { 'GET /Babelfin/Me/Overrides': { status: 500 } } });
        eq(ovTitle(later.document), 'Show and movie overrides', 'one failed read at load');
        change(cellSelect(later.document, 'all', 'audio'), 'lang:ja');
        await clickSave(later);
        eq([ovTitle(later.document), ovNames(later.document)], ['Show and movie overrides (2)', ['Sable Voyage', 'Étoile Lantern (2001)']], 'a save reads the list again and the table appears');
        // Closed and never opened: a failure is not even visible, and a removal can't be tried.
        const closed = await makeEnv(sample(), { fail: { 'GET /Babelfin/Me/Overrides': { status: 503, sticky: true } } });
        eq([ovOpen(closed.document), ovTitle(closed.document), errorToasts(closed.document)], [false, 'Show and movie overrides', []], 'closed: no error toast for a failed list');
        // Signed out or refused: the sign-in page like any other request.
        for (const status of [401, 403]) {
            const refusedEnv = await makeEnv(sample(), { fail: { 'GET /Babelfin/Me/Overrides': { status, sticky: true } } });
            eq(refusedEnv.document.querySelector('.bf-message h2') && refusedEnv.document.querySelector('.bf-message h2').textContent, 'Open this from Jellyfin', status + ' on the list: the sign-in page');
        }
        // An answer that isn't a list counts as none.
        const odd = await makeEnv(sample(), { summaries: () => ({ Message: 'what' }) });
        eq(ovTitle(odd.document), 'Show and movie overrides (0)', 'an answer that is not a list: none');
        const none = await makeEnv(sample(), { summaries: () => null });
        eq(ovTitle(none.document), 'Show and movie overrides (0)', 'an empty answer: none');
        // The count comes from the list, not from the document.
        const apart = await makeEnv(sample(), { summaries: () => [summarize(sample().Settings.Overrides[0])] });
        eq(ovTitle(apart.document), 'Show and movie overrides (1)', 'the count is the list\'s, not the document\'s');
    }

    // 43. Rows without a usable id: disabled, never a request.
    section('43 overrides: rows without an id');
    {
        const odd = (id, name) => Object.assign(summarize(storedOv(A_ID, 'Title', name)), { ItemId: id, Name: name, LibraryName: 'Movies' });
        const rows = () => [odd('', 'Empty id'), odd(null, 'No id'), odd('xyz', 'Short id'), Object.assign(summarize(storedOv(C_ID, 'Series', 'Good one')), { Name: 'Good one', LibraryName: 'Movies' })];
        const env = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' }, summaries: rows });
        const d = env.document;
        eq(ovNames(d), ['Empty id', 'Good one', 'No id', 'Short id'], 'every row is drawn');
        eq(ovRows(d).map((r) => [r.name, r.remove.disabled]), [['Empty id', true], ['Good one', false], ['No id', true], ['Short id', true]], 'a Remove is off for a row without a real id');
        ovRows(d).filter((r) => r.remove.disabled).forEach((r) => { r.remove.click(); });
        await settle();
        eq([deletes(env).length, env.removedAll], [0, 0], 'clicking the disabled ones sends nothing');
        // With ui.js's own guard switched off, the page still refuses: DELETE Me/Overrides/ would remove everything.
        let patched = false;
        const needle = 'return /^[0-9a-f]{32}$/.test(compactId(row.ItemId));';
        const bare = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' }, summaries: rows, patchUi: (src) => { patched = src.indexOf(needle) >= 0; return src.replace(needle, 'return true;'); } });
        check(patched, 'the test switched ui.js\'s guard off (its text is where it was)');
        const bd = bare.document;
        check(ovRows(bd).every((r) => !r.remove.disabled), 'with the guard off every Remove is on');
        ovRows(bd).filter((r) => r.name !== 'Good one').forEach((r) => r.remove.click());
        await settle();
        eq([deletes(bare).length, bare.removedAll, errorToasts(bd).length > 0, ovNames(bd).length], [0, 0, true, 4], 'the page itself refuses a row without an id: no DELETE, an error shown, the rows stay');
        ovRows(bd).find((r) => r.name === 'Good one').remove.click();
        await settle();
        eq(deletes(bare).map((r) => r.path), ['/Babelfin/Me/Overrides/' + C_ID], 'a row with a real id is still removed by id');
    }

    // 44. The table's words follow My languages and Languages for subtitles as edited, saved or not.
    section('44 overrides: words follow the languages');
    {
        const settings = quiet({
            Overrides: [storedOv(E_ID, 'Series', 'Glass Harbor', { AudioChain: [{ Kind: 'MyLanguages' }, { Kind: 'FileDefault' }], WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full' })]
        });
        const env = await makeEnv(sample({ settings }), { storage: { 'babelfin-app-group-overrides': '1' } });
        const d = env.document;
        const words = () => ovRows(d).map((r) => [r.audio, r.subtitles]);
        eq([ovNames(d), words()], [['Glass Harbor (2021)'], [['English', 'English']]], 'one language each: that language');
        d.querySelector('.bf-languages-line button').click();
        const input = d.querySelector('.bf-languages-editor .bfui-picker input');
        input.value = 'span';
        input.dispatchEvent(new Event('input'));
        d.querySelector('.bf-languages-editor').querySelectorAll('li').find((li) => li.dataset.code === 'es').click();
        await settle();
        eq(words(), [['Custom', 'English']], 'My languages now English and Spanish: "My languages first" is Custom; the subtitles follow Languages for subtitles');
        check(env.sandbox.BabelfinApp.hasChanges() && saveBarHidden(env) === false, 'the edit is unsaved');
        d.querySelector('.bf-languages-editor').querySelectorAll('.bfui-remove')[0].click();
        await settle();
        eq(words(), [['Spanish', 'English']], 'English taken out: Spanish');
        // Languages for subtitles (Advanced).
        d.getElementById('bf-advanced').open = true;
        group(d, 'reads').button.click();
        const readInput = group(d, 'reads').body.querySelector('.bfui-picker input');
        readInput.value = 'germ';
        readInput.dispatchEvent(new Event('input'));
        group(d, 'reads').body.querySelectorAll('li').find((li) => li.dataset.code === 'de').click();
        await settle();
        eq(words(), [['Spanish', 'Custom']], 'Languages for subtitles now English and German: Custom');
        group(d, 'reads').body.querySelectorAll('.bfui-remove')[0].click();
        await settle();
        eq(words(), [['Spanish', 'German']], 'English taken out: German');
        eq(deletes(env).length, 0, 'none of this sent a request');
        // Discard puts the words back.
        d.querySelectorAll('.bf-savebar .bfui-btn').find((b) => b.textContent === 'Discard').click();
        await confirmDialog(env, true);
        eq(words(), [['English', 'English']], 'Discard: the words are English again');
        check(ovOpen(d), 'and the section stays open');
    }

    // 45. The section in every translation: its words come from the file, no key shows, Remove all asks in the language.
    section('45 overrides: translations');
    for (const lang of fs.readdirSync(ASSETS + 'i18n').filter((f) => f.endsWith('.json')).map((f) => f.slice(0, -5))) {
        const table = JSON.parse(fs.readFileSync(ASSETS + 'i18n/' + lang + '.json', 'utf8'));
        const env = await makeEnv(sample(), { lang, storage: { 'babelfin-app-group-overrides': '1' } });
        const d = env.document;
        const sec = ovSection(d);
        eq(ovTitle(d), table['overrides.title'] + ' (2)', lang + ': the title and its count');
        eq(sec.querySelector('.bf-overrides-help').textContent, table['overrides.help'], lang + ': the help line');
        eq(sec.querySelectorAll('thead th').map((th) => th.textContent), [table['overrides.col.item'], table['overrides.col.library'], table['overrides.col.audio'], table['overrides.col.subtitles'], table['common.remove']], lang + ': the columns');
        const rows = ovRows(d);
        eq(rows.map((r) => r.library), ['Anime', 'Movies'], lang + ': library names are the server\'s');
        eq(rows[1].note, table['overrides.remembered'], lang + ': the remembered note');
        eq(rows.map((r) => r.remove.getAttribute('aria-label')), [table['overrides.removeFor'].split('{name}').join(rows[0].name), table['overrides.removeFor'].split('{name}').join(rows[1].name)], lang + ': Remove buttons are named in the language');
        eq(rows.map((r) => r.remove.textContent), [table['common.remove'], table['common.remove']], lang + ': Remove');
        eq(sec.querySelector('.bfui-overrides-remove-all').textContent, table['overrides.removeAll'], lang + ': Remove all');
        const rawKey = /(^|\s)(overrides|ui|common|titles)\.[A-Za-z]/;
        eq(visibleStrings(sec, true).filter((s) => rawKey.test(s)), [], lang + ': no key shows');
        sec.querySelector('.bfui-overrides-remove-all').click();
        const said = await answerDialog(env, false);
        eq(said && [said.title, said.message, said.buttons[1]], [table['overrides.removeAll.confirmTitle'], table['overrides.removeAll.confirmText'], table['common.remove']], lang + ': Remove all asks in the language');
        // Closed section in another language: empty, then the line.
        const empty = await makeEnv(sample({ settings: quiet() }), { lang, storage: { 'babelfin-app-group-overrides': '1' } });
        eq(empty.document.querySelector('.bfui-overrides-empty').textContent, table['overrides.empty'], lang + ': the empty line');
        // Removing says so in the language.
        ovRows(d)[0].remove.click();
        await settle();
        eq(toasts(d).slice(-1), [table['overrides.removed']], lang + ': the toast after a removal');
    }
    {
        // Strings: the retired keys are gone and the new ones are everywhere (all 16 files), translated.
        const RETIRED = ['titles.title', 'titles.remembered.title', 'titles.remembered.confirm.title', 'titles.remembered.confirm.text', 'titles.remembered.removed',
            'titles.summary.audioLanguage', 'titles.summary.audioOriginal', 'titles.summary.audioMine', 'titles.summary.audioDub', 'titles.summary.audioVideo', 'titles.summary.audioFileDefault',
            'titles.summary.audioFirst', 'titles.summary.subtitles', 'titles.summary.subtitlesOff', 'titles.summary.subtitlesVideo', 'titles.summary.subtitlesFull', 'titles.summary.subtitlesSigns',
            'titles.summary.subtitlesSmart', 'titles.summary.subtitlesCustom', 'titles.summary.other',
            'dialog.applyTo', 'dialog.chain', 'dialog.custom', 'dialog.inherit', 'dialog.off', 'dialog.thisCollection', 'dialog.thisEpisode', 'dialog.thisMovie', 'dialog.thisSeason', 'dialog.wholeShow',
            'languages.info'];
        const NEW_KEYS = ['client.menu.override', 'dialog.applies.show', 'dialog.applies.movie', 'dialog.applies.showNoLibrary', 'dialog.applies.movieNoLibrary', 'dialog.narrower', 'dialog.remove', 'dialog.removed', 'dialog.errorBurnedIn',
            'overrides.title', 'overrides.help', 'overrides.helpAdmin', 'overrides.empty', 'overrides.col.item', 'overrides.col.library', 'overrides.col.audio', 'overrides.col.subtitles', 'overrides.remembered', 'overrides.noLibrary',
            'overrides.notSet', 'overrides.removeFor', 'overrides.removeAll', 'overrides.removeAll.confirmTitle', 'overrides.removeAll.confirmText', 'overrides.removeAll.confirmTextFor', 'overrides.removeAll.done', 'overrides.removed'];
        // Still used: the burned-in list and the remembered-track words.
        const KEPT = ['titles.removeFor', 'titles.scope.episode', 'titles.scope.movie', 'titles.scope.title', 'titles.scope.season', 'titles.scope.show', 'titles.scope.collection',
            'titles.summary.commentary', 'titles.summary.audioDescription', 'titles.summary.subtitlesPartial', 'titles.summary.subtitlesSdh', 'titles.summary.unknownLanguage'];
        const PLACEHOLDERS = { 'dialog.applies.show': ['{library}'], 'dialog.applies.movie': ['{library}'], 'overrides.removeFor': ['{name}'], 'overrides.removeAll.confirmTextFor': ['{name}'] };
        for (const f of fs.readdirSync(ASSETS + 'i18n').filter((x) => x.endsWith('.json'))) {
            const table = JSON.parse(fs.readFileSync(ASSETS + 'i18n/' + f, 'utf8'));
            eq(RETIRED.filter((k) => k in table), [], f + ': the retired keys are gone');
            eq(KEPT.concat(NEW_KEYS).filter((k) => typeof table[k] !== 'string' || !table[k].trim()), [], f + ': the new and the kept keys are all there');
            Object.keys(PLACEHOLDERS).forEach((k) => PLACEHOLDERS[k].forEach((p) => check(String(table[k]).indexOf(p) >= 0, f + ': ' + k + ' keeps ' + p)));
            if (f !== 'en.json') {
                eq(NEW_KEYS.filter((k) => k !== 'overrides.col.audio' && table[k] === en[k]), [], f + ': the new strings are translated (Audio may read the same)');
            }
        }
        eq([en['overrides.title'], en['overrides.removeAll'], en['overrides.empty'], en['overrides.notSet'], en['overrides.noLibrary'], en['overrides.remembered'], en['overrides.removed']],
            ['Show and movie overrides', 'Remove all show and movie overrides', 'No show or movie overrides yet.', "Library's choice", 'Not in a library', 'Remembered from a track change', 'Override removed.'], 'the English words of');
        eq([en['overrides.col.item'], en['overrides.col.library'], en['overrides.col.audio'], en['overrides.col.subtitles'], en['overrides.removeFor'], en['overrides.removeAll.confirmTitle'], en['overrides.removeAll.done']],
            ['Show or movie', 'Library', 'Audio', 'Subtitles', 'Remove the override for {name}', 'Remove all show and movie overrides?', 'All show and movie overrides were removed.'], 'and the rest of them');
        eq([en['overrides.removeAll.confirmText'], en['overrides.removeAll.confirmTextFor']], ["Every show and movie goes back to its library choice. This can't be undone.", "Every show and movie goes back to {name}'s library choices. This can't be undone."], 'the two confirmation texts');
        eq([en['dialog.applies.show'], en['dialog.applies.movie'], en['dialog.applies.showNoLibrary'], en['dialog.applies.movieNoLibrary'], en['dialog.remove'], en['dialog.removed'], en['client.menu.override']],
            ["Applies to the whole show, in place of the {library} library's choice.", "Applies to this movie, in place of the {library} library's choice.", "Applies to the whole show, in place of the library's choice.", "Applies to this movie, in place of the library's choice.", 'Remove override', 'Override removed', 'Audio and subtitles (Babelfin)'], 'the dialog and menu words of');
        eq(en['dialog.narrower'], 'An episode or season of this show has its own override, which comes first for it. It is listed under Show and movie overrides in Babelfin Settings.', 'dialog.narrower');
        // The words other places use for these.
        check(en['advanced.trackChanges.help'].indexOf('Show and movie overrides') >= 0 && en['advanced.trackChanges.help'].indexOf('Shows and movies') < 0, 'Track changes points to "Show and movie overrides", not to "Shows and movies"');
        check(/show and movie overrides/.test(en['startOver.confirm.text']) && /show and movie overrides/.test(en['restore.confirm.text']), 'Start over and Restore defaults name the overrides in their confirmations');
        eq(Object.keys(en).filter((k) => typeof en[k] === 'string' && /Shows and movies/.test(en[k])), [], 'no string still says "Shows and movies"');
    }
/* <<< page */

/* >>> wording */
    // 46. Wording: no UI text describes the person with "understand" or "read" ("I understand", "you read", "languages you read",
    // "understood" and the like). The only places those words may appear are about Babelfin or a file, not about a person: the admin's
    // Exceptions describe track titles, a file, and stored settings. Checked in both English files and everything the page shows.
    section('46 no "understand" or "read" about the person');
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
        // The rule itself: what it must catch.
        ['I understand English', 'Languages you understand', 'Languages I read', 'the languages you read', 'Treat audio in an unknown language as understood', 'Reads', 'A language they can read', 'understanding'].forEach((s) => check(RULE.test(s), 'the rule catches "' + s + '"'));
        ['Languages for subtitles', 'When the audio\'s language is unknown', 'Already', 'Spread', 'Thread', 'The file couldn\'t be loaded', 'Unread items'].forEach((s) => check(!RULE.test(s), 'the rule leaves "' + s + '" alone'));
        // Everything the page shows, in every mode, with everything open: texts, options, labels, the explanations behind the "i" buttons.
        for (const [label, settings] of [['Same', undefined], ['Each', each()]]) {
            const env = await makeEnv(sample({ defaults: OPINIONS(), advanced: ADMIN_PROFILE, settings }), { storage: { 'babelfin-app-group-overrides': '1' } });
            const d = env.document;
            openAll(d);
            d.querySelector('.bf-languages-line button').click();
            const strings = visibleStrings(d.body, true)
                .concat(d.querySelectorAll('option').map((o) => o.textContent))
                .concat(d.querySelectorAll('.bfui-tip').map((x) => x.textContent))
                .concat(d.querySelectorAll('[aria-label]').map((x) => x.getAttribute('aria-label')));
            check(strings.length > 200, label + ': the scan saw the page (' + strings.length + ' strings)');
            eq(strings.filter((s) => RULE.test(s)), [], label + ': nothing the page shows says "understand" or "read"');
            check(strings.some((s) => /^Show and movie overrides \(\d+\)$/.test(s)) && strings.some((s) => s === 'Languages for subtitles') && strings.some((s) => s === 'My languages'), label + ': and it included the overrides section, Languages for subtitles and My languages');
        }
        // Dialogs and messages: Remove all, Restore defaults, Start over, Discard, and the toasts.
        {
            const env = await makeEnv(sample(), { storage: { 'babelfin-app-group-overrides': '1' } });
            const d = env.document;
            const said = [];
            const ask = async (click) => {
                click();
                await settle();
                const dialog = d.querySelector('.bfui-dialog');
                if (dialog) {
                    said.push(dialog.textContent);
                    dialog.querySelectorAll('button')[0].click();
                }
                await settle();
            };
            await ask(() => d.querySelector('.bfui-overrides-remove-all').click());
            d.getElementById('bf-advanced').open = true;
            await ask(() => d.getElementById('bf-restore-defaults').click());
            group(d, 'reset').button.click();
            await ask(() => group(d, 'reset').body.querySelector('.bfui-btn-danger').click());
            change(cellSelect(d, 'all', 'audio'), 'lang:ja');
            await ask(() => d.querySelectorAll('.bf-savebar .bfui-btn').find((b) => b.textContent === 'Discard').click());
            check(said.length === 4, 'four dialogs were read (' + said.length + ')');
            eq(said.filter((s) => RULE.test(s)), [], 'no dialog says "understand" or "read"');
            ovRow(d, 'Sable Voyage').remove.click();
            await settle();
            eq(toasts(d).concat(errorToasts(d)).filter((s) => RULE.test(s)), [], 'nor does a toast');
        }
        // The page's own hard-coded English (no translator): nothing in the scripts' string literals either.
        const literals = [];
        for (const f of fs.readdirSync(ASSETS).filter((x) => x.endsWith('.js'))) {
            const code = fs.readFileSync(ASSETS + f, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"\\])\/\/[^\n]*/g, '$1');
            for (const m of code.matchAll(/'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"/g)) {
                const text = m[1] !== undefined ? m[1] : m[2];
                if (/\s/.test(text) && RULE.test(text)) { literals.push(f + ': ' + text.slice(0, 80)); }
            }
        }
        eq(literals, [], 'no script has a sentence in a string literal that says "understand" or "read"');
    }
/* <<< wording */

    section('47 shared component contracts');
    {
        const runtime = loadUi();
        await require('../shared/contracts')({ ...runtime, Event, t: T, check, eq });
    }

    section('48 person genres use the existing save flow');
    {
        const hidden = { Id: 'unrelated', Name: 'Other stored rule', Enabled: false, Conditions: [], Settings: { FutureField: true } };
        const initial = sample(); initial.Settings.SmartRules.push(hidden);
        const env = await makeEnv(initial), d = env.document;
        const genres = () => d.querySelector('section.bfui-genres');
        const add = () => genres().querySelectorAll('button').find(b => b.parentNode === genres() && b.textContent === en['ui.genres.add']);
        const rows = () => genres().querySelector('tbody').children;
        const gets = () => env.requests.filter(r => r.path === '/Genres');
        check(d.querySelector('.bfui-table-wrap').nextSibling === genres(), 'person genre section is directly below the library table');
        eq([gets().length, putCount(env), rows().map(r => r.querySelector('th').textContent)], [0, 0, ['Kids']], 'person genres start lazy with the stored recognized rule');
        add().click(); await settle();
        eq(gets().map(r => [r.method, r.query.UserId]), [['GET', 'u1']], 'person catalogue targets the active person with exact UserId query');
        const pickerBox = genres().querySelector('.bfui-genres-picker'), picker = pickerBox.querySelector('select');
        eq([picker.value, pickerBox.querySelector('button').disabled], ['', true], 'opening the catalogue selects nothing');
        change(picker, 'Anime'); pickerBox.querySelector('button').click();
        const nodes = rows()[1].querySelectorAll('select');
        change(nodes[0], 'mine'); change(nodes[1], 'off');
        check(rows()[1].querySelectorAll('select').every((node, i) => node === nodes[i]), 'person genre cell edits retain the selects');
        eq([putCount(env), env.sandbox.BabelfinApp.hasChanges()], [0, true], 'person genre edits wait for the existing Save');
        rows()[1].querySelector('[data-action=up]').click();
        rows()[1].querySelector('[data-action=remove]').click();
        eq(env.doc().SmartRules.filter(env.UI.isGenreRow).map(r => r.Name), ['Anime'], 'person genre add, reorder and removal update the working document');
        eq(env.doc().SmartRules.find(r => r.Id === hidden.Id), hidden, 'person genre edits preserve unrecognized stored rules');
        await clickSave(env);
        const put = lastPut(env);
        eq(put.query, { keepOverrides: 'true', fields: 'smartRules' }, 'person genre save expresses only the changed array and keeps show and movie overrides');
        const saved = put.body.SmartRules.find(env.UI.isGenreRow);
        eq(saved, { Id: '', Name: 'Anime', Enabled: true, Match: 'All', Conditions: [{ Kind: 'Genre', Values: ['Anime'], Negate: false, IsRegex: false }], Settings: { AudioChain: [{ Kind: 'MyLanguages' }, { Kind: 'VideoDefault' }], WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' } }, 'person Save persists the exact genre rule and both table columns');
        eq(put.body.SmartRules.find(r => r.Id === hidden.Id), hidden, 'person genre Save preserves unrecognized rules');
        const reload = await makeEnv(clone(env.current));
        eq(reload.document.querySelector('section.bfui-genres tbody').querySelectorAll('select').map(n => n.value), ['mine', 'off'], 'person genre choices survive a complete reload');
        change(rows()[0].querySelector('select'), 'original');
        d.querySelectorAll('.bf-savebar .bfui-btn').find(b => b.textContent === 'Discard').click();
        await confirmDialog(env, true);
        eq([putCount(env), env.sandbox.BabelfinApp.hasChanges(), rows()[0].querySelector('select').value], [1, false, 'mine'], 'Discard restores saved person genre choices without another write');
        eq(env.requests.filter(r => r.method !== 'GET').map(r => [r.method, r.path]), [['PUT', '/Babelfin/Me/Settings']], 'all person genre writes use only the existing settings Save');
    }

    section('49 optional person choice-status endpoint');
    {
        let resolve;
        const pending = new Promise(done => { resolve = done; });
        const env = await makeEnv(sample({ settings: { Enabled: false } }), { choiceStatus: () => pending, ping: { enabled: false } });
        const status = () => env.document.querySelector('.bf-status [role=status]');
        check(!!env.document.querySelector('main') && status().hidden, 'pending optional status leaves the person page usable and silent');
        eq(env.requests.filter(r => /ChoiceStatus$/.test(r.path)).map(r => [r.method, r.path, r.query]), [['GET', '/Babelfin/Me/ChoiceStatus', {}]], 'person uses the exact optional status endpoint');
        check(env.document.querySelector('.bf-status').textContent.includes(en['app.serverOff']) && env.document.querySelector('.bf-status').textContent.includes(en['app.personOff']), 'server-off and person-off messages survive the pending status');
        resolve({ HasChoices: false }); await settle();
        eq([status().hidden, status().textContent], [false, en['app.noChoices']], 'saved HasChoices false shows the localized person status');
        change(cellSelect(env.document, 'all', 'audio'), 'mine');
        check(!status().hidden, 'unsaved choices do not change the saved-choice status');
        env.choiceStatus = { HasChoices: true }; await clickSave(env);
        check(status().hidden, 'saving refreshes the person status from the server');
        for (const answer of [{}, null, { HasChoices: 'false' }]) {
            const unknown = await makeEnv(sample(), { choiceStatus: answer });
            check(unknown.document.querySelector('.bf-status [role=status]').hidden && !!unknown.document.querySelector('main'), 'unknown optional person status stays silent and nonblocking');
        }
        for (const code of [401, 403, 404, 500]) {
            const bad = await makeEnv(sample(), { fail: { 'GET /Babelfin/Me/ChoiceStatus': { status: code, sticky: true, message: 'RAW STATUS ERROR: private detail' } } });
            check(!!bad.document.querySelector('main') && bad.document.querySelector('.bf-status [role=status]').hidden, code + ' from optional person status does not replace the page');
            check(!visibleStrings(bad.document.body, true).some(s => s.includes('RAW STATUS ERROR')), code + ' status exposes no raw error');
        }
    }

    console.log('\nby section:');
    sections.forEach((s, i) => {
        const end = i + 1 < sections.length ? sections[i + 1].start : passes + failures;
        const failEnd = i + 1 < sections.length ? sections[i + 1].failStart : failures;
        console.log('  ' + s.name + ': ' + (end - s.start) + ' checks' + (failEnd - s.failStart ? ', ' + (failEnd - s.failStart) + ' failed' : ''));
    });
    console.log('\n' + passes + ' passed, ' + failures + ' failed');
    process.exit(failures ? 1 : 0);
})().catch((e) => {
    console.error(e);
    process.exit(2);
});
