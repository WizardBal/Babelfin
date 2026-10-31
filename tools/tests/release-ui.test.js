'use strict';
// Runs the complete shipped client and shared dropdowns, with no production instrumentation.
// The DOM models persistent nodes, select values and event propagation; API calls stay in memory.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assets = path.resolve(__dirname, '../../plugin/src/Jellyfin.Plugin.Babelfin/Assets');
const source = file => fs.readFileSync(path.join(assets, file), 'utf8');
const locales = fs.readdirSync(path.join(assets, 'i18n')).filter(f => f.endsWith('.json')).sort();
const en = JSON.parse(source('i18n/en.json'));
const seriesId = '11111111111111111111111111111111';
const movieId = '22222222222222222222222222222222';
const episodeId = '33333333333333333333333333333333';
const seasonId = '44444444444444444444444444444444';
const videoId = '55555555555555555555555555555555';
const cases = [
    { type: 'Series', id: seriesId, target: seriesId, scope: 'Series', key: 'show' },
    { type: 'Season', id: seasonId, target: seriesId, scope: 'Series', key: 'show' },
    { type: 'Episode', id: episodeId, target: seriesId, scope: 'Series', key: 'show' },
    { type: 'Movie', id: movieId, target: movieId, scope: 'Title', key: 'movie' },
    { type: 'Video', id: videoId, target: videoId, scope: 'Title', key: 'video' }
];

class DomEvent {
    constructor(type, options = {}) { Object.assign(this, { type, bubbles: false, detail: 0 }, options); }
    preventDefault() { this.defaultPrevented = true; }
    stopPropagation() { this.stopped = true; }
}

class Node {
    constructor(tag, doc, text = '') {
        Object.assign(this, { tagName: tag.toUpperCase(), nodeType: tag === '#text' ? 3 : 1, ownerDocument: doc,
            childNodes: [], parentNode: null, attrs: {}, style: {}, listeners: {}, className: '', _text: text,
            _value: '', disabled: false, hidden: false, checked: false });
        this.classList = {
            contains: name => this.className.split(/\s+/).includes(name),
            add: (...names) => { this.className = [...new Set(this.className.split(/\s+/).filter(Boolean).concat(names))].join(' '); },
            remove: (...names) => { this.className = this.className.split(/\s+/).filter(n => !names.includes(n)).join(' '); },
            toggle: (name, force) => { const on = force === undefined ? !this.classList.contains(name) : !!force; this.classList[on ? 'add' : 'remove'](name); return on; }
        };
    }
    get children() { return this.childNodes.filter(n => n.nodeType === 1); }
    get firstChild() { return this.childNodes[0] || null; }
    get nextSibling() { return this.parentNode?.childNodes[this.parentNode.childNodes.indexOf(this) + 1] || null; }
    get textContent() { return this._text + this.childNodes.map(n => n.textContent).join(''); }
    set textContent(v) { this.childNodes.forEach(n => { n.parentNode = null; }); this.childNodes = []; this._text = String(v); }
    set innerHTML(v) { assert.equal(v, '', 'Only clearing a node is needed by these scripts'); this.textContent = ''; }
    get id() { return this.getAttribute('id') || ''; }
    set id(v) { this.setAttribute('id', v); }
    get options() { return this.querySelectorAll('option'); }
    get selectedIndex() { return this._selected ? this.options.indexOf(this._selected) : (this.options.length ? 0 : -1); }
    set selectedIndex(v) { this._selected = this.options[v]; }
    get value() { return this.tagName === 'SELECT' ? (this.options[this.selectedIndex]?.value || '') : this._value; }
    set value(v) { if (this.tagName === 'SELECT') this._selected = this.options.find(n => n.value === String(v)); else this._value = String(v); }
    get label() { return this.getAttribute('label') || ''; }
    get selected() {
        let select = this.parentNode; if (select?.tagName === 'OPTGROUP') select = select.parentNode;
        return select?.tagName === 'SELECT' && select.options[select.selectedIndex] === this;
    }
    get visible() { return !this.hidden && (this.parentNode ? this.parentNode.visible : true); }
    get offsetWidth() { return this.visible ? 260 : 0; }
    get offsetHeight() { return this.visible ? 40 : 0; }
    get isConnected() { return this === this.ownerDocument || !!this.parentNode?.isConnected; }
    appendChild(n) { return this.insertBefore(n, null); }
    insertBefore(n, before) {
        if (n.parentNode) n.parentNode.removeChild(n);
        const i = before ? this.childNodes.indexOf(before) : this.childNodes.length;
        assert.ok(i >= 0); this.childNodes.splice(i, 0, n); n.parentNode = this; return n;
    }
    removeChild(n) { const i = this.childNodes.indexOf(n); assert.ok(i >= 0); this.childNodes.splice(i, 1); n.parentNode = null; return n; }
    setAttribute(k, v) {
        this.attrs[k] = String(v);
        if (k === 'class') this.className = String(v);
        if (k === 'value') this.value = v;
        if (k === 'hidden') this.hidden = true;
    }
    getAttribute(k) { return k === 'class' ? this.className : (this.attrs[k] ?? null); }
    hasAttribute(k) { return this.getAttribute(k) !== null; }
    removeAttribute(k) { delete this.attrs[k]; if (k === 'hidden') this.hidden = false; }
    addEventListener(type, fn, capture = false) { (this.listeners[type] ||= []).push({ fn, capture }); }
    removeEventListener(type, fn) { this.listeners[type] = (this.listeners[type] || []).filter(e => e.fn !== fn); }
    dispatchEvent(e) {
        e.target = this;
        const chain = []; for (let n = this; n; n = n.parentNode) chain.push(n);
        const run = (n, capture) => { e.currentTarget = n; for (const l of [...(n.listeners[e.type] || [])]) if (!!l.capture === capture) l.fn(e); };
        for (const n of [...chain].reverse()) { run(n, true); if (e.stopped) return !e.defaultPrevented; }
        for (const n of chain) { run(n, false); if (e.stopped || !e.bubbles) break; }
        return !e.defaultPrevented;
    }
    fire(type, props) { return this.dispatchEvent(new DomEvent(type, props)); }
    click() { if (!this.disabled) this.fire('click', { bubbles: true }); }
    focus() { this.ownerDocument.activeElement = this; }
    contains(n) { return n === this || this.childNodes.some(c => c.contains(n)); }
    matches(s) {
        let excluded = false;
        s = s.replace(/:not\(([^)]+)\)/g, (_, q) => { if (this.matches(q)) excluded = true; return ''; });
        if (excluded) return false;
        const tag = /^[\w-]+/.exec(s); if (tag && this.tagName.toLowerCase() !== tag[0].toLowerCase()) return false;
        const id = /#([\w-]+)/.exec(s); if (id && this.id !== id[1]) return false;
        for (const [, name] of s.matchAll(/\.([\w-]+)/g)) if (!this.classList.contains(name)) return false;
        for (const [, name, quote, value] of s.matchAll(/\[([\w-]+)(?:=(["']?)([^"'\]]*)\2)?\]/g))
            if (!this.hasAttribute(name) || (value !== undefined && this.getAttribute(name) !== value)) return false;
        return true;
    }
    querySelectorAll(s) {
        const out = [];
        for (const c of this.children) { if (s.split(',').some(q => c.matches(q.trim()))) out.push(c); out.push(...c.querySelectorAll(s)); }
        return out;
    }
    querySelector(s) { return this.querySelectorAll(s)[0] || null; }
    cloneNode(deep) {
        const n = new Node(this.tagName, this.ownerDocument, this._text); Object.entries(this.attrs).forEach(([k, v]) => n.setAttribute(k, v)); n.className = this.className;
        if (deep) this.childNodes.forEach(c => n.appendChild(c.cloneNode(true))); return n;
    }
    getBoundingClientRect() { return { left: 20, top: 20, bottom: 60, width: this.offsetWidth, height: this.offsetHeight }; }
    scrollIntoView() {}
}

async function browser(c, dictionary = en, { fallback = false, flags = [], tv = false, pageCase = c, fromCard = false, language = 'en' } = {}) {
    const document = new Node('#document'); document.ownerDocument = document;
    document.createElement = tag => new Node(tag, document);
    document.createElementNS = (_, tag) => document.createElement(tag);
    document.createTextNode = text => new Node('#text', document, String(text));
    document.getElementById = id => document.querySelector('#' + id);
    document.getElementsByTagName = tag => document.querySelectorAll(tag);
    document.documentElement = document.appendChild(document.createElement('html'));
    document.head = document.documentElement.appendChild(document.createElement('head'));
    document.body = document.documentElement.appendChild(document.createElement('body'));
    if (tv) document.documentElement.classList.add('layout-tv');
    document.currentScript = { src: 'https://example.test/jf/Babelfin/client.js' };
    const page = document.body.appendChild(document.createElement('div')); page.id = 'itemDetailPage';
    const primary = page.appendChild(document.createElement('div')); primary.className = 'detailPagePrimaryContainer';
    const tracks = primary.appendChild(document.createElement('div')); tracks.className = 'trackSelections';
    function trackSelect(className, values, value) {
        const select = tracks.appendChild(document.createElement('select')); select.className = className;
        values.forEach(v => { const o = select.appendChild(document.createElement('option')); o.value = v; o.textContent = String(v); });
        select.value = value; return select;
    }
    const audioTrack = trackSelect('selectAudio', [0, 1], 0), subtitleTrack = trackSelect('selectSubtitles', [-1, 2], 2);
    const more = page.appendChild(document.createElement('button')); more.className = 'btnMoreCommands';
    let trigger = more;
    if (fromCard) {
        const card = page.appendChild(document.createElement('div')); card.setAttribute('data-id', c.id); card.setAttribute('data-type', c.type);
        trigger = card.appendChild(document.createElement('button')); trigger.setAttribute('data-action', 'menu');
    }
    const calls = [], warnings = [], frames = [], timers = new Map(); let timerId = 0;
    const window = new Node('window', document);
    const location = new URL('https://example.test/jf/web/#/details?id=' + pageCase.id);
    const t = (key, values) => String(dictionary[key] || key).replace(/\{(\w+)\}/g, (all, k) => values?.[k] ?? all);
    t.has = key => Object.hasOwn(dictionary, key); t.language = language;
    Object.assign(window, { document, location, Promise, fetch() {}, MutationObserver: class { observe() {} },
        innerWidth: 1024, innerHeight: 768, Intl, requestAnimationFrame: fn => frames.push(fn),
        BabelfinI18n: { detectLanguage: () => t.language, load: async () => fallback ? null : t } });
    const target = { TargetId: c.target, Scope: c.scope, Type: c.scope === 'Series' ? 'Series' : c.type,
        Name: 'Test title', LibraryName: 'Test library', Inherited: { Profile: { Understood: ['en'], Reads: ['en'] } } };
    let burned = JSON.parse(JSON.stringify(flags));
    window.ApiClient = {
        getCurrentUserId: () => 'viewer',
        getUrl: (p, q) => 'https://example.test/jf/' + p + (q ? '?' + new URLSearchParams(q) : ''),
        getItem: async (_, id) => {
            const item = cases.find(c => c.id === id); assert.ok(item);
            return { Type: item.type, SeriesId: item.scope === 'Series' ? seriesId : null };
        },
        async getJSON(url) {
            calls.push({ method: 'GET', url });
            if (url.includes('/Overrides/Target/')) return target;
            if (url.includes('/Me/Settings')) return { Settings: { Everyday: { Understood: ['en'], Reads: ['en'] }, BurnedIn: burned } };
            if (url.includes('/Languages')) return [{ Code: 'en', Base: 'en', Name: 'English' }, { Code: 'ja', Base: 'ja', Name: 'Japanese' }];
            if (url.includes('/Me/Choice')) return [{ AudioIndex: 1, SubtitleIndex: -1, Outcome: 'Selected', ForItemId: pageCase.id }];
            throw new Error('Unexpected GET ' + url);
        },
        async ajax(r) {
            const body = r.data && JSON.parse(r.data); calls.push({ method: r.type, url: r.url, body });
            if (r.type === 'POST' && r.url.includes('/BurnedIn/')) burned = [{ ItemId: c.target, Scope: body.Scope, Languages: body.Languages }];
            if (r.type === 'DELETE' && r.url.includes('/BurnedIn/')) burned = [];
            if (r.type === 'PUT' && r.url.includes('/Overrides/')) target.Override = { Settings: body };
            if (r.type === 'DELETE' && r.url.includes('/Overrides/')) delete target.Override;
        }
    };
    const context = vm.createContext({ window, document, location, navigator: { language: 'en' }, URL, URLSearchParams, Intl,
        Promise, MutationObserver: window.MutationObserver, Event: DomEvent, MouseEvent: DomEvent,
        localStorage: { getItem: () => null }, console: { warn: (...args) => warnings.push(args), log() {}, info() {} },
        setTimeout: (fn, ms) => { timers.set(++timerId, { fn, ms }); return timerId; }, clearTimeout: id => timers.delete(id) });
    vm.runInContext(source('ui.js'), context, { filename: 'ui.js' });
    vm.runInContext(source('client.js'), context, { filename: 'client.js' });
    async function settle() {
        for (let i = 0; i < 40; i++) { await Promise.resolve(); const pending = frames.splice(0); pending.forEach(fn => fn()); }
        assert.deepEqual(warnings, [], 'Client must not silently fail in the DOM model');
    }
    async function open() {
        trigger.click();
        const container = document.body.appendChild(document.createElement('div')); container.className = 'dialogContainer';
        const sheet = container.appendChild(document.createElement('div')); sheet.className = 'actionSheet';
        const row = sheet.appendChild(document.createElement('button')); row.className = 'actionSheetMenuItem'; row.setAttribute('data-id', 'addtoplaylist');
        row.appendChild(document.createElement('span')).className = 'actionSheetItemText';
        // A real sheet reports closing/close after its backdrop is activated.
        container.addEventListener('mousedown', e => { if (e.target === container) sheet.fire('closing'); });
        container.addEventListener('click', e => {
            if (e.target === container) { document.body.removeChild(container); sheet.fire('close'); }
        });
        document.fire('viewshow', { target: page });
        await settle();
        const entry = sheet.querySelector('.bfSheetItem'); assert.ok(entry, 'The existing ⋮ menu must offer Babelfin');
        assert.equal(entry.textContent, dictionary['client.menu.override'] || en['client.menu.override']); entry.click();
        await settle();
        const dialog = document.querySelector('.bfDialog'); assert.ok(dialog, 'Menu activation renders the dialog');
        assert.equal(document.querySelector('.actionSheet'), null, 'The Jellyfin sheet closes');
        return dialog;
    }
    await settle();
    return { document, window, page, more, trigger, audioTrack, subtitleTrack, calls, settle, open, target };
}

test('All 16 locale dictionaries retain translated scope labels and matching aliases', () => {
    assert.equal(locales.length, 16);
    for (const file of locales) {
        const dict = JSON.parse(source('i18n/' + file));
        const labels = ['show', 'movie', 'video'].map(kind => dict['dialog.burnedIn.' + kind]);
        assert.equal(new Set(labels).size, 3, file + ': distinct scopes');
        for (const [i, text] of labels.entries()) {
            assert.equal(typeof text, 'string'); assert.ok(text.trim());
            assert.doesNotMatch(text, /\b(understand|understood|reads?|reading)\b/i, file);
            if (file !== 'en.json') assert.notEqual(text, en['dialog.burnedIn.' + ['show', 'movie', 'video'][i]], file);
        }
    }
    for (const [alias, canonical] of [['pt', 'pt-PT'], ['zh', 'zh-Hans']]) assert.equal(source('i18n/' + alias + '.json'), source('i18n/' + canonical + '.json'));
});

for (const file of locales) {
    test(file + ': rendered label and accessible language name match the server target scope', async () => {
        const dict = JSON.parse(source('i18n/' + file));
        for (const c of cases) {
            const r = await browser(c, dict, { language: file.slice(0, -5) }), d = await r.open();
            const burn = d.querySelector('.bfBurned'); assert.ok(burn);
            assert.equal(burn.querySelector('.bfRadioLabel').textContent, dict['dialog.burnedIn.' + c.key], c.type);
            assert.equal(burn.querySelector('select').getAttribute('aria-label'), dict['dialog.burnedIn.' + c.key], c.type);
            assert.ok(r.calls.some(call => call.url.endsWith('/Overrides/Target/' + c.id)), 'Resolve the clicked item through the server');
            assert.doesNotMatch(d.textContent, /\b(understand|understood|reads?|reading)\b/i, file + ': rendered wording');
        }
    });
}

test('English fallback renders the same scoped labels when translation loading fails or a key is missing', async () => {
    for (const dict of [en, {}]) for (const c of cases) {
        const r = await browser(c, dict, { fallback: dict === en }), d = await r.open();
        assert.equal(d.querySelector('.bfRadioLabel').textContent, en['dialog.burnedIn.' + c.key], c.type);
        assert.equal(d.querySelector('.bfBurned').querySelector('select').getAttribute('aria-label'), en['dialog.burnedIn.' + c.key]);
    }
});

test('Burned-in Save and removal send the rendered show/movie/video scope and target ID', async () => {
    for (const c of cases) {
        const r = await browser(c), d = await r.open();
        const text = d.querySelector('.bfBurned').querySelector('input[type="text"]'); text.value = 'ja'; text.fire('input');
        d.querySelector('.bfButton-primary').click(); await r.settle();
        const writes = r.calls.filter(call => call.method !== 'GET');
        assert.deepEqual(writes, [{ method: 'POST', url: 'https://example.test/jf/Babelfin/Me/BurnedIn/' + c.target,
            body: { Languages: ['ja'], Scope: c.scope } }], c.type + ': no unintended audio override');
        assert.equal(r.document.querySelector('.bfDialog'), null);
        const again = await r.open(), burn = again.querySelector('.bfBurned');
        assert.equal(burn.querySelector('input[type="checkbox"]').checked, true);
        assert.equal(burn.querySelector('select').value, 'ja');
        burn.querySelector('input[type="checkbox"]').checked = false;
        again.querySelector('.bfButton-primary').click(); await r.settle();
        assert.deepEqual(r.calls.filter(call => call.method === 'DELETE'), [{ method: 'DELETE',
            url: 'https://example.test/jf/Babelfin/Me/BurnedIn/' + c.target + '?scope=' + c.scope, body: undefined }]);
    }
});

test('A narrower episode flag does not check the whole-show checkbox', async () => {
    const c = cases.find(c => c.type === 'Episode');
    const r = await browser(c, en, { flags: [{ ItemId: episodeId, Scope: 'Title', Languages: ['ja'] }] });
    const d = await r.open();
    assert.equal(d.querySelector('.bfRadioLabel').textContent, en['dialog.burnedIn.show']);
    assert.equal(d.querySelector('.bfBurned').querySelector('input[type="checkbox"]').checked, false);
    d.querySelector('.bfButton-primary').click(); await r.settle();
    assert.equal(r.calls.filter(call => call.method !== 'GET').length, 0, 'An unchanged show dialog preserves the narrower flag');
});

test('Detail pages and cached returns render no chip and do not poll choices to display one', async () => {
    for (const c of cases) {
        const r = await browser(c);
        assert.equal(r.window.Babelfin.version, '0.19.0.0');
        assert.equal(r.document.querySelector('.bfChip'), null, c.type);
        const d = await r.open(); assert.ok(d.querySelector('.bfField'));
        r.window.fire('keydown', { key: 'Escape' }); await r.settle();
        r.document.fire('viewshow'); await r.settle();
        assert.equal(r.document.querySelector('.bfChip'), null, c.type + ': cached return');
        assert.equal(r.calls.filter(call => call.url.includes('/Me/Choice')).length, 0, 'Choice polling belonged to the removed chip');
    }
    assert.doesNotMatch(source('client.css'), /\.bfChip/);
    assert.doesNotMatch(source('client.js'), /client\.chip\./);
});

test('The ⋮ dialog retains labelled controls, focus trapping, Escape and focus return without saving', async () => {
    const r = await browser(cases.find(c => c.type === 'Movie')), d = await r.open();
    assert.equal(d.getAttribute('role'), 'dialog'); assert.equal(d.getAttribute('aria-modal'), 'true');
    assert.equal(r.document.getElementById(d.getAttribute('aria-labelledby')).textContent, en['dialog.title']);
    const first = d.querySelector('select'), last = d.querySelector('.bfButton-primary');
    assert.equal(r.document.activeElement, first);
    for (const f of d.querySelectorAll('.bfField')) {
        const label = f.querySelector('label'), select = f.querySelector('select');
        assert.equal(label.getAttribute('for'), select.id); assert.equal(select.getAttribute('aria-label'), label.textContent);
    }
    r.window.fire('keydown', { key: 'Tab', shiftKey: true }); assert.equal(r.document.activeElement, last);
    r.window.fire('keydown', { key: 'Tab' }); assert.equal(r.document.activeElement, first);
    r.window.fire('keydown', { key: 'Escape' }); await r.settle();
    assert.equal(r.document.querySelector('.bfDialog'), null); assert.equal(r.document.activeElement, r.more);
    assert.equal(r.calls.filter(call => call.method !== 'GET').length, 0);
});

test('Audio/subtitle saves keep backend choices, unrelated fields and Jellyfin track dropdown refresh', async () => {
    for (const c of [cases[0], cases[3]]) {
        const r = await browser(c); r.target.Override = { Settings: { Format: 'PreferText' } };
        const d = await r.open();
        for (const [id, value] of [['bfDialogAudio', 'lang:ja'], ['bfDialogSubtitles', 'off']]) {
            const select = r.document.getElementById(id); assert.ok(select.options.some(o => o.value === value));
            select.value = value; select.fire('change');
        }
        d.querySelector('.bfButton-primary').click(); await r.settle();
        const writes = r.calls.filter(call => call.method !== 'GET'); assert.equal(writes.length, 1);
        assert.equal(writes[0].method, 'PUT');
        assert.equal(writes[0].url, 'https://example.test/jf/Babelfin/Me/Overrides/' + c.target + '?scope=' + c.scope);
        assert.deepEqual(writes[0].body.AudioChain, [{ Kind: 'Language', Language: 'ja' }, { Kind: 'VideoDefault' }]);
        assert.equal(writes[0].body.WhenAudioUnderstood, 'Off'); assert.equal(writes[0].body.WhenAudioNotUnderstood, 'Off');
        assert.equal(writes[0].body.Format, 'PreferText');
        assert.equal(r.audioTrack.value, '1'); assert.equal(r.subtitleTrack.value, '-1');
        assert.equal(r.document.querySelector('.bfChip'), null, 'Saving never recreates the chip');
    }
});

test('Saving from a related card still updates this show; unrelated titles preserve page track picks', async () => {
    for (const c of [cases.find(c => c.type === 'Season'), cases.find(c => c.type === 'Movie')]) {
        const r = await browser(c, en, { fromCard: true, pageCase: cases.find(c => c.type === 'Episode') });
        const d = await r.open(), text = d.querySelector('.bfBurned').querySelector('input[type="text"]');
        text.value = 'ja'; text.fire('input'); d.querySelector('.bfButton-primary').click(); await r.settle();
        const related = c.scope === 'Series';
        assert.equal(r.audioTrack.value, related ? '1' : '0'); assert.equal(r.subtitleTrack.value, related ? '-1' : '2');
    }
});

test('TV layout opens the same choices and scoped burned-in language picker using Enter and Back', async () => {
    const r = await browser(cases.find(c => c.type === 'Episode'), en, { tv: true }), d = await r.open();
    for (const select of d.querySelectorAll('select')) {
        select.focus(); select.fire('keydown', { key: 'Enter' });
        const picker = d.querySelector('.bfPicker'); assert.ok(picker);
        assert.equal(picker.querySelector('.bfPickerTitle').textContent, select.getAttribute('aria-label'));
        const buttons = picker.querySelectorAll('.bfPickerItem'); assert.equal(buttons.length, select.options.length);
        assert.equal(picker.querySelector('[aria-pressed="true"]'), r.document.activeElement);
        r.window.fire('keydown', { key: 'GoBack' });
        assert.equal(d.querySelector('.bfPicker'), null); assert.equal(r.document.activeElement, select);
    }
    const burn = d.querySelector('.bfBurned'), select = burn.querySelector('select');
    select.fire('keydown', { key: 'Enter' });
    const index = select.options.findIndex(o => o.value === 'en');
    d.querySelector('.bfPicker').querySelectorAll('.bfPickerItem')[index].click();
    assert.equal(select.value, 'en'); assert.equal(burn.querySelector('input[type="checkbox"]').checked, true);
    assert.equal(r.document.activeElement, select);
    r.window.fire('keydown', { key: 'Escape' }); assert.equal(r.document.activeElement, r.more);
});
