const fs = require('fs');
const assets = require('../shared/target').getAssetsPath();
const dir = assets + 'i18n-admin/';
const enRaw = fs.readFileSync(dir + 'en.json');
const en = JSON.parse(enRaw.toString('utf8'));
const enKeys = Object.keys(en);
const ph = (s) => (String(s).match(/\{[a-zA-Z]+\}/g) || []).sort().join(',');
let bad = 0;
const files = fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort();
for (const f of files) {
  const raw = fs.readFileSync(dir + f);
  const problems = [];
  if (raw[0] === 0xEF && raw[1] === 0xBB && raw[2] === 0xBF) problems.push('BOM');
  let j;
  try { j = JSON.parse(raw.toString('utf8')); } catch (e) { problems.push('invalid JSON: ' + e.message); }
  if (j) {
    const keys = Object.keys(j);
    if (JSON.stringify(keys) !== JSON.stringify(enKeys)) problems.push('keys differ from en.json (count ' + keys.length + ' vs ' + enKeys.length + ')');
    const want = f === 'en.json' ? false : true;
    if (j._machineTranslated !== want) problems.push('_machineTranslated is ' + j._machineTranslated);
    for (const k of enKeys) {
      if (k === '_machineTranslated') continue;
      if (typeof j[k] !== 'string' || !j[k].length) { problems.push('empty/missing ' + k); continue; }
      if (ph(j[k]) !== ph(en[k])) problems.push('placeholders differ in ' + k + ': ' + ph(j[k]) + ' vs ' + ph(en[k]));
      if (j[k].indexOf('!') >= 0 && f !== 'en.json') problems.push('exclamation in ' + k);
    }
  }
  // The immutable Windows baseline and mirror use CRLF. Accept consistent LF or CRLF;
  // still reject malformed lone CR characters and mixed line endings, without rewriting source.
  const text = raw.toString('utf8');
  if (/\r(?!\n)/.test(text)) problems.push('lone CR line endings');
  if (text.includes('\r\n') && /(?<!\r)\n/.test(text)) problems.push('mixed LF/CRLF line endings');
  console.log((problems.length ? 'BAD ' : 'ok  ') + f + (problems.length ? ': ' + problems.join('; ') : ''));
  bad += problems.length;
}
const same = (a, b) => fs.readFileSync(dir + a).equals(fs.readFileSync(dir + b));
console.log('pt.json == pt-PT.json:', same('pt.json', 'pt-PT.json'), ' zh.json == zh-Hans.json:', same('zh.json', 'zh-Hans.json'));
process.exit(bad ? 1 : 0);
