'use strict';
// Shared maintained feature contracts, executed on each suite's own fake DOM.
module.exports = async function sharedPreferenceContract({ UI, document, Event, t, check, eq }) {
    const copy = value => JSON.parse(JSON.stringify(value));
    const tick = async () => { for (let i = 0; i < 20; i++) { await Promise.resolve(); } };
    const host = document.createElement('div');
    document.body.appendChild(host);
    const select = (node, value) => { node.value = value; eq(node.value, value, 'option exists: ' + value); node.dispatchEvent(new Event('change')); };
    const rule = (name, id = name, settings = {}) => ({ Id: id, Name: name, Enabled: true, Match: 'All', Conditions: [{ Kind: 'Genre', Values: [name], Negate: false, IsRegex: false }], Settings: settings });

    for (const audience of ['person', 'admin']) {
        const choice = UI.tableChoice({ t, audience, kind: 'audio' });
        host.appendChild(choice.element);
        const node = choice.select;
        for (const [value, kind, key] of [['mine', 'MyLanguages', 'ui.table.audioMyLanguages'], ['original', 'OriginalLanguage', 'ui.table.audioOriginal']]) {
            select(node, value);
            const layer = { AvoidCommentary: true, SubtitleLanguages: ['fr'], ForeignTitles: 'DubInMyLanguage' };
            UI.writeTableAudio(layer, value);
            eq(copy(layer.AudioChain), [{ Kind: kind }, { Kind: 'VideoDefault' }], audience + ': exact chain');
            eq([layer.AvoidCommentary, layer.SubtitleLanguages, layer.ForeignTitles === undefined], [true, ['fr'], true], 'audio edit preserves unrelated settings and clears legacy ForeignTitles');
            for (const languages of [[], ['de'], ['de', 'en']]) { eq(UI.tableAudioValue(layer, languages), value, 'stored semantic audio choice survives language-list changes'); }
            eq(node.options.find(o => o.value === value).textContent, t(key), 'translated audio label');
            if (value === 'mine') {
                eq(node.getAttribute('aria-describedby'), null, 'My languages has no description');
                check(choice.element.querySelectorAll('.bfui-info-wrap').every(n => n.hidden), 'My languages has no visible info button');
            } else {
            const tip = document.getElementById(node.getAttribute('aria-describedby'));
            const wrap = choice.element.querySelectorAll('.bfui-info-wrap').find(n => !n.hidden);
            const button = wrap.querySelector('button');
            eq(tip.textContent, t(key + '.info'), 'selected audio choice has its own translated explanation');
            button.dispatchEvent(new Event('focus'));
            check(!tip.hidden, 'audio explanation opens from keyboard focus');
            const escape = new Event('keydown', { bubbles: true });
            escape.key = 'Escape';
            document.dispatchEvent(escape);
            check(tip.hidden, 'Escape closes the audio explanation');
            }
            check(choice.select === node, 'choosing and explaining audio retains the select node');
        }
        select(node, '');
        eq(node.getAttribute('aria-describedby'), null, 'Default removes the selected audio explanation');
        check(choice.element.querySelectorAll('.bfui-info-wrap').every(n => n.hidden), 'Default hides the original-language tip');
        const sub = UI.tableChoice({ t, audience, kind: 'subtitles' }).select;
        check(!sub.options.some(n => ['mine', 'original'].includes(n.value)), 'semantic audio choices never leak into subtitles');
    }

    const custom = [{ Kind: 'OriginalLanguage' }, { Kind: 'Language', Language: 'de' }];
    const visible = rule('Anime', 'a', { AudioChain: custom, FutureField: 'keep', AvoidCommentary: false });
    const hidden = [Object.assign(rule('disabled'), { Enabled: false }),
        Object.assign(rule('negative'), { Conditions: [{ Kind: 'Genre', Values: ['Drama'], Negate: true }] }),
        Object.assign(rule('regex'), { Conditions: [{ Kind: 'Genre', Values: ['Drama'], IsRegex: true }] }),
        Object.assign(rule('multiple'), { Conditions: [{ Kind: 'Genre', Values: ['Drama', 'Comedy'] }] }),
        Object.assign(rule('tag'), { Conditions: [{ Kind: 'Tag', Values: ['Drama'] }] })];
    const hiddenBefore = copy(hidden), settings = { SmartRules: [visible, ...hidden] };
    let changes = 0, attempts = 0;
    const genres = UI.genreRows(host, { settings, t, onChange: () => changes++, loadGenres: () => {
        attempts++;
        if (attempts === 1) { return Promise.reject(new Error('RAW GENRE FAILURE: private detail')); }
        return { Items: [{ Name: 'Anime' }, { Name: 'Drama' }, { Name: 'drama' }, { Name: '' }] };
    } });
    const el = genres.element, body = el.querySelector('tbody');
    const add = el.querySelectorAll('button').find(b => b.parentNode === el && b.textContent === t('ui.genres.add'));
    eq([el.tagName, attempts, changes, body.children.length], ['SECTION', 0, 0, 1], 'genre section is typed, lazy and displays only recognized rules');
    const nodes = body.querySelectorAll('select');
    select(nodes[0], 'mine'); select(nodes[1], 'off'); select(nodes[0], 'custom');
    eq(copy(visible.Settings.AudioChain), custom, 'Custom restores exactly the original genre audio column');
    eq([visible.Settings.FutureField, visible.Settings.AvoidCommentary, visible.Settings.WhenAudioUnderstood], ['keep', false, 'Off'], 'genre audio preserves other fields and subtitle edits');
    genres.refreshLanguages();
    check(body.querySelectorAll('select').every((n, i) => n === nodes[i]), 'genre language refresh keeps select nodes');
    eq(copy(hidden), hiddenBefore, 'unrecognized stored rules survive edits and refresh');
    add.click(); await tick();
    eq(el.querySelector('[role=alert]').textContent, t('ui.genres.loadError'), 'catalogue failure shows readable translated text');
    check(!el.textContent.includes('RAW GENRE FAILURE'), 'catalogue failure never exposes raw errors');
    el.querySelectorAll('button').find(b => b.textContent === t('ui.genres.retry')).click(); await tick();
    const pickerBox = el.querySelector('.bfui-genres-picker'), picker = pickerBox.querySelector('select');
    eq(picker.options.map(n => n.value), ['', 'Drama'], 'catalogue deduplicates by case and excludes existing genres');
    eq([picker.value, pickerBox.querySelector('button').disabled, changes], ['', true, 3], 'catalogue does not select or add a genre automatically');
    select(picker, 'Drama'); pickerBox.querySelector('button').click();
    eq(copy(settings.SmartRules.at(-1)), rule('Drama', ''), 'new genre has the exact literal rule shape and no client-generated id');
    body.children[1].querySelector('[data-action=up]').click();
    eq(settings.SmartRules.filter(UI.isGenreRow).map(r => r.Name), ['Drama', 'Anime'], 'genre reorder changes first-match order');
    eq(copy(settings.SmartRules.slice(1, -1)), hiddenBefore, 'reorder preserves unrelated rule slots');
    body.children[0].querySelector('[data-action=remove]').click();
    eq(settings.SmartRules.filter(UI.isGenreRow).map(r => r.Name), ['Anime'], 'genre removal edits only the chosen rule');
    eq(changes, 6, 'one change notification per genre edit, add, move and removal');

    const full = UI.genreRows(host, { settings: { SmartRules: Array.from({ length: 100 }, (_, i) => rule('Genre ' + i)) }, t, loadGenres: () => { throw new Error('must not fetch at cap'); } });
    const capAdd = full.element.querySelectorAll('button').find(b => b.parentNode === full.element && b.textContent === t('ui.genres.add'));
    check(capAdd.disabled, '100 recognized genre rows disable Add');
    full.element.querySelector('[data-action=remove]').click();
    check(!capAdd.disabled, 'removing a genre below the cap enables Add');

    let resolve, answer = new Promise(done => { resolve = done; });
    const status = UI.choiceStatus({ text: 'No saved choices', load: () => answer });
    check(status.element.hidden, 'choice status is silent while pending');
    await tick(); resolve({ HasChoices: false }); await tick();
    eq([status.element.hidden, status.element.textContent], [false, 'No saved choices'], 'explicit saved HasChoices false shows the empty-choice message');
    for (const response of [{ HasChoices: true }, {}, null, { HasChoices: 'false' }]) {
        answer = response; await status.refresh();
        check(status.element.hidden, 'true or unknown status stays silent: ' + JSON.stringify(response));
    }
    answer = Promise.reject(new Error('RAW STATUS FAILURE: private detail')); await status.refresh();
    check(status.element.hidden && !status.element.textContent.includes('RAW STATUS FAILURE'), 'status failures are silent and expose no raw error');
    let old;
    answer = new Promise(done => { old = done; }); status.refresh(); await tick();
    answer = { HasChoices: true }; await status.refresh(); old({ HasChoices: false }); await tick();
    check(status.element.hidden, 'late stale status cannot overwrite the latest saved status');
    host.remove();
};
