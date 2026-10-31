/* Babelfin shared UI: window.BabelfinUI.
   Reusable pieces for the person's page (app.js), the admin dashboard (dashboard.js) and the injected dialog (client.js):
   an API helper, the "Audio and subtitles" table with its two modes (libraryTable), the table's value readers (tableAudioValue,
   tableSubtitleValue), one column of that table as a dropdown with inherited defaults (tableChoice),
   the list of a person's show and movie overrides (overridesTable), collapsible groups (disclosure, reveal), a labelled
   dropdown of plain options (fieldSelect), the language picker and lists, an ordered list, a Jellyfin item
   search, a confirmation dialog and toasts.
   Plain ES2017 on purpose (old TV browsers run it: no ??, ?. or object spread), no framework, no build step, no external resources.
   Every visible string comes from the caller's translation function t(key, params) (keys "ui.*", "field.*", "enum.*" in
   Assets/i18n/<lang>.json). Styles are injected once, scoped to .bfui-* classes, through --bf-* variables that fall back to Jellyfin's
   theme variables, so nothing relies on the page's body styles.
   Data shapes are the server's (PascalCase, enums as names): SettingsLayer, PersonSettings, LanguageDto, BaseItemDto. */
(function (global) {
    'use strict';

    if (global.BabelfinUI) {
        return;
    }

    // ----- Model facts (the server's enum names the pages show, and SettingsFields in display order) -----

    const ENUMS = Object.freeze({
        Preference: ['Prefer', 'Neutral', 'Avoid'],
        FormatPreference: ['PreferText', 'Neutral', 'TextOnly'],
        EmbeddedPreference: ['Neutral', 'PreferEmbedded', 'PreferExternal'],
        WhenNothingMatches: ['ShowNothing', 'PartialInMyLanguage', 'FullInAnyBackupLanguage'],
        UnlabelledAudioRule: ['Unknown', 'OriginalLanguage', 'LibraryUsualOriginalLanguage', 'SpecificLanguage', 'OriginalWhenOnlyTrack'],
        MemoryScope: ['RestOfShow', 'Season', 'TitleOnly', 'DontRemember']
    });

    // type: languages | bool | enum | chain | tieBreaks | unlabelled; group: languages | audio | subtitles | memory
    const FIELDS = Object.freeze([
        { name: 'Understood', type: 'languages', group: 'languages' },
        { name: 'Reads', type: 'languages', group: 'languages' },
        { name: 'UsesAudioDescription', type: 'bool', group: 'languages' },
        { name: 'PrefersSdh', type: 'bool', group: 'languages' },
        { name: 'AudioChain', type: 'chain', group: 'audio' },
        { name: 'ForeignTitles', type: 'enum', enumName: 'ForeignTitlesRule', group: 'audio' },
        { name: 'AvoidCommentary', type: 'bool', group: 'audio' },
        { name: 'TieBreaks', type: 'tieBreaks', group: 'audio' },
        { name: 'UnlabelledAudio', type: 'unlabelled', enumName: 'UnlabelledAudioRule', group: 'audio' },
        { name: 'OriginalSource', type: 'enum', enumName: 'OriginalLanguageSource', group: 'audio' },
        { name: 'WhenAudioUnderstood', type: 'enum', enumName: 'SubtitleWant', group: 'subtitles' },
        { name: 'WhenAudioNotUnderstood', type: 'enum', enumName: 'SubtitleWant', group: 'subtitles' },
        { name: 'UnknownAudioCountsAsUnderstood', type: 'bool', group: 'subtitles' },
        { name: 'SubtitleLanguages', type: 'languages', group: 'subtitles' },
        { name: 'Sdh', type: 'enum', enumName: 'Preference', group: 'subtitles' },
        { name: 'Format', type: 'enum', enumName: 'FormatPreference', group: 'subtitles' },
        { name: 'Embedded', type: 'enum', enumName: 'EmbeddedPreference', group: 'subtitles' },
        { name: 'IfNothingMatches', type: 'enum', enumName: 'WhenNothingMatches', group: 'subtitles' },
        { name: 'Memory', type: 'enum', enumName: 'MemoryScope', group: 'memory' }
    ]);

    /** Whether a field is set in a layer (null and absent mean "use the default"). */
    function isFieldSet(layer, name) {
        return !!layer && layer[name] !== null && layer[name] !== undefined;
    }

    /** Unsets a field (UnlabelledAudio carries its language with it). */
    function clearField(layer, name) {
        delete layer[name];
        if (name === 'UnlabelledAudio') {
            delete layer.UnlabelledAudioLanguage;
        }
    }

    function deepCopy(value) {
        return value === null || value === undefined ? value : JSON.parse(JSON.stringify(value));
    }

    /** The names of the fields a layer sets, in display order. */
    function setFields(layer) {
        return FIELDS.filter(function (f) { return isFieldSet(layer, f.name); }).map(function (f) { return f.name; });
    }

    /** Maps a validation path's first segment ("audioChain[2].language", "unlabelledAudioLanguage") to a field name. */
    function fieldForPath(rest) {
        const segment = String(rest || '').split(/[.[]/)[0].toLowerCase();
        if (segment === 'unlabelledaudiolanguage') {
            return 'UnlabelledAudio';
        }
        const field = FIELDS.find(function (f) { return f.name.toLowerCase() === segment; });
        return field ? field.name : null;
    }

    // ----- DOM helpers -----

    let idCounter = 0;
    function uid(prefix) {
        idCounter += 1;
        return (prefix || 'bfui') + '-' + idCounter;
    }

    const DOM_PROPERTIES = new Set(['value', 'checked', 'disabled', 'selected', 'hidden', 'open', 'multiple']);

    /** Creates an element: h('button', { class, text, onclick, 'aria-label' }, child, [children], 'text'). Text is never parsed as HTML. */
    function h(tag, attrs) {
        const node = document.createElement(tag);
        if (attrs) {
            Object.keys(attrs).forEach(function (key) {
                const value = attrs[key];
                if (value === null || value === undefined || value === false) {
                    return;
                }
                if (key === 'class') {
                    node.className = value;
                } else if (key === 'text') {
                    node.textContent = String(value);
                } else if (key === 'dataset') {
                    Object.assign(node.dataset, value);
                } else if (key.slice(0, 2) === 'on' && typeof value === 'function') {
                    node.addEventListener(key.slice(2).toLowerCase(), value);
                } else if (DOM_PROPERTIES.has(key)) {
                    node[key] = value;
                } else {
                    node.setAttribute(key, value === true ? '' : String(value));
                }
            });
        }
        for (let i = 2; i < arguments.length; i++) {
            append(node, arguments[i]);
        }
        return node;
    }

    function append(node, child) {
        if (child === null || child === undefined || child === false) {
            return;
        }
        if (Array.isArray(child)) {
            child.forEach(function (c) { append(node, c); });
        } else if (typeof child === 'object' && typeof child.nodeType === 'number') {
            node.appendChild(child);
        } else {
            node.appendChild(document.createTextNode(String(child)));
        }
    }

    function clear(node) {
        while (node && node.firstChild) {
            node.removeChild(node.firstChild);
        }
    }

    // The translator used when a builder gets no options.t (set once with BabelfinUI.setTranslator(t)).
    let defaultT = null;

    /** Translates through the caller's t, else the default translator; with neither, the key itself. */
    function tr(t, key, params) {
        const fn = typeof t === 'function' ? t : defaultT;
        return typeof fn === 'function' ? fn(key, params) : key;
    }

    /** Sets the translator used by builders called without options.t (and by confirm(message) with a plain string). */
    function setTranslator(t) {
        defaultT = typeof t === 'function' ? t : null;
    }

    function uiLanguage(o) {
        return (o && (o.uiLanguage || (o.t && o.t.language))) || (typeof navigator !== 'undefined' && navigator.language) || 'en';
    }

    /**
     * A labelled on/off switch. options: { t, label, help, info, checked, disabled, onChange(checked) }; info adds an infoTip after the label.
     * Returns { element, input }.
     */
    function switchControl(options) {
        const o = options || {};
        const id = uid('bfui-sw');
        const helpId = o.help ? uid('bfui-swh') : null;
        const tip = o.info ? infoTip({ t: o.t, label: o.label, text: o.info }) : null;
        const input = h('input', {
            type: 'checkbox', id: id, class: 'bfui-switch-input', checked: !!o.checked, disabled: !!o.disabled,
            'aria-describedby': [helpId, tip && tip.tipId].filter(Boolean).join(' ') || null
        });
        input.addEventListener('change', function () {
            if (o.onChange) {
                o.onChange(input.checked);
            }
        });
        const element = h('div', { class: 'bfui bfui-switch' },
            h('div', { class: 'bfui-switch-line' },
                h('label', { for: id, class: 'bfui-switch-label' }, input, h('span', { class: 'bfui-switch-track', 'aria-hidden': 'true' }), h('span', { text: o.label || '' })),
                tip ? tip.element : null),
            o.help ? h('div', { class: 'bfui-help', id: helpId, text: o.help }) : null);
        return { element: element, input: input };
    }

    // The infoTip that is showing, so opening another one closes it.
    let shownTip = null;

    /**
     * infoTip(options): a small "i" button that explains a setting. The explanation shows while the pointer is over the button or the
     * button has keyboard focus, and stays after a click or tap until Escape, a click elsewhere or another click on the button. While it
     * shows it is moved to the end of <body> (above everything on the page) and put back when hidden. It is
     * also the button's accessible description; pass tipId to the control's aria-describedby so screen readers read it with the control.
     * options: { t, label (the setting's name, for "More about …"), text }. Returns { element, button, tip, tipId }.
     */
    function infoTip(options) {
        const o = options || {};
        const tipId = uid('bfui-tip');
        const button = h('button', {
            type: 'button', class: 'bfui-info', 'aria-label': tr(o.t, 'ui.info.about', { name: o.label || '' }), 'aria-describedby': tipId, 'aria-expanded': 'false'
        }, h('span', { class: 'bfui-info-mark', 'aria-hidden': 'true', text: 'i' }));
        const tip = h('span', { class: 'bfui-tip', id: tipId, role: 'tooltip', hidden: true, text: o.text || '' });
        const element = h('span', { class: 'bfui-info-wrap' }, button, tip);
        let hovered = false;
        let pinned = false;
        let watch = null;

        function place() {
            const margin = 8;
            const r = button.getBoundingClientRect();
            const width = Math.min(tip.offsetWidth || 0, window.innerWidth - 2 * margin);
            const left = Math.max(margin, Math.min(r.left - 12, window.innerWidth - width - margin));
            let top = r.bottom + 6;
            if (top + tip.offsetHeight > window.innerHeight - margin && r.top - tip.offsetHeight - 6 >= margin) {
                top = r.top - tip.offsetHeight - 6;
            }
            tip.style.left = Math.round(left) + 'px';
            tip.style.top = Math.round(top) + 'px';
        }

        function onOutside(e) {
            if (!element.contains(e.target)) {
                pinned = false;
                hovered = false;
                update();
            }
        }

        function onKey(e) {
            if (e.key === 'Escape' && !tip.hidden) {
                // Consumed, like the confirmation dialog does: jellyfin-web takes Escape as "go back" and would leave the page.
                e.preventDefault();
                e.stopPropagation();
                pinned = false;
                hovered = false;
                update();
            }
        }

        function update() {
            const show = (pinned || hovered) && element.isConnected;
            if (show === !tip.hidden) {
                if (show) {
                    place();
                }
                return;
            }
            // While it shows, the tip lives at the end of <body>, so no stacking context of the page (a table header, a positioned row)
            // can draw over it; it goes back next to its button when hidden. It also hides when its button leaves the page.
            if (show) {
                document.body.appendChild(tip);
                watch = setInterval(function () {
                    if (!element.isConnected) {
                        hideNow();
                    }
                }, 400);
            } else {
                clearInterval(watch);
                watch = null;
                if (tip.parentNode !== element) {
                    element.appendChild(tip);
                }
            }
            tip.hidden = !show;
            button.setAttribute('aria-expanded', show ? 'true' : 'false');
            if (show) {
                if (shownTip && shownTip !== hideNow) {
                    shownTip();
                }
                shownTip = hideNow;
                place();
                document.addEventListener('pointerdown', onOutside, true);
                document.addEventListener('keydown', onKey, true);
                window.addEventListener('scroll', place, true);
                window.addEventListener('resize', place);
            } else {
                if (shownTip === hideNow) {
                    shownTip = null;
                }
                document.removeEventListener('pointerdown', onOutside, true);
                document.removeEventListener('keydown', onKey, true);
                window.removeEventListener('scroll', place, true);
                window.removeEventListener('resize', place);
            }
        }

        function hideNow() {
            pinned = false;
            hovered = false;
            update();
        }

        button.addEventListener('mouseenter', function () { hovered = true; update(); });
        button.addEventListener('mouseleave', function () { hovered = false; update(); });
        button.addEventListener('focus', function () { hovered = true; update(); });
        button.addEventListener('blur', function () { hovered = false; pinned = false; update(); });
        button.addEventListener('click', function (e) {
            // Inside a <label> the click must not reach the labelled control.
            e.preventDefault();
            e.stopPropagation();
            pinned = !pinned;
            hovered = pinned;
            update();
        });
        return { element: element, button: button, tip: tip, tipId: tipId, hide: hideNow };
    }

    // ----- API helper -----

    /** Normalises ApiProblem.Errors ([{ Path, Message }]) and ASP.NET's { errors: { path: [messages] } } to [{ path, message }]. */
    // Resolve field-intent paths against either casing used by the API, including stable library ids.
    function fieldValue(doc, path) {
        let value = doc;
        const parts = path.split('.');
        if (parts[0] === 'libraries' && parts.length > 1) {
            const rows = doc && (doc.Libraries || doc.libraries) || [];
            value = rows.find(function (row) { return String(row.LibraryId || row.libraryId || '').replace(/-/g, '').toLowerCase() === parts[1]; });
            parts.splice(0, 2);
        }
        parts.forEach(function (part) {
            const key = value && Object.keys(value).find(function (key) { return key.toLowerCase() === part.toLowerCase(); });
            value = key ? value[key] : null;
        });
        if (value === undefined || value === null) { return path === 'seed.confirmed' ? false : path.slice(-14) === '.originalAudio' ? 'Auto' : null; }
        return value;
    }

    function setFieldValue(doc, path, value) {
        const parts = path.split('.');
        let holder = doc;
        if (parts[0] === 'libraries' && parts.length > 1) {
            const rows = doc.Libraries = doc.Libraries || [];
            holder = rows.find(function (row) { return String(row.LibraryId || '').replace(/-/g, '').toLowerCase() === parts[1]; });
            if (!holder) { holder = { LibraryId: parts[1], Settings: {} }; rows.push(holder); }
            parts.splice(0, 2);
        }
        parts.forEach(function (part, index) {
            const key = Object.keys(holder).find(function (key) { return key.toLowerCase() === part.toLowerCase(); }) || part.charAt(0).toUpperCase() + part.slice(1);
            if (index === parts.length - 1) { holder[key] = value === undefined ? null : JSON.parse(JSON.stringify(value)); }
            else { holder[key] = holder[key] || {}; holder = holder[key]; }
        });
    }

    function expectedValues(original, fields) {
        function camel(value) {
            if (Array.isArray(value)) { return value.map(camel); }
            if (value && typeof value === 'object') {
                const out = {};
                Object.keys(value).forEach(function (key) { out[key.charAt(0).toLowerCase() + key.slice(1)] = camel(value[key]); });
                return out;
            }
            return value;
        }
        const values = {};
        fields.forEach(function (path) { values[path] = camel(fieldValue(original, path)); });
        return values;
    }

    function withExpected(body, original, fields) {
        const submitted = Object.assign({}, body);
        submitted.ExpectedValues = expectedValues(original, fields);
        return submitted;
    }

    // Retained for older integrations; the bundled pages use body metadata to avoid header limits.
    function expectedHeaders(original, fields) {
        const values = expectedValues(original, fields);
        // Encode UTF-8 without depending on a newer browser's TextEncoder or btoa extensions.
        const bytes = encodeURIComponent(JSON.stringify(values)).replace(/%([0-9A-F]{2})/g, function (_, hex) { return String.fromCharCode(parseInt(hex, 16)); });
        const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
        let encoded = '', buffer = 0, bits = 0;
        for (let i = 0; i < bytes.length; i++) {
            buffer = (buffer << 8) | bytes.charCodeAt(i); bits += 8;
            while (bits >= 6) { bits -= 6; encoded += alphabet[(buffer >>> bits) & 63]; }
        }
        if (bits) { encoded += alphabet[(buffer << (6 - bits)) & 63]; }
        return { 'X-Babelfin-Expected': encoded };
    }

    function problemMessage(t, body, fallback) {
        const code = body && (body.Code || body.code);
        const key = 'api.' + code;
        const translated = code && t && t(key);
        return translated && translated !== key ? translated : body && (body.Message || body.message || body.Title || body.title) || fallback || '';
    }

    function conflictPanel(options) {
        const o = options, p = o.problem, t = o.t;
        const paths = p.ConflictingPaths || p.conflictingPaths || [];
        const actor = p.LastChangedBy || p.lastChangedBy;
        const name = p.LastChangedByName || p.lastChangedByName || o.actor || (actor === 'admin' ? t('conflict.admin') : t('conflict.someone'));
        const panel = h('div', { class: 'bfui bf-conflict', role: 'alert', tabindex: '-1' },
            h('p', { text: t('conflict.message', { name: name, fields: paths.map(function (path) { return o.label ? o.label(path) : t('conflict.settings'); }).filter(function (value, i, list) { return list.indexOf(value) === i; }).join(', ') }) }));
        const reload = h('button', { type: 'button', class: 'bfui-btn', 'data-conflict': 'reload', text: t('conflict.reload') });
        const keep = h('button', { type: 'button', class: 'bfui-btn', 'data-conflict': 'keep', text: t('conflict.keep') });
        [reload, keep].forEach(function (button, i) {
            button.addEventListener('click', function () {
                reload.disabled = keep.disabled = true;
                Promise.resolve().then(i ? o.keep : o.reload).catch(function (err) {
                    reload.disabled = keep.disabled = false;
                    if (o.error) { o.error(err); }
                });
            });
        });
        panel.appendChild(h('div', { class: 'bfui-actions' }, reload, keep));
        return panel;
    }

    function problemErrors(body) {
        if (!body || typeof body !== 'object') {
            return [];
        }
        const raw = body.Errors || body.errors;
        if (Array.isArray(raw)) {
            return raw.map(function (e) { return { path: String(e.Path || e.path || ''), message: String(e.Message || e.message || '') }; });
        }
        if (raw && typeof raw === 'object') {
            const list = [];
            Object.keys(raw).forEach(function (path) {
                [].concat(raw[path]).forEach(function (message) { list.push({ path: path.replace(/^\$\.?/, ''), message: String(message) }); });
            });
            return list;
        }
        return [];
    }

    /** The error the API helper throws: status (0 = network), body (parsed JSON or text), errors ([{ path, message }]), itemIds. */
    function apiError(status, body, message) {
        const error = new Error(message || ('HTTP ' + status));
        error.name = 'BabelfinApiError';
        error.status = status;
        error.body = body;
        error.errors = problemErrors(body);
        error.itemIds = (body && typeof body === 'object' && (body.ItemIds || body.itemIds)) || null;
        return error;
    }

    /**
     * api(baseUrl, authHeaderFn): { url(path, query), request(method, path, body, query, init), getJSON(path, query, init),
     * putJSON(path, body, query), postJSON(path, body, query), deleteJSON(path, query) }.
     * Paths are relative to the server's base URL ("/Babelfin/Me/Settings", "/Items"). authHeaderFn returns the Authorization value
     * (a string), a headers object, or null. Answers: parsed JSON, text, or null for an empty body; failures throw an error with
     * status, body, errors and itemIds (see apiError); an aborted request rethrows the AbortError.
     */
    function api(baseUrl, authHeaderFn) {
        const root = String(baseUrl || '').replace(/\/+$/, '');

        function url(path, query) {
            let full = root + (String(path).charAt(0) === '/' ? '' : '/') + path;
            if (query) {
                const parts = [];
                Object.keys(query).forEach(function (key) {
                    const value = query[key];
                    if (value === null || value === undefined || value === '') {
                        return;
                    }
                    parts.push(encodeURIComponent(key) + '=' + encodeURIComponent(Array.isArray(value) ? value.join(',') : String(value)));
                });
                if (parts.length) {
                    full += (full.indexOf('?') >= 0 ? '&' : '?') + parts.join('&');
                }
            }
            return full;
        }

        async function request(method, path, body, query, init) {
            const headers = { Accept: 'application/json' };
            const auth = typeof authHeaderFn === 'function' ? authHeaderFn() : null;
            if (typeof auth === 'string' && auth) {
                headers.Authorization = auth;
            } else if (auth && typeof auth === 'object') {
                Object.assign(headers, auth);
            }
            const options = Object.assign({ method: method, credentials: 'same-origin' }, init || {});
            Object.assign(headers, init && init.headers);
            options.headers = headers;
            if (body !== undefined) {
                headers['Content-Type'] = 'application/json';
                options.body = JSON.stringify(body);
            }
            let response;
            try {
                response = await fetch(url(path, query), options);
            } catch (e) {
                if (e && e.name === 'AbortError') {
                    throw e;
                }
                throw apiError(0, null, e && e.message);
            }
            const text = await response.text();
            let data = null;
            if (text) {
                try {
                    data = JSON.parse(text);
                } catch (e) {
                    data = text;
                }
            }
            if (!response.ok) {
                const message = data && typeof data === 'object' ? (data.Message || data.message || data.title) : null;
                throw apiError(response.status, data, message || ('HTTP ' + response.status));
            }
            return data;
        }

        return {
            url: url,
            request: request,
            getJSON: function (path, query, init) { return request('GET', path, undefined, query, init); },
            putJSON: function (path, body, query, init) { return request('PUT', path, body === undefined ? null : body, query, init); },
            postJSON: function (path, body, query) { return request('POST', path, body === undefined ? null : body, query); },
            deleteJSON: function (path, query) { return request('DELETE', path, undefined, query); }
        };
    }

    // ----- Language names -----

    const displayNamesByLocale = new Map();
    function intlName(code, locale) {
        if (!code || typeof Intl === 'undefined' || typeof Intl.DisplayNames !== 'function') {
            return null;
        }
        let names = displayNamesByLocale.get(locale);
        if (names === undefined) {
            try {
                names = new Intl.DisplayNames([locale], { type: 'language', fallback: 'none' });
            } catch (e) {
                names = null;
            }
            displayNamesByLocale.set(locale, names);
        }
        if (!names) {
            return null;
        }
        try {
            return names.of(code) || null;
        } catch (e) {
            return null;
        }
    }

    const nativeNames = new Map();
    function nativeName(code) {
        if (!code) {
            return null;
        }
        if (nativeNames.has(code)) {
            return nativeNames.get(code);
        }
        let name = null;
        try {
            if (typeof Intl.DisplayNames === 'function' && Intl.DisplayNames.supportedLocalesOf([code]).length) {
                name = intlName(code, code);
            }
        } catch (e) {
            name = null;
        }
        nativeNames.set(code, name);
        return name;
    }

    function normalize(text) {
        return String(text || '').toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '');
    }

    const indexCache = new WeakMap();
    const noLanguages = [];

    /** Builds (once per list and UI language) the searchable language index from GET Languages. */
    function languageIndex(languages, locale) {
        const list = Array.isArray(languages) ? languages : noLanguages;
        let perLocale = indexCache.get(list);
        if (!perLocale) {
            perLocale = new Map();
            indexCache.set(list, perLocale);
        }
        if (perLocale.has(locale)) {
            return perLocale.get(locale);
        }
        const byCode = new Map();
        const entries = [];
        list.forEach(function (dto) {
            const code = dto && (dto.Code || dto.code);
            if (!code) {
                return;
            }
            const english = dto.Name || dto.name || code;
            const name = intlName(code, locale) || english;
            const native = nativeName(code);
            const entry = {
                code: code,
                base: dto.Base || dto.base || code.split('-')[0],
                variety: dto.Variety || dto.variety || null,
                script: dto.Script || dto.script || null,
                region: dto.Region || dto.region || null,
                english: english,
                name: name,
                native: native && normalize(native) !== normalize(name) ? native : null
            };
            entry.parts = [code, english, name, native].filter(Boolean).map(normalize);
            byCode.set(code.toLowerCase(), entry);
            entries.push(entry);
        });
        const index = { entries: entries, byCode: byCode };
        perLocale.set(locale, index);
        return index;
    }

    /**
     * The display name of a language code: { code, name (in the viewer's language), native, english, label ("name — native") }.
     * options: { languages (GET Languages), uiLanguage or t }.
     */
    function languageName(code, options) {
        const o = options || {};
        if (!code) {
            return { code: '', name: '', native: null, english: '', label: '' };
        }
        const locale = uiLanguage(o);
        const entry = languageIndex(o.languages, locale).byCode.get(String(code).toLowerCase());
        const name = entry ? entry.name : (intlName(code, locale) || String(code));
        const native = entry ? entry.native : null;
        return { code: code, name: name, native: native, english: entry ? entry.english : name, label: native ? name + ' — ' + native : name };
    }

    // ----- Language picker -----

    const pickerKeyHandlers = new WeakMap();

    // A page's capture handler can give the focused picker first refusal without changing native radio navigation.
    function handleLanguagePickerKey(e) {
        const handle = pickerKeyHandlers.get(e.target);
        if (!handle) { return false; }
        // Leave native text-caret movement to the input, rather than moving focus to adjacent page buttons.
        return handle(e) || e.key === 'ArrowLeft' || e.key === 'ArrowRight';
    }

    /**
     * languagePicker(options): a search box over GET Languages; results grouped by base language (the base first, then its
     * varieties and scripts), each with its name in the viewer's language, the native name, the English name and the code.
     * options: { t, languages, uiLanguage, exclude: [codes], onPick(code, entry), label, limit }.
     * Returns { element, input, focus(), setExclude(codes) }.
     */
    function languagePicker(options) {
        const o = Object.assign({ languages: [], exclude: [], limit: 60, onPick: null, label: null }, options);
        const t = o.t;
        const locale = uiLanguage(o);
        const index = languageIndex(o.languages, locale);
        const listId = uid('bfui-langs');
        let exclude = new Set((o.exclude || []).map(function (c) { return String(c).toLowerCase(); }));
        let shown = [];
        let active = -1;

        const input = h('input', {
            type: 'search', class: 'bfui-input', role: 'combobox', autocomplete: 'off', spellcheck: 'false',
            'aria-autocomplete': 'list', 'aria-expanded': 'false', 'aria-controls': listId,
            placeholder: tr(t, 'ui.languages.search'), 'aria-label': o.label || tr(t, 'ui.languages.search')
        });
        const list = h('ul', { class: 'bfui-picker-list', role: 'listbox', id: listId, hidden: true, 'aria-label': o.label || tr(t, 'ui.languages.search') });
        const none = h('div', { class: 'bfui-help', hidden: true, role: 'status', text: tr(t, 'ui.languages.none') });
        const element = h('div', { class: 'bfui bfui-picker' }, input, list, none);

        function search(query) {
            const q = normalize(query).trim();
            if (!q) {
                return [];
            }
            const scored = [];
            index.entries.forEach(function (entry) {
                if (exclude.has(entry.code.toLowerCase())) {
                    return;
                }
                let score;
                if (entry.code.toLowerCase() === q) {
                    score = 0;
                } else if (entry.parts.some(function (part) { return part.indexOf(q) === 0; })) {
                    score = 1;
                } else if (entry.parts.some(function (part) { return part.indexOf(q) >= 0; })) {
                    score = 2;
                } else {
                    return;
                }
                scored.push({ entry: entry, score: score });
            });
            scored.sort(function (a, b) { return a.score - b.score || a.entry.name.localeCompare(b.entry.name, locale); });
            const groups = new Map();
            scored.slice(0, o.limit).forEach(function (s) {
                const base = s.entry.base.toLowerCase();
                if (!groups.has(base)) {
                    groups.set(base, []);
                }
                groups.get(base).push(s.entry);
            });
            groups.forEach(function (entries) {
                entries.sort(function (a, b) {
                    return (a.code.toLowerCase() === a.base.toLowerCase() ? 0 : 1) - (b.code.toLowerCase() === b.base.toLowerCase() ? 0 : 1);
                });
            });
            return Array.from(groups.entries());
        }

        function setActive(i) {
            if (!shown.length) {
                return;
            }
            if (active >= 0 && shown[active]) {
                shown[active].node.setAttribute('aria-selected', 'false');
                shown[active].node.classList.remove('bfui-active');
            }
            active = (i + shown.length) % shown.length;
            const node = shown[active].node;
            node.setAttribute('aria-selected', 'true');
            node.classList.add('bfui-active');
            input.setAttribute('aria-activedescendant', node.id);
            if (typeof node.scrollIntoView === 'function') {
                node.scrollIntoView({ block: 'nearest' });
            }
        }

        function render() {
            clear(list);
            shown = [];
            active = -1;
            input.removeAttribute('aria-activedescendant');
            search(input.value).forEach(function (group) {
                const base = group[0];
                const baseEntry = index.byCode.get(base);
                if (group[1].length > 1 || group[1][0].code.toLowerCase() !== base) {
                    list.appendChild(h('li', { class: 'bfui-picker-group', role: 'presentation', text: baseEntry ? baseEntry.name : (intlName(base, locale) || base) }));
                }
                group[1].forEach(function (entry) {
                    const secondary = [entry.native, entry.english !== entry.name ? entry.english : null, entry.code].filter(Boolean).join(' · ');
                    const option = h('li', { id: uid('bfui-opt'), role: 'option', class: 'bfui-picker-option', 'aria-selected': 'false', dataset: { code: entry.code } },
                        h('span', { class: 'bfui-picker-name', text: entry.name }),
                        h('span', { class: 'bfui-picker-secondary', text: secondary }));
                    option.addEventListener('mousedown', function (e) { e.preventDefault(); });
                    option.addEventListener('click', function () { pick(entry); });
                    shown.push({ entry: entry, node: option });
                    list.appendChild(option);
                });
            });
            const open = shown.length > 0;
            list.hidden = !open;
            input.setAttribute('aria-expanded', open ? 'true' : 'false');
            none.hidden = open || !input.value.trim();
            if (open) {
                setActive(0);
            }
        }

        function pick(entry) {
            input.value = '';
            render();
            if (o.onPick) {
                o.onPick(entry.code, entry);
            }
        }

        input.addEventListener('input', render);
        function onPickerKey(e) {
            if (e.defaultPrevented) { return false; }
            if (e.key === 'ArrowDown' && shown.length) {
                e.preventDefault();
                setActive(active + 1);
            } else if (e.key === 'ArrowUp' && shown.length) {
                e.preventDefault();
                setActive(active - 1);
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (shown[active]) {
                    pick(shown[active].entry);
                }
            } else if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                if (input.value) {
                    input.value = '';
                    render();
                } else if (o.onEscape) {
                    o.onEscape();
                }
            } else { return false; }
            return true;
        }
        pickerKeyHandlers.set(input, onPickerKey);
        input.addEventListener('keydown', onPickerKey);

        return {
            element: element,
            input: input,
            focus: function () { input.focus(); },
            setExclude: function (codes) {
                exclude = new Set((codes || []).map(function (c) { return String(c).toLowerCase(); }));
                render();
            }
        };
    }

    /**
     * languageChooser(options): a button showing one language; clicking it opens a picker below.
     * options: { t, languages, uiLanguage, value, label, disabled, onChange(code) }. Returns { element, getValue(), setValue(code), open() }.
     */
    function languageChooser(options) {
        const o = Object.assign({ value: null, label: null, disabled: false }, options);
        const t = o.t;
        let value = o.value || null;
        let picker = null;
        const button = h('button', { type: 'button', class: 'bfui-btn bfui-chooser', 'aria-expanded': 'false', disabled: !!o.disabled });
        const holder = h('div', { class: 'bfui-chooser-picker', hidden: true });
        const element = h('span', { class: 'bfui bfui-chooser-wrap' }, button, holder);

        function paint() {
            const name = value ? languageName(value, o).label : tr(t, 'ui.languages.choose');
            button.textContent = name;
            button.classList.toggle('bfui-missing', !value);
            button.setAttribute('aria-label', (o.label ? o.label + ': ' : '') + name);
        }

        function close() {
            holder.hidden = true;
            button.setAttribute('aria-expanded', 'false');
        }

        function open() {
            if (!picker) {
                picker = languagePicker(Object.assign({}, o, {
                    onEscape: function () { close(); button.focus(); },
                    onPick: function (code) {
                        value = code;
                        paint();
                        close();
                        button.focus();
                        if (o.onChange) {
                            o.onChange(code);
                        }
                    }
                }));
                holder.appendChild(picker.element);
            }
            holder.hidden = false;
            button.setAttribute('aria-expanded', 'true');
            picker.focus();
        }

        button.addEventListener('click', function () {
            if (holder.hidden) {
                open();
            } else {
                close();
            }
        });
        paint();
        return {
            element: element,
            getValue: function () { return value; },
            setValue: function (code) { value = code || null; paint(); },
            open: open
        };
    }

    // ----- Ordered list -----

    /**
     * orderedList(container, items, options): a list with move up / move down / remove buttons; the array is edited in place.
     * options: { t, render(item, index) → Node, onChange(items), label, emptyText, addControl (Node), removable, reorderable }.
     * Returns { element, getItems(), setItems(items), add(item), refresh(focusIndex, selector) }.
     */
    function orderedList(container, items, options) {
        const o = Object.assign({ render: function (item) { return String(item); }, onChange: null, label: null, emptyText: null, addControl: null, removable: true, reorderable: true }, options);
        const t = o.t;
        let list = Array.isArray(items) ? items : [];
        const ol = h('ol', { class: 'bfui-list', 'aria-label': o.label });
        const empty = h('p', { class: 'bfui-empty', text: o.emptyText || tr(t, 'ui.empty') });
        const element = h('div', { class: 'bfui bfui-ordered' }, ol, empty, o.addControl ? h('div', { class: 'bfui-list-add' }, o.addControl) : null);

        function render(focusIndex, selector) {
            clear(ol);
            list.forEach(function (item, i) {
                const actions = h('div', { class: 'bfui-list-actions' });
                if (o.reorderable) {
                    actions.appendChild(h('button', { type: 'button', class: 'bfui-icon-btn bfui-up', 'aria-label': tr(t, 'ui.moveUp'), title: tr(t, 'ui.moveUp'), disabled: i === 0, onclick: function () { move(i, -1); } }, '↑'));
                    actions.appendChild(h('button', { type: 'button', class: 'bfui-icon-btn bfui-down', 'aria-label': tr(t, 'ui.moveDown'), title: tr(t, 'ui.moveDown'), disabled: i === list.length - 1, onclick: function () { move(i, 1); } }, '↓'));
                }
                if (o.removable) {
                    actions.appendChild(h('button', { type: 'button', class: 'bfui-icon-btn bfui-remove', 'aria-label': tr(t, 'ui.remove'), title: tr(t, 'ui.remove'), onclick: function () { remove(i); } }, '×'));
                }
                ol.appendChild(h('li', { class: 'bfui-list-item' }, h('div', { class: 'bfui-list-content' }, o.render(item, i)), actions));
            });
            empty.hidden = list.length > 0;
            if (typeof focusIndex === 'number' && list.length) {
                const li = ol.children[Math.max(0, Math.min(focusIndex, list.length - 1))];
                let target = li && li.querySelector(selector || '.bfui-remove');
                if (target && target.disabled) {
                    target = li.querySelector('button:not([disabled])');
                }
                if (target) {
                    target.focus();
                }
            }
        }

        function changed() {
            if (o.onChange) {
                o.onChange(list);
            }
        }

        function move(i, delta) {
            const j = i + delta;
            if (j < 0 || j >= list.length) {
                return;
            }
            const item = list[i];
            list[i] = list[j];
            list[j] = item;
            changed();
            render(j, delta < 0 ? '.bfui-up' : '.bfui-down');
        }

        function remove(i) {
            list.splice(i, 1);
            changed();
            render(list.length ? Math.min(i, list.length - 1) : undefined);
        }

        render();
        if (container) {
            container.appendChild(element);
        }
        return {
            element: element,
            getItems: function () { return list; },
            setItems: function (next) { list = Array.isArray(next) ? next : []; render(); },
            add: function (item) { list.push(item); changed(); render(); },
            refresh: render
        };
    }

    /**
     * languageList(container, codes, options): an ordered list of languages with a picker to add more; the array is edited in place.
     * options: { t, languages, uiLanguage, onChange(codes), label, emptyText, assumed: [codes badged "assumed"], disabled }.
     * Returns the orderedList API.
     */
    function languageList(container, codes, options) {
        const o = Object.assign({ assumed: [], disabled: false }, options);
        const t = o.t;
        const assumed = new Set((o.assumed || []).map(function (c) { return String(c).toLowerCase(); }));
        let list = null;
        const picker = o.disabled ? null : languagePicker(Object.assign({}, o, {
            label: tr(t, 'ui.languages.add'),
            exclude: codes,
            onPick: function (code) {
                list.add(code);
                picker.setExclude(list.getItems());
                picker.focus();
            }
        }));
        list = orderedList(container, codes, {
            t: t,
            label: o.label,
            emptyText: o.emptyText || tr(t, 'ui.languages.emptyList'),
            removable: !o.disabled,
            reorderable: !o.disabled,
            addControl: picker ? picker.element : null,
            render: function (code) {
                return h('span', { class: 'bfui-lang' },
                    h('span', { text: languageName(code, o).label }),
                    assumed.has(String(code).toLowerCase()) ? h('span', { class: 'bfui-badge bfui-badge-warn', text: tr(t, 'ui.assumed') }) : null);
            },
            onChange: function (items) {
                if (picker) {
                    picker.setExclude(items);
                }
                if (o.onChange) {
                    o.onChange(items);
                }
            }
        });
        return list;
    }

    // ----- A labelled dropdown -----

    /**
     * fieldSelect(options): a labelled dropdown of plain options (no "Default (…)" entry; the caller selects the value that
     * applies). options: { label, ariaLabel, help, choices: [[value, text]], value (string; null selects nothing), disabled, lockedText (shown
     * under a control the person can't change), info (an explanation behind an infoTip after the label), onChange(value) }.
     * Returns { element, select, extra (a holder for a control that belongs to the choice), getValue(), setValue(value), showError(message),
     * clearError() }.
     */
    function fieldSelect(options) {
        const o = Object.assign({ choices: [], value: null, disabled: false, lockedText: '', info: '', onChange: null }, options);
        const id = uid('bfui-fs');
        const noteText = o.lockedText || o.help || '';
        const noteId = noteText ? uid('bfui-fsn') : null;
        const errorId = uid('bfui-fse');
        const tip = o.info ? infoTip({ t: o.t, label: o.label, text: o.info }) : null;
        const select = h('select', {
            id: id, class: 'bfui-select', disabled: !!o.disabled, 'aria-label': o.ariaLabel || null,
            'aria-describedby': [noteId, tip && tip.tipId, errorId].filter(Boolean).join(' ')
        });
        o.choices.forEach(function (choice) {
            select.appendChild(h('option', { value: String(choice[0]), text: choice[1] }));
        });
        const extra = h('div', { class: 'bfui-row-extra' });
        const error = h('div', { class: 'bfui-field-error', id: errorId, role: 'alert', hidden: true });
        const element = h('div', { class: 'bfui bfui-row' + (o.lockedText ? ' bfui-is-locked' : '') },
            h('div', { class: 'bfui-row-head' },
                h('label', { class: 'bfui-row-label', for: id }, o.lockedText ? lockIcon() : null, h('span', { text: o.label || '' })),
                tip ? tip.element : null),
            select,
            noteText ? h('div', { class: 'bfui-help', id: noteId, text: noteText }) : null,
            extra,
            error);

        function setValue(value) {
            select.value = value === null || value === undefined ? '' : String(value);
        }

        function clearError() {
            error.hidden = true;
            error.textContent = '';
            element.classList.remove('bfui-has-error');
        }

        setValue(o.value);
        select.addEventListener('change', function () {
            clearError();
            if (o.onChange) {
                o.onChange(select.value === '' ? null : select.value);
            }
        });
        return {
            element: element,
            select: select,
            extra: extra,
            getValue: function () { return select.value === '' ? null : select.value; },
            setValue: setValue,
            showError: function (message) {
                error.hidden = false;
                error.textContent = error.textContent ? error.textContent + ' · ' + message : String(message);
                element.classList.add('bfui-has-error');
            },
            clearError: clearError
        };
    }

    /** A small padlock drawn with SVG (no emoji, no external image). */
    function lockIcon() {
        const ns = 'http://www.w3.org/2000/svg';
        const svg = document.createElementNS(ns, 'svg');
        svg.setAttribute('viewBox', '0 0 16 16');
        svg.setAttribute('width', '1em');
        svg.setAttribute('height', '1em');
        svg.setAttribute('aria-hidden', 'true');
        svg.setAttribute('focusable', 'false');
        svg.setAttribute('class', 'bfui-lock');
        const body = document.createElementNS(ns, 'rect');
        body.setAttribute('x', '3');
        body.setAttribute('y', '7');
        body.setAttribute('width', '10');
        body.setAttribute('height', '8');
        body.setAttribute('rx', '1.5');
        body.setAttribute('fill', 'currentColor');
        const shackle = document.createElementNS(ns, 'path');
        shackle.setAttribute('d', 'M5 7V5a3 3 0 0 1 6 0v2');
        shackle.setAttribute('fill', 'none');
        shackle.setAttribute('stroke', 'currentColor');
        shackle.setAttribute('stroke-width', '1.6');
        svg.appendChild(body);
        svg.appendChild(shackle);
        return svg;
    }

    // ----- The "Audio and subtitles" table -----

    const TABLE_AUDIO_FIELDS = ['AudioChain', 'ForeignTitles'];
    const TABLE_SUBTITLE_FIELDS = ['WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'SubtitleLanguages'];
    const TABLE_FIELDS = TABLE_AUDIO_FIELDS.concat(TABLE_SUBTITLE_FIELDS);
    // Regional, script and spoken forms offered next to the plain languages (GET Languages lists them under another base or code).
    const TABLE_EXTRA_CODES = ['pt-BR', 'pt-PT', 'zh-Hans', 'zh-Hant', 'es-419', 'fr-CA', 'yue', 'fil'];
    // First audio steps that leave the track to the video file: shown as "Video default".
    const VIDEO_AUDIO_KINDS = ['VideoDefault', 'FileDefault', 'FirstTrack'];
    // The subtitle choices without a language: [WhenAudioUnderstood, WhenAudioNotUnderstood].
    const SUBTITLE_PAIRS = Object.freeze({
        video: ['VideoDefault', 'VideoDefault'],
        off: ['Off', 'Off'],
        smart: ['PartialOnly', 'Full'],
        signs: ['PartialOnly', 'PartialOnly']
    });

    function compactId(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function sameCode(a, b) {
        return String(a || '').toLowerCase() === String(b || '').toLowerCase();
    }

    function layerHasValues(layer) {
        return !!layer && Object.keys(layer).some(function (key) { return layer[key] !== null && layer[key] !== undefined; });
    }

    function oneCode(list) {
        return Array.isArray(list) && list.length === 1 && list[0] ? String(list[0]) : null;
    }

    /**
     * The table's audio choice for a layer: '' (Default), 'video', 'mine', 'original', 'lang:<code>' or 'custom'. A chain whose first step is a language
     * (without track-name text, and with no other language after it) shows as that language. "My languages first" (an order starting
     * with MyLanguages, or a dub in my language) keeps its legacy single-language display unless it is the exact new two-step choice.
     */
    function tableAudioValue(layer, understood) {
        const mine = oneCode(understood);
        if (isFieldSet(layer, 'AudioChain')) {
            const chain = Array.isArray(layer.AudioChain) ? layer.AudioChain : [];
            const first = chain[0];
            if (!first) {
                return 'custom';
            }
            if (chain.length === 2 && chain[1] && chain[1].Kind === 'VideoDefault'
                && !first.Language && !first.TitleContains && !chain[1].Language && !chain[1].TitleContains) {
                if (first.Kind === 'MyLanguages') { return 'mine'; }
                if (first.Kind === 'OriginalLanguage') { return 'original'; }
            }
            if (first.Kind === 'VideoDefault') {
                return 'video';
            }
            if (first.Kind === 'Language' && first.Language && !first.TitleContains
                && !chain.slice(1).some(function (s) { return s && s.Kind === 'Language'; })) {
                return 'lang:' + first.Language;
            }
            return first.Kind === 'MyLanguages' && mine ? 'lang:' + mine : 'custom';
        }
        if (!isFieldSet(layer, 'ForeignTitles')) {
            return '';
        }
        return layer.ForeignTitles === 'DubInMyLanguage' && mine ? 'lang:' + mine : 'custom';
    }

    /** Writes a table audio choice into a layer ('custom' is never written): Default unsets; explicit choices set the order. */
    function writeTableAudio(layer, value) {
        delete layer.ForeignTitles;
        if (!value) {
            delete layer.AudioChain;
        } else if (value === 'video') {
            layer.AudioChain = [{ Kind: 'VideoDefault' }];
        } else if (value === 'mine' || value === 'original') {
            layer.AudioChain = [{ Kind: value === 'mine' ? 'MyLanguages' : 'OriginalLanguage' }, { Kind: 'VideoDefault' }];
        } else if (String(value).indexOf('lang:') === 0) {
            layer.AudioChain = [{ Kind: 'Language', Language: String(value).slice(5) }, { Kind: 'VideoDefault' }];
        }
    }

    /**
     * The table's subtitle choice for a layer: '' (Default), 'video', 'off', 'smart', 'signs', 'lang:<code>' or 'custom'. Full subtitles
     * with no language of their own (Jellyfin's "Always" mode, carried over) show as the person's single subtitle language, when there is one.
     */
    function tableSubtitleValue(layer, reads) {
        const u = isFieldSet(layer, 'WhenAudioUnderstood') ? layer.WhenAudioUnderstood : null;
        const n = isFieldSet(layer, 'WhenAudioNotUnderstood') ? layer.WhenAudioNotUnderstood : null;
        const languages = isFieldSet(layer, 'SubtitleLanguages') ? layer.SubtitleLanguages : null;
        if (u === null && n === null && languages === null) {
            return '';
        }
        if (languages === null) {
            const pair = Object.keys(SUBTITLE_PAIRS).find(function (k) { return SUBTITLE_PAIRS[k][0] === u && SUBTITLE_PAIRS[k][1] === n; });
            if (pair) {
                return pair;
            }
            const read = oneCode(reads);
            return u === 'Full' && n === 'Full' && read ? 'lang:' + read : 'custom';
        }
        const code = oneCode(languages);
        return u === 'Full' && n === 'Full' && code ? 'lang:' + code : 'custom';
    }

    /** Writes a table subtitle choice into a layer: all three fields every time ('custom' is never written). */
    function writeTableSubtitles(layer, value) {
        delete layer.SubtitleLanguages;
        if (!value) {
            delete layer.WhenAudioUnderstood;
            delete layer.WhenAudioNotUnderstood;
        } else if (SUBTITLE_PAIRS[value]) {
            layer.WhenAudioUnderstood = SUBTITLE_PAIRS[value][0];
            layer.WhenAudioNotUnderstood = SUBTITLE_PAIRS[value][1];
        } else if (String(value).indexOf('lang:') === 0) {
            layer.WhenAudioUnderstood = 'Full';
            layer.WhenAudioNotUnderstood = 'Full';
            layer.SubtitleLanguages = [String(value).slice(5)];
        }
    }

    /** Whether a layer sets any of the table's five fields. */
    function hasTableFields(layer) {
        return TABLE_FIELDS.some(function (f) { return isFieldSet(layer, f); });
    }

    /**
     * The table's mode when nothing is stored (matching the server's rule): true (a choice per library) when any library entry
     * sets one of the five table fields, else false (the same for every library). settings: { Libraries: [{ Settings }] }.
     */
    function automaticPerLibrary(settings) {
        const list = settings && Array.isArray(settings.Libraries) ? settings.Libraries : [];
        return list.some(function (entry) { return !!entry && hasTableFields(entry.Settings); });
    }

    function compareText(locale) {
        try {
            return new Intl.Collator(locale).compare;
        } catch (e) {
            return function (a, b) { return a < b ? -1 : (a > b ? 1 : 0); };
        }
    }

    /** The language codes a list of layers stores (audio order languages, subtitle languages, understood and read languages). */
    function storedCodes(layers) {
        const out = [];
        (layers || []).forEach(function (layer) {
            if (!layer) {
                return;
            }
            (Array.isArray(layer.AudioChain) ? layer.AudioChain : []).forEach(function (step) {
                if (step && step.Kind === 'Language' && step.Language) {
                    out.push(step.Language);
                }
            });
            ['SubtitleLanguages', 'Understood', 'Reads'].forEach(function (name) {
                (Array.isArray(layer[name]) ? layer[name] : []).forEach(function (code) {
                    if (code) {
                        out.push(code);
                    }
                });
            });
        });
        return out;
    }

    /**
     * The "All languages" list: every plain language of GET Languages with a two-letter code, the regional and script forms people pick
     * (pt-BR, pt-PT, zh-Hans, zh-Hant, es-419, fr-CA, yue, fil) and the given stored codes, sorted by name in the viewer's language.
     * options: { languages, uiLanguage or t }. Returns [{ code, name }].
     */
    function tableLanguages(options, extraCodes) {
        const o = options || {};
        const byKey = new Map();
        function add(code) {
            const text = String(code || '').trim();
            if (text && !byKey.has(text.toLowerCase())) {
                byKey.set(text.toLowerCase(), text);
            }
        }
        (Array.isArray(o.languages) ? o.languages : []).forEach(function (dto) {
            const code = dto && (dto.Code || dto.code);
            const base = dto && (dto.Base || dto.base);
            if (code && base && sameCode(code, base) && /^[A-Za-z]{2}$/.test(String(base))) {
                add(code);
            }
        });
        TABLE_EXTRA_CODES.forEach(add);
        (extraCodes || []).forEach(add);
        const compare = compareText(uiLanguage(o));
        const list = Array.from(byKey.values()).map(function (code) { return { code: code, name: languageName(code, o).name }; });
        // Two codes the browser names alike (it calls both "tl" and "fil" Filipino) get the server's own names, so no two options read the same.
        const serverName = new Map();
        (Array.isArray(o.languages) ? o.languages : []).forEach(function (dto) {
            const code = dto && (dto.Code || dto.code);
            const name = dto && (dto.Name || dto.name);
            if (code && name) { serverName.set(String(code).toLowerCase(), String(name)); }
        });
        function clashes() {
            const counts = new Map();
            list.forEach(function (e) { const k = e.name.toLowerCase(); counts.set(k, (counts.get(k) || 0) + 1); });
            return counts;
        }
        let counts = clashes();
        list.forEach(function (e) {
            const own = serverName.get(e.code.toLowerCase());
            if (counts.get(e.name.toLowerCase()) > 1 && own && own.toLowerCase() !== e.name.toLowerCase()) {
                e.name = own;
            }
        });
        counts = clashes();
        list.forEach(function (e) {
            if (counts.get(e.name.toLowerCase()) > 1) {
                e.name = e.name + ' (' + e.code + ')';
            }
        });
        return list.sort(function (a, b) { return compare(a.name, b.name) || compare(a.code, b.code); });
    }

    /**
     * Fills a <select> with one column's choices: Default, Video default, (subtitles: Off, When the audio isn't in my languages, Signs and
     * songs only), the "My languages" group, the "All languages" group and Custom. cfg: { t, kind ('audio' | 'subtitles'), audience
     * ('person' | 'admin'), mine ([codes] for "My languages"; none for the admin), catalogue (tableLanguages), lang (languageName options) }.
     * Returns { defaultOption, videoOption, customOption, allGroup }; the Video default and Custom options are left for the caller to place.
     */
    function fillTableSelect(select, cfg) {
        const t = cfg.t;
        const admin = cfg.audience === 'admin';
        const defaultOption = h('option', { value: '', text: tr(t, 'ui.default') });
        const videoOption = h('option', { value: 'video', text: tr(t, 'ui.table.videoDefault') });
        const customOption = h('option', { value: 'custom', text: tr(t, 'ui.table.custom') });
        select.appendChild(defaultOption);
        select.appendChild(videoOption);
        if (cfg.kind === 'audio') {
            select.appendChild(h('option', { value: 'mine', text: tr(t, 'ui.table.audioMyLanguages') }));
            select.appendChild(h('option', { value: 'original', text: tr(t, 'ui.table.audioOriginal') }));
        }
        if (cfg.kind === 'subtitles') {
            select.appendChild(h('option', { value: 'off', text: tr(t, 'ui.table.off') }));
            select.appendChild(h('option', { value: 'smart', text: tr(t, admin ? 'ui.table.admin.smart' : 'ui.table.smart') }));
            select.appendChild(h('option', { value: 'signs', text: tr(t, 'ui.table.signs') }));
        }
        const allGroup = fillTableLanguageGroups(select, cfg);
        return { defaultOption: defaultOption, videoOption: videoOption, customOption: customOption, allGroup: allGroup };
    }

    function fillTableLanguageGroups(select, cfg) {
        const t = cfg.t;
        const admin = cfg.audience === 'admin';
        const used = new Set();
        const mine = admin ? [] : (cfg.mine || []).filter(function (code) {
            const key = String(code || '').toLowerCase();
            if (!key || used.has(key)) {
                return false;
            }
            used.add(key);
            return true;
        });
        if (mine.length) {
            const group = h('optgroup', { label: tr(t, 'ui.table.myLanguages') });
            mine.forEach(function (code) {
                const listed = (cfg.catalogue || []).find(function (entry) { return entry.code.toLowerCase() === String(code).toLowerCase(); });
                group.appendChild(h('option', { value: 'lang:' + code, text: listed ? listed.name : languageName(code, cfg.lang).name }));
            });
            select.appendChild(group);
        }
        const allGroup = h('optgroup', { label: tr(t, 'ui.table.allLanguages') });
        (cfg.catalogue || []).forEach(function (entry) {
            if (!used.has(entry.code.toLowerCase())) {
                allGroup.appendChild(h('option', { value: 'lang:' + entry.code, text: entry.name }));
            }
        });
        select.appendChild(allGroup);
        return allGroup;
    }

    // A language-list edit updates option groups in place, keeping the select, tips and table rows.
    function refreshTableLanguages(select, parts, cfg) {
        Array.prototype.slice.call(select.querySelectorAll('optgroup')).forEach(function (group) { select.removeChild(group); });
        if (parts.customOption.parentNode === select) { select.removeChild(parts.customOption); }
        parts.allGroup = fillTableLanguageGroups(select, cfg);
    }

    /** Selects the option for a value (language codes compare without case); a language with no option yet is added to "All languages". */
    function selectTableValue(select, parts, value, lang) {
        const wanted = value === null || value === undefined ? '' : String(value);
        let match = Array.prototype.find.call(select.options, function (option) { return sameCode(option.value, wanted); });
        if (!match && wanted.indexOf('lang:') === 0) {
            match = h('option', { value: wanted, text: languageName(wanted.slice(5), lang).name });
            parts.allGroup.appendChild(match);
        }
        select.value = match ? match.value : '';
    }

    // Explains just the selected audio option. Keep the select node and its native open/focus state intact.
    function audioChoiceInfo(select, t, kind) {
        const tips = kind === 'audio' ? ['original'].map(function (value) {
            const key = 'ui.table.audioOriginal';
            const tip = infoTip({ t: t, label: tr(t, key), text: tr(t, key + '.info') });
            tip.element.hidden = true;
            return { value: value, tip: tip };
        }) : [];
        const element = h('div', { class: 'bfui-choice-line' }, select);
        return {
            element: element,
            paint: function () {
                let description = '';
                tips.forEach(function (entry) {
                    entry.tip.element.hidden = select.value !== entry.value;
                    if (entry.tip.element.hidden) {
                        entry.tip.hide();
                        if (entry.tip.element.parentNode) { entry.tip.element.parentNode.removeChild(entry.tip.element); }
                    } else {
                        if (!entry.tip.element.parentNode) { element.appendChild(entry.tip.element); }
                        description = entry.tip.tipId;
                    }
                });
                if (description) { select.setAttribute('aria-describedby', description); }
                else { select.removeAttribute('aria-describedby'); }
            }
        };
    }

    // ----- The table's words (shared by libraryTable, tableChoice and overridesTable) -----

    /**
     * What a resolved audio order gives, in words: { video } when the first step leaves the track to the video, else { text } (empty when
     * the table has no word for it). w: { t, admin, name(code), understood ([codes], for an order starting with "My languages") }.
     */
    function tableAudioWords(w, chain) {
        const list = Array.isArray(chain) ? chain : null;
        if (!list) {
            return { video: false, text: '' };
        }
        const value = tableAudioValue({ AudioChain: list }, w.understood);
        if (value === 'mine' || value === 'original') {
            return { video: false, text: tr(w.t, value === 'mine' ? 'ui.table.audioMyLanguages' : 'ui.table.audioOriginal') };
        }
        const first = list[0];
        if (!first || VIDEO_AUDIO_KINDS.indexOf(first.Kind) >= 0) {
            return { video: true, text: '' };
        }
        switch (first.Kind) {
            case 'Language':
                return { video: false, text: first.Language ? w.name(first.Language) : '' };
            case 'OriginalLanguage':
            case 'LibraryUsualOriginalLanguage':
                return { video: false, text: tr(w.t, 'ui.table.original') };
            case 'MyLanguages':
                return { video: false, text: w.understood.length ? w.name(w.understood[0]) : tr(w.t, w.admin ? 'ui.table.admin.myLanguages' : 'ui.table.myLanguages') };
            default:
                return { video: false, text: '' };
        }
    }

    /** The same for a subtitle pair and its languages (w also carries reads: [codes] for full subtitles with no language of their own). */
    function tableSubtitleWords(w, u, n, languages) {
        if (u === 'VideoDefault' && n === 'VideoDefault') {
            return { video: true, text: '' };
        }
        if (u === 'Off' && n === 'Off') {
            return { video: false, text: tr(w.t, 'ui.table.off') };
        }
        if (u === 'PartialOnly' && n === 'Full') {
            return { video: false, text: tr(w.t, w.admin ? 'ui.table.admin.smart' : 'ui.table.smart') };
        }
        if (u === 'PartialOnly' && n === 'PartialOnly') {
            return { video: false, text: tr(w.t, 'ui.table.signs') };
        }
        if (u === 'Full' && n === 'Full') {
            const list = Array.isArray(languages) && languages.length ? languages : w.reads;
            return { video: false, text: list.length ? w.name(list[0]) : '' };
        }
        return { video: false, text: '' };
    }

    /** A Defaults row (or any layer carrying the five table fields) in words for one column; no row gives the empty words. */
    function tableRowWords(w, row, kind) {
        if (!row) {
            return { video: false, text: '' };
        }
        return kind === 'audio' ? tableAudioWords(w, row.AudioChain) : tableSubtitleWords(w, row.WhenAudioUnderstood, row.WhenAudioNotUnderstood, row.SubtitleLanguages);
    }

    /** Puts an option in a <select> (before the given node) or takes it out. */
    function placeOption(select, option, wanted, before) {
        if (wanted && option.parentNode !== select) {
            select.insertBefore(option, before || null);
        } else if (!wanted && option.parentNode === select) {
            select.removeChild(option);
        }
    }

    /**
     * Paints one table dropdown (fillTableSelect made it): the Default option's text from the words of what applies without the choice
     * (a default that leaves the track to the video reads "Video default", and an explicit "Video default" is offered only when the default
     * is something else, or is the current value), the Custom option when offered, and the selection (never anything else).
     * cfg: { t, words ({ video, text, perLibrary }), current (the table value), custom (offer Custom), lang (languageName options) }.
     */
    function paintTableSelect(select, parts, cfg) {
        const t = cfg.t;
        const d = cfg.words;
        const offerVideo = !d.video || cfg.current === 'video';
        const video = tr(t, 'ui.table.videoDefault');
        if (d.video) {
            parts.defaultOption.textContent = offerVideo ? tr(t, 'ui.defaultValue', { value: video }) : video;
        } else if (d.perLibrary) {
            parts.defaultOption.textContent = tr(t, 'ui.table.defaultPerLibrary');
        } else {
            parts.defaultOption.textContent = d.text ? tr(t, 'ui.defaultValue', { value: d.text }) : tr(t, 'ui.default');
        }
        placeOption(select, parts.videoOption, offerVideo, parts.defaultOption.nextSibling);
        placeOption(select, parts.customOption, cfg.custom, null);
        selectTableValue(select, parts, cfg.current, cfg.lang);
    }

    /** A function from a language code to its name as the table's dropdowns show it (the catalogue's name, else languageName's). */
    function catalogueNamer(catalogue, lang) {
        const byCode = new Map();
        (catalogue || []).forEach(function (entry) { byCode.set(entry.code.toLowerCase(), entry.name); });
        return function (code) {
            return byCode.get(String(code || '').toLowerCase()) || languageName(code, lang).name;
        };
    }

    /**
     * libraryTable(container, options): the "Audio and subtitles" card. At the top, a two-option segmented control (a
     * radiogroup labelled "Audio and subtitles"):
     * - "Same for every library" shows one row, "All libraries", editing the five table fields of settings.Everyday;
     * - "Choose for each library" shows one row per library, editing that library's entry in settings.Libraries (created on the first
     *   change, dropped when its settings become empty and nothing else is stored in it).
     * Picking a mode stores settings[modeField] = false or true and calls onChange; the other mode's values stay stored. While the field is
     * null the mode is automatic (per library when a library entry sets a table field). It is worked out once, so editing never switches
     * the rows; emptying the last library choice of an automatic per-library table stores true, so the next load shows the same mode.
     * Every language sits in the dropdowns: "My languages" (the person's, in order) and "All languages". Each dropdown starts with
     * "Default (…)" from options.defaults (rows the server computes without the person's table fields): a library row reads its own row;
     * the All libraries row reads the value every library row agrees on, else "Default (set by the server for each library)". A default
     * that leaves the track to the video reads "Video default", and an explicit "Video default" is offered only when the default is
     * something else. A choice the table can't show (an audio order or subtitle mix set elsewhere) appears as "Custom" and is left as it
     * is unless another option is picked.
     * options: { settings (a person's document, a template or { Everyday, Libraries, PerLibraryDefaults }, edited in place),
     *   modeField ('PerLibrary', the default, or 'PerLibraryDefaults'), defaults ({ AllLibraries: Row, Libraries: { "<id>": Row } } or null),
     *   libraries ([{ Id, Name }]), languages (GET Languages), translate (t), uiLanguage, audience ('person', the default, or 'admin': "the
     *   viewer's languages" wording and no "My languages" group), onChange() after each edit }.
     * Returns { element, refresh(defaults) (re-reads the settings; new defaults when given), perLibrary(), showErrors(errors) → unmatched,
     * clearErrors() }.
     */
    function libraryTable(container, options) {
        const o = Object.assign({ settings: null, modeField: 'PerLibrary', defaults: null, libraries: [], languages: [], translate: null, onChange: null, audience: 'person' }, options);
        const t = o.translate || o.t;
        const admin = o.audience === 'admin';
        const lang = { languages: o.languages, uiLanguage: o.uiLanguage || (t && t.language), t: t };
        let defaults = o.defaults || null;
        let automatic = null; // the mode shown while nothing is stored, worked out on the first render
        const snapshots = new Map(); // "<row key>|<kind>" → { fields as first shown (what "Custom" puts back), custom (first shown as Custom) }
        const rows = [];
        const tbody = h('tbody');
        const noLibraries = h('p', { class: 'bfui-empty bfui-table-none', text: tr(t, 'ui.table.noLibraries'), hidden: true });
        const modeButtons = [false, true].map(function (each) {
            const button = h('button', {
                type: 'button', role: 'radio', class: 'bfui-mode-option', dataset: { mode: each ? 'each' : 'same' },
                text: tr(t, each ? 'ui.table.mode.each' : 'ui.table.mode.same')
            });
            button.addEventListener('click', function () { pick(each, false); });
            button.addEventListener('keydown', onModeKey);
            return button;
        });
        const modeGroup = h('div', { class: 'bfui-mode', role: 'radiogroup', 'aria-label': tr(t, 'ui.table.mode') }, modeButtons);
        const element = h('div', { class: 'bfui bfui-table-wrap' },
            modeGroup,
            h('table', { class: 'bfui-table' },
                h('thead', null, h('tr', null,
                    h('th', { scope: 'col', text: tr(t, 'ui.table.library') }),
                    h('th', { scope: 'col', text: tr(t, 'ui.table.audio') }),
                    h('th', { scope: 'col', text: tr(t, 'ui.table.subtitles') }))),
                tbody),
            noLibraries,
            h('p', { class: 'bfui-help bfui-table-help', text: tr(t, admin ? 'ui.table.admin.videoDefaultHelp' : 'ui.table.videoDefaultHelp') }),
            h('p', { class: 'bfui-help bfui-table-help', text: tr(t, admin ? 'ui.table.admin.smartHelp' : 'ui.table.smartHelp') }));

        function settings() {
            const s = o.settings || {};
            s.Everyday = s.Everyday || {};
            s.Libraries = Array.isArray(s.Libraries) ? s.Libraries : [];
            return s;
        }

        // ----- The mode -----

        function storedMode() {
            const value = settings()[o.modeField];
            return value === true || value === false ? value : null;
        }

        function perLibrary() {
            const stored = storedMode();
            if (stored !== null) {
                return stored;
            }
            if (automatic === null) {
                automatic = automaticPerLibrary(settings());
            }
            return automatic;
        }

        function paintMode() {
            const each = perLibrary();
            modeButtons.forEach(function (button, i) {
                const on = (i === 1) === each;
                button.setAttribute('aria-checked', on ? 'true' : 'false');
                button.setAttribute('tabindex', on ? '0' : '-1');
            });
        }

        function pick(each, focus) {
            if (each !== perLibrary()) {
                settings()[o.modeField] = each;
                render();
                if (o.onChange) {
                    o.onChange();
                }
            }
            if (focus) {
                modeButtons[each ? 1 : 0].focus();
            }
        }

        // The radio group keys: Right and Down move to the next choice and pick it, Left and Up to the previous one (Left and Right swap on a
        // right-to-left page), both wrapping; Home and End pick the first and the last; Space and Enter pick the focused one. Tab leaves.
        function onModeKey(e) {
            const i = modeButtons.indexOf(e.currentTarget);
            const rtl = !!(modeGroup.closest && modeGroup.closest('[dir="rtl"]'));
            let next;
            switch (e.key) {
                case 'ArrowRight':
                    next = rtl ? i - 1 : i + 1;
                    break;
                case 'ArrowLeft':
                    next = rtl ? i + 1 : i - 1;
                    break;
                case 'ArrowDown':
                    next = i + 1;
                    break;
                case 'ArrowUp':
                    next = i - 1;
                    break;
                case 'Home':
                    next = 0;
                    break;
                case 'End':
                    next = modeButtons.length - 1;
                    break;
                case ' ':
                case 'Spacebar':
                case 'Enter':
                    next = i;
                    break;
                default:
                    return;
            }
            e.preventDefault();
            next = (next + modeButtons.length) % modeButtons.length;
            pick(next === 1, true);
        }

        // ----- Default values -----

        function rowDefault(libraryId) {
            if (!defaults) {
                return null;
            }
            if (libraryId === null) {
                return defaults.AllLibraries || null;
            }
            const map = defaults.Libraries || {};
            const wanted = compactId(libraryId);
            const key = Object.keys(map).find(function (k) { return compactId(k) === wanted; });
            return key ? map[key] : (defaults.AllLibraries || null);
        }

        // The person's languages as they stand now: the unsaved list when set, else what applies (from the defaults).
        function live(name, row) {
            if (admin) {
                return [];
            }
            const everyday = settings().Everyday;
            if (isFieldSet(everyday, name) && Array.isArray(everyday[name])) {
                return everyday[name];
            }
            const fallback = row || rowDefault(null);
            return (fallback && Array.isArray(fallback[name]) && fallback[name]) || [];
        }

        // Names come from the table's own language list, so a Default label always reads like the option it stands for.
        let currentCatalogue = [];
        function name(code) {
            const key = String(code || '').toLowerCase();
            const listed = currentCatalogue.find(function (entry) { return entry.code.toLowerCase() === key; });
            return listed ? listed.name : languageName(code, lang).name;
        }

        // A Defaults row in words for one column (tableRowWords), with this table's names and the languages as they stand now.
        function rowWords(row, kind) {
            return tableRowWords({ t: t, admin: admin, name: name, understood: live('Understood', row), reads: live('Reads', row) }, row, kind);
        }

        // What "Default" gives for a row: a library row reads its own Defaults row; the All libraries row the value every library row agrees
        // on (Defaults.AllLibraries when there is no library), else { perLibrary } ("set by the server for each library").
        function defaultFor(entry, kind) {
            if (entry.libraryId !== null) {
                return rowWords(rowDefault(entry.libraryId), kind);
            }
            const libs = libraryEntries();
            if (!defaults || !libs.length) {
                return rowWords(rowDefault(null), kind);
            }
            const all = libs.map(function (l) { return rowWords(rowDefault(l.libraryId), kind); });
            const agree = all.every(function (w) { return w.video === all[0].video && w.text === all[0].text; });
            return agree ? all[0] : { video: false, text: '', perLibrary: true };
        }

        // ----- Rows and cells -----

        function libraryEntries() {
            return (o.libraries || []).map(function (l) {
                return { key: compactId(l.Id || l.id), libraryId: l.Id || l.id, name: l.Name || l.name || '' };
            });
        }

        function findEntry(libraryId) {
            return settings().Libraries.find(function (l) { return l && compactId(l.LibraryId) === compactId(libraryId); }) || null;
        }

        function layerFor(entry, create) {
            if (entry.libraryId === null) {
                return settings().Everyday;
            }
            let found = findEntry(entry.libraryId);
            if (!found && create) {
                found = { LibraryId: compactId(entry.libraryId), Name: entry.name, Settings: {} };
                settings().Libraries.push(found);
            }
            if (found) {
                found.Settings = found.Settings || {};
            }
            return found ? found.Settings : null;
        }

        // A library entry goes when its settings are empty and it stores nothing else (an admin entry may mark an original-language library).
        function dropIfEmpty(entry) {
            if (entry.libraryId === null) {
                return;
            }
            const list = settings().Libraries;
            const i = list.findIndex(function (l) { return l && compactId(l.LibraryId) === compactId(entry.libraryId); });
            if (i < 0) {
                return;
            }
            const found = list[i];
            const others = Object.keys(found).filter(function (k) {
                if (k === 'LibraryId' || k === 'Name' || k === 'Settings' || found[k] === null || found[k] === undefined) {
                    return false;
                }
                return !(k === 'OriginalAudio' && (found[k] === 'Auto' || found[k] === 0));
            });
            if (!layerHasValues(found.Settings) && !others.length) {
                list.splice(i, 1);
            }
        }

        function readValue(entry, kind) {
            const layer = layerFor(entry, false);
            const row = rowDefault(entry.libraryId);
            return kind === 'audio' ? tableAudioValue(layer, live('Understood', row)) : tableSubtitleValue(layer, live('Reads', row));
        }

        function myLanguages(kind, row) {
            if (admin) {
                return [];
            }
            return kind === 'audio' ? live('Understood', row).slice() : live('Reads', row).concat(live('Understood', row));
        }

        // One cell: the dropdown and its error line.
        function cell(entry, kind, catalogue) {
            const fields = kind === 'audio' ? TABLE_AUDIO_FIELDS : TABLE_SUBTITLE_FIELDS;
            const snapKey = entry.key + '|' + kind;
            let current = readValue(entry, kind);
            if (!snapshots.has(snapKey)) {
                const layer = layerFor(entry, false);
                const taken = {};
                fields.forEach(function (f) {
                    if (isFieldSet(layer, f)) {
                        taken[f] = deepCopy(layer[f]);
                    }
                });
                snapshots.set(snapKey, { fields: taken, custom: current === 'custom' });
            }
            const snapshot = snapshots.get(snapKey).fields;
            const offerCustom = function () { return current === 'custom' || snapshots.get(snapKey).custom; };
            const select = h('select', {
                class: 'bfui-select',
                'aria-label': tr(t, kind === 'audio' ? 'ui.table.audioFor' : 'ui.table.subtitlesFor', { name: entry.name })
            });
            const parts = fillTableSelect(select, { t: t, kind: kind, audience: o.audience, mine: myLanguages(kind, rowDefault(entry.libraryId)), catalogue: catalogue, lang: lang });
            const error = h('div', { class: 'bfui-field-error', role: 'alert', hidden: true });
            const choiceInfo = audioChoiceInfo(select, t, kind);
            const td = h('td', { class: 'bfui-table-cell bfui-table-' + kind, dataset: { label: tr(t, kind === 'audio' ? 'ui.table.audio' : 'ui.table.subtitles') } }, choiceInfo.element, error);

            // Updates the Default text, whether Video default and Custom are offered, and the selection (never anything else).
            function paint() {
                paintTableSelect(select, parts, { t: t, words: defaultFor(entry, kind), current: current, custom: offerCustom(), lang: lang });
                choiceInfo.paint();
            }

            function clearError() {
                error.hidden = true;
                error.textContent = '';
                td.classList.remove('bfui-has-error');
            }

            function apply(value) {
                clearError();
                if (value === 'custom') {
                    const target = layerFor(entry, Object.keys(snapshot).length > 0);
                    if (target) {
                        fields.forEach(function (f) {
                            if (f in snapshot) {
                                target[f] = deepCopy(snapshot[f]);
                            } else {
                                delete target[f];
                            }
                        });
                    }
                } else if (value || layerFor(entry, false)) {
                    const target = layerFor(entry, true);
                    if (kind === 'audio') {
                        writeTableAudio(target, value);
                    } else {
                        writeTableSubtitles(target, value);
                    }
                }
                dropIfEmpty(entry);
                // An automatic per-library table whose last library choice went back to Default would load as "Same for every library"
                // next time: the mode shown is stored instead.
                if (entry.libraryId !== null && storedMode() === null && perLibrary() && !automaticPerLibrary(settings())) {
                    settings()[o.modeField] = true;
                }
                current = value;
                if (o.onChange) {
                    o.onChange();
                }
                paintAll();
            }

            select.addEventListener('change', function () {
                apply(select.value);
            });
            return {
                element: td,
                select: select,
                paint: paint,
                refreshLanguages: function (catalogue) {
                    current = readValue(entry, kind);
                    refreshTableLanguages(select, parts, {
                        t: t, kind: kind, audience: o.audience, mine: myLanguages(kind, rowDefault(entry.libraryId)), catalogue: catalogue, lang: lang
                    });
                },
                fields: fields,
                showError: function (message) {
                    error.hidden = false;
                    error.textContent = error.textContent ? error.textContent + ' · ' + message : String(message);
                    td.classList.add('bfui-has-error');
                },
                clearError: clearError
            };
        }

        function paintAll() {
            rows.forEach(function (row) {
                row.audio.paint();
                row.subtitles.paint();
            });
        }

        function render() {
            clear(tbody);
            rows.length = 0;
            paintMode();
            const s = settings();
            const each = perLibrary();
            const catalogue = tableLanguages(lang, storedCodes([s.Everyday].concat(s.Libraries.map(function (l) { return l && l.Settings; }))));
            currentCatalogue = catalogue;
            const entries = each ? libraryEntries() : [{ key: 'all', libraryId: null, name: tr(t, 'ui.table.allLibraries') }];
            entries.forEach(function (entry) {
                const audio = cell(entry, 'audio', catalogue);
                const subtitles = cell(entry, 'subtitles', catalogue);
                const row = h('tr', { class: 'bfui-table-row' + (entry.libraryId === null ? ' bfui-table-all' : ''), dataset: { row: entry.key } },
                    h('th', { scope: 'row', class: 'bfui-table-name', text: entry.name }), audio.element, subtitles.element);
                tbody.appendChild(row);
                rows.push({ entry: entry, audio: audio, subtitles: subtitles });
            });
            element.setAttribute('data-mode', each ? 'each' : 'same');
            noLibraries.hidden = !(each && !entries.length);
            paintAll();
        }

        render();
        if (container) {
            container.appendChild(element);
        }

        return {
            element: element,
            refresh: function (next) {
                if (next !== undefined) {
                    defaults = next || null;
                }
                if (element.getAttribute('data-mode') !== (perLibrary() ? 'each' : 'same')) {
                    render();
                } else {
                    const s = settings();
                    const catalogue = tableLanguages(lang, storedCodes([s.Everyday].concat(s.Libraries.map(function (l) { return l && l.Settings; }))));
                    currentCatalogue = catalogue;
                    rows.forEach(function (row) {
                        row.audio.refreshLanguages(catalogue);
                        row.subtitles.refreshLanguages(catalogue);
                    });
                    paintAll();
                }
            },
            perLibrary: perLibrary,
            showErrors: function (errors) {
                const unmatched = [];
                const s = settings();
                (errors || []).forEach(function (error) {
                    const path = String(error.path || '');
                    let rest = null;
                    let key = null;
                    const library = /^libraries\[(\d+)\]\.settings\.(.*)$/i.exec(path);
                    if (/^everyday\./i.test(path)) {
                        key = 'all';
                        rest = path.slice(9);
                    } else if (library && s.Libraries[Number(library[1])]) {
                        key = compactId(s.Libraries[Number(library[1])].LibraryId);
                        rest = library[2];
                    }
                    const field = rest === null ? null : fieldForPath(rest);
                    const row = key === null ? null : rows.find(function (r) { return r.entry.key === key; });
                    const target = row && field ? [row.audio, row.subtitles].find(function (c) { return c.fields.indexOf(field) >= 0; }) : null;
                    if (target) {
                        target.showError(error.message);
                    } else {
                        unmatched.push(error);
                    }
                });
                return unmatched;
            },
            clearErrors: function () {
                rows.forEach(function (row) {
                    row.audio.clearError();
                    row.subtitles.clearError();
                });
            }
        };
    }

    // ----- One column of the table as a dropdown -----

    /**
     * tableChoice(options): one dropdown with exactly the table's choices for one column, built by fillTableSelect and painted
     * by the same code as the table's cells: "Default (X)" (X is what applies without the choice, worded the way the table words its Default),
     * "Video default" (only when the default isn't the video's own), for subtitles Off, "When the audio isn't in my languages" and "Signs and
     * songs only", the "My languages" and "All languages" groups, and "Custom" for a stored choice the table can't show (it stays offered once
     * it has been the value, and it is never written).
     * options: { t, kind ('audio' | 'subtitles'), audience ('person', the default, or 'admin'), languages (GET Languages), uiLanguage,
     *   mine ([codes]: My languages), reads ([codes]: Languages for subtitles; optional, listed before mine for subtitles and used for the
     *   Default text, and mine stands in when it is left out; the 'admin' audience has no person's languages, so it uses neither),
     *   defaultLayer (a layer-like object with AudioChain, ForeignTitles, WhenAudioUnderstood,
     *   WhenAudioNotUnderstood, SubtitleLanguages: what applies without the choice), value (a table value: '', 'video', 'lang:xx', 'off',
     *   'smart', 'signs' or 'custom'), label (the dropdown's aria-label), onChange(value) after the person picks one }.
     * custom keeps an original Custom choice available across row moves. Returns { element, select, getValue(), setValue(value), setLanguages(mine, reads) }. tableAudioValue / tableSubtitleValue turn a layer into a value, and
     * writeTableAudio / writeTableSubtitles write a value back into a layer.
     */
    function tableChoice(options) {
        const o = Object.assign({ kind: 'audio', audience: 'person', languages: [], mine: [], reads: null, defaultLayer: null, value: '', label: null, onChange: null }, options);
        const t = o.t;
        const kind = o.kind === 'subtitles' ? 'subtitles' : 'audio';
        const lang = { languages: o.languages, uiLanguage: o.uiLanguage || (t && t.language), t: t };
        let mine = Array.isArray(o.mine) ? o.mine : [];
        let reads = Array.isArray(o.reads) ? o.reads : mine;
        const value = o.value === null || o.value === undefined ? '' : String(o.value);
        let catalogue = tableLanguages(lang, storedCodes([o.defaultLayer, { Understood: mine, Reads: reads }]).concat(value.indexOf('lang:') === 0 ? [value.slice(5)] : []));
        const select = h('select', { class: 'bfui-select', 'aria-label': o.label || null });
        const parts = fillTableSelect(select, { t: t, kind: kind, audience: o.audience, mine: kind === 'audio' ? mine : reads.concat(mine), catalogue: catalogue, lang: lang });
        // The admin table words its Default without any person's languages (live() gives it none), so this does too.
        const admin = o.audience === 'admin';
        let words = tableRowWords({ t: t, admin: admin, name: catalogueNamer(catalogue, lang), understood: admin ? [] : mine, reads: admin ? [] : reads }, o.defaultLayer, kind);
        let current = value;
        let offerCustom = value === 'custom' || !!o.custom;
        const choiceInfo = audioChoiceInfo(select, t, kind);

        function paint() {
            paintTableSelect(select, parts, { t: t, words: words, current: current, custom: offerCustom, lang: lang });
            choiceInfo.paint();
        }

        select.addEventListener('change', function () {
            current = select.value;
            paint();
            if (o.onChange) {
                o.onChange(current);
            }
        });
        paint();
        return {
            element: h('div', { class: 'bfui bfui-table-choice' }, choiceInfo.element),
            select: select,
            getValue: function () { return select.value; },
            setLanguages: function (nextMine, nextReads) {
                mine = Array.isArray(nextMine) ? nextMine : [];
                reads = Array.isArray(nextReads) ? nextReads : mine;
                catalogue = tableLanguages(lang, storedCodes([o.defaultLayer, { Understood: mine, Reads: reads }])
                    .concat(current.indexOf('lang:') === 0 ? [current.slice(5)] : []));
                refreshTableLanguages(select, parts, {
                    t: t, kind: kind, audience: o.audience, mine: kind === 'audio' ? mine : reads.concat(mine), catalogue: catalogue, lang: lang
                });
                words = tableRowWords({ t: t, admin: admin, name: catalogueNamer(catalogue, lang), understood: admin ? [] : mine, reads: admin ? [] : reads }, o.defaultLayer, kind);
                paint();
            },
            setValue: function (next) {
                current = next === null || next === undefined ? '' : String(next);
                if (current === 'custom') {
                    offerCustom = true;
                }
                paint();
            }
        };
    }

    /** Reads a saved-choice status without blocking the page; never infers it from an unsaved table. */
    function choiceStatus(options) {
        const o = options || {};
        const element = h('p', { class: o.className || 'bfui-help', role: 'status', hidden: true, text: o.text || '' });
        let revision = 0;
        function refresh() {
            const token = ++revision;
            element.hidden = true;
            return Promise.resolve().then(o.load).then(function (answer) {
                if (token === revision) { element.hidden = !(answer && answer.HasChoices === false); }
            }, function () { /* Unknown status says nothing. */ });
        }
        refresh();
        return { element: element, refresh: refresh };
    }

    /** A deliberately narrow rule shape: one enabled, positive, literal Genre condition with one name. */
    function isGenreRow(rule) {
        if (!rule || rule.Enabled !== true || !Array.isArray(rule.Conditions) || rule.Conditions.length !== 1) { return false; }
        const c = rule.Conditions[0];
        return !!c && c.Kind === 'Genre' && (c.Negate === false || c.Negate === undefined)
            && (c.IsRegex === false || c.IsRegex === undefined) && Array.isArray(c.Values) && c.Values.length === 1
            && typeof c.Values[0] === 'string' && c.Values[0].trim().length > 0
            && (rule.Match === 'All' || rule.Match === 'Any' || rule.Match === undefined);
    }

    /** Applies genre edits without undoing fresh saved-row order; new rows keep their edited position before surviving saved rows. */
    function mergeGenreRows(before, edited, fresh) {
        function genreKey(rule) {
            return rule.Conditions[0].Values[0].trim().toLowerCase();
        }
        function identity(rule) {
            return rule.Id ? 'id:' + String(rule.Id).toLowerCase() : 'genre:' + genreKey(rule);
        }
        const initialRows = (before || []).filter(isGenreRow);
        const editedRows = (edited || []).filter(isGenreRow);
        const initial = new Map();
        initialRows.forEach(function (rule) { initial.set(identity(rule), rule); });
        const wanted = new Set(editedRows.map(identity));
        // Compare only surviving original rows: adding/removing a row is not a request to reorder the others.
        const originalOrder = initialRows.map(identity).filter(function (id) { return wanted.has(id); });
        const editedOrder = editedRows.map(identity).filter(function (id) { return initial.has(id); });
        const orderEdited = originalOrder.some(function (id, i) { return id !== editedOrder[i]; });
        const stored = new Map();
        (fresh || []).filter(isGenreRow).forEach(function (rule) { stored.set(identity(rule), rule); });
        const desired = [];
        const additions = [];
        editedRows.forEach(function (rule) {
            const id = identity(rule);
            const was = initial.get(id);
            if (!was) {
                additions.push(rule);
                return;
            }
            const target = stored.get(id);
            if (!target) { return; } // A concurrent removal is kept.
            TABLE_FIELDS.forEach(function (field) {
                const oldValue = was.Settings && was.Settings[field];
                const newValue = rule.Settings && rule.Settings[field];
                if (JSON.stringify(oldValue) !== JSON.stringify(newValue)) {
                    target.Settings = target.Settings || {};
                    if (newValue === undefined) { delete target.Settings[field]; }
                    else { target.Settings[field] = deepCopy(newValue); }
                }
            });
            desired.push(target);
        });
        let out = (fresh || []).filter(function (rule) {
            return !isGenreRow(rule) || !initial.has(identity(rule)) || wanted.has(identity(rule));
        });
        if (orderEdited) {
            let at = 0;
            out = out.map(function (rule) {
                return isGenreRow(rule) && initial.has(identity(rule)) ? desired[at++] : rule;
            });
        }
        const genres = new Set(out.filter(isGenreRow).map(genreKey));
        additions.forEach(function (rule) {
            const genre = genreKey(rule);
            // Two admins may add the same genre before either sees the other's row.
            // The fresh row wins the entire addition conflict: retain its id, choices, fields and position unchanged.
            if (!String(rule.Id || '').trim() && genres.has(genre)) { return; }
            // Add appends by default. Moving a new row up gives it a following saved-row anchor.
            // Insert before the next surviving anchor in edited order, leaving the fresh saved rows in their current order.
            const next = editedRows.slice(editedRows.indexOf(rule) + 1).find(function (following) {
                const id = identity(following);
                return initial.has(id) && out.indexOf(stored.get(id)) >= 0;
            });
            const at = next ? out.indexOf(stored.get(identity(next))) : out.length;
            out.splice(at, 0, deepCopy(rule));
            genres.add(genre);
        });
        return out;
    }

    /**
     * Genre rows edit settings.SmartRules in place, using tableChoice. No network writes.
     * options: { settings, t, languages, uiLanguage, audience, mine/reads (arrays or live getters),
     * loadGenres() -> /Genres response (filtered to the signed-in viewer by the caller), onChange() }.
     * Only structural edits draw rows again; changing either dropdown preserves every control node.
     */
    function genreRows(container, options) {
        const o = options || {};
        const t = o.t;
        let catalogue = null;
        let loading = false;
        const snapshots = new Map(); // rule object -> original audio/subtitle columns, retained across structural edits
        const controls = [];
        const tbody = h('tbody');
        const list = h('table', { class: 'bfui-table' },
            h('thead', null, h('tr', null,
                h('th', { scope: 'col', text: tr(t, 'ui.genres.title') }),
                h('th', { scope: 'col', text: tr(t, 'ui.table.audio') }),
                h('th', { scope: 'col', text: tr(t, 'ui.table.subtitles') }),
                h('th', { scope: 'col' }, h('span', { class: 'bfui-sr', text: tr(t, 'ui.remove') })))), tbody);
        const empty = h('p', { class: 'bfui-help', text: tr(t, 'ui.genres.empty') });
        const message = h('p', { class: 'bfui-help', role: 'status', hidden: true });
        const error = h('p', { class: 'bfui-field-error', role: 'alert', hidden: true });
        const picker = h('select', { class: 'bfui-select', 'aria-label': tr(t, 'ui.genres.choose') });
        const add = h('button', { type: 'button', class: 'bfui-btn', text: tr(t, 'ui.genres.add'), onclick: openPicker });
        const confirm = h('button', { type: 'button', class: 'bfui-btn', text: tr(t, 'ui.genres.add'), disabled: true, onclick: addRow });
        const retry = h('button', { type: 'button', class: 'bfui-btn', text: tr(t, 'ui.genres.retry'), hidden: true, onclick: openPicker });
        const pickerBox = h('div', { class: 'bfui-genres-picker', hidden: true }, picker, confirm,
            h('button', { type: 'button', class: 'bfui-btn', text: tr(t, 'ui.cancel'), onclick: function () { pickerBox.hidden = true; add.focus(); } }));
        const element = h('section', { class: 'bfui bfui-genres', 'aria-label': tr(t, 'ui.genres.title') },
            h('h3', { text: tr(t, 'ui.genres.title') }),
            h('p', { class: 'bfui-help', text: tr(t, 'ui.genres.firstMatch') }), list, empty,
            add, pickerBox, message, error, retry);
        picker.addEventListener('change', function () { confirm.disabled = !picker.value; });

        function rules() { return Array.isArray(o.settings.SmartRules) ? o.settings.SmartRules : []; }
        function rows() { return rules().filter(isGenreRow); }
        function genre(rule) { return rule.Conditions[0].Values[0]; }
        function key(name) { return String(name).trim().toLowerCase(); }
        function duplicate(name) { return rows().some(function (r) { return key(genre(r)) === key(name); }); }
        function full() { return rows().length >= 100; }
        function live(name) {
            const value = typeof o[name] === 'function' ? o[name]() : o[name];
            return Array.isArray(value) ? value : [];
        }
        function notify() { if (o.onChange) { o.onChange(); } }
        function fillPicker() {
            clear(picker);
            picker.appendChild(h('option', { value: '', text: tr(t, 'ui.genres.choose') }));
            (catalogue || []).filter(function (name) { return !duplicate(name); }).forEach(function (name) {
                picker.appendChild(h('option', { value: name, text: name }));
            });
            picker.value = '';
            confirm.disabled = true;
            message.hidden = picker.options.length > 1;
            message.textContent = tr(t, 'ui.genres.none');
        }
        function openPicker() {
            if (loading || full()) { return; }
            error.hidden = true;
            retry.hidden = true;
            if (catalogue !== null) {
                fillPicker();
                pickerBox.hidden = false;
                picker.focus();
                return;
            }
            loading = true;
            add.disabled = true;
            message.hidden = false;
            message.textContent = tr(t, 'common.loading');
            Promise.resolve().then(function () {
                if (typeof o.loadGenres !== 'function') { throw new Error('Missing genre loader'); }
                return o.loadGenres();
            }).then(function (response) {
                const items = Array.isArray(response) ? response : response && response.Items;
                if (!Array.isArray(items)) { throw new Error('Invalid genre response'); }
                const seen = new Set();
                catalogue = items.map(function (item) { return typeof item === 'string' ? item : item && item.Name; })
                    .filter(function (name) {
                        if (typeof name !== 'string' || !name.trim() || seen.has(key(name))) { return false; }
                        seen.add(key(name));
                        return true;
                    }).sort(compareText(uiLanguage(o)));
                loading = false;
                add.disabled = full();
                fillPicker();
                pickerBox.hidden = false;
                if (element.isConnected) { picker.focus(); }
            }).catch(function () {
                loading = false;
                add.disabled = full();
                message.hidden = true;
                error.hidden = false;
                error.textContent = tr(t, 'ui.genres.loadError');
                retry.hidden = false;
            });
        }
        function addRow() {
            const name = picker.value;
            if (!name || full()) { return; }
            if (duplicate(name)) {
                error.textContent = tr(t, 'ui.genres.duplicate');
                error.hidden = false;
                return;
            }
            if (!catalogue || catalogue.indexOf(name) < 0) { return; }
            if (!Array.isArray(o.settings.SmartRules)) { o.settings.SmartRules = []; }
            o.settings.SmartRules.push({
                Id: '', Name: name, Enabled: true, Match: 'All',
                Conditions: [{ Kind: 'Genre', Values: [name], Negate: false, IsRegex: false }], Settings: {}
            });
            pickerBox.hidden = true;
            message.hidden = true;
            error.hidden = true;
            render();
            notify();
            const selects = tbody.querySelectorAll('select');
            if (selects.length) { selects[selects.length - 2].focus(); }
        }
        function move(rule, direction) {
            const visible = rows();
            const next = visible[visible.indexOf(rule) + direction];
            if (!next) { return; }
            const stored = rules();
            const a = stored.indexOf(rule);
            const b = stored.indexOf(next);
            stored[a] = next;
            stored[b] = rule;
            render();
            notify();
            focusRow(rule, direction < 0 ? 'up' : 'down');
        }
        function focusRow(rule, action) {
            const row = tbody.children[rows().indexOf(rule)];
            const target = row && row.querySelector('[data-action="' + action + '"]');
            if (target && !target.disabled) { target.focus(); }
            else if (row) { row.querySelector('select').focus(); }
            else { add.focus(); }
        }
        function render() {
            clear(tbody);
            controls.length = 0;
            const visible = rows();
            visible.forEach(function (rule, index) {
                const name = genre(rule);
                const cells = ['audio', 'subtitles'].map(function (kind) {
                    const fields = kind === 'audio' ? TABLE_AUDIO_FIELDS : TABLE_SUBTITLE_FIELDS;
                    const current = kind === 'audio' ? tableAudioValue(rule.Settings, live('mine')) : tableSubtitleValue(rule.Settings, live('reads'));
                    if (!snapshots.has(rule)) { snapshots.set(rule, {}); }
                    const saved = snapshots.get(rule);
                    if (!saved[kind]) {
                        const taken = {};
                        fields.forEach(function (field) {
                            if (isFieldSet(rule.Settings, field)) { taken[field] = deepCopy(rule.Settings[field]); }
                        });
                        saved[kind] = { fields: taken, custom: current === 'custom' };
                    }
                    const snapshot = saved[kind].fields;
                    const control = tableChoice({
                        t: t, kind: kind, audience: o.audience, languages: o.languages || [], uiLanguage: o.uiLanguage,
                        mine: live('mine'), reads: live('reads'),
                        value: current, custom: saved[kind].custom,
                        label: tr(t, kind === 'audio' ? 'ui.table.audioFor' : 'ui.table.subtitlesFor', { name: name }),
                        onChange: function (value) {
                            // Custom restores just this column's initial fields; other stored settings always survive.
                            rule.Settings = rule.Settings || {};
                            fields.forEach(function (field) {
                                if (value === 'custom') {
                                    if (field in snapshot) { rule.Settings[field] = deepCopy(snapshot[field]); }
                                    else { delete rule.Settings[field]; }
                                }
                            });
                            if (value !== 'custom') {
                                if (kind === 'audio') { writeTableAudio(rule.Settings, value); }
                                else { writeTableSubtitles(rule.Settings, value); }
                            }
                            notify();
                        }
                    });
                    controls.push(control);
                    return h('td', { class: 'bfui-table-cell', 'data-label': tr(t, kind === 'audio' ? 'ui.table.audio' : 'ui.table.subtitles') }, control.element);
                });
                const actions = h('div', { class: 'bfui-genres-actions' });
                function action(id, text, label, disabled, fn) {
                    actions.appendChild(h('button', {
                        type: 'button', class: 'bfui-btn', text: text, disabled: disabled, 'data-action': id,
                        'aria-label': label + ' · ' + name, onclick: fn
                    }));
                }
                action('up', '↑', tr(t, 'ui.moveUp'), index === 0, function () { move(rule, -1); });
                action('down', '↓', tr(t, 'ui.moveDown'), index === visible.length - 1, function () { move(rule, 1); });
                action('remove', tr(t, 'ui.remove'), tr(t, 'ui.remove'), false, function () {
                    rules().splice(rules().indexOf(rule), 1);
                    snapshots.delete(rule);
                    render();
                    notify();
                    focusRow(rows()[Math.min(index, rows().length - 1)], 'remove');
                });
                tbody.appendChild(h('tr', { class: 'bfui-table-row' },
                    h('th', { scope: 'row', class: 'bfui-table-name', text: name }), cells, h('td', null, actions)));
            });
            list.hidden = !visible.length;
            empty.hidden = visible.length > 0;
            add.disabled = loading || full();
            message.hidden = true;
            if (full()) {
                message.hidden = false;
                message.textContent = tr(t, 'ui.genres.limit');
                pickerBox.hidden = true;
            }
        }
        render();
        if (container) { container.appendChild(element); }
        return {
            element: element,
            refreshLanguages: function () {
                controls.forEach(function (control) { control.setLanguages(live('mine'), live('reads')); });
            }
        };
    }

    // ----- Show and movie overrides -----

    /** An override's item with its year; a season or an episode after its show ("Show · Season 2"). row: an OverrideSummary. */
    function overrideName(row, t) {
        const inShow = !!row.SeriesName && (row.Type === 'Season' || row.Type === 'Episode');
        // An episode has no season or episode number here, so it reads like a season does: "Show · Name".
        return describeItem({ Name: row.Name || '', Type: inShow ? 'Season' : '', ProductionYear: row.ProductionYear, SeriesName: row.SeriesName }, t);
    }

    /** The word for an override that is narrower than a show or a movie (a season, an episode, an older collection choice); '' otherwise. */
    function overrideScopeWord(row, t) {
        const scope = String(row.Scope || '');
        if (scope === 'Season') {
            return tr(t, 'titles.scope.season');
        }
        if (scope === 'Title' && row.Type === 'Episode') {
            return tr(t, 'titles.scope.episode');
        }
        return scope === 'Collection' ? tr(t, 'titles.scope.collection') : '';
    }

    /** A table value (not '') as the option reads. w: { t, admin, name(code) }. */
    function tableValueText(w, value) {
        switch (value) {
            case 'mine':
                return tr(w.t, 'ui.table.audioMyLanguages');
            case 'original':
                return tr(w.t, 'ui.table.audioOriginal');
            case 'video':
                return tr(w.t, 'ui.table.videoDefault');
            case 'off':
                return tr(w.t, 'ui.table.off');
            case 'smart':
                return tr(w.t, w.admin ? 'ui.table.admin.smart' : 'ui.table.smart');
            case 'signs':
                return tr(w.t, 'ui.table.signs');
            case 'custom':
                return tr(w.t, 'ui.table.custom');
            default:
                return String(value).indexOf('lang:') === 0 ? w.name(String(value).slice(5)) : '';
        }
    }

    /** A remembered track in words: its language, and "commentary", "audio description", "signs and songs" or "SDH" when it is one. */
    function rememberedTrackText(w, track, kind) {
        const code = track && (track.Variety || track.Language);
        const language = code ? w.name(code) : tr(w.t, 'titles.summary.unknownLanguage');
        const word = String((track && track.Kind) || '').toLowerCase();
        if (kind === 'audio') {
            if (word === 'commentary') {
                return tr(w.t, 'titles.summary.commentary', { language: language });
            }
            return word === 'ad' ? tr(w.t, 'titles.summary.audioDescription', { language: language }) : language;
        }
        if (word === 'partial') {
            return tr(w.t, 'titles.summary.subtitlesPartial', { languages: language });
        }
        return word === 'sdh' ? tr(w.t, 'titles.summary.subtitlesSdh', { languages: language }) : language;
    }

    /**
     * What an override sets for one column, in the table's words: { text, set }. The override's own settings come first (through
     * tableAudioValue / tableSubtitleValue), then a remembered track change; a column neither sets reads "Library's choice" (set false).
     */
    function overrideColumn(w, row, kind) {
        const settings = row.Settings || {};
        const value = kind === 'audio' ? tableAudioValue(settings, w.understood) : tableSubtitleValue(settings, w.reads);
        if (value) {
            return { text: tableValueText(w, value), set: true };
        }
        const manual = row.Manual || null;
        if (manual && kind === 'audio' && manual.Audio) {
            return { text: rememberedTrackText(w, manual.Audio, 'audio'), set: true };
        }
        if (manual && kind === 'subtitles' && manual.SubtitlesOff) {
            return { text: tr(w.t, 'ui.table.off'), set: true };
        }
        if (manual && kind === 'subtitles' && manual.Subtitle) {
            return { text: rememberedTrackText(w, manual.Subtitle, 'subtitles'), set: true };
        }
        return { text: tr(w.t, 'overrides.notSet'), set: false };
    }

    /** The language codes the remembered track changes of some overrides name. */
    function rememberedCodes(rows) {
        const out = [];
        rows.forEach(function (row) {
            [row.Manual && row.Manual.Audio, row.Manual && row.Manual.Subtitle].forEach(function (track) {
                const code = track && (track.Variety || track.Language);
                if (code) {
                    out.push(code);
                }
            });
        });
        return out;
    }

    /**
     * overridesTable(container, options): a person's show and movie overrides as a table, for the person page and the admin
     * People editor: Show or movie | Library | Audio | Subtitles | Remove, sorted by library then name (a show's seasons after the show; rows
     * in no library last).
     * - The first column is the item's name with its year, and for a season or an episode its scope ("Show · Season 2 · For this season");
     *   a remembered track change adds a second line, "Remembered from a track change".
     * - Audio and Subtitles are worded like the "Audio and subtitles" table ("Japanese", "Off", "Video default", "Custom", …); a column the
     *   override doesn't set reads "Library's choice", unless only a remembered track change sets it, which shows that track's language.
     * - Each row has a Remove button (aria-label "Remove the override for <name>"). It calls onRemove(row) and, when that resolves, takes the
     *   row out and moves focus to the next row's Remove (else the previous one's, else the section's heading button). A row whose ItemId
     *   isn't 32 hex digits (after the dashes are dropped) gets a disabled Remove and never reaches onRemove. Bulk removal uses its own
     *   explicit action.
     * - "Remove all show and movie overrides" (a danger button, shown while there is a row) asks first, then calls onRemoveAll().
     * - With no row the table gives way to a line saying so, and the section stays.
     * - It stacks like the other tables on a narrow screen.
     * options: { t, rows (OverrideSummary[]: ItemId, Scope, Name, Type, ProductionYear, SeriesName, LibraryName, Settings, Manual),
     *   languages (GET Languages), uiLanguage, mine ([codes]: My languages) and reads ([codes]: Languages for subtitles) for the table's words
     *   (each an array, or a function returning one so it can follow unsaved edits), audience ('person', the default, or 'admin'),
     *   onRemove(row) → Promise, onRemoveAll() → Promise (a rejection leaves the rows as they are and goes to onError(error) when given),
     *   confirmText (the confirmation's message; the default is overrides.removeAll.confirmText) }.
     * Returns { element, refresh(rows) } (without rows, refresh draws the same rows again, e.g. after My languages changed).
     */
    function overridesTable(container, options) {
        const o = Object.assign({ rows: [], languages: [], mine: [], reads: [], audience: 'person', onRemove: null, onRemoveAll: null, onError: null, confirmText: '' }, options);
        const t = o.t;
        const lang = { languages: o.languages, uiLanguage: o.uiLanguage || (t && t.language), t: t };
        const compare = compareText(uiLanguage(lang));
        const element = h('div', { class: 'bfui bfui-overrides' });
        const buttons = new Map(); // row key → that row's Remove button
        let removeAllButton = null;
        let list = Array.isArray(o.rows) ? o.rows.slice() : [];
        let shown = []; // the keys of the rows that have a working Remove, in the order they are drawn
        let busy = false;
        let fresh = 0; // counts refresh(rows) calls: rows a callback has just brought up to date aren't emptied again

        function rowKey(row) {
            return compactId(row.ItemId) + '|' + String(row.Scope || '');
        }

        // Only an override with a real id can be removed alone; without one the DELETE would be the route that removes them all.
        function removable(row) {
            return /^[0-9a-f]{32}$/.test(compactId(row.ItemId));
        }

        function codes(source) {
            const value = typeof source === 'function' ? source() : source;
            return Array.isArray(value) ? value : [];
        }

        function fail(error) {
            if (o.onError) {
                o.onError(error);
            }
        }

        // The Remove buttons are off while a removal is on its way, so a second click can't start another.
        function setBusy(on) {
            busy = on;
            buttons.forEach(function (button) { button.disabled = on; });
            if (removeAllButton) {
                removeAllButton.disabled = on;
            }
        }

        // The section the table sits in (its heading button), else the table itself: where focus goes when no row is left to take it.
        function focusSection() {
            const section = element.closest ? element.closest('.bfui-disclosure') : null;
            const heading = section ? section.querySelector('.bfui-disclosure-button') : null;
            if (heading) {
                heading.focus();
            } else {
                element.setAttribute('tabindex', '-1');
                element.focus();
            }
        }

        function render() {
            const active = typeof document !== 'undefined' ? document.activeElement : null;
            let focusKey = null;
            buttons.forEach(function (button, key) {
                if (button === active) {
                    focusKey = key;
                }
            });
            const focusAll = !!removeAllButton && removeAllButton === active;
            clear(element);
            buttons.clear();
            removeAllButton = null;
            shown = [];
            if (!list.length) {
                element.appendChild(h('p', { class: 'bfui-empty bfui-overrides-empty', text: tr(t, 'overrides.empty') }));
                return;
            }
            const mine = codes(o.mine);
            const reads = codes(o.reads);
            const catalogue = tableLanguages(lang, storedCodes(list.map(function (row) { return row.Settings; })).concat(rememberedCodes(list), mine, reads));
            const w = { t: t, admin: o.audience === 'admin', name: catalogueNamer(catalogue, lang), understood: mine, reads: reads };
            const keys = new Set(); // each row gets a key of its own, also rows that share an id or have none
            const entries = list.map(function (row, at) {
                const name = overrideName(row, t);
                const scope = overrideScopeWord(row, t);
                let key = removable(row) ? rowKey(row) : '#' + at;
                if (keys.has(key)) {
                    key += '#' + at;
                }
                keys.add(key);
                return { row: row, key: key, sort: row.SeriesName || row.Name || '', name: name, title: scope ? name + ' · ' + scope : name, library: row.LibraryName || '' };
            }).sort(function (a, b) {
                if (!a.library !== !b.library) {
                    return a.library ? -1 : 1;
                }
                // Within a show, the show's own row (no SeriesName) comes before its seasons and episodes.
                return compare(a.library, b.library) || compare(a.sort, b.sort) || (a.row.SeriesName ? 1 : 0) - (b.row.SeriesName ? 1 : 0)
                    || compare(a.title, b.title) || compare(a.key, b.key);
            });
            const tbody = h('tbody');
            entries.forEach(function (entry) {
                const row = entry.row;
                const can = removable(row);
                const remove = h('button', {
                    type: 'button', class: 'bfui-btn bfui-overrides-remove', text: tr(t, 'common.remove'), disabled: busy || !can,
                    'aria-label': tr(t, 'overrides.removeFor', { name: entry.title }),
                    onclick: function () { removeOne(row, entry.key); }
                });
                const cell = function (kind) {
                    const column = overrideColumn(w, row, kind);
                    return h('td', {
                        class: 'bfui-overrides-cell bfui-overrides-' + kind + (column.set ? '' : ' bfui-overrides-unset'),
                        dataset: { label: tr(t, kind === 'audio' ? 'overrides.col.audio' : 'overrides.col.subtitles') }, text: column.text
                    });
                };
                // A disabled Remove (no usable id) stays out of the focus order and out of setBusy, which would switch it back on.
                if (can) {
                    buttons.set(entry.key, remove);
                    shown.push(entry.key);
                }
                tbody.appendChild(h('tr', { class: 'bfui-overrides-row', dataset: { item: compactId(row.ItemId), scope: String(row.Scope || '') } },
                    h('th', { scope: 'row', class: 'bfui-overrides-name' },
                        h('span', { class: 'bfui-overrides-title', text: entry.title }),
                        row.Manual ? h('span', { class: 'bfui-overrides-note', text: tr(t, 'overrides.remembered') }) : null),
                    h('td', {
                        class: 'bfui-overrides-cell bfui-overrides-library' + (entry.library ? '' : ' bfui-overrides-unset'),
                        dataset: { label: tr(t, 'overrides.col.library') }, text: entry.library || tr(t, 'overrides.noLibrary')
                    }),
                    cell('audio'),
                    cell('subtitles'),
                    h('td', { class: 'bfui-overrides-action' }, remove)));
            });
            removeAllButton = h('button', {
                type: 'button', class: 'bfui-btn bfui-btn-danger bfui-overrides-remove-all', text: tr(t, 'overrides.removeAll'), disabled: busy, onclick: removeAll
            });
            element.appendChild(h('table', { class: 'bfui-table bfui-overrides-table', 'aria-label': tr(t, 'overrides.title') },
                h('thead', null, h('tr', null,
                    h('th', { scope: 'col', text: tr(t, 'overrides.col.item') }),
                    h('th', { scope: 'col', text: tr(t, 'overrides.col.library') }),
                    h('th', { scope: 'col', text: tr(t, 'overrides.col.audio') }),
                    h('th', { scope: 'col', text: tr(t, 'overrides.col.subtitles') }),
                    h('th', { scope: 'col' }, h('span', { class: 'bfui-sr', text: tr(t, 'common.remove') })))),
                tbody));
            element.appendChild(h('div', { class: 'bfui-overrides-actions' }, removeAllButton));
            // A redraw for another reason (new rows, My languages changed) keeps focus on the same button.
            if (focusKey && buttons.get(focusKey)) {
                buttons.get(focusKey).focus();
            } else if (focusAll) {
                removeAllButton.focus();
            }
        }

        async function removeOne(row, key) {
            if (busy || !removable(row)) {
                return;
            }
            const at = shown.indexOf(key);
            const neighbour = shown[at + 1] || shown[at - 1] || null;
            setBusy(true);
            let done = false;
            try {
                await o.onRemove(row);
                done = true;
            } catch (e) {
                fail(e);
            }
            if (!done) {
                setBusy(false);
                // The button was disabled during the request, which drops focus; put it back where the person was.
                const again = buttons.get(key);
                if (again) {
                    again.focus();
                }
                return;
            }
            busy = false;
            const gone = rowKey(row);
            list = list.filter(function (each) { return !removable(each) || rowKey(each) !== gone; });
            render();
            const next = neighbour ? buttons.get(neighbour) : null;
            if (next) {
                next.focus();
            } else {
                focusSection();
            }
        }

        async function removeAll() {
            if (busy) {
                return;
            }
            const ok = await confirmDialog({
                t: t, title: tr(t, 'overrides.removeAll.confirmTitle'), message: o.confirmText || tr(t, 'overrides.removeAll.confirmText'),
                okText: tr(t, 'common.remove'), danger: true
            });
            if (!ok) {
                return;
            }
            const seen = fresh;
            setBusy(true);
            let done = false;
            try {
                await o.onRemoveAll();
                done = true;
            } catch (e) {
                fail(e);
            }
            if (!done) {
                setBusy(false);
                // The button was disabled during the request, which drops focus; put it back where the person was.
                if (removeAllButton) {
                    removeAllButton.focus();
                }
                return;
            }
            busy = false;
            if (fresh === seen) {
                list = [];
            }
            render();
            focusSection();
        }

        render();
        if (container) {
            container.appendChild(element);
        }
        return {
            element: element,
            refresh: function (rows) {
                if (rows !== undefined) {
                    list = Array.isArray(rows) ? rows.slice() : [];
                    fresh++;
                }
                render();
            }
        };
    }

    // ----- Collapsible groups -----

    function readOpen(key) {
        if (!key) {
            return null;
        }
        try {
            const value = global.localStorage ? global.localStorage.getItem(key) : null;
            return value === '1' ? true : (value === '0' ? false : null);
        } catch (e) {
            return null;
        }
    }

    function writeOpen(key, open) {
        if (!key) {
            return;
        }
        try {
            if (global.localStorage) {
                global.localStorage.setItem(key, open ? '1' : '0');
            }
        } catch (e) {
            // storage blocked: the group just isn't remembered
        }
    }

    /**
     * disclosure(options): a group with a heading button that shows and hides its body (aria-expanded, aria-controls). Closed by default;
     * with a storageKey its state is remembered in localStorage ('1' open, '0' closed; blocked storage is ignored).
     * options: { title, level (heading level, 2–6; default 3), storageKey, open (when nothing is stored; default false), className, id,
     *   build(body) (called once, on the first opening), onToggle(open) }.
     * Returns { element, body, button, open(), close(), toggle(), isOpen() }; element.bfuiDisclosure is the same object (see reveal).
     */
    function disclosure(options) {
        const o = Object.assign({ title: '', level: 3, storageKey: null, open: false, className: '', id: null, build: null, onToggle: null }, options);
        const bodyId = uid('bfui-dc');
        const level = Math.min(6, Math.max(2, Number(o.level) || 3));
        const button = h('button', { type: 'button', class: 'bfui-disclosure-button', 'aria-expanded': 'false', 'aria-controls': bodyId },
            h('span', { class: 'bfui-disclosure-icon', 'aria-hidden': 'true' }),
            h('span', { class: 'bfui-disclosure-title', text: o.title }));
        const body = h('div', { class: 'bfui-disclosure-body', id: bodyId, hidden: true });
        const element = h('section', { class: 'bfui bfui-disclosure' + (o.className ? ' ' + o.className : ''), id: o.id || null },
            h('h' + level, { class: 'bfui-disclosure-heading' }, button),
            body);
        let built = false;
        let open = false;

        function set(next, remember) {
            open = !!next;
            if (open && !built) {
                built = true;
                if (o.build) {
                    o.build(body);
                }
            }
            body.hidden = !open;
            button.setAttribute('aria-expanded', open ? 'true' : 'false');
            element.classList.toggle('bfui-disclosure-open', open);
            if (remember) {
                writeOpen(o.storageKey, open);
            }
            if (o.onToggle) {
                o.onToggle(open);
            }
        }

        button.addEventListener('click', function () { set(!open, true); });
        const stored = readOpen(o.storageKey);
        set(stored === null ? !!o.open : stored, false);
        const handle = {
            element: element,
            body: body,
            button: button,
            open: function () { if (!open) { set(true, true); } },
            close: function () { if (open) { set(false, true); } },
            toggle: function () { set(!open, true); },
            isOpen: function () { return open; }
        };
        element.bfuiDisclosure = handle;
        return handle;
    }

    /** Opens every closed <details> and disclosure group around a node, so something inside them (an error) can be seen. */
    function reveal(node) {
        for (let n = node; n && n.nodeType === 1; n = n.parentNode) {
            if (n.tagName === 'DETAILS' && !n.open) {
                n.open = true;
            } else if (n.bfuiDisclosure && !n.bfuiDisclosure.isOpen()) {
                n.bfuiDisclosure.open();
            }
        }
    }

    // ----- Items (Jellyfin search) -----

    /** A one-line description of a Jellyfin item ("Show · S1E2 · Name", "Movie (2001)"). */
    function describeItem(item, t) {
        if (!item) {
            return '';
        }
        const name = item.Name || '';
        // A number that is 0 (season 0, Specials) stays; only a missing one reads "?".
        const number = function (n) { return n === null || n === undefined ? '?' : n; };
        switch (item.Type) {
            case 'Episode':
                return tr(t, 'ui.item.episode', { series: item.SeriesName || '', season: number(item.ParentIndexNumber), episode: number(item.IndexNumber), name: name });
            case 'Season':
                return tr(t, 'ui.item.season', { series: item.SeriesName || '', name: name });
            default:
                // A name that already ends in its year (taken from the file name or the file's own title) doesn't get it twice.
                return item.ProductionYear && !new RegExp('[(\\[]' + item.ProductionYear + '[)\\]]\\s*$').test(name)
                    ? tr(t, 'ui.item.withYear', { name: name, year: item.ProductionYear })
                    : name;
        }
    }

    /**
     * itemSearch(container, options): a search box over Jellyfin's GET /Items, as the person options.userId names (else as the signed-in one).
     * options: { t, api (from BabelfinUI.api, or anything with the same getJSON), userId, types (default Series, Movie, Season, Episode, BoxSet),
     * limit, label, onPick(item) }.
     * Returns { element, focus(), clear() }.
     */
    function itemSearch(container, options) {
        const o = Object.assign({ types: ['Series', 'Movie', 'Season', 'Episode', 'BoxSet'], limit: 20, onPick: null, label: null }, options);
        const t = o.t;
        const inputId = uid('bfui-is');
        const input = h('input', { type: 'search', id: inputId, class: 'bfui-input', autocomplete: 'off', placeholder: tr(t, 'ui.search.placeholder') });
        const status = h('div', { class: 'bfui-help', role: 'status', 'aria-live': 'polite' });
        const results = h('ul', { class: 'bfui-results', 'aria-label': tr(t, 'ui.search.results') });
        const element = h('div', { class: 'bfui bfui-search' }, h('label', { for: inputId, class: 'bfui-field-label', text: o.label || tr(t, 'ui.search.label') }), input, status, results);
        let timer = null;
        let controller = null;

        async function run() {
            const term = input.value.trim();
            if (controller) {
                controller.abort();
                controller = null;
            }
            clear(results);
            if (term.length < 2) {
                status.textContent = '';
                return;
            }
            const mine = typeof AbortController === 'function' ? new AbortController() : null;
            controller = mine;
            status.textContent = tr(t, 'ui.search.searching');
            try {
                const answer = await o.api.getJSON('/Items', {
                    searchTerm: term, includeItemTypes: o.types.join(','), recursive: true, limit: o.limit, userId: o.userId,
                    enableImages: false, enableUserData: false
                }, mine ? { signal: mine.signal } : undefined);
                if (controller !== mine) {
                    return;
                }
                const items = (answer && answer.Items) || [];
                status.textContent = items.length ? tr(t, 'ui.search.count', { count: items.length }) : tr(t, 'ui.search.none');
                items.forEach(function (item) {
                    results.appendChild(h('li', null, h('button', {
                        type: 'button', class: 'bfui-result',
                        onclick: function () {
                            if (o.onPick) {
                                o.onPick(item);
                            }
                        }
                    }, h('span', { class: 'bfui-result-name', text: describeItem(item, t) }), h('span', { class: 'bfui-badge', text: tr(t, 'ui.itemType.' + item.Type) }))));
                });
            } catch (e) {
                if (e && e.name === 'AbortError') {
                    return;
                }
                status.textContent = tr(t, 'ui.search.error');
            }
        }

        input.addEventListener('input', function () {
            clearTimeout(timer);
            timer = setTimeout(run, 300);
        });
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                clearTimeout(timer);
                run();
            }
        });
        if (container) {
            container.appendChild(element);
        }
        return {
            element: element,
            focus: function () { input.focus(); },
            clear: function () { input.value = ''; clear(results); status.textContent = ''; }
        };
    }

    // ----- Confirm and toast -----

    /**
     * confirm(options) or confirm(message, options): an accessible modal; resolves true (OK) or false (cancel, Escape, backdrop).
     * options: { t, title, message, okText, cancelText, danger }. Without okText/cancelText the buttons read ui.ok / ui.cancel
     * through options.t or the default translator; with no translator at all they show ✓ and ✕.
     */
    function confirmDialog(options, extra) {
        const o = typeof options === 'string' ? Object.assign({ message: options }, extra || {}) : (options || {});
        const t = o.t || defaultT;
        const label = function (key, symbol) {
            const text = tr(t, key);
            return text === key ? symbol : text;
        };
        injectStyles();
        return new Promise(function (resolve) {
            const previous = document.activeElement;
            const titleId = uid('bfui-dt');
            const messageId = uid('bfui-dm');
            const cancel = h('button', { type: 'button', class: 'bfui-btn', text: o.cancelText || label('ui.cancel', '✕') });
            const ok = h('button', { type: 'button', class: 'bfui-btn ' + (o.danger ? 'bfui-btn-danger' : 'bfui-btn-primary'), text: o.okText || label('ui.ok', '✓') });
            const box = h('div', { class: 'bfui-dialog', role: o.danger ? 'alertdialog' : 'dialog', 'aria-modal': 'true', 'aria-labelledby': titleId, 'aria-describedby': messageId },
                h('h2', { class: 'bfui-dialog-title', id: titleId, text: o.title || '' }),
                h('p', { class: 'bfui-dialog-message', id: messageId, text: o.message || '' }),
                h('div', { class: 'bfui-dialog-actions' }, cancel, ok));
            const notice = t && t.machineTranslated ? h('a', { class: 'bfui-translation-notice', href: 'https://github.com/WizardBal/Babelfin/blob/main/CONTRIBUTING.md#translations', target: '_blank', rel: 'noopener', text: tr(t, 'common.machineTranslated') }) : null;
            if (notice) { box.appendChild(notice); }
            const overlay = h('div', { class: 'bfui bfui-overlay' }, box);
            function onKey(e) {
                const dialogs = document.querySelectorAll('.bfui-dialog');
                if (dialogs[dialogs.length - 1] !== box) { return; }
                const key = e.key || '', code = e.keyCode || 0;
                if (key === 'Escape' || key === 'Esc' || code === 27 || key === 'BrowserBack' || key === 'GoBack' || code === 461 || code === 10009) {
                    e.preventDefault();
                    e.stopPropagation();
                    done(false);
                } else if (e.key === 'Tab') {
                    const focusables = notice ? [cancel, ok, notice] : [cancel, ok];
                    const i = focusables.indexOf(document.activeElement);
                    e.preventDefault();
                    focusables[(i + (e.shiftKey ? -1 : 1) + focusables.length) % focusables.length].focus();
                }
            }
            function done(answer) {
                document.removeEventListener('keydown', onKey, true);
                overlay.remove();
                if (previous && typeof previous.focus === 'function') {
                    previous.focus();
                }
                resolve(answer);
            }
            cancel.addEventListener('click', function () { done(false); });
            ok.addEventListener('click', function () { done(true); });
            overlay.addEventListener('click', function (e) {
                if (e.target === overlay) {
                    done(false);
                }
            });
            document.addEventListener('keydown', onKey, true);
            document.body.appendChild(overlay);
            (o.danger ? cancel : ok).focus();
        });
    }

    /** toast(message, options): a short message in a polite live region. options: { kind: 'info' | 'error', timeout (ms) }. */
    function toast(message, options) {
        const o = options || {};
        injectStyles();
        let region = document.querySelector('.bfui-toasts');
        if (!region) {
            region = h('div', { class: 'bfui bfui-toasts', role: 'status', 'aria-live': 'polite' });
            document.body.appendChild(region);
        }
        const node = h('div', { class: 'bfui-toast' + (o.kind === 'error' ? ' bfui-toast-error' : ''), text: message });
        region.appendChild(node);
        setTimeout(function () { node.remove(); }, o.timeout || (o.kind === 'error' ? 8000 : 3500));
        return node;
    }

    // ----- Styles (scoped; the variables fall back to Jellyfin's theme) -----

    const CSS = [
        '.bfui{--bfx-text:var(--bf-text,var(--jf-palette-text-primary,#eee));--bfx-dim:var(--bf-dim,var(--jf-palette-text-secondary,rgba(255,255,255,.66)));',
        '--bfx-accent:var(--bf-accent,var(--jf-palette-primary-main,#00a4dc));--bfx-surface:var(--bf-surface,rgba(255,255,255,.04));--bfx-surface2:var(--bf-surface-2,rgba(255,255,255,.08));',
        '--bfx-border:var(--bf-border,rgba(255,255,255,.14));--bfx-danger:var(--bf-danger,#ff6b6b);--bfx-warn:var(--bf-warn,#f0b400);--bfx-radius:var(--bf-radius,8px);',
        '--bfx-panel:var(--bf-panel,var(--jf-palette-background-paper,#1c1c1c));color:var(--bfx-text);line-height:1.4;box-sizing:border-box}',
        '.bfui *,.bfui *::before,.bfui *::after{box-sizing:border-box}',
        '.bfui-help{color:var(--bfx-dim);font-size:.88em;margin-top:.25em}',
        '.bfui-field-error{color:var(--bfx-danger);font-size:.9em;margin-top:.25em}',
        '.bfui-lock{flex:0 0 auto;vertical-align:-.12em;color:var(--bfx-dim)}',
        '.bfui-row{display:flex;flex-wrap:wrap;align-items:center;gap:.35em 1em;padding:.55em 0;border-top:1px solid var(--bfx-border)}',
        '.bfui-row:first-child{border-top:0}.bfui-row-head{flex:1 1 14em;min-width:0;display:flex;align-items:center;gap:.1em}',
        '.bfui-row-label{min-width:0;display:inline-flex;gap:.35em;align-items:center}',
        '.bfui-row>.bfui-select{flex:0 1 auto;min-width:0;max-width:100%}',
        '.bfui-row>.bfui-help,.bfui-row>.bfui-field-error,.bfui-row-extra{flex:1 1 100%;margin-top:0}.bfui-row-extra:empty{display:none}',
        '.bfui-table-wrap{max-width:100%}.bfui-table{width:100%;border-collapse:collapse;table-layout:auto}',
        '.bfui-table th,.bfui-table td{text-align:start;vertical-align:top;padding:.45em .5em;border-top:1px solid var(--bfx-border)}',
        '.bfui-table thead th{border-top:0;font-size:.8em;text-transform:uppercase;letter-spacing:.05em;color:var(--bfx-dim);font-weight:600}',
        '.bfui-table-name{width:26%;font-weight:600;overflow-wrap:anywhere;padding-top:.85em!important}.bfui-table-all .bfui-table-name{color:var(--bfx-text)}',
        '.bfui-table .bfui-select{width:100%;min-width:0}.bfui-table-cell{min-width:9em}',
        '.bfui-table-help{margin-top:.35em}.bfui-table-wrap>.bfui-table+.bfui-table-help{margin-top:.6em}.bfui-picker-option{overflow-wrap:anywhere}',
        '@media (max-width:600px){.bfui-table,.bfui-table tbody,.bfui-table tr,.bfui-table th,.bfui-table td{display:block;width:100%}',
        '.bfui-table thead{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0)}.bfui-table tr{padding:.5em 0;border-top:1px solid var(--bfx-border)}',
        '.bfui-table tr:first-child{border-top:0}.bfui-table th,.bfui-table td{border-top:0;padding:.2em 0}.bfui-table-name{padding-top:.2em!important}.bfui-table-cell{min-width:0}',
        '.bfui-table td[data-label]::before{content:attr(data-label);display:block;font-size:.75em;text-transform:uppercase;letter-spacing:.06em;color:var(--bfx-dim);margin:.25em 0 .15em}}',
        '.bfui-has-error{box-shadow:inset 3px 0 0 var(--bfx-danger);padding-left:.6em}',
        '.bfui-select,.bfui-input{font:inherit;color:var(--bfx-text);background:var(--bfx-surface2);border:1px solid var(--bfx-border);border-radius:calc(var(--bfx-radius)*.75);padding:.45em .6em;min-height:2.4em;max-width:100%}',
        '.bfui-select option,.bfui-select optgroup{background:var(--bfx-panel);color:var(--bfx-text)}.bfui-select optgroup{color:var(--bfx-dim)}.bfui-input{width:100%}',
        '.bfui-btn{font:inherit;color:var(--bfx-text);background:var(--bfx-surface2);border:1px solid var(--bfx-border);border-radius:calc(var(--bfx-radius)*.75);padding:.45em .9em;min-height:2.4em;cursor:pointer}',
        '.bfui-btn:hover:not([disabled]){background:rgba(255,255,255,.14)}.bfui-btn-primary,.bfui-btn-primary:hover:not([disabled]){background:var(--bfx-accent);border-color:transparent;color:#fff}',
        '.bfui-btn-danger,.bfui-btn-danger:hover:not([disabled]){background:var(--bfx-danger);border-color:transparent;color:#111}.bfui-btn[disabled],.bfui-icon-btn[disabled]{opacity:.4;cursor:default}',
        '.bfui-icon-btn{font:inherit;color:var(--bfx-text);background:transparent;border:1px solid transparent;border-radius:6px;min-width:2.2em;min-height:2.2em;cursor:pointer}',
        '.bfui-icon-btn:hover:not([disabled]){background:var(--bfx-surface2)}',
        '.bfui :focus-visible{outline:2px solid var(--bfx-accent);outline-offset:2px}',
        '.bfui-list{list-style:none;margin:.2em 0;padding:0}',
        '.bfui-list-item{display:flex;align-items:center;gap:.4em;padding:.25em .4em;margin:.2em 0;background:var(--bfx-surface);border:1px solid var(--bfx-border);border-radius:var(--bfx-radius)}',
        '.bfui-list-content{flex:1 1 auto;min-width:0}.bfui-list-actions{display:flex;flex:0 0 auto}.bfui-list-add{margin-top:.35em}',
        '.bfui-empty{color:var(--bfx-dim);font-style:italic;margin:.25em 0}',
        '.bfui-lang{display:inline-flex;gap:.5em;align-items:center;flex-wrap:wrap}',
        '.bfui-badge{display:inline-block;font-size:.75em;padding:.1em .5em;border-radius:1em;background:var(--bfx-surface2);color:var(--bfx-dim);white-space:nowrap}',
        '.bfui-badge-warn{background:rgba(240,180,0,.18);color:var(--bfx-warn)}',
        '.bfui-picker{position:relative;margin-top:.3em}',
        '.bfui-picker-list{list-style:none;margin:.2em 0 0;padding:.2em;max-height:16em;overflow:auto;background:var(--bfx-panel);border:1px solid var(--bfx-border);border-radius:var(--bfx-radius)}',
        '.bfui-picker-group{padding:.4em .5em .1em;font-size:.78em;text-transform:uppercase;letter-spacing:.05em;color:var(--bfx-dim)}',
        '.bfui-picker-option{display:flex;flex-wrap:wrap;gap:.1em .6em;padding:.45em .6em;border-radius:6px;cursor:pointer}',
        '.bfui-picker-option:hover,.bfui-picker-option.bfui-active{background:var(--bfx-surface2)}.bfui-picker-option.bfui-active{box-shadow:inset 3px 0 0 var(--bfx-accent)}',
        '.bfui-picker-secondary{color:var(--bfx-dim);font-size:.85em}.bfui-chooser-wrap{display:inline-flex;flex-direction:column;max-width:100%}',
        '.bfui-chooser.bfui-missing{border-style:dashed;color:var(--bfx-warn)}.bfui-chooser-picker{min-width:min(22em,80vw)}',
        '.bfui-switch-label{display:inline-flex;align-items:center;gap:.55em;cursor:pointer;min-height:2.2em}',
        '.bfui-switch-line{display:flex;align-items:center;gap:.2em;flex-wrap:wrap}',
        '.bfui-info-wrap{display:inline-flex;align-items:center;flex:0 0 auto}',
        '.bfui-info{display:inline-flex;align-items:center;justify-content:center;width:28px;height:28px;margin:-4px 0;padding:0;border:0;border-radius:50%;background:transparent;color:var(--bfx-dim,rgba(255,255,255,.66));cursor:help;font:inherit}',
        '.bfui-info-mark{display:inline-flex;align-items:center;justify-content:center;width:17px;height:17px;border:1.5px solid currentColor;border-radius:50%;font:700 11px/1 Georgia,"Times New Roman",serif;font-style:italic}',
        '.bfui-info:hover,.bfui-info[aria-expanded=true]{color:var(--bfx-accent,#00a4dc)}.bfui-info:focus-visible{outline:2px solid var(--bfx-accent,#00a4dc);outline-offset:1px}',
        '.bfui-tip{position:fixed;z-index:2147483000;display:block;width:max-content;max-width:min(24rem,calc(100vw - 16px));padding:.6em .8em;border:1px solid rgba(255,255,255,.18);border-radius:8px;background:#262626;color:#eee;box-shadow:0 8px 24px rgba(0,0,0,.5);font-size:14px;font-weight:400;font-style:normal;line-height:1.45;text-align:start;text-transform:none;letter-spacing:normal;white-space:pre-line;pointer-events:none}',
        '.bfui-tip[hidden]{display:none!important}',
        '.bfui-switch-input{position:absolute;opacity:0;width:1px;height:1px}',
        '.bfui-switch-track{position:relative;flex:0 0 auto;width:2.4em;height:1.35em;border-radius:1em;background:rgba(255,255,255,.22);transition:background .15s}',
        '.bfui-switch-track::after{content:"";position:absolute;top:.15em;left:.15em;width:1.05em;height:1.05em;border-radius:50%;background:#fff;transition:transform .15s}',
        '.bfui-switch-input:checked+.bfui-switch-track{background:var(--bfx-accent)}.bfui-switch-input:checked+.bfui-switch-track::after{transform:translateX(1.05em)}',
        '.bfui-switch-input:focus-visible+.bfui-switch-track{outline:2px solid var(--bfx-accent);outline-offset:2px}.bfui-switch-input:disabled+.bfui-switch-track{opacity:.4}',
        '[dir=rtl] .bfui-switch-input:checked+.bfui-switch-track::after{transform:translateX(-1.05em)}[dir=rtl] .bfui-switch-track::after{left:auto;right:.15em}',
        '.bfui-results{list-style:none;padding:0;margin:.3em 0}',
        '.bfui-result{display:flex;width:100%;gap:.6em;justify-content:space-between;align-items:center;text-align:start;font:inherit;color:var(--bfx-text);background:var(--bfx-surface);border:1px solid var(--bfx-border);border-radius:var(--bfx-radius);padding:.55em .7em;margin:.2em 0;cursor:pointer}',
        '.bfui-result:hover{background:var(--bfx-surface2)}.bfui-result-name{min-width:0;overflow-wrap:anywhere}',
        '.bfui-overlay{position:fixed;inset:0;z-index:100000;background:rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;padding:1em}',
        '.bfui-dialog{background:var(--bfx-panel);color:var(--bfx-text);border:1px solid var(--bfx-border);border-radius:calc(var(--bfx-radius)*1.5);padding:1.2em 1.3em;max-width:30em;width:100%;box-shadow:0 20px 50px rgba(0,0,0,.6)}',
        '.bfui-dialog-title{margin:0 0 .5em;font-size:1.2em}.bfui-dialog-message{margin:0 0 1.2em;white-space:pre-line}.bfui-dialog-actions{display:flex;justify-content:flex-end;gap:.6em;flex-wrap:wrap}',
        '.bfui-toasts{position:fixed;z-index:100001;left:50%;bottom:1.2em;transform:translateX(-50%);display:flex;flex-direction:column;gap:.5em;align-items:center;pointer-events:none;width:max-content;max-width:calc(100vw - 2em)}',
        '.bfui-toast{pointer-events:auto;background:var(--bfx-panel);color:var(--bfx-text);border:1px solid var(--bfx-border);border-left:4px solid var(--bfx-accent);border-radius:var(--bfx-radius);padding:.6em 1em;box-shadow:0 8px 24px rgba(0,0,0,.5)}',
        '.bfui-toast-error{border-left-color:var(--bfx-danger)}',
        '.bfui-mode{display:flex;width:100%;max-width:36em;margin:0 0 .8em;padding:.25em;gap:.25em;background:var(--bfx-surface2);border:1px solid var(--bfx-border);border-radius:calc(var(--bfx-radius)*1.25)}',
        '.bfui-mode-option{flex:1 1 0;min-width:0;min-height:max(2.75em,44px);margin:0;padding:.45em .7em;font:inherit;font-weight:600;line-height:1.25;color:var(--bfx-dim);background:transparent;border:1px solid transparent;border-radius:var(--bfx-radius);cursor:pointer;text-align:center;overflow-wrap:anywhere}',
        '.bfui-mode-option:hover:not([aria-checked="true"]){background:rgba(255,255,255,.06);color:var(--bfx-text)}',
        '.bfui-mode-option[aria-checked="true"]{background:var(--bfx-accent);color:#fff;box-shadow:0 1px 3px rgba(0,0,0,.35)}',
        '.bfui .bfui-mode-option:focus-visible{outline:2px solid var(--bfx-text);outline-offset:2px}',
        '@media (forced-colors:active){.bfui-mode-option[aria-checked="true"]{border-color:Highlight;outline:2px solid Highlight}}',
        '.bfui-table-none{margin:.4em 0}',
        '.bfui-table-choice{max-width:100%}.bfui-table-choice .bfui-select{width:100%}',
        '.bfui-choice-line{display:flex;align-items:center;gap:.25em;min-width:0}.bfui-choice-line>.bfui-select{flex:1 1 auto;min-width:0}',
        '.bfui-genres{margin-top:1em}.bfui-genres h3{font-size:1em;margin:.5em 0}.bfui-genres-actions,.bfui-genres-picker{display:flex;flex-wrap:wrap;gap:.35em;align-items:center}.bfui-genres-picker{margin-top:.5em}',
        // The overrides table: plain text cells in the table's own look and stacking; the narrow-screen rules above apply to it too.
        '.bfui-overrides-name{width:32%;font-weight:600;overflow-wrap:anywhere}.bfui-overrides-title{display:block}',
        '.bfui-overrides-note{display:block;margin-top:.15em;font-size:.85em;font-weight:400;color:var(--bfx-dim)}',
        '.bfui-overrides-cell{overflow-wrap:anywhere}.bfui-overrides-unset{color:var(--bfx-dim)}',
        '.bfui-overrides-action{width:1%;white-space:nowrap;text-align:end!important}.bfui-overrides-actions{margin-top:.8em}',
        '@media (max-width:600px){.bfui-overrides-name,.bfui-overrides-action{width:100%}.bfui-overrides-action{text-align:start!important;white-space:normal;padding-top:.45em!important}}',
        '.bfui-disclosure{border-top:1px solid var(--bfx-border)}.bfui-disclosure-heading{margin:0;font-size:1em;font-weight:600}',
        '.bfui-disclosure-button{display:flex;align-items:center;gap:.6em;width:100%;min-height:max(2.75em,44px);margin:0;padding:.55em .25em;font:inherit;font-weight:600;color:var(--bfx-text);background:transparent;border:0;border-radius:6px;text-align:start;cursor:pointer}',
        '.bfui-disclosure-button:hover{background:var(--bfx-surface)}.bfui-disclosure-title{flex:1 1 auto;min-width:0;overflow-wrap:anywhere}',
        '.bfui-disclosure-icon{flex:0 0 auto;width:.5em;height:.5em;margin:0 .2em;border-right:2px solid var(--bfx-dim);border-bottom:2px solid var(--bfx-dim);transform:rotate(-45deg);transition:transform .15s}',
        '.bfui-disclosure-open>.bfui-disclosure-heading .bfui-disclosure-icon{transform:rotate(45deg)}[dir=rtl] .bfui-disclosure:not(.bfui-disclosure-open) .bfui-disclosure-icon{transform:rotate(135deg)}',
        '.bfui-disclosure-body{padding:0 .25em .8em}',
        '@media (prefers-reduced-motion:reduce){.bfui-disclosure-icon{transition:none}}',
        '.bfui-translation-notice{display:block;margin-top:.8em;font-size:.85em;color:inherit;text-decoration:underline;text-underline-offset:.15em}',
        '.bfui-sr{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}',
        '.bfui [hidden],.bfui[hidden]{display:none!important}'
    ].join('\n');

    /** Adds the shared styles once (every builder calls it). */
    function injectStyles() {
        if (typeof document === 'undefined' || document.getElementById('bfui-styles')) {
            return;
        }
        const style = document.createElement('style');
        style.id = 'bfui-styles';
        style.textContent = CSS;
        (document.head || document.documentElement).appendChild(style);
    }

    function styled(fn) {
        return function () {
            injectStyles();
            return fn.apply(this, arguments);
        };
    }

    global.BabelfinUI = Object.freeze({
        version: 2,
        ENUMS: ENUMS,
        FIELDS: FIELDS,
        api: api,
        problemErrors: problemErrors,
        fieldValue: fieldValue, setFieldValue: setFieldValue, withExpected: withExpected,
        expectedHeaders: expectedHeaders, problemMessage: problemMessage, conflictPanel: conflictPanel,
        h: h,
        clear: clear,
        uid: uid,
        setTranslator: setTranslator,
        isFieldSet: isFieldSet,
        clearField: clearField,
        setFields: setFields,
        fieldForPath: fieldForPath,
        languageName: languageName,
        languagePicker: styled(languagePicker),
        handleLanguagePickerKey: handleLanguagePickerKey,
        languageChooser: styled(languageChooser),
        languageList: styled(languageList),
        orderedList: styled(orderedList),
        fieldSelect: styled(fieldSelect),
        libraryTable: styled(libraryTable),
        tableChoice: styled(tableChoice),
        genreRows: styled(genreRows),
        isGenreRow: isGenreRow,
        mergeGenreRows: mergeGenreRows,
        choiceStatus: styled(choiceStatus),
        overridesTable: styled(overridesTable),
        automaticPerLibrary: automaticPerLibrary,
        tableLanguages: tableLanguages,
        tableAudioValue: tableAudioValue,
        tableSubtitleValue: tableSubtitleValue,
        writeTableAudio: writeTableAudio,
        writeTableSubtitles: writeTableSubtitles,
        disclosure: styled(disclosure),
        reveal: reveal,
        switchControl: styled(switchControl),
        infoTip: styled(infoTip),
        itemSearch: styled(itemSearch),
        describeItem: describeItem,
        confirm: confirmDialog,
        toast: toast,
        injectStyles: injectStyles
    });
})(typeof window !== 'undefined' ? window : this);
