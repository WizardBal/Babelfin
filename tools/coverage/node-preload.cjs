'use strict';
// Inspector sees the actual compiled VM text, including unnamed scripts. No VM rewriting.
const inspector = require('node:inspector');
const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');
const { hash, inventory } = require('./source-map');
if (process.env.BF_COVERAGE_DIR && process.env.BF_COVERAGE_ROOT) {
    const sources = inventory(process.env.BF_COVERAGE_ROOT);
    const session = new inspector.Session();
    session.connect();
    const accepted = new Map();
    let rejected = 0;
    session.on('Debugger.scriptParsed', ({ params }) => {
        session.post('Debugger.getScriptSource', { scriptId: params.scriptId }, (error, data) => {
            if (error || !data) return;
            const key = hash(data.scriptSource), source = sources.get(key);
            if (source) accepted.set(params.scriptId, { hash: key, file: source.file });
            else rejected++;
        });
    });
    session.post('Debugger.enable');
    session.post('Profiler.enable');
    session.post('Profiler.startPreciseCoverage', { callCount: true, detailed: true });
    process.once('exit', () => {
        session.post('Profiler.takePreciseCoverage', (error, data) => {
            const records = [];
            for (const script of data?.result || []) {
                const source = accepted.get(script.scriptId);
                if (source) records.push({ ...script, ...source });
            }
            fs.mkdirSync(process.env.BF_COVERAGE_DIR, { recursive: true });
            fs.writeFileSync(path.join(process.env.BF_COVERAGE_DIR, 'node-' + process.pid + '-' + randomUUID() + '.json'),
                JSON.stringify({ kind: 'node', error: error?.message, rejected, records }), 'utf8');
        });
        session.disconnect();
    });
}
