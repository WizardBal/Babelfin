'use strict';
// Opt-in transport observer. Its auto-attached sessions are independent of the UI driver's sessions.
const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');
const { inventory, hash } = require('./source-map');
class BrowserCoverage {
    constructor(chrome, root, out) {
        this.chrome = chrome; this.out = out; this.sources = inventory(root);
        this.assetNames = new Set([...this.sources.values()].map(s => path.basename(s.file).toLowerCase()));
        this.sessions = new Map(); this.jobs = new Set(); this.errors = []; this.records = [];
        this.rejected = 0; this.prefiltered = 0; this.snapshotTail = Promise.resolve();
    }
    track(promise) {
        const job = promise.catch(error => this.errors.push(error.message));
        this.jobs.add(job); job.finally(() => this.jobs.delete(job));
        return job;
    }
    async start() {
        await this.chrome.send('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: true, flatten: true,
            filter: [{ type: 'page' }, { type: 'iframe' }, { exclude: true }] });
        await this.drain();
    }
    async drain() { while (this.jobs.size) await Promise.all([...this.jobs]); }
    event(message) {
        const p = message.params || {};
        if (message.method === 'Target.attachedToTarget') {
            this.track(this.attach(p));
            return;
        }
        if (message.method === 'Target.detachedFromTarget') {
            this.sessions.delete(p.sessionId);
            return;
        }
        const state = this.sessions.get(message.sessionId);
        if (!state) return;
        if (message.method === 'Debugger.scriptParsed') {
            // URL is only a cheap candidate filter. Inline/eval code still needs
            // its source fetched because the configuration bootstrap has no asset URL.
            let candidate = !p.url || p.startLine > 0 || p.startColumn > 0 || p.hasSourceURL;
            if (!candidate) {
                try {
                    const url = new URL(p.url);
                    const basename = decodeURIComponent(url.pathname.split('/').pop());
                    candidate = this.assetNames.has(basename.toLowerCase()) || !/\.m?js$/i.test(basename);
                } catch { candidate = true; }
            }
            if (!candidate) {
                state.scripts.set(p.scriptId, null); this.prefiltered++;
                this.record(state, []);
                return;
            }
            this.track(this.chrome.send('Debugger.getScriptSource', { scriptId: p.scriptId }, message.sessionId).then(data => {
                const key = hash(data.scriptSource);
                if (this.sources.has(key)) state.scripts.set(p.scriptId, key);
                else { state.scripts.set(p.scriptId, null); this.rejected++; }
                this.record(state, []);
            }).catch(error => {
                // A destroyed context can lose source; never infer its identity from its URL.
                state.sourceMisses++; this.errors.push('script source unavailable: ' + error.message);
            }));
        } else if (message.method === 'Profiler.preciseCoverageDeltaUpdate') {
            this.record(state, p.result || []);
        }
    }
    async attach(p) {
        if (this.sessions.has(p.sessionId)) return;
        // The UI driver attaches its own session too. Profiling the same isolate
        // twice resets V8 counters, so retain exactly one collector per target.
        if ([...this.sessions.values()].some(s => s.targetId === p.targetInfo.targetId)) {
            if (p.waitingForDebugger) await this.chrome.send('Runtime.runIfWaitingForDebugger', {}, p.sessionId);
            return;
        }
        const state = { targetId: p.targetInfo.targetId, scripts: new Map(), pending: [], sourceMisses: 0 };
        this.sessions.set(p.sessionId, state);
        try {
            await this.chrome.send('Debugger.enable', {}, p.sessionId);
            await this.chrome.send('Profiler.enable', {}, p.sessionId);
            await this.chrome.send('Profiler.startPreciseCoverage', { callCount: true, detailed: true, allowTriggeredUpdates: true }, p.sessionId);
            await this.chrome.send('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: true, flatten: true,
                filter: [{ type: 'iframe' }, { exclude: true }] }, p.sessionId);
        } finally {
            await this.chrome.send('Runtime.runIfWaitingForDebugger', {}, p.sessionId);
        }
    }
    record(state, scripts) {
        const waiting = state.pending.concat(scripts);
        state.pending = [];
        for (const script of waiting) {
            if (!state.scripts.has(script.scriptId)) { state.pending.push(script); continue; }
            const key = state.scripts.get(script.scriptId), source = this.sources.get(key);
            if (source) this.records.push({ ...script, hash: key, file: source.file });
        }
    }
    snapshotThen(action, targetId) {
        const operation = this.snapshotTail.then(async () => {
            await this.collect(targetId);
            return action();
        });
        // A failed operation is returned to its caller, but cannot poison the
        // queue and prevent another tab from being captured or closed.
        this.snapshotTail = operation.catch(() => {});
        return operation;
    }
    take(targetId) { return this.snapshotThen(() => {}, targetId); }
    async collect(targetId) {
        await this.drain();
        for (const [sessionId, state] of this.sessions) {
            if (targetId && state.targetId !== targetId) continue;
            try {
                const data = await this.chrome.send('Profiler.takePreciseCoverage', {}, sessionId);
                await this.drain();
                this.record(state, data.result);
            } catch (error) { this.errors.push('coverage snapshot unavailable: ' + error.message); }
        }
        this.flush();
    }
    flush() {
        fs.mkdirSync(this.out, { recursive: true });
        if (!this.file) this.file = path.join(this.out, 'browser-' + process.pid + '-' + randomUUID() + '.json');
        fs.writeFileSync(this.file, JSON.stringify({ kind: 'browser', records: this.records, rejected: this.rejected, prefiltered: this.prefiltered, errors: this.errors }), 'utf8');
    }
}
module.exports = { BrowserCoverage };
