"use strict";
// Complete shipped scripts on the maintained fake DOM; no network or production server.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');
const { Document, Event } = require('./ui-smoke/person/fakedom');
const assets = process.env.BF_ASSETS || path.resolve(__dirname, '../../plugin/src/Jellyfin.Plugin.Babelfin/Assets');
const source = name => fs.readFileSync(path.join(assets, name), 'utf8');
const copy = value => JSON.parse(JSON.stringify(value));
const settle = async () => { for (let i = 0; i < 25; i++) await new Promise(resolve => setImmediate(resolve)); };
const words = JSON.parse(source('i18n/en.json'));
function translator(table = words) {
    const t = (key, params = {}) => String(table[key] || key).replace(/\{(\w+)\}/g, (all, key) => params[key] === undefined ? all : params[key]);
    t.has = key => Object.hasOwn(table, key);
    t.language = 'en';
    return t;
}
function instrument(name, hook) {
    const text = source(name);
    const end = /\}\)\(\);\s*$/;
    assert.match(text, end);
    return text.replace(end, hook + '\n})();');
}
function environment(DocumentClass = Document) {
    const document = new DocumentClass();
    const timers = [], frames = [], logs = [], observers = [];
    const window = {
        document, console: Object.fromEntries(['info', 'warn', 'error', 'log'].map(name => [name, (...args) => logs.push([name, ...args])])),
        Promise, URL, URLSearchParams, Intl, Event, DOMException, AbortController,
        navigator: { language: 'en' },
        location: new URL('https://media.example/jf/Babelfin/app'),
        history: { length: 1, back() {} },
        innerWidth: 1024, innerHeight: 768, scrollY: 0, scrollTo() {},
        listeners: {},
        addEventListener(name, fn) { (this.listeners[name] ||= []).push(fn); },
        removeEventListener(name, fn) { this.listeners[name] = (this.listeners[name] || []).filter(f => f !== fn); },
        setTimeout(fn, ms) { timers.push({ fn, ms }); return timers.length; }, clearTimeout() {},
        setInterval() { return 1; }, clearInterval() {},
        requestAnimationFrame(fn) { frames.push(fn); },
        MutationObserver: class {
            constructor(fn) { this.fn = fn; }
            observe(node, options) { observers.push({ node, options }); }
        }
    };
    window.window = window; window.top = window; window.parent = window;
    document.defaultView = window;
    const store = new Map();
    window.localStorage = { getItem: key => store.get(key) || null, setItem: (key, value) => store.set(key, value) };
    window.fetch = async () => { throw new Error('Unexpected fetch'); };
    const context = vm.createContext(window);
    return { window, document, context, store, timers, frames, logs, observers };
}
async function appBrowser({ embedded = false, signedOut = false, staleParent = false, DocumentClass = Document } = {}) {
    const env = environment(DocumentClass);
    const { window, document, context, store } = env;
    let token = 'expired', userId = 'person-one', rejectPut = true, pingStatus = 200, reauthenticated = false;
    const calls = [];
    const saved = {
        Settings: {
            SchemaVersion: 1, UserId: userId, Enabled: true, HandleNextEpisode: false,
            Everyday: { Understood: ['en'], Reads: ['en'], UnlabelledAudio: 'Unknown' },
            Libraries: [], SmartRules: [], Overrides: [], CollectionOrder: [], BurnedIn: [], Locks: {}
        },
        Inherited: { Profile: { Understood: ['en'], Reads: ['en'], AudioChain: [{ Kind: 'VideoDefault' }], WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' } },
        Libraries: [], Assumed: []
    };
    const saveCredentials = () => store.set('jellyfin_credentials', JSON.stringify({ Servers: [{
        AccessToken: token, UserId: userId, ManualAddress: 'https://media.example/jf'
    }] }));
    if (!signedOut) saveCredentials();
    async function respond(url, init = {}) {
        const address = new URL(url, window.location.origin);
        const route = address.pathname.replace(/^\/jf/, '');
        const method = init.method || 'GET';
        calls.push({ route, method, token: init.headers && init.headers.Authorization, headers: init.headers, body: init.body && JSON.parse(init.body), query: Object.fromEntries(address.searchParams) });
        let status = 200, body;
        if (route === '/Babelfin/Ping') { status = reauthenticated && /expired/.test(init.headers.Authorization) && pingStatus === 200 ? 401 : pingStatus; body = { version: 'test' }; }
        else if (route === '/Babelfin/Languages') body = ['en', 'de', 'fr'].map(Code => ({ Code, Name: Code, Base: Code }));
        else if (route === '/Babelfin/Me/Overrides' && method === 'GET') body = [];
        else if (route === '/Babelfin/Me/Overrides/All' && method === 'DELETE') body = null;
        else if (/^\/Babelfin\/Me\/Overrides\/[a-f0-9]{32}$/.test(route) && method === 'DELETE') body = null;
        else if (route === '/Babelfin/Me/ChoiceStatus') body = { HasChoices: true };
        else if (route === '/Babelfin/Me/Settings' && method === 'PUT') {
            status = rejectPut ? 401 : 200;
            if (!rejectPut) { saved.Settings = JSON.parse(init.body); delete saved.Settings.ExpectedValues; }
            body = saved;
        } else if (route === '/Babelfin/Me/Settings') body = saved;
        else throw new Error('Unexpected route ' + method + ' ' + route);
        if (status >= 400) body = { Message: 'Sign in again' };
        return { status, ok: status < 400, text: async () => JSON.stringify(copy(body)), json: async () => copy(body) };
    }
    window.fetch = respond;
    if (embedded) {
        const makeClient = () => {
            const clientUser = userId, clientToken = token;
            return {
            getCurrentUserId: () => clientUser,
            getUrl: (route, query) => 'https://media.example/jf/' + route + (query ? '?' + new URLSearchParams(query) : ''),
            async ajax(options) {
                const reply = await respond(options.url, { method: options.type, body: options.data, headers: Object.assign({ Authorization: 'parent-' + clientToken }, options.headers) });
                if (!reply.ok) throw reply;
                return reply.text();
            }
        }; };
        window.parent = window.top = { location: { origin: window.location.origin }, ApiClient: makeClient() };
        env.refreshParent = () => { window.parent.ApiClient = makeClient(); };
    }
    document.readyState = 'loading';
    const root = document.createElement('div'); root.id = 'bf-app'; document.body.appendChild(root);
    window.BabelfinI18n = { detectLanguage: () => 'en', load: async () => translator() };
    vm.runInContext(source('ui.js'), context, { filename: 'ui.js' });
    vm.runInContext(instrument('app.js', 'window.testApp = { state: state, save: save, renderPage: renderPage, removeAllOverrides: removeAllOverrides, removeOverride: removeOverride, load: load, discard: discard, boot: boot, start: start, setT: function(v) { t = v; }, tvKey: typeof tvKey === "function" ? tvKey : null };'), context, { filename: 'app.js' });
    document.dispatchEvent(new Event('DOMContentLoaded'));
    await settle();
    return Object.assign(env, {
        calls, app: window.testApp,
        signIn(nextUser = userId) { token = 'renewed'; userId = nextUser; reauthenticated = true; saveCredentials(); if (env.refreshParent && !staleParent) env.refreshParent(); },
        allowSave() { rejectPut = false; },
        pingFails(status) { pingStatus = status; }
    });
}
function retry(env) {
    const button = env.document.querySelectorAll('.bf-message button').find(n => n.textContent === words['app.retry']);
    assert.ok(button, 'Retained draft offers Try again');
    button.click();
}
function settingsReads(env) { return env.calls.filter(c => c.method === 'GET' && c.route === '/Babelfin/Me/Settings').length; }

for (const [embedded, staleParent] of [[false, false], [true, false], [true, true]]) test(`Save 401 retains the complete draft through ${staleParent ? 'a stale parent ApiClient' : embedded ? 'parent ApiClient' : 'standalone'} reauthentication`, { timeout: 3000 }, async () => {
    const env = await appBrowser({ embedded, staleParent });
    const state = env.app.state;
    assert.ok(state.doc, 'Initial settings loaded');
    const baseline = copy(state.base);
    const audio = env.document.querySelectorAll('.bfui-table select').find(n => n.options.some(o => o.value === 'original'));
    assert.ok(audio);
    audio.value = 'original'; audio.dispatchEvent(new Event('change', { bubbles: true }));
    state.doc.Everyday.SubtitleLanguages = ['de', 'fr'];
    state.doc.HandleNextEpisode = true;
    const draft = copy(state.doc);
    await env.app.save();
    assert.deepEqual(copy(state.doc), draft);
    assert.deepEqual(copy(state.base), baseline);
    assert.equal(state.saving, false);
    assert.equal(env.window.BabelfinApp.hasChanges(), true);
    const signIn = env.document.querySelector('.bf-message a');
    assert.equal(signIn.getAttribute('target'), '_blank', 'Signing in leaves the draft tab/frame alive');
    assert.equal(signIn.getAttribute('rel'), 'noopener');
    const reads = settingsReads(env);
    env.signIn(); retry(env); await settle();
    assert.deepEqual(copy(state.doc), draft);
    assert.deepEqual(copy(state.base), baseline);
    assert.equal(settingsReads(env), reads, 'Reauthentication must not load over the draft');
    assert.ok(env.document.querySelector('.bfui-table'));
    assert.equal(env.window.BabelfinApp.hasChanges(), true);
    env.allowSave(); await env.app.save(); await settle();
    const writes = env.calls.filter(c => c.method === 'PUT');
    assert.equal(writes.length, 2);
    assert.deepEqual(writes[1].body, Object.assign({}, draft, { ExpectedValues: {
        'everyday.audioChain': null, 'everyday.subtitleLanguages': null, handleNextEpisode: false
    } }));
    assert.deepEqual(writes[1].body, writes[0].body, 'Retry preserves the complete draft and original conflict expectations');
    assert.equal(writes[1].headers['X-Babelfin-Expected'], undefined);
    assert.deepEqual(writes[1].query, { keepOverrides: 'true', fields: 'everyday.audioChain,everyday.subtitleLanguages,handleNextEpisode' });
    assert.equal(writes[1].token, embedded && !staleParent ? 'parent-renewed' : 'MediaBrowser Token="renewed"');
    assert.equal(env.window.BabelfinApp.hasChanges(), false);
});

test('A different signed-in person cannot resume or save the retained draft', { timeout: 3000 }, async () => {
    const env = await appBrowser();
    env.app.state.doc.Enabled = false;
    const draft = copy(env.app.state.doc);
    await env.app.save();
    const calls = env.calls.length;
    env.signIn('person-two'); retry(env); await settle();
    await env.app.save();
    assert.equal(env.calls.length, calls, 'No authenticated request or Save for the wrong person');
    assert.deepEqual(copy(env.app.state.doc), draft);
    assert.ok(env.document.querySelector('.bf-message'));
    env.signIn('person-one'); retry(env); await settle();
    assert.ok(env.document.querySelector('.bfui-table'));
    assert.deepEqual(copy(env.app.state.doc), draft);
});

test('Repeated sign-in failures and double retry keep the draft and baseline', { timeout: 3000 }, async () => {
    const env = await appBrowser();
    env.app.state.doc.Enabled = false;
    const draft = copy(env.app.state.doc), baseline = copy(env.app.state.base);
    await env.app.save();
    env.signIn(); env.pingFails(401);
    const before = env.calls.filter(c => c.route === '/Babelfin/Ping').length;
    retry(env); retry(env); await settle();
    assert.equal(env.calls.filter(c => c.route === '/Babelfin/Ping').length, before + 1);
    assert.deepEqual(copy(env.app.state.doc), draft);
    assert.deepEqual(copy(env.app.state.base), baseline);
    assert.equal(env.app.state.saving, false);
    env.pingFails(500); retry(env); await settle();
    assert.deepEqual(copy(env.app.state.doc), draft);
    env.pingFails(200); retry(env); await settle();
    assert.deepEqual(copy(env.app.state.doc), draft);
    assert.equal(settingsReads(env), 1);
});

test('Initial signed-out screen preserves the existing Jellyfin navigation', { timeout: 3000 }, async () => {
    const env = await appBrowser({ signedOut: true });
    assert.equal(env.document.querySelector('.bf-message a').getAttribute('href'), '/jf/web/');
    assert.equal(env.document.querySelector('.bf-message a').getAttribute('target'), null);
    assert.equal(env.document.querySelectorAll('.bf-message button').length, 0);
});

function clientBrowser({ pendingBody = false } = {}) {
    const env = environment();
    const { document, window, context } = env;
    document.currentScript = { src: 'https://media.example/jf/Babelfin/client.js?v=test' };
    window.location.hash = '#/home';
    const body = document.body;
    if (pendingBody) document.body = null;
    const run = () => vm.runInContext(instrument('client.js', 'window.testClient = { renderDialog: renderDialog, state: state, tableApi: tableApi };'), context, { filename: 'client.js' });
    run();
    return Object.assign(env, { run, body });
}
for (const pendingBody of [false, true]) test(`Client double load installs one lifecycle ${pendingBody ? 'before DOM readiness' : 'after DOM readiness'}`, () => {
    const env = clientBrowser({ pendingBody });
    const first = env.window.Babelfin, internals = env.window.testClient;
    env.run();
    assert.equal(env.window.Babelfin, first);
    assert.equal(env.window.testClient, internals, 'Second evaluation exits before initialization');
    if (pendingBody) {
        assert.equal(env.document.listeners.DOMContentLoaded.length, 1);
        env.document.body = env.body;
        env.document.dispatchEvent(new Event('DOMContentLoaded'));
    }
    assert.equal(env.observers.length, 1);
    for (const event of ['click', 'contextmenu', 'command', 'viewshow']) assert.equal(env.document.listeners[event].length, 1);
    assert.equal(env.window.listeners.hashchange.length, 1);
    assert.equal(env.frames.length, 1);
    assert.equal(env.timers.filter(t => t.ms === 250).length, 1);
    assert.deepEqual(env.logs, [], 'Normal startup is quiet');
});

test('Client startup failures still warn', () => {
    const env = environment();
    env.document.currentScript = { src: 'https://media.example/jf/Babelfin/client.js' };
    env.window.MutationObserver = class { observe() { throw new Error('observer unavailable'); } };
    vm.runInContext(source('client.js'), env.context, { filename: 'client.js' });
    assert.ok(env.logs.some(([kind, text]) => kind === 'warn' && text === '[Babelfin] start'));
    assert.ok(!env.logs.some(([kind]) => kind === 'info'));
});

for (const Type of ['Series', 'Movie', 'Video']) for (const library of ['', 'Home']) test(`Dialog wording respects ${Type} with ${library ? 'a library' : 'no library'}`, () => {
    const env = clientBrowser();
    vm.runInContext(source('ui.js'), env.context, { filename: 'ui.js' });
    env.window.testClient.state.t = translator();
    const box = env.document.createElement('div'); env.document.body.appendChild(box);
    env.window.testClient.renderDialog({ box }, {
        TargetId: 'a'.repeat(32), Scope: Type === 'Series' ? 'Series' : 'Title', Type,
        Name: 'Example', LibraryName: library, Inherited: { Profile: {} }
    }, { Settings: { Everyday: {}, BurnedIn: [] } }, []);
    const noun = Type === 'Series' ? 'show' : Type.toLowerCase();
    const expected = `Applies to ${Type === 'Series' ? 'the whole' : 'this'} ${noun}, in place of ${library ? 'the Home library' : 'the library'}'s choice.`;
    assert.equal(box.querySelector('.bfExplain').textContent, expected);
    assert.ok(box.querySelector('.bfCheck').parentNode.textContent.includes(noun));
});

test('Available locale-specific video wording overrides the English fallback', () => {
    const env = clientBrowser();
    vm.runInContext(source('ui.js'), env.context, { filename: 'ui.js' });
    env.window.testClient.state.t = translator({ ...words, 'dialog.applies.video': 'Video scope: {library}', 'dialog.applies.videoNoLibrary': 'Video scope without a library' });
    for (const LibraryName of ['', 'Home']) {
        const box = env.document.createElement('div'); env.document.body.appendChild(box);
        env.window.testClient.renderDialog({ box }, { TargetId: 'a'.repeat(32), Scope: 'Title', Type: 'Video', LibraryName, Inherited: { Profile: {} } }, {}, []);
        assert.equal(box.querySelector('.bfExplain').textContent, LibraryName ? 'Video scope: Home' : 'Video scope without a library');
    }
});


test('Bulk removal uses /Overrides/All while a single removal still requires an item ID', { timeout: 3000 }, async () => {
    const env = await appBrowser();
    const draft = env.app.state.doc;
    draft.Enabled = false;
    await env.app.removeAllOverrides();
    const bulk = env.calls.filter(c => c.method === 'DELETE');
    assert.equal(bulk.length, 1);
    assert.equal(bulk[0].route, '/Babelfin/Me/Overrides/All');
    assert.deepEqual(bulk[0].query, {});
    assert.equal(draft.Enabled, false, 'Bulk removal preserves other pending edits');
    for (const ItemId of ['', 'All', 'not-an-id']) await assert.rejects(env.app.removeOverride({ ItemId, Scope: 'Title' }), /no valid id/);
    assert.equal(env.calls.filter(c => c.method === 'DELETE').length, 1, 'Invalid single IDs never become bulk removal');
    await env.app.removeOverride({ ItemId: 'b'.repeat(32), Scope: 'Title' });
    const single = env.calls.filter(c => c.method === 'DELETE').at(-1);
    assert.equal(single.route, '/Babelfin/Me/Overrides/' + 'b'.repeat(32));
    assert.deepEqual(single.query, { scope: 'Title' });
    assert.ok(!env.calls.some(c => c.method === 'DELETE' && /\/Overrides\/?$/.test(c.route)), 'Bare collection route is GET-only');
});

test('Existing show, movie and burned-in video strings remain in all 16 real locales', () => {
    const result = require('node:child_process').spawnSync(process.execPath, [path.join(__dirname, 'ui-smoke/person/validate-i18n.js'), '--assets', assets], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const files = fs.readdirSync(path.join(assets, 'i18n')).filter(name => name.endsWith('.json'));
    assert.equal(files.length, 16);
    for (const file of files) {
        const table = JSON.parse(source('i18n/' + file));
        for (const key of ['dialog.applies.show', 'dialog.applies.movie', 'dialog.applies.video', 'dialog.applies.showNoLibrary', 'dialog.applies.movieNoLibrary', 'dialog.applies.videoNoLibrary', 'dialog.burnedIn.show', 'dialog.burnedIn.movie', 'dialog.burnedIn.video']) {
            assert.equal(typeof table[key], 'string', file + ': ' + key);
            assert.ok(table[key].trim(), file + ': ' + key);
        }
    }
});


test('Missing UI.setFields fails closed with the existing error view and no Save', () => {
    const env = clientBrowser();
    vm.runInContext(source('ui.js'), env.context, { filename: 'ui.js' });
    const UI = env.window.BabelfinUI;
    const target = { TargetId: 'a'.repeat(32), Scope: 'Title', Type: 'Movie', Inherited: { Profile: {} } };
    const calls = [];
    env.window.ApiClient = { getUrl: p => p, ajax: options => { calls.push(options); return Promise.resolve(); } };
    for (const setFields of [undefined, null, false]) {
        env.window.BabelfinUI = Object.assign({}, UI, { setFields });
        assert.equal(env.window.testClient.tableApi(), null, 'A partial UI cannot authorize saving');
        const box = env.document.createElement('div'); env.document.body.appendChild(box);
        env.window.testClient.renderDialog({ box }, target, { Settings: { Everyday: {} } }, []);
        assert.equal(box.querySelector('.bfError').textContent, words['common.error']);
        assert.equal(box.querySelector('.bfButton-primary'), null);
        assert.equal(box.querySelectorAll('.bfField select').length, 0);
        box.querySelectorAll('button').forEach(button => button.click());
        assert.deepEqual(calls, [], 'No settings or burned-in request can run without the helper');
    }
    env.window.BabelfinUI = UI;
    assert.equal(env.window.testClient.tableApi(), UI);
    const box = env.document.createElement('div'); env.document.body.appendChild(box);
    env.window.testClient.renderDialog({ box }, target, {}, []);
    assert.ok(box.querySelector('.bfButton-primary'), 'Complete UI restores Save');
    assert.deepEqual(copy(UI.setFields({ UnlabelledAudio: 'Unknown' })), ['UnlabelledAudio']);
});

test('A stale parent cannot resume the draft with another person\'s stored credentials', { timeout: 3000 }, async () => {
    const env = await appBrowser({ embedded: true, staleParent: true });
    env.app.state.doc.Enabled = false;
    const draft = copy(env.app.state.doc);
    await env.app.save();
    env.signIn('person-two'); retry(env); await settle();
    await env.app.save();
    assert.deepEqual(copy(env.app.state.doc), draft);
    assert.equal(env.app.state.needsSignIn, true);
    assert.equal(settingsReads(env), 1);
    assert.ok(!env.calls.some(c => c.token === 'MediaBrowser Token="renewed"'), 'Other stored user token is never sent');
    assert.equal(env.calls.filter(c => c.method === 'PUT').length, 1);
});

for (const embedded of [false, true]) test('settings save sends the original edited values through ' + (embedded ? 'parent API' : 'fetch'), async () => {
    const r = await appBrowser({ embedded }); r.allowSave();
    r.app.state.doc.Everyday.Understood = ['de']; await r.app.save();
    const call = r.calls.find(c => c.method === 'PUT');
    assert.equal(call.headers['X-Babelfin-Expected'], undefined);
    const expected = call.body.ExpectedValues;
    assert.deepEqual(expected, { 'everyday.understood': ['en'] });
});

test('settings retry installs global save and leave listeners once', async () => {
    const r = await appBrowser(), fetch = r.window.fetch; let failed = false;
    r.window.fetch = async (url, init) => {
        if (!failed && String(url).includes('/Me/Settings')) { failed = true; return { status: 503, ok: false, text: async () => JSON.stringify({ Message: 'Unavailable' }) }; }
        return fetch(url, init);
    };
    await r.app.boot(); assert.ok(r.document.querySelector('.bf-message'));
    await r.app.boot(); assert.ok(r.app.state.doc);
    assert.equal(r.document.listeners.keydown.length, 1);
    assert.equal(r.window.listeners.beforeunload.length, 1);
    r.allowSave(); r.app.state.doc.Enabled = false;
    r.document.dispatchEvent(new Event('keydown', { key: 's', ctrlKey: true })); await settle();
    assert.equal(r.calls.filter(call => call.method === 'PUT').length, 1);
});

test('known API problem codes use the selected translation and unknown codes retain the message', async () => {
    const r = await appBrowser();
    const de = JSON.parse(source('i18n/de.json'));
    r.app.setT(translator(de));
    const UI = r.window.BabelfinUI;
    assert.equal(UI.problemMessage(translator(de), { Code: 'library-unavailable', Message: 'English fallback' }), de['api.library-unavailable']);
    assert.notEqual(de['api.library-unavailable'], words['api.library-unavailable']);
    assert.equal(UI.problemMessage(translator(de), { Code: 'future-code', Message: 'Keep this message' }), 'Keep this message');
});

test('embedded TV keyboard moves controls, changes selects and closes with Back', async () => {
    const r = await appBrowser({ embedded: true });
    r.window.parent.document = new Document(); r.window.parent.document.documentElement.classList.add('layout-tv');
    let closed = 0; r.window.parent.Babelfin = { closeSettings: () => { closed++; } };
    const select = r.document.querySelector('.bfui-table-audio select'); select.focus();
    const key = name => select.dispatchEvent(new Event('keydown', { key: name, bubbles: true }));
    const before = select.value; key('ArrowRight'); assert.notEqual(select.value, before);
    key('ArrowDown'); assert.notEqual(r.document.activeElement, select);
    key('BrowserBack'); await settle(); assert.equal(closed, 1);
});

for (const action of ['reload', 'keep']) test('person conflict ' + action + ' preserves unrelated draft fields', async () => {
    const r = await appBrowser(); r.allowSave(); const fetch = r.window.fetch; let writes = 0;
    r.window.fetch = async (url, init = {}) => {
        if (init.method === 'PUT') {
            writes++;
            if (writes === 1) return { status: 409, ok: false, text: async () => JSON.stringify({ Code: 'field-conflict', ConflictingPaths: ['enabled'], CurrentValues: { enabled: true }, LastChangedByName: 'Example' }) };
        }
        return fetch(url, init);
    };
    r.app.state.doc.Enabled = false; r.app.state.doc.Everyday.Reads = ['de']; await r.app.save(); await settle();
    const box = r.document.querySelector('.bf-conflict'); assert.ok(box); assert.match(box.textContent, /Example/);
    box.querySelector('[data-conflict="' + action + '"]').click(); await settle();
    if (action === 'reload') {
        assert.equal(r.app.state.doc.Enabled, true); assert.deepEqual(Array.from(r.app.state.doc.Everyday.Reads), ['de']);
        assert.equal(writes, 1);
    } else {
        assert.equal(writes, 2); assert.equal(r.app.state.doc.Enabled, false);
        const call = r.calls.filter(c => c.method === 'PUT').at(-1); assert.equal(call.body.ExpectedValues, undefined);
    }
});

for (const action of ['reload', 'keep']) test('person conflict ' + action + ' refreshes subsequent expected values', async () => {
    const r = await appBrowser(), fetch = r.window.fetch, UI = r.window.BabelfinUI;
    const stored = copy(r.app.state.base), requests = []; stored.Everyday.Understood = ['fr'];
    const reply = (status, body) => ({ status, ok: status < 400, text: async () => JSON.stringify(body) });
    r.window.fetch = async (url, init = {}) => {
        if (String(url).includes('/Me/Settings')) {
            if (init.method === 'PUT') {
                const body = JSON.parse(init.body), fields = new URL(url, r.window.location.origin).searchParams.get('fields').split(',');
                assert.equal(init.headers['X-Babelfin-Expected'], undefined);
                const expected = body.ExpectedValues || null;
                requests.push(expected);
                const conflicts = expected ? fields.filter(path => JSON.stringify(expected[path]) !== JSON.stringify(UI.fieldValue(stored, path))) : [];
                if (conflicts.length) return reply(409, { Code: 'field-conflict', ConflictingPaths: conflicts, LastChangedByName: 'Example' });
                fields.forEach(path => UI.setFieldValue(stored, path, UI.fieldValue(body, path)));
            }
            return reply(200, { Settings: stored, Inherited: { Profile: {} }, Libraries: [], Assumed: [] });
        }
        return fetch(url, init);
    };
    r.app.state.doc.Everyday.Understood = ['de']; r.app.state.doc.Everyday.Reads = ['ja'];
    await r.app.save(); r.document.querySelector('[data-conflict="' + action + '"]').click(); await settle();
    if (action === 'reload') {
        assert.deepEqual(Array.from(r.app.state.doc.Everyday.Understood), ['fr']);
        assert.deepEqual(Array.from(r.app.state.doc.Everyday.Reads), ['ja']);
        await r.app.save(); assert.deepEqual(requests[1], { 'everyday.reads': ['en'] });
    } else { assert.equal(requests[1], null); }
    r.app.state.doc.Everyday.Understood = ['en']; await r.app.save();
    assert.deepEqual(requests.at(-1), { 'everyday.understood': [action === 'reload' ? 'fr' : 'de'] });
    assert.deepEqual(Array.from(stored.Everyday.Understood), ['en']); assert.equal(r.document.querySelector('.bf-conflict'), null);
});

test('TV capture moves past native radio navigation and Left/Right changes the radio choice', async () => {
    const captureDom = require('./ui-smoke/admin/fakedom');
    const r = await appBrowser({ embedded: true, DocumentClass: captureDom.Document });
    r.window.parent.document = new captureDom.Document(); r.window.parent.document.documentElement.classList.add('layout-tv');
    const selected = () => r.document.querySelector('[role="radio"][aria-checked="true"]');
    const mode = selected(), before = r.app.state.doc.PerLibrary; assert.ok(mode); mode.focus();
    mode.dispatchEvent(new captureDom.Event('keydown', { key: 'ArrowDown', bubbles: true }));
    assert.equal(r.app.state.doc.PerLibrary, before, 'Up/Down traverses without activating a different radio');
    assert.equal(r.document.activeElement === mode, false, 'The radio group cannot trap D-pad traversal');
    mode.focus(); mode.dispatchEvent(new captureDom.Event('keydown', { key: 'ArrowRight', bubbles: true }));
    assert.equal(r.app.state.doc.PerLibrary, true); assert.equal(selected() === r.document.activeElement, true);
});


async function tvBrowser() {
    const dom = require('./ui-smoke/admin/fakedom');
    const r = await appBrowser({ embedded: true, DocumentClass: dom.Document });
    r.window.parent.document = new dom.Document();
    r.window.parent.document.documentElement.classList.add('layout-tv');
    r.closed = 0;
    r.window.parent.Babelfin = { closeSettings() { r.closed++; } };
    r.key = (target, key, keyCode = 0) => {
        const e = new dom.Event('keydown', { key, keyCode, bubbles: true });
        target.dispatchEvent(e);
        return e;
    };
    return r;
}

test('TV language search owns result arrows and leaves Left/Right available for text editing', async () => {
    const r = await tvBrowser();
    r.app.state.languages = r.app.state.languages.concat([{ Code: 'fr-CA', Name: 'French Canadian', Base: 'fr' }]);
    r.app.renderPage();
    r.document.querySelector('.bf-languages-line button').click();
    const input = r.document.querySelector('.bf-languages-editor input');
    input.value = 'fr'; input.dispatchEvent(new Event('input'));
    const options = r.document.querySelectorAll('.bf-languages-editor [role="option"]');
    assert.equal(options.length, 2);
    assert.equal(input.getAttribute('aria-activedescendant'), options[0].id);
    for (const [key, option] of [['ArrowDown', options[1]], ['ArrowUp', options[0]], ['ArrowDown', options[1]]]) {
        assert.equal(r.key(input, key).defaultPrevented, true);
        assert.ok(r.document.activeElement === input);
        assert.equal(input.getAttribute('aria-activedescendant'), option.id, 'One key advances exactly one result');
    }
    for (const key of ['ArrowLeft', 'ArrowRight']) {
        assert.equal(r.key(input, key).defaultPrevented, false, 'Native caret movement remains available');
        assert.ok(r.document.activeElement === input);
    }
    r.key(input, 'Enter');
    assert.ok(r.app.state.doc.Everyday.Understood.includes(options[1].getAttribute('data-code')));
    assert.equal(r.closed, 0);
});

test('TV language chooser Escape clears search then dismisses the picker before Settings Back', async () => {
    const r = await tvBrowser();
    const chooser = r.window.BabelfinUI.languageChooser({ t: translator(), languages: r.app.state.languages, value: 'en' });
    r.document.getElementById('bf-app').appendChild(chooser.element); chooser.open();
    const input = chooser.element.querySelector('input'), button = chooser.element.querySelector('button');
    input.value = 'fr'; input.dispatchEvent(new Event('input'));
    for (const expected of ['true', 'false']) {
        const e = r.key(input, 'Escape', 27);
        assert.equal(e.defaultPrevented, true); assert.equal(e._stop, true);
        assert.equal(input.value, ''); assert.equal(button.getAttribute('aria-expanded'), expected);
        assert.equal(r.closed, 0);
    }
    assert.ok(r.document.activeElement === button);
    r.key(button, 'BrowserBack', 166); assert.equal(r.closed, 1, 'Page Back resumes after the picker closes');
});

for (const [key, keyCode] of [['Escape', 27], ['BrowserBack', 166], ['GoBack', 166], ['Unidentified', 461], ['Unidentified', 10009]]) {
    test('TV confirmation Back cancels only the top dialog and retains the draft: ' + key + '/' + keyCode, async () => {
        const r = await tvBrowser(), UI = r.window.BabelfinUI;
        r.app.state.doc.Enabled = false;
        const draft = copy(r.app.state.doc), opener = r.document.querySelector('.bf-languages-line button'); opener.focus();
        let lowerAnswer = null;
        const lower = r.window.BabelfinApp.confirmLeave().then(value => { lowerAnswer = value; return value; });
        const lowerFocus = r.document.activeElement;
        const upper = UI.confirm({ t: translator(), title: 'Confirm', message: 'Another action', danger: true });
        const dialogs = r.document.querySelectorAll('.bfui-dialog'), top = dialogs[dialogs.length - 1];
        r.key(r.document.activeElement, 'ArrowDown');
        assert.ok(top.contains(r.document.activeElement), 'D-pad navigation stays in the top confirmation');
        let escaped = 0; r.document.body.addEventListener('keydown', () => { escaped++; });
        const first = r.key(r.document.activeElement, key, keyCode);
        assert.equal(first.defaultPrevented, true); assert.equal(first._stop, true);
        assert.equal(await upper, false); await settle();
        assert.equal(r.document.querySelectorAll('.bfui-dialog').length, 1);
        assert.equal(lowerAnswer, null); assert.ok(r.document.activeElement === lowerFocus);
        const second = r.key(r.document.activeElement, key, keyCode);
        assert.equal(await lower, false);
        assert.equal(second.defaultPrevented, true); assert.equal(second._stop, true);
        assert.equal(r.document.querySelector('.bfui-dialog'), null);
        assert.ok(r.document.activeElement === opener); assert.equal(escaped, 0); assert.equal(r.closed, 0);
        assert.deepEqual(copy(r.app.state.doc), draft); assert.equal(r.window.BabelfinApp.hasChanges(), true);
        assert.equal(r.calls.filter(call => call.method === 'PUT').length, 0);
    });
}

test('TV confirmation Escape takes priority over a previously opened tooltip', async () => {
    const r = await tvBrowser(), UI = r.window.BabelfinUI;
    r.app.state.doc.Enabled = false;
    const tip = UI.infoTip({ t: translator(), label: 'Setting', text: 'More details' });
    r.document.getElementById('bf-app').appendChild(tip.element);
    tip.element.querySelector('button').click();
    const answer = r.window.BabelfinApp.confirmLeave();
    const e = r.key(r.document.activeElement, 'Escape', 27);
    assert.equal(Boolean(r.document.querySelector('.bfui-dialog')), false);
    assert.equal(await answer, false); assert.equal(e.defaultPrevented, true); assert.equal(e._stop, true);
    assert.equal(r.closed, 0); assert.equal(r.window.BabelfinApp.hasChanges(), true);
});

// Optional native DOM checks run with BF_TV_BROWSER=1; they use a private Chrome profile and a loopback mock, never Jellyfin.
async function nativeTvBrowser(t) {
    const http = require('node:http'), os = require('node:os');
    const { Chrome } = require('../rig/e2e/cdp');
    const settings = {
        Settings: { SchemaVersion: 1, UserId: 'a'.repeat(32), Enabled: true, Everyday: { Understood: ['en'], Reads: ['en'] }, Libraries: [], SmartRules: [], Overrides: [], BurnedIn: [], Locks: {} },
        Inherited: { Profile: { Understood: ['en'], Reads: ['en'], AudioChain: [{ Kind: 'VideoDefault' }], WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' } }, Libraries: [], Assumed: []
    };
    const server = http.createServer((req, res) => {
        const pathname = new URL(req.url, 'http://localhost').pathname;
        if (pathname === '/') {
            res.setHeader('Content-Type', 'text/html');
            res.end(`<!doctype html><html class="layout-tv"><body><script>
                window.closeAttempts=0;window.Babelfin={closeSettings(){window.closeAttempts++}};
                window.ApiClient={getCurrentUserId:()=> '${'a'.repeat(32)}',getUrl:(p,q)=>location.origin+'/'+p+(q?'?'+new URLSearchParams(q):''),ajax:async o=>{const r=await fetch(o.url,{method:o.type,headers:o.headers,body:o.data});if(!r.ok)throw r;return r.text()}};
                </script><iframe style="width:1200px;height:850px" src="/Babelfin/app?embedded=1"></iframe></body></html>`);
            return;
        }
        if (pathname === '/Babelfin/app') { res.setHeader('Content-Type', 'text/html'); res.end(source('app.html')); return; }
        if (pathname.startsWith('/Babelfin/assets/')) {
            const file = pathname.slice('/Babelfin/assets/'.length);
            if (!/^(?:[\w-]+\/)?[\w.-]+$/.test(file)) { res.writeHead(404).end(); return; }
            res.setHeader('Content-Type', file.endsWith('.js') ? 'application/javascript' : file.endsWith('.css') ? 'text/css' : 'application/json');
            res.end(source(file)); return;
        }
        let result;
        if (pathname === '/Babelfin/Ping') result = { version: 'test' };
        else if (pathname === '/Babelfin/Languages') result = ['en', 'fr', 'fr-CA', 'de'].map(Code => ({ Code, Name: Code, Base: Code.split('-')[0] }));
        else if (pathname === '/Babelfin/Me/Settings') result = settings;
        else if (pathname === '/Babelfin/Me/Overrides') result = [];
        else if (pathname === '/Babelfin/Me/ChoiceStatus') result = { HasChoices: true };
        else { res.writeHead(404).end(); return; }
        res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(result));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    t.after(() => new Promise(resolve => server.close(resolve)));
    const chrome = await Chrome.launch();
    const profile = fs.realpathSync(chrome.profile), temporary = fs.realpathSync(os.tmpdir());
    assert.ok(profile.startsWith(temporary + path.sep) && path.basename(profile).startsWith('babelfin-e2e-chrome-'));
    t.after(() => chrome.close());
    const page = await chrome.newPage(); await page.navigate('http://127.0.0.1:' + server.address().port + '/');
    await page.waitFor(() => !!document.querySelector('iframe').contentDocument.querySelector('.bfui-table-audio select'));
    return page;
}

test('native TV language search handles arrows and Escape before page navigation', { skip: process.env.BF_TV_BROWSER !== '1', timeout: 45000 }, async t => {
    const page = await nativeTvBrowser(t);
    const options = await page.evaluate(() => {
        const w = document.querySelector('iframe').contentWindow, d = w.document;
        d.querySelector('.bf-languages-line button').click();
        const input = d.querySelector('.bf-languages-editor input'); input.value = 'fr'; input.dispatchEvent(new w.Event('input')); input.focus();
        return Array.from(d.querySelectorAll('.bf-languages-editor [role="option"]')).map(option => option.id);
    });
    assert.equal(options.length, 2);
    for (const [key, expected] of [['ArrowDown', options[1]], ['ArrowUp', options[0]]]) {
        await page.press(key);
        assert.deepEqual(await page.evaluate(() => { const d = document.querySelector('iframe').contentDocument, input = d.querySelector('.bf-languages-editor input'); return [d.activeElement === input, input.getAttribute('aria-activedescendant')]; }), [true, expected]);
    }
    await page.press('Escape');
    assert.deepEqual(await page.evaluate(() => { const d = document.querySelector('iframe').contentDocument; return [window.closeAttempts, d.querySelector('.bf-languages-editor input').value]; }), [0, '']);
    assert.deepEqual(page.exceptions, []);
});

test('native TV confirmation consumes every remote Back and preserves the settings draft', { skip: process.env.BF_TV_BROWSER !== '1', timeout: 45000 }, async t => {
    const page = await nativeTvBrowser(t);
    await page.evaluate(() => {
        const w = document.querySelector('iframe').contentWindow, d = w.document, audio = d.querySelector('.bfui-table-audio select');
        audio.value = 'original'; audio.dispatchEvent(new w.Event('change')); audio.focus();
        w.keyResults = []; w.leakedKeys = 0;
        w.addEventListener('keydown', e => w.setTimeout(() => w.keyResults.push({ prevented: e.defaultPrevented }), 0), true);
        d.body.addEventListener('keydown', () => { w.leakedKeys++; });
    });
    for (const key of ['BrowserBack', 'GoBack', 'WebOSBack', 'TizenBack', 'Escape']) {
        await page.evaluate(() => { const w = document.querySelector('iframe').contentWindow; w.leaveAnswer = null; w.BabelfinApp.confirmLeave().then(answer => { w.leaveAnswer = answer; }); });
        await page.press(key);
        await page.evaluate(() => new Promise(resolve => setTimeout(resolve, 0)));
        assert.deepEqual(await page.evaluate(() => { const w = document.querySelector('iframe').contentWindow, d = w.document; return { open: !!d.querySelector('.bfui-dialog'), answer: w.leaveAnswer, closed: window.closeAttempts, dirty: w.BabelfinApp.hasChanges(), audio: d.querySelector('.bfui-table-audio select').value, focused: d.activeElement === d.querySelector('.bfui-table-audio select'), prevented: w.keyResults[w.keyResults.length - 1].prevented, leaked: w.leakedKeys }; }), { open: false, answer: false, closed: 0, dirty: true, audio: 'original', focused: true, prevented: true, leaked: 0 }, key);
    }
    assert.deepEqual(page.exceptions, []);
});

test('TV horizontal navigation preserves text caret and moves from a link to its neighbour', async () => {
    const r = await tvBrowser(); const root = r.document.getElementById('bf-app');
    const row = r.document.createElement('div'); root.appendChild(row);
    const text = r.document.createElement('input'); text.type = 'text';
    const link = r.document.createElement('a'); link.setAttribute('href', '#help');
    const button = r.document.createElement('button');
    [text, link, button].forEach((node, i) => { node.getBoundingClientRect = () => ({ top: 2000, left: 10 + i * 100, width: 80, height: 30 }); row.appendChild(node); });
    text.focus(); const caret = r.key(text, 'ArrowRight');
    assert.equal(caret.defaultPrevented, false); assert.ok(r.document.activeElement === text);
    for (const tag of ['search', 'textarea']) {
        const field = r.document.createElement(tag === 'search' ? 'input' : 'textarea');
        if (tag === 'search') field.type = 'search';
        field.getBoundingClientRect = () => ({ top: 2100, left: 10, width: 80, height: 30 }); root.appendChild(field);
        field.focus(); const event = r.key(field, 'ArrowLeft');
        assert.equal(event.defaultPrevented, false); assert.ok(r.document.activeElement === field);
    }
    link.focus(); const move = r.key(link, 'ArrowRight');
    assert.equal(move.defaultPrevented, true); assert.ok(r.document.activeElement === button);
});

test('successful TV save keeps focus in the save controls', async () => {
    const r = await tvBrowser(); r.allowSave();
    assert.equal(r.document.querySelector('.bf-savebar').hidden, true);
    r.app.state.doc.Enabled = false; r.app.renderPage();
    const button = r.document.querySelector('.bf-savebar .bfui-btn-primary'); assert.ok(button); button.focus();
    await r.app.save();
    assert.ok(r.document.activeElement && r.document.activeElement.closest('.bf-savebar'));
    const focused = r.document.activeElement; assert.equal(focused.getAttribute('aria-disabled'), 'true');
    const puts = r.calls.filter(call => call.method === 'PUT').length; focused.click(); await settle();
    assert.equal(r.calls.filter(call => call.method === 'PUT').length, puts);
    await r.app.load(); assert.equal(r.document.querySelector('.bf-savebar').hidden, true);
});

test('TV saved status and focus departure hide the clean save bar without hiding a dirty draft', async () => {
    const r = await tvBrowser(); r.allowSave();
    r.app.state.doc.Enabled = false; r.app.renderPage();
    let bar = r.document.querySelector('.bf-savebar');
    bar.querySelector('.bfui-btn-primary').focus(); await r.app.save();
    bar = r.document.querySelector('.bf-savebar');
    const savedButton = bar.querySelector('.bfui-btn-primary');
    assert.equal(savedButton.getAttribute('aria-disabled'), 'true');
    assert.equal(bar.querySelector('.bf-savebar-status').textContent, words['app.saved']);
    assert.match(source('app.css'), /\.bf-savebar\s+\.bfui-btn\[aria-disabled=["']true["']\]\s*\{[^}]*opacity\s*:\s*(?:0?\.4)/);
    const outside = r.document.querySelector('.bfui-table select'); assert.ok(outside);
    outside.focus(); savedButton.dispatchEvent(new Event('focusout', { bubbles: true, relatedTarget: outside }));
    assert.equal(bar.hidden, true);
    r.app.state.doc.Enabled = true; r.app.renderPage();
    bar = r.document.querySelector('.bf-savebar'); const dirtyButton = bar.querySelector('.bfui-btn-primary');
    assert.equal(bar.hidden, false); assert.equal(dirtyButton.getAttribute('aria-disabled'), 'false');
    outside.focus(); dirtyButton.dispatchEvent(new Event('focusout', { bubbles: true, relatedTarget: outside }));
    assert.equal(bar.hidden, false); assert.equal(r.window.BabelfinApp.hasChanges(), true);
});

test('TV clean Save has a dimmed style', () => {
    assert.match(source('app.css'), /\.bf-savebar\s+\.bfui-btn\[aria-disabled=["']true["']\]\s*\{[^}]*opacity\s*:\s*(?:0?\.4)/);
});

test('TV focus leaving a clean save bar hides it', async () => {
    const r = await tvBrowser(); r.allowSave();
    r.app.state.doc.Enabled = false; r.app.renderPage();
    r.document.querySelector('.bf-savebar .bfui-btn-primary').focus(); await r.app.save();
    const bar = r.document.querySelector('.bf-savebar'), button = bar.querySelector('.bfui-btn-primary');
    const outside = r.document.querySelector('.bfui-table select'); assert.ok(outside);
    outside.focus(); button.dispatchEvent(new Event('focusout', { bubbles: true, relatedTarget: outside }));
    assert.equal(bar.hidden, true);
});

test('person conflict identifies an administrator without a name', async () => {
    const r = await appBrowser(); r.allowSave(); const fetch = r.window.fetch;
    r.window.fetch = async (url, init = {}) => init.method === 'PUT'
        ? { status: 409, ok: false, text: async () => JSON.stringify({ Code: 'field-conflict', ConflictingPaths: ['enabled'], LastChangedBy: 'admin' }) }
        : fetch(url, init);
    r.app.state.doc.Enabled = false; await r.app.save(); await settle();
    const panel = r.document.querySelector('.bf-conflict'); assert.ok(panel); assert.match(panel.textContent, /An administrator/);
});
