'use strict';
/* Which keys of Assets/i18n-admin/en.json dashboard.js and configPage.html use: literal t('key') calls, data-i18n attributes, and the
   key families dashboard.js builds ('apps.mode.' + mode, 'tracknames.' + list, 'kind.' + subtitle kind,
   'activityKind.' + kind, 'backup.matched.' / 'backup.action.' + the server's answer). Prints the keys used but missing and the keys no
   one uses. Run: node keycheck.js
   The infoTips' words are literal t('….info') calls; BabelfinUI.infoTip names its "i" button with ui.info.about,
   which the page's tips read from Assets/i18n (S.uiT), so that key must be in Assets/i18n/en.json.
   The People editor's "Show and movie overrides" words (overrides.title, .helpAdmin, .removed, .removeAll.done and
   .removeAll.confirmTextFor, which takes the person's name) are shared words too: uiWord('…') and S.uiT('…', { … }) with parameters count. */
const fs = require('fs');
const path = require('path');
const assets = require('../shared/target').getAssetsPath();
const root = path.resolve(assets, '..') + path.sep;
const en = JSON.parse(fs.readFileSync(root + 'Assets/i18n-admin/en.json', 'utf8'));
const js = fs.readFileSync(root + 'Assets/dashboard.js', 'utf8');
const shared = fs.readFileSync(root + 'Assets/ui.js', 'utf8');
const html = fs.readFileSync(root + 'Configuration/configPage.html', 'utf8');
const used = new Set();
for (const m of js.matchAll(/(?<![A-Za-z])t\('([a-zA-Z][a-zA-Z0-9._-]*)'(?!\s*\+)/g)) { used.add(m[1]); }
// t(cond ? 'a' : 'b')
for (const m of js.matchAll(/(?<![A-Za-z])t\([^()']*\?\s*'([a-zA-Z][a-zA-Z0-9._-]*)'\s*:\s*'([a-zA-Z][a-zA-Z0-9._-]*)'/g)) { used.add(m[1]); used.add(m[2]); }
for (const m of html.matchAll(/data-i18n="([^"]+)"/g)) { used.add(m[1]); }
const dyn = {
    'apps.mode.': ['Trust', 'ReplaceOnAutoplay', 'ReplaceUnlessManual'],
    'tracknames.': ['PartialWords', 'SdhWords', 'FullWords', 'CommentaryWords', 'AudioDescriptionWords'],
    'kind.': ['full', 'partial', 'sdh'],
    'activityKind.': ['override-created', 'manual-trusted', 'correction-sent', 'settings-changed', 'jellyfin-setting-changed', 'restored'],
    'backup.matched.': ['id', 'name'],
    'backup.action.': ['create', 'update', 'skip']
};
for (const p in dyn) {
    if (js.indexOf("'" + p + "'") < 0) { console.log('family no longer built: ' + p); process.exitCode = 1; }
    dyn[p].forEach((s) => used.add(p + s));
}
for (const m of shared.matchAll(/tr\(o?\.?t, '(conflict\.[^']+)'|\bt\('(conflict\.[^']+)'/g)) { used.add(m[1] || m[2]); }
require('../shared/problem-codes').forEach(code => used.add('api.' + code));
const missing = [...used].filter((k) => !(k in en));
// the shared words dashboard.js reads from the person's files (uiWord('...') and S.uiT('...')) must be in Assets/i18n/en.json.
const enUi = JSON.parse(fs.readFileSync(root + 'Assets/i18n/en.json', 'utf8'));
const uiUsed = new Set();
for (const m of js.matchAll(/(?:uiWord|S\.uiT)\('([a-zA-Z][a-zA-Z0-9._-]*)'\s*[,)]/g)) { uiUsed.add(m[1]); }
if (uiUsed.size < 6) { console.log('expected the shared words (My languages, the table words), found ' + uiUsed.size); process.exitCode = 1; }
[...uiUsed].filter((k) => !(k in enUi)).forEach((k) => missing.push('i18n/' + k));
if (js.indexOf('infoTip(') >= 0 && !('ui.info.about' in enUi)) { missing.push('i18n/ui.info.about'); }
console.log('shared words from Assets/i18n:', [...uiUsed].join(', '));
const unused = Object.keys(en).filter((k) => k !== '_machineTranslated' && !used.has(k));
console.log('missing:', missing);
console.log('unused (' + unused.length + '):', JSON.stringify(unused));
console.log('count', Object.keys(en).length);
process.exit(missing.length || unused.length ? 1 : (process.exitCode || 0));
