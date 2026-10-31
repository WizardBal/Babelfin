/* Babelfin script injected into jellyfin-web.
   - window.Babelfin: version, baseUrl, userId, hasToken.
   - "Babelfin Settings" in the user menu (modern layout: the avatar menu; legacy layouts: a row on #myPreferencesMenuPage after the
     Subtitles row), linking to #/babelfin.
   - #/babelfin: Babelfin Settings ({baseUrl}/Babelfin/app in a frame) under Jellyfin's own header.
   - The item "⋮" menu (jellyfin-web's action sheet): an "Audio and subtitles (Babelfin)" entry for movies, shows, seasons and
     episodes that opens the dialog, for the person's own audio and subtitles of that show or movie. The dialog's
     dropdowns come from ui.js; the entry is offered only once it is loaded. In TV layout they open an in-dialog pick list.
   Plain ES5 syntax on purpose (old TV browsers); bails out quietly without fetch, Promise or MutationObserver.
   Never throws into the page: every entry point is wrapped and logs with the "[Babelfin]" prefix. */
(function () {
    'use strict';

    if (window.Babelfin && window.Babelfin.clientLoaded) { return; }

    var FALLBACK_VERSION = '0.19.0.0';
    var has = function (o, k) { return Object.prototype.hasOwnProperty.call(o, k); };

    function warn(what, err) {
        try { console.warn('[Babelfin] ' + what, err || ''); } catch (e) { /* no console */ }
    }

    function ownScriptSrc() {
        var script = document.currentScript;
        if (script && script.src) { return script.src; }
        var all = document.getElementsByTagName('script');
        for (var i = all.length - 1; i >= 0; i--) {
            if (all[i].src && all[i].src.toLowerCase().indexOf('/babelfin/client.js') >= 0) { return all[i].src; }
        }
        return '';
    }

    function baseUrlFrom(src) {
        var at = src.toLowerCase().indexOf('/babelfin/client.js');
        if (at < 0) { return ''; }
        try { return new URL(src.slice(0, at), location.href).pathname.replace(/\/+$/, ''); } catch (e) { return ''; }
    }

    function versionFrom(src) {
        var m = /[?&]v=([^&#]+)/.exec(src);
        try { return m ? decodeURIComponent(m[1]) : FALLBACK_VERSION; } catch (e) { return FALLBACK_VERSION; }
    }

    function readCredentials() {
        try {
            var raw = localStorage.getItem('jellyfin_credentials');
            var servers = raw ? (JSON.parse(raw).Servers || []) : [];
            var best = null;
            for (var i = 0; i < servers.length; i++) {
                var s = servers[i];
                if (s && s.AccessToken && (!best || (s.DateLastAccessed || 0) > (best.DateLastAccessed || 0))) { best = s; }
            }
            return best;
        } catch (e) {
            return null;
        }
    }

    var SRC = ownScriptSrc();
    var BASE = baseUrlFrom(SRC);
    var VERSION = versionFrom(SRC);
    var server = readCredentials();
    window.Babelfin = { version: VERSION, baseUrl: BASE, userId: server ? (server.UserId || null) : null, hasToken: !!server };

    if (!window.Promise || !window.fetch || !window.MutationObserver || !document.querySelectorAll || !document.addEventListener) {
        return;
    }

    window.Babelfin.clientLoaded = true;

    // English defaults, used only when a key is missing from every loaded translation file (or the files fail to load).
    var DEFAULTS = {
        'client.languages': 'Babelfin Settings', 'client.menu.override': 'Audio and subtitles (Babelfin)',
        'dialog.title': 'Audio and subtitles', 'dialog.audio': 'Audio', 'dialog.subtitles': 'Subtitles',
        'dialog.applies.show': "Applies to the whole show, in place of the {library} library's choice.",
        'dialog.applies.movie': "Applies to this movie, in place of the {library} library's choice.",
        'dialog.applies.video': "Applies to this video, in place of the {library} library's choice.",
        'dialog.applies.showNoLibrary': "Applies to the whole show, in place of the library's choice.",
        'dialog.applies.movieNoLibrary': "Applies to this movie, in place of the library's choice.",
        'dialog.applies.videoNoLibrary': "Applies to this video, in place of the library's choice.",
        'dialog.narrower': 'An episode or season of this show has its own override, which comes first for it. It is listed under Show and movie overrides in Babelfin Settings.',
        'dialog.burnedIn.show': 'All episodes of this show have burned-in subtitles in',
        'dialog.burnedIn.movie': 'This movie has burned-in subtitles in', 'dialog.burnedIn.video': 'This video has burned-in subtitles in',
        'dialog.burnedInOther': 'or type a language',
        'dialog.save': 'Save', 'dialog.cancel': 'Cancel', 'dialog.saved': 'Saved', 'dialog.remove': 'Remove override', 'dialog.removed': 'Override removed',
        'dialog.error': 'Something went wrong. Please try again.', 'dialog.errorBurnedIn': 'Your audio and subtitles were saved, but the burned-in subtitles were not.',
        'common.error': 'Something went wrong.', 'common.loading': 'Loading…', 'common.close': 'Close', 'common.machineTranslated': 'Machine translated: help improve it',
        // The words of the table's dropdowns (ui.js asks the translator passed to tableChoice for them).
        'ui.info.about': 'More about {name}', 'ui.default': 'Default', 'ui.defaultValue': 'Default ({value})', 'ui.table.videoDefault': 'Video default', 'ui.table.off': 'Off',
        'ui.table.smart': "When the audio isn't in my languages", 'ui.table.signs': 'Signs and songs only', 'ui.table.myLanguages': 'My languages',
        'ui.table.allLanguages': 'All languages', 'ui.table.custom': 'Custom', 'ui.table.original': 'Original language',
        'ui.table.audioMyLanguages': 'My languages, in order',
        'ui.table.audioOriginal': "The title's original language",
        'ui.table.audioOriginal.info': "Uses the original language in the title's TMDb or OMDb metadata. If it is missing, or no audio track matches, the video's default plays."
    };

    var state = { t: null, lang: null, i18nPromise: null, i18nLang: null, languages: null, uiPromise: null, dialog: null, queued: false, menuWait: false, trigger: null, items: {} };
    var displayNames = null;

    // ── Small helpers ────────────────────────────────────────────────────

    function tHas(key) {
        var t = state.t;
        return !!t && (typeof t.has === 'function' ? t.has(key) : t(key) !== key);
    }

    function T(key, params) {
        if (tHas(key)) { return state.t(key, params); }
        var text = has(DEFAULTS, key) ? DEFAULTS[key] : key;
        for (var p in params || {}) {
            if (has(params, p)) { text = text.split('{' + p + '}').join(String(params[p])); }
        }
        return text;
    }

    function guard(name, fn) {
        return function () {
            try { return fn.apply(this, arguments); } catch (e) { warn(name + ' failed', e); return undefined; }
        };
    }

    function el(tag, attrs, children) {
        var node = document.createElement(tag);
        for (var k in attrs || {}) {
            var v = attrs[k];
            if (!has(attrs, k) || v === null || v === undefined || v === false) { continue; }
            if (k === 'text') {
                node.textContent = v;
            } else if (k === 'className') {
                node.className = v;
            } else if (k.indexOf('on') === 0 && typeof v === 'function') {
                node.addEventListener(k.slice(2), guard(k, v));
            } else {
                node.setAttribute(k, v === true ? '' : String(v));
            }
        }
        for (var i = 0; children && i < children.length; i++) {
            if (children[i] !== null && children[i] !== undefined) {
                node.appendChild(typeof children[i] === 'string' ? document.createTextNode(children[i]) : children[i]);
            }
        }
        return node;
    }

    function remove(node) {
        if (node && node.parentNode) { node.parentNode.removeChild(node); }
    }

    function each(list, fn) {
        for (var i = 0; list && i < list.length; i++) { fn(list[i], i); }
    }

    function find(list, fn) {
        for (var i = 0; list && i < list.length; i++) { if (fn(list[i], i)) { return list[i]; } }
        return null;
    }

    function asset(path) {
        return BASE + '/Babelfin/assets/' + path + '?v=' + encodeURIComponent(VERSION);
    }

    function loadScript(url, globalName) {
        return new Promise(function (resolve) {
            if (window[globalName]) { resolve(window[globalName]); return; }
            var s = document.createElement('script');
            s.src = url;
            s.async = true;
            s.onload = function () { resolve(window[globalName] || null); };
            s.onerror = function () { resolve(null); };
            (document.head || document.documentElement).appendChild(s);
        });
    }

    function withTimeout(promise, ms) {
        return Promise.race([promise, new Promise(function (resolve) { setTimeout(function () { resolve(null); }, ms); })]);
    }

    function ensureCss() {
        if (!document.getElementById('bfClientCss')) {
            (document.head || document.documentElement).appendChild(el('link', { id: 'bfClientCss', rel: 'stylesheet', href: asset('client.css') }));
        }
    }

    function client() {
        return window.ApiClient && typeof window.ApiClient.getUrl === 'function' ? window.ApiClient : null;
    }

    function currentUserId() {
        var c = client();
        try { return (c && c.getCurrentUserId && c.getCurrentUserId()) || null; } catch (e) { return null; }
    }

    function apiGet(path, params) {
        var c = client();
        return c ? c.getJSON(c.getUrl(path, params)) : Promise.reject(new Error('no ApiClient'));
    }

    function apiSend(method, path, params, body) {
        var c = client();
        if (!c) { return Promise.reject(new Error('no ApiClient')); }
        var request = { type: method, url: c.getUrl(path, params || undefined) };
        if (body !== undefined) { request.data = JSON.stringify(body); request.contentType = 'application/json'; }
        return c.ajax(request);
    }

    function statusOf(err) {
        return err && typeof err.status === 'number' ? err.status : 0;
    }

    function problemText(err) {
        if (!err || typeof err.json !== 'function') { return Promise.resolve(''); }
        return err.json().then(function (p) {
            var code = p && (p.Code || p.code), key = 'api.' + code;
            if (code && tHas(key)) { return T(key); }
            var list = p && (p.Errors || p.errors);
            var first = list && list[0];
            return (first && (first.Message || first.message)) || (p && (p.Message || p.message)) || '';
        }, function () { return ''; });
    }

    function ensureI18n() {
        var uid = currentUserId() || window.Babelfin.userId;
        var lang = 'en';
        try {
            lang = window.BabelfinI18n ? window.BabelfinI18n.detectLanguage(uid) : ((uid && localStorage.getItem(uid + '-language')) || navigator.language || 'en');
        } catch (e) {
            lang = navigator.language || 'en';
        }
        if (state.i18nPromise && state.i18nLang === lang) { return state.i18nPromise; }
        state.i18nLang = lang;
        state.i18nPromise = loadScript(asset('i18n.js'), 'BabelfinI18n').then(function (i18n) {
            return i18n ? i18n.load(BASE, lang, 'i18n', VERSION) : null;
        }).then(null, function (e) {
            warn('translations unavailable', e);
            return null;
        }).then(function (t) {
            state.t = t || null;
            state.lang = (t && t.language) || lang;
            displayNames = null;
            return state.t;
        });
        return state.i18nPromise;
    }

    // ── Language names and matching ──────────────────────────────────────

    function canon(code) {
        if (!code) { return ''; }
        try { if (window.Intl && Intl.getCanonicalLocales) { return Intl.getCanonicalLocales(String(code))[0].toLowerCase(); } } catch (e) { /* not a tag */ }
        return String(code).toLowerCase();
    }

    function baseOf(code) {
        return canon(code).split('-')[0];
    }

    function langName(code, english) {
        if (!code) { return english || ''; }
        try {
            if (!displayNames && window.Intl && Intl.DisplayNames) {
                try {
                    displayNames = new Intl.DisplayNames([state.lang || 'en', 'en'], { type: 'language' });
                } catch (e) {
                    displayNames = new Intl.DisplayNames(['en'], { type: 'language' });
                }
            }
            var name = displayNames ? displayNames.of(String(code)) : null;
            if (name && name.toLowerCase() !== String(code).toLowerCase()) { return name; }
        } catch (e) { /* unknown code */ }
        return english || String(code);
    }

    // ── Pages ────────────────────────────────────────────────────────────

    function shownPage(id) {
        return find(document.querySelectorAll('#' + id), function (p) { return !p.classList.contains('hide'); });
    }

    function detailItemId() {
        var hash = location.hash || '';
        if (hash.split('?')[0].toLowerCase().indexOf('details') < 0) { return null; }
        var m = /[?&]id=([0-9a-fA-F-]{32,36})(&|$)/.exec(hash);
        return m ? m[1].replace(/-/g, '').toLowerCase() : null;
    }

    // ── "Babelfin Settings" in the user menu ─────────────────────────────

    // Material "Translate" icon (the same 24px set jellyfin-web's menu uses).
    var TRANSLATE_ICON = 'm12.87 15.07-2.54-2.51.03-.03c1.74-1.94 2.98-4.17 3.71-6.53H17V4h-7V2H8v2H1v1.99h11.17C11.5 7.92 10.44 9.75 9 11.35 8.07 10.32 7.3 9.19 6.69 8h-2c.73 1.63 1.73 3.17 2.98 4.56l-5.09 5.02L4 19l5-5 3.11 3.11zM18.5 10h-2L12 22h2l1.12-3h4.75L21 22h2zm-2.62 7 1.62-4.33L19.12 17z';

    // The modern layout exposes its avatar menu through this control and menu id.
    function hasAvatarMenu() {
        return !!document.querySelector('button[aria-controls="app-user-menu"], #app-user-menu');
    }

    function userMenuVisible(menu) {
        if (menu.hidden || menu.getAttribute('aria-hidden') === 'true' || menu.classList.contains('MuiModal-hidden')) { return false; }
        var style = typeof window.getComputedStyle === 'function' ? window.getComputedStyle(menu) : null;
        return !style || (style.display !== 'none' && style.visibility !== 'hidden');
    }

    function userMenuRows(menu) {
        if (!userMenuVisible(menu)) { return []; }
        return Array.prototype.filter.call(menu.querySelectorAll('a[href], [role="menuitem"]'), function (node) {
            return !node.disabled && !node.hidden && node.getAttribute('aria-disabled') !== 'true';
        });
    }

    function syncTvMenuFocus(menu) {
        var rows = userMenuRows(menu), tv = isTvLayout() && userMenuVisible(menu);
        if (tv && !menu.classList.contains('focuscontainer')) {
            menu.classList.add('focuscontainer');
            menu.setAttribute('data-bf-native-scope', 'true');
        } else if (!tv && menu.getAttribute('data-bf-native-scope')) {
            menu.classList.remove('focuscontainer');
            menu.removeAttribute('data-bf-native-scope');
        }
        // Native TV input can bypass command events and move spatially. Jellyfin excludes
        // roving tabindex=-1 controls unless they carry its focusable class.
        each(menu.querySelectorAll('[data-bf-native-focus]'), function (node) {
            if (!tv || rows.indexOf(node) < 0) {
                node.classList.remove('focusable');
                node.removeAttribute('data-bf-native-focus');
            }
        });
        if (tv) {
            each(rows, function (node) {
                if (!node.classList.contains('focusable')) {
                    node.classList.add('focusable');
                    node.setAttribute('data-bf-native-focus', 'true');
                }
            });
        }
    }

    function observeTvMenuFocus(menu) {
        // React keeps this popup mounted: opening it may change only attributes, not children.
        if (!menu._babelfinNativeFocusObserver) {
            menu._babelfinNativeFocusObserver = new MutationObserver(guard('menu native focus', function () { syncTvMenuFocus(menu); }));
            menu._babelfinNativeFocusObserver.observe(menu, {
                attributes: true, attributeFilter: ['class', 'style', 'hidden', 'aria-hidden', 'aria-disabled'], childList: true, subtree: true
            });
        }
        syncTvMenuFocus(menu);
    }

    // Modern layout: "Babelfin Settings" right after "Settings" in the avatar menu. The menu is rebuilt each time it opens.
    function applyUserMenu() {
        var menu = document.getElementById('app-user-menu');
        if (!menu) { return; }
        var settings = find(menu.querySelectorAll('a[href]'), function (n) { return /mypreferencesmenu/i.test(n.getAttribute('href') || ''); });
        if (!settings || !settings.parentNode) { return; }
        var existing = menu.querySelector('.bfMenuItem');
        if (existing) {
            if (settings.nextSibling !== existing) { settings.parentNode.insertBefore(existing, settings.nextSibling); }
            observeTvMenuFocus(menu);
            return;
        }
        if (!state.t) {
            if (!state.menuWait) {
                state.menuWait = true;
                ensureI18n().then(function () { state.menuWait = false; schedule(); }, function () { state.menuWait = false; });
            }
            return;
        }
        var item = settings.cloneNode(true);
        item.removeAttribute('id');
        each(item.querySelectorAll('[id]'), function (n) { n.removeAttribute('id'); });
        item.classList.remove('Mui-focusVisible', 'Mui-selected', 'active');
        item.classList.add('bfMenuItem');
        item.removeAttribute('aria-current');
        item.setAttribute('tabindex', '-1');
        item.setAttribute('href', ROUTE);
        item.addEventListener('click', guard('menu item', function () { if (String(location.hash).toLowerCase().indexOf(ROUTE) !== 0) { panel.returnFocus = item; panel.returnRoute = location.hash; } closeUserMenu(menu); }));
        var label = T('client.languages');
        var textHost = item.querySelector('.MuiListItemText-root');
        var text = textHost && (textHost.querySelector('.MuiTypography-root') || textHost.querySelector('span, p, div'));
        if (text) {
            text.textContent = label;
        } else {
            item.textContent = label;
        }
        var svg = item.querySelector('svg');
        if (svg) {
            while (svg.firstChild) { svg.removeChild(svg.firstChild); }
            svg.removeAttribute('data-testid');
            svg.setAttribute('viewBox', '0 0 24 24');
            var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
            path.setAttribute('d', TRANSLATE_ICON);
            svg.appendChild(path);
        }
        settings.parentNode.insertBefore(item, settings.nextSibling);
        observeTvMenuFocus(menu);
        // React's roving list does not know about a DOM-inserted row. Bridge only moves into or out of that row.
        if (menu._babelfinFocus) {
            menu.removeEventListener('keydown', menu._babelfinFocus, true);
            menu.removeEventListener('command', menu._babelfinFocus, true);
        }
        menu._babelfinFocus = guard('menu focus', function (e) {
            // TV inputManager emits command events; keyboard input uses keydown.
            var command = e.detail && e.detail.command;
            var step = command === 'down' || e.key === 'ArrowDown' || e.keyCode === 40 ? 1
                : command === 'up' || e.key === 'ArrowUp' || e.keyCode === 38 ? -1 : 0;
            if (!step) { return; }
            var rows = userMenuRows(menu);
            var at = rows.indexOf(document.activeElement);
            if (at < 0) { return; }
            var next = rows[(at + step + rows.length) % rows.length];
            if (document.activeElement !== item && next !== item) { return; }
            e.preventDefault();
            e.stopPropagation();
            if (e.stopImmediatePropagation) { e.stopImmediatePropagation(); }
            each(rows, function (n) { n.setAttribute('tabindex', n === next ? '0' : '-1'); });
            next.focus();
        });
        menu.addEventListener('keydown', menu._babelfinFocus, true);
        menu.addEventListener('command', menu._babelfinFocus, true);
    }

    // Legacy layout: the avatar opens the Settings page, so the entry is a row there (after "Subtitles").
    function applyMenu() {
        var page = shownPage('myPreferencesMenuPage');
        var anchor = page && page.querySelector('a.lnkSubtitlePreferences');
        if (!anchor || !anchor.parentNode) { return; }
        var row = page.querySelector('.bfLanguagesRow');
        if (hasAvatarMenu() && !isTvLayout()) {
            if (row) { remove(row); }
            return;
        }
        if (row) {
            if (anchor.nextSibling !== row) { anchor.parentNode.insertBefore(row, anchor.nextSibling); }
            return;
        }
        ensureI18n().then(guard('menu row', function () {
            if (shownPage('myPreferencesMenuPage') !== page || page.querySelector('.bfLanguagesRow') || !anchor.parentNode || (hasAvatarMenu() && !isTvLayout())) { return; }
            ensureCss();
            // Same markup as the Subtitles row (built fresh so none of its handlers or ids come along).
            var link = document.createElement('a');
            link.className = (anchor.className || 'emby-button listItem-border').replace(/\blnkSubtitlePreferences\b/, '').replace(/\s+/g, ' ').trim() + ' bfLanguagesRow';
            link.setAttribute('style', anchor.getAttribute('style') || 'display:block;margin:0;padding:0');
            link.setAttribute('href', ROUTE);
            var inner = anchor.querySelector('.listItem');
            var item = inner ? inner.cloneNode(true) : el('div', { className: 'listItem' }, [
                el('span', { className: 'material-icons listItemIcon listItemIcon-transparent' }),
                el('div', { className: 'listItemBody' })
            ]);
            each(item.querySelectorAll('[id]'), function (n) { n.removeAttribute('id'); });
            var icon = item.querySelector('.listItemIcon');
            if (icon) {
                icon.className = 'material-icons listItemIcon listItemIcon-transparent translate';
                icon.textContent = '';
                icon.setAttribute('aria-hidden', 'true');
            }
            var body = item.querySelector('.listItemBody');
            if (body) {
                while (body.firstChild) { body.removeChild(body.firstChild); }
                body.appendChild(el('div', { className: 'listItemBodyText', text: T('client.languages') }));
            }
            link.appendChild(item);
            anchor.parentNode.insertBefore(link, anchor.nextSibling);
        }));
    }

    // The effective choice is fetched after a dialog save to update Jellyfin's own track dropdowns.
    function fetchChoice(itemId) {
        return apiGet('Babelfin/Me/Choice', { itemId: itemId }).then(function (list) {
            return list && list.length ? list[0] : null;
        }, function (e) {
            if (statusOf(e) !== 404) { warn('choice unavailable', e); }
            return null;
        });
    }

    function isSet(v) {
        return v !== null && v !== undefined;
    }

    function hasSomethingToSay(choice) {
        return !!choice && choice.Outcome !== 'None' && (isSet(choice.AudioIndex) || isSet(choice.SubtitleIndex));
    }

    // ── Override dialog ──────────────────────────────────────────────────

    // ui.js holds the table's dropdowns. It is fetched when an item menu is opened; a browser that can't run it gets no menu entry.
    // Each change of state schedules a pass, which lets a waiting menu catch up.
    function startUi() {
        if (!state.uiPromise) {
            state.uiPromise = loadScript(asset('ui.js'), 'BabelfinUI').then(function (ui) {
                schedule();
                return ui;
            });
        }
        return state.uiPromise;
    }

    // The dialog is only opened when ui.js is there, so this wait is for a slow connection only; it is long enough to show the dialog or
    // an honest message, never a "try again" for a file that has not arrived.
    function loadUi() {
        return withTimeout(startUi(), 10000);
    }

    // The Target endpoint takes a movie, a video, a show, and a season or an episode that belongs to a show (it answers 400 for the rest).
    var OVERRIDE_TYPES = { Movie: true, Video: true, Series: true, Season: true, Episode: true };

    // info: { Type, SeriesId }. A card carries its type but not its show (SeriesId undefined: the server decides, and the dialog shows its answer).
    function offersOverride(info) {
        return !!info && has(OVERRIDE_TYPES, info.Type) && (info.SeriesId !== null || (info.Type !== 'Season' && info.Type !== 'Episode'));
    }

    // The type, and the show of a season or an episode, of an item that is not on a card (the item page's own item). Failures are not remembered.
    function fetchItemInfo(itemId) {
        if (has(state.items, itemId)) { return Promise.resolve(state.items[itemId]); }
        var uid = currentUserId();
        var c = client();
        if (!c || !uid) { return Promise.resolve(null); }
        return Promise.resolve().then(function () {
            return typeof c.getItem === 'function' ? c.getItem(uid, itemId) : apiGet('Items/' + itemId, { userId: uid });
        }).then(function (item) {
            var info = item && item.Type ? { Type: item.Type, SeriesId: item.SeriesId || null } : null;
            if (info) { state.items[itemId] = info; }
            return info;
        }, function (e) {
            warn('item type unavailable', e);
            return null;
        });
    }

    function getLanguages() {
        if (state.languages) { return Promise.resolve(state.languages); }
        return apiGet('Babelfin/Languages').then(function (list) {
            state.languages = list || [];
            return state.languages;
        });
    }

    function sameId(a, b) {
        return !!a && !!b && String(a).replace(/-/g, '').toLowerCase() === String(b).replace(/-/g, '').toLowerCase();
    }

    function copyLayer(layer) {
        try {
            var copy = layer ? JSON.parse(JSON.stringify(layer)) : {};
            delete copy.IsEmpty;
            return copy;
        } catch (e) {
            return {};
        }
    }

    // Focus goes back to the control that opened the dialog; a card or a list row (right-click, long-press) is not focusable itself, so its
    // first focusable part stands in for it.
    var FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

    function isFocusable(node) {
        var tag = node.tagName;
        return tag === 'BUTTON' || tag === 'A' || tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA' || node.hasAttribute('tabindex');
    }

    function focusTarget(node) {
        if (!node || !document.body.contains(node)) { return null; }
        return isFocusable(node) ? node : (node.querySelector(FOCUSABLE) || node);
    }

    function closeDialog() {
        var d = state.dialog;
        if (!d) { return; }
        state.dialog = null;
        window.removeEventListener('keydown', d.onKey, true);
        remove(d.root);
        try {
            var back = focusTarget(d.returnFocus);
            if (back) { back.focus(); }
        } catch (e) { /* gone */ }
    }

    function toast(message) {
        try {
            if (window.BabelfinUI && typeof window.BabelfinUI.toast === 'function') { window.BabelfinUI.toast(message); return; }
        } catch (e) { /* fall back to the built-in toast */ }
        var node = el('div', { className: 'bfToast', role: 'status', text: message });
        document.body.appendChild(node);
        setTimeout(function () { remove(node); }, 3500);
    }

    // The controls Tab can reach: the ones on screen (a TV pick list hides the rest of the dialog).
    function trapFocus(box, e) {
        var items = Array.prototype.filter.call(box.querySelectorAll('input:not([disabled]), select:not([disabled]), button:not([disabled]), textarea, a[href]'), function (n) {
            return n.offsetWidth > 0 || n.offsetHeight > 0;
        });
        if (!items.length) { return; }
        var first = items[0];
        var last = items[items.length - 1];
        if (e.shiftKey && (document.activeElement === first || document.activeElement === box)) {
            e.preventDefault();
            last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
            e.preventDefault();
            first.focus();
        }
    }

    // The table helpers of ui.js, or null when ui.js didn't load (old browser, network) or lacks one of them.
    function tableApi() {
        var UI = window.BabelfinUI;
        if (!UI) { return null; }
        var names = ['tableChoice', 'tableAudioValue', 'tableSubtitleValue', 'writeTableAudio', 'writeTableSubtitles', 'setFields'];
        for (var i = 0; i < names.length; i++) {
            if (typeof UI[names[i]] !== 'function') { return null; }
        }
        return UI;
    }

    function choiceValue(choice) {
        var v = choice.getValue();
        return v === null || v === undefined ? '' : String(v);
    }

    function fireChange(select) {
        var evt;
        try {
            evt = new Event('change', { bubbles: true });
        } catch (e) {
            evt = document.createEvent('HTMLEvents');
            evt.initEvent('change', true, false);
        }
        select.dispatchEvent(evt);
    }

    // jellyfin-web puts its layout on <html> (layout-tv, layout-mobile, layout-desktop).
    function isTvLayout() {
        return document.documentElement.classList.contains('layout-tv');
    }

    // TV layout: jellyfin-web's keyboard navigation takes Up and Down for moving between controls, and jellyfin avoids native popups on TVs,
    // so a dropdown could only change through a popup the remote can't drive. Each choice of the dialog opens a list of buttons in the dialog
    // instead (see openPicker), on Enter, the remote's select key or a click; the dropdown itself stays what holds the value.
    function pickFromList(d, select, title) {
        function open() { openPicker(d, select, title); }
        select.addEventListener('mousedown', guard('picker mousedown', function (e) {
            e.preventDefault();
            select.focus();
        }));
        select.addEventListener('click', guard('picker click', function (e) {
            e.preventDefault();
            open();
        }));
        select.addEventListener('keydown', guard('picker key', function (e) {
            var code = e.keyCode || 0;
            if (e.key === 'Enter' || e.key === ' ' || e.key === 'Spacebar' || code === 13 || code === 32 || code === 195) {
                e.preventDefault();
                open();
            }
        }));
    }

    // The list replaces the dialog's content until a pick or Back/Escape: one button per option (aria-pressed marks the current one, which
    // has the focus), the name of an option group as a heading above its options, scrolling when it is long. A pick sets the dropdown's value
    // and fires its change, as the dropdown would; Back, Escape and Cancel return to the dialog with nothing changed.
    function openPicker(d, select, title) {
        if (d.picker || state.dialog !== d) { return; }
        var box = d.box;
        var openedAt = Date.now();
        var scroll = box.scrollTop;
        var groups = 0;
        var current = null;
        var list = el('div', { className: 'bfPickerList', role: 'group', 'aria-labelledby': 'bfPickerTitle' });

        function close() {
            if (d.picker !== picker) { return; }
            d.picker = null;
            box.classList.remove('bfPicking');
            remove(root);
            box.scrollTop = scroll;
            select.focus();
        }
        function addOption(parent, option) {
            var button = el('button', { type: 'button', className: 'bfPickerItem', 'aria-pressed': option.selected ? 'true' : 'false', text: option.textContent });
            // Same guard as the dialog's: the click that opened the list must not also land on an option under the pointer.
            button.addEventListener('click', guard('pick', function (e) {
                if (Date.now() - openedAt < 400 && e.detail > 0) { return; }
                close();
                if (select.value !== option.value) {
                    select.value = option.value;
                    fireChange(select);
                }
            }));
            if (option.selected) { current = button; }
            parent.appendChild(button);
        }

        each(select.children, function (child) {
            if (child.tagName === 'OPTGROUP') {
                groups++;
                var headingId = 'bfPickerGroup' + groups;
                var group = el('div', { className: 'bfPickerGroup', role: 'group', 'aria-labelledby': headingId }, [el('div', { id: headingId, className: 'bfPickerHeading', text: child.label })]);
                each(child.children, function (option) { addOption(group, option); });
                list.appendChild(group);
            } else if (child.tagName === 'OPTION') {
                addOption(list, child);
            }
        });
        var back = el('button', { type: 'button', className: 'bfButton', text: T('dialog.cancel'), onclick: close });
        var root = el('div', { className: 'bfPicker' }, [el('h3', { id: 'bfPickerTitle', className: 'bfPickerTitle', text: title }), list, el('div', { className: 'bfActions' }, [back])]);
        var notice = translationNotice();
        if (notice) { root.appendChild(notice); }
        var picker = { close: close };
        d.picker = picker;
        box.classList.add('bfPicking');
        box.appendChild(root);
        box.scrollTop = 0;
        (current || list.querySelector('button') || back).focus();
    }

    // The dialog for a show or a movie. itemId is any item of it: the server maps a season or an episode to its show.
    function openDialog(itemId, returnFocus) {
        closeDialog();
        ensureCss();
        var box = el('div', { className: 'bfDialog focuscontainer', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'bfDialogTitle', tabindex: '-1' }, [
            el('div', { className: 'bfLoading', text: T('common.loading') })
        ]);
        var root = el('div', { className: 'bfBackdrop' }, [box]);
        var d = { root: root, box: box, returnFocus: returnFocus, itemId: itemId, picker: null };
        d.onKey = function (e) {
            try {
                var key = e.key || '';
                var code = e.keyCode || 0;
                // Escape, plus the remote "back" keys some TV browsers send. With a TV pick list open they go back to the dialog.
                if (key === 'Escape' || key === 'Esc' || key === 'GoBack' || key === 'BrowserBack' || code === 27 || code === 461 || code === 10009) {
                    e.preventDefault();
                    e.stopPropagation();
                    if (d.picker) { d.picker.close(); } else { closeDialog(); }
                } else if (key === 'Tab' || code === 9) {
                    trapFocus(box, e);
                }
            } catch (err) {
                warn('dialog key', err);
            }
        };
        // A double-click on the menu entry, or a TV remote's tap-through, would land on whatever opens under the pointer:
        // ignore pointer clicks in the first moment after opening (keyboard activation has no pointer position and is kept).
        var openedAt = Date.now();
        root.addEventListener('click', function (e) {
            if (Date.now() - openedAt < 400 && e.detail > 0) {
                e.preventDefault();
                e.stopPropagation();
            }
        }, true);
        root.addEventListener('click', guard('backdrop', function (e) {
            if (e.target === root) { closeDialog(); }
        }));
        window.addEventListener('keydown', d.onKey, true);
        state.dialog = d;
        document.body.appendChild(root);
        box.focus();

        var none = function () { return null; };
        Promise.all([
            ensureI18n(),
            apiGet('Babelfin/Me/Overrides/Target/' + itemId),
            apiGet('Babelfin/Me/Settings').then(null, function (e) { warn('settings unavailable', e); return null; }),
            getLanguages().then(null, none),
            loadUi().then(null, none)
        ]).then(function (r) {
            if (state.dialog !== d) { return; }
            try {
                renderDialog(d, r[1], r[2], r[3]);
            } catch (e) {
                showFailure(d, e);
            }
        }, function (e) {
            ensureI18n().then(function () { showFailure(d, e); });
        });
    }

    function translationNotice() {
        return state.t && state.t.machineTranslated ? el('p', { className: 'bfNote' }, [el('a', { href: 'https://github.com/WizardBal/Babelfin/blob/main/CONTRIBUTING.md#translations', target: '_blank', rel: 'noopener', text: T('common.machineTranslated') })]) : null;
    }

    // The dialog's error state: what went wrong (when the server said) and Close; nothing else changes.
    function showFailure(d, err) {
        warn('dialog data unavailable', err);
        if (state.dialog !== d) { return; }
        problemText(err).then(function (detail) {
            if (state.dialog !== d) { return; }
            d.box.innerHTML = '';
            // A 400 is the server saying why this item has no override ("Overrides are for shows and movies only."): trying again won't change it.
            var text = statusOf(err) === 400 && detail ? detail : T('dialog.error') + (detail ? ' ' + detail : '');
            d.box.appendChild(el('p', { className: 'bfError', text: text }));
            var close = el('button', { type: 'button', className: 'bfButton', text: T('common.close'), onclick: closeDialog });
            d.box.appendChild(el('div', { className: 'bfActions' }, [close]));
            var notice = translationNotice();
            if (notice) { d.box.appendChild(notice); }
            close.focus();
        });
    }

    // target: GET Me/Overrides/Target/{itemId} (OverrideTarget). settingsView: GET Me/Settings (its burned-in flags and languages), or null.
    function renderDialog(d, target, settingsView, languages) {
        var box = d.box;
        box.innerHTML = '';
        var UI = tableApi();
        var burnedInKnown = !!(settingsView && settingsView.Settings);
        var doc = (settingsView && settingsView.Settings) || {};
        var everyday = doc.Everyday || {};
        var inherited = (target.Inherited && target.Inherited.Profile) || {};
        var override = target.Override || null;
        var stored = (override && override.Settings) || {};
        var library = target.LibraryName || '';
        // Only a real id (32 hex digits once the dashes are gone) is used for a single-override request.
        var targetId = plainId(target.TargetId) || '';
        var targetIdOk = /^[0-9a-f]{32}$/.test(targetId);
        var overrideUrl = 'Babelfin/Me/Overrides/' + targetId;
        var params = { scope: target.Scope };

        // The person's languages as the table's "My languages" group takes them: their own list when set, else what applies to this item.
        function mine(name) {
            var own = everyday[name];
            var list = isSet(own) && Array.isArray(own) ? own : inherited[name];
            return Array.isArray(list) ? list : [];
        }
        var understood = mine('Understood');
        var reads = mine('Reads');

        box.appendChild(el('h2', { id: 'bfDialogTitle', className: 'bfTitle', text: T('dialog.title') }));
        box.appendChild(el('div', { className: 'bfSubtitle' }, [el('span', { className: 'bfTarget', text: target.Name || '' }), library ? ' · ' + library : null]));
        var appliesType = target.Scope === 'Series' ? 'show' : target.Type === 'Video' ? 'video' : 'movie';
        box.appendChild(el('p', { className: 'bfExplain', text: T('dialog.applies.' + appliesType + (library ? '' : 'NoLibrary'), { library: library }) }));
        if (target.HasNarrower) { box.appendChild(el('p', { className: 'bfExplain', text: T('dialog.narrower') })); }

        // Audio and Subtitles: the settings table's own dropdowns. "Default (X)" reads from what applies without this override.
        function pick(kind, value, label) {
            return UI.tableChoice({
                t: T, kind: kind, audience: 'person', languages: languages || [], uiLanguage: state.lang,
                mine: understood, reads: reads, defaultLayer: inherited, value: value, label: label
            });
        }
        function field(id, label, choice) {
            if (choice.select) {
                choice.select.id = id;
                if (isTvLayout()) { pickFromList(d, choice.select, label); }
            }
            return el('div', { className: 'bfField' }, [el('label', { className: 'bfFieldLabel', 'for': id, text: label }), choice.element]);
        }
        var errorLine = el('p', { className: 'bfError', role: 'alert' });
        var audio = null;
        var subs = null;
        var audioStart = '';
        var subsStart = '';
        var fields = [];
        if (UI) {
            try {
                audio = pick('audio', UI.tableAudioValue(stored, understood), T('dialog.audio'));
                subs = pick('subtitles', UI.tableSubtitleValue(stored, reads), T('dialog.subtitles'));
                fields = [field('bfDialogAudio', T('dialog.audio'), audio), field('bfDialogSubtitles', T('dialog.subtitles'), subs)];
                // What the dropdowns show is the starting point: a stored choice the table has no option for ("Custom" that it cannot
                // show) is then never overwritten by a Save that changed nothing in that dropdown.
                audioStart = choiceValue(audio);
                subsStart = choiceValue(subs);
            } catch (e) {
                warn('table choice unavailable', e);
                audio = null;
                subs = null;
                fields = [];
            }
        }
        each(fields, function (f) { box.appendChild(f); });
        // No dropdowns (ui.js missing or failing): saying "try again" would promise what a retry can't do. Remove override still works.
        if (!audio || !subs) { errorLine.textContent = T('common.error'); }

        // Burned-in subtitles: the person's own languages (and those already flagged) in a list, and a free text field.
        var flag = find(doc.BurnedIn, function (f) { return sameId(f.ItemId, target.TargetId) && f.Scope === target.Scope; });
        var flagLang = flag && flag.Languages && flag.Languages[0];
        var burnedInLabel = T(target.Scope === 'Series' ? 'dialog.burnedIn.show' : target.Type === 'Movie' ? 'dialog.burnedIn.movie' : 'dialog.burnedIn.video');
        var burnCheck = el('input', { type: 'checkbox', className: 'bfCheck' });
        var burnText = el('input', { type: 'text', className: 'bfText', placeholder: T('dialog.burnedInOther'), 'aria-label': T('dialog.burnedInOther'), maxlength: '40' });
        var langSelect = el('select', { className: 'bfSelect', 'aria-label': burnedInLabel }, [el('option', { value: '', text: '—' })]);
        var seen = {};
        function addOption(code, english) {
            var key = canon(code);
            if (!key || key === 'und' || seen[key]) { return; }
            seen[key] = true;
            langSelect.appendChild(el('option', { value: code, text: langName(code, english) }));
        }
        if (audio && subs) {
            each([].concat(reads, understood, (target.Inherited && target.Inherited.BurnedInLanguages) || [], flagLang ? [flagLang] : []), function (code) {
                if (!code) { return; }
                var hit = find(languages, function (l) { return canon(l.Code) === canon(code); }) || find(languages, function (l) { return canon(l.Code) === baseOf(code); });
                addOption(hit ? hit.Code : code, hit ? hit.Name : null);
            });
            if (flagLang) {
                burnCheck.checked = true;
                each(langSelect.options, function (o) { if (canon(o.value) === canon(flagLang)) { langSelect.value = o.value; } });
            }
            box.appendChild(el('div', { className: 'bfGroup bfBurned' }, [
                el('label', { className: 'bfRadio' }, [burnCheck, el('span', { className: 'bfRadioLabel', text: burnedInLabel })]),
                el('div', { className: 'bfRow' }, [langSelect, burnText]),
                burnedInKnown ? null : el('p', { className: 'bfNote', role: 'status', text: T('dialog.error') })
            ]));
            if (!burnedInKnown) { burnCheck.disabled = true; langSelect.disabled = true; burnText.disabled = true; }
            burnText.addEventListener('input', guard('burned text', function () { if (burnText.value) { burnCheck.checked = true; } }));
            langSelect.addEventListener('change', guard('burned select', function () { if (langSelect.value) { burnCheck.checked = true; } }));
            // TV layout: the language list opens as an in-dialog pick list like the two choices above, not as a native popup the remote can't drive.
            if (burnedInKnown && isTvLayout()) { pickFromList(d, langSelect, burnedInLabel); }
        }

        box.appendChild(errorLine);
        var saveButton = audio && subs ? el('button', { type: 'button', className: 'bfButton bfButton-primary', text: T('dialog.save') }) : null;
        var removeButton = override ? el('button', { type: 'button', className: 'bfButton bfButton-danger', text: T('dialog.remove') }) : null;
        var cancelButton = el('button', { type: 'button', className: 'bfButton', text: T(saveButton ? 'dialog.cancel' : 'common.close'), onclick: closeDialog });
        box.appendChild(el('div', { className: 'bfActions' }, [removeButton, cancelButton, saveButton]));
        var notice = translationNotice();
        if (notice) { box.appendChild(notice); }

        function running(on) {
            if (saveButton) { saveButton.disabled = on; }
            if (removeButton) { removeButton.disabled = on; }
        }
        // partial: the override was written and what failed came after it (the burned-in subtitles), so the message must not say nothing was saved.
        function failed(e, partial) {
            running(false);
            warn('save failed', e);
            return problemText(e).then(function (detail) {
                errorLine.textContent = T(partial ? 'dialog.errorBurnedIn' : 'dialog.error') + (detail ? ' ' + detail : '');
            });
        }
        // A target without a real id is refused with the error line, and nothing is sent.
        function refuseWithoutId() {
            if (targetIdOk) { return false; }
            errorLine.textContent = T('common.error');
            return true;
        }
        function finished(message) {
            if (state.dialog === d) { closeDialog(); }
            toast(message);
            return afterChange(d.itemId, target.TargetId);
        }

        // Save: the dropdowns' table values go into a copy of the stored override, so every other field of it stays. An override with
        // nothing set (and no remembered track change) is deleted instead of stored empty.
        if (saveButton) {
            saveButton.addEventListener('click', guard('save', function () {
                errorLine.textContent = '';
                if (refuseWithoutId()) { return; }
                var audioValue = choiceValue(audio);
                var subsValue = choiceValue(subs);
                var layer = copyLayer(stored);
                if (audioValue !== audioStart) { UI.writeTableAudio(layer, audioValue); }
                if (subsValue !== subsStart) { UI.writeTableSubtitles(layer, subsValue); }

                // Once the override is written the dialog stands on what was written, so a second Save (after the burned-in part failed) sends only the rest.
                var written = false;
                function settled(next, exists) {
                    written = true;
                    stored = next;
                    override = exists ? { Settings: next, Manual: override && override.Manual } : null;
                    audioStart = audioValue;
                    subsStart = subsValue;
                }

                var ops = [];
                if (audioValue !== audioStart || subsValue !== subsStart) {
                    if (UI.setFields(layer).length > 0 || (override && override.Manual)) {
                        ops.push(function () { return apiSend('PUT', overrideUrl, params, layer).then(function () { settled(layer, true); }); });
                    } else if (override) {
                        ops.push(function () { return apiSend('DELETE', overrideUrl, params).then(function () { settled({}, false); }); });
                    }
                }
                var burnLang = (burnText.value || '').trim() || langSelect.value;
                if (burnedInKnown && burnCheck.checked && burnLang) {
                    if (!flagLang || canon(flagLang) !== canon(burnLang)) {
                        ops.push(function () { return apiSend('POST', 'Babelfin/Me/BurnedIn/' + targetId, null, { Languages: [burnLang], Scope: target.Scope }); });
                    }
                } else if (burnedInKnown && !burnCheck.checked && flag) {
                    ops.push(function () { return apiSend('DELETE', 'Babelfin/Me/BurnedIn/' + targetId, params); });
                }

                if (ops.length === 0) {
                    // Nothing to change: close without claiming a save.
                    closeDialog();
                    return;
                }

                running(true);
                var run = Promise.resolve();
                each(ops, function (op) { run = run.then(op); });
                run.then(function () { return finished(T('dialog.saved')); }, function (e) {
                    // A stored override changes what plays, so the page's dropdowns follow it even though the dialog stays open.
                    return failed(e, written).then(function () { return written ? afterChange(d.itemId, target.TargetId) : null; });
                }).then(null, function (e) { warn('save', e); });
            }));
        }

        // Remove override: the whole override, remembered track change included.
        if (removeButton) {
            removeButton.addEventListener('click', guard('remove', function () {
                errorLine.textContent = '';
                if (refuseWithoutId()) { return; }
                running(true);
                apiSend('DELETE', overrideUrl, params).then(function () { return finished(T('dialog.removed')); }, failed).then(null, function (e) { warn('remove', e); });
            }));
        }

        var first = (audio && audio.select) || box.querySelector('select, input, button');
        if (first) { first.focus(); }
    }

    // The show or movie an override for this item is stored under (the same mapping as the server's Target endpoint), when the item is known.
    function targetOf(itemId) {
        var info = state.items[itemId];
        if (!info) { return null; }
        return info.Type === 'Season' || info.Type === 'Episode' ? info.SeriesId : itemId;
    }

    // After a save or a removal: point the page's dropdowns at the new choice (Play sends those values) when the change
    // was for what the page shows: the item the dialog was opened for is the page's, or both belong to the same show. A change for some
    // other title (a "More like this" card) leaves the page's dropdowns as the person set them.
    function afterChange(dialogItemId, targetId) {
        var page = shownPage('itemDetailPage');
        var itemId = detailItemId();
        if (!page || !itemId) { return Promise.resolve(); }
        // A dialog opened from a card may belong to the page item's show. Resolve that relationship on demand.
        return Promise.all([fetchItemInfo(itemId), fetchChoice(itemId)]).then(guard('after change', function (r) {
            if (shownPage('itemDetailPage') !== page || detailItemId() !== itemId) { return; }
            if (sameId(dialogItemId, itemId) || sameId(targetId, targetOf(itemId))) { syncTrackSelects(page, itemId, r[1]); }
        }));
    }

    // The page's own track dropdowns exist only on a playable item's page, and their values are stream indexes of one media source.
    function syncTrackSelects(page, itemId, choice) {
        if (!hasSomethingToSay(choice) || !sameId(choice.ForItemId || itemId, itemId)) { return; }
        var sourceSelect = page.querySelector('select.selectSource');
        if (sourceSelect && sourceSelect.value && choice.SourceId && !sameId(sourceSelect.value, choice.SourceId)) { return; }
        setTrackSelect(page.querySelector('select.selectAudio'), choice.AudioIndex);
        setTrackSelect(page.querySelector('select.selectSubtitles'), isSet(choice.SubtitleIndex) ? choice.SubtitleIndex : -1);
    }

    // The options carry the stream index as their value (the subtitles start with "Off", -1).
    function setTrackSelect(select, index) {
        if (!select || !isSet(index)) { return; }
        var target = -1;
        each(select.options, function (o, i) { if (target < 0 && o.value === String(index)) { target = i; } });
        if (target < 0 || select.selectedIndex === target) { return; }
        select.selectedIndex = target;
        fireChange(select);
    }

    // ── "Audio and subtitles (Babelfin)" in the item "⋮" menu ─────────────

    // jellyfin-web 12.2's item menu is its legacy action sheet: a dialog (div.dialog.actionSheet inside a div.dialogContainer) of
    // button.actionSheetMenuItem[data-id] rows and div.actionsheetDivider lines. The sheet says nothing about its item, so the click (or
    // right-click / long-press, or the TV remote's "menu" command) that opens it is noted first, in the capture phase; a sheet that
    // appears within MENU_WINDOW of it is taken to belong to that item, when it has the rows of an item menu (sort, filter and user
    // menus are sheets too and get nothing).
    var MENU_WINDOW = 3000;
    var SHEET_CLOSE_WAIT = 800;
    // data-id of the rows (lower case): the play group at the top, and the other rows an item menu has.
    var PLAY_ROWS = ['resume', 'play', 'playallfromhere', 'stopplayback', 'clearqueue', 'queue', 'queuenext', 'shuffle', 'instantmix'];
    var ITEM_ROWS = PLAY_ROWS.concat(['multiselect', 'addtocollection', 'addtoplaylist', 'download', 'delete', 'edit', 'editimages', 'editsubtitles', 'identify', 'refresh', 'moremediainfo']);

    function ancestor(node, test) {
        for (var n = node; n && n.nodeType === 1; n = n.parentNode) {
            if (test(n)) { return n; }
        }
        return null;
    }

    function plainId(id) {
        return id ? String(id).replace(/-/g, '').toLowerCase() : null;
    }

    function isItemNode(n) {
        return n.hasAttribute('data-id') && n.hasAttribute('data-type');
    }

    function isMenuButton(n) {
        return n.getAttribute('data-action') === 'menu' || n.classList.contains('btnCardOptions') || n.classList.contains('btnMoreCommands');
    }

    function rowId(row) {
        return String(row.getAttribute('data-id') || '').toLowerCase();
    }

    // Notes which item the next action sheet would be for: node is where the event happened, button the menu button when it was one.
    // trigger.node is where focus goes back to: the button, else the target when it can take focus, else its card or list row (a right-click
    // lands on the card's image, which cannot). The dialog is made of ui.js, so loading it starts now.
    function noteMenu(node, button) {
        var trigger = null;
        if (button && button.classList.contains('btnMoreCommands')) {
            // The item page's more button: the page's item (its type is not on the page, so it is asked for).
            var pageId = detailItemId();
            if (pageId) { trigger = { id: pageId, info: null, node: button }; }
        } else {
            var card = ancestor(node, isItemNode);
            var type = card ? card.getAttribute('data-type') : null;
            if (card && has(OVERRIDE_TYPES, type) && plainId(card.getAttribute('data-id'))) {
                trigger = { id: plainId(card.getAttribute('data-id')), info: { Type: type, SeriesId: undefined }, node: button || (isFocusable(node) ? node : card) };
            }
        }
        if (trigger) {
            trigger.at = Date.now();
            if (!trigger.info) { trigger.lookup = fetchItemInfo(trigger.id); }
            startUi();
        }
        state.trigger = trigger;
    }

    function onMenuClick(e) {
        var entry = ancestor(e.target, function (n) { return n.getAttribute('href') === ROUTE; });
        if (entry && String(location.hash).toLowerCase().indexOf(ROUTE) !== 0) { panel.returnFocus = entry; panel.returnRoute = location.hash; }

        var button = ancestor(e.target, isMenuButton);
        // Any other click means the next sheet is not an item menu.
        if (button) { noteMenu(button, button); } else { state.trigger = null; }
    }

    function onMenuContext(e) {
        noteMenu(e.target, null);
    }

    function onMenuCommand(e) {
        if (e.detail && e.detail.command === 'menu') { noteMenu(e.target, null); }
    }

    // Looks for a new action sheet that belongs to the noted trigger.
    function applySheet() {
        var trigger = state.trigger;
        if (!trigger) { return; }
        if (Date.now() - trigger.at > MENU_WINDOW) {
            state.trigger = null;
            return;
        }
        var sheet = find(document.querySelectorAll('.actionSheet'), function (s) { return !s.getAttribute('data-bf-menu'); });
        if (!sheet) { return; }
        sheet.setAttribute('data-bf-menu', '1');
        state.trigger = null;
        if (!find(sheet.querySelectorAll('.actionSheetMenuItem'), function (r) { return ITEM_ROWS.indexOf(rowId(r)) >= 0; })) { return; }
        // The entry waits for ui.js (the dialog is made of it) and is left out when it can't load. A sheet still open when it arrives gets the entry then.
        Promise.all([ensureI18n(), trigger.info || trigger.lookup, startUi()]).then(guard('menu entry', function (r) {
            if (offersOverride(r[1]) && tableApi()) { addSheetRow(sheet, trigger); }
        }), function (e) { warn('menu entry', e); });
    }

    // jellyfin-web places a sheet that belongs to a button (position fixed, left and top) once, from the size it has then. A row added after
    // that makes it taller and wider, so its clamp is run again (positionTo in the action sheet's chunk): the sheet ends no closer than 20 px
    // to the right and bottom edges of the window, and 10 px to the left and top. Layout sizes, as jellyfin reads them: the sheet may still
    // be scaling in.
    function keepSheetOnScreen(sheet) {
        if (sheet.style.position !== 'fixed') { return; }
        var left = parseFloat(sheet.style.left);
        var top = parseFloat(sheet.style.top);
        if (isNaN(left) || isNaN(top)) { return; }
        sheet.style.left = Math.max(Math.min(left, window.innerWidth - 20 - sheet.offsetWidth), 10) + 'px';
        sheet.style.top = Math.max(Math.min(top, window.innerHeight - 20 - sheet.offsetHeight), 10) + 'px';
    }

    // The entry: a copy of one of the sheet's own rows (so it looks exactly like them), after "Add to playlist" when there is one; else at
    // the end of the play group (before the first divider after its rows); else last.
    function addSheetRow(sheet, trigger) {
        if (!document.body.contains(sheet) || sheet.classList.contains('hide') || sheet.querySelector('.bfSheetItem')) { return; }
        var rows = sheet.querySelectorAll('.actionSheetMenuItem');
        var anchor = find(rows, function (r) { return rowId(r) === 'addtoplaylist'; });
        var template = anchor || rows[0];
        if (!template || !template.parentNode) { return; }
        ensureCss();
        var row = template.cloneNode(true);
        row.removeAttribute('id');
        row.removeAttribute('autofocus');
        each(row.querySelectorAll('[id]'), function (n) { n.removeAttribute('id'); });
        each(row.querySelectorAll('.secondary, .listItemAside'), remove);
        row.setAttribute('data-id', 'babelfin-override');
        row.classList.add('bfSheetItem');
        var icon = row.querySelector('.actionsheetMenuItemIcon');
        if (icon) {
            icon.className = 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons translate';
            icon.removeAttribute('style');
            icon.textContent = '';
        }
        var text = row.querySelector('.actionSheetItemText') || row.querySelector('.listItemBodyText');
        if (text) {
            text.textContent = T('client.menu.override');
        } else {
            row.textContent = T('client.menu.override');
        }

        // The sheet's own handler (a click listener on the sheet) takes the row's data-id for one of its commands, closes the sheet and
        // runs the item menu's command switch, which rejects an unknown id. The row keeps the actionSheetMenuItem class (Jellyfin's CSS
        // styles the rows by it), so the click is stopped here, on the way down, before it can reach that listener.
        row.addEventListener('click', guard('menu entry click', function (e) {
            e.stopPropagation();
            e.preventDefault();
            if (sheet.getAttribute('data-bf-chosen')) { return; }
            sheet.setAttribute('data-bf-chosen', '1');
            closeSheet(sheet, guard('menu dialog', function () { openDialog(trigger.id, trigger.node); }));
        }), true);

        var lastPlay = null;
        each(rows, function (r) { if (PLAY_ROWS.indexOf(rowId(r)) >= 0) { lastPlay = r; } });
        var divider = lastPlay ? lastPlay.nextSibling : null;
        while (divider && !(divider.classList && divider.classList.contains('actionsheetDivider'))) { divider = divider.nextSibling; }
        if (anchor) {
            anchor.parentNode.insertBefore(row, anchor.nextSibling);
        } else if (divider) {
            divider.parentNode.insertBefore(row, divider);
        } else {
            var last = rows[rows.length - 1];
            last.parentNode.insertBefore(row, last.nextSibling);
        }
        keepSheetOnScreen(sheet);
    }

    function fireMouse(node, type) {
        var evt;
        try {
            evt = new MouseEvent(type, { bubbles: true, cancelable: true, view: window });
        } catch (e) {
            evt = document.createEvent('MouseEvents');
            evt.initEvent(type, true, true);
        }
        node.dispatchEvent(evt);
    }

    // Closes a jellyfin-web action sheet, then calls done. A sheet is a dialogHelper dialog, which pushes a history entry when it opens
    // (data-history) and pops it when it closes, from its own close(): the sheet's rows end in that close(), and so does a click on the
    // dimmed area around the sheet, which is the dialog container (mousedown and click on it, both on the container itself). That is the
    // option used here: it is the same close() the sheet's own rows run, so the entry is popped, the sheet animates away and is
    // removed, and the page stays where it is. Escape does nothing for a sheet outside the TV layout, and history.back() would
    // navigate away from the page if a sheet ever had no entry, so neither is used. Should the sheet not react (a jellyfin-web that
    // handles the container differently), it is left alone (the person closes it) and done still runs. done runs when the sheet reports
    // that it closed (after its history entry is gone), or after SHEET_CLOSE_WAIT at the latest.
    function closeSheet(sheet, done) {
        var finished = false;
        var closing = false;
        var timer = null;
        function finish() {
            if (finished) { return; }
            finished = true;
            clearTimeout(timer);
            sheet.removeEventListener('close', finish);
            done();
        }
        sheet.addEventListener('closing', function () { closing = true; });
        sheet.addEventListener('close', finish);
        timer = setTimeout(finish, SHEET_CLOSE_WAIT);
        try {
            var container = sheet.dialogContainer || sheet.parentNode;
            if (container && container.classList && container.classList.contains('dialogContainer')) {
                fireMouse(container, 'mousedown');
                fireMouse(container, 'click');
            }
        } catch (e) {
            warn('action sheet close', e);
        }
        if (!closing && !sheet.classList.contains('hide')) {
            warn('the action sheet did not close');
            finish();
        }
    }

    // ── Babelfin Settings under Jellyfin's header (#/babelfin) ───────────

    // jellyfin-web has no route for a person's plugin page (plugin configuration pages are admin-only), and any unknown route shows its
    // "Page not found" page inside the normal layout, header included. At #/babelfin that page's text is hidden and a panel below the
    // header shows Babelfin Settings in a frame. The panel is not one of jellyfin-web's pages, so leaving the route can first ask about
    // unsaved changes (window.BabelfinApp.confirmLeave in the frame); staying goes back to #/babelfin.
    var ROUTE = '#/babelfin';
    var panel = { el: null, frame: null, leaving: false, returnFocus: null, returnRoute: null };
    window.Babelfin.closeSettings = function () {
        if (!panel.el) { return; }
        var app = frameApp(), asked;
        try { asked = Promise.resolve(app && app.confirmLeave ? app.confirmLeave() : true); } catch (e) { warn('leave check', e); asked = Promise.resolve(true); }
        asked.then(function (leave) {
            if (!leave) { return; }
            var target = panel.returnFocus, route = panel.returnRoute || '#/mypreferencesmenu';
            if (String(route).toLowerCase().indexOf(ROUTE) === 0) { route = '#/mypreferencesmenu'; }
            hidePanel();
            location.hash = route;
            var tries = 0;
            (function returnFocus() {
                var live = focusTarget(target);
                if (!live || !visibleBox(live)) { live = document.querySelector('#myPreferencesMenuPage:not(.hide) .bfLanguagesRow'); }
                if (!live || !visibleBox(live)) { live = document.querySelector('[aria-controls="app-user-menu"]'); }
                if (live && visibleBox(live) && !onSettingsRoute()) { live.focus(); panel.returnFocus = null; return; }
                if (++tries < 60) { setTimeout(returnFocus, 50); }
            })();
        });
    };

    function onSettingsRoute() {
        var h = String(location.hash || '').toLowerCase();
        return h === ROUTE || h.indexOf(ROUTE + '?') === 0 || h.indexOf(ROUTE + '/') === 0;
    }

    // The avatar menu of the modern layout closes on a click of its backdrop (or Escape), as it does for its own entries.
    function closeUserMenu(menu) {
        var root = menu && (menu.closest ? menu.closest('.MuiModal-root, .MuiPopover-root') : null);
        var backdrop = root && root.querySelector('.MuiBackdrop-root');
        if (backdrop) {
            backdrop.click();
            return;
        }
        try {
            menu.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        } catch (e) { /* old browsers: the menu closes when the page changes */ }
    }

    function visibleBox(node) {
        if (!node) { return null; }
        var r = node.getBoundingClientRect();
        var cs = window.getComputedStyle(node);
        return r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden' ? r : null;
    }

    // The panel fills the window below Jellyfin's header (the modern AppBar or the legacy .skinHeader) and above the now-playing bar.
    function placePanel() {
        if (!panel.el) { return; }
        var top = 0;
        each(document.querySelectorAll('.MuiAppBar-root, .skinHeader'), function (n) {
            var r = visibleBox(n);
            if (r && r.top <= 1) { top = Math.max(top, r.bottom); }
        });
        var bar = visibleBox(document.querySelector('.nowPlayingBar:not(.hide)'));
        var bottom = bar && bar.top < window.innerHeight ? window.innerHeight - bar.top : 0;
        panel.el.style.top = Math.max(0, Math.round(top)) + 'px';
        panel.el.style.bottom = Math.max(0, Math.round(bottom)) + 'px';
    }

    function settingsTitle() {
        var title = T('client.languages');
        if (document.title !== title) { document.title = title; }
        each(document.querySelectorAll('.skinHeader .pageTitle'), function (n) {
            if (n.textContent !== title && !n.querySelector('img')) { n.textContent = title; }
        });
    }

    function showPanel() {
        ensureCss();
        if (!panel.el) {
            panel.returnFocus = panel.returnFocus || document.activeElement;
            panel.frame = el('iframe', { className: 'bfSettingsFrame', title: T('client.languages'), src: BASE + '/Babelfin/app?embedded=1' });
            panel.frame.addEventListener('load', guard('settings focus', function () {
                if (!document.documentElement.classList.contains('layout-tv') || !panel.frame) { return; }
                panel.frame.focus();
                var app = frameApp();
                if (app && app.focusFirst) { app.focusFirst(); }
            }));
            panel.el = el('div', { className: 'bfSettingsPanel' }, [panel.frame]);
            document.body.appendChild(panel.el);
            window.addEventListener('resize', placePanel);
        }
        document.documentElement.classList.add('bfSettingsRoute');
        placePanel();
        settingsTitle();
    }

    function hidePanel() {
        remove(panel.el);
        panel.el = null;
        panel.frame = null;
        window.removeEventListener('resize', placePanel);
        document.documentElement.classList.remove('bfSettingsRoute');
    }

    function frameApp() {
        try {
            return panel.frame && panel.frame.contentWindow ? panel.frame.contentWindow.BabelfinApp || null : null;
        } catch (e) {
            return null;
        }
    }

    function applySettingsRoute() {
        if (panel.leaving) { return; }
        if (onSettingsRoute()) {
            showPanel();
            return;
        }
        if (!panel.el) { return; }
        var app = frameApp(), dirty = false;
        try { dirty = !!(app && typeof app.hasChanges === 'function' && app.hasChanges()); } catch (e) { warn('unsaved check', e); }
        if (!dirty) {
            hidePanel();
            return;
        }
        // Something is unsaved: the settings stay on screen and ask; "Stay" goes back to the route.
        panel.leaving = true;
        var asked;
        try { asked = Promise.resolve(app.confirmLeave()); } catch (e) { asked = Promise.reject(e); }
        asked.then(function (leave) {
            panel.leaving = false;
            if (leave) {
                hidePanel();
            } else {
                location.hash = ROUTE;
            }
            schedule();
        }, function () {
            panel.leaving = false;
            hidePanel();
        });
    }

    // ── Wiring ───────────────────────────────────────────────────────────

    function apply() {
        state.queued = false;
        try { applySettingsRoute(); } catch (e) { warn('settings route', e); }
        try { if (!panel.el && !onSettingsRoute()) { document.documentElement.classList.remove('bfSettingsRoute'); } } catch (e) { /* no panel owns the class */ }
        try { applyMenu(); } catch (e) { warn('menu', e); }
        try { applyUserMenu(); } catch (e) { warn('user menu', e); }
        try { applySheet(); } catch (e) { warn('menu entry', e); }
    }

    function schedule() {
        if (state.queued) { return; }
        state.queued = true;
        (window.requestAnimationFrame || function (fn) { return setTimeout(fn, 16); })(apply);
    }

    function whenReady(fn) {
        var tries = 0;
        (function poll() {
            if (currentUserId()) {
                guard('ready', fn)();
            } else if (++tries < 1200) {
                setTimeout(poll, 250);
            }
        })();
    }

    function start() {
        try {
            document.addEventListener('viewshow', guard('viewshow', schedule));
            // The item "⋮" menu: what opens an action sheet is noted before the sheet exists (capture phase, ahead of jellyfin-web's own handlers).
            document.addEventListener('click', guard('menu click', onMenuClick), true);
            document.addEventListener('contextmenu', guard('menu contextmenu', onMenuContext), true);
            document.addEventListener('command', guard('menu command', onMenuCommand), true);
            window.addEventListener('hashchange', guard('hashchange', function () {
                closeDialog();
                applySettingsRoute();
                schedule();
            }));
            new MutationObserver(guard('observer', schedule)).observe(document.body, { childList: true, subtree: true });
            schedule();
            whenReady(function () {
                ensureI18n().then(null, function (e) { warn('translations', e); });
                schedule();
            });
        } catch (e) {
            warn('start', e);
        }
    }

    try {
        if (document.body) {
            start();
        } else {
            document.addEventListener('DOMContentLoaded', guard('start', start));
        }
    } catch (e) {
        warn('boot', e);
    }
})();
