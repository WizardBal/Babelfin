'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawnSync } = require('node:child_process');
const { inventory, hash, zeroCounters } = require('./source-map');
const { BrowserCoverage } = require('./cdp-coverage');

function fixture(t) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'babelfin-coverage-'));
    t.after(() => fs.rmSync(root, { recursive: true, force: true }));
    const assets = path.join(root, 'plugin/src/Jellyfin.Plugin.Babelfin/Assets');
    fs.mkdirSync(assets, { recursive: true });
    const config = path.join(root, 'plugin/src/Jellyfin.Plugin.Babelfin/Configuration');
    fs.mkdirSync(config);
    fs.writeFileSync(path.join(config, 'configPage.html'), '<script src="external.js"></script><script>window.inline = true;</script>', 'utf8');
    const source = 'window.pick = function pick(flag) { return flag ? "yes" : "no"; };';
    fs.writeFileSync(path.join(assets, 'example.js'), source, 'utf8');
    return { root, assets, source };
}

test('inventory includes inline code independently and rejects a same-name expanded script', t => {
    const { root, source } = fixture(t), sources = inventory(root);
    assert.equal(sources.size, 2);
    assert.ok(sources.has(hash(source)));
    assert.equal(sources.has(hash(source + '\nwindow.testHook = true;')), false);
    assert.equal(sources.has(hash(source.replace('yes', 'changed'))), false);
});

test('whole-source inventory includes the standalone bootstrap and nested assets, excluding JSON data blocks', t => {
    const { root, assets } = fixture(t);
    fs.writeFileSync(path.join(assets, 'app.html'), '<script>window.boot = true;</script><script type="application/json">{"value":1}</script>');
    fs.mkdirSync(path.join(assets, 'nested'));
    fs.writeFileSync(path.join(assets, 'nested', 'extra.js'), 'window.extra = true;');
    const sources = inventory(root);
    assert.equal(sources.size, 4);
    assert.ok(sources.has(hash('window.boot = true;')));
    assert.ok(sources.has(hash('window.extra = true;')));
    assert.equal(sources.has(hash('{"value":1}')), false);
});

test('unloaded sources cannot inherit artificial line, function or branch hits', () => {
    const counts = { file: { s: { 0: 1, 1: 9 }, f: { 0: 7 }, b: { 0: [1, 2], 1: [3] }, statementMap: { 0: { start: 1 } } } };
    const zero = zeroCounters(counts);
    assert.deepEqual(zero.file.s, { 0: 0, 1: 0 });
    assert.deepEqual(zero.file.f, { 0: 0 });
    assert.deepEqual(zero.file.b, { 0: [0, 0], 1: [0] });
    assert.deepEqual(zero.file.statementMap, { 0: { start: 1 } });
});

test('Node inspector counts exact anonymous VM source, rejecting mutated text with a genuine filename', t => {
    const { root, source } = fixture(t), out = path.join(root, 'raw');
    const env = { ...process.env, BF_COVERAGE_ROOT: root, BF_COVERAGE_DIR: out };
    delete env.NODE_OPTIONS;
    const code = `const vm=require('node:vm');
        const source=${JSON.stringify(source)};
        const real={window:{}}; vm.runInNewContext(source,real); real.window.pick(true);
        const fake={window:{}}; vm.runInNewContext(source+'\\nwindow.injected=true;',fake,{filename:'example.js'}); fake.window.pick(false);`;
    const result = spawnSync(process.execPath, ['--require', path.join(__dirname, 'node-preload.cjs'), '-e', code], { env, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    const raw = JSON.parse(fs.readFileSync(path.join(out, fs.readdirSync(out)[0]), 'utf8'));
    assert.equal(raw.records.length, 1);
    assert.equal(raw.records[0].hash, hash(source));
    assert.ok(raw.records[0].functions.some(f => f.functionName === 'pick' && f.ranges[0].count === 1));
    assert.ok(raw.rejected > 0);
});

test('browser candidate filtering avoids unrelated bundles while preserving assets and configuration inline sources', async t => {
    const { root, source } = fixture(t), calls = [];
    const chrome = { async send(method, params) { calls.push({ method, params }); return { scriptSource: params.scriptId === 'inline' ? 'window.inline = true;' : source }; } };
    const collector = new BrowserCoverage(chrome, root, path.join(root, 'raw'));
    const state = { scripts: new Map(), pending: [], sourceMisses: 0 };
    collector.sessions.set('session', state);
    function parsed(scriptId, url, more = {}) {
        collector.event({ method: 'Debugger.scriptParsed', sessionId: 'session', params: { scriptId, url, startLine: 0, startColumn: 0, ...more } });
    }
    parsed('vendor', 'http://localhost/web/jellyfin.bundle.js');
    await collector.drain();
    assert.equal(calls.length, 0, 'unrelated bundle source is never fetched');
    parsed('asset', 'http://localhost/Babelfin/assets/example.js?v=1');
    parsed('anonymous', '');
    parsed('inline', 'http://localhost/web/index.html#/configurationpage?name=Babelfin', { startLine: 51 });
    await collector.drain();
    assert.equal(calls.length, 3);
    assert.equal(state.scripts.get('asset'), hash(source));
    assert.equal(state.scripts.get('anonymous'), hash(source));
    assert.equal(state.scripts.get('inline'), hash('window.inline = true;'));
});

test('concurrent tab close and navigation cannot invalidate an in-flight coverage snapshot', async t => {
    const { root } = fixture(t);
    const { Chrome, Page } = require('../rig/e2e/cdp');
    const chrome = new Chrome();
    const collector = new BrowserCoverage(chrome, root, path.join(root, 'raw'));
    chrome.coverage = collector;
    let active = 0, maximum = 0;
    const violations = [], operations = [];
    const pages = [0, 1, 2].map(i => {
        const page = new Page(chrome, 'target-' + i, 'session-' + i);
        chrome.pages.set(page.sessionId, page);
        collector.sessions.set(page.sessionId, { targetId: page.targetId, scripts: new Map(), pending: [], sourceMisses: 0 });
        return page;
    });
    chrome.send = async (method, params, sessionId) => {
        if (method === 'Profiler.takePreciseCoverage') {
            active++; maximum = Math.max(maximum, active);
            await new Promise(resolve => setTimeout(resolve, 5));
            active--; return { result: [] };
        }
        if (['Target.closeTarget', 'Page.navigate', 'Page.reload'].includes(method)) {
            if (active) violations.push(method + ' overlaps ' + active + ' pending snapshots');
            operations.push(method);
            if (method === 'Target.closeTarget') {
                for (const [id, state] of collector.sessions) if (state.targetId === params.targetId) collector.sessions.delete(id);
            } else chrome.pages.get(sessionId).dispatch('Page.loadEventFired', {});
            return { loaderId: 'loaded' };
        }
        return {};
    };
    await Promise.all([chrome.closePage(pages[0]), pages[1].navigate('http://127.0.0.1/fixture'), pages[2].reload()]);
    assert.deepEqual(violations, [], 'a target operation must not race a snapshot of any live target');
    assert.equal(maximum, 1, 'V8 snapshots are coordinated across concurrent tabs');
    assert.deepEqual(operations.sort(), ['Page.navigate', 'Page.reload', 'Target.closeTarget']);
    assert.deepEqual(collector.errors, []);
});

test('a failed queued operation remains rejected without poisoning the next snapshot', async t => {
    const { root } = fixture(t);
    const collector = new BrowserCoverage({ async send() { return { result: [] }; } }, root, path.join(root, 'raw'));
    await assert.rejects(collector.snapshotThen(() => { throw new Error('operation failed'); }), /operation failed/);
    let continued = false;
    await collector.snapshotThen(() => { continued = true; });
    assert.equal(continued, true);
    assert.deepEqual(collector.errors, []);
});

test('browser hook collects real assets from tabs and frames and drops altered responses', { skip: !process.env.BF_COVERAGE_BROWSER_TESTS }, async t => {
    const { root, source } = fixture(t), out = path.join(root, 'raw');
    const http = require('node:http');
    const server = http.createServer((req, res) => {
        if (req.url.endsWith('.js')) {
            res.setHeader('content-type', 'text/javascript');
            res.end(source + (req.url === '/mutated/example.js' ? '\nwindow.extra=true;' : ''));
        } else {
            res.setHeader('content-type', 'text/html');
            res.end('<script src="/example.js"></script><script>window.pick(true)</script>' +
                (req.url === '/' ? '<iframe src="http://localhost:' + server.address().port + '/frame"></iframe><script src="/mutated/example.js"></script>' : ''));
        }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    t.after(() => new Promise(resolve => server.close(resolve)));
    const oldDir = process.env.BF_COVERAGE_DIR, oldRoot = process.env.BF_COVERAGE_ROOT;
    process.env.BF_COVERAGE_DIR = out; process.env.BF_COVERAGE_ROOT = root;
    t.after(() => {
        if (oldDir === undefined) delete process.env.BF_COVERAGE_DIR; else process.env.BF_COVERAGE_DIR = oldDir;
        if (oldRoot === undefined) delete process.env.BF_COVERAGE_ROOT; else process.env.BF_COVERAGE_ROOT = oldRoot;
    });
    const { Chrome } = require('../rig/e2e/cdp');
    const chrome = await Chrome.launch();
    t.after(() => chrome.killNow());
    const first = await chrome.firstPage();
    await first.navigate('http://127.0.0.1:' + server.address().port + '/');
    const second = await chrome.newPage();
    await second.navigate('http://127.0.0.1:' + server.address().port + '/second');
    await second.reload();
    const parallel = await Promise.all(Array.from({ length: 6 }, () => chrome.newPage()));
    await Promise.all(parallel.map(page => page.navigate('http://127.0.0.1:' + server.address().port + '/parallel')));
    await Promise.all(parallel.map(page => page.evaluate(() => window.pick(false))));
    // Match the full dropdown sweep: several tabs complete/close together while
    // navigation and reload in other tabs also request coverage snapshots.
    let deadline;
    try {
        await Promise.race([
            Promise.all([...parallel.map(page => chrome.closePage(page)), second.reload()]),
            new Promise((_, reject) => { deadline = setTimeout(() => reject(new Error('parallel coverage lifecycle stalled')), 10000); })
        ]);
    } finally { clearTimeout(deadline); }
    await chrome.closePage(second);
    await chrome.close();
    assert.ok(fs.existsSync(out), 'opt-in hook writes browser evidence');
    const raw = JSON.parse(fs.readFileSync(path.join(out, fs.readdirSync(out)[0]), 'utf8'));
    assert.deepEqual(raw.errors, []);
    const called = raw.records.filter(r => r.functions.some(f => f.functionName === 'pick' && f.ranges[0].count > 0));
    assert.ok(called.length >= 10, 'main tab, cross-site frame, sequential reloads and six concurrent tabs: ' + JSON.stringify(raw.records.map(r => ({ url: r.url, functions: r.functions }))));
    assert.ok(called.some(r => new URL(r.url).hostname === 'localhost'), 'cross-site iframe has positive execution evidence independently of the other tabs');
    assert.ok(raw.records.every(r => r.hash === hash(source)));
    assert.ok(raw.rejected > 0, 'test scripts and mutated response rejected');
});
