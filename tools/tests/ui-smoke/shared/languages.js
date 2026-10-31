'use strict';
/* A realistic GET Babelfin/Languages answer, built from the server's language table source (IsoLanguages.g.cs + IsoLanguages.Extra.cs):
   Code = (Variety ?? Base) + -Script + -Region, Base = the two-letter code when there is one, else the three-letter one.
   Used by the person and admin smoke tests for the language-list rule. */
const fs = require('fs');
const path = require('path');
const DIR = path.resolve(require('./target').getAssetsPath(), '../Engine/Languages') + path.sep;
function parse(text) {
    const out = [];
    const re = /new LanguageEntry\(([^\n]*)\),?\s*(?:\/\/[^\n]*)?\n/g;
    let m;
    while ((m = re.exec(text))) {
        const args = [];
        const named = {};
        const argRe = /(\w+:\s*)?(null|"(?:[^"\\]|\\.)*")/g;
        let a;
        let i = 0;
        while ((a = argRe.exec(m[1]))) {
            const value = a[2] === 'null' ? null : JSON.parse(a[2]);
            if (a[1]) { named[a[1].replace(/[:\s]/g, '')] = value; } else { args[i++] = value; }
        }
        const pick = (name, index) => (named[name] !== undefined ? named[name] : (args[index] !== undefined ? args[index] : null));
        const two = args[0];
        const three = args[1];
        const variety = pick('Variety', 4);
        const script = pick('Script', 5);
        const region = pick('Region', 6);
        const base = two || three;
        const code = (variety || base) + (script ? '-' + script : '') + (region ? '-' + region : '');
        out.push({ Code: code, Name: args[3], Base: base, Variety: variety, Script: script, Region: region });
    }
    return out;
}
const all = parse(fs.readFileSync(DIR + 'IsoLanguages.g.cs', 'utf8')).concat(parse(fs.readFileSync(DIR + 'IsoLanguages.Extra.cs', 'utf8')));
const seen = new Set();
const list = all.filter((l) => { const k = l.Code.toLowerCase(); if (seen.has(k)) { return false; } seen.add(k); return true; });
module.exports = list;
if (require.main === module) {
    const plain = list.filter((l) => l.Code === l.Base && /^[a-z]{2}$/i.test(l.Base));
    console.log(list.length + ' languages, ' + plain.length + ' plain two-letter');
    console.log('not plain: ' + list.filter((l) => l.Code !== l.Base).map((l) => l.Code).join(' '));
}
