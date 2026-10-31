'use strict';
// Execute the shipped dashboard with the maintained admin DOM/API fixtures; no network or production server.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Module = require('node:module');
const filename = path.join(__dirname, 'ui-smoke/admin/test.js');
const fixture = new Module(filename, module); fixture.filename = filename; fixture.paths = module.paths;
const fixtureText = fs.readFileSync(filename, 'utf8');
assert.ok(fixtureText.includes('async function main()'), 'Maintained admin fixture boundary exists');
fixture._compile(fixtureText.slice(0, fixtureText.indexOf('async function main()')) + '\nmodule.exports = { boot, settle, openFold, openGroup, Event };', filename);
const { boot, settle, openFold, openGroup, Event } = fixture.exports;
const copy = value => JSON.parse(JSON.stringify(value));
const defer = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
async function env(options = {}) {
    const r = await boot({ patchScript(name, text) {
        if (name !== 'dashboard.js') return text;
        if (process.env.BF_ADMIN_DASHBOARD_SOURCE) text = fs.readFileSync(process.env.BF_ADMIN_DASHBOARD_SOURCE, 'utf8');
        if (options.patch) text = options.patch(text);
        return text.replace(/\}\)\(\);\s*$/, 'window.adminFixture = { api, state: () => S, save: savePage, render: renderSettings, refresh: refreshAfterMutation, preview: renderImportPreview, libraries: renderLibraries, people: renderPeople, advanced: renderAdvanced, activity: activityTool, apps: appsGroup, check: checkTool, backup: backupTool, reset: resetTool, person: personEditor, fields: typeof editedFields === "function" ? editedFields : () => [] };\n})();');
    }, afterScript(name, ctx) {
        if (name !== 'dashboard.js') return;
        ctx.Emby = { Page: { show: async (...args) => ctx.routeCalls.push(['show', ...args]), back: async () => ctx.routeCalls.push(['back']) } };
        ctx.routeCalls = []; if (options.legacy) legacyHistory(ctx);
        if (!options.legacy) ctx.navigation = { listeners: [], addEventListener(name, fn) { this.listeners.push(fn); }, removeEventListener(name, fn) { this.listeners = this.listeners.filter(listener => listener !== fn); }, navigate(url) { ctx.routeCalls.push(['navigate', url]); }, traverseTo(key) { ctx.routeCalls.push(['traverse', key]); } };
    } });
    r.a = r.ctx.adminFixture;
    return r;
}
function host(r) { const n = r.document.createElement('div'); r.page.appendChild(n); return n; }
function dirty(r) { r.page.querySelector('#bfEnabled').click(); assert.equal(Object.keys(r.a.state().pending).includes('Enabled'), true); }
async function answer(r, yes) {
    await settle(3);
    const box = r.document.querySelector('.bfui-dialog'); assert.ok(box, 'A leave confirmation is visible');
    const labels = box.querySelectorAll('button').map(b => b.textContent);
    assert.deepEqual(labels, ['Cancel', 'Discard']);
    box.querySelectorAll('button')[yes ? 1 : 0].click(); await settle();
}
async function reenter(r) { r.ctx.BabelfinDashboard.hide(); r.ctx.BabelfinDashboard.show(r.page); await settle(45); return r.a.state(); }

test('save preserves fresh hidden Advanced and unknown fields while Advanced stays unopened', async () => {
    const r = await env();
    try {
        dirty(r);
        const cfg = r.server.state.config;
        Object.assign(cfg, { ManualChangeAfterSeconds: 17, ActivityRetentionDays: 23, FutureSetting: { on: true } });
        cfg.Everyday.UnlabelledAudio = 'Unknown'; cfg.Everyday.FutureRecognition = ['custom'];
        cfg.AppModes[0].Mode = 'Trust'; cfg.Keywords.PartialWords = ['server-edited'];
        const before = copy(cfg); await r.a.save(); const after = copy(r.server.state.config);
        before.Enabled = false; assert.deepEqual(after, before);
        assert.equal(r.a.state().rendered.advanced, undefined);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const status of [401, 403, 409, 503]) test(`failed save ${status} retains edits, baseline and leave protection`, async () => {
    const r = await env();
    try {
        dirty(r); const state = r.a.state(), before = copy(state.config);
        r.a.api.put = async () => { throw { status }; };
        await r.a.save();
        assert.equal(typeof state.pending.Enabled, 'function'); assert.deepEqual(copy(state.config), before);
        assert.equal(r.page.querySelector('#bfEnabled').checked, false);
        assert.equal(r.page.querySelector('#bfSave').disabled, false);
        assert.ok(r.page.querySelector('#bfSaveError').textContent.includes(String(status)));
        const leaving = r.ctx.Emby.Page.back(); await answer(r, false); await leaving;
        assert.deepEqual(r.ctx.routeCalls, []);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const success of [false, true]) test(`late save ${success ? 'success' : 'failure'} cannot change re-entered page`, async () => {
    const r = await env();
    try {
        dirty(r); const held = defer(), originalPut = r.a.api.put;
        r.a.api.put = () => held.promise;
        const saving = r.a.save(); await settle();
        const fresh = await reenter(r); dirty(r); const before = copy(fresh.config);
        const errors = r.page.querySelector('#bfSaveError').textContent;
        if (success) held.resolve({ Enabled: false, FutureSetting: 'obsolete' }); else held.reject({ status: 401 });
        await saving; await settle();
        assert.deepEqual(copy(fresh.config), before, 'Old completion must not replace current settings');
        assert.equal(typeof fresh.pending.Enabled, 'function', 'Current draft survives');
        assert.equal(r.page.querySelector('#bfSaveError').textContent, errors, 'Old failure must not appear in the current UI');
        assert.equal(r.page.querySelector('#bfEnabled').checked, false);
        r.a.api.put = originalPut;
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('leaving while save reread is pending prevents a late PUT', async () => {
    const r = await env();
    try {
        dirty(r); const held = defer(), get = r.a.api.get;
        r.a.api.get = (route, ...args) => route === 'Babelfin/Admin/Settings' ? held.promise : get(route, ...args);
        const saving = r.a.save(); r.ctx.BabelfinDashboard.hide();
        held.resolve(copy(r.server.state.config)); await saving;
        assert.equal(r.server.state.puts.length, 0, 'An abandoned save cannot submit a late write');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('Cancel and Discard guard host navigation; handlers restore after hide and attach once on re-entry', async () => {
    const r = await env();
    try {
        dirty(r);
        let leaving = r.ctx.Emby.Page.show('/dashboard'); await answer(r, false); await leaving;
        assert.deepEqual(r.ctx.routeCalls, []); assert.equal(typeof r.a.state().pending.Enabled, 'function');
        leaving = r.ctx.Emby.Page.back(); await answer(r, true); await leaving;
        assert.deepEqual(r.ctx.routeCalls, [['back']]); assert.deepEqual(Object.keys(r.a.state().pending), []);
        r.ctx.BabelfinDashboard.hide();
        assert.equal((r.ctx.windowListeners.beforeunload || []).length, 0);
        await reenter(r); dirty(r);
        leaving = r.ctx.Emby.Page.show('/home'); await settle(4);
        assert.equal(r.document.querySelectorAll('.bfui-dialog').length, 1); await answer(r, false); await leaving;
        assert.equal((r.ctx.windowListeners.beforeunload || []).length, 1);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('beforeunload warns for a dirty shown page, then detaches on hide', async () => {
    const r = await env();
    try {
        dirty(r); const event = new Event('beforeunload');
        (r.ctx.windowListeners.beforeunload || []).forEach(fn => fn(event));
        assert.equal(event.defaultPrevented, true); assert.equal(event.returnValue, '');
        r.ctx.BabelfinDashboard.hide(); assert.equal((r.ctx.windowListeners.beforeunload || []).length, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('Enabled recovers after a failed mutation reread and re-entry', async () => {
    const r = await env();
    try {
        const get = r.a.api.get;
        r.a.api.get = (route, ...args) => route === 'Babelfin/Admin/Settings' ? Promise.reject({ status: 503 }) : get(route, ...args);
        await r.a.refresh(host(r)); assert.equal(r.page.querySelector('#bfEnabled').disabled, true);
        r.a.api.get = get; await reenter(r);
        assert.equal(r.page.querySelector('#bfEnabled').disabled, false);
        dirty(r); await r.a.save(); assert.equal(r.server.state.config.Enabled, false);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const success of [false, true]) test(`late mutation reread ${success ? 'success' : 'failure'} cannot affect re-entry`, async () => {
    const r = await env();
    try {
        const held = defer(), get = r.a.api.get;
        r.a.api.get = (route, ...args) => route === 'Babelfin/Admin/Settings' ? held.promise : get(route, ...args);
        const refreshing = r.a.refresh(host(r)); r.a.api.get = get;
        const fresh = await reenter(r); const before = copy(fresh.config);
        if (success) held.resolve({ Enabled: false }); else held.reject({ status: 503 });
        await refreshing; assert.deepEqual(copy(fresh.config), before);
        assert.equal(r.page.querySelector('#bfEnabled').disabled, false);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const section of ['apps', 'libraries', 'people']) for (const success of [false, true]) test(`obsolete ${section} ${success ? 'success' : 'failure'} leaves the current view intact`, async () => {
    const r = await env();
    try {
        const held = defer(), get = r.a.api.get;
        const route = 'Babelfin/Admin/' + ({ apps: 'Apps', libraries: 'Libraries', people: 'Users' })[section];
        r.a.api.get = (url, ...args) => url === route ? held.promise : get(url, ...args);
        const target = section === 'apps' ? host(r) : r.page.querySelector(section === 'libraries' ? '#bfLibraries' : '#bfPeople');
        if (section !== 'apps') delete r.a.state().cache[section === 'libraries' ? 'libraries' : 'users'];
        const render = r.a[section](target); r.a.api.get = get;
        await reenter(r); const state = r.a.state(), users = copy(state.userList), html = r.page.innerHTML;
        if (success) held.resolve([]); else held.reject({ status: 401 });
        await render; await settle();
        assert.deepEqual(copy(state.userList), users, 'Late data cannot replace current cached users');
        assert.equal(r.page.innerHTML, html, 'Late data/failure cannot repaint the current page');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('Check a title Escape first clears, then blurs, and never reaches host Back', async () => {
    const r = await env();
    try {
        const target = host(r); r.a.check(target); await settle();
        const person = target.querySelector('select'); person.value = person.options[1].value; person.dispatchEvent(new Event('change'));
        const input = target.querySelector('input[type="search"]'); assert.ok(input);
        let back = 0; const onBack = event => { if (event.key === 'Escape') back++; };
        r.document.addEventListener('keydown', onBack);
        input.focus(); input.value = 'Sable';
        let key = new Event('keydown', { key: 'Escape', bubbles: true }); input.dispatchEvent(key);
        assert.equal(key.defaultPrevented, true); assert.equal(input.value, ''); assert.equal(r.document.activeElement, input); assert.equal(back, 0);
        key = new Event('keydown', { key: 'Escape', bubbles: true }); input.dispatchEvent(key);
        assert.equal(key.defaultPrevented, true); assert.notEqual(r.document.activeElement, input); assert.equal(back, 0);
        r.ctx.BabelfinDashboard.hide();
        input.dispatchEvent(new Event('keydown', { key: 'Escape', bubbles: true })); assert.equal(back, 1, 'Page handler detaches on hide');
        r.document.removeEventListener('keydown', onBack);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const [name, result, applicable] of [
    ['empty', {}, false], ['warning only', { Warnings: ['Unsupported version'] }, false],
    ['all skipped', { Users: [{ Action: 'skip' }] }, false],
    ['unknown action', { Users: [{ Action: 'future' }] }, false],
    ['admin changes', { Admin: { WillChange: true } }, true],
    ['valid person with warning', { Warnings: ['Admin invalid'], Users: [{ Action: 'skip' }, { Action: 'update' }] }, true],
    ['camelCase create', { users: [{ action: 'create' }] }, true],
    ['already applied', { Applied: true, Admin: { WillChange: true } }, false]
]) test(`import ${name} offers Apply only if applicable`, async () => {
    const r = await env();
    try {
        const target = host(r); r.a.preview(target, result, {}, host(r));
        const apply = target.querySelector('button.button-submit');
        assert.equal(!!apply && !apply.disabled, applicable);
        assert.equal(r.server.state.requests.filter(q => q.method === 'POST' && q.p.endsWith('/Import')).length, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('field intent keeps Advanced edits that land between admin GET and PUT', async () => {
    const r = await env();
    try {
        dirty(r); const put = r.a.api.put; let fields;
        r.a.api.put = (route, body, query) => {
            fields = query && query.fields;
            Object.assign(r.server.state.config, { UnknownAppMode: 'ReplaceUnlessManual', CorrectAfterStart: 'Off', FutureHidden: { keep: 9 } });
            r.server.state.config.Everyday.UnlabelledAudio = 'Unknown';
            r.server.state.config.Keywords.PartialWords = ['concurrent'];
            return put(route, body, query);
        };
        await r.a.save();
        assert.equal(fields, 'enabled');
        const cfg = r.server.state.config;
        assert.equal(cfg.Enabled, false); assert.equal(cfg.UnknownAppMode, 'ReplaceUnlessManual');
        assert.equal(cfg.CorrectAfterStart, 'Off'); assert.deepEqual(cfg.FutureHidden, { keep: 9 });
        assert.equal(cfg.Everyday.UnlabelledAudio, 'Unknown'); assert.deepEqual(cfg.Keywords.PartialWords, ['concurrent']);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const [field, mutate, expected] of [
    ['everyday.unlabelledAudio', cfg => { cfg.Everyday.UnlabelledAudio = null; }, null],
    ['keywords.partialWords', cfg => { cfg.Keywords.PartialWords = []; }, []]
]) test(`field intent preserves sibling fields and supports explicit default ${field}`, async () => {
    const r = await env();
    try {
        r.server.state.config.Everyday.UnlabelledAudio = 'Unknown';
        r.a.state().config.Everyday.UnlabelledAudio = 'Unknown';
        r.a.state().pending.changed = mutate;
        const put = r.a.api.put; let fields;
        r.a.api.put = (route, body, query) => {
            fields = query && query.fields;
            r.server.state.config.Everyday.AudioChain = [{ Kind: 'OriginalLanguage' }];
            r.server.state.config.Keywords.FullWords = ['new-full'];
            return put(route, body, query);
        };
        await r.a.save(); assert.equal(fields, field);
        const cfg = r.server.state.config;
        assert.deepEqual(cfg.Everyday.AudioChain, [{ Kind: 'OriginalLanguage' }]);
        assert.deepEqual(cfg.Keywords.FullWords, ['new-full']);
        assert.deepEqual(field.startsWith('everyday') ? cfg.Everyday.UnlabelledAudio : cfg.Keywords.PartialWords, expected);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('failed reread keeps the draft and submits no write; save freezes editors until completion', async () => {
    const r = await env();
    try {
        dirty(r); const held = defer(), get = r.a.api.get;
        r.a.api.get = (route, ...args) => route === 'Babelfin/Admin/Settings' ? held.promise : get(route, ...args);
        const saving = r.a.save();
        assert.equal(r.page.querySelector('#bfEnabled').disabled, true);
        assert.equal(r.page.querySelector('#bfDiscard').disabled, true);
        held.reject({ status: 401 }); await saving;
        assert.equal(r.page.querySelector('#bfEnabled').disabled, false);
        assert.equal(r.page.querySelector('#bfEnabled').checked, false);
        assert.equal(r.page.querySelector('#bfDiscard').disabled, false);
        assert.equal(typeof r.a.state().pending.Enabled, 'function'); assert.equal(r.server.state.puts.length, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('multiple navigation requests open one dialog and resume only the first route', async () => {
    const r = await env();
    try {
        dirty(r); const first = r.ctx.Emby.Page.show('/home'), second = r.ctx.Emby.Page.back();
        await settle(4); assert.equal(r.document.querySelectorAll('.bfui-dialog').length, 1);
        await answer(r, true); await Promise.all([first, second]);
        assert.deepEqual(r.ctx.routeCalls, [['show', '/home']]);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const success of [false, true]) test(`Escape invalidates a late title-search ${success ? 'success' : 'failure'}`, async () => {
    const r = await env();
    try {
        const held = defer(), fetch = r.ctx.fetch;
        r.ctx.fetch = (url, options) => String(url).includes('/Items?') ? held.promise : fetch(url, options);
        const target = host(r); r.a.check(target); await settle();
        const person = target.querySelector('select'); person.value = person.options[1].value; person.dispatchEvent(new Event('change'));
        const input = target.querySelector('input[type="search"]'); input.value = 'Sable';
        input.dispatchEvent(new Event('keydown', { key: 'Enter', bubbles: true })); await settle(3);
        input.dispatchEvent(new Event('keydown', { key: 'Escape', bubbles: true }));
        const status = target.querySelector('.bfui-search [role="status"]'); assert.equal(status.textContent, '');
        if (success) held.resolve({ ok: true, status: 200, text: async () => JSON.stringify({ Items: [{ Id: 'a'.repeat(32), Name: 'Late title', Type: 'Series' }] }) });
        else held.reject(new Error('late network failure'));
        await settle();
        assert.equal(status.textContent, '', 'A cleared search cannot show a stale failure/count');
        assert.equal(target.querySelectorAll('.bfui-result').length, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('a partially valid import applies once and then rereads settings', async () => {
    const r = await env();
    try {
        const target = host(r); let writes = 0, reads = 0;
        const get = r.a.api.get;
        r.a.api.get = (url, ...args) => { if (url === 'Babelfin/Admin/Settings') reads++; return get(url, ...args); };
        r.a.api.post = async (url, doc, query) => {
            assert.equal(url, 'Babelfin/Admin/Import'); assert.equal(query.dryRun, false); writes++;
            return { Applied: true, Users: [{ Action: 'skip' }, { Action: 'update' }] };
        };
        r.a.preview(target, { Warnings: ['Invalid admin section'], Users: [{ Action: 'skip' }, { Action: 'update' }] }, { SchemaVersion: 1 }, host(r));
        target.querySelector('button.button-submit').click(); await settle(3);
        r.document.querySelector('.bfui-dialog').querySelectorAll('button')[1].click(); await settle(40);
        assert.equal(writes, 1); assert.equal(reads, 1); assert.ok(target.querySelector('button.button-submit') === null, 'No Apply button is offered');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const [name, document, incomplete] of [
    ['complete', { Complete: true, SkippedUsers: [] }, false],
    ['partial', { Complete: false, SkippedUsers: [{ UserName: 'Anna', Reason: 'Unsupported schema' }] }, true],
    ['camelCase partial', { complete: false, skippedUsers: [{ userName: 'Ben', reason: 'Unreadable file' }] }, true],
    ['skipped accounts without complete flag', { SkippedUsers: [{ UserName: 'Cleo', Reason: 'Storage unavailable' }] }, true]
]) test(`export ${name} downloads readable data and accurately warns about completeness`, async () => {
    const r = await env();
    try {
        let downloads = 0; r.ctx.URL = { createObjectURL() { downloads++; return 'blob:private-test'; }, revokeObjectURL() {} };
        r.a.api.blob = async (url, query) => {
            assert.equal(url, 'Babelfin/Admin/Export'); assert.equal(query.allowPartial, true);
            return { name: 'private-backup.json', blob: { text: async () => JSON.stringify(document) } };
        };
        const target = host(r); r.a.backup(target); target.querySelector('button').click(); await settle();
        assert.equal(downloads, 1, 'Readable accounts are still downloadable');
        const warnings = target.querySelectorAll('[role="alert"]').filter(n => !n.classList.contains('hide'));
        assert.equal(warnings.length > 0, incomplete);
        if (incomplete) {
            const text = warnings.map(n => n.textContent).join(' ');
            assert.match(text, /incomplete/i); const skipped = document.SkippedUsers || document.skippedUsers;
            assert.ok(text.includes(skipped[0].UserName || skipped[0].userName)); assert.ok(text.includes(skipped[0].Reason || skipped[0].reason));
        }
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('invalid export content fails visibly and cannot download a misleading backup', async () => {
    const r = await env();
    try {
        let downloads = 0; r.ctx.URL = { createObjectURL() { downloads++; return 'blob:private-test'; }, revokeObjectURL() {} };
        r.a.api.blob = async () => ({ name: 'invalid.json', blob: { text: async () => '<html>sign in</html>' } });
        const target = host(r); r.a.backup(target); target.querySelector('button').click(); await settle();
        assert.equal(downloads, 0); assert.ok(target.querySelector('.bf-alert-error').textContent.includes("isn't a Babelfin settings file"));
        assert.equal(target.querySelector('button').disabled, false);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('partial reset names skipped and failed accounts with reasons and warns its backup is incomplete', async () => {
    const r = await env();
    try {
        let writes = 0;
        r.a.api.post = async (url, body, query) => {
            assert.equal(url, 'Babelfin/Admin/ResetAll'); assert.equal(query.allowPartial, true); writes++;
            return { BackupPath: '/private/backups/reset.json', FailedNames: ['Anna', 'Ben'], SkippedUsers: [{ UserName: 'Anna', Reason: 'Unreadable original file' }], FailedUsers: [{ UserName: 'Ben', Reason: 'Remember flags could not be reset' }] };
        };
        const target = host(r); r.a.reset(target); target.querySelector('button').click(); await settle(3);
        r.document.querySelector('.bfui-dialog').querySelectorAll('button')[1].click(); await settle(40);
        assert.equal(writes, 1); assert.ok(target.querySelector('.bf-alert-ok') === null);
        assert.match(target.textContent, /incomplete/i);
        for (const text of ['Anna', 'Unreadable original file', 'Ben', 'Remember flags could not be reset', '/private/backups/reset.json']) assert.ok(target.textContent.includes(text), text);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('People editor sends only its changed field and retains concurrent hidden settings and overrides', async () => {
    const r = await env();
    try {
        const id = r.a.state().userList[0].UserId;
        r.page.querySelector('#bfPeople tbody tr button').click(); await settle(40);
        const editor = r.page.querySelector('.bf-editor-row'); assert.ok(editor);
        const enabled = editor.querySelector('input[type="checkbox"]'); enabled.click();
        const put = r.a.api.put; let query;
        r.a.api.put = (url, body, params) => {
            query = params; const doc = r.server.state.docs[id];
            doc.Everyday.AvoidCommentary = false; doc.Locks.FutureLock = 'keep'; doc.FutureHidden = 17;
            return put(url, body, params);
        };
        editor.querySelector('button.button-submit').click(); await settle(40);
        assert.equal(query.keepOverrides, true); assert.equal(query.fields, 'enabled');
        const doc = r.server.state.docs[id]; assert.equal(doc.Enabled, false); assert.equal(doc.Everyday.AvoidCommentary, false);
        assert.equal(doc.Locks.FutureLock, 'keep'); assert.equal(doc.FutureHidden, 17); assert.equal(doc.Overrides.length, 2);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('field paths are real changes, leaf-scoped objects and atomic arrays', async () => {
    const r = await env();
    try {
        const before = { Everyday: { AudioChain: null, UnlabelledAudio: 'Unknown' }, Locks: { WhenAudioUnderstood: 'Off' }, Seed: { Confirmed: false }, Keywords: { PartialWords: ['old'], FullWords: ['full'] }, AppModes: [{ Client: 'Web', Mode: 'Trust' }] };
        const after = copy(before); after.Everyday.AudioChain = [{ Kind: 'OriginalLanguage' }]; after.Locks.WhenAudioUnderstood = null;
        after.Seed.Confirmed = true; after.Keywords.PartialWords = []; after.AppModes[0].Mode = 'ReplaceOnAutoplay';
        assert.deepEqual(copy(r.a.fields(before, after)).sort(), ['appModes', 'everyday.audioChain', 'keywords.partialWords', 'locks.whenAudioUnderstood', 'seed.confirmed'].sort());
        assert.deepEqual(copy(r.a.fields(before, copy(before))), []);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('stable library leaf saves preserve concurrent sibling fields and other rows', async () => {
    const r = await env();
    try {
        const id = r.a.state().config.Libraries[0].LibraryId;
        r.a.state().pending.row = cfg => { cfg.Libraries.find(row => row.LibraryId === id).Settings.AudioChain = [{ Kind: 'OriginalLanguage' }]; };
        const put = r.a.api.put; let fields;
        r.a.api.put = (url, body, query) => {
            fields = query.fields; const row = r.server.state.config.Libraries.find(row => row.LibraryId === id);
            row.Settings.UnlabelledAudio = 'SpecificLanguage'; row.Settings.UnlabelledAudioLanguage = 'de';
            r.server.state.config.Libraries.push({ LibraryId: 'f'.repeat(32), Name: 'New library', Settings: { FutureField: 21 } });
            return put(url, body, query);
        };
        await r.a.save(); assert.equal(fields, `libraries.${id}.settings.audioChain`);
        const row = r.server.state.config.Libraries.find(row => row.LibraryId === id);
        assert.deepEqual(row.Settings.AudioChain, [{ Kind: 'OriginalLanguage' }]);
        assert.equal(row.Settings.UnlabelledAudio, 'SpecificLanguage'); assert.equal(row.Settings.UnlabelledAudioLanguage, 'de');
        assert.equal(r.server.state.config.Libraries.find(row => row.LibraryId === 'f'.repeat(32)).Settings.FutureField, 21);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('library leaf diff normalizes IDs, ignores row order/name metadata and clears only removed leaves', async () => {
    const r = await env();
    try {
        const id = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', other = 'b'.repeat(32);
        const before = { Libraries: [{ LibraryId: id, Name: 'Old name', OriginalAudio: 'Yes', Settings: { AudioChain: [{ Kind: 'OriginalLanguage' }], Understood: ['en'] } }, { LibraryId: other, Settings: {} }] };
        const unchanged = { Libraries: [{ LibraryId: other, Settings: {} }, { LibraryId: 'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', Name: 'New name', OriginalAudio: 'Yes', Settings: copy(before.Libraries[0].Settings) }] };
        assert.deepEqual(copy(r.a.fields(before, unchanged)), []);
        const removed = { Libraries: [{ LibraryId: other, Settings: {} }] };
        assert.deepEqual(copy(r.a.fields(before, removed)).sort(), [`libraries.${id}.settings.audioChain`, `libraries.${id}.settings.understood`, `libraries.${id}.originalAudio`].sort());
        assert.equal(r.a.fields(before, removed).includes('libraries'), false, 'No broad/leaf overlap');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('hiding during a leave dialog closes it and detaches both modal and static page handlers', async () => {
    const r = await env();
    try {
        dirty(r); const leaving = r.ctx.Emby.Page.back(); await settle(3);
        assert.ok(r.document.querySelector('.bfui-dialog'));
        const enabled = r.page.querySelector('#bfEnabled'); assert.equal(enabled._listeners.change.length, 1);
        r.ctx.BabelfinDashboard.hide(); await leaving;
        assert.equal(r.document.querySelector('.bfui-dialog'), null);
        assert.equal((r.document._captureListeners.keydown || []).length, 0);
        assert.equal(enabled._listeners.change.length, 0); assert.deepEqual(r.ctx.routeCalls, []);
        await reenter(r); assert.equal(enabled._listeners.change.length, 1);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('native traversal Cancel stays, Discard resumes destination, and the navigation handler detaches', async () => {
    const r = await env();
    try {
        dirty(r);
        const event = () => new Event('navigate', { cancelable: true, navigationType: 'traverse', destination: { key: 'previous', url: '#/home' } });
        let key = event(); r.ctx.navigation.listeners.slice().forEach(fn => fn(key));
        assert.equal(key.defaultPrevented, true); await answer(r, false); assert.deepEqual(r.ctx.routeCalls, []);
        key = event(); r.ctx.navigation.listeners.slice().forEach(fn => fn(key)); await answer(r, true);
        assert.deepEqual(r.ctx.routeCalls, [['traverse', 'previous']]);
        r.ctx.BabelfinDashboard.hide(); assert.equal(r.ctx.navigation.listeners.length, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('a refused server reset is visible even when people succeeded and FailedNames is empty', async () => {
    const r = await env();
    try {
        r.a.api.post = async () => ({ BackupPath: '/private/recovery.json', People: 1, FailedNames: [], AdminReset: false, AdminResetReason: 'Server settings changed after the recovery backup.' });
        const target = host(r); r.a.reset(target); target.querySelector('button').click(); await settle(3);
        r.document.querySelector('.bfui-dialog').querySelectorAll('button')[1].click(); await settle(40);
        assert.ok(target.querySelector('.bf-alert-ok') === null, 'The UI must not claim all settings were reset');
        assert.ok(target.querySelector('.bf-reset-done .bf-alert-error').textContent.includes('Server settings stay as they are.'));
        assert.ok(target.querySelector('.bf-reset-done .bf-alert-error').textContent.includes('Server settings changed after the recovery backup.'));
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

function legacyHistory(ctx) {
    const origin = 'http://srv/jf/index.html', route = '#/configurationpage?name=Babelfin';
    const entries = [{ url: origin + '#/older', state: { idx: 0, value: 'older' } }, { url: origin + '#/home', state: { idx: 1, value: 'home' } }, { url: origin + route, state: { idx: 2, value: 'admin' } }];
    let cursor = 2;
    const captures = new Map();
    ctx.addEventListener = function (type, fn, capture) { (this.windowListeners[type] = this.windowListeners[type] || []).push(fn); captures.set(fn, !!capture); };
    ctx.removeEventListener = function (type, fn) { const list = this.windowListeners[type] || []; const i = list.indexOf(fn); if (i >= 0) list.splice(i, 1); captures.delete(fn); };
    function emit(event) {
        const listeners = (ctx.windowListeners[event.type] || []).slice();
        for (const fn of listeners) { fn(event); if (event._immediate) break; }
    }
    function move(next) {
        if (next < 0 || next >= entries.length || next === cursor) return;
        const delta = next - cursor;
        setTimeout(() => {
            const destination = cursor + delta;
            if (destination < 0 || destination >= entries.length) return;
            const oldURL = entries[cursor].url; cursor = destination;
            const newURL = entries[cursor].url, state = copy(entries[cursor].state);
            emit(new Event('popstate', { state })); if (oldURL !== newURL) emit(new Event('hashchange', { oldURL, newURL }));
        }, 0);
    }
    ctx.location = { get href() { return entries[cursor].url; }, set href(value) { const url = new URL(value, entries[cursor].url).href; const oldURL = entries[cursor].url; entries.splice(cursor + 1); entries.push({ url, state: null }); cursor++; emit(new Event('popstate', { state: null })); setTimeout(() => { emit(new Event('hashchange', { oldURL, newURL: url })); }, 0); }, get hash() { return new URL(this.href).hash; }, set hash(value) { this.href = value; } };
    ctx.history = {
        get length() { return entries.length; }, get state() { return entries[cursor].state; },
        go(delta) { move(cursor + Number(delta)); }, back() { move(cursor - 1); }, forward() { move(cursor + 1); },
        replaceState(state, title, url) { entries[cursor] = { state: copy(state), url: url ? new URL(url, entries[cursor].url).href : entries[cursor].url }; },
        pushState(state, title, url) { entries.splice(cursor + 1); entries.push({ state: copy(state), url: new URL(url, entries[cursor].url).href }); cursor++; }
    };
    ctx.legacy = { snapshot: () => ({ cursor, entries: copy(entries) }), back: () => move(cursor - 1), route, emit, clearPreviousState: () => { entries[1].state = null; } };
    ctx.hostNavigation = [];
    ctx.addEventListener('popstate', () => { ctx.hostNavigation.push(ctx.location.href); if (ctx.location.hash !== route) ctx.BabelfinDashboard.hide(ctx.document.querySelector('#babelfinConfigPage')); });
    ctx.addEventListener('hashchange', () => { ctx.hostNavigation.push(ctx.location.href); if (ctx.location.hash !== route) ctx.BabelfinDashboard.hide(ctx.document.querySelector('#babelfinConfigPage')); });
}

test('without Navigation API, native browser Back Cancel preserves the history cursor, entries and draft', async () => {
    const r = await env({ legacy: true });
    try {
        dirty(r); const before = r.ctx.legacy.snapshot(); r.ctx.legacy.back(); await settle();
        assert.ok(r.document.querySelector('.bfui-dialog'), 'Native Back must prompt before the host hides the page');
        assert.equal(r.ctx.location.hash, r.ctx.legacy.route); assert.equal(r.a.state().shown, true, 'An earlier host pagehide cannot discard the draft');
        await answer(r, false); assert.deepEqual(r.ctx.legacy.snapshot(), before); assert.equal(typeof r.a.state().pending.Enabled, 'function');
        r.ctx.legacy.back(); await settle(); await answer(r, true); await settle();
        assert.equal(r.ctx.location.hash, '#/home'); assert.equal(r.ctx.legacy.snapshot().cursor, 1);
        assert.equal(r.server.state.puts.length, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('without Navigation API, repeated direct hash Cancel preserves Back history and Discard chooses the first destination', async () => {
    const r = await env({ legacy: true });
    try {
        dirty(r); const original = r.ctx.legacy.snapshot();
        for (let i = 0; i < 2; i++) {
            r.ctx.location.hash = '#/dashboard'; await settle();
            assert.ok(r.document.querySelector('.bfui-dialog'), 'Direct hash changes must prompt');
            assert.equal(r.ctx.location.hash, r.ctx.legacy.route); await answer(r, false);
            const now = r.ctx.legacy.snapshot(); assert.equal(now.cursor, original.cursor);
            assert.deepEqual(now.entries.slice(0, original.entries.length), original.entries, 'No replacement/extra admin entries on Cancel');
            assert.equal(now.entries.length, original.entries.length + 1, 'Only the requested hash entry exists, with no added repair entries');
        }
        r.ctx.location.hash = '#/home'; await settle(); r.ctx.location.hash = '#/dashboard'; await settle();
        assert.equal(r.document.querySelectorAll('.bfui-dialog').length, 1); await answer(r, true); await settle();
        assert.equal(r.ctx.location.hash, '#/home'); assert.equal(r.server.state.puts.length, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('without Navigation API, history methods and listeners detach on hide and attach once on re-entry', async () => {
    const r = await env({ legacy: true });
    try {
        dirty(r); const leaving = r.ctx.history.back(); await settle(); await answer(r, false); await leaving;
        assert.equal(r.ctx.location.hash, r.ctx.legacy.route);
        r.ctx.BabelfinDashboard.hide();
        assert.equal((r.ctx.windowListeners.popstate || []).length, 1, 'Only the host listener remains');
        assert.equal((r.ctx.windowListeners.hashchange || []).length, 1);
        await reenter(r); assert.equal((r.ctx.windowListeners.popstate || []).length, 2);
        assert.equal((r.ctx.windowListeners.hashchange || []).length, 2);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const discard of [false, true]) test(`multiple Navigation API destinations ${discard ? 'Discard resumes first only' : 'Cancel resumes none'}`, async () => {
    const r = await env();
    try {
        dirty(r);
        for (const url of ['http://srv/#/home', 'http://srv/#/dashboard']) {
            const event = new Event('navigate', { cancelable: true, navigationType: 'push', destination: { url } });
            r.ctx.navigation.listeners.slice().forEach(fn => fn(event)); assert.equal(event.defaultPrevented, true);
        }
        await settle(3); assert.equal(r.document.querySelectorAll('.bfui-dialog').length, 1); await answer(r, discard);
        assert.deepEqual(r.ctx.routeCalls, discard ? [['navigate', 'http://srv/#/home']] : []);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const value of [['fr'], [], null]) test(`template language leaf preserves hidden concurrent settings (${JSON.stringify(value)})`, async () => {
    const r = await env();
    try {
        r.a.state().config.NewUserTemplate = r.server.state.config.NewUserTemplate = { Enabled: true, Everyday: { Understood: ['en'], AvoidCommentary: true }, Locks: { WhenAudioUnderstood: 'Off' } };
        r.a.state().pending.template = cfg => { cfg.NewUserTemplate = value === null ? null : { ...cfg.NewUserTemplate, Everyday: { ...cfg.NewUserTemplate.Everyday, Understood: value } }; };
        const put = r.a.api.put; let fields;
        r.a.api.put = (url, body, query) => {
            fields = query.fields; r.server.state.config.NewUserTemplate.Everyday.AvoidCommentary = false;
            r.server.state.config.NewUserTemplate.FutureHidden = 'keep';
            return put(url, body, query);
        };
        await r.a.save(); assert.equal(fields, 'newUserTemplate.everyday.understood');
        const template = r.server.state.config.NewUserTemplate;
        assert.deepEqual(template.Everyday.Understood, value); assert.equal(template.Everyday.AvoidCommentary, false);
        assert.equal(template.FutureHidden, 'keep'); assert.deepEqual(template.Locks, { WhenAudioUnderstood: 'Off' });
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('legacy Back Cancel never rewrites a previous entry whose state is null', async () => {
    const r = await env({ legacy: true });
    try {
        r.ctx.legacy.clearPreviousState(); dirty(r); const before = r.ctx.legacy.snapshot();
        r.ctx.legacy.back(); await settle(); await answer(r, false);
        assert.deepEqual(r.ctx.legacy.snapshot(), before, 'URL, cursor and every history state survive Cancel byte-for-byte');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('coalesced legacy hash setters restore once and keep the first destination', async () => {
    const r = await env({ legacy: true });
    try {
        dirty(r); r.ctx.location.hash = '#/home'; r.ctx.location.hash = '#/dashboard'; await settle();
        assert.equal(r.ctx.location.hash, r.ctx.legacy.route); assert.equal(r.document.querySelectorAll('.bfui-dialog').length, 1);
        await answer(r, true); await settle(); assert.equal(r.ctx.location.hash, '#/home'); assert.equal(r.server.state.puts.length, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('legacy Cancel keeps the draft and later hash prompt when Jellyfin rebuilds the admin view during rollback', async () => {
    const r = await env({ legacy: true });
    try {
        dirty(r); const state = r.a.state(), oldPage = r.page, original = r.ctx.legacy.snapshot();
        r.ctx.location.hash = '#/home'; await settle();
        assert.ok(r.document.querySelector('.bfui-dialog'));
        // Jellyfin's view manager can rebuild the view before its first leave animation finishes.
        const replacement = r.document.createElement('div'); replacement.id = oldPage.id; replacement.innerHTML = oldPage.innerHTML;
        oldPage.remove(); r.document.body.appendChild(replacement);
        r.ctx.BabelfinDashboard.show(replacement); r.page = replacement;
        assert.equal(r.a.state() === state, true, 'Rollback must retain the same draft/guard, not reload it from the server');
        assert.equal(replacement.querySelector('#bfEnabled').checked, false);
        assert.equal(typeof state.pending.Enabled, 'function'); assert.ok(r.document.querySelector('.bfui-dialog'));
        r.ctx.BabelfinDashboard.hide(oldPage);
        assert.equal(state.shown, true, 'Delayed pagehide belongs to the removed view');
        await answer(r, false);
        const length = r.ctx.history.length;
        assert.equal(r.ctx.legacy.snapshot().cursor, original.cursor);
        assert.equal(length, original.entries.length + 1, 'Cancel retains the forward home entry');
        r.ctx.location.hash = '#/home'; await settle();
        assert.equal(r.ctx.history.length, length, 'The second hash truncates/replaces the forward entry, so length does not grow');
        assert.ok(r.document.querySelector('.bfui-dialog'), 'The pending draft must still prompt on the second hash');
        await answer(r, true); await settle();
        assert.equal(r.ctx.location.hash, '#/home'); assert.equal(r.server.state.puts.length, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('late host pagehide at the restored legacy URL cannot close the pending confirmation', async () => {
    const r = await env({ legacy: true });
    try {
        dirty(r); r.ctx.location.hash = '#/home'; await settle();
        assert.ok(r.document.querySelector('.bfui-dialog'));
        r.ctx.BabelfinDashboard.hide(r.page);
        assert.equal(r.a.state().shown, true); assert.ok(r.document.querySelector('.bfui-dialog'));
        await answer(r, false); assert.equal(typeof r.a.state().pending.Enabled, 'function');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

async function isolatedPerson(r, doc) {
    const target = host(r), id = r.a.state().userList[0].UserId;
    r.a.state().person = { id, dirty: false };
    doc = Object.assign({ UserId: id, Enabled: true, Everyday: {}, Libraries: [], Locks: {} }, doc);
    r.a.person(target, { UserId: id, Name: 'Example' }, copy(doc), null,
        (doc.Libraries || []).map(row => ({ Id: row.LibraryId, Name: row.Name || 'Example library' })), { rows: [] });
    await settle(3);
    return { target, id, doc, enabled: target.querySelector('input[type="checkbox"]'), save: target.querySelector('button.button-submit') };
}

test('person Save is single flight and preserves a later reversal for the next save', async () => {
    const r = await env();
    try {
        const p = await isolatedPerson(r), held = defer(); let writes = 0;
        r.a.api.put = async (url, body, query) => {
            writes++; assert.equal(query.keepOverrides, true); assert.equal(query.fields, 'enabled');
            if (writes === 1) { await held.promise; return Object.assign(copy(body), { FutureHidden: 91 }); }
            assert.equal(body.Enabled, true); assert.equal(body.FutureHidden, 91);
            return body;
        };
        p.enabled.click(); p.save.click(); await settle(3);
        assert.equal(p.save.disabled, true);
        p.enabled.click(); // Back to the original value while false is being submitted.
        assert.equal(p.save.disabled, true, 'Typing cannot unlock a duplicate Save');
        p.save.dispatchEvent(new Event('click')); await settle(3); assert.equal(writes, 1);
        held.resolve(); await settle(30);
        assert.equal(p.enabled.checked, true); assert.equal(r.a.state().person.dirty, true);
        assert.equal(p.save.disabled, false); p.save.click(); await settle(30);
        assert.equal(writes, 2); assert.equal(r.a.state().person.dirty, false); assert.equal(p.save.disabled, true);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('person save rebases later library edits by identity and keeps fresh hidden leaves and rows', async () => {
    const r = await env();
    try {
        const id = 'a'.repeat(32), other = 'b'.repeat(32), added = 'c'.repeat(32);
        const p = await isolatedPerson(r, { PerLibrary: true, Libraries: [
            { LibraryId: id, Name: 'First', Settings: { AudioChain: [{ Kind: 'Language', Language: 'en' }] } },
            { LibraryId: other, Name: 'Second', Settings: {} }
        ] });
        const held = defer(); let writes = 0; const calls = [], activeEditor = r.a.state().person;
        r.a.api.put = async (url, body, query) => {
            calls.push(copy({body, query})); writes++;
            if (writes === 1) {
                assert.equal(query.fields, 'enabled'); await held.promise;
                const saved = copy(body); saved.Everyday.FutureHidden = 'fresh';
                saved.Libraries[0].Settings.FutureHidden = 'fresh sibling'; saved.Libraries.reverse();
                saved.Libraries.push({ LibraryId: added, Settings: { FutureHidden: 'new row' } }); return saved;
            }
            assert.equal(query.fields, `libraries.${id}.settings.audioChain`);
            assert.equal(body.Everyday.FutureHidden, 'fresh');
            assert.equal(body.Libraries.find(row => row.LibraryId === id).Settings.FutureHidden, 'fresh sibling');
            assert.equal(body.Libraries.find(row => row.LibraryId === added).Settings.FutureHidden, 'new row');
            assert.equal(body.Libraries.find(row => row.LibraryId === other).Settings.AudioChain, undefined);
            return body;
        };
        p.enabled.click(); p.save.click(); await settle(3);
        const select = p.target.querySelector('.bfui-table-audio select');
        select.value = 'original'; select.dispatchEvent(new Event('change')); held.resolve(); await settle(30);
        assert.equal(select.value, 'original'); assert.equal(p.save.disabled, false);
        assert.ok(r.a.state().person === activeEditor, 'Success must retain the live editor and its later draft');
        p.save.click(); await settle(30); assert.equal(writes, 2); assert.equal(r.a.state().person.dirty, false);
        assert.equal(calls[0].query.fields, 'enabled'); assert.equal(calls[0].query.keepOverrides, true);
        const saved = calls[1]; assert.equal(saved.query.keepOverrides, true); assert.equal(saved.query.fields, `libraries.${id}.settings.audioChain`);
        assert.equal(saved.body.Everyday.FutureHidden, 'fresh');
        assert.equal(saved.body.Libraries.find(row => row.LibraryId === id).Settings.FutureHidden, 'fresh sibling');
        assert.equal(saved.body.Libraries.find(row => row.LibraryId === added).Settings.FutureHidden, 'new row');
        assert.equal(saved.body.Libraries.find(row => row.LibraryId === other).Settings.AudioChain, undefined);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const status of [401, 409, 503]) test(`person save failure ${status} keeps later inputs and releases busy state`, async () => {
    const r = await env();
    try {
        const p = await isolatedPerson(r), held = defer();
        r.a.api.put = () => held.promise; p.enabled.click(); p.save.click(); await settle(3);
        p.enabled.click(); held.reject({ status }); await settle(20);
        assert.equal(p.enabled.checked, true); assert.equal(p.save.classList.contains('bf-busy'), false);
        assert.equal(p.save.disabled, true, 'Reverting all changes leaves nothing to save');
        assert.equal(r.a.state().person.dirty, false); assert.match(p.target.querySelector('.bf-alert-error').textContent, new RegExp(String(status)));
        p.enabled.click(); assert.equal(p.save.disabled, false);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const success of [false, true]) test(`mutation refresh releases an obsolete page Save before its late ${success ? 'success' : 'failure'}`, async () => {
    const r = await env();
    try {
        dirty(r); const held = defer(); let writes = 0;
        const put = r.a.api.put; r.a.api.put = (...args) => ++writes === 1 ? held.promise : put(...args);
        const saving = r.a.save(); await settle(4);
        await r.a.refresh(host(r));
        assert.equal(r.a.state().saving, false); assert.equal(r.page.querySelector('#bfSave').disabled, false);
        dirty(r); const newer = r.a.save(); await newer;
        assert.equal(writes, 2, 'A new draft can save after Reset/Import invalidated the old operation');
        if (success) held.resolve({ Enabled: false, OldResult: true }); else held.reject({ status: 409 });
        await saving; assert.equal(r.a.state().config.OldResult, undefined); assert.equal(r.a.state().saving, false);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const rows of [
    [{ LibraryId: 'corrupt', Settings: { AudioChain: null } }],
    [{ LibraryId: 'a'.repeat(16) + '--' + 'a'.repeat(16), Settings: {} }],
    [{ LibraryId: 'a'.repeat(32), Settings: {} }, { LibraryId: 'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', Settings: {} }]
]) test(`invalid library identity fails closed and keeps person Save usable (${rows.length} rows)`, async () => {
    const r = await env();
    try {
        const p = await isolatedPerson(r, { Libraries: rows }), put = r.a.api.put; let writes = 0;
        r.a.api.put = (...args) => { writes++; return put(...args); };
        // Changing library settings forces field-intent validation, including duplicate normalized IDs.
        const mode = p.target.querySelector('[data-mode="each"]'); mode.click();
        const select = p.target.querySelector('.bfui-table-audio select');
        select.value = 'original'; select.dispatchEvent(new Event('change'));
        assert.doesNotThrow(() => p.save.click()); await settle(20);
        assert.equal(writes, 0); assert.equal(p.save.disabled, false); assert.equal(p.save.classList.contains('bf-busy'), false);
        assert.equal(r.a.state().person.dirty, true);
        assert.match(p.target.querySelector('.bf-alert-error').textContent, /library|common.invalidLibrary/i);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('document capture guards the real sidebar router before it can tear down the editor', async () => {
    const r = await env();
    try {
        r.ctx.location = { href: 'http://srv/jf/web/#/configurationpage?name=Babelfin' };
        const link = r.document.createElement('a'), child = r.document.createElement('span');
        link.href = '#/dashboard'; link.appendChild(child); r.document.body.appendChild(link);
        const routes = [];
        // Like Jellyfin's delegated router: it hides the old view before the browser navigate event.
        function router(event) { if (link.contains(event.target)) { routes.push(link.href); r.ctx.BabelfinDashboard.hide(r.page); } }
        r.document.addEventListener('click', router); dirty(r);
        child.dispatchEvent(new Event('click', { bubbles: true, button: 0 })); await answer(r, false);
        assert.deepEqual(routes, []); assert.equal(r.a.state().shown, true); assert.equal(r.page.querySelector('#bfEnabled').checked, false);
        child.dispatchEvent(new Event('click', { bubbles: true, button: 0 })); await answer(r, true);
        assert.deepEqual(routes, ['#/dashboard']); assert.equal(r.a.state().shown, false);
        await reenter(r); dirty(r); child.dispatchEvent(new Event('click', { bubbles: true, button: 0 })); await answer(r, false);
        assert.deepEqual(routes, ['#/dashboard']); r.document.removeEventListener('click', router);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('sidebar guard keeps the first destination and permits modified, download and local control clicks', async () => {
    const r = await env();
    try {
        r.ctx.location = { href: 'http://srv/jf/web/#/configurationpage?name=Babelfin' }; dirty(r);
        const make = href => { const a = r.document.createElement('a'); a.href = href; r.document.body.appendChild(a); return a; };
        const first = make('#/dashboard'), second = make('#/home'); let firstClicks = 0, secondClicks = 0;
        first.addEventListener('click', () => firstClicks++); second.addEventListener('click', () => secondClicks++);
        first.click(); second.click(); await answer(r, true); assert.equal(firstClicks, 1); assert.equal(secondClicks, 0);
        await reenter(r); dirty(r);
        for (const [a, init] of [[make('#'), {}], [make('#/home'), { ctrlKey: true }], [make('#/home'), { button: 1 }], [make('https://example.org/'), {}]]) {
            const e = new Event('click', Object.assign({ bubbles: true }, init)); a.dispatchEvent(e); assert.equal(e.defaultPrevented, false);
        }
        const download = make('#/download'); download.setAttribute('download', 'backup');
        const e = new Event('click', { bubbles: true }); download.dispatchEvent(e); assert.equal(e.defaultPrevented, false);
        assert.equal(r.document.querySelector('.bfui-dialog'), null);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const result of [
    { CanApply: false, Admin: { WillChange: true } },
    { canApply: false, users: [{ action: 'update' }], warnings: ['Validation failed'] },
    { StorageBlocked: true, Users: [{ Action: 'create' }] },
    { storageBlocked: true, Admin: { WillChange: true } }
]) test('import eligibility refuses blocked plans even when rows appear changeable: ' + JSON.stringify(result), async () => {
    const r = await env();
    try {
        const target = host(r); r.a.preview(target, result, {}, host(r));
        assert.ok(target.querySelector('button.button-submit') === null, 'No Apply button is offered');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const envelope of ["Preview", "preview", "Import", "import"]) for (const applied of [false, true]) test(`partial import conflict ${envelope} displays reported applied=${applied} without resubmitting`, async () => {
    const r = await env();
    try {
        const target = host(r), box = host(r); let writes = 0, reads = 0; const get = r.a.api.get;
        r.a.api.get = (url, ...args) => { if (url === 'Babelfin/Admin/Settings') reads++; return get(url, ...args); };
        const report = { Applied: applied, CanApply: false, Admin: { WillChange: false }, Users: [{ UserName: 'Example', Action: 'skip', Reason: 'Storage changed' }], Warnings: ['Inspect blocked account'] };
        r.a.api.post = async () => { writes++; throw { status: 409, problem: { Message: applied ? 'Some settings were imported' : 'Nothing was imported', Applied: applied, [envelope]: report } }; };
        r.a.preview(target, { CanApply: true, Users: [{ Action: 'update' }] }, {}, box);
        target.querySelector('button.button-submit').click(); await settle(3);
        r.document.querySelector('.bfui-dialog').querySelectorAll('button')[1].click(); await settle(30);
        assert.equal(writes, 1); assert.equal(reads, applied ? 1 : 0);
        assert.match(box.textContent, applied ? /Some settings were imported/ : /Nothing was imported/);
        assert.match(target.textContent, /Storage changed/); assert.ok(target.querySelector('button.button-submit') === null, 'No Apply button is offered');
        if (applied) assert.equal(target.querySelector('h5').textContent, r.a.state().t('backup.appliedTitle'));
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('Reset/Import invalidation during the Save reread prevents an obsolete PUT', async () => {
    const r = await env();
    try {
        dirty(r); const held = defer(), get = r.a.api.get; let reads = 0;
        r.a.api.get = (url, ...args) => url === 'Babelfin/Admin/Settings' && ++reads === 1 ? held.promise : get(url, ...args);
        const saving = r.a.save(); await settle(3); await r.a.refresh(host(r));
        dirty(r); await r.a.save(); assert.equal(r.server.state.puts.length, 1);
        held.resolve(copy(r.server.state.config)); await saving;
        assert.equal(r.server.state.puts.length, 1); assert.equal(r.a.state().saving, false);
        assert.equal(r.page.querySelector('#bfSave').disabled, false);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const success of [false, true]) test(`Reset/Import invalidates late person save ${success ? 'success' : 'failure'} without replacing the next editor`, async () => {
    const r = await env();
    try {
        const p = await isolatedPerson(r), held = defer(); r.a.api.put = () => held.promise;
        p.enabled.click(); p.save.click(); await settle(3); await r.a.refresh(host(r));
        const next = await isolatedPerson(r); next.enabled.click(); const active = r.a.state().person;
        if (success) held.resolve(Object.assign(copy(p.doc), { Enabled: false })); else held.reject({ status: 409 });
        await settle(20);
        assert.ok(r.a.state().person === active); assert.equal(active.dirty, true);
        assert.equal(next.enabled.checked, false); assert.equal(next.save.disabled, false);
        assert.equal(next.target.querySelector('.bf-alert-error').textContent, '');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('invalid admin library leaf intent fails visibly without losing the draft or locking Save', async () => {
    const r = await env();
    try {
        r.a.state().pending.library = cfg => { cfg.Libraries.push({ LibraryId: 'corrupt', Settings: { Understood: ['en'] } }); };
        await r.a.save();
        assert.equal(r.server.state.puts.length, 0); assert.equal(r.a.state().saving, false);
        assert.equal(r.page.querySelector('#bfSave').disabled, false);
        assert.equal(typeof r.a.state().pending.library, 'function');
        assert.match(r.page.querySelector('#bfSaveError').textContent, /library|common.invalidLibrary/i);
        const leave = r.ctx.Emby.Page.back(); await answer(r, false); await leave;
        assert.equal(typeof r.a.state().pending.library, 'function');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('an in-flight person save stays protected when later typing returns to its old baseline', async () => {
    const r = await env(); const held = defer();
    try {
        const p = await isolatedPerson(r); r.a.api.put = () => held.promise;
        p.enabled.click(); p.save.click(); await settle(3); p.enabled.click();
        const leaving = r.ctx.Emby.Page.back(); await answer(r, false); await leaving;
        assert.deepEqual(r.ctx.routeCalls, []); assert.equal(p.enabled.checked, true);
        held.resolve(Object.assign(copy(p.doc), { Enabled: false })); await settle(20);
        assert.equal(r.a.state().person.dirty, true); assert.equal(p.save.disabled, false);
    } finally { held.resolve(); r.ctx.BabelfinDashboard.hide(); }
});

test('person Save preserves uncommitted language search text, focus and picker handlers', async () => {
    const r = await env(); const held = defer();
    try {
        r.page.querySelector('#bfPeople tbody tr button').click(); await settle(35);
        const editor = r.page.querySelector('.bf-editor-row'), active = r.a.state().person;
        const enabled = editor.querySelector('input[type="checkbox"]'), save = editor.querySelector('button.button-submit');
        const picker = editor.querySelector('input[role="combobox"]'); let submitted;
        r.a.api.put = (url, body) => { submitted = copy(body); return held.promise; };
        enabled.click(); save.click(); await settle(3);
        picker.value = 'German'; picker.focus(); picker.dispatchEvent(new Event('input', { bubbles: true }));
        held.resolve(submitted); await settle(30);
        assert.ok(r.a.state().person === active); assert.ok(editor.querySelector('input[role="combobox"]') === picker);
        assert.equal(picker.value, 'German'); assert.ok(r.document.activeElement === picker);
        const option = editor.querySelector('[role="option"]'); assert.ok(option, 'The live language result remains available');
        option.click(); await settle(3);
        assert.equal(r.a.state().person.dirty, true); assert.equal(save.disabled, false, 'The retained picker still changes the working document');
    } finally { held.resolve(); r.ctx.BabelfinDashboard.hide(); }
});

function editorGenre(name, id = name.toLowerCase()) {
    return { Id: id, Name: name, Enabled: true, Match: 'All', Conditions: [{ Kind: 'Genre', Values: [name], Negate: false, IsRegex: false }], Settings: {} };
}
function personGenreRow(target, name) {
    return target.querySelectorAll('section.bfui-genres tbody tr').find(row => row.querySelector('th').textContent === name);
}

test('person genre dropdown remains live after Save and retains hidden fields and concurrent genre changes', async () => {
    for (const scenario of ['unchanged', 'hidden fields', 'reordered and added', 'assigned id', 'server choice changed']) {
        const r = await env();
        try {
            const p = await isolatedPerson(r, { SmartRules: [editorGenre('Anime', scenario === 'assigned id' ? '' : 'anime'), editorGenre('Drama')] });
            const active = r.a.state().person, held = defer(), writes = [];
            const picker = p.target.querySelector('input[role="combobox"]');
            const genreSelect = personGenreRow(p.target, 'Anime').querySelector('select');
            r.a.api.put = async (url, body, query) => {
                writes.push(copy({ body, query }));
                const saved = copy(body);
                if (writes.length === 1) {
                    await held.promise;
                    if (scenario !== 'unchanged') {
                        saved.FutureHidden = 'fresh top';
                        saved.SmartRules[0].Settings.FutureHidden = 'fresh genre';
                        saved.SmartRules[0].FutureRuleField = { keep: true };
                    }
                    if (scenario === 'reordered and added') saved.SmartRules = [saved.SmartRules[1], editorGenre('Comedy'), saved.SmartRules[0]];
                    if (scenario === 'assigned id') saved.SmartRules[0].Id = 'assigned-anime';
                    if (scenario === 'server choice changed') saved.SmartRules[0].Settings.AudioChain = [{ Kind: 'Language', Language: 'de' }];
                }
                return saved;
            };
            p.enabled.click(); p.save.click(); await settle(3);
            if (scenario === 'assigned id') { genreSelect.value = 'mine'; genreSelect.dispatchEvent(new Event('change')); }
            picker.value = 'German'; picker.focus(); picker.dispatchEvent(new Event('input', { bubbles: true }));
            if (scenario === 'server choice changed') genreSelect.focus();
            held.resolve(); await settle(30);
            assert.equal(writes.length, 1); assert.equal(p.save.disabled, scenario !== 'assigned id');
            assert.ok(r.a.state().person === active, scenario + ': retain editor');
            assert.ok(p.target.querySelector('input[role="combobox"]') === picker);
            assert.equal(picker.value, 'German');
            const select = personGenreRow(p.target, 'Anime').querySelector('select');
            assert.ok(r.document.activeElement === (scenario === 'server choice changed' ? select : picker));
            if (scenario === 'server choice changed') assert.equal(select.value, 'lang:de', 'A returned visible choice is repainted');
            if (scenario === 'assigned id') assert.equal(select.value, 'mine', 'A later edit survives server-assigned rule identity');
            select.value = 'original'; select.dispatchEvent(new Event('change')); await settle(3);
            assert.equal(p.save.disabled, false, scenario + ': changing a genre must enable Save after rebasing');
            if (scenario !== 'reordered and added' && scenario !== 'server choice changed') assert.ok(select === genreSelect, scenario + ': unchanged visible rows retain their controls');
            p.save.click(); await settle(30);
            assert.equal(writes.length, 2); assert.equal(writes[1].query.fields, 'smartRules');
            const saved = writes[1].body, anime = saved.SmartRules.find(rule => rule.Name === 'Anime');
            assert.equal(anime.Settings.AudioChain[0].Kind, 'OriginalLanguage');
            if (scenario !== 'unchanged') {
                assert.equal(saved.FutureHidden, 'fresh top'); assert.equal(anime.Settings.FutureHidden, 'fresh genre');
                assert.deepEqual(anime.FutureRuleField, { keep: true });
            }
            if (scenario === 'reordered and added') assert.deepEqual(saved.SmartRules.map(rule => rule.Name), ['Drama', 'Comedy', 'Anime']);
            if (scenario === 'assigned id') assert.equal(anime.Id, 'assigned-anime');
            assert.equal(p.save.disabled, true); assert.equal(r.a.state().person.dirty, false);
        } finally { r.ctx.BabelfinDashboard.hide(); }
    }
});

test('removing the first person genre after Save removes that rule and preserves concurrent rows and in-flight edits', async () => {
    for (const scenario of ['unchanged', 'concurrent list', 'remove during save', 'concurrent removal']) {
        const r = await env();
        try {
            const p = await isolatedPerson(r, { SmartRules: [editorGenre('Anime'), editorGenre('Drama')] });
            const held = defer(), writes = [];
            r.a.api.put = async (url, body, query) => {
                writes.push(copy({ body, query }));
                const saved = copy(body);
                if (writes.length === 1) {
                    await held.promise;
                    if (scenario !== 'unchanged') {
                        saved.SmartRules[1].Settings.FutureHidden = 'keep sibling';
                        saved.SmartRules = [saved.SmartRules[1], editorGenre('Comedy'), saved.SmartRules[0]];
                        saved.SmartRules.push({ Id: 'complex', Name: 'Complex', Enabled: false, Match: 'Any', Conditions: [], Settings: { FutureHidden: 42 } });
                    }
                    if (scenario === 'concurrent removal') saved.SmartRules = saved.SmartRules.filter(rule => rule.Name !== 'Drama');
                }
                return saved;
            };
            p.enabled.click(); p.save.click(); await settle(3);
            if (scenario === 'remove during save') personGenreRow(p.target, 'Anime').querySelector('[data-action="remove"]').click();
            held.resolve(); await settle(30);
            if (scenario !== 'remove during save') {
                if (scenario === 'concurrent removal') assert.ok(!personGenreRow(p.target, 'Drama'), 'The concurrent removal is reflected in the live editor');
                personGenreRow(p.target, 'Anime').querySelector('[data-action="remove"]').click();
            }
            await settle(3); assert.equal(p.save.disabled, false); p.save.click(); await settle(30);
            assert.equal(writes.length, 2); assert.equal(writes[1].query.fields, 'smartRules');
            const saved = writes[1].body.SmartRules;
            assert.deepEqual(saved.map(rule => rule.Name), scenario === 'unchanged' ? ['Drama'] : scenario === 'concurrent removal' ? ['Comedy', 'Complex'] : ['Drama', 'Comedy', 'Complex']);
            if (scenario !== 'unchanged') {
                const drama = saved.find(rule => rule.Name === 'Drama'); if (drama) assert.equal(drama.Settings.FutureHidden, 'keep sibling');
                assert.equal(saved.find(rule => rule.Id === 'complex').Settings.FutureHidden, 42);
            }
            assert.equal(r.a.state().person.dirty, false);
        } finally { r.ctx.BabelfinDashboard.hide(); }
    }
});

test('admin intent sends page-original values despite its fresh reread', async () => {
    const r = await env();
    try {
        dirty(r); r.server.state.config.FutureValue = 'fresh';
        let submitted; r.a.api.put = async (url, body, query, headers) => { assert.equal(headers, undefined); submitted = copy(body); return body; };
        await r.a.save();
        assert.deepEqual(submitted.ExpectedValues, { enabled: true });
    } finally { r.ctx.BabelfinDashboard.hide(); }
});


test('unchanged damaged and duplicate stored library rows do not block valid library edits', async () => {
    const r = await env();
    try {
        const id = 'a'.repeat(32), other = 'b'.repeat(32);
        const before = { Libraries: [{ LibraryId: '', Settings: {} }, { LibraryId: id, Settings: {} }, { LibraryId: id, Settings: {} }, { LibraryId: other, Settings: {} }] };
        const after = copy(before); after.Libraries[3].Settings.AudioChain = [{ Kind: 'OriginalLanguage' }];
        assert.deepEqual(Array.from(r.a.fields(before, after)), ['libraries.' + other + '.settings.audioChain']);
        after.Libraries[0].Settings.AudioChain = []; assert.throws(() => r.a.fields(before, after));
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('blocked import explains whose settings prevent applying the file', async () => {
    const r = await env();
    try {
        const target = host(r); let writes = 0; r.a.api.post = async () => { writes++; }; r.a.api.put = async () => { writes++; };
        r.a.preview(target, { StorageBlocked: true, Users: [{ UserName: 'Example', Action: 'skip', Reason: 'Storage blocked' }] }, {}, host(r));
        assert.match(target.textContent, /Settings for Example can't be read\./);
        assert.equal(target.querySelector('button.button-submit'), null);
        assert.equal(writes, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('an Applied-only import error preserves the preview', async () => {
    const r = await env();
    try {
        const target = host(r), box = host(r);
        r.a.api.post = async () => { throw { status: 400, problem: { Applied: false, Message: 'Invalid import' } }; };
        r.a.preview(target, { Users: [{ UserName: 'Example', Action: 'update' }] }, {}, box);
        target.querySelector('button.button-submit').click(); await settle(3);
        r.document.querySelector('.bfui-dialog').querySelectorAll('button')[1].click(); await settle(30);
        assert.match(target.textContent, /Example/); assert.match(box.textContent, /Invalid import/);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('sidebar guard closes the mobile drawer before showing discard', async () => {
    const r = await env();
    try {
        r.ctx.location = { href: 'http://srv/jf/web/#/configurationpage?name=Babelfin' }; dirty(r);
        const backdrop = r.document.createElement('div'); backdrop.className = 'mdl-layout__obfuscator is-visible'; r.document.body.appendChild(backdrop);
        let closed = false; backdrop.addEventListener('click', () => { closed = true; });
        const link = r.document.createElement('a'); link.href = '#/dashboard'; r.document.body.appendChild(link); link.click();
        await settle(3); assert.equal(closed, true); await answer(r, false);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const action of ['reload', 'keep']) test('server conflict ' + action + ' keeps unrelated pending edits', async () => {
    const r = await env();
    try {
        dirty(r); r.a.state().pending.other = cfg => { cfg.ManualChangeAfterSeconds = 18; };
        let writes = 0; const put = r.a.api.put; const headers = [];
        r.a.api.put = async (url, body, query, extra) => {
            headers.push(extra); writes++;
            if (writes === 1) throw { status: 409, problem: { Code: 'field-conflict', ConflictingPaths: ['enabled'], CurrentValues: { enabled: true }, LastChangedByName: 'Example' } };
            return put(url, body, query, extra);
        };
        await r.a.save(); const box = r.page.querySelector('.bf-conflict'); assert.ok(box); assert.match(box.textContent, /Example/);
        if (action === 'keep') r.server.state.expectUnconditionalSettingsPut = true;
        box.querySelector('[data-conflict="' + action + '"]').click(); await settle(40);
        if (action === 'reload') { assert.equal(r.page.querySelector('#bfEnabled').checked, true); assert.ok(Object.keys(r.a.state().pending).length); }
        else { assert.equal(writes, 2); assert.equal(headers[1], undefined); assert.equal(r.server.state.config.ManualChangeAfterSeconds, 18); }
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('successful person save reports saved state when editor rebasing fails', async () => {
    const r = await env({ patch: text => text.replace('const later = editedFields(submitted, work);', 'if (window.failRebase) { throw new Error("Invalid library row"); } const later = editedFields(submitted, work);') });
    try {
        const p = await isolatedPerson(r); r.ctx.failRebase = true; let saved;
        r.a.api.put = async (url, body) => { saved = copy(body); return body; };
        p.enabled.click(); p.save.click(); await settle(30);
        assert.equal(saved.Enabled, false);
        assert.match(p.target.textContent, /Saved.*Reload/);
        assert.equal(p.target.querySelector('input[type="checkbox"]').checked, false);
        assert.equal(r.a.state().person.dirty, false);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('successful person save invalidates People even if the editor was replaced', async () => {
    const r = await env();
    try {
        const p = await isolatedPerson(r), held = defer(); r.a.api.put = () => held.promise;
        p.enabled.click(); p.save.click(); await settle(3); r.a.state().person = { id: 'another', dirty: false };
        assert.ok(r.a.state().cache.users); held.resolve(Object.assign(copy(p.doc), { Enabled: false })); await settle(30);
        assert.equal(r.a.state().cache.users, undefined);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const action of ['reload', 'keep']) test('admin person conflict ' + action + ' keeps unrelated edits', async () => {
    const r = await env();
    try {
        const p = await isolatedPerson(r); let writes = 0, firstExpected;
        const audio = p.target.querySelector('.bfui-table-audio select'); audio.value = 'original'; audio.dispatchEvent(new Event('change'));
        r.a.api.get = async () => ({ Enabled: true, Everyday: {}, Libraries: [], Locks: {} });
        r.a.api.put = async (url, body, query, headers) => {
            writes++;
            if (writes === 1) { firstExpected = copy(body.ExpectedValues); throw { status: 409, problem: { Code: 'field-conflict', ConflictingPaths: ['enabled'], CurrentValues: { enabled: true }, LastChangedByName: 'Example' } }; }
            assert.equal(headers, undefined); assert.equal(body.ExpectedValues, undefined); return body;
        };
        p.enabled.click(); p.save.click(); await settle(25);
        assert.deepEqual(firstExpected, { enabled: true, 'everyday.audioChain': null });
        const box = p.target.querySelector('.bf-conflict'); assert.ok(box); box.querySelector('[data-conflict="' + action + '"]').click(); await settle(30);
        assert.equal(writes, action === 'reload' ? 1 : 2);
        assert.equal(p.target.querySelector('input[type="checkbox"]').checked, action === 'reload');
        assert.equal(p.target.querySelector('.bfui-table-audio select').value, 'original');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});


test('copy confirmation promises a backup before replacement and cancels without mutation', async () => {
    const r = await env();
    try {
        const p = await isolatedPerson(r); let writes = 0; r.a.api.post = async () => { writes++; };
        p.target.querySelector('.bf-actions .bf-btn').click(); await settle(3);
        const select = p.target.querySelector('.bf-copy select'); select.value = select.options[0].value;
        p.target.querySelector('.bf-copy button').click(); await settle(3);
        const dialog = r.document.querySelector('.bfui-dialog'); assert.ok(dialog);
        assert.match(dialog.textContent, /current settings are saved in a backup first/);
        dialog.querySelector('button').click(); await settle(3); assert.equal(writes, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('known German server errors replace the English summary in the admin page', async () => {
    const r = await env();
    try {
        const de = JSON.parse(fs.readFileSync(path.join(__dirname, '../../plugin/src/Jellyfin.Plugin.Babelfin/Assets/i18n-admin/de.json'), 'utf8'));
        dirty(r); r.a.state().t = key => de[key] || key;
        r.a.api.put = async () => { throw { status: 503, problem: { Code: 'library-unavailable', Message: 'The library is unavailable' } }; };
        await r.a.save(); assert.equal(r.page.querySelector('#bfSaveError').textContent, de['api.library-unavailable']);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const where of ['admin defaults', 'People editor']) test('switching Original and My languages removes and restores the tooltip: ' + where, async () => {
    const r = await env();
    try {
        const target = where === 'People editor' ? (await isolatedPerson(r)).target : r.page.querySelector('#bfLibraries');
        const select = target.querySelector('.bfui-table-audio select'), line = select.closest('.bfui-choice-line');
        for (let i = 0; i < 3; i++) {
            select.value = 'original'; select.dispatchEvent(new Event('change'));
            const button = line.querySelector('.bfui-info'), id = select.getAttribute('aria-describedby');
            assert.ok(button && id); button.click(); assert.equal(r.document.getElementById(id).hidden, false);
            select.focus(); select.value = 'mine'; select.dispatchEvent(new Event('change'));
            assert.equal(select.getAttribute('aria-describedby'), null); assert.equal(Boolean(line.querySelector('.bfui-info')), false);
            assert.equal(Boolean(r.document.getElementById(id)), false);
        }
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const shipped of [90, 120, undefined]) test('Restore defaults ignores retired activity values and preserves New users: ' + String(shipped), async () => {
    const r = await env();
    try {
        r.a.state().config.ActivityRetentionDays = r.server.state.config.ActivityRetentionDays = 240;
        const template = { Everyday: { Understood: ['de'] } };
        r.a.state().config.NewUserTemplate = copy(template); r.server.state.config.NewUserTemplate = copy(template);
        r.a.advanced(); await settle(30);
        assert.equal(r.page.querySelector('#bf-group-activityRetention'), null);
        r.a.state().cache.shipped = Promise.resolve(shipped === undefined ? {} : { ActivityRetentionDays: shipped });
        r.page.querySelector('#bfRestoreDefaults').click(); await settle(3);
        const dialog = r.document.querySelector('.bfui-dialog'); assert.match(dialog.textContent, /Apps and Track recognition/);
        dialog.querySelectorAll('button')[1].click(); await settle(30);
        assert.equal(r.server.state.puts.length, 0);
        assert.equal(Object.prototype.hasOwnProperty.call(r.a.state().pending, 'ActivityRetentionDays'), false);
        assert.deepEqual(copy(r.server.state.config.NewUserTemplate), template);
        await r.a.save(); assert.equal(r.server.state.config.ActivityRetentionDays, 240);
        assert.deepEqual(copy(r.server.state.config.NewUserTemplate), template);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const action of ['reload', 'keep']) test('server conflict ' + action + ' refreshes the next expected baseline', async () => {
    const r = await env();
    try {
        dirty(r); r.a.state().pending.other = cfg => { cfg.ManualChangeAfterSeconds = 18; };
        r.server.state.config.Enabled = false;
        const put = r.a.api.put, submitted = [];
        r.a.api.put = async (url, body, query, headers) => {
            submitted.push({ body: copy(body), fields: query.fields, headers });
            if (submitted.length === 1) throw { status: 409, problem: { Code: 'field-conflict', ConflictingPaths: ['enabled'], CurrentValues: { enabled: false }, LastChangedByName: 'Example' } };
            if (body.ExpectedValues) {
                const expected = body.ExpectedValues;
                if ('enabled' in expected) assert.equal(expected.enabled, r.server.state.config.Enabled, 'Expected value follows the latest successful save or Reload');
            }
            return put(url, body, query, headers);
        };
        await r.a.save();
        if (action === 'keep') r.server.state.expectUnconditionalSettingsPut = true;
        r.page.querySelector('[data-conflict="' + action + '"]').click(); await settle(35);
        if (action === 'reload') {
            assert.equal(r.page.querySelector('#bfEnabled').checked, false); await r.a.save();
            assert.equal(submitted[1].fields, 'manualChangeAfterSeconds'); assert.ok(submitted[1].body.ExpectedValues);
        } else { assert.equal(submitted[1].body.ExpectedValues, undefined); }
        assert.equal(r.server.state.config.ManualChangeAfterSeconds, 18);
        r.page.querySelector('#bfEnabled').click(); await r.a.save();
        assert.ok(submitted.at(-1).body.ExpectedValues);
        assert.equal(r.server.state.config.Enabled, true); assert.equal(r.page.querySelector('.bf-conflict'), null);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('successful person save reports a failed People refresh without losing the saved state', async () => {
    const r = await env();
    try {
        const p = await isolatedPerson(r), get = r.a.api.get;
        r.a.api.get = async (url, query) => { if (url === 'Babelfin/Admin/Users') throw new Error('Summary unavailable'); return get(url, query); };
        r.a.api.put = async (url, body) => body;
        p.enabled.click(); p.save.click(); await settle(30);
        assert.equal(p.enabled.checked, false); assert.equal(r.a.state().person.dirty, false);
        assert.match(p.target.textContent, /Saved.*Reload/);
        assert.equal(p.save.disabled, true);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

for (const action of ['reload', 'keep']) test('committed mutation clears stale server conflict before ' + action, async () => {
    const r = await env();
    try {
        dirty(r);
        r.a.api.put = async () => { throw { status: 409, problem: { Code: 'field-conflict', ConflictingPaths: ['enabled'], CurrentValues: { enabled: true } } }; };
        await r.a.save();
        const box = r.page.querySelector('#bfSaveError');
        assert.ok(box.querySelector('.bf-conflict'));
        r.a.refresh(host(r)); await settle(30);
        assert.ok(!box.querySelector('.bf-conflict'), 'Mutation removes the conflict panel');
        assert.equal(box.textContent, '');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('stale conflict reload closes panel after a concurrent refresh', async () => {
    const r = await env();
    try {
        dirty(r);
        r.a.api.put = async () => { throw { status: 409, problem: { Code: 'field-conflict', ConflictingPaths: ['enabled'], CurrentValues: { enabled: true } } }; };
        await r.a.save();
        const box = r.page.querySelector('#bfSaveError'); const held = defer(), original = r.a.api.get;
        r.a.api.get = (route, ...args) => route === 'Babelfin/Admin/Settings' ? held.promise : original(route, ...args);
        box.querySelector('[data-conflict="reload"]').click();
        r.a.refresh(host(r)); held.resolve(copy(r.server.state.config)); await settle(30);
        assert.ok(!box.querySelector('.bf-conflict'), 'Stale reload removes the conflict panel');
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('keep mine with no pending changes closes or re-enables the conflict panel', async () => {
    const r = await env();
    try {
        dirty(r);
        r.a.api.put = async () => { throw { status: 409, problem: { Code: 'field-conflict', ConflictingPaths: ['enabled'], CurrentValues: { enabled: true } } }; };
        await r.a.save(); const box = r.page.querySelector('#bfSaveError');
        r.a.state().pending = {};
        box.querySelector('[data-conflict="keep"]').click(); await settle(20);
        const panel = box.querySelector('.bf-conflict');
        assert.ok(!panel || Array.from(panel.querySelectorAll('button')).every(button => !button.disabled));
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('blocked import without reason does not attribute the block to every listed person', async () => {
    const r = await env();
    try {
        const target = host(r); let writes = 0; r.a.api.post = async () => { writes++; }; r.a.api.put = async () => { writes++; };
        r.a.preview(target, { StorageBlocked: true, Users: [{ UserName: 'Person One', Action: 'skip' }, { UserName: 'Person Two', Action: 'update' }] }, {}, host(r));
        const message = target.querySelectorAll('.bf-hint').find(node => /Settings for one or more people can't be read\./.test(node.textContent));
        assert.ok(message); assert.doesNotMatch(message.textContent, /Person One|Person Two/);
        assert.equal(target.querySelector('button.button-submit'), null); assert.equal(writes, 0);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});


test('activity retention controls are absent from Advanced', async () => {
    const r = await env();
    try {
        r.a.advanced();
        assert.equal(r.page.querySelector('#bf-group-activityRetention'), null);
        assert.doesNotMatch(r.page.querySelector('#bfAdvanced').textContent, /Activity history retention/);
    } finally { r.ctx.BabelfinDashboard.hide(); }
});

test('activity list explains its session lifetime and Jellyfin logs', async () => {
    const r = await env();
    try {
        const target = host(r), get = r.a.api.get;
        r.a.api.get = (route, ...args) => route === 'Babelfin/Admin/Activity' ? Promise.resolve([]) : get(route, ...args);
        r.a.activity(target); await settle(30);
        assert.match(target.textContent, /Shows activity since Jellyfin last started/);
        assert.match(target.textContent, /Earlier events are in Jellyfin.s logs.*Dashboard.*Logs/);
        assert.ok(target.querySelector('.bf-hint'));
    } finally { r.ctx.BabelfinDashboard.hide(); }
});
