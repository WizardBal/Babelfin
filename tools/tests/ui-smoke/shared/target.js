'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// This tree installs at <repo>/tools/tests/ui-smoke; never depend on the caller's cwd.
const repoRoot = path.resolve(__dirname, '../../../..');
const defaultAssets = path.join(repoRoot, 'plugin/src/Jellyfin.Plugin.Babelfin/Assets');
function getAssetsPath() {
    return path.resolve(process.env.BF_ASSETS || defaultAssets) + path.sep;
}

// Verify behavior rather than a machine-specific checkout name or a pinned source hash.
function verifySemanticTarget(assets, Document) {
    const document = new Document();
    const context = { document, console, setTimeout, clearTimeout, Intl };
    context.window = context;
    vm.createContext(context);
    vm.runInContext(fs.readFileSync(path.join(assets, 'ui.js'), 'utf8'), context, { filename: 'ui.js' });
    const UI = context.BabelfinUI;
    assert.equal(typeof UI.tableChoice, 'function', 'Semantic guard: tableChoice is required');
    const words = JSON.parse(fs.readFileSync(path.join(assets, 'i18n/en.json'), 'utf8'));
    const t = key => words[key] || key;
    const audio = UI.tableChoice({ t, kind: 'audio' }).select;
    const subtitles = UI.tableChoice({ t, kind: 'subtitles' }).select;
    for (const [value, kind] of [['mine', 'MyLanguages'], ['original', 'OriginalLanguage']]) {
        assert.equal(audio.options.filter(o => o.value === value).length, 1,
            'Semantic guard: required audio option "' + value + '" must exist exactly once');
        assert.equal(subtitles.options.some(o => o.value === value), false, 'Semantic guard: "' + value + '" is audio-only');
        const settings = {};
        UI.writeTableAudio(settings, value);
        assert.deepEqual(JSON.parse(JSON.stringify(settings.AudioChain)), [{ Kind: kind }, { Kind: 'VideoDefault' }],
            'Semantic guard: "' + value + '" must store its semantic chain');
        assert.equal(UI.tableAudioValue(settings, []), value, 'Semantic guard: semantic audio must round-trip without selected languages');
    }
    assert.equal(typeof UI.genreRows, 'function', 'Semantic guard: genreRows is required');
    assert.equal(typeof UI.choiceStatus, 'function', 'Semantic guard: choiceStatus is required');
    console.log('UI semantic guard passed: ' + fs.realpathSync(assets));
}
module.exports = { getAssetsPath, verifySemanticTarget, repoRoot };
