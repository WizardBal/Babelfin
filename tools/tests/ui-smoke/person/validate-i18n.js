const fs = require('fs');
const ASSETS = require('../shared/target').getAssetsPath();
const A = ASSETS + 'i18n/';
const ADMIN = ASSETS + 'i18n-admin/';
let bad = 0;
const fail = (m) => { bad++; console.log('FAIL ' + m); };
const enBuf = fs.readFileSync(A + 'en.json');
const en = JSON.parse(enBuf.toString('utf8'));
const enKeys = Object.keys(en);
if (enKeys.some(k => /myLanguages.*\.info$/i.test(k))) { fail('My languages cannot have a tooltip'); }
const ph = (s) => (typeof s === 'string' ? (s.match(/\{[A-Za-z]+\}/g) || []).sort().join(',') : '');
const files = fs.readdirSync(A).filter((f) => f.endsWith('.json'));
for (const f of files) {
    const buf = fs.readFileSync(A + f);
    if (buf[0] === 0xEF && buf[1] === 0xBB && buf[2] === 0xBF) { fail(f + ' has a BOM'); }
    let j;
    try { j = JSON.parse(buf.toString('utf8')); } catch (e) { fail(f + ' invalid JSON ' + e.message); continue; }
    const keys = Object.keys(j);
    if (keys.join('|') !== enKeys.join('|')) { fail(f + ' key list/order differs: missing ' + enKeys.filter((k) => !(k in j)).join(' ') + ' extra ' + keys.filter((k) => !(k in en)).join(' ')); }
    for (const k of enKeys) {
        if (ph(j[k]) !== ph(en[k])) { fail(f + ' ' + k + ' placeholders ' + ph(j[k]) + ' vs ' + ph(en[k])); }
        if (k !== '_machineTranslated' && (typeof j[k] !== 'string')) { fail(f + ' ' + k + ' not a string'); }
        if (typeof j[k] === 'string' && /!/.test(j[k])) { fail(f + ' ' + k + ' has "!"'); }
        if (typeof j[k] === 'string' && /\p{Extended_Pictographic}/u.test(j[k])) { fail(f + ' ' + k + ' has an emoji'); }
    }
    if (f !== 'en.json' && j._machineTranslated !== true) { fail(f + ' _machineTranslated is not true'); }
}
if (fs.readFileSync(A + 'pt.json', 'utf8') !== fs.readFileSync(A + 'pt-PT.json', 'utf8')) { fail('pt.json differs from pt-PT.json'); }
if (fs.readFileSync(A + 'zh.json', 'utf8') !== fs.readFileSync(A + 'zh-Hans.json', 'utf8')) { fail('zh.json differs from zh-Hans.json'); }
for (const k of ['dialog.fetchNow', 'dialog.fetchLater', 'dialog.fetchQueued', 'languages.understood', 'advanced.reset', 'file.reset', 'ui.value.none']) { if (k in en) { fail('still has ' + k); } }
// Understood is "My languages" everywhere.
for (const k of Object.keys(en)) { if (typeof en[k] === 'string' && /languages I understand|languages understood|understands/i.test(en[k])) { fail('en.json ' + k + ' uses an old wording for My languages'); } }

// The old "Shows and movies" list, the old per-track item dialog and the My languages tip are gone from every file,
// and the override words are in every file (placeholders were compared with en.json above), translated.
const RETIRED = ['titles.title', 'titles.remembered.title', 'titles.remembered.confirm.title', 'titles.remembered.confirm.text', 'titles.remembered.removed',
    'titles.summary.audioLanguage', 'titles.summary.audioOriginal', 'titles.summary.audioMine', 'titles.summary.audioDub', 'titles.summary.audioVideo', 'titles.summary.audioFileDefault',
    'titles.summary.audioFirst', 'titles.summary.subtitles', 'titles.summary.subtitlesOff', 'titles.summary.subtitlesVideo', 'titles.summary.subtitlesFull',
    'titles.summary.subtitlesSigns', 'titles.summary.subtitlesSmart', 'titles.summary.subtitlesCustom', 'titles.summary.other',
    'dialog.applyTo', 'dialog.chain', 'dialog.custom', 'dialog.inherit', 'dialog.off', 'dialog.thisCollection', 'dialog.thisEpisode', 'dialog.thisMovie', 'dialog.thisSeason', 'dialog.wholeShow',
    'languages.info', 'dialog.burnedIn', 'client.chip.audio', 'client.chip.subtitles', 'client.chip.full', 'client.chip.partial', 'client.chip.sdh', 'client.chip.none'];
const NEW_KEYS = ['dialog.burnedIn.show', 'dialog.burnedIn.movie', 'dialog.burnedIn.video', 'client.menu.override', 'dialog.applies.show', 'dialog.applies.movie', 'dialog.applies.showNoLibrary', 'dialog.applies.movieNoLibrary', 'dialog.narrower', 'dialog.remove', 'dialog.removed', 'dialog.errorBurnedIn']
    .concat(['overrides.title', 'overrides.help', 'overrides.helpAdmin', 'overrides.empty', 'overrides.col.item', 'overrides.col.library', 'overrides.col.audio', 'overrides.col.subtitles', 'overrides.remembered',
        'overrides.noLibrary', 'overrides.notSet', 'overrides.removeFor', 'overrides.removeAll', 'overrides.removeAll.confirmTitle', 'overrides.removeAll.confirmText', 'overrides.removeAll.confirmTextFor',
        'overrides.removeAll.done', 'overrides.removed'])
    .concat(['ui.table.audioMyLanguages', 'ui.table.audioOriginal', 'ui.table.audioOriginal.info',
        'ui.genres.title', 'ui.genres.add', 'ui.genres.choose', 'ui.genres.empty', 'ui.genres.firstMatch', 'ui.genres.none',
        'ui.genres.loadError', 'ui.genres.retry', 'ui.genres.limit', 'ui.genres.duplicate', 'app.noChoices']);
for (const f of files) {
    const j = JSON.parse(fs.readFileSync(A + f, 'utf8'));
    RETIRED.filter((k) => k in j).forEach((k) => fail(f + ' still has the retired key ' + k));
    NEW_KEYS.filter((k) => typeof j[k] !== 'string' || !j[k].trim()).forEach((k) => fail(f + ' has no ' + k));
    // The same in the placeholders that matter, and not left in English (Audio may read the same).
    if (f !== 'en.json') { NEW_KEYS.filter((k) => !['overrides.col.audio', 'ui.genres.title'].includes(k) && j[k] === en[k]).forEach((k) => fail(f + ' ' + k + ' is still English')); }
    if (typeof j['overrides.removeAll.confirmTextFor'] === 'string' && j['overrides.removeAll.confirmTextFor'].indexOf('{name}') < 0) { fail(f + ' overrides.removeAll.confirmTextFor has no {name}'); }
}

// No UI text describes the person with "understand" or "read" ("I understand", "you read", "languages you read", ...).
// English: every string of both English files, except pinned messages about track titles, a file, and stored settings.
// Other languages: a conservative scan for the same verbs, with a narrow admin-only exception for the stored-settings message.
const ENGLISH_RULE = /\b(understand|understands|understanding|understood|read|reads|reading|reader|readers|readable)\b/i;
const ALLOWED = [
    ['tracknames.help', 'Babelfin reads track titles to tell what kind of track each one is. For example, a subtitle track titled "English Signs" is signs and songs, and "English SDH" is for the deaf and hard of hearing. Add the words your files use, one per line.'],
    ['backup.readError', "The file couldn't be read."],
    ['backup.storageBlocked', "Settings for {name} can't be read. Repair those settings or remove the affected people from the file before importing anything."]
];
for (const [label, dir] of [['Assets/i18n', A], ['Assets/i18n-admin', ADMIN]]) {
    const table = JSON.parse(fs.readFileSync(dir + 'en.json', 'utf8'));
    Object.keys(table).forEach((k) => {
        if (typeof table[k] === 'string' && ENGLISH_RULE.test(table[k]) && !(dir === ADMIN && ALLOWED.some((a) => a[0] === k && a[1] === table[k]))) { fail(label + '/en.json ' + k + ' says "understand" or "read": ' + table[k]); }
    });
}
const OTHER = {
    de: /versteh|verstanden|\blese\b|\bliest\b|\blesen\b|\blesbar/i,
    es: /entiend|entend|comprend|\bleo\b|\blee\b|\blees\b|\bleer\b|\bleen\b/i,
    fr: /comprend|comprenn|compris|\blis\b|\blit\b|\blire\b|\blisez\b|\blisent\b/i,
    it: /capisc|capire|capito|\bleggi\b|\bleggo\b|\bleggere\b|\blegge\b|\bleggono\b/i,
    nl: /begrijp|begrepen|\blees\b|\bleest\b|\blezen\b|\bleesbaar/i,
    pl: /(^|[^\p{L}])(rozumie|czyta)/iu,
    pt: /(^|[^\p{L}])(entend|compreend|ler|leio|lês|lê|lemos|lido)/iu,
    ru: /(^|[^\p{L}])(понима|читаю|читаешь|читаете|читает|читать)/iu,
    ja: /理解|分かる|分かり|わかる|読(む|め|み(?!込))/,
    ko: /이해|읽/,
    zh: /理解|读懂|看懂|能读|阅读|懂/
};
// Keys that are about a file, a download or loading a page, or about an app (fr: "lit ce que l'application a demandé" is about the app).
const ABOUT_FILES = ['tracknames.help', 'backup.readError', 'app.loading', 'app.loadError', 'common.loading', 'page.loadError', 'people.needsUpdate', 'apps.mode.info'];
for (const [label, dir] of [['Assets/i18n', A], ['Assets/i18n-admin', ADMIN]]) {
    for (const f of fs.readdirSync(dir).filter((x) => x.endsWith('.json') && x !== 'en.json')) {
        const rule = OTHER[f.split('-')[0].replace('.json', '')];
        if (!rule) { fail(label + '/' + f + ' has no wording rule in validate-i18n.js'); continue; }
        const table = JSON.parse(fs.readFileSync(dir + f, 'utf8'));
        Object.keys(table).forEach((k) => {
            if (typeof table[k] === 'string' && ABOUT_FILES.indexOf(k) < 0 && !(dir === ADMIN && k === 'backup.storageBlocked') && rule.test(table[k])) { fail(label + '/' + f + ' ' + k + ' may say "understand" or "read" about the person: ' + table[k]); }
        });
    }
}

// Check every string, including older keys and admin copy. Identical English is
// allowed only for these exact technical formats or genuine same-word labels in
// the named languages. Pin the English value too so the allowance cannot hide prose.
const SAME_WORDS = {
    'common.ok': ['OK', '*'], 'ui.ok': ['OK', '*'],
    'app.back': ['← Jellyfin', '*'],
    'ui.item.episode': ['{series} · S{season}E{episode} · {name}', '*'],
    'ui.item.season': ['{series} · {name}', '*'],
    'ui.item.withYear': ['{name} ({year})', '*'],
    'ui.itemType.Episode': ['Episode', 'de'],
    'ui.itemType.BoxSet': ['Collection', 'fr'],
    'ui.genres.title': ['Genres', 'de fr nl'],
    'ui.no': ['No', 'es it'],
    'advanced.audio': ['Audio', 'es fr it nl pl'],
    'overrides.col.audio': ['Audio', 'es fr it nl pl'],
    'ui.table.audio': ['Audio', 'es fr it nl pl'],
    'dialog.audio': ['Audio', 'es fr it nl pl'],
    'libraries.audio': ['Audio', 'es fr it nl pl'],
    'common.person': ['Person', 'de'],
    'people.name': ['Name', 'de'],
    'backup.matched.name': ['Name', 'de'],
    'apps.title': ['Apps', 'de nl pt-BR'],
    'apps.app': ['App', 'de it nl pt-BR'],
    'tracknames.SdhWords': ['SDH', '*'], 'kind.sdh': ['SDH', '*'],
    'backup.matched.id': ['ID', '*'], 'backup.action': ['Action', 'fr']
};
const normalizedCopy = text => text.normalize('NFKC').trim().replace(/\s+/gu, ' ').toLowerCase();
for (const [label, dir] of [['Assets/i18n', A], ['Assets/i18n-admin', ADMIN]]) {
    const english = JSON.parse(fs.readFileSync(dir + 'en.json', 'utf8'));
    const locales = fs.readdirSync(dir).filter(f => f.endsWith('.json'));
    if (locales.length !== 16) { fail(label + ' must retain all 16 locale files'); }
    for (const file of locales.filter(f => f !== 'en.json')) {
        const locale = file.slice(0, -5);
        const table = JSON.parse(fs.readFileSync(dir + file, 'utf8'));
        for (const [key, value] of Object.entries(table)) {
            if (typeof value !== 'string' || typeof english[key] !== 'string' || !value.trim()
                || normalizedCopy(value) !== normalizedCopy(english[key])) { continue; }
            const allowed = SAME_WORDS[key];
            if (allowed && english[key] === allowed[0]
                && (allowed[1] === '*' || allowed[1].split(' ').includes(locale))) { continue; }
            fail(label + '/' + file + ' ' + key + ' is still English');
        }
    }
}

console.log(files.length + ' files, ' + enKeys.length + ' keys each, ' + (bad ? bad + ' problems' : 'all valid'));
process.exit(bad ? 1 : 0);
