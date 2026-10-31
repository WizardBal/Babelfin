'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const hash = text => createHash('sha256').update(text, 'utf8').digest('hex');
function inventory(root) {
    const result = new Map();
    const dir = path.join(root, 'plugin/src/Jellyfin.Plugin.Babelfin/Assets');
    function files(folder) {
        return fs.readdirSync(folder, { withFileTypes: true }).flatMap(entry =>
            entry.isDirectory() ? files(path.join(folder, entry.name)) : [path.join(folder, entry.name)]);
    }
    for (const file of files(dir).filter(name => name.endsWith('.js'))) {
        const source = fs.readFileSync(file, 'utf8');
        result.set(hash(source), { file, source, hash: hash(source) });
    }
    // Both standalone and configuration bootstraps execute production JavaScript.
    // Keep inline scripts separate from HTML and include unloaded bootstraps at zero.
    const htmlFiles = files(dir).filter(name => name.endsWith('.html'))
        .concat(files(path.join(root, 'plugin/src/Jellyfin.Plugin.Babelfin/Configuration')).filter(name => name.endsWith('.html')));
    for (const htmlFile of htmlFiles) {
        const html = fs.readFileSync(htmlFile, 'utf8');
        let index = 0;
        for (const match of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script\s*>/gi)) {
            if (/\bsrc\s*=/i.test(match[1]) || !match[2].trim()) continue;
            const type = /\btype\s*=\s*["']([^"']*)["']/i.exec(match[1]);
            if (type && !/^(?:module|(?:text|application)\/(?:java|ecma)script)$/i.test(type[1])) continue;
            const source = match[2], file = htmlFile + '.inline-' + (++index) + '.js';
            result.set(hash(source), { file, source, hash: hash(source) });
        }
    }
    return result;
}
// Unloaded sources have no evidence of execution. Do not let converter defaults
// mark blank/wrapper lines as hit while constructing their zero-denominator maps.
function zeroCounters(files) {
    for (const file of Object.values(files)) {
        for (const key of Object.keys(file.s)) file.s[key] = 0;
        for (const key of Object.keys(file.f)) file.f[key] = 0;
        for (const key of Object.keys(file.b)) file.b[key] = file.b[key].map(() => 0);
    }
    return files;
}
module.exports = { hash, inventory, zeroCounters };
