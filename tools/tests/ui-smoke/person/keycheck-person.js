'use strict';
/* Checks the shipped person dictionaries against literal keys and the exact dynamic families used by the web clients.
   Advanced field labels, help, explanations and enum options come from everydaySelect; item badges come from itemSearch;
   dialog explanations follow the target's show/movie/video type, with or without a library. English client fallbacks must match.
   Run: node tools/tests/ui-smoke/person/keycheck-person.js */
const fs = require('fs');
const vm = require('vm');
const A = require('../shared/target').getAssetsPath();
const en = JSON.parse(fs.readFileSync(A + 'i18n/en.json', 'utf8'));
const scripts = fs.readdirSync(A).filter((f) => f.endsWith('.js'));

/** A script's text without its comments: block comments, whole-line comments and trailing comments after code with balanced quotes. */
function withoutComments(text) {
    return text.replace(/\/\*[\s\S]*?\*\//g, '').split('\n').map((line) => {
        if (/^\s*\/\//.test(line)) { return ''; }
        const at = line.search(/\s\/\/\s/);
        if (at < 0) { return line; }
        const before = line.slice(0, at);
        const quotes = (before.match(/(?<!\\)'/g) || []).length;
        return quotes % 2 === 0 && before.indexOf('`') < 0 ? before : line;
    }).join('\n');
}
const code = scripts.map((f) => withoutComments(fs.readFileSync(A + f, 'utf8'))).join('\n');
const app = fs.readFileSync(A + 'app.js', 'utf8');

const sandbox = { window: {}, document: undefined };
sandbox.window = sandbox;
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(A + 'ui.js', 'utf8'), sandbox);
const UI = sandbox.BabelfinUI;

const used = new Set();
for (const m of code.matchAll(/['"]([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9-]+)+)['"]/g)) { used.add(m[1]); }
const dynamic = new Set();
const fieldsShown = [...new Set([...app.matchAll(/everydaySelect\('(\w+)'/g)].map((m) => m[1]))];
fieldsShown.forEach((name) => {
    const field = UI.FIELDS.find((f) => f.name === name);
    if (!field) { console.log('unknown field ' + name); process.exitCode = 1; return; }
    dynamic.add('field.' + name + '.label');
    dynamic.add('field.' + name + '.info');
    if (('field.' + name + '.help') in en) { used.add('field.' + name + '.help'); }
    if (field.type === 'bool') { ['true', 'false'].forEach((v) => { if (('field.' + name + '.' + v) in en) { used.add('field.' + name + '.' + v); } }); }
    if (field.enumName) { UI.ENUMS[field.enumName].forEach((v) => dynamic.add('enum.' + field.enumName + '.' + v)); }
});
['Series', 'Season', 'Episode', 'Movie', 'BoxSet'].forEach((k) => dynamic.add('ui.itemType.' + k));
// The override dialog builds its applies-to key from the target's scope and type.
const FAMILIES = { 'dialog.applies.': ['show', 'movie', 'video', 'showNoLibrary', 'movieNoLibrary', 'videoNoLibrary'] };
Object.keys(FAMILIES).forEach((p) => FAMILIES[p].forEach((v) => dynamic.add(p + v)));
// audioChoiceInfo builds the original-language explanation as key + '.info'.
for (const key of ['ui.table.audioOriginal']) {
    if (!code.includes("'" + key + "'") || !code.includes("tr(t, key + '.info')")) {
        throw new Error('Audio choice info family is no longer built: ' + key);
    }
    dynamic.add(key + '.info');
}
require('../shared/problem-codes').forEach(code => dynamic.add('api.' + code));
dynamic.forEach((k) => used.add(k));
const unused = Object.keys(en).filter((k) => k !== '_machineTranslated' && !used.has(k));

// Literal keys in t()/tr() calls missing from en.json (person-side namespaces only; dashboard.js reads Assets/i18n-admin first and is
// checked by admin/keycheck.js, except the ui.* keys ui.js asks for, which are in the literal scan of ui.js).
const missing = [];
const personCode = ['ui.js', 'app.js', 'client.js'].map((f) => withoutComments(fs.readFileSync(A + f, 'utf8'))).join('\n');
const NAMESPACES = 'ui|app|languages|table|titles|advanced|burned|file|common|client|dialog|field|enum|restore|startOver|overrides|conflict|api';
for (const m of personCode.matchAll(new RegExp("(?:\\bt\\(|\\btr\\(t, |\\bT\\(|\\boptional\\()'((?:" + NAMESPACES + ")\\.[A-Za-z0-9.]*[A-Za-z0-9])'(?!\\s*\\+)", 'g'))) {
    if (!(m[1] in en)) { missing.push(m[1]); }
}
const NS2 = 'ui|titles|advanced|app|restore|startOver|overrides|conflict|api';
for (const m of personCode.matchAll(new RegExp("\\?\\s*'((?:" + NS2 + ")\\.[A-Za-z0-9.]+)'\\s*:\\s*'((?:" + NS2 + ")\\.[A-Za-z0-9.]+)'", 'g'))) {
    [m[1], m[2]].forEach((k) => { if (!(k in en)) { missing.push(k); } });
}
// Keys the scripts build as 'overrides.' + something or 'titles.' + something: none (a family would need listing here).
const familiesBuilt = [...new Set([...personCode.matchAll(new RegExp("'((?:" + NAMESPACES + ")\\.[A-Za-z0-9.]*)'\\s*\\+", 'g'))].map((m) => m[1]))];
// The families this checker knows (above, and the ones ui.js and app.js build and the lists at the top cover) and the ones nobody has looked at.
const KNOWN_FAMILIES = Object.keys(FAMILIES).concat(['ui.itemType.', 'enum.', 'field.', 'api.']);
const families = familiesBuilt.filter((p) => KNOWN_FAMILIES.indexOf(p) < 0);
const familiesGone = Object.keys(FAMILIES).filter((p) => familiesBuilt.indexOf(p) < 0);
const missingDynamic = [...dynamic].filter((k) => !(k in en));
const RETIRED = ['field.PrefersSdh.help', 'field.Format.help', 'field.UnlabelledAudio.label', 'ui.unlabelledLanguage', 'app.chooseLanguage',
    // Retired show/movie lists, per-track dialog controls and the My languages tip.
    'titles.title', 'titles.remembered.title', 'titles.remembered.confirm.title', 'titles.remembered.confirm.text', 'titles.remembered.removed',
    'titles.summary.audioLanguage', 'titles.summary.audioOriginal', 'titles.summary.audioMine', 'titles.summary.audioDub', 'titles.summary.audioVideo', 'titles.summary.audioFileDefault',
    'titles.summary.audioFirst', 'titles.summary.subtitles', 'titles.summary.subtitlesOff', 'titles.summary.subtitlesVideo', 'titles.summary.subtitlesFull',
    'titles.summary.subtitlesSigns', 'titles.summary.subtitlesSmart', 'titles.summary.subtitlesCustom', 'titles.summary.other',
    'dialog.applyTo', 'dialog.chain', 'dialog.custom', 'dialog.inherit', 'dialog.off', 'dialog.thisCollection', 'dialog.thisEpisode', 'dialog.thisMovie', 'dialog.thisSeason', 'dialog.wholeShow',
    'languages.info', 'dialog.burnedIn', 'client.chip.audio', 'client.chip.subtitles', 'client.chip.full', 'client.chip.partial', 'client.chip.sdh', 'client.chip.none'];
const REMOVED_KEYS = ["common.yes", "common.no", "common.add", "common.retry", "ui.plainText", "ui.chain.addStep", "ui.chain.empty", "ui.chain.stepKind", "ui.chain.language", "ui.chain.titleContains", "ui.chain.titleContains.placeholder", "field.AudioChain.label", "enum.AudioStepKind.Language", "enum.AudioStepKind.OriginalLanguage", "enum.AudioStepKind.LibraryUsualOriginalLanguage", "enum.AudioStepKind.MyLanguages", "enum.AudioStepKind.FileDefault", "enum.AudioStepKind.FirstTrack", "enum.AudioStepKind.VideoDefault", "enum.UnlabelledAudioRule.Unknown", "enum.UnlabelledAudioRule.OriginalLanguage", "enum.UnlabelledAudioRule.LibraryUsualOriginalLanguage", "enum.UnlabelledAudioRule.SpecificLanguage", "enum.UnlabelledAudioRule.OriginalWhenOnlyTrack"];
RETIRED.push(...REMOVED_KEYS);
const retiredLeft = RETIRED.filter((k) => k in en);
const retiredUsed = RETIRED.filter((k) => used.has(k));

// client.js's English fallback table (DEFAULTS) against en.json.
const clientSource = fs.readFileSync(A + 'client.js', 'utf8');
const start = clientSource.indexOf('var DEFAULTS = {');
const end = clientSource.indexOf('\n    };', start);
let defaults = null;
try {
    defaults = vm.runInNewContext('(' + clientSource.slice(start + 'var DEFAULTS = '.length, end + 7).replace(/;\s*$/, '') + ')');
} catch (e) {
    console.log('client.js DEFAULTS could not be read:', e.message);
}
const defaultsDiffer = defaults ? Object.keys(defaults).filter((k) => en[k] !== defaults[k]) : ['(unreadable)'];
// Every requested key needs DEFAULTS too: even English translation files can fail to load.
const clientAsks = [...withoutComments(clientSource).matchAll(/\bT\('((?:client|dialog|common)\.[A-Za-z0-9.]+)'/g)].map((m) => m[1]);
const familyKeys = Object.keys(FAMILIES).flatMap((p) => FAMILIES[p].map((v) => p + v))
    .concat(['dialog.burnedIn.show', 'dialog.burnedIn.movie', 'dialog.burnedIn.video',
        'ui.default', 'ui.defaultValue', 'ui.table.videoDefault', 'ui.table.off', 'ui.table.smart',
        'ui.table.signs', 'ui.table.myLanguages', 'ui.table.allLanguages', 'ui.table.custom',
        'ui.table.audioMyLanguages', 'ui.table.audioOriginal', 'ui.table.audioOriginal.info']);
const clientNoWords = defaults
    ? [...new Set(clientAsks)].filter((k) => Object.keys(FAMILIES).indexOf(k) < 0 && !(k in defaults)).concat(familyKeys.filter((k) => !(k in en) || !(k in defaults)))
    : ['(unreadable)'];

console.log('fields shown in Advanced:', fieldsShown.join(', '));
console.log('unused (' + unused.length + '):', JSON.stringify(unused));
console.log('missing literal:', [...new Set(missing)]);
console.log('missing dynamic:', missingDynamic);
console.log('families built that this checker does not know:', families, '; known families no longer built:', familiesGone);
console.log('retired keys still in en.json:', retiredLeft);
console.log('retired keys a script still uses:', retiredUsed);
console.log('client.js DEFAULTS that differ from en.json:', defaultsDiffer);
console.log('client.js keys with no words anywhere:', clientNoWords);
console.log('keys:', Object.keys(en).length);
process.exit(unused.length || missing.length || missingDynamic.length || retiredLeft.length || retiredUsed.length || families.length || familiesGone.length
    || defaultsDiffer.length || clientNoWords.length ? 1 : 0);
