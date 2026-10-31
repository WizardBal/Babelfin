'use strict';
// Offline driver tests: no Chrome, Jellyfin process, or network. Run with Node:
// node --test tools/tests/browser-driver.test.js
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { Page, Chrome } = require('../rig/e2e/cdp');
const { requireLoopback, mockResetAll } = require('../rig/e2e/run');

function driver() {
    const calls = [];
    const page = new Page({ async send(method, params) {
        calls.push({ method, params });
        if (method === 'Fetch.getResponseBody') return { body: '{"original":true}', base64Encoded: false };
        return {};
    } }, 'test-target', 'test-session');
    const pause = (method = 'POST', suffix = '/ResetAll') => ({
        requestId: 'mock-request', request: { url: 'http://127.0.0.1:8098/Babelfin/Admin' + suffix, method, postData: '{}' }
    });
    return { page, calls, pause };
}

for (const base of ['http://127.0.0.1:8098', 'http://localhost:8098/jf', 'http://[::1]:8098']) {
    test('Browser fixtures accept loopback ' + base, () => requireLoopback({ base }));
}
for (const base of ['http://media.example', 'http://192.0.2.10:8096', 'http://127.0.0.1.example', 'ftp://localhost', 'file:///C:/fixture']) {
    test('Browser fixtures reject non-rig base ' + base, () => assert.throws(() => requireLoopback({ base }), /loopback/));
}

test('partial reset mock fulfils the POST before it reaches Jellyfin', async () => {
    const { page, calls, pause } = driver();
    const stop = await page.mockResponses('*/Babelfin/Admin/ResetAll', request => {
        assert.equal(request.method, 'POST');
        return { body: { People: 1, FailedNames: ['Rig failed person'], BackupPath: 'test-backup.json' } };
    });
    assert.deepEqual(calls[0].params.patterns, [{ urlPattern: '*/Babelfin/Admin/ResetAll', requestStage: 'Request' }]);
    calls.length = 0;
    await page.onPaused(pause());
    assert.deepEqual(calls.map(c => c.method), ['Fetch.fulfillRequest']);
    const fulfilled = calls[0].params;
    assert.equal(fulfilled.responseCode, 200);
    assert.deepEqual(JSON.parse(Buffer.from(fulfilled.body, 'base64').toString()), {
        People: 1, FailedNames: ['Rig failed person'], BackupPath: 'test-backup.json'
    });
    const stats = await stop();
    assert.equal(stats.hits, 1);
    assert.equal(stats.fulfilled, 1);
    assert.equal(calls.at(-1).method, 'Fetch.disable');
});

test('synthetic acknowledgement has an empty 204 response and never clears server history', async () => {
    const { page, calls, pause } = driver();
    const stop = await page.mockResponses('*/Migrations/Acknowledge', () => ({ status: 204, body: '' }));
    calls.length = 0;
    await page.onPaused(pause('POST', '/Migrations/Acknowledge'));
    assert.deepEqual(calls.map(c => c.method), ['Fetch.fulfillRequest']);
    assert.equal(calls[0].params.responseCode, 204);
    assert.equal(calls[0].params.body, '');
    assert.equal((await stop()).fulfilled, 1);
});

test('Request mocks coexist with response rewriting and stop independently', async () => {
    const { page, calls, pause } = driver();
    const stopRead = await page.interceptResponses('*/Migrations', () => '[]');
    const stopAck = await page.mockResponses('*/Migrations/Acknowledge', () => ({ status: 204, body: '' }));
    assert.deepEqual(calls.at(-1).params.patterns.map(p => p.requestStage), ['Response', 'Request']);
    await stopAck();
    await stopAck();
    assert.deepEqual(calls.at(-1).params.patterns, [{ urlPattern: '*/Migrations', requestStage: 'Response' }]);
    calls.length = 0;
    await page.onPaused(Object.assign(pause('GET', '/Migrations'), {
        responseStatusCode: 200,
        responseHeaders: [
            { name: 'Content-Type', value: 'application/json' },
            { name: 'Content-Encoding', value: 'br' }, { name: 'Content-Length', value: '100' }, { name: 'ETag', value: 'old' }
        ]
    }));
    assert.deepEqual(calls.map(c => c.method), ['Fetch.getResponseBody', 'Fetch.fulfillRequest']);
    assert.equal(Buffer.from(calls[1].params.body, 'base64').toString(), '[]');
    assert.deepEqual(calls[1].params.responseHeaders, [{ name: 'Content-Type', value: 'application/json' }]);
    assert.equal((await stopRead()).rewritten, 1);
    assert.equal(calls.at(-1).method, 'Fetch.disable');
});

test('A failing destructive-action mock fails closed and remains visible to browser hygiene', async () => {
    const { page, calls, pause } = driver();
    const stop = await page.mockResponses('*/ResetAll', () => { throw new Error('fixture failure'); });
    calls.length = 0;
    await page.onPaused(pause());
    assert.deepEqual(calls.map(c => c.method), ['Fetch.failRequest']);
    assert.match(page.exceptions[0].text, /fixture failure/);
    assert.match(page.exceptions[0].url, /Babelfin/);
    assert.equal((await stop()).fulfilled, 0);
});

// Model CDP responses, keeping the complete shipped Page.click implementation under test.
// The two quads reproduce the half-size/open sheet and its final Babelfin row from the real trace.
function clickDriver({ moveBox = false, coverOnMove = false } = {}) {
    const opening = [1029.59375, 244.078125, 1172.78125, 244.078125, 1172.78125, 264.140625, 1029.59375, 264.140625];
    const settled = [958, 151, 1244.375, 151, 1244.375, 191.125, 958, 191.125];
    const calls = [], activations = [];
    let pointerMoved = false;
    const quad = () => moveBox && !pointerMoved ? opening : settled;
    const hit = (x, y) => {
        if (coverOnMove && pointerMoved) return 'div#overlay';
        const q = quad();
        return x >= q[0] && x <= q[2] && y >= q[1] && y <= q[5] ? 'ok' : 'button#other-native-command';
    };
    const page = new Page({ async send(method, params) {
        calls.push({ method, params });
        if (method === 'Runtime.evaluate') return { result: { objectId: 'babelfin-row', subtype: 'node' } };
        if (method === 'DOM.getBoxModel') return { model: { border: quad().slice() } };
        if (method === 'Runtime.callFunctionOn') {
            if (params.arguments) return { result: { value: hit(...params.arguments.map(a => a.value)) } };
            return { result: { value: true } }; // The renderer has delivered the requested frames.
        }
        if (method === 'Input.dispatchMouseEvent') {
            if (params.type === 'mouseMoved') pointerMoved = true;
            if (params.type === 'mousePressed') activations.push(hit(params.x, params.y));
        }
        return {};
    } }, 'click-target', 'click-session');
    return { page, calls, activations };
}

test('Page.click remeasures after pointer movement before pressing an opening menu row', async () => {
    const { page, calls, activations } = clickDriver({ moveBox: true });
    const clicked = await page.click('.bfSheetItem', { timeout: 1000 });
    assert.equal(calls[0].method, 'Page.bringToFront', 'Foreground the tab before measuring or clicking it');
    assert.deepEqual(clicked, { x: 1101.1875, y: 171.0625 }, 'Click the final row, not the former half-size coordinates');
    assert.deepEqual(activations, ['ok'], 'Never activate the different native command at the old point');
    const input = calls.filter(c => c.method === 'Input.dispatchMouseEvent' && c.params.type !== 'mouseMoved');
    assert.deepEqual(input.map(c => [c.params.type, c.params.x, c.params.y]), [
        ['mousePressed', 1101.1875, 171.0625], ['mouseReleased', 1101.1875, 171.0625]
    ], 'Send exactly one down/up pair at the settled coordinates');
});

test('Page.click refuses a target covered after pointer movement without pressing the overlay', async () => {
    const { page, calls, activations } = clickDriver({ coverOnMove: true });
    await assert.rejects(page.click('.bfSheetItem', { timeout: 25 }), /covered.*div#overlay/);
    assert.deepEqual(activations, [], 'A successful pre-move hit test must not authorize a covered click');
    assert.equal(calls.filter(c => c.method === 'Input.dispatchMouseEvent' && c.params.type !== 'mouseMoved').length, 0,
        'Neither mouse down nor mouse up is sent to the covering element');
});

test('A mock can explicitly pass through an unrelated method', async () => {
    const { page, calls, pause } = driver();
    const stop = await page.mockResponses('*/ResetAll', request => request.method === 'POST' ? { body: {} } : null);
    calls.length = 0;
    await page.onPaused(pause('GET'));
    assert.deepEqual(calls.map(c => c.method), ['Fetch.continueRequest']);
    assert.equal((await stop()).fulfilled, 0);
});

for (const suffix of ['/ResetAll', '/ResetAll?allowPartial=true', '/ResetAll?allowPartial=true&other=value']) {
    test('the real partial-reset fixture blocks server mutation for ' + suffix, async () => {
        const { page, calls, pause } = driver();
        const t = { base: 'http://127.0.0.1:8098', url: path => 'http://127.0.0.1:8098' + path };
        const stop = await mockResetAll(t, page, () => ({ body: { People: 1, FailedNames: ['Synthetic failure'] } }));
        calls.length = 0;
        await page.onPaused(pause('POST', suffix));
        assert.deepEqual(calls.map(c => c.method), ['Fetch.fulfillRequest']);
        assert.equal((await stop()).fulfilled, 1);
    });
}
for (const invalid of ['empty response', 'unexpected suffix']) {
    test('the real partial-reset fixture fails closed for ' + invalid, async () => {
        const { page, calls, pause } = driver();
        const t = { base: 'http://127.0.0.1:8098', url: path => 'http://127.0.0.1:8098' + path };
        const stop = await mockResetAll(t, page, () => null);
        calls.length = 0;
        await page.onPaused(pause('POST', invalid === 'unexpected suffix' ? '/ResetAllUnexpected' : '/ResetAll?allowPartial=true'));
        assert.deepEqual(calls.map(c => c.method), ['Fetch.failRequest']);
        assert.equal(page.exceptions.length, 1);
        assert.equal((await stop()).fulfilled, 0);
    });
}


test('a missing CDP reply rejects within a bounded deadline and releases the pending request', async () => {
    const chrome = new Chrome();
    chrome.commandTimeout = 15;
    chrome.ws = { readyState: 1, send() {} };
    await assert.rejects(chrome.send('Profiler.takePreciseCoverage', {}, 'gone'), /command timed out/);
    assert.equal(chrome.pending.size, 0);
});

test('target detachment rejects only that session and leaves other requests usable', async t => {
    const original = global.WebSocket;
    let socket;
    global.WebSocket = class { constructor() { socket = this; this.readyState = 1; } send() {} };
    t.after(() => { global.WebSocket = original; });
    const chrome = new Chrome();
    const connected = chrome.connect('ws://127.0.0.1/fixture');
    socket.onopen(); await connected;
    const gone = chrome.send('Profiler.takePreciseCoverage', {}, 'removed-session');
    const live = chrome.send('Runtime.evaluate', {}, 'live-session');
    const rejected = assert.rejects(gone, /target session detached/);
    socket.onmessage({ data: JSON.stringify({ method: 'Target.detachedFromTarget', params: { sessionId: 'removed-session' } }) });
    await rejected;
    assert.equal(chrome.pending.size, 1);
    const [id] = chrome.pending.keys();
    socket.onmessage({ data: JSON.stringify({ id, result: { live: true } }) });
    assert.deepEqual(await live, { live: true });
    assert.equal(chrome.pending.size, 0);
});


test('final coverage failure cannot bypass Chrome cleanup', async () => {
    const chrome = new Chrome();
    let cleaned = 0;
    chrome.coverage = { async take() { throw new Error('coverage unavailable'); } };
    chrome.killNow = () => { cleaned++; chrome.closed = true; };
    await assert.rejects(chrome.close(), /coverage unavailable/);
    assert.equal(cleaned, 1);
});

test('CDP send failure clears its timer and cannot leave a pending command', async () => {
    const chrome = new Chrome();
    chrome.ws = { readyState: 1, send() { throw new Error('socket write failed'); } };
    await assert.rejects(chrome.send('Target.getTargets'), /socket write failed/);
    assert.equal(chrome.pending.size, 0);
});


for (const operation of ['navigate', 'reload']) {
    test(operation + ' starts its load deadline only after the coverage queue releases it', async () => {
        let page;
        const chrome = {
            coverage: { async snapshotThen(action) { await new Promise(resolve => setTimeout(resolve, 40)); return action(); } },
            async send() { page.dispatch('Page.loadEventFired', {}); return { loaderId: 'loaded' }; }
        };
        page = new Page(chrome, 'queued-target', 'queued-session');
        if (operation === 'navigate') await page.navigate('http://127.0.0.1/fixture', { timeout: 15 });
        else await page.reload({ timeout: 15 });
    });
}

for (const [name, key, keyCode] of [['BrowserBack', 'BrowserBack', 166], ['GoBack', 'GoBack', 166], ['WebOSBack', 'Unidentified', 461], ['TizenBack', 'Unidentified', 10009]]) test('remote Back key is dispatched through CDP: ' + name, async () => {
    const r = driver(); await r.page.press(name);
    assert.deepEqual(r.calls.map(call => [call.method, call.params.type, call.params.key, call.params.windowsVirtualKeyCode]), [
        ['Input.dispatchKeyEvent', 'rawKeyDown', key, keyCode], ['Input.dispatchKeyEvent', 'keyUp', key, keyCode]
    ]);
});

test('request observers receive original headers without fulfilling the request', async () => {
    const { page, calls, pause } = driver();
    let observed;
    const stop = await page.mockResponses('*/ResetAll', request => { observed = request; return null; });
    const request = pause();
    request.request.headers = { 'X-Babelfin-Expected': '{"everyday.subtitleLanguages":["en"]}' };
    calls.length = 0;
    await page.onPaused(request);
    assert.deepEqual(observed.headers, request.request.headers);
    assert.deepEqual(calls.map(call => call.method), ['Fetch.continueRequest']);
    assert.equal((await stop()).fulfilled, 0);
});
test('edited-settings assertions permit server audit stamps but still reject unrelated preference writes', () => {
    const { strayPaths } = require('../rig/e2e/run');
    const before = { LastChangedAt: '2026-01-01T00:00:00Z', LastChangedBy: 'self', Everyday: { Sdh: 'Avoid', Format: 'Any' } };
    const after = { LastChangedAt: '2026-01-02T00:00:00Z', LastChangedBy: 'admin', Everyday: { Sdh: 'Prefer', Format: 'Text' } };
    assert.deepEqual(strayPaths(before, after, ['Everyday.Sdh']), ['Everyday.Format']);
});
// Exercise startup without launching Chrome or waiting for the real startup deadline.
test('Chrome retries a port-file timeout once after cleaning the owned profile', async t => {
    const { EventEmitter } = require('node:events');
    const { PassThrough } = require('node:stream');
    const fs = require('node:fs');
    const path = require('node:path');
    const profiles = [], killed = [];
    const listenersBefore = ['exit', 'SIGINT', 'SIGTERM'].map(event => process.listenerCount(event));
    const originalConnect = Chrome.prototype.connect;
    const originalWarn = console.warn;
    const warnings = [];
    Chrome.prototype.connect = async function () {};
    console.warn = message => warnings.push(message);
    t.after(() => { Chrome.prototype.connect = originalConnect; console.warn = originalWarn; });
    const fakeSpawn = (exe, args, options) => {
        assert.deepEqual(options.stdio, ['ignore', 'ignore', 'pipe']);
        const profile = args.find(arg => arg.startsWith('--user-data-dir=')).slice('--user-data-dir='.length);
        if (profiles.length) assert.equal(fs.existsSync(profiles[0]), false, 'first profile removed before retry');
        profiles.push(profile);
        const proc = new EventEmitter();
        proc.pid = 1000 + profiles.length;
        proc.stderr = new PassThrough();
        if (profiles.length === 1) proc.stderr.write('first startup diagnostic');
        else fs.writeFileSync(path.join(profile, 'DevToolsActivePort'), '12345\n/devtools/browser/test\n');
        return proc;
    };
    const chrome = await Chrome.launch({ chromePath: process.execPath, startupTimeout: 20, startupPollInterval: 2, _spawn: fakeSpawn, _killTree: pid => { killed.push(pid); return true; } });
    assert.equal(profiles.length, 2);
    assert.deepEqual(killed, [1001]);
    assert.notEqual(profiles[0], profiles[1]);
    assert.match(warnings[0], /first startup diagnostic/);
    assert.equal(chrome.port, 12345);
    chrome.killNow();
    assert.deepEqual(killed, [1001, 1002]);
    assert.equal(fs.existsSync(profiles[1]), false);
    assert.deepEqual(['exit', 'SIGINT', 'SIGTERM'].map(event => process.listenerCount(event)), listenersBefore);
});

test('Chrome reports both bounded timeout diagnostics and never makes a third attempt', async t => {
    const { EventEmitter } = require('node:events');
    const { PassThrough } = require('node:stream');
    const fs = require('node:fs');
    const profiles = [];
    const originalWarn = console.warn;
    console.warn = () => {};
    t.after(() => { console.warn = originalWarn; });
    const fakeSpawn = (exe, args) => {
        profiles.push(args.find(arg => arg.startsWith('--user-data-dir=')).slice('--user-data-dir='.length));
        const proc = new EventEmitter(); proc.stderr = new PassThrough();
        proc.stderr.write('attempt ' + profiles.length + ' ' + 'x'.repeat(9000));
        return proc;
    };
    await assert.rejects(Chrome.launch({ chromePath: process.execPath, startupTimeout: 10, startupPollInterval: 2, _spawn: fakeSpawn }), error => {
        assert.match(error.message, /First startup attempt/);
        assert.ok(error.message.length < 17000, 'stderr is bounded per attempt');
        return true;
    });
    assert.equal(profiles.length, 2);
    assert.ok(profiles.every(profile => !fs.existsSync(profile)));
});

test('Chrome startup exit and missing executable fail directly without retry', async () => {
    const { EventEmitter } = require('node:events');
    const { PassThrough } = require('node:stream');
    let attempts = 0;
    const fakeSpawn = () => {
        attempts++;
        const proc = new EventEmitter(); proc.stderr = new PassThrough();
        process.nextTick(() => { proc.stderr.write('fatal startup'); proc.emit('exit', 7, null); });
        return proc;
    };
    await assert.rejects(Chrome.launch({ chromePath: process.execPath, startupTimeout: 100, startupPollInterval: 2, _spawn: fakeSpawn }), /exit code 7[\s\S]*fatal startup/);
    assert.equal(attempts, 1);
    await assert.rejects(Chrome.launch({ chromePath: process.execPath + '.missing', _spawn: fakeSpawn }), /Chrome not found/);
    assert.equal(attempts, 1);
});

test('Chrome connection failure after the port file is not retried', async t => {
    const { EventEmitter } = require('node:events');
    const { PassThrough } = require('node:stream');
    const fs = require('node:fs');
    const path = require('node:path');
    const originalConnect = Chrome.prototype.connect;
    Chrome.prototype.connect = async () => { throw new Error('connection failed'); };
    t.after(() => { Chrome.prototype.connect = originalConnect; });
    let attempts = 0;
    const fakeSpawn = (exe, args) => {
        attempts++;
        const profile = args.find(arg => arg.startsWith('--user-data-dir=')).slice('--user-data-dir='.length);
        fs.writeFileSync(path.join(profile, 'DevToolsActivePort'), '12345\n/devtools/browser/test\n');
        const proc = new EventEmitter(); proc.stderr = new PassThrough(); return proc;
    };
    await assert.rejects(Chrome.launch({ chromePath: process.execPath, _spawn: fakeSpawn }), /connection failed/);
    assert.equal(attempts, 1);
});


test('Chrome spawn error is reported directly and releases process listeners', async () => {
    const { EventEmitter } = require('node:events');
    const { PassThrough } = require('node:stream');
    const listenersBefore = ['exit', 'SIGINT', 'SIGTERM'].map(event => process.listenerCount(event));
    let attempts = 0;
    const fakeSpawn = () => {
        attempts++;
        const proc = new EventEmitter(); proc.stderr = new PassThrough();
        process.nextTick(() => proc.emit('error', new Error('spawn denied')));
        return proc;
    };
    await assert.rejects(Chrome.launch({ chromePath: process.execPath, startupTimeout: 100, startupPollInterval: 2, _spawn: fakeSpawn }), /spawn error: spawn denied/);
    assert.equal(attempts, 1);
    assert.deepEqual(['exit', 'SIGINT', 'SIGTERM'].map(event => process.listenerCount(event)), listenersBefore);
});


test('Chrome does not retry a timeout when owned process cleanup cannot be confirmed', async () => {
    const { EventEmitter } = require('node:events');
    const { PassThrough } = require('node:stream');
    const fs = require('node:fs');
    let attempts = 0, profile;
    const fakeSpawn = (exe, args, options) => {
        attempts++;
        assert.equal(options.detached, process.platform !== 'win32');
        profile = args.find(arg => arg.startsWith('--user-data-dir=')).slice('--user-data-dir='.length);
        const proc = new EventEmitter(); proc.pid = 1234; proc.stderr = new PassThrough(); return proc;
    };
    await assert.rejects(Chrome.launch({ chromePath: process.execPath, startupTimeout: 10, startupPollInterval: 2,
        _spawn: fakeSpawn, _killTree: () => false }), /process tree cleanup could not be confirmed/);
    assert.equal(attempts, 1);
    assert.equal(fs.existsSync(profile), false);
});
