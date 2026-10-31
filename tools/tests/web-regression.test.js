'use strict';
// Offline regressions executing shipped scripts with the maintained fake-DOM/API rigs.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const Module = require('node:module');
const { Event } = require('./ui-smoke/person/fakedom');
const assets = process.env.BF_ASSETS || path.resolve(__dirname, '../../plugin/src/Jellyfin.Plugin.Babelfin/Assets');
const source = name => fs.readFileSync(name === 'client.js' && process.env.BF_CLIENT_SOURCE || path.join(assets, name), 'utf8');
const copy = value => JSON.parse(JSON.stringify(value));
// Reuse only fixture builders, stopping before their test entry points. Product scripts run whole.
function fixtures(file, end, exports) {
    const filename = path.join(__dirname, file), text = fs.readFileSync(filename, 'utf8');
    assert.ok(text.includes(end), 'Maintained fixture boundary exists');
    const mod = new Module(filename, module); mod.filename = filename; mod.paths = module.paths;
    const fixtureText = text.slice(0, text.indexOf(end))
        .replace("const assets = path.resolve(__dirname, '../../plugin/src/Jellyfin.Plugin.Babelfin/Assets');", "const assets = process.env.BF_ASSETS || path.resolve(__dirname, '../../plugin/src/Jellyfin.Plugin.Babelfin/Assets');")
        .replace('removeOverride: removeOverride };', 'removeOverride: removeOverride, load: load, discard: discard, setT: function(v) { t = v; } };');
    mod._compile(fixtureText + '\nmodule.exports = {' + exports + '};', filename);
    return mod.exports;
}
const rig = fixtures('web-lifecycle.test.js', 'for (const [embedded, staleParent]', 'environment, appBrowser, retry, settle, translator');
const admin = fixtures('ui-smoke/admin/test.js', 'async function main()', 'boot, openFold, openGroup, dialogAnswer, Event, settle');
function instrument(name, hook) { return source(name).replace(/\}\)\(\);\s*$/, hook + '\n})();'); }
function client() {
    const r = rig.environment();
    r.window.getComputedStyle = () => ({ display: 'block', visibility: 'visible' });
    const create = r.document.createElement.bind(r.document);
    r.document.createElement = tag => {
        const n = create(tag);
        n.cloneNode = function(deep) {
            const next = r.document.createElement(this.localName);
            for (const [key, value] of this.attributes) next.setAttribute(key, value);
            if (deep) for (const child of this.childNodes) next.appendChild(child.nodeType === 3 ? r.document.createTextNode(child.textContent) : child.cloneNode(true));
            return next;
        };
        return n;
    };
    r.window.BabelfinI18n = { detectLanguage: () => 'en', load: async () => rig.translator() };
    r.document.currentScript = { src: 'https://media.example/jf/Babelfin/client.js' };
    r.window.location.hash = '#/home';
    vm.runInContext(source('ui.js'), r.context);
    vm.runInContext(instrument('client.js', 'window.clientFixture = { state: state, panel: panel, apply: apply, renderDialog: renderDialog, applyUserMenu: applyUserMenu, applyMenu: applyMenu, showFailure: showFailure, showPanel: showPanel };'), r.context);
    r.api = r.window.clientFixture; r.api.state.t = rig.translator();
    return r;
}
function node(r, tag, attrs = {}, parent = r.document.body) {
    const n = r.document.createElement(tag);
    for (const [name, value] of Object.entries(attrs)) n.setAttribute(name, value);
    parent.appendChild(n); return n;
}
function dialog(r, settings) {
    const box = node(r, 'div');
    r.api.renderDialog({ box }, { TargetId: 'a'.repeat(32), Scope: 'Title', Type: 'Movie', Name: 'Example', Inherited: { Profile: {} } }, settings, []);
    return box;
}

for (const kind of ['avatar', 'settings']) test('reclicking ' + kind + ' entry keeps the original route and focus', async () => {
    const r = client(); let entry;
    if (kind === 'avatar') {
        const menu = node(r, 'div', { id: 'app-user-menu' });
        node(r, 'a', { href: '#/mypreferencesmenu', role: 'menuitem' }, menu);
        r.api.applyUserMenu(); entry = menu.querySelector('.bfMenuItem');
    } else {
        const page = node(r, 'div', { id: 'myPreferencesMenuPage' });
        node(r, 'a', { class: 'lnkSubtitlePreferences', href: '#/subtitlepreferences' }, page);
        r.api.applyMenu(); await rig.settle(); entry = page.querySelector('.bfLanguagesRow');
    }
    entry.focus(); entry.click(); r.window.location.hash = '#/babelfin'; r.api.apply();
    entry.click();
    assert.equal(r.api.panel.returnRoute, '#/home');
    assert.equal(r.api.panel.returnFocus, entry);
    r.window.Babelfin.closeSettings(); await rig.settle();
    assert.equal(r.window.location.hash, '#/home');
    assert.equal(r.document.activeElement, entry);
});

test('failed Settings frame creation does not strand the host scroll lock', () => {
    const r = client(); const create = r.document.createElement.bind(r.document);
    r.document.createElement = tag => { if (tag === 'iframe') throw new Error('frame unavailable'); return create(tag); };
    r.window.location.hash = '#/babelfin'; r.api.apply();
    assert.equal(r.document.querySelector('.bfSettingsPanel'), null);
    assert.equal(r.document.documentElement.classList.contains('bfSettingsRoute'), false);
    r.window.location.hash = '#/home'; r.api.apply();
    assert.equal(r.document.documentElement.classList.contains('bfSettingsRoute'), false);
});

test('a stale Settings scroll lock is removed outside the route', () => {
    const r = client(); r.window.location.hash = '#/home';
    r.document.documentElement.classList.add('bfSettingsRoute'); r.api.apply();
    assert.equal(r.document.documentElement.classList.contains('bfSettingsRoute'), false);
});

for (const fault of ['hasChanges', 'confirmLeave']) test('Settings route leaves and unlocks when ' + fault + ' throws', async () => {
    const r = client(); r.window.location.hash = '#/babelfin'; r.api.apply();
    r.api.panel.frame.contentWindow = { BabelfinApp: { hasChanges() { if (fault === 'hasChanges') throw new Error('injected'); return true; }, confirmLeave() { throw new Error('injected'); } } };
    r.window.location.hash = '#/home'; r.api.apply(); await rig.settle();
    assert.equal(r.document.querySelector('.bfSettingsPanel'), null);
    assert.equal(r.document.documentElement.classList.contains('bfSettingsRoute'), false);
});

test('Settings Close leaves when the confirmation throws', async () => {
    const r = client(); r.window.location.hash = '#/babelfin'; r.api.apply();
    r.api.panel.frame.contentWindow = { BabelfinApp: { confirmLeave() { throw new Error('injected'); } } };
    r.api.panel.returnRoute = '#/home';
    assert.doesNotThrow(() => r.window.Babelfin.closeSettings()); await rig.settle();
    assert.equal(r.window.location.hash, '#/home');
    assert.equal(r.document.documentElement.classList.contains('bfSettingsRoute'), false);
});
for (const query of ['', 'French']) test(`language chooser Escape consumes page Back and preserves value (${query || 'empty'})`, () => {
    const r = rig.environment();
    vm.runInContext(source('ui.js'), r.context);
    let changed = 0, navigated = 0;
    const chooser = r.window.BabelfinUI.languageChooser({ t: rig.translator(), languages: [{ Code: 'fr', Name: 'French' }], value: 'en', onChange() { changed++; } });
    r.document.body.appendChild(chooser.element); chooser.open();
    const input = chooser.element.querySelector('input'), button = chooser.element.querySelector('button');
    input.value = query; input.dispatchEvent(new Event('input'));
    r.document.addEventListener('keydown', () => navigated++);
    const key = new Event('keydown', { key: 'Escape', bubbles: true }); input.dispatchEvent(key);
    assert.equal(navigated, 0, 'Host Back handler must not receive Escape'); assert.equal(key.defaultPrevented, true);
    assert.equal(chooser.getValue(), 'en'); assert.equal(changed, 0);
    assert.equal(button.getAttribute('aria-expanded'), query ? 'true' : 'false');
    if (query) { assert.equal(input.value, ''); input.dispatchEvent(new Event('keydown', { key: 'Escape', bubbles: true })); }
    assert.ok(r.document.activeElement === button, "Focus reaches the intended live control");
});
test('TV keeps the Settings-page entry when an avatar menu also exists', { timeout: 5000 }, async () => {
    const r = client(); r.document.documentElement.classList.add('layout-tv');
    node(r, 'button', { 'aria-controls': 'app-user-menu' });
    const page = node(r, 'div', { id: 'myPreferencesMenuPage' });
    node(r, 'a', { class: 'lnkSubtitlePreferences', href: '#/subtitlepreferences' }, page);
    r.api.applyMenu(); await rig.settle();
    assert.equal(page.querySelector('.bfLanguagesRow')?.getAttribute('href'), '#/babelfin');
});
test('avatar roving focus reaches Babelfin and can move back and forward', () => {
    const r = client(), menu = node(r, 'div', { id: 'app-user-menu' });
    const settings = node(r, 'a', { href: '#/mypreferencesmenu', tabindex: '0', role: 'menuitem' }, menu);
    const next = node(r, 'a', { href: '#/logout', tabindex: '-1', role: 'menuitem' }, menu);
    r.api.applyUserMenu(); const item = menu.querySelector('.bfMenuItem');
    settings.focus(); settings.dispatchEvent(new Event('keydown', { key: 'ArrowDown', bubbles: true }));
    assert.ok(r.document.activeElement === item, "Focus reaches the intended live control");
    item.dispatchEvent(new Event('keydown', { key: 'ArrowDown', bubbles: true })); assert.ok(r.document.activeElement === next, "Focus reaches the intended live control");
    next.dispatchEvent(new Event('keydown', { key: 'ArrowUp', bubbles: true })); assert.ok(r.document.activeElement === item, "Focus reaches the intended live control");
    item.dispatchEvent(new Event('keydown', { key: 'ArrowUp', bubbles: true })); assert.ok(r.document.activeElement === settings, "Focus reaches the intended live control");
});
test('unavailable Settings leaves burned-in state disabled and cannot send mutations', { timeout: 5000 }, async () => {
    const r = client(), calls = [];
    r.window.ApiClient = { getUrl: p => p, ajax: async request => { calls.push(request); return {}; } };
    const box = dialog(r, null), checkbox = box.querySelector('.bfCheck');
    assert.equal(checkbox.disabled, true, 'Unavailable state must never look like an editable unchecked flag');
    assert.equal(box.querySelector('.bfBurned select').disabled, true);
    assert.equal(box.querySelector('.bfBurned input[type="text"]').disabled, true);
    assert.ok(box.querySelector('.bfBurned [role="status"]')?.textContent);
    checkbox.checked = true; box.querySelector('.bfBurned input[type="text"]').value = 'fr';
    box.querySelector('.bfButton-primary').click(); await rig.settle();
    assert.deepEqual(calls, [], 'Even programmatic field changes cannot write unknown burned-in state');
});
test('a loaded existing burned-in flag remains checked and editable', () => {
    const r = client(), box = dialog(r, { Settings: { Everyday: {}, BurnedIn: [{ ItemId: 'a'.repeat(32), Scope: 'Title', Languages: ['fr'] }] } });
    assert.equal(box.querySelector('.bfCheck').checked, true); assert.equal(box.querySelector('.bfCheck').disabled, false);
    assert.equal(box.querySelector('.bfBurned select').value, 'fr');
});
test('Sign-in after Save failure focuses a live sign-in link and suppresses savebar padding', { timeout: 5000 }, async () => {
    const r = await rig.appBrowser(); r.app.state.doc.Enabled = false;
    await r.app.save();
    assert.ok(r.document.activeElement === r.document.querySelector('.bf-message a'), "Focus reaches the intended live control");
    assert.equal(r.document.body.classList.contains('bf-has-savebar'), false);
    assert.ok(!r.document.querySelector('.bf-savebar'));
});
test('Sign-in retry is busy and immediately uses stored credentials if parent is unavailable', { timeout: 5000 }, async () => {
    const r = await rig.appBrowser({ embedded: true }), state = r.app.state;
    state.doc.Enabled = false; await r.app.save(); const draft = copy(state.doc), base = copy(state.base);
    r.signIn(); delete r.window.parent.ApiClient;
    const beforeTimers = r.timers.length, button = r.document.querySelector('.bf-message button');
    button.focus(); button.click();
    assert.equal(button.disabled, true, 'Retry must show that it is doing work');
    assert.equal(button.getAttribute('aria-busy'), 'true');
    await rig.settle();
    assert.equal(r.timers.slice(beforeTimers).filter(t => t.ms === 50).length, 0, 'Retry must not run the initial 10-second frame wait');
    assert.ok(r.document.querySelector('.bfui-table')); assert.equal(r.document.activeElement.isConnected, true);
    assert.notEqual(r.document.activeElement, r.document.body);
    assert.deepEqual(copy(state.doc), draft); assert.deepEqual(copy(state.base), base);
});
const contribution = 'https://github.com/WizardBal/Babelfin/blob/main/CONTRIBUTING.md#translations';
function checkNotice(root, expected) {
    const links = root.querySelectorAll('a').filter(n => n.getAttribute('href') === contribution);
    assert.equal(links.length, 1); assert.equal(links[0].textContent, expected);
    assert.equal(links[0].getAttribute('target'), '_blank'); assert.equal(links[0].getAttribute('rel'), 'noopener');
}
test('Machine translation notice links from the person page and sign-in page using existing text', { timeout: 5000 }, async () => {
    const r = await rig.appBrowser();
    const t = rig.translator(); t.machineTranslated = true;
    r.app.setT(t); r.app.renderPage(); checkNotice(r.document, t('common.machineTranslated'));
    r.app.state.doc.Enabled = false; await r.app.save(); checkNotice(r.document, t('common.machineTranslated'));
});
test('Machine translation notice in override dialogs is an actual link', () => {
    const r = client(); r.api.state.t.machineTranslated = true;
    checkNotice(dialog(r, { Settings: {} }), r.api.state.t('common.machineTranslated'));
});
for (const operation of ['ResetAll', 'Import']) test(`${operation} refresh failure removes stale editors and offers read-only retry`, { timeout: 5000 }, async () => {
    let failed = false, writes = 0;
    const env = await admin.boot({ patchScript(name, text) {
        if (name !== 'dashboard.js') return text;
        return text.replace(/\}\)\(\);\s*$/, 'window.reviewAdmin = { api: api, getState: function() { return S; }, reset: resetTool, preview: renderImportPreview };\n})();');
    } });
    try {
        const a = env.ctx.reviewAdmin, state = a.getState(), cfg = copy(state.config);
        state.pending.changed = fresh => { fresh.Enabled = false; };
        const get = a.api.get, post = a.api.post;
        a.api.post = async (route, ...args) => {
            if (!route.endsWith('/' + operation)) return post(route, ...args);
            writes++; failed = true; env.server.state.config.Enabled = false;
            return operation === 'ResetAll' ? { BackupPath: 'disposable-backup.json', FailedNames: [] } : { Applied: true };
        };
        a.api.get = (route, ...args) => failed && route === 'Babelfin/Admin/Settings' ? Promise.reject({ status: 503, message: 'offline' }) : get(route, ...args);
        const host = env.document.createElement('div'), errors = env.document.createElement('div'); env.page.appendChild(host); env.page.appendChild(errors);
        if (operation === 'ResetAll') a.reset(host); else a.preview(host, { Admin: { WillChange: true } }, {}, errors);
        const button = host.querySelectorAll('button')[0]; button.click(); await admin.dialogAnswer(env, true); await admin.settle();
        assert.equal(writes, 1); assert.ok(!env.page.querySelector('#bfLibraries select'), 'Stale settings table is removed');
        assert.equal(env.page.querySelector('#bfEnabled').disabled, true);
        assert.deepEqual(Object.keys(state.pending), [], 'Draft callbacks invalidated even if refresh fails');
        const retry = host.querySelectorAll('button').find(n => n.textContent === 'Try again'); assert.ok(retry, 'Refresh failure offers a read retry');
        failed = false; retry.click(); await admin.settle(40);
        assert.equal(writes, 1, 'Retry must never replay the destructive mutation');
        assert.equal(state.config.Enabled, false); assert.equal(env.page.querySelector('#bfEnabled').disabled, false);
        assert.ok(env.page.querySelector('#bfLibraries select')); assert.notDeepEqual(copy(state.config), cfg);
    } finally { env.ctx.BabelfinDashboard.hide(); }
});
test('Machine translation notice on admin page uses the shared translation text', { timeout: 5000 }, async () => {
    const env = await admin.boot({ lang: 'de' });
    try {
        const de = JSON.parse(source('i18n/de.json'));
        checkNotice(env.page, de['common.machineTranslated']);
    } finally { env.ctx.BabelfinDashboard.hide(); }
});

test('Rebuilding an avatar item installs only one focus bridge', () => {
    const r = client(), menu = node(r, 'div', { id: 'app-user-menu' });
    const settings = node(r, 'a', { href: '#/mypreferencesmenu', tabindex: '0', role: 'menuitem' }, menu);
    node(r, 'a', { href: '#/logout', tabindex: '-1', role: 'menuitem' }, menu);
    r.api.applyUserMenu(); menu.querySelector('.bfMenuItem').remove(); r.api.applyUserMenu();
    assert.equal(menu.listeners.keydown.length, 1);
    settings.focus(); settings.dispatchEvent(new Event('keydown', { key: 'ArrowDown', bubbles: true }));
    assert.ok(r.document.activeElement === menu.querySelector('.bfMenuItem'), "Focus reaches the intended live control");
});
test('Shared confirmation notice is a link and participates in modal Tab order', async () => {
    const r = rig.environment(); vm.runInContext(source('ui.js'), r.context);
    const t = rig.translator(); t.machineTranslated = true;
    const pending = r.window.BabelfinUI.confirm({ t, message: 'Confirm' });
    const box = r.document.querySelector('.bfui-dialog'); checkNotice(box, t('common.machineTranslated'));
    const buttons = box.querySelectorAll('button'); buttons[1].focus();
    r.document.dispatchEvent(new Event('keydown', { key: 'Tab', bubbles: true }));
    assert.ok(r.document.activeElement === box.querySelector('a'), "Focus reaches the intended live control");
    buttons[0].click(); assert.equal(await pending, false);
});
test('Override data failure retains the translation contribution link', { timeout: 5000 }, async () => {
    const r = client(); r.api.state.t.machineTranslated = true;
    const d = { box: node(r, 'div') }; r.api.state.dialog = d;
    r.api.showFailure(d, { status: 503 }); await rig.settle();
    checkNotice(d.box, r.api.state.t('common.machineTranslated'));
});
test('a pre-reset library response cannot restore an invalidated editor', { timeout: 5000 }, async () => {
    const env = await admin.boot({ patchScript(name, text) {
        return name === 'dashboard.js' ? text.replace(/\}\)\(\);\s*$/, 'window.reviewAdmin = { api: api, getState: function() { return S; }, reset: resetTool, libraries: renderLibraries };\n})();') : text;
    } });
    try {
        const a = env.ctx.reviewAdmin, state = a.getState(), get = a.api.get;
        let release, hold = true;
        a.api.get = (route, ...args) => {
            if (route === 'Babelfin/Admin/Libraries' && hold) { hold = false; return new Promise(resolve => { release = resolve; }); }
            if (route === 'Babelfin/Admin/Settings') return Promise.reject({ status: 503 });
            return get(route, ...args);
        };
        delete state.cache.libraries; const oldRender = a.libraries();
        a.api.post = async () => ({ BackupPath: 'disposable.json' });
        const host = env.document.createElement('div'); env.page.appendChild(host); a.reset(host);
        host.querySelector('button').click(); await admin.dialogAnswer(env, true); await admin.settle();
        release([]); await oldRender; await admin.settle();
        assert.ok(!env.page.querySelector('#bfLibraries select'), 'Pre-reset response cannot mount old controls');
    } finally { env.ctx.BabelfinDashboard.hide(); }
});

for (const operation of ['ResetAll', 'Import']) for (const section of ['Libraries', 'People']) test(`An obsolete ${section} rejection preserves the fresh UI after ${operation}`, { timeout: 5000 }, async () => {
    const env = await admin.boot({ patchScript(name, text) {
        return name === 'dashboard.js' ? text.replace(/\}\)\(\);\s*$/, 'window.reviewAdmin = { api: api, getState: function() { return S; }, libraries: renderLibraries, people: renderPeople, reset: resetTool, preview: renderImportPreview };\n})();') : text;
    } });
    try {
        const a = env.ctx.reviewAdmin, state = a.getState(), get = a.api.get;
        const route = 'Babelfin/Admin/' + (section === 'Libraries' ? 'Libraries' : 'Users');
        const cacheKey = section === 'Libraries' ? 'libraries' : 'users';
        let rejectOld, hold = true;
        a.api.get = (url, ...args) => {
            if (url === route && hold) {
                hold = false;
                return new Promise((resolve, reject) => { rejectOld = reject; });
            }
            return get(url, ...args);
        };
        delete state.cache[cacheKey];
        const oldRender = section === 'Libraries' ? a.libraries() : a.people();
        a.api.post = async () => operation === 'ResetAll' ? { BackupPath: 'disposable.json' } : { Applied: true };
        const host = env.document.createElement('div'), errors = env.document.createElement('div');
        env.page.appendChild(host); env.page.appendChild(errors);
        if (operation === 'ResetAll') a.reset(host); else a.preview(host, { Admin: { WillChange: true } }, {}, errors);
        host.querySelector('button').click(); await admin.dialogAnswer(env, true); await admin.settle(40);
        const body = env.page.querySelector(section === 'Libraries' ? '#bfLibraries' : '#bfPeople');
        const freshTable = body.querySelector('table');
        const controls = body.querySelectorAll('input, select, button, a[href]');
        assert.ok(freshTable && controls.length > 0, 'The post-mutation read rendered fresh controls');
        rejectOld({ status: 503, message: 'Obsolete request failed' }); await oldRender; await admin.settle();
        assert.equal(body.querySelectorAll('input, select, button, a[href]').length, controls.length, 'A late failure must not clear fresh controls');
        assert.ok(body.querySelector('table') === freshTable, 'The fresh table stays mounted');
        assert.ok(controls.every(control => body.contains(control)), 'Fresh controls remain connected');
        // A failure from the current revision must still be reported.
        delete state.cache[cacheKey];
        a.api.get = (url, ...args) => url === route ? Promise.reject({ status: 503, message: 'Current request failed' }) : get(url, ...args);
        await (section === 'Libraries' ? a.libraries() : a.people());
        assert.ok(!body.querySelector('table') && body.querySelector('[role="alert"]'), 'Current failures still show an error');
    } finally { env.ctx.BabelfinDashboard.hide(); }
});

// The maintained DOM does not synthesize the browser's native Enter-to-click action.
function enterButton(button) {
    button.dispatchEvent(new Event('keydown', { key: 'Enter', bubbles: true }));
    button.click();
    button.dispatchEvent(new Event('keyup', { key: 'Enter', bubbles: true }));
}
for (const embedded of [false, true]) test(`Clean Start over reauthentication restores usable keyboard focus (${embedded ? 'embedded' : 'standalone'})`, { timeout: 5000 }, async () => {
    const r = await rig.appBrowser({ embedded }), before = copy(r.app.state.doc);
    let resets = 0;
    const denied = () => ({ status: 401, ok: false, text: async () => '{"Message":"Sign in again"}' });
    const isReset = url => new URL(url, r.window.location.origin).pathname.endsWith('/Babelfin/Me/Reset');
    if (embedded) {
        const client = r.window.parent.ApiClient, ajax = client.ajax;
        client.ajax = async options => { if (isReset(options.url)) { resets++; throw denied(); } return ajax(options); };
    } else {
        const fetch = r.window.fetch;
        r.window.fetch = (url, options) => { if (isReset(url)) { resets++; return Promise.resolve(denied()); } return fetch(url, options); };
    }
    r.document.getElementById('bf-advanced').open = true;
    const group = r.document.getElementById('bf-group-reset');
    group.querySelector('.bfui-disclosure-button').click();
    const startOver = group.querySelector('.bfui-btn-danger'); startOver.focus(); enterButton(startOver); await rig.settle();
    const confirm = r.document.querySelector('.bfui-dialog').querySelectorAll('button')[1]; confirm.focus(); enterButton(confirm); await rig.settle();
    assert.equal(resets, 1); assert.ok(r.document.querySelector('.bf-message button'));
    assert.equal(r.window.BabelfinApp.hasChanges(), false, 'A failed Start over left a clean document');
    r.signIn(); const retry = r.document.querySelector('.bf-message button'); retry.focus(); enterButton(retry); await rig.settle();
    assert.ok(r.document.querySelector('.bfui-table')); assert.equal(r.window.BabelfinApp.hasChanges(), false);
    assert.deepEqual(copy(r.app.state.doc), before); assert.deepEqual(copy(r.app.state.base), before);
    assert.equal(r.document.querySelector('.bf-savebar').hidden, true);
    const focus = r.document.activeElement;
    assert.ok(focus !== r.document.body && focus.isConnected && !focus.disabled && !focus.closest('[hidden]'), 'Reauthentication focuses a visible live control without a Save button');
    assert.ok(focus === r.document.querySelector('.bf-languages-line button'), 'Keyboard resumes at the language control');
    enterButton(focus);
    assert.equal(r.document.querySelector('.bf-languages-editor').hidden, false, 'Enter still operates the focused control');
    assert.ok(r.document.activeElement.tagName === 'INPUT');
});

// Execute the shipped person clients against persistent, scoped API fixtures.
const normalId = id => String(id).replace(/-/g, '').toLowerCase();
function burnedRows(r) { return r.document.querySelectorAll('#bf-group-burnedIn .bf-item'); }
function openBurned(r) {
    r.document.getElementById('bf-advanced').open = true;
    const heading = r.document.querySelector('#bf-group-burnedIn .bfui-disclosure-button');
    if (heading.getAttribute('aria-expanded') !== 'true') heading.click();
}
async function burnedApp({ flags, items = [], status = 204, embedded = false, deferred = false } = {}) {
    const r = await rig.appBrowser({ embedded }), requests = [];
    let stored = copy(flags || [{ ItemId: 'a'.repeat(32), Scope: 'Title', Name: 'Example (1999)', Languages: ['fr'] }]);
    r.app.state.doc.BurnedIn = copy(stored); r.app.state.base.BurnedIn = copy(stored);
    let release;
    const gate = deferred ? new Promise(resolve => { release = resolve; }) : Promise.resolve();
    const respond = async (url, init = {}) => {
        const address = new URL(url, r.window.location.origin), method = init.method || 'GET';
        const route = address.pathname.replace(/^\/jf/, '');
        const request = { method, route, query: Object.fromEntries(address.searchParams), body: init.body && JSON.parse(init.body) };
        requests.push(request);
        let code = 200, body;
        if (route.startsWith('/Babelfin/Me/BurnedIn/') && method === 'DELETE') {
            await gate; if (status === 0) throw new TypeError('Network unavailable'); code = status;
            if (code === 204) stored = stored.filter(f => !(normalId(f.ItemId) === route.split('/').at(-1) && f.Scope === request.query.scope));
            else body = { Message: 'Burned-in flag could not be removed.' };
        } else if (route === '/Babelfin/Me/Settings') {
            if (method === 'PUT') assert.equal(request.query.keepOverrides, 'true');
            body = { Settings: { ...copy(method === 'PUT' ? request.body : r.app.state.base), BurnedIn: copy(stored) } };
        } else if (route === '/Babelfin/Me/Overrides') body = [];
        else if (route === '/Babelfin/Me/ChoiceStatus') body = { HasChoices: true };
        else if (route === '/Items') body = { Items: items };
        else if (route === '/Babelfin/Ping') body = { version: 'test' };
        else throw new Error('Unexpected persistent fixture route: ' + method + ' ' + route);
        return { status: code, ok: code < 400, text: async () => code === 204 ? '' : JSON.stringify(body) };
    };
    if (embedded) r.window.parent.ApiClient.ajax = async o => {
        const response = await respond(o.url, { method: o.type, body: o.data });
        if (!response.ok) throw response;
        return response.text();
    };
    else r.window.fetch = respond;
    r.app.renderPage(); openBurned(r);
    return Object.assign(r, { requests, release: () => release?.(), stored: () => copy(stored) });
}
for (const embedded of [false, true]) for (const Scope of ['Title', 'Season', 'Series', 'Collection']) {
    test(`burned-in ${Scope} removal persists through Save, Discard and reload (${embedded ? 'ApiClient' : 'fetch'})`, async () => {
        const id = 'ABCDEF01-2345-6789-ABCD-EF0123456789';
        const flag = { ItemId: id, Scope, Name: 'Example (1999)', Languages: ['fr'] };
        const sibling = { ...flag, ItemId: 'b'.repeat(32), Name: 'Example (2024)' };
        const otherScope = { ...flag, Scope: Scope === 'Title' ? 'Season' : 'Title' };
        const r = await burnedApp({ embedded, flags: [flag, sibling, otherScope] });
        r.app.state.doc.Enabled = false;
        const remove = burnedRows(r)[0].querySelector('button'); remove.focus(); remove.click(); await rig.settle();
        assert.deepEqual(r.requests.filter(q => q.method === 'DELETE'), [{ method: 'DELETE', route: '/Babelfin/Me/BurnedIn/' + normalId(id),
            query: { scope: Scope, ...(Scope === 'Collection' ? { collectionId: normalId(id) } : {}) }, body: undefined }]);
        assert.deepEqual(copy(r.app.state.doc.BurnedIn), [sibling, otherScope], 'Only the exact stored ID/scope is removed');
        assert.deepEqual(copy(r.app.state.base.BurnedIn), [sibling, otherScope], 'The saved baseline reflects the immediate DELETE');
        assert.equal(r.app.state.doc.Enabled, false); assert.equal(r.app.state.base.Enabled, true);
        assert.equal(r.window.BabelfinApp.hasChanges(), true);
        assert.ok(r.document.activeElement === burnedRows(r)[0].querySelector('button'), 'Focus moves to the next Remove');
        const discarding = r.app.discard(); await rig.settle();
        r.document.querySelector('.bfui-dialog').querySelectorAll('button')[1].click(); await discarding;
        assert.equal(r.app.state.doc.Enabled, true); assert.deepEqual(copy(r.app.state.doc.BurnedIn), [sibling, otherScope]);
        r.app.state.doc.HandleNextEpisode = true; await r.app.save(); await rig.settle();
        assert.deepEqual(copy(r.app.state.doc.BurnedIn), [sibling, otherScope]);
        assert.equal(r.requests.filter(q => q.method === 'PUT').length, 1);
        await r.app.load(); assert.deepEqual(copy(r.app.state.doc.BurnedIn), [sibling, otherScope]);
    });
}
for (const status of [400, 403, 404, 409, 503]) test(`failed burned-in DELETE ${status} keeps row, baseline, draft and usable focus`, async () => {
    const r = await burnedApp({ status }), before = copy(r.app.state.base);
    r.app.state.doc.Everyday.Reads = ['de'];
    const remove = burnedRows(r)[0].querySelector('button'); remove.focus(); remove.click(); await rig.settle();
    assert.equal(r.requests.filter(q => q.method === 'DELETE').length, 1);
    assert.deepEqual(copy(r.app.state.base), before); assert.deepEqual(copy(r.app.state.doc.BurnedIn), before.BurnedIn);
    assert.deepEqual(copy(r.app.state.doc.Everyday.Reads), ['de']); assert.equal(burnedRows(r).length, 1);
    assert.ok(r.document.activeElement.isConnected && !r.document.activeElement.disabled);
    assert.equal(burnedRows(r)[0].querySelector('[role="alert"]').textContent, 'Burned-in flag could not be removed.', 'The row retains the server explanation');
});
test('burned-in removal serializes Save/Discard and keeps draft edits made during DELETE', async () => {
    const r = await burnedApp({ deferred: true });
    const remove = burnedRows(r)[0].querySelector('button'); remove.focus(); remove.click(); remove.click(); await rig.settle();
    assert.equal(r.requests.filter(q => q.method === 'DELETE').length, 1, 'Double Remove sends only one request');
    assert.equal(remove.disabled, true); assert.equal(remove.getAttribute('aria-busy'), 'true');
    r.app.state.doc.Enabled = false; await r.app.save(); await r.app.discard();
    assert.equal(r.requests.filter(q => q.method === 'PUT').length, 0, 'Save cannot race the DELETE');
    assert.equal(r.document.querySelector('.bfui-dialog'), null, 'Discard cannot race the DELETE');
    r.release(); await rig.settle();
    assert.equal(r.app.state.doc.Enabled, false); assert.equal(r.app.state.base.Enabled, true);
    assert.deepEqual(copy(r.app.state.doc.BurnedIn), []); assert.deepEqual(copy(r.app.state.base.BurnedIn), []);
    assert.equal(r.document.getElementById('bf-group-burnedIn').hidden, true);
    assert.ok(r.document.activeElement === r.document.querySelector('#bf-group-reset .bfui-disclosure-button'), 'Last Remove moves to a visible group heading');
    await r.app.save(); assert.deepEqual(copy(r.app.state.doc.BurnedIn), []);
});
test('failed pending removal keeps a concurrently edited field and its focus', async () => {
    const r = await burnedApp({ deferred: true, status: 503 });
    burnedRows(r)[0].querySelector('button').click(); await rig.settle();
    const editor = r.document.querySelector('.bfui-table select'); editor.focus();
    r.app.state.doc.Everyday.Reads = ['de']; r.release(); await rig.settle();
    assert.ok(r.document.activeElement === editor, 'An async response must not steal focus from an editor');
    assert.deepEqual(copy(r.app.state.doc.Everyday.Reads), ['de']); assert.equal(burnedRows(r).length, 1);
});
for (const ItemId of ['', 'All', '../a', 'xyz', '0'.repeat(32)]) test(`invalid burned-in ID ${JSON.stringify(ItemId)} never sends a mutation`, async () => {
    const r = await burnedApp({ flags: [{ ItemId, Scope: 'Title', Languages: ['fr'] }] });
    const remove = burnedRows(r)[0].querySelector('button'); remove.click(); await rig.settle();
    assert.equal(remove.disabled, true); assert.equal(r.requests.filter(q => q.method === 'DELETE').length, 0);
    assert.equal(burnedRows(r).length, 1);
});
test('Remove is unavailable while a Settings Save is pending', async () => {
    const r = await burnedApp(); let finish;
    const fetch = r.window.fetch;
    r.window.fetch = (url, init) => init?.method === 'PUT' ? new Promise(resolve => { finish = () => fetch(url, init).then(resolve); }) : fetch(url, init);
    r.app.state.doc.Enabled = false; const saving = r.app.save(); await rig.settle();
    burnedRows(r)[0].querySelector('button').click(); await rig.settle();
    assert.equal(r.requests.filter(q => q.method === 'DELETE').length, 0, 'An older Save cannot resurrect a newer removal');
    assert.equal(burnedRows(r).length, 1, 'A pending Save prevents even a draft-only removal');
    finish(); await saving;
});
for (const embedded of [false, true]) test(`Settings 409 exposes the server explanation as text and retry can load again (${embedded ? 'ApiClient' : 'fetch'})`, async () => {
    const r = await rig.appBrowser({ embedded });
    const detail = 'Settings schema 99 is newer than this plugin. <b>Upgrade Babelfin</b>.';
    const denied = () => ({ status: 409, ok: false, text: async () => JSON.stringify({ Message: detail }) });
    let restore;
    if (embedded) {
        const client = r.window.parent.ApiClient, ajax = client.ajax;
        client.ajax = o => new URL(o.url).pathname.endsWith('/Me/Settings') ? Promise.reject(denied()) : ajax(o);
        restore = () => { client.ajax = ajax; };
    } else {
        const fetch = r.window.fetch;
        r.window.fetch = (url, init) => new URL(url, r.window.location.origin).pathname.endsWith('/Me/Settings') ? Promise.resolve(denied()) : fetch(url, init);
        restore = () => { r.window.fetch = fetch; };
    }
    await r.app.load();
    const error = r.document.querySelector('.bf-message');
    assert.ok(error.textContent.includes(detail)); assert.equal(error.querySelector('b'), null, 'Server detail is never treated as markup');
    assert.equal(r.document.querySelector('.bfui-table'), null);
    restore(); error.querySelector('button').click(); await rig.settle();
    assert.ok(r.document.querySelector('.bfui-table'));
});
test('client dialog has complete English fallbacks when all translation fetches fail', () => {
    const r = client(); r.api.state.t = null;
    const box = dialog(r, null), words = JSON.parse(source('i18n/en.json'));
    for (const key of ['ui.table.audioMyLanguages', 'ui.table.audioOriginal']) {
        assert.ok(box.textContent.includes(words[key]), key + ' has English words');
    }
    const audio = box.querySelector('#bfDialogAudio');
    for (const [value, key] of [['original', 'ui.table.audioOriginal.info']]) {
        audio.value = value; audio.dispatchEvent(new Event('change', { bubbles: true }));
        assert.ok(box.textContent.includes(words[key]), key + ' has English explanatory text');
    }
    assert.ok(box.querySelector('.bfBurned [role="status"]').textContent.includes(words['dialog.error']));
    assert.doesNotMatch(box.textContent, /\b(?:ui|dialog)\.[A-Za-z]/);
});
// Jellyfin inputManager dispatches a cancelable command on the active element, then
// invokes the native select click only when the command was not canceled.
function remote(r, command) {
    const target = r.document.activeElement;
    const event = new Event('command', { detail: { command }, bubbles: true, cancelable: true });
    if (target.dispatchEvent(event) && command === 'select') {
        const click = new Event('click', { bubbles: true, cancelable: true });
        if (target.dispatchEvent(click) && target.getAttribute('href')) r.window.location.hash = target.getAttribute('href');
    }
    return event;
}
test('TV remote commands reach and activate Settings in the avatar menu', () => {
    const r = client(); r.document.documentElement.classList.add('layout-tv');
    const menu = node(r, 'div', { id: 'app-user-menu' });
    const settings = node(r, 'a', { href: '#/mypreferencesmenu', tabindex: '0', role: 'menuitem' }, menu);
    const logout = node(r, 'a', { href: '#/logout', tabindex: '-1', role: 'menuitem' }, menu);
    r.api.applyUserMenu(); const item = menu.querySelector('.bfMenuItem');
    settings.focus(); assert.equal(remote(r, 'down').defaultPrevented, true); assert.ok(r.document.activeElement === item);
    remote(r, 'down'); assert.ok(r.document.activeElement === logout);
    remote(r, 'up'); assert.ok(r.document.activeElement === item);
    remote(r, 'up'); assert.ok(r.document.activeElement === settings);
    remote(r, 'down'); remote(r, 'select'); assert.equal(r.window.location.hash, '#/babelfin');
});
test('TV Settings page remote select opens Babelfin with an avatar present', async () => {
    const r = client(); r.document.documentElement.classList.add('layout-tv');
    node(r, 'button', { 'aria-controls': 'app-user-menu' });
    const page = node(r, 'div', { id: 'myPreferencesMenuPage' });
    node(r, 'a', { class: 'emby-button lnkSubtitlePreferences', href: '#/subtitlepreferences' }, page);
    r.api.applyMenu(); await rig.settle(); const row = page.querySelector('.bfLanguagesRow');
    row.focus(); remote(r, 'select'); assert.equal(r.window.location.hash, '#/babelfin');
    // The actual hashchange lifecycle mounts the frame and then removes it on Back.
    for (const handler of r.window.listeners.hashchange) handler();
    assert.ok(r.document.querySelector('.bfSettingsFrame'));
    r.window.location.hash = '#/mypreferencesmenu'; for (const handler of r.window.listeners.hashchange) handler();
    assert.equal(r.document.querySelector('.bfSettingsFrame'), null);
});
test('shared UI drops its unused duplicate translator and retired audio-step enumeration', () => {
    const r = rig.environment(); vm.runInContext(source('ui.js'), r.context);
    assert.equal(Object.hasOwn(r.window.BabelfinUI.ENUMS, 'AudioStepKind'), false);
    assert.equal(Object.hasOwn(r.window.BabelfinUI, 'combineT'), false);
});
function localeValidation(mutate) {
    const messages = []; let code = 0;
    const real = fs.readFileSync;
    const fake = Object.assign({}, fs, { readFileSync(file, encoding) {
        const text = real(file, 'utf8');
        if (!String(file).endsWith('.json')) return encoding ? text : Buffer.from(text);
        const table = JSON.parse(text); mutate?.(String(file).replace(/\\/g, '/'), table);
        const changed = JSON.stringify(table); return encoding ? changed : Buffer.from(changed);
    } });
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'ui-smoke/person/validate-i18n.js'), 'utf8'), {
        require(name) { return name === 'fs' ? fake : name === '../shared/target' ? { getAssetsPath: () => assets + path.sep } : require(name); },
        console: { log: (...args) => messages.push(args.join(' ')) }, process: { exit(value) { code = value; } }
    });
    return { code, messages: messages.join('\n') };
}
test('all 16 real locales pass with narrow technical and genuine same-word allowances', () => {
    const result = localeValidation(); assert.equal(result.code, 0, result.messages);
    assert.match(result.messages, /16 files/);
});
test('storage wording allowance does not permit person-language wording', () => {
    const result = localeValidation((file, table) => {
        if (file.endsWith('/i18n-admin/en.json')) table['backup.storageBlocked'] = 'Languages {name} can read';
        if (file.endsWith('/i18n/en.json')) table['app.save'] = 'Languages I read';
        if (file.endsWith('/i18n-admin/es.json')) table['backup.blockedPeople'] = 'Idiomas que lees';
    });
    assert.equal(result.code, 1);
    assert.match(result.messages, /i18n-admin\/en\.json backup\.storageBlocked says/);
    assert.match(result.messages, /i18n\/en\.json app\.save says/);
    assert.match(result.messages, /i18n-admin\/es\.json backup\.blockedPeople may say/);
});
for (const locale of ['de', 'es', 'fr', 'it', 'ja', 'ko', 'nl', 'pl', 'pt-BR', 'pt-PT', 'pt', 'ru', 'zh-Hans', 'zh-Hant', 'zh']) {
    test(`locale validator rejects copied English prose in ${locale}`, () => {
        const en = JSON.parse(source('i18n/en.json'));
        const result = localeValidation((file, table) => {
            if (file.endsWith('/i18n/' + locale + '.json')) table['app.save'] = '  ' + en['app.save'] + '  ';
        });
        assert.equal(result.code, 1); assert.match(result.messages, new RegExp(locale + '\\.json app.save.*English'));
    });
}
test('locale validator covers admin prose and does not allow all Audio labels in every locale', () => {
    const adminWords = JSON.parse(source('i18n-admin/en.json')), en = JSON.parse(source('i18n/en.json'));
    const result = localeValidation((file, table) => {
        if (file.endsWith('/i18n-admin/de.json')) table['page.loadError'] = adminWords['page.loadError'];
        if (file.endsWith('/i18n/ja.json')) table['dialog.audio'] = en['dialog.audio'];
    });
    assert.equal(result.code, 1); assert.match(result.messages, /de.json page.loadError.*English/);
    assert.match(result.messages, /ja.json dialog.audio.*English/);
});

for (const status of [204, 503]) test(`Remove restores focus when the browser blurs a disabled button (${status})`, async () => {
    const r = await burnedApp({ deferred: true, status });
    const remove = burnedRows(r)[0].querySelector('button'); remove.focus(); remove.click();
    // Native browsers can blur a focused button as soon as it becomes disabled.
    remove.blur(); r.release(); await rig.settle();
    const expected = status === 204 ? r.document.querySelector('#bf-group-reset .bfui-disclosure-button') : burnedRows(r)[0].querySelector('button');
    assert.ok(r.document.activeElement === expected && !expected.disabled, 'Async Remove returns to a usable control');
});
for (const embedded of [false, true]) test(`burned-in network failure preserves the row and baseline (${embedded ? 'ApiClient' : 'fetch'})`, async () => {
    const r = await burnedApp({ status: 0, embedded }), before = copy(r.app.state.base);
    burnedRows(r)[0].querySelector('button').click(); await rig.settle();
    assert.deepEqual(copy(r.app.state.doc), before); assert.deepEqual(copy(r.app.state.base), before);
    assert.equal(burnedRows(r).length, 1); assert.equal(burnedRows(r)[0].querySelector('button').disabled, false);
    assert.match(burnedRows(r)[0].querySelector('[role="alert"]').textContent, /Network unavailable/);
});
test('burned-in 401 keeps the flag and other draft edits through reauthentication', async () => {
    const r = await burnedApp({ status: 401 }); r.app.state.doc.Enabled = false;
    const before = copy(r.app.state.base);
    burnedRows(r)[0].querySelector('button').click(); await rig.settle();
    assert.deepEqual(copy(r.app.state.base), before); assert.deepEqual(copy(r.app.state.doc.BurnedIn), before.BurnedIn);
    assert.equal(r.app.state.doc.Enabled, false); assert.equal(r.app.state.needsSignIn, true);
    assert.ok(r.document.activeElement === r.document.querySelector('.bf-message a'));
    r.signIn(); r.document.querySelector('.bf-message button').click(); await rig.settle(); openBurned(r);
    assert.equal(r.app.state.needsSignIn, false); assert.equal(burnedRows(r).length, 1);
    assert.equal(r.app.state.doc.Enabled, false); assert.deepEqual(copy(r.app.state.base), before);
    assert.ok(r.document.activeElement.isConnected);
});
test('looked-up years and episode/season labels stay bound to their exact stored IDs', async () => {
    const items = [
        { Id: 'a'.repeat(32), Type: 'Movie', Name: 'Same name', ProductionYear: 1999 },
        { Id: 'b'.repeat(32), Type: 'Movie', Name: 'Same name', ProductionYear: 2024 },
        { Id: 'c'.repeat(32), Type: 'Season', SeriesName: 'Show', Name: 'Specials' },
        { Id: 'd'.repeat(32), Type: 'Episode', SeriesName: 'Show', ParentIndexNumber: 0, IndexNumber: 0, Name: 'Pilot' }
    ];
    const r = await burnedApp({ items, flags: items.map(item => ({ ItemId: item.Id, Scope: item.Type === 'Season' ? 'Season' : 'Title', Languages: ['fr'] })) });
    await rig.settle();
    const labels = burnedRows(r).map(row => row.querySelector('.bf-item-name').textContent);
    assert.deepEqual(labels, ['Same name (1999)', 'Same name (2024)', 'Show · Specials', 'Show · S0E0 · Pilot']);
    for (const index of [3, 2, 1]) {
        burnedRows(r)[index].querySelector('button').click(); await rig.settle();
        const request = r.requests.filter(q => q.method === 'DELETE').at(-1);
        assert.equal(request.route, '/Babelfin/Me/BurnedIn/' + items[index].Id);
        assert.equal(request.query.scope, index === 2 ? 'Season' : 'Title');
    }
    assert.deepEqual(r.stored().map(f => f.ItemId), [items[0].Id]);
    r.app.state.doc.Enabled = false; await r.app.save(); await r.app.load();
    assert.deepEqual(copy(r.app.state.doc.BurnedIn).map(f => f.ItemId), [items[0].Id]);
});
for (const Scope of ['Library', '', 'Episode', '../Title']) test(`invalid burned-in scope ${JSON.stringify(Scope)} stays read only`, async () => {
    const r = await burnedApp({ flags: [{ ItemId: 'a'.repeat(32), Scope, Languages: ['fr'] }] });
    const remove = burnedRows(r)[0].querySelector('button'); assert.equal(remove.disabled, true); remove.click();
    await rig.settle(); assert.equal(r.requests.filter(q => q.method === 'DELETE').length, 0);
});
test('each avatar rebuild keeps exactly one keyboard and one remote command bridge', () => {
    const r = client(), menu = node(r, 'div', { id: 'app-user-menu' });
    const settings = node(r, 'a', { href: '#/mypreferencesmenu', tabindex: '0', role: 'menuitem' }, menu);
    r.api.applyUserMenu(); menu.querySelector('.bfMenuItem').remove(); r.api.applyUserMenu(); r.api.applyUserMenu();
    assert.equal(menu.listeners.keydown.length, 1); assert.equal(menu.listeners.command.length, 1);
    settings.focus(); remote(r, 'down'); assert.ok(r.document.activeElement === menu.querySelector('.bfMenuItem'));
});

test('a successful pending Remove preserves actual table changes and editor focus', async () => {
    const r = await burnedApp({ deferred: true });
    burnedRows(r)[0].querySelector('button').click();
    const editor = r.document.querySelector('.bfui-table select'); editor.focus();
    editor.value = 'original'; editor.dispatchEvent(new Event('change', { bubbles: true }));
    const draft = copy(r.app.state.doc.Everyday);
    assert.deepEqual(draft.AudioChain, [{ Kind: 'OriginalLanguage' }, { Kind: 'VideoDefault' }]);
    r.release(); await rig.settle();
    assert.deepEqual(copy(r.app.state.doc.Everyday), draft);
    assert.ok(r.document.activeElement === editor, 'The user keeps working in the same live editor');
    assert.equal(r.app.state.base.Everyday.AudioChain, undefined);
    assert.equal(r.window.BabelfinApp.hasChanges(), true);
});
test('removing an earlier row preserves focus on the exact surviving item', async () => {
    const flags = ['a', 'b', 'c', 'd'].map(id => ({ ItemId: id.repeat(32), Scope: 'Title', Name: id, Languages: ['fr'] }));
    const r = await burnedApp({ deferred: true, flags });
    burnedRows(r)[0].querySelector('button').click();
    burnedRows(r)[2].querySelector('button').focus(); r.release(); await rig.settle();
    assert.ok(r.document.activeElement === burnedRows(r)[1].querySelector('button'), 'Focus stays on c, even after its row index changes');
});
test('parallel removals update only their own keys and keep the unsaved draft', async () => {
    const flags = ['a', 'b', 'c'].map(id => ({ ItemId: id.repeat(32), Scope: id === 'b' ? 'Season' : 'Title', Name: id, Languages: ['fr'] }));
    const r = await burnedApp({ deferred: true, flags });
    burnedRows(r)[0].querySelector('button').click(); burnedRows(r)[1].querySelector('button').click();
    r.app.state.doc.Enabled = false; r.release(); await rig.settle();
    assert.equal(r.requests.filter(q => q.method === 'DELETE').length, 2);
    assert.deepEqual(copy(r.app.state.doc.BurnedIn), [flags[2]]); assert.deepEqual(copy(r.app.state.base.BurnedIn), [flags[2]]);
    assert.equal(r.app.state.doc.Enabled, false); assert.equal(r.app.state.base.Enabled, true);
    await r.app.save(); assert.deepEqual(copy(r.app.state.doc.BurnedIn), [flags[2]]);
});

// Simulate SettingsIntent's nullable-leaf semantics and atomic arrays, not a legacy full PUT.
async function intentApp({ embedded = false, libraries = [] } = {}) {
    const r = await rig.appBrowser({ embedded }), requests = [];
    const server = copy(r.app.state.base), status = { read: 200, write: 200 };
    let holdRead = null, holdWrite = null;
    const transport = async (url, init = {}) => {
        const address = new URL(url, r.window.location.origin), method = init.method || 'GET';
        if (!address.pathname.endsWith('/Me/Settings')) return null;
        const query = Object.fromEntries(address.searchParams), body = init.body && JSON.parse(init.body);
        requests.push({ method, query, body });
        if (method === 'GET' && holdRead) await holdRead.promise;
        if (method === 'PUT' && holdWrite) await holdWrite.promise;
        const code = method === 'GET' ? status.read : status.write;
        if (code >= 400) return { status: code, ok: false, text: async () => JSON.stringify({ Message: 'Intent fixture refused request' }) };
        if (method === 'PUT') {
            const next = query.fields ? copy(server) : copy(body);
            for (const field of query.fields?.split(',') || []) {
                if (field.startsWith('libraries.')) {
                    const [, id, , key] = field.split('.'), name = key[0].toUpperCase() + key.slice(1);
                    const from = body.Libraries.find(row => normalId(row.LibraryId) === id);
                    let row = next.Libraries.find(row => normalId(row.LibraryId) === id);
                    if (!row && !from) continue;
                    if (!row) { row = { LibraryId: id, Name: from.Name, Settings: {} }; next.Libraries.push(row); }
                    const value = from?.Settings?.[name]; (row.Settings ||= {})[name] = value === undefined ? null : copy(value);
                    continue;
                }
                const keys = field.split('.').map(key => key[0].toUpperCase() + key.slice(1));
                let to = next, from = body;
                for (const key of keys.slice(0, -1)) { to = to[key] ||= {}; from = from?.[key]; }
                const value = from?.[keys.at(-1)]; to[keys.at(-1)] = value === undefined ? null : copy(value);
            }
            if (query.keepOverrides === 'true') { next.Overrides = copy(server.Overrides); next.BurnedIn = copy(server.BurnedIn); }
            Object.assign(server, next);
        }
        return { status: code, ok: true, text: async () => JSON.stringify({ Settings: server, Libraries: libraries }) };
    };
    if (embedded) {
        const client = r.window.parent.ApiClient, ajax = client.ajax;
        client.ajax = async o => { const response = await transport(o.url, { method: o.type, body: o.data }); if (!response) return ajax(o); if (!response.ok) throw response; return response.text(); };
    } else {
        const fetch = r.window.fetch;
        r.window.fetch = async (url, init) => await transport(url, init) || fetch(url, init);
    }
    const defer = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
    return Object.assign(r, { server, requests, intentStatus: status,
        holdRead() { holdRead = defer(); return holdRead.resolve; }, holdWrite() { holdWrite = defer(); return holdWrite.resolve; } });
}
for (const embedded of [false, true]) test(`person Save sends only explicit changed fields and preserves concurrent hidden values (${embedded ? 'ApiClient' : 'fetch'})`, async () => {
    const r = await intentApp({ embedded });
    r.app.state.doc.Enabled = false;
    r.server.Everyday.Sdh = 'Avoid'; r.server.Everyday.FutureSetting = { server: 'fresh' };
    r.server.PerLibrary = true; r.server.Seed = { Confirmed: true, FutureSeed: 'fresh' };
    await r.app.save();
    assert.deepEqual(r.requests.map(q => [q.method, q.query]), [['PUT', { keepOverrides: 'true', fields: 'enabled' }]]);
    assert.equal(r.server.Enabled, false); assert.equal(r.server.PerLibrary, true); assert.equal(r.server.Everyday.Sdh, 'Avoid');
    assert.deepEqual(r.server.Everyday.FutureSetting, { server: 'fresh' }); assert.equal(r.server.Seed.FutureSeed, 'fresh');
    assert.equal(r.window.BabelfinApp.hasChanges(), false);
});
test('Everyday and seed intent uses camelCase leaf paths and sends explicit null for clears', async () => {
    const r = await intentApp();
    for (const doc of [r.app.state.doc, r.app.state.base, r.server]) {
        doc.Everyday.Sdh = 'Avoid'; doc.Everyday.AvoidCommentary = true; doc.PerLibrary = true; doc.Seed = { Confirmed: false, FromTemplate: true };
    }
    delete r.app.state.doc.Everyday.Sdh; r.app.state.doc.Everyday.AvoidCommentary = false;
    delete r.app.state.doc.PerLibrary; r.app.state.doc.Seed.Confirmed = true;
    r.server.Everyday.UsesAudioDescription = true; r.server.Seed.FromTemplate = false;
    await r.app.save(); const request = r.requests.find(q => q.method === 'PUT');
    assert.deepEqual(request.query, { keepOverrides: 'true', fields: 'everyday.avoidCommentary,everyday.sdh,perLibrary,seed.confirmed' });
    assert.equal(request.body.Everyday.Sdh, null); assert.equal(request.body.PerLibrary, null);
    assert.equal(r.server.Everyday.Sdh, null); assert.equal(r.server.Everyday.AvoidCommentary, false);
    assert.equal(r.server.Everyday.UsesAudioDescription, true); assert.equal(r.server.Seed.FromTemplate, false);
    assert.equal(r.server.Seed.Confirmed, true);
});
test('an unchanged or null-equivalent draft never emits an empty fields query or a legacy full PUT', async () => {
    const r = await intentApp(); r.app.state.doc.PerLibrary = null; r.app.state.doc.Everyday.Sdh = null;
    await r.app.save(); assert.deepEqual(r.requests, []);
});
test('stable library leaf intent preserves concurrent hidden fields and rows without a preflight', async () => {
    const r = await intentApp(), id = 'a'.repeat(32), added = { LibraryId: 'c'.repeat(32), Settings: { Sdh: 'Avoid' } };
    const original = { LibraryId: id, Name: 'Movies', Settings: { AudioChain: [{ Kind: 'FileDefault' }], Sdh: 'Neutral', UsesAudioDescription: false }, FutureRow: 'old' };
    for (const doc of [r.app.state.doc, r.app.state.base, r.server]) doc.Libraries = [copy(original)];
    r.app.state.doc.Libraries[0].Settings.AudioChain = [{ Kind: 'OriginalLanguage' }, { Kind: 'VideoDefault' }];
    Object.assign(r.server.Libraries[0], { FutureRow: 'fresh' }); Object.assign(r.server.Libraries[0].Settings, { Sdh: 'Avoid', UsesAudioDescription: true, FutureOption: ['fresh'] });
    r.server.Libraries.push(added); await r.app.save();
    assert.deepEqual(r.requests.map(q => q.method), ['PUT']);
    assert.equal(r.requests[0].query.fields, 'libraries.' + id + '.settings.audioChain');
    assert.ok(!r.requests[0].query.fields.split(',').includes('libraries'), 'Broad and leaf intent never overlap');
    assert.deepEqual(r.server.Libraries[0].Settings.AudioChain, [{ Kind: 'OriginalLanguage' }, { Kind: 'VideoDefault' }]);
    assert.equal(r.server.Libraries[0].Settings.Sdh, 'Avoid'); assert.equal(r.server.Libraries[0].Settings.UsesAudioDescription, true);
    assert.deepEqual(r.server.Libraries[0].Settings.FutureOption, ['fresh']); assert.equal(r.server.Libraries[0].FutureRow, 'fresh');
    assert.deepEqual(r.server.Libraries[1], added);
});
test('clearing the last visible library field retains a concurrently added hidden setting', async () => {
    const r = await intentApp(), id = 'a'.repeat(32);
    const original = { LibraryId: id, Settings: { AudioChain: [{ Kind: 'FileDefault' }] } };
    for (const doc of [r.app.state.doc, r.app.state.base, r.server]) doc.Libraries = [copy(original)];
    r.app.state.doc.Libraries = []; r.server.Libraries[0].Settings.Sdh = 'Avoid';
    await r.app.save();
    assert.equal(r.server.Libraries.length, 1); assert.equal(r.server.Libraries[0].Settings.Sdh, 'Avoid');
    assert.ok(!r.server.Libraries[0].Settings.AudioChain);
});
for (const status of [401, 409, 503]) test(`failed library leaf Save ${status} retains the draft/baseline`, async () => {
    const r = await intentApp(); r.app.state.doc.Libraries.push({ LibraryId: 'a'.repeat(32), Settings: { Sdh: 'Avoid' } });
    const draft = copy(r.app.state.doc), baseline = copy(r.app.state.base); r.intentStatus.write = status;
    await r.app.save(); assert.deepEqual(r.requests.map(q => q.method), ['PUT']);
    assert.equal(r.requests[0].query.fields, 'libraries.' + 'a'.repeat(32) + '.settings.sdh');
    assert.deepEqual(copy(r.app.state.doc), draft); assert.deepEqual(copy(r.app.state.base), baseline); assert.equal(r.app.state.saving, false);
});
test('edits made while a library leaf PUT is pending survive as a new dirty draft', async () => {
    const r = await intentApp(); r.app.state.doc.Libraries.push({ LibraryId: 'a'.repeat(32), Settings: { Sdh: 'Avoid' } });
    const write = r.holdWrite(); const saving = r.app.save(); await rig.settle();
    r.app.state.doc.Enabled = false; await rig.settle();
    r.app.state.doc.Everyday.Reads = ['de']; write(); await saving;
    assert.equal(r.requests.find(q => q.method === 'PUT').query.fields, 'libraries.' + 'a'.repeat(32) + '.settings.sdh');
    assert.equal(r.app.state.doc.Enabled, false); assert.deepEqual(copy(r.app.state.doc.Everyday.Reads), ['de']);
    assert.equal(r.app.state.base.Enabled, true); assert.deepEqual(copy(r.app.state.base.Everyday.Reads), ['en']);
    assert.equal(r.window.BabelfinApp.hasChanges(), true); await r.app.save();
    assert.equal(r.requests.filter(q => q.method === 'PUT').at(-1).query.fields, 'enabled,everyday.reads');
});

test('library leaf intent normalizes GUIDs, clears only named leaves, and never mixes broad paths', async () => {
    const r = await intentApp(), id = 'ABCDEF01-2345-6789-ABCD-EF0123456789';
    const original = { LibraryId: id, Settings: { Sdh: 'Avoid', AudioChain: [{ Kind: 'FileDefault' }], UsesAudioDescription: false } };
    for (const doc of [r.app.state.doc, r.app.state.base, r.server]) doc.Libraries = [copy(original)];
    delete r.app.state.doc.Libraries[0].Settings.Sdh;
    r.app.state.doc.Libraries[0].Settings.UsesAudioDescription = true;
    r.server.Libraries[0].Settings.WhenAudioUnderstood = 'Off'; await r.app.save();
    const request = r.requests[0], prefix = 'libraries.' + normalId(id) + '.settings.';
    assert.equal(request.query.fields, prefix + 'sdh,' + prefix + 'usesAudioDescription');
    assert.equal(request.body.Libraries[0].Settings.Sdh, null); assert.equal(r.server.Libraries[0].Settings.Sdh, null);
    assert.equal(r.server.Libraries[0].Settings.UsesAudioDescription, true);
    assert.equal(r.server.Libraries[0].Settings.WhenAudioUnderstood, 'Off');
    assert.deepEqual(r.server.Libraries[0].Settings.AudioChain, [{ Kind: 'FileDefault' }]);
});
test('hidden library edits arriving during PUT survive and removed unrelated rows stay removed', async () => {
    const r = await intentApp(), a = 'a'.repeat(32), b = 'b'.repeat(32), c = 'c'.repeat(32);
    const original = [{ LibraryId: a, Name: 'Old name', Settings: { Sdh: 'Neutral' } }, { LibraryId: b, Settings: { Sdh: 'Avoid' } }];
    for (const doc of [r.app.state.doc, r.app.state.base, r.server]) doc.Libraries = copy(original);
    r.app.state.doc.Libraries[0].Settings.Sdh = 'Prefer';
    const finish = r.holdWrite(), saving = r.app.save(); await rig.settle();
    r.server.Libraries[0].Name = 'Current name'; r.server.Libraries[0].Settings.UsesAudioDescription = true;
    r.server.Libraries.splice(1, 1); r.server.Libraries.push({ LibraryId: c, Settings: { Memory: 'Season' } });
    finish(); await saving;
    assert.equal(r.requests[0].query.fields, 'libraries.' + a + '.settings.sdh');
    assert.deepEqual(r.server.Libraries.map(row => row.LibraryId), [a, c]);
    assert.equal(r.server.Libraries[0].Name, 'Current name'); assert.equal(r.server.Libraries[0].Settings.UsesAudioDescription, true);
    assert.equal(r.server.Libraries[0].Settings.Sdh, 'Prefer'); assert.equal(r.window.BabelfinApp.hasChanges(), false);
});


// A real library dropdown stays editable while an unrelated Settings PUT is pending.
async function libraryRace(embedded, rows) {
    const ids = { a: 'a'.repeat(32), b: 'b'.repeat(32), c: 'c'.repeat(32) };
    const libraries = [{ Id: ids.a, Name: 'Movies' }, { Id: ids.b, Name: 'Shows' }, { Id: ids.c, Name: 'Other' }];
    const r = await intentApp({ embedded, libraries });
    for (const doc of [r.app.state.doc, r.app.state.base, r.server]) {
        doc.Libraries = copy(rows(ids)); doc.PerLibrary = true;
    }
    r.app.state.libraries = libraries; r.app.renderPage();
    const select = () => r.document.querySelectorAll('.bfui-table-row').find(row => row.dataset.row === ids.a).querySelector('.bfui-table-subtitles select');
    r.app.state.doc.Enabled = false;
    const release = r.holdWrite(), saving = r.app.save(); await rig.settle();
    assert.equal(select().disabled, false, 'The shipped dropdown remains editable during Save');
    assert.deepEqual(r.requests[0].query, { keepOverrides: 'true', fields: 'enabled' });
    return Object.assign(r, { ids, select, release, saving, choose(value) {
        const control = select(); control.focus(); control.value = value;
        control.dispatchEvent(new Event('change', { bubbles: true }));
    } });
}
const subtitleLeaves = id => ['subtitleLanguages', 'whenAudioNotUnderstood', 'whenAudioUnderstood'].map(key => 'libraries.' + id + '.settings.' + key);
for (const embedded of [false, true]) {
    const transport = embedded ? 'ApiClient' : 'fetch';
    test(`in-flight edit retains removed library identity/name and saves the visible choice (${transport})`, async () => {
        const r = await libraryRace(embedded, ids => [
            { LibraryId: ids.a, Name: 'Movies', Settings: { WhenAudioUnderstood: 'VideoDefault', Memory: 'Season', FutureSetting: 'removed' } },
            { LibraryId: ids.b, Name: 'Shows', Settings: { Sdh: 'Avoid' } }
        ]);
        const c = { LibraryId: r.ids.c, Name: 'Server added', Settings: { Sdh: 'Prefer', FutureSetting: 'fresh' }, FutureRow: true };
        r.server.Libraries = [copy(c)]; r.choose('off'); r.release(); await r.saving;
        const row = r.app.state.doc.Libraries.find(row => row.LibraryId === r.ids.a);
        assert.ok(row, 'The edited row retains a usable LibraryId'); assert.equal(row.Name, 'Movies');
        assert.deepEqual(copy(row.Settings), { WhenAudioUnderstood: 'Off', WhenAudioNotUnderstood: 'Off' }, 'Removed hidden settings are not reconstructed');
        assert.equal(r.select().value, 'off'); assert.equal(r.window.BabelfinApp.hasChanges(), true);
        assert.deepEqual(copy(r.app.state.base.Libraries), [c], 'Only the response becomes the saved baseline');
        assert.ok(!r.app.state.doc.Libraries.some(row => row.LibraryId === r.ids.b), 'Unedited removed rows stay removed');
        await r.app.save();
        assert.equal(r.requests.length, 2, 'A second Save actually persists the retained choice');
        assert.equal(r.requests[1].query.fields, subtitleLeaves(r.ids.a).slice(1).join(','));
        assert.equal(r.requests[1].body.Libraries.find(row => row.LibraryId === r.ids.a).Name, 'Movies');
        assert.equal(r.server.Libraries.find(row => row.LibraryId === r.ids.a).Settings.WhenAudioNotUnderstood, 'Off');
        assert.deepEqual(r.server.Libraries.find(row => row.LibraryId === r.ids.c), c);
        assert.equal(r.window.BabelfinApp.hasChanges(), false); assert.equal(r.select().value, 'off');
    });
    for (const serverCreatesRow of [false, true]) test(`in-flight new row preserves identity and fresh row metadata (${transport}, server row ${serverCreatesRow})`, async () => {
        const r = await libraryRace(embedded, ids => [{ LibraryId: ids.b, Name: 'Shows', Settings: { Memory: 'Season' } }]);
        const fresh = { LibraryId: r.ids.a, Name: 'Fresh server name', Settings: { Memory: 'TitleOnly', FutureSetting: ['fresh'] }, FutureRow: { keep: true } };
        if (serverCreatesRow) r.server.Libraries.push(copy(fresh));
        r.choose('off'); r.release(); await r.saving;
        const row = r.app.state.doc.Libraries.find(row => row.LibraryId === r.ids.a);
        assert.ok(row); assert.equal(row.Name, serverCreatesRow ? fresh.Name : 'Movies');
        assert.equal(row.Settings.WhenAudioUnderstood, 'Off'); assert.equal(r.select().value, 'off');
        if (serverCreatesRow) {
            assert.equal(row.Settings.Memory, fresh.Settings.Memory);
            assert.deepEqual(copy(row.Settings.FutureSetting), fresh.Settings.FutureSetting); assert.deepEqual(copy(row.FutureRow), fresh.FutureRow);
        }
        assert.equal(r.window.BabelfinApp.hasChanges(), true); await r.app.save();
        assert.equal(r.requests[1].query.fields, subtitleLeaves(r.ids.a).slice(1).join(','));
        assert.equal(r.window.BabelfinApp.hasChanges(), false);
        assert.equal(r.server.Libraries.find(row => row.LibraryId === r.ids.a).Name, serverCreatesRow ? fresh.Name : 'Movies');
    });
    for (const explicitNull of [false, true]) test(`in-flight default clear does not reconstruct an empty deleted row (${transport}, null ${explicitNull})`, async () => {
        const r = await libraryRace(embedded, ids => [{ LibraryId: ids.a, Name: 'Movies', Settings: {
            WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['en'], Memory: 'Season'
        } }]);
        r.server.Libraries = []; r.choose('');
        if (explicitNull) {
            const settings = r.app.state.doc.Libraries[0].Settings;
            settings.WhenAudioUnderstood = null; settings.WhenAudioNotUnderstood = null; settings.SubtitleLanguages = null;
        }
        r.release(); await r.saving;
        assert.deepEqual(copy(r.app.state.doc.Libraries), [], 'A clear already satisfied by row deletion stays satisfied');
        assert.deepEqual(copy(r.app.state.base.Libraries), []);
        assert.equal(r.select().value, ''); assert.equal(r.window.BabelfinApp.hasChanges(), false);
        await r.app.save(); assert.equal(r.requests.length, 1, 'No empty or identity-less follow-up PUT is possible');
    });
    test(`in-flight default clear preserves surviving server hidden fields and row name (${transport})`, async () => {
        const r = await libraryRace(embedded, ids => [{ LibraryId: ids.a, Name: 'Movies', Settings: {
            WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full', SubtitleLanguages: ['en'], Memory: 'Season', FutureSetting: 'old'
        } }]);
        const row = r.server.Libraries[0]; row.Name = 'Current name'; row.Settings.Memory = 'TitleOnly';
        row.Settings.FutureSetting = 'fresh'; row.Settings.UsesAudioDescription = true; row.FutureRow = 'fresh';
        r.choose(''); r.release(); await r.saving;
        const pending = r.app.state.doc.Libraries[0];
        assert.equal(pending.LibraryId, r.ids.a); assert.equal(pending.Name, 'Current name');
        assert.equal(pending.Settings.Memory, 'TitleOnly'); assert.equal(pending.Settings.FutureSetting, 'fresh');
        assert.equal(pending.Settings.UsesAudioDescription, true); assert.equal(pending.FutureRow, 'fresh');
        assert.equal(r.select().value, ''); assert.equal(r.window.BabelfinApp.hasChanges(), true); await r.app.save();
        assert.equal(r.requests[1].query.fields, subtitleLeaves(r.ids.a).join(','));
        assert.equal(r.server.Libraries[0].Settings.WhenAudioUnderstood, null);
        assert.equal(r.server.Libraries[0].Settings.SubtitleLanguages, null);
        assert.equal(r.server.Libraries[0].Settings.FutureSetting, 'fresh'); assert.equal(r.server.Libraries[0].Name, 'Current name');
        assert.equal(r.window.BabelfinApp.hasChanges(), false);
    });
    test(`in-flight unchanged removed rows are never resurrected (${transport})`, async () => {
        const r = await libraryRace(embedded, ids => [
            { LibraryId: ids.a, Name: 'Movies', Settings: { Memory: 'Season' } },
            { LibraryId: ids.b, Name: 'Shows', Settings: { Sdh: 'Avoid' } }
        ]);
        r.server.Libraries = []; r.release(); await r.saving;
        assert.deepEqual(copy(r.app.state.doc.Libraries), []); assert.deepEqual(copy(r.app.state.base.Libraries), []);
        assert.equal(r.window.BabelfinApp.hasChanges(), false); await r.app.save(); assert.equal(r.requests.length, 1);
    });
}

for (const embedded of [false, true]) test(`in-flight clear already satisfied by the response retains its empty row identity (${embedded ? 'ApiClient' : 'fetch'})`, async () => {
    const r = await libraryRace(embedded, ids => [{ LibraryId: ids.a, Name: 'Movies', Settings: {
        WhenAudioUnderstood: 'Full', WhenAudioNotUnderstood: 'Full'
    } }]);
    r.choose(''); assert.equal(r.app.state.doc.Libraries.length, 0, 'The real default action dropped its empty draft row');
    r.server.Libraries[0].Name = 'Current name';
    r.server.Libraries[0].Settings.WhenAudioUnderstood = null; r.server.Libraries[0].Settings.WhenAudioNotUnderstood = null;
    r.release(); await r.saving;
    assert.deepEqual(copy(r.app.state.doc.Libraries.map(row => [row.LibraryId, row.Name])), [[r.ids.a, 'Current name']]);
    assert.equal(r.app.state.doc.Libraries[0].Settings.WhenAudioUnderstood ?? null, null);
    assert.equal(r.select().value, ''); assert.equal(r.window.BabelfinApp.hasChanges(), false);
    await r.app.save(); assert.equal(r.requests.length, 1, 'An already-satisfied clear cannot leave a dirty draft with no writable fields');
});

// Native TV spatial navigation can run without inputManager command subscribers.
// Its candidates include .focusable, or controls whose tabindex is not -1.
function nativeMenuCandidates(menu) {
    return menu.querySelectorAll('a[href], [role="menuitem"]').filter(n =>
        !n.disabled && !n.hidden && n.getAttribute('aria-disabled') !== 'true'
        && (n.classList.contains('focusable') || n.getAttribute('tabindex') !== '-1'));
}
function nativeMenu(r) {
    const menu = node(r, 'ul', { id: 'app-user-menu', role: 'menu' });
    const profile = node(r, 'a', { href: '#/profile', role: 'menuitem', tabindex: '0' }, menu);
    const settings = node(r, 'a', { href: '#/mypreferencesmenu', role: 'menuitem', tabindex: '-1' }, menu);
    const logout = node(r, 'li', { role: 'menuitem', tabindex: '-1' }, menu);
    return { menu, profile, settings, logout };
}
test('TV native spatial candidates include every live roving-menu row and Babelfin', () => {
    const r = client(); r.document.documentElement.classList.add('layout-tv');
    const { menu, profile, settings, logout } = nativeMenu(r);
    r.api.applyUserMenu(); const item = menu.querySelector('.bfMenuItem');
    assert.deepEqual(nativeMenuCandidates(menu).map(n => n.getAttribute('href')), [profile, settings, item, logout].map(n => n.getAttribute('href')), 'Native input must not skip tabindex=-1 Settings or trap focus at Babelfin');
    profile.focus();
    for (const expected of [settings, item, logout]) {
        const candidates = nativeMenuCandidates(menu), at = candidates.indexOf(r.document.activeElement);
        candidates[at + 1].focus(); assert.ok(r.document.activeElement === expected);
    }
    assert.equal(menu.querySelectorAll('.bfMenuItem').length, 1);
    assert.equal(menu.listeners.keydown.length, 1); assert.equal(menu.listeners.command.length, 1);
});
test('TV native focus eligibility follows menu rebuilds and newly inserted host rows', () => {
    const r = client(), { menu } = nativeMenu(r);
    r.api.applyUserMenu(); const item = menu.querySelector('.bfMenuItem');
    assert.equal(item.getAttribute('tabindex'), '-1'); assert.equal(item.classList.contains('focusable'), false);
    r.document.documentElement.classList.add('layout-tv'); r.api.applyUserMenu();
    assert.ok(nativeMenuCandidates(menu).includes(item), 'An already-open desktop menu must work after switching to TV');
    const added = node(r, 'a', { href: '#/downloads', role: 'menuitem', tabindex: '-1' }, menu);
    r.api.applyUserMenu(); assert.ok(nativeMenuCandidates(menu).includes(added));
    item.remove(); r.api.applyUserMenu(); r.api.applyUserMenu();
    assert.equal(menu.querySelectorAll('.bfMenuItem').length, 1);
    assert.equal(menu.listeners.keydown.length, 1); assert.equal(menu.listeners.command.length, 1);
    assert.ok(nativeMenuCandidates(menu).includes(menu.querySelector('.bfMenuItem')));
});
test('TV focus integration skips disabled/hidden rows and preserves host-owned focus classes', () => {
    const r = client(); r.document.documentElement.classList.add('layout-tv');
    const { menu, profile, settings } = nativeMenu(r); profile.classList.add('focusable');
    const disabled = node(r, 'a', { href: '#/disabled', role: 'menuitem', tabindex: '-1', 'aria-disabled': 'true' }, menu);
    const hidden = node(r, 'a', { href: '#/hidden', role: 'menuitem', tabindex: '-1', hidden: '' }, menu);
    r.api.applyUserMenu(); assert.equal(disabled.classList.contains('focusable'), false); assert.equal(hidden.classList.contains('focusable'), false);
    assert.equal(settings.classList.contains('focusable'), true);
    settings.setAttribute('aria-disabled', 'true'); r.api.applyUserMenu(); assert.equal(settings.classList.contains('focusable'), false);
    settings.removeAttribute('aria-disabled'); r.api.applyUserMenu(); assert.equal(settings.classList.contains('focusable'), true);
    r.document.documentElement.classList.remove('layout-tv'); r.api.applyUserMenu();
    assert.equal(profile.classList.contains('focusable'), true, 'A class supplied by Jellyfin stays owned by Jellyfin');
    assert.equal(settings.classList.contains('focusable'), false); assert.equal(menu.querySelector('.bfMenuItem').classList.contains('focusable'), false);
    assert.equal(settings.getAttribute('tabindex'), '-1', 'Native TV integration does not rewrite React roving tabindex');
});
for (const embedded of [false, true]) test(`Save sign-in failure announces its explanation on focused actions (${embedded ? 'embedded' : 'standalone'})`, async () => {
    const r = await rig.appBrowser({ embedded }); r.app.state.doc.Enabled = false;
    const draft = copy(r.app.state.doc), baseline = copy(r.app.state.base);
    await r.app.save();
    const message = r.document.querySelector('.bf-message');
    assert.equal(message.getAttribute('role'), 'alert'); assert.equal(message.getAttribute('aria-atomic'), 'true');
    const link = message.querySelector('a'), retry = message.querySelector('button');
    assert.ok(r.document.activeElement === link && link.isConnected);
    for (const action of [link, retry]) {
        const descriptions = (action.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean).map(id => r.document.getElementById(id));
        assert.equal(descriptions.length, 2, 'The focused action announces both the sign-in heading and reason');
        assert.ok(descriptions.every(n => n && message.contains(n) && n.textContent));
        assert.equal(descriptions[0].textContent, rig.translator()('app.signIn.title'));
        assert.equal(descriptions[1].textContent, rig.translator()('app.signIn.text'));
    }
    assert.deepEqual(copy(r.app.state.doc), draft); assert.deepEqual(copy(r.app.state.base), baseline);
    assert.equal(r.document.querySelector('.bf-savebar'), null);
    r.signIn(); retry.click(); await rig.settle();
    assert.ok(r.document.querySelector('.bfui-table')); assert.ok(r.document.activeElement.isConnected);
    assert.deepEqual(copy(r.app.state.doc), draft); assert.deepEqual(copy(r.app.state.base), baseline);
});
test('a repeated sign-in failure keeps a single live alert and valid focus descriptions', async () => {
    const r = await rig.appBrowser(); r.app.state.doc.Enabled = false; await r.app.save();
    r.signIn(); r.pingFails(401); r.document.querySelector('.bf-message button').click(); await rig.settle();
    const message = r.document.querySelector('.bf-message'), retry = message.querySelector('button');
    assert.equal(message.getAttribute('role'), 'alert'); assert.ok(r.document.activeElement === retry && !retry.disabled);
    for (const id of retry.getAttribute('aria-describedby').split(/\s+/)) {
        assert.equal(r.document.querySelectorAll('#' + id).length, 1); assert.ok(message.contains(r.document.getElementById(id)));
    }
});
test('My languages has no tooltip in the page heading or audio dropdown', async () => {
    const r = await rig.appBrowser();
    assert.equal(r.document.querySelector('.bf-languages-line .bfui-info'), null);
    const select = r.document.querySelector('.bfui-table-audio select');
    select.value = 'mine'; select.dispatchEvent(new Event('change', { bubbles: true }));
    assert.equal(select.getAttribute('aria-describedby'), null);
    assert.ok(r.document.querySelectorAll('.bfui-table-audio .bfui-info-wrap').every(n => n.hidden));
});

test('native TV popup focus scope is temporary and does not remove a scope owned by Jellyfin', () => {
    const r = client(), { menu } = nativeMenu(r);
    r.document.documentElement.classList.add('layout-tv'); r.api.applyUserMenu();
    assert.equal(menu.classList.contains('focuscontainer'), true, 'Spatial focus stays within the popup at its first/last row');
    r.document.documentElement.classList.remove('layout-tv'); r.api.applyUserMenu();
    assert.equal(menu.classList.contains('focuscontainer'), false);
    menu.classList.add('focuscontainer'); r.document.documentElement.classList.add('layout-tv'); r.api.applyUserMenu();
    r.document.documentElement.classList.remove('layout-tv'); r.api.applyUserMenu();
    assert.equal(menu.classList.contains('focuscontainer'), true, 'Existing host scope is preserved');
});

test('a kept-mounted TV menu refreshes native focus when opened or hidden without new rows', () => {
    const r = client(), { menu } = nativeMenu(r); r.document.documentElement.classList.add('layout-tv');
    const Observer = r.window.MutationObserver, callbacks = new Map();
    r.window.MutationObserver = class extends Observer {
        observe(node, options) { super.observe(node, options); callbacks.set(node, this.fn); }
    };
    menu.setAttribute('aria-hidden', 'true'); menu.classList.add('MuiModal-hidden'); r.api.applyUserMenu();
    const item = menu.querySelector('.bfMenuItem');
    assert.equal(item.classList.contains('focusable'), false, 'Closed popup rows must not become native spatial targets');
    const observer = r.observers.find(o => o.node === menu); assert.ok(observer, 'Kept-mounted open/close changes need their own refresh');
    assert.ok(observer.options.attributes && observer.options.subtree);
    menu.removeAttribute('aria-hidden'); menu.classList.remove('MuiModal-hidden'); callbacks.get(menu)([]);
    assert.equal(item.classList.contains('focusable'), true); assert.equal(menu.classList.contains('focuscontainer'), true);
    menu.setAttribute('aria-hidden', 'true'); callbacks.get(menu)([]);
    assert.equal(item.classList.contains('focusable'), false); assert.equal(menu.classList.contains('focuscontainer'), false);
    r.api.applyUserMenu(); assert.equal(r.observers.filter(o => o.node === menu).length, 1);
    assert.equal(menu.querySelectorAll('.bfMenuItem').length, 1);
});

for (const admin of [false, true]) test('My languages selection has no information tooltip in the ' + (admin ? 'admin' : 'person') + ' table', () => {
    const r = rig.environment(); vm.runInContext(source('ui.js'), r.context);
    const host = node(r, 'div');
    host.appendChild(r.window.BabelfinUI.tableChoice({ t: rig.translator(), audience: admin ? 'admin' : 'person', value: '', onChange() {} }).element);
    const select = host.querySelector('select'); assert.ok(select); select.value = 'mine'; select.dispatchEvent(new Event('change'));
    assert.equal(select.hasAttribute('aria-describedby'), false);
    assert.ok(!host.querySelectorAll('.bfui-info').some(n => !n.parentElement.hidden));
});
test('My languages information keys are absent in every locale and fallback', () => {
    for (const file of fs.readdirSync(path.join(assets, 'i18n')).filter(f => f.endsWith('.json'))) {
        assert.ok(!Object.keys(JSON.parse(source('i18n/' + file))).some(k => /myLanguages.*\.info$/i.test(k)), file);
    }
    assert.ok(!source('client.js').includes('audioMyLanguages.info'));
    assert.match(source('client.js'), /'ui.info.about':/);
});

test('TV Settings iframe load focuses the frame and the page first control', () => {
    const r = client(); r.document.documentElement.classList.add('layout-tv');
    r.api.showPanel(); const frame = r.document.querySelector('.bfSettingsFrame'); let focused = 0;
    frame.contentWindow = { BabelfinApp: { focusFirst() { focused++; } } };
    frame.dispatchEvent(new Event('load'));
    assert.equal(r.document.activeElement, frame); assert.equal(focused, 1);
});

function audioTipTransitions(document, select) {
    const line = select.closest('.bfui-choice-line');
    for (let i = 0; i < 3; i++) {
        select.value = 'original'; select.dispatchEvent(new Event('change', { bubbles: true }));
        const button = line.querySelector('.bfui-info'), id = select.getAttribute('aria-describedby');
        assert.ok(button && id); button.click(); assert.equal(document.getElementById(id).hidden, false);
        select.focus(); select.value = 'mine'; select.dispatchEvent(new Event('change', { bubbles: true }));
        assert.equal(select.getAttribute('aria-describedby'), null);
        assert.equal(Boolean(line.querySelector('.bfui-info')), false);
        assert.equal(Boolean(document.getElementById(id)), false, 'The prior tooltip is detached, including if it was open');
    }
}
for (const where of ['person page', 'item dialog']) test('switching Original and My languages removes and restores the tooltip: ' + where, async () => {
    if (where === 'person page') {
        const r = await rig.appBrowser(); audioTipTransitions(r.document, r.document.querySelector('.bfui-table-audio select'));
    } else {
        const r = client(), box = dialog(r, { Settings: { Everyday: {}, BurnedIn: [] } });
        audioTipTransitions(r.document, box.querySelector('#bfDialogAudio'));
    }
});

test('all stable action-specific problem codes have person and admin translations', () => {
    const codes = ['item-unavailable','person-unavailable','missing-settings','copy-target-required','simulation-target-required','simulation-busy','empty-import','invalid-import','invalid-override-scope','invalid-override-target','invalid-collection-target','burned-in-limit','override-limit'];
    for (const folder of ['i18n', 'i18n-admin']) for (const file of fs.readdirSync(path.join(assets, folder)).filter(f => f.endsWith('.json'))) {
        const words = JSON.parse(source(folder + '/' + file));
        for (const code of codes) assert.ok(words['api.' + code], folder + '/' + file + ': ' + code);
    }
});
