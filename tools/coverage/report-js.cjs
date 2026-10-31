'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const { inventory, zeroCounters } = require('./source-map');
const dependencies = createRequire(path.join(path.resolve(process.argv[2]), 'package.json'));
const v8toIstanbul = dependencies('v8-to-istanbul');
const { createCoverageMap } = dependencies('istanbul-lib-coverage');
const { createContext } = dependencies('istanbul-lib-report');
const reports = dependencies('istanbul-reports');
async function main() {
    const [, , , root, raw, out] = process.argv;
    const sources = inventory(root), map = createCoverageMap({}), seen = new Set(), origins = new Map(), acceptedByKind = {};
    let accepted = 0, rejected = 0, prefiltered = 0, errors = [];
    for (const file of fs.readdirSync(raw).filter(f => f.endsWith('.json'))) {
        const data = JSON.parse(fs.readFileSync(path.join(raw, file), 'utf8'));
        if (data.error) errors.push(data.error);
        errors.push(...(data.errors || []));
        rejected += data.rejected || 0;
        prefiltered += data.prefiltered || 0;
        for (const record of data.records || []) {
            const source = sources.get(record.hash);
            if (!source) { rejected++; continue; }
            // Exact source hashes determine identity, never a claimed filename/URL.
            const converter = v8toIstanbul(source.file, 0, { source: source.source });
            await converter.load();
            converter.applyCoverage(record.functions);
            map.merge(converter.toIstanbul()); seen.add(source.hash); accepted++;
            const kind = data.kind || 'unknown';
            if (!origins.has(source.hash)) origins.set(source.hash, new Set());
            origins.get(source.hash).add(kind);
            acceptedByKind[kind] = (acceptedByKind[kind] || 0) + 1;
        }
    }
    for (const source of sources.values()) if (!seen.has(source.hash)) {
        const converter = v8toIstanbul(source.file, 0, { source: source.source });
        await converter.load();
        converter.applyCoverage([{ functionName: '(unloaded)', isBlockCoverage: true,
            ranges: [{ startOffset: 0, endOffset: source.source.length, count: 0 }] }]);
        map.merge(zeroCounters(converter.toIstanbul()));
    }
    fs.mkdirSync(out, { recursive: true });
    const context = createContext({ dir: out, coverageMap: map, sourceFinder: file => [...sources.values()].find(s => s.file === file).source });
    for (const name of ['json', 'json-summary', 'text', 'html', 'lcovonly']) reports.create(name).execute(context);
    const provenance = { accepted, accepted_by_kind: acceptedByKind, rejected, prefiltered, errors,
        all_production_sources_observed: seen.size === sources.size,
        runtime_function_branch_denominators_are_exhaustive: false,
        denominator_note: 'Functions and branch/block ranges are reported by V8 at runtime, not an exhaustive syntactic census. Unloaded sources have all lines at zero and one synthetic function/block; their functions and branches are unknown.',
        sources: [...sources.values()].map(s => ({ file: path.relative(root, s.file).replaceAll('\\', '/'), hash: s.hash, observed: seen.has(s.hash), observed_by: [...(origins.get(s.hash) || [])] })) };
    fs.writeFileSync(path.join(out, 'provenance.json'), JSON.stringify(provenance, null, 2), 'utf8');
    if (errors.length) process.exitCode = 1;
}
main().catch(error => { console.error(error); process.exitCode = 1; });
