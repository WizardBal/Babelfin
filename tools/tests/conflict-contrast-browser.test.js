'use strict';
// Run with Node on a machine with Chrome; CHROME_PATH can select the browser.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { Chrome } = require('../rig/e2e/cdp');
const assets = process.env.BF_ASSETS || path.join(__dirname, '../../plugin/src/Jellyfin.Plugin.Babelfin/Assets');
const shared = fs.readFileSync(path.join(assets, 'ui.js'), 'utf8');
const themes = {
    light: { text: '#000', background: '#e8e8e8', body: '#f2f2f2' },
    dark: { text: '#fff', background: '#202020', body: '#101010' },
    appletv: { text: 'rgba(0,0,0,.87)', background: '#eaeaea', body: '#d5e9f2' },
    blueradiance: { text: '#eee', background: '#011432', body: '#033361' },
    purplehaze: { text: '#f8f8fe', background: '#230c33', body: '#230c33' },
    wmc: { text: '#eee', background: '#0c2450', body: '#0f3562' }
};

test('conflict panel remains readable with palette fixtures', async t => {
    const chrome = await Chrome.launch();
    try {
        const page = await chrome.newPage();
        for (const [theme, palette] of Object.entries(themes)) for (const width of [375, 1280]) for (const kind of ['dashboard', 'person']) await t.test(`${theme} ${width} ${kind}`, async () => {
            await page.setViewport(width, 800, false);
            await page.navigate('about:blank');
            const css = fs.readFileSync(path.join(assets, kind === 'dashboard' ? 'dashboard.css' : 'app.css'), 'utf8');
            const result = await page.evaluate((cssText, scriptText, colors, dashboard) => {
                document.head.innerHTML = '<meta name="viewport" content="width=device-width,initial-scale=1">';
                const style = document.createElement('style'); style.textContent = ':root{--jf-palette-text-primary:' + colors.text + ';--jf-palette-background-paper:' + colors.background + ';--jf-palette-primary-main:#00a4dc}body{background:' + colors.body + '}' + cssText; document.head.appendChild(style);
                (0, eval)(scriptText);
                const t = (key, values) => key === 'conflict.message' ? values.name + ' changed ' + values.fields : key;
                BabelfinUI.switchControl({ t, label: 'Warm up' });
                const wrapper = document.createElement('div'); wrapper.className = dashboard ? 'babelfinDashboard' : ''; document.body.appendChild(wrapper);
                const panel = BabelfinUI.conflictPanel({ t, problem: { LastChangedByName: 'x'.repeat(70), ConflictingPaths: ['enabled'] }, label: () => 'Enabled', reload: () => {}, keep: () => {} }); wrapper.appendChild(panel);
                const rgb = color => { const a = color.match(/[\d.]+/g).map(Number); return [a[0], a[1], a[2], a.length > 3 ? a[3] : 1]; };
                const blend = (fg, bg) => fg.slice(0, 3).map((v, i) => v * fg[3] + bg[i] * (1 - fg[3]));
                const luminance = c => c.slice(0, 3).map(v => { v /= 255; return v <= .03928 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4); }).reduce((a, v, i) => a + v * [.2126, .7152, .0722][i], 0);
                const ratio = (a, b) => { const x = luminance(a), y = luminance(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); };
                const background = rgb(getComputedStyle(panel).backgroundColor);
                const message = ratio(blend(rgb(getComputedStyle(panel.querySelector('p')).color), background), background);
                const buttons = Array.from(panel.querySelectorAll('button')).map(button => { const style = getComputedStyle(button); const bg = blend(rgb(style.backgroundColor), background); return ratio(blend(rgb(style.color), bg), bg); });
                return { message, buttons, scroll: document.documentElement.scrollWidth, width: document.documentElement.clientWidth };
            }, css, shared, palette, kind === 'dashboard');
            assert.ok(result.message >= 4.5, `${theme} ${width} ${kind} message ${result.message}`);
            assert.ok(result.buttons.every(value => value >= 4.5), `${theme} ${width} ${kind} buttons ${result.buttons}`);
            assert.ok(result.scroll <= result.width + 1, `${theme} ${width} ${kind} horizontal overflow`);
        });
    } finally { await chrome.close(); }
});

test('embedded Settings conflict uses its own fallback palette', async () => {
    const chrome = await Chrome.launch();
    try {
        const page = await chrome.newPage(); await page.navigate('about:blank');
        const css = fs.readFileSync(path.join(assets, 'app.css'), 'utf8');
        const result = await page.evaluate((cssText, scriptText) => {
            document.documentElement.style.setProperty('--jf-palette-background-paper', '#eaeaea');
            document.documentElement.style.setProperty('--jf-palette-text-primary', 'rgba(0,0,0,.87)');
            const frame = document.createElement('iframe'); document.body.appendChild(frame);
            const w = frame.contentWindow, d = frame.contentDocument;
            const style = d.createElement('style'); style.textContent = cssText; d.head.appendChild(style);
            w.eval(scriptText);
            const t = (key, values) => key === 'conflict.message' ? values.name + ' changed ' + values.fields : key;
            w.BabelfinUI.switchControl({ t, label: 'Warm up' });
            const panel = w.BabelfinUI.conflictPanel({ t, problem: { LastChangedByName: 'Admin', ConflictingPaths: ['enabled'] }, label: () => 'Enabled', reload: () => {}, keep: () => {} });
            d.body.appendChild(panel);
            return { inherited: w.getComputedStyle(d.documentElement).getPropertyValue('--jf-palette-background-paper').trim(), background: w.getComputedStyle(panel).backgroundColor, color: w.getComputedStyle(panel.querySelector('p')).color };
        }, css, shared);
        assert.equal(result.inherited, '');
        assert.equal(result.background, 'rgb(51, 43, 25)');
        assert.equal(result.color, 'rgb(255, 255, 255)');
    } finally { await chrome.close(); }
});


// The host can contain a viewport-height page below its header. The embedded settings
// page must own scrolling while its route is active, then leave the host as it was.
test('settings route confines scrolling to the embedded page', async () => {
    const chrome = await Chrome.launch();
    try {
        const page = await chrome.newPage();
        await page.setViewport(1280, 800, false);
        await page.navigate('about:blank');
        const css = fs.readFileSync(path.join(assets, 'client.css'), 'utf8');
        await page.evaluate(cssText => {
            document.head.innerHTML = '';
            const host = document.createElement('style');
            host.textContent = 'html,body{margin:0}body{box-sizing:content-box;padding-top:48px;min-height:100vh}.header{position:fixed;top:0;left:0;right:0;height:48px}#fallbackPage{height:100vh}#fallbackPage p{margin:0}';
            document.head.appendChild(host);
            const style = document.createElement('style'); style.textContent = cssText; document.head.appendChild(style);
            document.body.innerHTML = '<div class="header">Header</div><main id="fallbackPage"><p>Page not found</p></main>';
        }, css);
        const wheel = async (x, y) => {
            await page.send('Input.dispatchMouseEvent', { type: 'mouseWheel', x, y, deltaX: 0, deltaY: 400 });
            await new Promise(resolve => setTimeout(resolve, 150));
        };
        await wheel(20, 20);
        const before = await page.evaluate(() => ({ extent: document.documentElement.scrollHeight - innerHeight, moved: window.scrollY }));
        assert.ok(before.extent >= 48 && before.moved > 0, 'host fixture must scroll before opening settings');
        await page.evaluate(() => {
            window.scrollTo(0, 0);
            document.documentElement.classList.add('bfSettingsRoute');
            const panel = document.createElement('div'); panel.className = 'bfSettingsPanel'; panel.style.top = '48px';
            const frame = document.createElement('iframe'); frame.className = 'bfSettingsFrame'; panel.appendChild(frame); document.body.appendChild(panel);
            frame.contentDocument.open(); frame.contentDocument.write('<!doctype html><style>body{margin:0;min-height:1800px}</style><p>Settings</p>'); frame.contentDocument.close();
        });
        await wheel(20, 20);
        const host = await page.evaluate(() => ({ extent: document.documentElement.scrollHeight - innerHeight, moved: window.scrollY,
            width: document.querySelector('.bfSettingsPanel').getBoundingClientRect().width, viewport: innerWidth }));
        await wheel(200, 200);
        const frame = await page.evaluate(() => {
            const w = document.querySelector('.bfSettingsFrame').contentWindow;
            return { extent: w.document.documentElement.scrollHeight - w.innerHeight, moved: w.scrollY };
        });
        await page.evaluate(() => { document.querySelector('.bfSettingsPanel').remove(); document.documentElement.classList.remove('bfSettingsRoute'); window.scrollTo(0, 0); });
        await wheel(20, 20);
        const after = await page.evaluate(() => ({ extent: document.documentElement.scrollHeight - innerHeight, moved: window.scrollY }));
        assert.ok(frame.extent > 0 && frame.moved > 0, 'settings frame must remain scrollable');
        assert.ok(after.extent >= 48 && after.moved > 0, 'host scrolling must return after leaving settings');
        assert.ok(host.extent <= 1, `settings route host extends ${host.extent}px beyond the viewport`);
        assert.equal(host.width, host.viewport, 'settings panel must span the viewport');
        assert.equal(host.moved, 0, 'wheel over the host must not scroll behind settings');
    } finally { await chrome.close(); }
});
