'use strict';
// Portable regressions against CURRENT mirror sources; no dependencies or external fake DOM.
// node --test tools/tests/preference-ui.test.js
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { spawnSync } = require('node:child_process');
const assets = path.resolve(__dirname, '../../plugin/src/Jellyfin.Plugin.Babelfin/Assets');
const source = name => fs.readFileSync(path.join(assets, name), 'utf8');
const copy = value => JSON.parse(JSON.stringify(value));
const tick = async () => { for (let i = 0; i < 15; i++) await Promise.resolve(); };
const en = JSON.parse(source('i18n/en.json'));
const adminEn = JSON.parse(source('i18n-admin/en.json'));
function translate(key, values) {
    return String(adminEn[key] || en[key] || key).replace(/\{(\w+)\}/g, (all, name) => values && values[name] !== undefined ? values[name] : all);
}
translate.language = 'en';
translate.has = key => key in en || key in adminEn;

// Model native select values/options and persistent nodes so rebuilds and auto-picks fail.
class Node {
    constructor(tag, doc, text) {
        Object.assign(this, { tagName: tag.toUpperCase(), nodeType: tag === '#text' ? 3 : 1, ownerDocument: doc,
            childNodes: [], parentNode: null, attrs: {}, dataset: {}, style: {}, listeners: {}, hidden: false,
            disabled: false, checked: false, _text: text || '', _value: '', className: '' });
        this.classList = {
            contains: name => this.className.split(/\s+/).includes(name),
            add: (...names) => { this.className = [...new Set(this.className.split(/\s+/).filter(Boolean).concat(names))].join(' '); },
            remove: (...names) => { this.className = this.className.split(/\s+/).filter(n => !names.includes(n)).join(' '); },
            toggle: (name, force) => { const on = force === undefined ? !this.classList.contains(name) : !!force; this.classList[on ? 'add' : 'remove'](name); return on; }
        };
    }
    get children() { return this.childNodes.filter(n => n.nodeType === 1); }
    get firstChild() { return this.childNodes[0] || null; }
    get nextSibling() { return this.parentNode && this.parentNode.childNodes[this.parentNode.childNodes.indexOf(this) + 1] || null; }
    get isConnected() { return this === this.ownerDocument.documentElement || !!(this.parentNode && this.parentNode.isConnected); }
    get textContent() { return this._text + this.childNodes.map(n => n.textContent).join(''); }
    set textContent(v) { this.childNodes.forEach(n => { n.parentNode = null; }); this.childNodes = []; this._text = String(v); }
    get text() { return this.textContent; }
    get id() { return this.attrs.id || ''; }
    set id(v) { this.attrs.id = v; }
    get options() { return this.querySelectorAll('option'); }
    get selectedIndex() { return this.options.indexOf(this._selected); }
    get value() {
        if (this.tagName !== 'SELECT') return this._value;
        if (this._selected && this.options.includes(this._selected)) return this._selected.value;
        return this._noSelection ? '' : this.options.length ? this.options[0].value : '';
    }
    set value(v) {
        if (this.tagName === 'SELECT') { this._selected = this.options.find(n => n.value === String(v)); this._noSelection = !this._selected; }
        else this._value = String(v);
    }
    appendChild(node) { return this.insertBefore(node, null); }
    insertBefore(node, before) {
        if (node.parentNode) node.parentNode.removeChild(node);
        const at = before ? this.childNodes.indexOf(before) : this.childNodes.length;
        assert.ok(at >= 0); this.childNodes.splice(at, 0, node); node.parentNode = this; return node;
    }
    removeChild(node) { const at = this.childNodes.indexOf(node); assert.ok(at >= 0); this.childNodes.splice(at, 1); node.parentNode = null; return node; }
    remove() { if (this.parentNode) this.parentNode.removeChild(this); }
    setAttribute(name, v) {
        v = String(v);
        if (name === 'class') this.className = v;
        else if (name === 'value') this.value = v;
        else if (name === 'hidden') this.hidden = true;
        else if (name.startsWith('data-')) this.dataset[name.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = v;
        this.attrs[name] = v;
    }
    getAttribute(name) {
        if (name === 'class') return this.className;
        if (name.startsWith('data-')) return this.dataset[name.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] || null;
        return this.attrs[name] === undefined ? null : this.attrs[name];
    }
    removeAttribute(name) { delete this.attrs[name]; }
    addEventListener(type, fn) { (this.listeners[type] || (this.listeners[type] = [])).push(fn); }
    removeEventListener(type, fn) { this.listeners[type] = (this.listeners[type] || []).filter(f => f !== fn); }
    fire(type, props = {}) {
        const e = Object.assign({ type, target: this, currentTarget: this, preventDefault() {}, stopPropagation() {} }, props);
        for (const fn of [...(this.listeners[type] || [])]) fn(e);
    }
    click() { if (!this.disabled) this.fire('click'); }
    focus() { this.ownerDocument.activeElement = this; this.fire('focus'); }
    contains(node) { return this === node || this.childNodes.some(n => n.contains(node)); }
    matches(selector) {
        const tag = /^[\w-]+/.exec(selector);
        if (tag && this.tagName.toLowerCase() !== tag[0].toLowerCase()) return false;
        for (const [, name] of selector.matchAll(/\.([\w-]+)/g)) if (!this.classList.contains(name)) return false;
        const id = /#([\w-]+)/.exec(selector);
        if (id && this.id !== id[1]) return false;
        for (const [, name, quote, value] of selector.matchAll(/\[([\w-]+)(?:=(["']?)([^"'\]]*)\2)?\]/g))
            if (this.getAttribute(name) === null || (value !== undefined && this.getAttribute(name) !== value)) return false;
        return true;
    }
    querySelectorAll(selector) {
        const out = [];
        for (const child of this.children) {
            if (selector.split(',').some(s => child.matches(s.trim()))) out.push(child);
            out.push(...child.querySelectorAll(selector));
        }
        return out;
    }
    querySelector(s) { return this.querySelectorAll(s)[0] || null; }
    closest(s) { return this.matches(s) ? this : this.parentNode && this.parentNode.closest(s); }
    getBoundingClientRect() { return { left: 20, top: 20, bottom: 40 }; }
    get offsetWidth() { return 260; }
    get offsetHeight() { return 70; }
    scrollIntoView() {}
}
function runtime() {
    const document = {
        readyState: 'loading', listeners: {}, addEventListener: Node.prototype.addEventListener, removeEventListener: Node.prototype.removeEventListener,
        createElement(tag) { return new Node(tag, this); }, createElementNS(ns, tag) { return this.createElement(tag); },
        createTextNode(v) { return new Node('#text', this, String(v)); },
        getElementById(id) { return this.documentElement.querySelector('#' + id); },
        querySelector(s) { return this.documentElement.querySelector(s); }, querySelectorAll(s) { return this.documentElement.querySelectorAll(s); }
    };
    document.documentElement = document.createElement('html');
    document.head = document.createElement('head'); document.body = document.createElement('body');
    document.documentElement.appendChild(document.head); document.documentElement.appendChild(document.body);
    const storage = new Map(), calls = [];
    const window = {
        document, innerWidth: 1024, innerHeight: 768, scrollY: 0,
        location: { origin: 'https://example.test', pathname: '/jellyfin/Babelfin/app', search: '' },
        localStorage: { getItem: k => storage.get(k) || null, setItem: (k, v) => storage.set(k, v) },
        listeners: {}, addEventListener: Node.prototype.addEventListener, removeEventListener: Node.prototype.removeEventListener,
        scrollTo() {}, confirm() { return true; }
    };
    window.top = window; window.parent = window;
    let respond = () => null;
    const apiClient = {
        getCurrentUserId: () => 'signed-in-viewer',
        getUrl(p, query) { return 'https://example.test/jellyfin/' + p.replace(/^\//, '') + (query ? '?' + new URLSearchParams(query) : ''); },
        accessToken: () => 'test-token'
    };
    const context = vm.createContext({
        window, document, navigator: { language: 'en' }, location: window.location, localStorage: window.localStorage,
        URL, URLSearchParams, DOMException, Intl, console, ApiClient: apiClient, Dashboard: { showLoadingMsg() {}, hideLoadingMsg() {} },
        setInterval: () => 1, clearInterval() {}, setTimeout: () => 1, clearTimeout() {},
        fetch: async (url, options = {}) => {
            const call = { url, options }; calls.push(call); const value = await respond(call);
            return { ok: true, status: 200, text: async () => JSON.stringify(value) };
        }
    });
    vm.runInContext(source('ui.js'), context, { filename: 'ui.js' });
    const UI = window.BabelfinUI; UI.setTranslator(translate);
    function loadInternals(name, extra) {
        vm.runInContext(source(name).replace(/\}\)\(\);\s*$/, extra + '\n})();'), context, { filename: name });
    }
    function admin(state) {
        loadInternals('dashboard.js', 'window.__admin = { peopleCell: peopleCell, mountLibraryTable: mountLibraryTable, renderLibraries: renderLibraries, personEditor: personEditor, renderHeader: renderHeader, appsGroup: appsGroup, templateGroup: templateGroup, setState: function(v) { S = v; } };');
        const merged = Object.assign({ config: {}, cache: {}, pending: {}, claims: [], lang: 'en', languageList: [], uiT: translate, t: translate, userList: [], page: document.body }, state);
        window.__admin.setState(merged);
        return { api: window.__admin, state: merged };
    }
    function app() {
        window.BabelfinI18n = {};
        loadInternals('app.js', 'window.__app = { state: state, tableSection: tableSection, header: header, isDirty: isDirty, readyFrameClient: readyFrameClient, authHeader: authHeader, setTranslator: function(v) { t = v; } };');
        window.__app.setTranslator(translate); return window.__app;
    }
    function host(id) { const h = document.createElement('div'); if (id) h.id = id; document.body.appendChild(h); return h; }
    return { UI, document, window, context, host, calls, admin, app, respond: fn => { respond = fn; }, apiClient };
}
function select(node, value) { node.value = value; node.fire('change'); }
function neutral() { return { AllLibraries: { AudioChain: [{ Kind: 'VideoDefault' }], WhenAudioUnderstood: 'VideoDefault', WhenAudioNotUnderstood: 'VideoDefault' }, Libraries: {} }; }
function rule(name, id = name, settings = {}) { return { Id: id, Name: name, Enabled: true, Match: 'All', Conditions: [{ Kind: 'Genre', Values: [name], Negate: false, IsRegex: false }], Settings: settings }; }
function genreControl(r, settings, loadGenres = () => ({ Items: [{ Name: 'Anime' }, { Name: 'Drama' }] })) {
    let changes = 0;
    const control = r.UI.genreRows(r.host(), { settings, t: translate, loadGenres, onChange: () => { changes++; } });
    return { control, get changes() { return changes; } };
}
function genreAdd(el) { return el.querySelectorAll('button').find(b => b.textContent === en['ui.genres.add'] && b.parentNode === el); }
function pickGenre(el, name) { const box = el.querySelector('.bfui-genres-picker'); select(box.querySelector('select'), name); box.querySelector('button').click(); }

test('chains round-trip without auto-selecting languages', () => {
    const { UI } = runtime();
    for (const [value, kind] of [['mine', 'MyLanguages'], ['original', 'OriginalLanguage']]) {
        const layer = { ForeignTitles: 'DubInMyLanguage', AvoidCommentary: true, SubtitleLanguages: ['fr'] };
        UI.writeTableAudio(layer, value);
        assert.deepEqual(copy(layer.AudioChain), [{ Kind: kind }, { Kind: 'VideoDefault' }]);
        for (const codes of [[], ['de'], ['de', 'en']]) assert.equal(UI.tableAudioValue(layer, codes), value);
        assert.equal(layer.AvoidCommentary, true); assert.deepEqual(layer.SubtitleLanguages, ['fr']); assert.equal(layer.ForeignTitles, undefined);
        UI.writeTableAudio(layer, ''); assert.equal(layer.AudioChain, undefined);
    }
    assert.equal(UI.tableAudioValue({}, ['en']), '');
    assert.equal(UI.tableAudioValue({ AudioChain: [{ Kind: 'OriginalLanguage' }, { Kind: 'Language', Language: 'en' }] }, []), 'custom');
});
test('choices and inherited labels agree for person/admin', () => {
    const { UI } = runtime();
    for (const audience of ['person', 'admin']) for (const value of ['mine', 'original']) {
        const layer = {}; UI.writeTableAudio(layer, value);
        const audio = UI.tableChoice({ t: translate, audience, defaultLayer: layer });
        const expected = en[value === 'mine' ? 'ui.table.audioMyLanguages' : 'ui.table.audioOriginal'];
        assert.equal(audio.select.value, ''); assert.equal(audio.select.options.find(n => n.value === value).textContent, expected);
        assert.ok(audio.select.options[0].textContent.includes(expected));
        const sub = UI.tableChoice({ t: translate, kind: 'subtitles', audience });
        assert.ok(!sub.select.options.some(n => ['mine', 'original'].includes(n.value)));
    }
});
test('original-language info supports hover/keyboard/tap/Escape while My languages has no tip', () => {
    const r = runtime(), c = r.UI.tableChoice({ t: translate, value: 'mine' });
    r.host().appendChild(c.element); const node = c.select;
    select(node, 'mine'); assert.equal(node.getAttribute('aria-describedby'), null);
    assert.ok(c.element.querySelectorAll('.bfui-info-wrap').every(n => n.hidden));
    for (const value of ['original']) {
        select(node, value);
        const wrap = c.element.querySelectorAll('.bfui-info-wrap').find(n => !n.hidden), button = wrap.querySelector('button');
        const tip = r.document.getElementById(node.getAttribute('aria-describedby'));
        assert.ok(tip.textContent.includes(value === 'mine' ? 'My languages' : 'TMDb'));
        button.fire('mouseenter'); assert.equal(tip.hidden, false);
        button.fire('mouseleave'); assert.equal(tip.hidden, true);
        button.focus(); assert.equal(tip.hidden, false);
        button.fire('blur'); button.click(); assert.equal(tip.hidden, false);
        for (const fn of r.document.listeners.keydown) fn({ key: 'Escape', preventDefault() {}, stopPropagation() {} });
        assert.equal(tip.hidden, true); assert.equal(c.select, node);
    }
    select(node, ''); assert.equal(node.getAttribute('aria-describedby'), null);
    assert.ok(c.element.querySelectorAll('.bfui-info-wrap').every(n => n.hidden));
});
test('library cells stay mounted and built-ins stay Video default', () => {
    const r = runtime(), settings = { Everyday: { Understood: ['en'] }, Libraries: [], PerLibrary: false };
    let changes = 0;
    const table = r.UI.libraryTable(r.host(), { settings, translate, defaults: neutral(), onChange: () => changes++ });
    const nodes = table.element.querySelectorAll('select'), before = copy(settings);
    assert.equal(nodes[0].value, ''); assert.equal(nodes[0].options[0].textContent, en['ui.table.videoDefault']); assert.deepEqual(settings, before);
    select(nodes[0], 'mine'); select(nodes[1], 'smart'); select(nodes[0], 'original');
    assert.deepEqual(table.element.querySelectorAll('select'), nodes); assert.equal(changes, 3);
    assert.deepEqual(copy(settings.Everyday.AudioChain).map(s => s.Kind), ['OriginalLanguage', 'VideoDefault']);
    assert.equal(settings.Everyday.WhenAudioNotUnderstood, 'Full');
});
test('overrides and People summarize the same choices', () => {
    const r = runtime(), { api } = r.admin();
    for (const value of ['mine', 'original']) {
        const settings = {}; r.UI.writeTableAudio(settings, value);
        const expected = en[value === 'mine' ? 'ui.table.audioMyLanguages' : 'ui.table.audioOriginal'];
        const c = r.UI.overridesTable(r.host(), { t: translate, mine: ['en'], rows: [{ ItemId: 'a'.repeat(32), Scope: 'Title', Name: 'Film', Settings: settings }] });
        assert.equal(c.element.querySelector('.bfui-overrides-audio').textContent, expected);
        assert.equal(api.peopleCell({ HasSettings: true, Table: settings }, 'audio', new Map()), expected);
    }
});

test('language-list refresh keeps table/select nodes and saved choices', () => {
    const r = runtime(), settings = { Everyday: { Understood: ['en'] }, Libraries: [], SmartRules: [rule('Anime')] };
    const table = r.UI.libraryTable(r.host(), { settings, translate, defaults: neutral() });
    const genre = r.UI.genreRows(r.host(), { settings, t: translate, mine: () => settings.Everyday.Understood, reads: () => settings.Everyday.Reads || [] });
    const nodes = table.element.querySelectorAll('select'), genreNodes = genre.element.querySelector('tbody').querySelectorAll('select');
    select(nodes[0], 'mine'); select(genreNodes[0], 'original');
    settings.Everyday.Understood = ['de', 'en']; settings.Everyday.Reads = ['fr'];
    const before = copy(settings);
    table.refresh(); genre.refreshLanguages();
    assert.deepEqual(table.element.querySelectorAll('select'), nodes);
    assert.deepEqual(genre.element.querySelector('tbody').querySelectorAll('select'), genreNodes);
    for (const node of [nodes[0], genreNodes[0]]) {
        const mine = node.querySelectorAll('optgroup').find(g => g.getAttribute('label') === en['ui.table.myLanguages']);
        assert.deepEqual(mine.children.map(n => n.value), ['lang:de', 'lang:en']);
    }
    assert.equal(nodes[0].value, 'mine'); assert.equal(genreNodes[0].value, 'original');
    assert.deepEqual(copy(settings), before);
});
test('custom choice survives a move and language refresh, restoring only that column', () => {
    const r = runtime(), original = [{ Kind: 'OriginalLanguage' }, { Kind: 'Language', Language: 'de' }];
    const a = rule('Anime', 'a', { AudioChain: original, FutureField: true }), settings = { SmartRules: [a, rule('Drama')] };
    const c = genreControl(r, settings), body = c.control.element.querySelector('tbody');
    select(body.children[0].querySelector('select'), 'mine');
    body.children[0].querySelector('[data-action="down"]').click();
    c.control.refreshLanguages();
    const moved = body.children[1].querySelectorAll('select');
    assert.ok(moved[0].options.some(n => n.value === 'custom'));
    select(moved[1], 'off'); select(moved[0], 'custom');
    assert.deepEqual(copy(a.Settings.AudioChain), original); assert.equal(a.Settings.WhenAudioUnderstood, 'Off');
    assert.equal(a.Settings.FutureField, true);
});

test('catalogue is lazy, unselected, retryable and deduplicated by case', async () => {
    const r = runtime(), settings = { SmartRules: [] }; let attempts = 0;
    const c = genreControl(r, settings, () => { if (++attempts === 1) throw Error('offline'); return { Items: [{ Name: 'Anime' }, { Name: 'ANIME' }, { Name: 'Drama' }] }; });
    const el = c.control.element;
    assert.equal(attempts, 0); assert.equal(c.changes, 0);
    genreAdd(el).click(); await tick();
    assert.equal(el.querySelector('[role="alert"]').hidden, false);
    assert.equal(el.querySelector('[role="alert"]').textContent, en['ui.genres.loadError']);
    el.querySelectorAll('button').find(n => n.textContent === en['ui.genres.retry']).click(); await tick();
    const picker = el.querySelector('.bfui-genres-picker').querySelector('select');
    assert.equal(picker.value, ''); assert.equal(picker.options.length, 3); assert.equal(settings.SmartRules.length, 0);
    pickGenre(el, 'Anime'); assert.equal(c.changes, 1); assert.deepEqual(copy(settings.SmartRules[0]), rule('Anime', ''));
    genreAdd(el).click(); assert.equal(picker.options.some(n => n.value.toLowerCase() === 'anime'), false); assert.equal(attempts, 2);
});
test('malformed catalogue fails readably and can retry to an empty result', async () => {
    const r = runtime(); let calls = 0;
    const c = genreControl(r, { SmartRules: [] }, () => ++calls === 1 ? {} : []);
    genreAdd(c.control.element).click(); await tick();
    const retry = c.control.element.querySelectorAll('button').find(b => b.textContent === en['ui.genres.retry']);
    assert.equal(retry.hidden, false); retry.click(); await tick();
    assert.equal(c.control.element.querySelector('[role="alert"]').hidden, true);
    assert.ok(c.control.element.textContent.includes(en['ui.genres.none'])); assert.equal(c.changes, 0);
});
test('only recognized rows are edited; custom and unrelated stored fields survive', () => {
    const r = runtime(), visible = rule('Anime', 'a', { AudioChain: [{ Kind: 'OriginalLanguage' }, { Kind: 'Language', Language: 'de' }], AvoidCommentary: false, FutureField: 'keep' });
    visible.FutureProperty = { keep: true };
    const hidden = [
        Object.assign(rule('disabled'), { Enabled: false }),
        Object.assign(rule('multi'), { Conditions: [{ Kind: 'Genre', Values: ['Drama', 'Comedy'] }] }),
        Object.assign(rule('negative'), { Conditions: [{ Kind: 'Genre', Values: ['Anime'], Negate: true }] }),
        Object.assign(rule('pattern'), { Conditions: [{ Kind: 'Genre', Values: ['Anime'], IsRegex: true }] }),
        Object.assign(rule('tag'), { Conditions: [{ Kind: 'Tag', Values: ['Anime'] }] }),
        Object.assign(rule('two'), { Conditions: [{ Kind: 'Genre', Values: ['Anime'] }, { Kind: 'Tag', Values: ['X'] }] })
    ];
    const before = copy(hidden), settings = { SmartRules: [hidden[0], visible, ...hidden.slice(1)] }, c = genreControl(r, settings);
    const body = c.control.element.querySelector('tbody'); assert.equal(body.children.length, 1);
    const row = body.children[0], nodes = row.querySelectorAll('select');
    select(nodes[0], 'mine'); select(nodes[1], 'off'); select(nodes[0], 'custom');
    assert.equal(body.children[0], row); assert.deepEqual(row.querySelectorAll('select'), nodes);
    assert.deepEqual(copy(visible.Settings.AudioChain), [{ Kind: 'OriginalLanguage' }, { Kind: 'Language', Language: 'de' }]);
    assert.equal(visible.Settings.AvoidCommentary, false); assert.equal(visible.Settings.FutureField, 'keep');
    assert.deepEqual(visible.FutureProperty, { keep: true }); assert.equal(visible.Settings.WhenAudioUnderstood, 'Off');
    assert.deepEqual(hidden, before); assert.equal(c.changes, 3);
});
test('reorder/remove keeps unrelated rule slots/order and restores focus', () => {
    const r = runtime(), a = rule('Anime'), b = rule('Drama'), hidden = { Name: 'hidden', Enabled: false, FutureField: 42 };
    const settings = { SmartRules: [a, hidden, b] }, c = genreControl(r, settings), body = c.control.element.querySelector('tbody');
    body.children[0].querySelector('[data-action="down"]').click(); assert.deepEqual(settings.SmartRules, [b, hidden, a]); assert.ok(r.document.activeElement.isConnected);
    body.children[0].querySelector('[data-action="remove"]').click(); assert.deepEqual(settings.SmartRules, [hidden, a]);
    body.children[0].querySelector('[data-action="remove"]').click(); assert.deepEqual(settings.SmartRules, [hidden]);
    assert.equal(r.document.activeElement, genreAdd(c.control.element)); assert.equal(c.changes, 3);
});
test('100-row cap prevents fetch/add, clears after removing a row', () => {
    const r = runtime(), settings = { SmartRules: Array.from({ length: 100 }, (_, i) => rule('Genre ' + i)) }; let loaded = false;
    const c = genreControl(r, settings, () => { loaded = true; return []; });
    assert.equal(genreAdd(c.control.element).disabled, true); genreAdd(c.control.element).click(); assert.equal(loaded, false);
    const message = c.control.element.querySelector('[role="status"]'); assert.equal(message.hidden, false);
    c.control.element.querySelector('[data-action="remove"]').click(); assert.equal(settings.SmartRules.length, 99);
    assert.equal(genreAdd(c.control.element).disabled, false); assert.equal(message.hidden, true);
});
test('duplicate guard rejects a stale picker regardless of case', async () => {
    const r = runtime(), settings = { SmartRules: [] }, c = genreControl(r, settings);
    genreAdd(c.control.element).click(); await tick(); settings.SmartRules.push(rule('aNiMe', 'concurrent'));
    pickGenre(c.control.element, 'Anime'); assert.equal(settings.SmartRules.length, 1); assert.equal(c.changes, 0);
    assert.equal(c.control.element.querySelector('[role="alert"]').textContent, en['ui.genres.duplicate']);
});
test('fresh admin save merges only changed table fields and genre order', () => {
    const { UI } = runtime(), a = rule('Anime', 'a', { AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }], AvoidCommentary: false });
    const b = rule('Drama', 'b', { WhenAudioUnderstood: 'Off' }), hidden = { Enabled: false, Name: 'other', Future: 1 };
    const before = [a, hidden, b], work = copy(before), fresh = copy(before); UI.writeTableAudio(work[0].Settings, 'mine');
    Object.assign(fresh[0].Settings, { Future: 'concurrent', AvoidCommentary: true }); fresh[0].Name = 'concurrent rename'; fresh[1].Future = 2;
    const extra = rule('Comedy', 'new-concurrent'); fresh.push(extra);
    const result = UI.mergeGenreRows(before, [work[2], work[1], work[0], rule('Animation', '')], fresh);
    assert.deepEqual(copy(result).map(r => r.Id), ['b', undefined, 'a', 'new-concurrent', '']);
    assert.equal(result[1], fresh[1]); assert.equal(result[2].Name, 'concurrent rename');
    assert.equal(result[2].Settings.Future, 'concurrent'); assert.equal(result[2].Settings.AvoidCommentary, true);
    assert.deepEqual(copy(result[2].Settings.AudioChain), [{ Kind: 'MyLanguages' }, { Kind: 'VideoDefault' }]); assert.equal(result[3], extra);
    assert.equal(UI.mergeGenreRows(before, [work[0]], [hidden]).length, 1);
});

test('field-only genre save keeps the fresh concurrent order', () => {
    const { UI } = runtime();
    const before = [rule('Anime', 'a'), rule('Drama', 'b')], edited = copy(before);
    UI.writeTableAudio(edited[0].Settings, 'original');
    const fresh = [copy(before[1]), copy(before[0])], b = fresh[0], a = fresh[1];
    a.Settings.FutureField = 'concurrent';
    const result = UI.mergeGenreRows(before, edited, fresh);
    assert.deepEqual(copy(result).map(r => r.Id), ['b', 'a']);
    assert.equal(result[0], b); assert.equal(result[1], a);
    assert.deepEqual(copy(a.Settings.AudioChain), [{ Kind: 'OriginalLanguage' }, { Kind: 'VideoDefault' }]);
    assert.equal(a.Settings.FutureField, 'concurrent');
});
test('removing a genre does not move survivors past concurrent additions', () => {
    const { UI } = runtime();
    const before = [rule('Anime', 'a'), rule('Drama', 'b')];
    const fresh = [copy(before[0]), rule('Comedy', 'c'), copy(before[1])];
    const result = UI.mergeGenreRows(before, [copy(before[1])], fresh);
    assert.deepEqual(copy(result).map(r => r.Id), ['c', 'b']);
    assert.equal(result[0], fresh[1]); assert.equal(result[1], fresh[2]);
});
test('explicit survivor reorder still applies alongside removals and concurrent rows', () => {
    const { UI } = runtime();
    const before = [rule('Anime', 'a'), rule('Drama', 'b'), rule('Comedy', 'c')];
    const fresh = [copy(before[0]), rule('Animation', 'concurrent'), copy(before[2]), copy(before[1])];
    const result = UI.mergeGenreRows(before, [copy(before[2]), copy(before[0])], fresh);
    assert.deepEqual(copy(result).map(r => r.Id), ['c', 'concurrent', 'a']);
    assert.equal(result[1], fresh[1]);
});
test('two admins adding the same genre keep the fresh row and all its choices', () => {
    const { UI } = runtime();
    for (const name of ['Anime', 'ANIME', ' anime ']) {
        const local = rule(name, '', {});
        UI.writeTableAudio(local.Settings, 'original');
        UI.writeTableSubtitles(local.Settings, 'off');
        const server = rule('Anime', 'server-assigned', {
            AudioChain: [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }],
            WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['en'], FutureField: 'keep'
        });
        server.FutureProperty = true;
        const original = copy(server), localOriginal = copy(local);
        const result = UI.mergeGenreRows([], [local], [server]);
        assert.equal(result.length, 1, name);
        assert.equal(result[0], server);
        assert.deepEqual(copy(server), original, 'The fresh row wins the whole addition conflict.');
        assert.deepEqual(copy(local), localOriginal);
    }
});
test('additions preserve fresh priority, skip a concurrent duplicate and append distinct genres', () => {
    const { UI } = runtime();
    const before = [rule('Drama', 'a'), rule('Comedy', 'b')];
    const local = rule('Anime', '', {}), distinct = rule('Animation', '', {});
    UI.writeTableAudio(local.Settings, 'mine');
    const fresh = [copy(before[1]), rule('ANIME', 'concurrent'), copy(before[0])];
    const original = copy(fresh);
    const result = UI.mergeGenreRows(before, copy(before).concat([local, distinct]), fresh);
    assert.deepEqual(copy(result).map(r => r.Id), ['b', 'concurrent', 'a', '']);
    assert.deepEqual(copy(result.slice(0, 3)), original);
    assert.equal(result[1], fresh[1]);
    assert.equal(result[3].Conditions[0].Values[0], 'Animation');
});


test('new genre placement: Add Drama then Move up saves Drama before existing Kids', async () => {
    const r = runtime(), before = [rule('Kids', 'r1')], settings = { SmartRules: copy(before) };
    const c = genreControl(r, settings, () => ({ Items: [{ Name: 'Drama' }] }));
    genreAdd(c.control.element).click(); await tick();
    pickGenre(c.control.element, 'Drama');
    const body = c.control.element.querySelector('tbody');
    body.children[1].querySelector('[data-action="up"]').click();
    assert.deepEqual(settings.SmartRules.map(row => row.Name), ['Drama', 'Kids']);
    const saved = r.UI.mergeGenreRows(before, settings.SmartRules, copy(before));
    assert.deepEqual(copy(saved).map(row => row.Name), ['Drama', 'Kids']);
});
test('new genre rows retain their edited position around surviving existing rows', () => {
    const { UI } = runtime(), kids = rule('Kids', 'kids'), drama = rule('Drama', 'drama');
    const cases = [
        { before: [kids], edited: [rule('Drama', ''), copy(kids)], fresh: [copy(kids)], expected: ['Drama', 'Kids'] },
        { before: [kids, drama], edited: [copy(kids), rule('Comedy', ''), copy(drama)], fresh: [copy(kids), copy(drama)], expected: ['Kids', 'Comedy', 'Drama'] },
        { before: [kids], edited: [rule('Drama', ''), rule('Comedy', ''), copy(kids)], fresh: [copy(kids)], expected: ['Drama', 'Comedy', 'Kids'] },
        { before: [kids, drama], edited: [rule('Comedy', ''), copy(kids), copy(drama)], fresh: [copy(drama)], expected: ['Comedy', 'Drama'] },
        { before: [kids], edited: [rule('Drama', ''), copy(kids)], fresh: [], expected: ['Drama'] }
    ];
    for (const c of cases) {
        const saved = UI.mergeGenreRows(c.before, c.edited, c.fresh);
        assert.deepEqual(copy(saved).map(row => row.Name), c.expected);
    }
});
test('moving a new genre leaves fresh existing order and concurrent rows intact', () => {
    const { UI } = runtime(), before = [rule('Kids', 'kids'), rule('Drama', 'drama')];
    const edited = [copy(before[0]), rule('Comedy', ''), copy(before[1])];
    const concurrent = rule('Animation', 'concurrent', { FutureField: true });
    const fresh = [copy(before[1]), concurrent, copy(before[0])];
    const saved = UI.mergeGenreRows(before, edited, fresh);
    // Comedy moved up from the end to before Drama; saved rows retain the fresh Drama/Animation/Kids order.
    assert.deepEqual(copy(saved).map(row => row.Name), ['Comedy', 'Drama', 'Animation', 'Kids']);
    assert.deepEqual(saved.filter(row => row.Id), fresh);
    assert.equal(saved[2], concurrent);
});
test('moving a new duplicate never repositions or overwrites the fresh winner', () => {
    const { UI } = runtime(), before = [rule('Kids', 'kids')];
    const local = rule(' anime ', '', {}); UI.writeTableAudio(local.Settings, 'mine');
    const freshWinner = rule('Anime', 'server-anime', { FutureField: true });
    UI.writeTableAudio(freshWinner.Settings, 'original');
    const fresh = [copy(before[0]), freshWinner], snapshot = copy(fresh);
    const saved = UI.mergeGenreRows(before, [local, copy(before[0])], fresh);
    assert.deepEqual(copy(saved), snapshot);
    assert.equal(saved[1], freshWinner);
});

test('person page mounts below library table, requests viewer genres and stays unsaved', async () => {
    const r = runtime(), app = r.app(), doc = { Everyday: {}, Libraries: [], SmartRules: [] };
    Object.assign(app.state, { doc, base: copy(doc), credentials: { userId: 'person-viewer' }, defaults: neutral() });
    r.respond(() => ({ Items: [{ Name: 'Anime' }] }));
    const section = app.tableSection(); r.host().appendChild(section);
    assert.equal(section.querySelector('.bfui-table-wrap').nextSibling.className, 'bfui bfui-genres'); assert.equal(r.calls.length, 0);
    genreAdd(section.querySelector('.bfui-genres')).click(); await tick();
    assert.equal(new URL(r.calls[0].url, r.window.location.origin).pathname, '/jellyfin/Genres'); assert.equal(new URL(r.calls[0].url, r.window.location.origin).searchParams.get('UserId'), 'person-viewer');
    pickGenre(section.querySelector('.bfui-genres'), 'Anime'); assert.equal(app.isDirty(), true);
    assert.equal(r.calls.length, 1); assert.ok(r.calls.every(c => c.options.method === 'GET'));
});
test('admin Defaults use the admin catalogue; People use the edited person’s visible genres', async () => {
    const r = runtime(), { api } = r.admin(); r.respond(() => ({ Items: [{ Name: 'Drama' }] }));
    for (const audience of ['admin', 'person']) {
        const host = r.host(), settings = { Everyday: {}, Libraries: [], SmartRules: [], UserId: 'edited-person' }; let changes = 0;
        assert.equal(api.mountLibraryTable(host, { settings, defaults: neutral(), libraries: [], audience, onChange: () => changes++ }), true);
        const genres = host.querySelector('.bfui-genres'); genreAdd(genres).click(); await tick();
        assert.equal(new URL(r.calls.at(-1).url, r.window.location.origin).searchParams.get('UserId'), audience === 'admin' ? 'signed-in-viewer' : 'edited-person');
        pickGenre(genres, 'Drama'); assert.equal(changes, 1); assert.equal(settings.SmartRules[0].Id, '');
    }
    assert.ok(r.calls.every(c => c.options.method === 'GET'));
});
test('admin Defaults records edits in existing save bar, preserving unrelated fresh settings', async () => {
    const r = runtime(); r.host('bfLibraries'); r.host('bfSaveBar');
    const { api, state } = r.admin({ config: { SmartRules: [rule('Anime', 'a')], Everyday: {}, Libraries: [] } });
    r.respond(call => call.url.includes('/Libraries') || call.url.includes('/Languages') ? [] : null); await api.renderLibraries();
    const genres = r.document.getElementById('bfLibraries').querySelector('.bfui-genres'), audio = genres.querySelector('tbody').querySelector('select');
    select(audio, 'original'); assert.equal(typeof state.pending.GenreRows, 'function'); assert.equal(state.pending.LibrariesTable, undefined);
    assert.equal(r.document.getElementById('bfSaveBar').classList.contains('hide'), false);
    const fresh = { SmartRules: [rule('Anime', 'a', { FutureField: true }), { Enabled: false, Name: 'untouched' }] };
    state.pending.GenreRows(fresh); assert.equal(fresh.SmartRules[0].Settings.FutureField, true);
    assert.deepEqual(copy(fresh.SmartRules[0].Settings.AudioChain), [{ Kind: 'OriginalLanguage' }, { Kind: 'VideoDefault' }]);
    assert.equal(fresh.SmartRules[1].Name, 'untouched'); assert.ok(r.calls.every(c => c.options.method === 'GET'));
});

test('People editor holds genre edits until its existing Save with keepOverrides', async () => {
    const r = runtime(); r.host('bfPeople');
    const { api, state } = r.admin({ person: { id: 'edited-person', dirty: false } });
    let saved = null;
    r.respond(call => {
        if (call.options.method === 'PUT') { saved = call; return JSON.parse(call.options.body); }
        return [];
    });
    const host = r.host(), doc = { UserId: 'edited-person', Everyday: {}, Libraries: [], SmartRules: [rule('Anime', 'a', { FutureField: 'keep' })] };
    api.personEditor(host, { UserId: 'edited-person', Name: 'Person' }, doc, neutral(), [], { rows: [] });
    const genre = host.querySelector('.bfui-genres');
    select(genre.querySelector('tbody').querySelector('select'), 'mine');
    assert.equal(saved, null); assert.equal(state.person.dirty, true);
    const save = host.querySelectorAll('button').find(b => b.textContent === adminEn['common.save']);
    assert.equal(save.disabled, false); save.click(); await tick();
    assert.ok(saved); assert.equal(new URL(saved.url).searchParams.get('keepOverrides'), 'true');
    assert.equal(new URL(saved.url).pathname, '/jellyfin/Babelfin/Admin/Users/edited-person/Settings');
    const sent = JSON.parse(saved.options.body);
    assert.equal(sent.SmartRules[0].Settings.FutureField, 'keep');
    assert.deepEqual(sent.SmartRules[0].Settings.AudioChain, [{ Kind: 'MyLanguages' }, { Kind: 'VideoDefault' }]);
    assert.equal(doc.SmartRules[0].Settings.AudioChain, undefined);
});


for (const outcome of ['success', 'failure', 'cancel']) {
    test('copying neutral settings refreshes admin choice status only after success: ' + outcome, async () => {
        const r = runtime();
        r.host('bfPeople');
        const head = r.host(); head.className = 'bf-head';
        const label = r.host(), input = r.document.createElement('input');
        input.id = 'bfEnabled'; label.appendChild(input);
        const { api } = r.admin({
            person: { id: 'source-person', dirty: false },
            userList: [{ UserId: 'source-person', Name: 'Source' }, { UserId: 'target-person', Name: 'Target' }]
        });
        let serverHasChoices = true;
        r.respond(call => {
            const url = new URL(call.url);
            if (url.pathname.endsWith('/ChoiceStatus')) return { HasChoices: serverHasChoices };
            if (call.options.method === 'POST' && url.pathname.endsWith('/CopyTo/target-person')) {
                if (outcome === 'failure') throw Object.assign(new Error('Copy failed'), { status: 503 });
                serverHasChoices = false;
                return { SkippedItemIds: [] };
            }
            return [];
        });
        api.renderHeader(); await tick();
        const status = r.document.getElementById('bfChoiceStatus');
        assert.equal(status.hidden, true); // The target is the last configured person.
        const host = r.host();
        api.personEditor(host, { UserId: 'source-person', Name: 'Source' },
            { UserId: 'source-person', Everyday: {}, Libraries: [], SmartRules: [] }, neutral(), [], { rows: [] });
        select(host.querySelector('.bf-copy').querySelector('select'), 'target-person');
        host.querySelectorAll('button').find(b => b.textContent === adminEn['people.copy']).click();
        const dialog = r.document.querySelector('.bfui-dialog-actions');
        assert.ok(dialog);
        assert.equal(r.calls.filter(c => c.url.includes('/ChoiceStatus')).length, 1);
        if (outcome === 'cancel') dialog.children[0].click();
        else dialog.children[1].click();
        await tick();
        const statusCalls = r.calls.filter(c => c.url.includes('/ChoiceStatus'));
        const copies = r.calls.filter(c => c.options.method === 'POST' && c.url.includes('/CopyTo/'));
        assert.equal(copies.length, outcome === 'cancel' ? 0 : 1);
        assert.equal(statusCalls.length, outcome === 'success' ? 2 : 1);
        assert.equal(status.hidden, outcome !== 'success');
        if (outcome === 'success') assert.ok(r.calls.indexOf(statusCalls[1]) > r.calls.indexOf(copies[0]));
    });
}

test('status is silent pending/error/unknown and ignores stale responses', async () => {
    const r = runtime(); let resolve, response = new Promise(done => { resolve = done; });
    const status = r.UI.choiceStatus({ text: en['app.noChoices'], load: () => response });
    assert.equal(status.element.hidden, true); await tick(); resolve({ HasChoices: false }); await tick(); assert.equal(status.element.hidden, false);
    response = Promise.reject(Error('offline')); await status.refresh(); assert.equal(status.element.hidden, true);
    for (const answer of [{ HasChoices: true }, {}, null, { HasChoices: 'false' }]) { response = answer; await status.refresh(); assert.equal(status.element.hidden, true); }
    let old; response = new Promise(done => { old = done; }); status.refresh(); await tick(); response = { HasChoices: true }; await status.refresh();
    old({ HasChoices: false }); await tick(); assert.equal(status.element.hidden, true);
});
test('person endpoint is exact and both disabled messages stay', async () => {
    const r = runtime(), app = r.app(); app.state.doc = { Enabled: false }; app.state.ping = { enabled: false };
    r.respond(() => ({ HasChoices: false })); const header = app.header(true); r.host().appendChild(header);
    const line = header.querySelector('[role="status"]'); assert.equal(line.hidden, true);
    assert.ok(header.textContent.includes(en['app.serverOff'])); assert.ok(header.textContent.includes(en['app.personOff']));
    await tick(); assert.equal(line.hidden, false); assert.equal(new URL(r.calls[0].url, r.window.location.origin).pathname, '/jellyfin/Babelfin/Me/ChoiceStatus');
    assert.equal(line.textContent, en['app.noChoices']);
});
test('admin top status uses exact endpoint and replaces previous header status', async () => {
    const r = runtime(), head = r.host(); head.className = 'bf-head';
    const label = r.host(), input = r.document.createElement('input'); input.id = 'bfEnabled'; label.appendChild(input);
    const { api } = r.admin(); r.respond(() => ({ HasChoices: false })); api.renderHeader();
    const status = r.document.getElementById('bfChoiceStatus'); assert.equal(status.parentNode, head); assert.equal(status.hidden, true);
    await tick(); assert.equal(status.hidden, false); assert.equal(new URL(r.calls[0].url, r.window.location.origin).pathname, '/jellyfin/Babelfin/Admin/ChoiceStatus');
    api.renderHeader(); assert.equal(status.isConnected, false); assert.equal(r.document.querySelectorAll('#bfChoiceStatus').length, 1);
});
test('Auth-ready parent still waits for a delayed current user; device identity stays', async () => {
    const r = runtime(), app = r.app(); let user = '';
    const parent = { location: { origin: r.window.location.origin }, ApiClient: { getUrl: r.apiClient.getUrl, ajax() {}, getCurrentUserId: () => user } };
    r.window.parent = parent; let now = 0; r.context.Date = { now: () => now++ }; r.context.setTimeout = done => { user = 'ready-viewer'; done(); };
    const client = await app.readyFrameClient(); assert.equal(client, parent.ApiClient); assert.equal(client.getCurrentUserId(), 'ready-viewer');
    app.state.credentials = { token: 'token', client: 'Original client', deviceName: 'Original device', deviceId: 'original-id', version: 'original-version' };
    assert.ok(app.authHeader().includes('Device="Original device"')); assert.ok(app.authHeader().includes('DeviceId="original-id"'));
});

test('Pre-play checkbox remains editable through existing pending saves', async () => {
    const r = runtime(); r.host('bfSaveBar');
    const { api, state } = r.admin({ restorers: [], config: {} });
    r.respond(call => call.url.includes('/Apps') ? [{ Client: 'Jellyfin Web', Mode: 'Trust', RememberPrePlayChoices: true }] : {});
    const host = r.host(); api.appsGroup(host); await state.appsReady;
    const checkbox = host.querySelectorAll('input').find(n => (n.getAttribute('aria-label') || '').includes(adminEn['apps.rememberPrePlay']));
    assert.ok(checkbox); assert.equal(checkbox.checked, true);
    checkbox.checked = false; checkbox.fire('change');
    const apply = state.pending['app.remember.jellyfin web']; assert.equal(typeof apply, 'function');
    const fresh = { AppModes: [{ Client: 'Jellyfin Web', Mode: 'Trust', Future: 1 }] }; apply(fresh);
    assert.equal(fresh.AppModes[0].RememberPrePlayChoices, false); assert.equal(fresh.AppModes[0].Future, 1);
});
test('new-user template keeps its language control and has no genre control', async () => {
    const r = runtime(), { api } = r.admin(); r.respond(() => []);
    const host = r.host(); api.templateGroup(host); await tick();
    assert.equal(host.querySelector('.bfui-genres'), null);
    assert.ok(host.textContent.includes(adminEn['template.title']));
    assert.ok(!r.calls.some(c => c.url.includes('/Genres')));
});

test('All 16 locales have parallel keys, natural new copy and exact pt/zh aliases', () => {
    for (const folder of ['i18n', 'i18n-admin']) {
        const files = fs.readdirSync(path.join(assets, folder)).filter(f => f.endsWith('.json')); assert.equal(files.length, 16);
        const dictionaries = files.map(f => [f, JSON.parse(source(folder + '/' + f))]), expected = Object.keys(dictionaries[0][1]).sort();
        for (const [file, dict] of dictionaries) {
            assert.deepEqual(Object.keys(dict).sort(), expected, folder + '/' + file);
            const newKeys = folder === 'i18n' ? Object.keys(en).filter(k => k.startsWith('ui.genres.') || (k.startsWith('ui.table.audioMyLanguages') || k.startsWith('ui.table.audioOriginal')) || k === 'app.noChoices') : ['page.noChoices'];
            for (const key of newKeys) {
                assert.equal(typeof dict[key], 'string', file + ':' + key); assert.ok(dict[key].trim().length, file + ':' + key);
                assert.ok(!/\b(understand|understood|reads?|reading)\b/i.test(dict[key]), file + ':' + key);
                if (file !== 'en.json' && key !== 'ui.genres.title') assert.notEqual(dict[key], en[key] || adminEn[key], file + ':' + key);
            }
        }
        assert.equal(source(folder + '/pt.json'), source(folder + '/pt-PT.json')); assert.equal(source(folder + '/zh.json'), source(folder + '/zh-Hans.json'));
    }
    const languages = source('app.js').split('function languagesSection()')[1].split('// ----- Audio and subtitles')[0];
    assert.ok(!languages.includes('infoTip'));
});
test('Modified JavaScript and this portable test pass Node syntax checks', () => {
    for (const file of ['ui.js', 'app.js', 'dashboard.js', __filename]) {
        const result = spawnSync(process.execPath, ['--check', path.isAbsolute(file) ? file : path.join(assets, file)], { encoding: 'utf8' });
        assert.equal(result.status, 0, result.stderr);
    }
});
