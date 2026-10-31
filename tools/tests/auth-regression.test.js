'use strict';
// Executes the complete shipped UI helper and app in an isolated browser-like VM. The single
// instrumentation assignment exposes the real app API; no functions are extracted or reimplemented.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');
const assets = path.resolve(__dirname, '../../plugin/src/Jellyfin.Plugin.Babelfin/Assets');
const uiSource = fs.readFileSync(path.join(assets, 'ui.js'), 'utf8');
const appSource = fs.readFileSync(path.join(assets, 'app.js'), 'utf8');
const seam = '    const api = {';
assert.equal(appSource.split(seam).length, 2, 'Expected one app API declaration for runtime instrumentation');

async function browser({ servers = [], storage = {}, url = 'https://media.example/jf/Babelfin/app', parent = null, publicId = null, malformed = false, blockedDeviceStorage = false, onFrameTimer = null } = {}) {
    const calls = [];
    const writes = [];
    const reads = [];
    const frameTimers = [];
    const clockStart = Date.now();
    let elapsed = 0;
    class FrameDate extends Date {
        static now() { return clockStart + elapsed; }
    }
    let domReady;
    let translated;
    const authReady = new Promise(resolve => { translated = resolve; });
    const location = new URL(url);
    const values = Object.assign({ jellyfin_credentials: malformed ? '{broken' : JSON.stringify({ Servers: servers }) }, storage);
    const window = {
        location,
        BabelfinI18n: {
            detectLanguage() { return 'en'; },
            load() { translated(); return new Promise(() => {}); }
        },
        addEventListener() {}
    };
    window.parent = parent || window;
    window.top = parent || window;
    const context = vm.createContext({
        window, location, URL, Intl, DOMException, console,
        Date: FrameDate,
        setTimeout(resolve, delay) {
            frameTimers.push(delay);
            elapsed += delay;
            if (onFrameTimer) onFrameTimer({ elapsed, timers: frameTimers.length, calls, reads });
            queueMicrotask(resolve);
            return frameTimers.length;
        },
        localStorage: {
            getItem(key) { reads.push(key); if (blockedDeviceStorage && key === '_deviceId2') throw new Error('storage blocked'); return values[key] || null; },
            setItem(key, value) { writes.push([key, value]); }
        },
        document: {
            readyState: 'loading',
            addEventListener(name, fn) { if (name === 'DOMContentLoaded') { domReady = fn; } },
            getElementById() { return { classList: { add() {} } }; },
            documentElement: { classList: { add() {} } }
        },
        async fetch(address, options) {
            calls.push({ address, options });
            let data;
            if (address.endsWith('/System/Info/Public')) { data = { Id: publicId }; }
            else if (address.endsWith('/Babelfin/Ping')) { data = { version: '999.0-plugin' }; }
            else { data = { ok: true }; }
            return new Response(JSON.stringify(data), { status: 200 });
        }
    });
    vm.runInContext(uiSource, context, { filename: 'ui.js' });
    vm.runInContext(appSource.replace(seam, '    const api = globalThis.testApi = {'), context, { filename: 'app.js' });
    domReady();
    await authReady;
    assert.deepEqual(writes, [], 'Opening settings must never create or overwrite stored identity');
    return { api: context.testApi, calls, reads, writes, frameTimers, elapsed };
}

const entry = (overrides = {}) => Object.assign({
    Id: 'this-server', ManualAddress: 'https://media.example/jf', AccessToken: 'this-token', UserId: 'person', DateLastAccessed: 1
}, overrides);
const auth = b => b.calls.find(c => c.address.endsWith('/Babelfin/Ping'))?.options.headers.Authorization;

for (const foreign of [
    'https://media.example/other', 'https://media.example/jf-other', 'https://media.example',
    'http://media.example/jf', 'https://media.example:8443/jf', 'https://another.example/jf'
]) {
    test(`standalone selects exact origin/base instead of newer server ${foreign}`, async () => {
        const b = await browser({ servers: [entry(), entry({ Id: 'foreign', ManualAddress: foreign, AccessToken: 'foreign-token', DateLastAccessed: 99 })] });
        assert.equal(auth(b), 'MediaBrowser Token="this-token"');
        assert.equal(b.calls.length, 1);
    });
}

test('standalone preserves original stored device name id client and version', async () => {
    const b = await browser({ servers: [entry({ DeviceName: 'My Chrome', DeviceId: 'original-id', AppName: 'Jellyfin Web', AppVersion: '12.2-web' })] });
    assert.equal(auth(b), 'MediaBrowser Token="this-token", Client="Jellyfin Web", Device="My Chrome", DeviceId="original-id", Version="12.2-web"');
    await b.api.getJSON('/Babelfin/Me/Settings');
    assert.equal(b.calls[1].options.headers.Authorization, auth(b), 'Plugin ping version must never replace device app version');
});

test('standalone reuses jellyfin-web stored device id without inventing name or version', async () => {
    const b = await browser({ servers: [entry()], storage: { _deviceId2: 'web-original', 'babelfin-device-id': 'old-babelfin-id' } });
    assert.equal(auth(b), 'MediaBrowser Token="this-token", DeviceId="web-original"');
});

test('standalone uses token only when identity is absent or unsafe', async () => {
    const b = await browser({ servers: [entry({ DeviceName: 'Name,unsafe', AppName: '"unsafe"', AppVersion: 'bad\nvalue' })] });
    assert.equal(auth(b), 'MediaBrowser Token="this-token"');
});

test('stable public server id resolves an alternate address without sending a token to discovery', async () => {
    const b = await browser({ publicId: 'this-server', servers: [entry({ ManualAddress: 'http://alternate.example/jellyfin' }), entry({ Id: 'foreign', ManualAddress: 'https://foreign.example', AccessToken: 'foreign-token', DateLastAccessed: 99 })] });
    assert.equal(b.calls[0].address, '/jf/System/Info/Public');
    assert.equal(b.calls[0].options.headers.Authorization, undefined);
    assert.equal(auth(b), 'MediaBrowser Token="this-token"');
});

test('no matching origin or server id never tries another server token', async () => {
    const b = await browser({ publicId: 'new-server', servers: [entry({ Id: 'foreign', ManualAddress: 'https://foreign.example', AccessToken: 'foreign-token' })] });
    assert.equal(b.calls.length, 1);
    assert.equal(b.calls[0].options.headers.Authorization, undefined);
    assert.equal(auth(b), undefined);
});

test('malformed credentials never send authenticated requests', async () => {
    const b = await browser({ malformed: true });
    assert.equal(b.calls.length, 0);
});

function clientParent() {
    const requests = [];
    let respond = () => JSON.stringify({ version: '999-plugin' });
    const client = {
        getCurrentUserId() { return 'parent-user'; },
        getUrl(p, q) {
            const parts = Object.entries(q || {}).filter(([, v]) => v !== null && v !== undefined && v !== '').map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`);
            return 'https://media.example/jf/' + p + (parts.length ? '?' + parts.join('&') : '');
        },
        async ajax(options) { requests.push(options); return respond(options); }
    };
    return { ApiClient: client, requests, response(fn) { respond = fn; } };
}

test('embedded uses parent ApiClient and keeps JSON empty text and mutation contracts', async () => {
    const parent = clientParent();
    const b = await browser({ parent, servers: [entry({ AccessToken: 'foreign-stored-token' })] });
    assert.deepEqual(b.calls, []);
    assert.deepEqual(b.reads, []);
    assert.equal(parent.requests[0].url, 'https://media.example/jf/Babelfin/Ping');
    assert.equal(parent.requests[0].dataType, 'text');
    parent.response(() => '{"Settings":{"Enabled":true}}');
    assert.equal((await b.api.getJSON('/Babelfin/Me/Settings')).Settings.Enabled, true);
    parent.response(() => '');
    assert.equal(await b.api.deleteJSON('/Babelfin/Me/Overrides/one', { scope: 'Series' }), null);
    parent.response(() => 'plain success');
    assert.equal(await b.api.postJSON('/Babelfin/Me/Reset'), 'plain success');
    parent.response(() => '{"saved":true}');
    assert.equal((await b.api.putJSON('/Babelfin/Me/Settings', { Enabled: true }, { keepOverrides: true })).saved, true);
    const last = parent.requests.at(-1);
    assert.equal(last.type, 'PUT');
    assert.equal(last.url, 'https://media.example/jf/Babelfin/Me/Settings?keepOverrides=true');
    assert.equal(last.contentType, 'application/json');
    assert.equal(last.data, '{"Enabled":true}');
    assert.equal(last.headers, undefined, 'ApiClient owns authentication and device headers');
});

test('embedded normalizes validation and permission errors to UI.api shape', async () => {
    const parent = clientParent();
    const b = await browser({ parent });
    for (const status of [400, 401, 403, 503]) {
        const body = { Message: 'Could not save', Errors: [{ Path: 'Everyday.Reads', Message: 'Invalid language' }], ItemIds: ['item'] };
        parent.response(() => { throw new Response(JSON.stringify(body), { status }); });
        await assert.rejects(b.api.putJSON('/Babelfin/Me/Settings', {}), e => {
            assert.equal(e.name, 'BabelfinApiError');
            assert.equal(e.status, status);
            assert.equal(e.message, body.Message);
            assert.equal(e.body.Message, body.Message);
            assert.equal(e.errors[0].path, 'Everyday.Reads');
            assert.equal(e.errors[0].message, 'Invalid language');
            assert.equal(e.itemIds[0], 'item');
            return true;
        });
    }
    parent.response(() => { throw new TypeError('Network unavailable'); });
    await assert.rejects(b.api.getJSON('/Babelfin/Ping'), e => e.status === 0 && e.body === null);
    const aborted = new DOMException('aborted', 'AbortError');
    parent.response(() => { throw aborted; });
    await assert.rejects(b.api.getJSON('/Babelfin/Ping'), e => e === aborted);
});

test('embedded refuses parent ApiClient for a different server base', async () => {
    const parent = clientParent();
    parent.ApiClient.getUrl = p => 'https://media.example/other/' + p;
    const b = await browser({ parent, servers: [entry()] });
    assert.equal(parent.requests.length, 0);
    assert.equal(auth(b), 'MediaBrowser Token="this-token"');
});

test('embedded preserves preflight and in-flight AbortError behavior', async () => {
    const parent = clientParent();
    const b = await browser({ parent });
    const before = new AbortController();
    before.abort();
    const sent = parent.requests.length;
    await assert.rejects(b.api.getJSON('/Babelfin/Me/Settings', null, { signal: before.signal }), e => e.name === 'AbortError');
    assert.equal(parent.requests.length, sent);
    const during = new AbortController();
    parent.response(() => new Promise(() => {}));
    const pending = b.api.getJSON('/Babelfin/Me/Settings', null, { signal: during.signal });
    during.abort();
    await assert.rejects(pending, e => e.name === 'AbortError');
});

test('blocked optional device storage preserves authenticated token-only access', async () => {
    const b = await browser({ servers: [entry()], blockedDeviceStorage: true });
    assert.equal(auth(b), 'MediaBrowser Token="this-token"');
    await b.api.getJSON('/Babelfin/Me/Settings');
    assert.equal(b.calls[1].options.headers.Authorization, auth(b));
});

// Advance only the app's real readiness timer; no auth function is extracted or replaced.
test('embedded reload waits for a delayed same-origin parent ApiClient SDK', async () => {
    const parent = clientParent();
    parent.location = new URL('https://media.example/jf/web/#/babelfin');
    const sdk = parent.ApiClient;
    delete parent.ApiClient;
    const b = await browser({
        parent, servers: [entry({ AccessToken: 'stored-token-must-not-be-used' })],
        onFrameTimer({ timers, calls, reads }) {
            assert.deepEqual(calls, [], 'No standalone request while waiting for the parent SDK');
            assert.deepEqual(reads, [], 'No stored identity fallback before the readiness deadline');
            assert.deepEqual(parent.requests, [], 'No parent request before its SDK is available');
            if (timers === 3) parent.ApiClient = sdk;
        }
    });
    assert.deepEqual(b.frameTimers, [50, 50, 50]);
    assert.equal(b.elapsed, 150);
    assert.deepEqual(b.calls, []);
    assert.deepEqual(b.reads, []);
    assert.equal(parent.requests.length, 1);
    assert.equal(parent.requests[0].url, 'https://media.example/jf/Babelfin/Ping');
    assert.equal(parent.requests[0].headers, undefined);
    parent.response(() => '{"Settings":{"Enabled":true}}');
    assert.equal((await b.api.getJSON('/Babelfin/Me/Settings')).Settings.Enabled, true);
    assert.equal(parent.requests.at(-1).url, 'https://media.example/jf/Babelfin/Me/Settings');
});

test('embedded reload waits for a delayed parent user id after the SDK exists', async () => {
    const parent = clientParent();
    parent.location = new URL('https://media.example/jf/web/#/babelfin');
    let user = null;
    parent.ApiClient.getCurrentUserId = () => user;
    const b = await browser({
        parent, servers: [entry({ AccessToken: 'stored-token-must-not-be-used' })],
        onFrameTimer({ timers, calls, reads }) {
            assert.deepEqual(calls, []);
            assert.deepEqual(reads, []);
            assert.deepEqual(parent.requests, [], 'No request through an unauthenticated parent SDK');
            if (timers === 4) user = 'parent-user';
        }
    });
    assert.deepEqual(b.frameTimers, [50, 50, 50, 50]);
    assert.equal(b.elapsed, 200);
    assert.deepEqual(b.calls, []);
    assert.deepEqual(b.reads, []);
    assert.equal(parent.requests.length, 1);
    assert.equal(parent.requests[0].url, 'https://media.example/jf/Babelfin/Ping');
    assert.equal(parent.requests[0].headers, undefined);
});

test('embedded readiness deadline falls back to exact-server stored credentials after ten seconds', async () => {
    const parent = { location: new URL('https://media.example/jf/web/#/babelfin') };
    const b = await browser({ parent, servers: [entry()], onFrameTimer({ elapsed, calls, reads }) {
        assert.ok(elapsed <= 10000, 'The parent readiness wait must remain bounded');
        assert.deepEqual(calls, []);
        assert.deepEqual(reads, [], 'Do not read stored credentials before the deadline');
    } });
    assert.equal(b.elapsed, 10000);
    assert.equal(b.frameTimers.length, 200);
    assert.equal(auth(b), 'MediaBrowser Token="this-token"');
    assert.equal(b.calls.length, 1);
});

test('waiting never adopts a ready parent SDK for another server base', async () => {
    const parent = clientParent();
    parent.location = new URL('https://media.example/jf/web/#/babelfin');
    parent.ApiClient.getUrl = p => 'https://media.example/other/' + p;
    const b = await browser({ parent, servers: [entry()] });
    assert.equal(b.elapsed, 10000);
    assert.equal(b.frameTimers.length, 200);
    assert.deepEqual(parent.requests, []);
    assert.equal(auth(b), 'MediaBrowser Token="this-token"');
});
