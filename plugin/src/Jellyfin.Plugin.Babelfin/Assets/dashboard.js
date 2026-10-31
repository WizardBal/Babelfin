/* Babelfin admin page (Dashboard > Plugins > Babelfin). Loaded by Configuration/configPage.html inside Jellyfin's
   dashboard, where ApiClient and Dashboard exist. window.BabelfinUI (Assets/ui.js) supplies the language lists and pickers, the "Audio and
   subtitles" table with its two modes (Defaults for everyone, a person's editor) and its value readers, the collapsible groups, the item
   search, dialogs and toasts. Every string comes from Assets/i18n-admin/<lang>.json; the ui.js pieces (and the few shared words such as
   "My languages") also read Assets/i18n/<lang>.json.
   Visible: the header, Defaults for everyone and People (Name, Audio, Subtitles, Edit, from GET Admin/Users). Collapsed: Advanced (Apps,
   New users, Track recognition, then "Restore defaults"), Troubleshooting (Check a title, Activity) and Back up and restore, each group
   collapsible on its own. Dropdowns outside the table list plain options: the stored value is selected, else the shipped value from
   GET Admin/Defaults; picking the shipped value clears a field that may be left unset.
   Server settings are saved by one Save bar: it reads Babelfin/Admin/Settings fresh, applies only the fields changed on this page and PUTs
   the result with explicit field intent, so the server merges the edits under its lock and keeps hidden settings. A person's settings are
   saved from their editor through Babelfin/Admin/Users/{id}/Settings?keepOverrides=true. The editor also lists the person's show and movie
   overrides (GET Babelfin/Admin/Users/{id}/Overrides) with a Remove per row and Remove all, which act at once.
   Nothing is saved on load. */
(function () {
    'use strict';

    const CHECK_POLL_MS = 1000;
    const CHECK_ROWS = 50;
    const CHECK_TYPES = ['Series', 'Season', 'Episode', 'Movie'];
    const ACTIVITY_PAGE = 50;
    const ACTIVITY_MAX = 5000;
    const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';
    const PICK = '__pick';
    const FOLD_KEY = 'babelfin.admin.open.';
    const KEYWORD_LISTS = ['PartialWords', 'SdhWords', 'FullWords', 'CommentaryWords', 'AudioDescriptionWords'];
    const APP_MODES = ['Trust', 'ReplaceOnAutoplay', 'ReplaceUnlessManual'];
    // The engine's built-in rule for audio tracks without a language, used when the shipped settings leave it unset (they do).
    const BUILTIN_UNLABELLED = 'OriginalWhenOnlyTrack';

    // Enum names in declaration order (a number from an older server maps by position).
    const ENUMS = {
        UnlabelledAudioRule: ['Unknown', 'OriginalLanguage', 'LibraryUsualOriginalLanguage', 'SpecificLanguage', 'OriginalWhenOnlyTrack'],
        AppModeKind: ['Trust', 'ReplaceOnAutoplay', 'ReplaceUnlessManual'],
        CorrectAfterStartMode: ['Auto', 'On', 'Off']
    };

    let S = null; // the shown page's state

    /* ---------- small helpers ---------- */

    function t(key, params) {
        return S && S.t ? S.t(key, params) : key;
    }

    function ui() {
        return window.BabelfinUI || null;
    }

    function identityT() {
        const fn = function (key) { return key; };
        fn.has = function () { return false; };
        return fn;
    }

    /** Combines translation functions: the first that has a key wins (the admin strings, then the person's page strings ui.js asks for). */
    function combineT(first, second) {
        const list = [first, second].filter(function (f) { return typeof f === 'function'; });
        const fn = function (key, params) {
            for (let i = 0; i < list.length; i++) {
                if (typeof list[i].has !== 'function' || list[i].has(key)) {
                    return list[i](key, params);
                }
            }
            return key;
        };
        fn.has = function (key) { return list.some(function (f) { return typeof f.has === 'function' && f.has(key); }); };
        fn.machineTranslated = list.some(function (f) { return f.machineTranslated; });
        fn.language = first && first.language;
        return fn;
    }

    /**
     * Creates an element, customised (is="emby-button") when asked. jellyfin-web replaces document.createElement with an old
     * web-components polyfill that only accepts the extension name as a plain string (the options form throws inside it), while
     * browsers without the polyfill want the options form; try both, then fall back to a plain element with the is attribute.
     */
    function createElement(tag, is) {
        if (!is) {
            return document.createElement(tag);
        }
        try {
            return document.createElement(tag, is);
        } catch (e) {
            // not the polyfill's signature
        }
        try {
            return document.createElement(tag, { is: is });
        } catch (e) {
            // neither form: a plain element below
        }
        const plain = document.createElement(tag);
        plain.setAttribute('is', is);
        return plain;
    }

    function h(tag, props, children) {
        const node = createElement(tag, props && props.is);
        if (props) {
            Object.keys(props).forEach(function (k) {
                const v = props[k];
                if (v === null || v === undefined || v === false || k === 'is') {
                    return;
                }
                if (k === 'class') {
                    // Added, not assigned: a customised element (emby-button) gets its own class when it is created, and assigning
                    // className would remove it, and with it Jellyfin's button styling.
                    String(v).split(/\s+/).filter(Boolean).forEach(function (c) { node.classList.add(c); });
                } else if (k === 'text') {
                    node.textContent = String(v);
                } else if (k.indexOf('on') === 0 && typeof v === 'function') {
                    node.addEventListener(k.slice(2), v);
                } else if (k === 'value' || k === 'checked' || k === 'selected' || k === 'disabled' || k === 'multiple') {
                    node[k] = v;
                } else {
                    node.setAttribute(k, v === true ? '' : String(v));
                }
            });
        }
        append(node, children);
        if (props && props.is) {
            node.setAttribute('is', props.is);
        }
        return node;
    }

    function append(node, children) {
        if (children === null || children === undefined) {
            return node;
        }
        (Array.isArray(children) ? children : [children]).forEach(function (c) {
            if (c === null || c === undefined || c === false) {
                return;
            }
            if (Array.isArray(c)) {
                append(node, c);
                return;
            }
            node.appendChild(typeof c === 'string' || typeof c === 'number' ? document.createTextNode(String(c)) : c);
        });
        return node;
    }

    function clear(node) {
        while (node && node.firstChild) {
            node.removeChild(node.firstChild);
        }
        return node;
    }

    let idCounter = 0;
    function uid() {
        idCounter += 1;
        return 'bf-' + idCounter;
    }

    function q(selector) {
        return S.page.querySelector(selector);
    }

    function clone(value) {
        return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
    }

    /** A copy without null, undefined or empty-object members, keys sorted: two settings mean the same when these match. */
    function normalized(value) {
        if (Array.isArray(value)) {
            return value.map(normalized);
        }
        if (value && typeof value === 'object') {
            const out = {};
            Object.keys(value).sort().forEach(function (k) {
                const v = normalized(value[k]);
                if (v !== null && v !== undefined && !(typeof v === 'object' && !Array.isArray(v) && Object.keys(v).length === 0)) {
                    out[k] = v;
                }
            });
            return out;
        }
        return value === undefined ? null : value;
    }

    function sameValue(a, b) {
        return JSON.stringify(normalized(a)) === JSON.stringify(normalized(b));
    }

    /** Field intent names settings leaves and stable-id library leaves; the other arrays are atomic. */
    function editedFields(before, after) {
        const fields = [];
        const camel = function (key) { return key.charAt(0).toLowerCase() + key.slice(1); };
        const keys = function (a, b) { return Object.keys(Object.assign({}, a, b)); };
        keys(before, after).forEach(function (key) {
            const was = before && before[key], now = after && after[key];
            if (sameValue(was, now)) { return; }
            if (key === 'NewUserTemplate') {
                // This editor changes only My languages; null clears that leaf, never the hidden template fields.
                if (!sameValue(was && was.Everyday && was.Everyday.Understood, now && now.Everyday && now.Everyday.Understood)) {
                    fields.push('newUserTemplate.everyday.understood');
                }
            } else if (key === 'Libraries') {
                const rows = function (list, baseline) {
                    const byId = {};
                    (list || []).forEach(function (row, index) {
                        const id = row && normId(row.LibraryId);
                        const duplicate = id && (list || []).filter(function (entry) { return entry && normId(entry.LibraryId) === id; }).length > 1;
                        if (!row || typeof row.LibraryId !== 'string' || !/^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.test(row.LibraryId) || /^0+$/.test(id) || duplicate) {
                            if (!baseline || (baseline || []).some(function (stored) { return sameValue(stored, row); })) { return; }
                            throw userError(t('common.invalidLibrary'));
                        }
                        byId[id] = row;
                    });
                    return byId;
                };
                const previous = rows(was), next = rows(now, was || []);
                keys(previous, next).forEach(function (id) {
                    if (!/^[0-9a-f]{32}$/.test(id) || /^0+$/.test(id)) { throw userError(t('common.invalidLibrary')); }
                    const oldRow = previous[id] || {}, row = next[id] || {};
                    keys(oldRow.Settings, row.Settings).forEach(function (leaf) {
                        if (!sameValue(oldRow.Settings && oldRow.Settings[leaf], row.Settings && row.Settings[leaf])) {
                            fields.push('libraries.' + id + '.settings.' + camel(leaf));
                        }
                    });
                    if (!sameValue(oldRow.OriginalAudio || 'Auto', row.OriginalAudio || 'Auto')) {
                        fields.push('libraries.' + id + '.originalAudio');
                    }
                });
            } else if (['Everyday', 'Locks', 'Seed', 'Keywords', 'Starter'].indexOf(key) >= 0) {
                keys(was, now).forEach(function (leaf) {
                    if (!sameValue(was && was[leaf], now && now[leaf])) { fields.push(camel(key) + '.' + camel(leaf)); }
                });
            } else {
                fields.push(camel(key));
            }
        });
        return fields;
    }

    function applyEditedFields(target, source, fields) {
        fields.forEach(function (field) {
            const parts = field.split('.');
            let to = target, from = source;
            if (parts[0] === 'libraries') {
                const id = parts[1];
                from = (source.Libraries || []).find(function (row) { return normId(row.LibraryId) === id; }) || {};
                target.Libraries = target.Libraries || [];
                to = target.Libraries.find(function (row) { return normId(row.LibraryId) === id; });
                if (!to) { to = { LibraryId: id, Settings: {} }; target.Libraries.push(to); }
                parts.splice(0, 2);
            }
            parts.forEach(function (part, index) {
                const key = part.charAt(0).toUpperCase() + part.slice(1);
                if (index === parts.length - 1) {
                    if (from && Object.prototype.hasOwnProperty.call(from, key)) { to[key] = clone(from[key]); }
                    else { delete to[key]; }
                } else {
                    to[key] = to[key] || {};
                    to = to[key]; from = from && from[key];
                }
            });
        });
    }

    function replaceDocument(target, source) {
        Object.keys(target).forEach(function (key) { delete target[key]; });
        Object.assign(target, clone(source));
    }

    // Genre controls close over their rule object. Keep surviving objects, even when the response assigns a new row its id.
    function retainGenreRules(previous, incoming) {
        const UI = ui(), before = previous || [], used = new Set();
        if (!UI || typeof UI.isGenreRow !== 'function') { return { rows: incoming || [], redraw: true }; }
        const genre = function (rule) { return UI.isGenreRow(rule) ? rule.Conditions[0].Values[0].trim().toLowerCase() : null; };
        const visible = before.filter(UI.isGenreRow);
        const displayed = function (list) { return list.map(function (rule) { return { name: rule.Conditions[0].Values[0], fields: tableFields(rule.Settings) }; }); };
        const priorDisplay = displayed(visible);
        const rows = (incoming || []).map(function (rule) {
            let match = before.find(function (old) { return !used.has(old) && old.Id && rule.Id && String(old.Id).toLowerCase() === String(rule.Id).toLowerCase(); });
            if (!match && genre(rule)) {
                const candidates = before.filter(function (old) { return !used.has(old) && !old.Id && genre(old) === genre(rule); });
                if (candidates.length === 1) { match = candidates[0]; }
            }
            if (!match) { return rule; }
            used.add(match);
            replaceDocument(match, rule);
            return match;
        });
        const nextVisible = rows.filter(UI.isGenreRow);
        const redraw = visible.length !== nextVisible.length || visible.some(function (rule, index) { return rule !== nextVisible[index]; })
            || !sameValue(priorDisplay, displayed(nextVisible));
        return { rows: rows, redraw: redraw };
    }

    function normId(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function sameId(a, b) {
        return normId(a) === normId(b) && normId(a).length > 0;
    }

    function enumName(group, value) {
        if (typeof value === 'number') {
            return ENUMS[group][value] || null;
        }
        return value === undefined || value === '' ? null : value;
    }

    function fmtDate(value) {
        if (!value) {
            return '';
        }
        const d = new Date(value);
        if (isNaN(d.getTime())) {
            return String(value);
        }
        try {
            return d.toLocaleString(S.lang || undefined);
        } catch (e) {
            return d.toLocaleString();
        }
    }

    function langName(code) {
        if (!code) {
            return t('common.unknownLanguage');
        }
        const UI = ui();
        if (UI && typeof UI.languageName === 'function') {
            try {
                const name = UI.languageName(code, { languages: S.languageList || [], uiLanguage: S.lang });
                if (name && name.name) {
                    return name.name;
                }
            } catch (e) {
                // the code itself below
            }
        }
        return String(code);
    }

    function languagesText(codes) {
        return Array.isArray(codes) && codes.length ? codes.map(langName).join(', ') : '—';
    }

    function langOf(value) {
        return typeof value === 'string' && value.indexOf('lang:') === 0 ? value.slice(5) : null;
    }

    function itemLabel(item) {
        if (!item) {
            return '';
        }
        let label = item.Name || '';
        if (item.Type === 'Episode' && item.SeriesName) {
            const number = item.ParentIndexNumber !== undefined && item.ParentIndexNumber !== null && item.IndexNumber !== undefined && item.IndexNumber !== null
                ? ' S' + String(item.ParentIndexNumber).padStart(2, '0') + 'E' + String(item.IndexNumber).padStart(2, '0')
                : '';
            label = item.SeriesName + number + ' · ' + label;
        } else if (item.Type === 'Season' && item.SeriesName) {
            label = item.SeriesName + ' · ' + label;
        }
        if (item.ProductionYear && item.Type !== 'Episode' && item.Type !== 'Season') {
            label += ' (' + item.ProductionYear + ')';
        }
        return label;
    }

    function toast(message, kind) {
        const UI = ui();
        if (UI && typeof UI.toast === 'function') {
            try {
                UI.toast(message, { kind: kind || 'info' });
                return;
            } catch (e) {
                // the page's own toast below
            }
        }
        const node = h('div', { class: 'bf-toast' + (kind === 'error' ? ' bf-toast-error' : ''), role: 'status', text: message });
        S.page.appendChild(node);
        setTimeout(function () {
            if (node.parentNode) {
                node.parentNode.removeChild(node);
            }
        }, 3500);
    }

    function confirmAsync(message, options) {
        const o = options || {};
        const UI = ui();
        if (UI && typeof UI.confirm === 'function') {
            try {
                const state = S;
                const previousDialogs = Array.from(document.querySelectorAll('.bfui-dialog'));
                const r = UI.confirm({
                    t: S.uiT, title: o.title || t('common.confirmTitle'), message: message, okText: o.okText || t('common.ok'), cancelText: t('common.cancel'), danger: !!o.danger
                });
                if (r && typeof r.then === 'function') {
                    const dialog = Array.from(document.querySelectorAll('.bfui-dialog')).find(function (node) { return previousDialogs.indexOf(node) < 0; });
                    const closeOnHide = function () {
                        if (dialog && dialog.isConnected) { const cancel = dialog.querySelector('button'); if (cancel) { cancel.click(); } }
                    };
                    if (state.cleanups) { state.cleanups.push(closeOnHide); }
                    const answered = function (value) {
                        const index = state.cleanups ? state.cleanups.indexOf(closeOnHide) : -1;
                        if (index >= 0) { state.cleanups.splice(index, 1); }
                        return !!value;
                    };
                    return r.then(answered, function () { return answered(false); });
                }
            } catch (e) {
                // the browser's dialog below
            }
        }
        return Promise.resolve(window.confirm(message));
    }

    function saveBlob(blob, name) {
        const url = URL.createObjectURL(blob);
        const a = h('a', { href: url, download: name || 'babelfin.json', class: 'hide' });
        document.body.appendChild(a);
        a.click();
        setTimeout(function () {
            URL.revokeObjectURL(url);
            if (a.parentNode) {
                a.parentNode.removeChild(a);
            }
        }, 1000);
    }

    function busy(button, on) {
        if (button) {
            button.disabled = !!on;
            button.classList.toggle('bf-busy', !!on);
        }
    }

    function readStore(key) {
        try {
            return window.localStorage.getItem(key);
        } catch (e) {
            return null;
        }
    }

    function writeStore(key, value) {
        try {
            window.localStorage.setItem(key, value);
        } catch (e) {
            // storage blocked: the open section just isn't remembered
        }
    }

    /* ---------- API ---------- */

    function authHeaders(json) {
        const headers = {};
        try {
            if (typeof ApiClient.setRequestHeaders === 'function') {
                ApiClient.setRequestHeaders(headers);
            }
        } catch (e) {
            // use the token below
        }
        if (!headers.Authorization && !headers['X-Emby-Authorization']) {
            const token = typeof ApiClient.accessToken === 'function' ? ApiClient.accessToken() : null;
            if (token) {
                headers.Authorization = 'MediaBrowser Token="' + token + '"';
            }
        }
        if (json) {
            headers['Content-Type'] = 'application/json';
        }
        headers.Accept = 'application/json, text/csv;q=0.9, */*;q=0.5';
        return headers;
    }

    function cleanQuery(query) {
        if (!query) {
            return undefined;
        }
        const out = {};
        Object.keys(query).forEach(function (k) {
            if (query[k] !== null && query[k] !== undefined && query[k] !== '') {
                out[k] = query[k];
            }
        });
        return out;
    }

    function fileNameOf(response, fallback) {
        const header = (response.headers && typeof response.headers.get === 'function' && response.headers.get('Content-Disposition')) || '';
        const star = /filename\*=UTF-8''([^;]+)/i.exec(header);
        if (star) {
            try {
                return decodeURIComponent(star[1]);
            } catch (e) {
                // the plain name below
            }
        }
        const plain = /filename="?([^";]+)"?/i.exec(header);
        return plain ? plain[1] : fallback;
    }

    function request(method, path, options) {
        options = options || {};
        const hasBody = options.body !== undefined;
        const url = ApiClient.getUrl(path, cleanQuery(options.query));
        return fetch(url, {
            method: method,
            headers: Object.assign(authHeaders(hasBody), options.headers),
            body: hasBody ? JSON.stringify(options.body) : undefined,
            credentials: 'same-origin'
        }).then(function (response) {
            if (options.blob && response.ok) {
                return response.blob().then(function (blob) {
                    return { blob: blob, name: fileNameOf(response, options.fileName) };
                });
            }
            return response.text().then(function (text) {
                let data = null;
                if (text) {
                    try {
                        data = JSON.parse(text);
                    } catch (e) {
                        data = text;
                    }
                }
                if (!response.ok) {
                    const err = new Error('HTTP ' + response.status);
                    err.status = response.status;
                    err.problem = data && typeof data === 'object' ? data : null;
                    throw err;
                }
                return data;
            });
        });
    }

    const api = {
        get: function (path, query) { return request('GET', path, { query: query }); },
        put: function (path, body, query, headers) { return request('PUT', path, { body: body, query: query, headers: headers }); },
        post: function (path, body, query) { return request('POST', path, { body: body, query: query }); },
        del: function (path, query) { return request('DELETE', path, { query: query }); },
        blob: function (path, query, fileName) { return request('GET', path, { query: query, blob: true, fileName: fileName }); }
    };

    function userError(message, path) {
        const err = new Error(message);
        err.userMessage = message;
        err.fieldPath = path || null;
        return err;
    }

    function problemOf(err) {
        const out = { message: '', errors: [], itemIds: [] };
        const p = err && err.problem;
        if (p && typeof p === 'object') {
            out.message = ui() && ui().problemMessage ? ui().problemMessage(t, p) : p.Message || p.message || p.title || p.Title || '';
            const errors = p.Errors || p.errors;
            if (Array.isArray(errors)) {
                errors.forEach(function (e) {
                    out.errors.push({ path: (e && (e.Path || e.path)) || '', message: (e && (e.Message || e.message)) || '' });
                });
            } else if (errors && typeof errors === 'object') {
                Object.keys(errors).forEach(function (k) {
                    [].concat(errors[k]).forEach(function (m) {
                        out.errors.push({ path: k.replace(/^\$\.?/, ''), message: String(m) });
                    });
                });
            }
            out.itemIds = p.ItemIds || p.itemIds || [];
        }
        if (err && err.fieldPath && !out.errors.length) {
            out.errors.push({ path: err.fieldPath, message: err.userMessage || '' });
        }
        if (!out.message) {
            if (err && err.userMessage) {
                out.message = err.userMessage;
            } else if (err && err.status === 404) {
                out.message = t('common.notAvailable');
            } else if (err && err.status) {
                out.message = t('common.httpError', { status: err.status });
            } else {
                out.message = t('common.networkError');
            }
        }
        return out;
    }

    function errorBox() {
        return h('div', { class: 'bf-alert bf-alert-error hide', role: 'alert' });
    }

    function fieldErrorBox() {
        return h('div', { class: 'bf-field-error hide', role: 'alert' });
    }

    function clearProblem(box) {
        if (box) {
            clear(box);
            box.classList.add('hide');
        }
    }

    function isConnected(node) {
        if (!node) {
            return false;
        }
        if (typeof node.isConnected === 'boolean') {
            return node.isConnected;
        }
        let n = node;
        while (n && n !== document) {
            n = n.parentNode;
        }
        return n === document;
    }

    /** The node that claimed the longest path the error's path starts with ("libraries[2]" for "libraries[2].settings.audioChain"). */
    function claimFor(claims, path) {
        const p = String(path || '').toLowerCase();
        let best = null;
        (claims || []).forEach(function (c) {
            const next = p.charAt(c.path.length);
            if (p.indexOf(c.path) === 0 && (next === '' || next === '.' || next === '[') && (!best || c.path.length > best.path.length) && isConnected(c.node)) {
                best = c;
            }
        });
        return best ? best.node : null;
    }

    function addFieldError(node, message) {
        let box = node.querySelector('.bf-field-error');
        if (!box) {
            box = fieldErrorBox();
            node.appendChild(box);
        }
        box.textContent = box.textContent ? box.textContent + ' · ' + message : message;
        box.classList.remove('hide');
        const UI = ui();
        if (UI && typeof UI.reveal === 'function') {
            UI.reveal(node);
        }
    }

    function clearFieldErrors(root) {
        if (!root) {
            return;
        }
        root.querySelectorAll('.bf-field-error').forEach(function (box) {
            box.textContent = '';
            box.classList.add('hide');
        });
    }

    /** Shows a failure: each validation error next to the control that claimed its path, the rest (or the summary) in the box. */
    function showErrors(err, box, claims) {
        const p = problemOf(err);
        const rest = [];
        p.errors.forEach(function (e) {
            const node = claimFor(claims, e.path);
            if (node) {
                addFieldError(node, e.message);
            } else {
                rest.push(e);
            }
        });
        if (!box) {
            return;
        }
        clear(box);
        if (!p.errors.length || rest.length) {
            append(box, h('div', { class: 'bf-problem-title', text: p.message }));
        }
        if (rest.length) {
            append(box, h('ul', { class: 'bf-problem-list' }, rest.map(function (e) { return h('li', { text: e.message }); })));
        }
        if (p.itemIds && p.itemIds.length) {
            append(box, h('div', { class: 'bf-problem-ids', text: t('common.itemIds', { ids: p.itemIds.join(', ') }) }));
        }
        box.classList.toggle('hide', !box.firstChild);
    }

    function showLoadError(container, err) {
        const box = errorBox();
        append(clear(container), box);
        showErrors(err, box, []);
    }

    function loading(container) {
        append(clear(container), h('p', { class: 'bf-hint', text: t('common.loading') }));
    }

    /* ---------- shared data (cached while the page is shown) ---------- */

    function current(state, revision) {
        return S === state && state.shown !== false && (revision === undefined || state.revision === revision);
    }

    function cached(key, load) {
        const state = S, cache = state.cache;
        if (!cache[key]) {
            const pending = load().then(null, function (err) {
                // A late rejection must not evict a newer request for the same key.
                if (cache[key] === pending) { delete cache[key]; }
                throw err;
            });
            cache[key] = pending;
        }
        return cache[key];
    }

    function invalidate(key) {
        delete S.cache[key];
    }

    function users() {
        const state = S, cache = state.cache;
        return cached('users', function () {
            return api.get('Babelfin/Admin/Users').then(function (list) {
                if (current(state) && state.cache === cache) { state.userList = list || []; }
                return list || [];
            });
        });
    }

    function libraries() {
        return cached('libraries', function () {
            return api.get('Babelfin/Admin/Libraries').then(function (list) { return list || []; });
        });
    }

    function languages() {
        const state = S, cache = state.cache;
        return cached('languages', function () {
            return api.get('Babelfin/Languages').then(function (list) {
                const result = Array.isArray(list) ? list : [];
                if (current(state) && state.cache === cache) { state.languageList = result; }
                return result;
            }, function () {
                if (current(state) && state.cache === cache) { state.languageList = []; }
                return [];
            });
        });
    }

    /**
     * The shipped settings (GET Admin/Defaults, a fresh configuration): what a dropdown shows when nothing is stored, and what "Restore
     * defaults" puts back. Fields the server leaves out of the JSON count as null.
     */
    function shippedSettings() {
        const state = S, cache = state.cache;
        return cached('shipped', function () {
            return api.get('Babelfin/Admin/Defaults').then(function (shipped) {
                const result = shipped && typeof shipped === 'object' ? shipped : null;
                if (current(state) && state.cache === cache) { state.shipped = result; }
                return result;
            });
        });
    }

    /**
     * "Audio tracks without a language" as a dropdown value from a settings layer: the rule, 'lang:<code>' for a specific language, '' when
     * nothing is stored (a specific language without its language counts as nothing).
     */
    function unlabelledValue(layer) {
        const ev = layer || {};
        const rule = enumName('UnlabelledAudioRule', ev.UnlabelledAudio);
        if (!rule) {
            return '';
        }
        if (rule === 'SpecificLanguage') {
            return ev.UnlabelledAudioLanguage ? 'lang:' + ev.UnlabelledAudioLanguage : '';
        }
        return rule;
    }

    /** What the shipped settings store for audio tracks without a language ('' = nothing: the engine's built-in rule applies). */
    function shippedUnlabelledStored() {
        return unlabelledValue(S.shipped && S.shipped.Everyday);
    }

    /** The rule that applies with the shipped settings, as a dropdown value. */
    function shippedUnlabelled() {
        return shippedUnlabelledStored() || BUILTIN_UNLABELLED;
    }

    function userPath(userId) {
        return 'Babelfin/Admin/Users/' + encodeURIComponent(userId) + '/Settings';
    }

    /**
     * The person's show and movie overrides: the list, or with an override the one DELETE removes (add ?scope=). An override
     * whose id isn't 32 hex digits is refused (it throws). Removing every override uses the separate ".../Overrides/All" route.
     */
    function overridesPath(userId, row) {
        const base = 'Babelfin/Admin/Users/' + encodeURIComponent(userId) + '/Overrides';
        if (!row) {
            return base;
        }
        const id = normId(row.ItemId);
        if (!/^[0-9a-f]{32}$/.test(id)) {
            throw new Error('the override has no valid id');
        }
        return base + '/' + id;
    }

    /** The person's document from an Admin/Users/{id}/Settings answer: the document itself, or the Settings of { Settings, Defaults, Libraries }. */
    function personDoc(answer) {
        if (answer && answer.Settings && typeof answer.Settings === 'object' && !Array.isArray(answer.Settings)) {
            return answer.Settings;
        }
        return answer;
    }

    function isLibraryList(list) {
        return Array.isArray(list) && list.length > 0 && list.every(function (l) { return l && l.Id && l.Name !== undefined && l.Settings === undefined; });
    }

    function userName(id) {
        const u = (S.userList || []).find(function (x) { return sameId(x.UserId, id); });
        return u ? u.Name : '';
    }

    /** The library ids a person can see (their Jellyfin views), or null when Jellyfin doesn't say. */
    function userViews(userId) {
        return ApiClient.getJSON(ApiClient.getUrl('UserViews', { userId: userId })).then(function (res) {
            return ((res && res.Items) || []).map(function (i) { return normId(i.Id); });
        }, function () {
            return null;
        });
    }

    function ensureDatalist() {
        if (S.page.querySelector('#bfLangList')) {
            return;
        }
        const list = h('datalist', { id: 'bfLangList' });
        (S.languageList || []).forEach(function (l) {
            const code = l.Code || l.code;
            append(list, h('option', { value: code, label: langName(code) }));
        });
        S.page.appendChild(list);
    }

    /* ---------- pending changes to the server settings, and the Save bar ---------- */

    /** Records a change (or forgets it when the value is back where it started); apply(fresh) writes it into a freshly read configuration. */
    function track(key, initial, current, apply) {
        if (!S.expectedBase && sameValue(initial, current)) {
            delete S.pending[key];
        } else {
            S.pending[key] = apply;
        }
        paintSaveBar();
    }

    function paintSaveBar() {
        const bar = q('#bfSaveBar');
        if (bar) {
            bar.classList.toggle('hide', Object.keys(S.pending).length === 0);
        }
    }

    function claim(path, node) {
        S.claims.push({ path: String(path).toLowerCase(), node: node });
    }

    /** Reads settings fresh and changes only explicit edits, preserving hidden and unknown fields. */
    function saveStructured(mutate, box, btn, force) {
        const state = S, revision = state.revision;
        const original = clone(state.expectedBase || state.config);
        clearProblem(box);
        state.saving = true;
        const operation = state.saveOperation = {};
        // Freeze the draft while its callbacks are being saved. Failure restores the same controls and edits.
        const controls = Array.from(state.page.querySelectorAll('input, select, textarea, button')).filter(function (node) { return !node.disabled; });
        controls.forEach(function (node) { node.disabled = true; });
        busy(btn, true);
        const restoreControls = function () { controls.forEach(function (node) { node.disabled = false; }); busy(btn, false); };
        function finish() {
            if (state.saveOperation !== operation) { return; }
            state.saveOperation = null;
            state.saving = false;
            restoreControls();
            const index = state.cleanups.indexOf(finish);
            if (index >= 0) { state.cleanups.splice(index, 1); }
        }
        operation.finish = finish;
        state.cleanups.push(finish);
        return Promise.resolve().then(function () { return api.get('Babelfin/Admin/Settings'); }).then(function (fresh) {
            if (!current(state, revision)) { return; }
            const changed = clone(fresh);
            mutate(changed);
            const intended = clone(original);
            mutate(intended);
            const fields = editedFields(original, intended);
            return fields.length ? api.put('Babelfin/Admin/Settings', force === true ? changed : ui().withExpected(changed, original, fields), { fields: fields.join(',') }) : fresh;
        }).then(function (saved) {
            if (!current(state, revision)) { return; }
            state.config = saved;
            state.expectedBase = null;
            finish();
            toast(t('common.saved'));
            return saved;
        }, function (err) {
            if (current(state, revision)) {
                finish();
                if (err.status === 409 && err.problem && (err.problem.Code || err.problem.code) === 'field-conflict') {
                    serverConflict(err.problem, box);
                } else { showErrors(err, box, state.claims); }
            }
            throw err;
        }).finally(finish);
    }

    function conflictActor(problem) {
        const actor = String(problem.LastChangedBy || problem.lastChangedBy || '').replace(/^admin:/, '');
        const person = (S.userList || []).find(function (person) { return sameId(person.UserId, actor); });
        return person && person.Name;
    }

    function conflictLabel(path) {
        if (/understood$/.test(path)) { return uiWord('languages.title'); }
        if (/reads$|subtitle/i.test(path)) { return t('libraries.subtitles'); }
        if (/audio/i.test(path)) { return t('libraries.audio'); }
        if (path === 'enabled') { return t('people.enabled'); }
        return t('conflict.settings');
    }

    function serverConflict(problem, box) {
        const state = S, revision = state.revision, UI = ui();
        const paths = problem.ConflictingPaths || problem.conflictingPaths || [];
        const panel = UI.conflictPanel({ t: t, problem: problem, actor: conflictActor(problem), label: conflictLabel,
            reload: function () {
                return api.get('Babelfin/Admin/Settings').then(function (fresh) {
                    if (!current(state, revision)) { if (panel.parentNode) { panel.parentNode.removeChild(panel); } return; }
                    const baseline = clone(state.expectedBase || state.config), draft = clone(state.config);
                    Object.keys(state.pending).forEach(function (key) { state.pending[key](draft); });
                    paths.forEach(function (path) {
                        UI.setFieldValue(baseline, path, UI.fieldValue(fresh, path));
                        UI.setFieldValue(draft, path, UI.fieldValue(fresh, path));
                    });
                    const remaining = editedFields(baseline, draft);
                    state.config = draft; state.expectedBase = baseline;
                    state.pending = remaining.length ? { retained: function (target) { applyEditedFields(target, draft, remaining); } } : {};
                    clearProblem(box); renderSettings();
                });
            },
            keep: function () { if (!Object.keys(state.pending).length) { clearProblem(box); return Promise.resolve(); } return savePage(true); },
            error: function (err) { showErrors(err, box, []); }
        });
        clear(box); box.classList.remove('hide'); box.appendChild(panel); panel.focus();
    }

    function savePage(force) {
        const state = S, revision = state.revision;
        if (state.saving) { return Promise.resolve(); }
        const applies = Object.keys(state.pending).map(function (k) { return state.pending[k]; });
        if (!applies.length) {
            return Promise.resolve();
        }
        clearFieldErrors(state.page);
        return saveStructured(function (fresh) {
            applies.forEach(function (apply) { apply(fresh); });
        }, q('#bfSaveError'), q('#bfSave'), force).then(function () {
            if (!current(state, revision)) { return; }
            state.pending = {};
            invalidate('libraries');
            renderSettings();
        }, function () { /* shown next to the fields, retaining the draft */ });
    }

    function discardPage() {
        if (S.expectedBase) { S.config = S.expectedBase; S.expectedBase = null; }
        S.pending = {};
        clearProblem(q('#bfSaveError'));
        renderSettings();
    }

    /** Renders every part of the page that shows server settings (on load, and after a save, a discard or a restore). */
    function renderSettings() {
        S.claims = [];
        paintSaveBar();
        renderHeader();
        renderMigrationNotice();
        const done = renderLibraries();
        if (S.rendered.advanced) {
            renderAdvanced();
        }
        return done;
    }

    /* ---------- controls ---------- */

    function smallButton(text, onClick, title) {
        return h('button', { type: 'button', class: 'bf-btn', onclick: onClick, title: title || null }, text);
    }

    function button(text, onClick, cls) {
        // "emby-button" is named too: when jellyfin-web doesn't upgrade a button made by script, it still gets Jellyfin's button styling.
        return h('button', { is: 'emby-button', type: 'button', class: 'raised emby-button ' + (cls || ''), onclick: onClick }, h('span', { text: text }));
    }

    function hint(text) {
        return h('p', { class: 'bf-hint', text: text });
    }

    function groupTitle(text) {
        return h('h4', { class: 'bf-group-title', text: text });
    }

    /** An "i" button explaining a setting (BabelfinUI.infoTip), or null before the shared components load. Returns { element, tipId }. */
    function info(label, text) {
        const UI = ui();
        return UI && typeof UI.infoTip === 'function' && text ? UI.infoTip({ t: S.uiT || t, label: label, text: text }) : null;
    }

    /** Adds the tip's text to a control's accessible description. */
    function describe(control, tip) {
        if (control && tip && control.setAttribute) {
            control.setAttribute('aria-describedby', ((control.getAttribute('aria-describedby') || '') + ' ' + tip.tipId).trim());
        }
    }

    /** A title (h4, h5, …) with an infoTip beside it: the heading keeps only its text (its accessible name), the tip follows it. */
    function titleWithInfo(tag, cls, text, infoText) {
        const tip = info(text, infoText);
        const title = h(tag, { class: cls, text: text });
        return tip ? h('div', { class: 'bf-title-line ' + cls + '-line' }, [title, tip.element]) : title;
    }

    /** headers: texts, or [text, infoText] pairs for a header with an infoTip. */
    function table(headers, rows, cls) {
        return h('div', { class: 'bf-table-wrap' }, h('table', { class: 'bf-table ' + (cls || '') }, [
            h('thead', null, h('tr', null, headers.map(function (x) {
                if (Array.isArray(x)) {
                    const tip = info(x[0], x[1]);
                    return h('th', { scope: 'col', class: 'bf-has-info' }, [h('span', { text: x[0] }), tip && tip.element]);
                }
                return h('th', { scope: 'col', text: x });
            }))),
            h('tbody', null, rows)
        ]));
    }

    /** A labelled row: the label (with an infoTip when infoText is given), the control, an optional help line and a place for an error. */
    function row(labelText, control, help, infoText) {
        const target = control && (control.id ? control : control.querySelector && control.querySelector('select, input, textarea'));
        const id = target && target.id;
        const tip = info(labelText, infoText);
        describe(target, tip);
        const label = h(id ? 'label' : 'div', { class: 'bf-row-label', for: id || null, text: labelText });
        return h('div', { class: 'bf-row' }, [
            tip ? h('div', { class: 'bf-row-head' }, [label, tip.element]) : label,
            h('div', { class: 'bf-row-control' }, control),
            help ? h('div', { class: 'bf-hint', text: help }) : null,
            fieldErrorBox()
        ]);
    }

    function option(value, text) {
        return h('option', { value: value, text: text });
    }

    function dropdown(choices, value, label) {
        const sel = h('select', { class: 'bf-select', id: uid(), 'aria-label': label || null });
        choices.forEach(function (c) { append(sel, option(c[0], c[1])); });
        sel.value = value === null || value === undefined ? '' : String(value);
        if (sel.selectedIndex < 0 && sel.options.length) {
            sel.selectedIndex = 0;
        }
        return sel;
    }

    function checkbox(text, checked, onChange, infoText) {
        const input = h('input', { is: 'emby-checkbox', type: 'checkbox' });
        input.checked = !!checked;
        if (onChange) {
            input.addEventListener('change', function () { onChange(input.checked); });
        }
        const tip = info(text, infoText);
        describe(input, tip);
        const label = h('label', { class: 'checkboxContainer bf-check' }, [input, h('span', { text: text })]);
        return { node: tip ? h('div', { class: 'bf-check-line' }, [label, tip.element]) : label, input: input };
    }

    /**
     * A dropdown whose values can also be languages ('lang:<code>'): the fixed choices, then the common languages, then an entry
     * ("A specific language…") that opens BabelfinUI.languageChooser beside the dropdown. o: { choices: [[value, text]], common: [codes],
     * value, label, pickText, onChange(value) }. Returns { node, select, value(), setValue(value) (selects it without calling onChange) }.
     */
    function languageDropdown(o) {
        const sel = h('select', { class: 'bf-select', id: uid(), 'aria-label': o.label || null });
        const extra = h('span', { class: 'bf-chooser hide' });
        let previous = '';

        function fill(value) {
            clear(sel);
            o.choices.forEach(function (c) { append(sel, option(c[0], c[1])); });
            const codes = (o.common || []).slice();
            const current = langOf(value);
            if (current && !codes.some(function (c) { return String(c).toLowerCase() === current.toLowerCase(); })) {
                codes.push(current);
            }
            codes.forEach(function (c) { append(sel, option('lang:' + c, langName(c))); });
            if (ui() && typeof ui().languageChooser === 'function') {
                append(sel, option(PICK, o.pickText || t('common.specificLanguage')));
            }
            const match = Array.prototype.find.call(sel.options, function (opt) {
                return opt.value === value || (langOf(opt.value) && current && langOf(opt.value).toLowerCase() === current.toLowerCase());
            });
            sel.value = match ? match.value : (sel.options.length ? sel.options[0].value : '');
            previous = sel.value;
        }

        function closeChooser() {
            clear(extra);
            extra.classList.add('hide');
        }

        function openChooser() {
            clear(extra);
            extra.classList.remove('hide');
            const chooser = ui().languageChooser({
                t: S.uiT,
                languages: S.languageList || [],
                uiLanguage: S.lang,
                label: o.label,
                value: null,
                onChange: function (code) {
                    if (!code) {
                        return;
                    }
                    closeChooser();
                    fill('lang:' + code);
                    o.onChange(sel.value);
                    sel.focus();
                }
            });
            append(extra, [chooser.element, smallButton(t('common.cancel'), function () {
                closeChooser();
                sel.value = previous;
                sel.focus();
            })]);
            if (typeof chooser.open === 'function') {
                chooser.open();
            }
        }

        sel.addEventListener('change', function () {
            if (sel.value === PICK) {
                openChooser();
                return;
            }
            closeChooser();
            previous = sel.value;
            o.onChange(sel.value);
        });
        fill(o.value || '');
        return {
            node: h('span', { class: 'bf-langdrop' }, [sel, extra]),
            select: sel,
            value: function () { return sel.value === PICK ? previous : sel.value; },
            setValue: function (value) {
                closeChooser();
                fill(value || '');
            }
        };
    }

    /** A checkbox for a boolean setting; write(fresh, value) stores it. Returns { node, set(value) } (set: as if the person clicked it). */
    function checkRow(label, value, key, write, infoText) {
        const initial = !!value;
        const changed = function (on) {
            track(key, initial, on, function (fresh) { write(fresh, on); });
        };
        const box = checkbox(label, initial, changed, infoText);
        return {
            node: box.node,
            set: function (on) {
                box.input.checked = !!on;
                changed(!!on);
            }
        };
    }

    function fromLines(text) {
        return String(text || '').split(/\r?\n/).map(function (s) { return s.trim(); }).filter(Boolean);
    }

    /** A text area with one entry per line for a list setting. Returns { node, set(values) } (set: as if the person typed them). */
    function linesRow(label, values, key, path, write, help) {
        const initial = (values || []).slice();
        const area = h('textarea', { class: 'bf-textarea', rows: 4, id: uid(), spellcheck: 'false' });
        area.value = initial.join('\n');
        const changed = function () {
            const v = fromLines(area.value);
            track(key, initial, v, function (fresh) { write(fresh, v); });
        };
        area.addEventListener('input', changed);
        const r = row(label, area, help);
        claim(path, r);
        return {
            node: r,
            set: function (list) {
                area.value = (list || []).join('\n');
                changed();
            }
        };
    }

    function everyday(fresh) {
        fresh.Everyday = fresh.Everyday || {};
        return fresh.Everyday;
    }

    /* ---------- header ---------- */

    function renderHeader() {
        const priorNotice = q('.bf-translation-notice');
        if (priorNotice) { priorNotice.remove(); }
        if (S.uiT && S.uiT.machineTranslated) {
            append(q('.bf-head'), h('a', { class: 'bf-translation-notice', href: 'https://github.com/WizardBal/Babelfin/blob/main/CONTRIBUTING.md#translations', target: '_blank', rel: 'noopener', text: S.uiT('common.machineTranslated') }));
        }
        const priorStatus = q('#bfChoiceStatus');
        if (priorStatus && priorStatus.parentNode) { priorStatus.parentNode.removeChild(priorStatus); }
        const UI = ui();
        if (UI && typeof UI.choiceStatus === 'function') {
            S.choiceStatus = UI.choiceStatus({
                className: 'bf-hint', text: t('page.noChoices'),
                load: function () { return api.get('Babelfin/Admin/ChoiceStatus'); }
            });
            S.choiceStatus.element.id = 'bfChoiceStatus';
            const header = q('.bf-head');
            if (header) { append(header, S.choiceStatus.element); }
        }
        const box = q('#bfEnabled');
        S.enabledInitial = S.config.Enabled !== false;
        box.checked = S.enabledInitial;
        box.disabled = false;
        claim('enabled', box.parentNode);
        // The page's static header gets its infoTips once.
        const label = box.parentNode;
        if (label && label.parentNode && !label.parentNode.querySelector('.bf-enabled-info')) {
            const tip = info(t('page.enabled'), t('page.enabled.info'));
            if (tip) {
                describe(box, tip);
                tip.element.classList.add('bf-enabled-info');
                const line = h('div', { class: 'bf-check-line' });
                label.parentNode.insertBefore(line, label);
                append(line, [label, tip.element]);
            }
        }
        const title = q('#bfDefaultsTitle');
        if (title && title.parentNode && !title.parentNode.classList.contains('bf-title-line')) {
            const tip = info(t('defaults.title'), t('defaults.info'));
            if (tip) {
                const line = h('div', { class: 'bf-title-line bf-section-title-line' });
                title.parentNode.insertBefore(line, title);
                append(line, [title, tip.element]);
            }
        }
    }

    function renderMigrationNotice() {
        const state = S;
        api.get('Babelfin/Admin/Migrations').then(function (notices) {
            if (S !== state) return;
            const prior = q('#bfMigrationNotice');
            if (prior && prior.parentNode) prior.parentNode.removeChild(prior);
            if (!notices || !notices.length) return;
            const panel = h('section', { id: 'bfMigrationNotice', class: 'bf-alert bf-alert-info' }, h('h3', { text: t('migration.title') }));
            notices.forEach(function (n) {
                append(panel, [h('strong', { text: n.Name }), h('ul', null, (n.Changes || []).map(function (c) { return h('li', { text: c }); }))]);
            });
            append(panel, smallButton(t('migration.dismiss'), function () {
                api.post('Babelfin/Admin/Migrations/Acknowledge', {}).then(function () {
                    if (panel.parentNode) panel.parentNode.removeChild(panel);
                }, function (err) { showLoadError(panel, err); });
            }));
            const header = q('.bf-head');
            if (header) append(header, panel);
        }, function () { /* An older server has no migration report. */ });
    }

    /* ---------- Libraries: the same choice for every library, or one per library ---------- */

    // The fields the "Audio and subtitles" table edits in a settings layer.
    const TABLE_FIELDS = ['AudioChain', 'ForeignTitles', 'WhenAudioUnderstood', 'WhenAudioNotUnderstood', 'SubtitleLanguages'];

    /** The Default of every row of the admin's own tables: the server's built-in defaults leave audio and subtitles to the video. */
    function neutralDefaults() {
        return {
            AllLibraries: {
                AudioChain: [{ Kind: 'VideoDefault' }],
                WhenAudioUnderstood: 'VideoDefault',
                WhenAudioNotUnderstood: 'VideoDefault',
                SubtitleLanguages: [],
                Understood: [],
                Reads: []
            },
            Libraries: {}
        };
    }

    /** The table fields of a layer, each null when unset. */
    function tableFields(layer) {
        const out = {};
        TABLE_FIELDS.forEach(function (f) {
            out[f] = layer && layer[f] !== undefined ? clone(layer[f]) : null;
        });
        return out;
    }

    /** What the Libraries table edits: { Mode (PerLibraryDefaults: true, false or null), Everyday: fields, Libraries: { id: fields } }. */
    function tableState(doc) {
        const libs = {};
        ((doc && doc.Libraries) || []).forEach(function (l) {
            if (l && l.LibraryId) {
                libs[normId(l.LibraryId)] = tableFields(l.Settings);
            }
        });
        const mode = doc ? doc.PerLibraryDefaults : null;
        return { Mode: mode === true || mode === false ? mode : null, Everyday: tableFields(doc && doc.Everyday), Libraries: libs };
    }

    function findLibrary(config, id) {
        return ((config && config.Libraries) || []).find(function (l) { return l && sameId(l.LibraryId, id); }) || null;
    }

    /** The heading cell of a row of the Libraries table ('all' or a library id), where a save error for that row is shown. */
    function tableRowHead(host, key) {
        const row = Array.prototype.find.call(host.querySelectorAll('tr.bfui-table-row'), function (r) { return r.getAttribute('data-row') === key; });
        return row ? row.querySelector('th') : host;
    }

    /**
     * Records the Libraries table as one pending change: on save it writes PerLibraryDefaults when the mode was picked, and only the table
     * fields that differ from what was loaded, into Everyday and into each library's entry (an entry is created when a library has none;
     * nothing else in an entry is touched).
     */
    function trackLibrariesTable(initial, work, host) {
        const before = tableState(initial);
        const now = tableState(work);
        const names = {};
        (work.Libraries || []).concat(initial.Libraries || []).forEach(function (l) {
            if (l && l.LibraryId && l.Name && !names[normId(l.LibraryId)]) {
                names[normId(l.LibraryId)] = l.Name;
            }
        });
        track('LibrariesTable', before, now, function (fresh) {
            if (before.Mode !== now.Mode) {
                fresh.PerLibraryDefaults = now.Mode;
                claim('perLibraryDefaults', host);
            }
            const changedEveryday = TABLE_FIELDS.filter(function (f) { return !sameValue(before.Everyday[f], now.Everyday[f]); });
            if (changedEveryday.length) {
                const ev = everyday(fresh);
                changedEveryday.forEach(function (f) { ev[f] = clone(now.Everyday[f]); });
                claim('everyday', tableRowHead(host, 'all'));
            }
            const ids = Object.keys(before.Libraries).concat(Object.keys(now.Libraries).filter(function (id) { return !(id in before.Libraries); }));
            ids.forEach(function (id) {
                const was = before.Libraries[id] || tableFields(null);
                const is = now.Libraries[id] || tableFields(null);
                const changed = TABLE_FIELDS.filter(function (f) { return !sameValue(was[f], is[f]); });
                if (!changed.length) {
                    return;
                }
                fresh.Libraries = fresh.Libraries || [];
                let entry = findLibrary(fresh, id);
                if (!entry) {
                    entry = { LibraryId: id, Name: names[id] || null, OriginalAudio: 'Auto', Settings: {} };
                    fresh.Libraries.push(entry);
                }
                entry.Settings = entry.Settings || {};
                changed.forEach(function (f) { entry.Settings[f] = clone(is[f]); });
                claim('libraries[' + fresh.Libraries.indexOf(entry) + ']', tableRowHead(host, id));
            });
        });
    }

    function renderLibraries() {
        const state = S, revision = S.revision;
        const body = q('#bfLibraries');
        loading(body);
        return Promise.all([libraries(), languages()]).then(function (res) {
            if (S !== state || state.revision !== revision || state.shown === false) { return; }
            clear(body);
            // The table edits a copy; the Save bar writes only what changed into the settings read fresh at save time.
            const initial = {
                PerLibraryDefaults: S.config.PerLibraryDefaults === true || S.config.PerLibraryDefaults === false ? S.config.PerLibraryDefaults : null,
                Everyday: clone(S.config.Everyday || {}),
                Libraries: clone(S.config.Libraries || []),
                SmartRules: clone(S.config.SmartRules || [])
            };
            const work = clone(initial);
            const host = h('div', { class: 'bf-lib-table' });
            append(body, host);
            mountLibraryTable(host, {
                settings: work,
                modeField: 'PerLibraryDefaults',
                defaults: neutralDefaults(),
                libraries: res[0].map(function (l) { return { Id: l.Id, Name: l.Name }; }),
                audience: 'admin',
                onChange: function () {
                    trackLibrariesTable(initial, work, host);
                    const UI = ui();
                    track('GenreRows', initial.SmartRules, work.SmartRules, function (fresh) {
                        fresh.SmartRules = UI.mergeGenreRows(initial.SmartRules, work.SmartRules, fresh.SmartRules || []);
                        claim('smartRules', host);
                    });
                }
            });
        }, function (err) {
            if (S !== state || state.revision !== revision || state.shown === false) { return; }
            showLoadError(body, err);
        });
    }

    /* ---------- People ---------- */

    /** A word from the person's page translations (the shared words: "My languages", the table's option names). */
    function uiWord(key) {
        return S.uiT(key);
    }

    /**
     * What a People row shows in its Audio or Subtitles column, from the GET Admin/Users row: "Default" without a Babelfin
     * file or without a choice, "Set per library" when the person chooses per library, else the "every library" choice in words through
     * ui.js's table readers (Video default, a language, Off, When the audio isn't in their languages, Signs and songs only, Custom). The
     * row's PerLibrary and Table may be missing from the JSON: missing counts as false and null.
     */
    function peopleCell(u, kind, names) {
        if (!u.HasSettings) {
            return uiWord('ui.default');
        }
        if (u.PerLibrary === true) {
            return t('people.perLibrary');
        }
        const UI = ui();
        const layer = u.Table && typeof u.Table === 'object' ? u.Table : null;
        if (!UI || typeof UI.tableAudioValue !== 'function' || typeof UI.tableSubtitleValue !== 'function') {
            return layer ? uiWord('ui.table.custom') : uiWord('ui.default');
        }
        const value = kind === 'audio' ? UI.tableAudioValue(layer, []) : UI.tableSubtitleValue(layer, []);
        if (!value) {
            return uiWord('ui.default');
        }
        if (value.indexOf('lang:') === 0) {
            const code = value.slice(5);
            return names.get(code.toLowerCase()) || langName(code);
        }
        switch (value) {
            case 'mine':
                return uiWord('ui.table.audioMyLanguages');
            case 'original':
                return uiWord('ui.table.audioOriginal');
            case 'video':
                return uiWord('ui.table.videoDefault');
            case 'off':
                return uiWord('ui.table.off');
            case 'smart':
                return t('people.smart');
            case 'signs':
                return uiWord('ui.table.signs');
            default:
                return uiWord('ui.table.custom');
        }
    }

    /** Language names as the table's dropdowns show them (two codes the browser names alike keep apart). */
    function tableNames(list) {
        const UI = ui();
        const names = new Map();
        if (!UI || typeof UI.tableLanguages !== 'function') {
            return names;
        }
        const codes = [];
        list.forEach(function (u) {
            const layer = u && u.Table;
            ((layer && layer.AudioChain) || []).forEach(function (s) { if (s && s.Language) { codes.push(s.Language); } });
            ((layer && layer.SubtitleLanguages) || []).forEach(function (c) { if (c) { codes.push(c); } });
        });
        try {
            UI.tableLanguages({ languages: S.languageList || [], uiLanguage: S.lang }, codes).forEach(function (e) { names.set(e.code.toLowerCase(), e.name); });
        } catch (e) {
            // the plain names below
        }
        return names;
    }

    function renderPeople(openId) {
        const state = S, revision = S.revision;
        const body = q('#bfPeople');
        S.person = null;
        loading(body);
        return Promise.all([users(), languages()]).then(function (res) {
            if (S !== state || state.revision !== revision || state.shown === false) { return; }
            const list = res[0];
            clear(body);
            if (!list.length) {
                append(body, hint(t('people.none')));
                return;
            }
            const names = tableNames(list);
            const tbody = h('tbody');
            list.forEach(function (u) {
                const tr = h('tr', { 'data-user': normId(u.UserId) });
                append(tr, [
                    h('th', { scope: 'row', text: u.Name }),
                    h('td', { class: 'bf-people-audio', text: peopleCell(u, 'audio', names), 'data-label': t('libraries.audio') }),
                    h('td', { class: 'bf-people-subtitles', text: peopleCell(u, 'subtitles', names), 'data-label': t('libraries.subtitles') }),
                    h('td', { class: 'bf-cell-action' }, smallButton(t('common.edit'), function () { togglePerson(u, tr); }))
                ]);
                append(tbody, tr);
                if (openId && sameId(openId, u.UserId)) {
                    openPerson(u, tr);
                }
            });
            const head = h('thead', null, h('tr', null, [
                h('th', { scope: 'col', text: t('people.name') }),
                h('th', { scope: 'col', text: t('libraries.audio') }),
                h('th', { scope: 'col', text: t('libraries.subtitles') }),
                // The Edit column's header is for screen readers only: every row's button already reads "Edit".
                h('th', { scope: 'col', class: 'bf-cell-action' }, h('span', { class: 'bf-sr', text: t('common.edit') }))
            ]));
            body.insertBefore(h('div', { class: 'bf-table-wrap' }, h('table', { class: 'bf-table bf-people' }, [head, tbody])), body.firstChild);
        }, function (err) {
            if (S !== state || state.revision !== revision || state.shown === false) { return; }
            showLoadError(body, err);
        });
    }

    function closePerson() {
        if (S.person && S.person.row && S.person.row.parentNode) {
            S.person.row.parentNode.removeChild(S.person.row);
        }
        S.person = null;
    }

    function togglePerson(u, tr) {
        const same = S.person && sameId(S.person.id, u.UserId);
        const go = function () {
            if (same) {
                closePerson();
            } else {
                openPerson(u, tr);
            }
        };
        if (S.person && S.person.dirty) {
            confirmAsync(t('people.discard')).then(function (ok) {
                if (ok) {
                    go();
                }
            });
        } else {
            go();
        }
    }

    function openPerson(u, tr) {
        closePerson();
        const cell = h('td', { colspan: 4, class: 'bf-editor-cell' });
        const editorRow = h('tr', { class: 'bf-editor-row' }, cell);
        tr.parentNode.insertBefore(editorRow, tr.nextSibling);
        S.person = { id: u.UserId, row: editorRow, dirty: false };
        loading(cell);
        // The table's "Default (…)" values come from Admin/Users/{id}/Defaults (what each row gives without the person's own choice).
        const defaultsCall = api.get('Babelfin/Admin/Users/' + encodeURIComponent(u.UserId) + '/Defaults').then(null, function (err) {
            console.warn('[Babelfin] the table defaults could not be loaded', err);
            return null;
        });
        // The person's show and movie overrides. They are secondary: a failure leaves the editor usable and is shown in its section.
        const overridesCall = api.get(overridesPath(u.UserId)).then(function (list) {
            return { rows: Array.isArray(list) ? list : [], error: null };
        }, function (err) {
            console.warn('[Babelfin] the show and movie overrides could not be loaded', err);
            return { rows: null, error: err };
        });
        Promise.all([api.get(userPath(u.UserId)), libraries(), userViews(u.UserId), users(), defaultsCall, overridesCall]).then(function (res) {
            if (!S.person || S.person.row !== editorRow) {
                return;
            }
            const answer = res[0];
            const views = res[2];
            const libs = isLibraryList(answer && answer.Libraries) && answer.Settings ? answer.Libraries
                : res[1].filter(function (l) { return !views || views.indexOf(normId(l.Id)) >= 0; });
            const defaults = res[4] || (answer && (answer.Defaults || (answer.Settings && answer.Settings.Defaults))) || null;
            personEditor(clear(cell), u, personDoc(answer), defaults, libs, res[5]);
        }, function (err) {
            showLoadError(cell, err);
        });
    }

    /**
     * BabelfinUI.libraryTable (the "Audio and subtitles" table with its two modes), or a line saying the files are out of date.
     * o: { settings, modeField ('PerLibrary' or 'PerLibraryDefaults'), defaults, libraries, audience ('person' or 'admin' for the server's
     * wording), onChange }.
     */
    function mountLibraryTable(host, o) {
        const UI = ui();
        if (UI && typeof UI.libraryTable === 'function') {
            try {
                const table = UI.libraryTable(host, {
                    settings: o.settings,
                    modeField: o.modeField || 'PerLibrary',
                    defaults: o.defaults,
                    libraries: o.libraries,
                    languages: S.languageList || [],
                    translate: S.uiT,
                    t: S.uiT,
                    uiLanguage: S.lang,
                    audience: o.audience || 'person',
                    onChange: o.onChange
                });
                const genreOptions = {
                    settings: o.settings, t: S.uiT, languages: S.languageList || [], uiLanguage: S.lang,
                    audience: o.audience || 'person',
                    mine: function () {
                        return (o.settings.Everyday && o.settings.Everyday.Understood)
                            || (o.defaults && o.defaults.AllLibraries && o.defaults.AllLibraries.Understood) || [];
                    },
                    reads: function () {
                        return (o.settings.Everyday && o.settings.Everyday.Reads)
                            || (o.defaults && o.defaults.AllLibraries && o.defaults.AllLibraries.Reads) || [];
                    },
                    loadGenres: function () {
                        const viewerId = o.audience === 'admin' ? ApiClient.getCurrentUserId() : o.settings.UserId;
                        if (!viewerId) { return Promise.reject(new Error('No viewer')); }
                        return api.get('Genres', { UserId: viewerId });
                    },
                    onChange: o.onChange
                };
                let genres = UI.genreRows(host, genreOptions);
                function redrawGenres() {
                    const active = document.activeElement;
                    const restoreFocus = active && genres.element.contains(active);
                    const label = restoreFocus && active.getAttribute('aria-label');
                    const tag = restoreFocus && active.tagName;
                    genres.element.remove();
                    genres = UI.genreRows(host, genreOptions);
                    if (o.onMount) { o.onMount(table, genres, redrawGenres); }
                    if (restoreFocus) {
                        const controls = Array.from(genres.element.querySelectorAll('input, select, button'));
                        const control = controls.find(function (node) { return node.tagName === tag && label && node.getAttribute('aria-label') === label; });
                        const target = control || controls.find(function (node) { return !node.disabled; });
                        if (target) { target.focus(); }
                    }
                }
                if (o.onMount) { o.onMount(table, genres, redrawGenres); }
                return true;
            } catch (e) {
                console.warn('[Babelfin] the library table could not be shown', e);
                clear(host);
            }
        }
        append(host, hint(t('people.needsUpdate')));
        return false;
    }

    /**
     * The person's editor (opened under their row). overrides: { rows, error } from GET Admin/Users/{id}/Overrides (rows null when the list
     * couldn't be loaded). Under the "Audio and subtitles" table sits the collapsible "Show and movie overrides (N)" section;
     * removing an override acts at once, so the working document and its baseline drop it too (it never counts as an unsaved change), and the
     * save below keeps the stored overrides (keepOverrides), so it can't bring one back or drop one added meanwhile.
     */
    function personEditor(host, u, doc, defaults, libs, overrides, baseline) {
        const state = S, editor = state.person;
        const base = clone(doc) || {};
        delete base.Defaults;
        base.Everyday = base.Everyday || {};
        base.Libraries = base.Libraries || [];
        const original = clone(baseline || base);
        const work = clone(base);
        const revision = state.revision;
        let saving = false;
        const claims = [];
        const box = errorBox();
        const name = u.Name || work.UserName || '';
        const detected = !!(work.Seed && work.Seed.Confirmed === false && (work.Seed.Assumed || []).length);
        let overrideRows = overrides && Array.isArray(overrides.rows) ? overrides.rows : null;
        let overridesError = overrides ? overrides.error : null;
        let overridesGroup = null;
        let overridesView = null;
        let editorTable = null;
        let editorGenres = null;
        let redrawEditorGenres = null;

        const saveBtn = button(t('common.save'), save, 'button-submit');
        const copyBtn = smallButton(t('people.copyTo'), function () { copyHost.classList.toggle('hide'); });
        const copyHost = h('div', { class: 'bf-copy hide' });

        function changed() {
            const dirty = !sameValue(work, original);
            if (current(state) && state.person === editor) {
                editor.dirty = saving || dirty;
            }
            saveBtn.disabled = saving || !dirty;
            copyBtn.disabled = saving || dirty;
        }

        const enabled = checkbox(t('people.enabled'), work.Enabled !== false, function (on) {
            work.Enabled = on;
            changed();
        });

        const langHost = h('div', { class: 'bf-row-control' });
        const langRow = h('div', { class: 'bf-row' }, [h('div', { class: 'bf-row-label', text: uiWord('languages.title') }), langHost, fieldErrorBox()]);
        claims.push({ path: 'everyday.understood', node: langRow });
        const UI = ui();
        const codes = (work.Everyday.Understood || []).slice();
        if (UI && typeof UI.languageList === 'function') {
            UI.languageList(langHost, codes, {
                t: S.uiT,
                languages: S.languageList || [],
                uiLanguage: S.lang,
                label: uiWord('languages.title'),
                onChange: function (items) {
                    work.Everyday.Understood = items.slice();
                    if (work.Seed) {
                        work.Seed.Confirmed = true;
                    }
                    if (editorTable) { editorTable.refresh(); }
                    if (editorGenres) { editorGenres.refreshLanguages(); }
                    if (overridesView) {
                        overridesView.refresh();
                    }
                    changed();
                }
            });
        } else {
            append(langHost, h('span', { text: languagesText(codes) }));
        }

        const tableHost = h('div', { class: 'bf-person-table' });
        claims.push({ path: 'libraries', node: tableHost });
        claims.push({ path: 'smartRules', node: tableHost });

        // The target list for "Copy settings to…".
        const target = h('select', { class: 'bf-select', 'aria-label': t('people.copyTo') });
        (S.userList || []).filter(function (x) { return !sameId(x.UserId, u.UserId); }).forEach(function (x) {
            append(target, option(x.UserId, x.Name));
        });
        const copyGo = button(t('people.copy'), function () {
            const to = target.value;
            if (!to) {
                return;
            }
            const toName = target.options[target.selectedIndex].text;
            confirmAsync(t('people.copyConfirm', { from: name, to: toName })).then(function (ok) {
                if (!ok) {
                    return;
                }
                clearProblem(box);
                busy(copyGo, true);
                api.post('Babelfin/Admin/Users/' + encodeURIComponent(u.UserId) + '/CopyTo/' + encodeURIComponent(to)).then(function (result) {
                    busy(copyGo, false);
                    const skipped = (result && (result.SkippedItemIds || result.skippedItemIds)) || [];
                    toast(skipped.length ? t('people.copiedSkipped', { count: skipped.length }) : t('people.copied'));
                    invalidate('users');
                    if (S.choiceStatus) { S.choiceStatus.refresh(); }
                    renderPeople(u.UserId);
                }, function (err) {
                    busy(copyGo, false);
                    showErrors(err, box, []);
                });
            });
        });
        append(copyHost, [target, copyGo]);

        // ----- Show and movie overrides -----

        function overridesTitle() {
            return overrideRows ? uiWord('overrides.title') + ' (' + overrideRows.length + ')' : uiWord('overrides.title');
        }

        function paintOverridesTitle() {
            const title = overridesGroup && overridesGroup.button.querySelector('.bfui-disclosure-title');
            if (title) {
                title.textContent = overridesTitle();
            }
        }

        function sameOverride(a, b) {
            return sameId(a.ItemId, b.ItemId) && String(a.Scope) === String(b.Scope);
        }

        // My languages and Languages for subtitles as they stand in the editor (unsaved edits first, else what applies), for the table's words.
        function personLanguages(field) {
            const own = work.Everyday[field];
            const row = defaults && defaults.AllLibraries;
            return Array.isArray(own) ? own : ((row && Array.isArray(row[field]) && row[field]) || []);
        }

        // The list again from the server (after a removal, so an override added meanwhile shows too); a failure keeps what is shown.
        function reloadOverrides() {
            if (S.choiceStatus) { S.choiceStatus.refresh(); }
            return api.get(overridesPath(u.UserId)).then(function (list) {
                overrideRows = Array.isArray(list) ? list : [];
                overridesError = null;
                paintOverridesTitle();
                if (overridesView) {
                    overridesView.refresh(overrideRows);
                }
            }, function (err) {
                overridesError = err;
            });
        }

        // DELETE Admin/Users/{id}/Overrides/{itemId}?scope=…: the explicit settings and the remembered track change go together.
        function removeOverride(row) {
            clearProblem(box);
            // Inside a promise, so a row overridesPath refuses (no real id) comes back as a rejection and nothing is sent.
            return Promise.resolve().then(function () {
                return api.del(overridesPath(u.UserId, row), { scope: row.Scope });
            }).then(null, function (err) {
                // Gone already (removed meanwhile): the list is brought up to date below all the same.
                if (err && err.status === 404) {
                    return null;
                }
                throw err;
            }).then(function () {
                [work, original].forEach(function (each) {
                    if (Array.isArray(each.Overrides)) {
                        each.Overrides = each.Overrides.filter(function (o) { return !sameOverride(o, row); });
                    }
                });
                overrideRows = (overrideRows || []).filter(function (o) { return !sameOverride(o, row); });
                paintOverridesTitle();
                toast(uiWord('overrides.removed'));
                changed();
                return reloadOverrides();
            });
        }

        function removeAllOverrides() {
            clearProblem(box);
            return api.del(overridesPath(u.UserId) + '/All').then(function () {
                work.Overrides = [];
                original.Overrides = [];
                overrideRows = [];
                paintOverridesTitle();
                toast(uiWord('overrides.removeAll.done'));
                changed();
                return reloadOverrides();
            });
        }

        // The section's body: the help line, then the table (or a message and "Refresh" when the list couldn't be loaded).
        function fillOverrides(body) {
            const UI = ui();
            const listHost = h('div', { class: 'bf-person-overrides-list' });
            overridesView = null;
            append(clear(body), [hint(uiWord('overrides.helpAdmin')), listHost]);
            if (!UI || typeof UI.overridesTable !== 'function') {
                append(listHost, hint(t('people.needsUpdate')));
                return;
            }
            if (!overrideRows) {
                showLoadError(listHost, overridesError);
                append(listHost, smallButton(t('common.refresh'), function () {
                    reloadOverrides().then(function () { fillOverrides(body); });
                }));
                return;
            }
            overridesView = UI.overridesTable(listHost, {
                t: S.uiT,
                rows: overrideRows,
                languages: S.languageList || [],
                uiLanguage: S.lang,
                audience: 'person',
                mine: function () { return personLanguages('Understood'); },
                reads: function () { return personLanguages('Reads'); },
                onRemove: removeOverride,
                onRemoveAll: removeAllOverrides,
                onError: function (err) { showErrors(err, box, []); },
                confirmText: S.uiT('overrides.removeAll.confirmTextFor', { name: name })
            });
        }

        const overridesHost = h('div', { class: 'bf-person-overrides' });
        overridesGroup = foldGroup(overridesHost, 'overrides', overridesTitle(), fillOverrides, true);

        function save(force) {
            if (saving || !current(state, revision) || state.person !== editor) { return; }
            clearFieldErrors(host);
            clearProblem(box);
            saving = true;
            busy(saveBtn, true);
            changed();
            const submitted = clone(work);
            let committed = false, committedDocument = null;
            // Both intent construction and the request run inside the chain: malformed rows fail visibly and release Save.
            Promise.resolve().then(function () {
                const fields = editedFields(original, submitted);
                if (!fields.length) { return null; }
                return api.put(userPath(u.UserId), force === true ? submitted : ui().withExpected(submitted, original, fields), { keepOverrides: true, fields: fields.join(',') });
            }).then(function (saved) {
                if (!saved) { return; }
                committed = true;
                committedDocument = clone(personDoc(saved));
                invalidate('users');
                if (!current(state, revision) || state.person !== editor) { return; }
                const later = editedFields(submitted, work);
                const rebased = clone(personDoc(saved));
                delete rebased.Defaults;
                rebased.Everyday = rebased.Everyday || {};
                rebased.Libraries = rebased.Libraries || [];
                rebased.SmartRules = rebased.SmartRules || [];
                replaceDocument(original, rebased);
                applyEditedFields(rebased, work, later.filter(function (field) { return field !== 'smartRules'; }));
                if (later.indexOf('smartRules') >= 0) {
                    // Only the visible genre edits are newer than the submission; keep returned hidden fields and concurrent rows.
                    const UI = ui(), before = clone(submitted.SmartRules || []), edited = clone(work.SmartRules || []);
                    before.forEach(function (rule) {
                        if (rule.Id || !UI.isGenreRow(rule)) { return; }
                        const genre = rule.Conditions[0].Values[0].trim().toLowerCase();
                        const sameGenre = function (row) { return UI.isGenreRow(row) && row.Conditions[0].Values[0].trim().toLowerCase() === genre; };
                        const matches = rebased.SmartRules.filter(sameGenre);
                        if (matches.length !== 1 || !matches[0].Id) { return; }
                        // PrepareRules assigns ids to new rows. Use that identity for later edits and removals too.
                        [before, edited].forEach(function (list) { list.filter(function (row) { return !row.Id && sameGenre(row); }).forEach(function (row) { row.Id = matches[0].Id; }); });
                    });
                    rebased.SmartRules = UI.mergeGenreRows(before, edited, rebased.SmartRules);
                }
                const genres = retainGenreRules(work.SmartRules, rebased.SmartRules);
                replaceDocument(work, rebased);
                work.SmartRules = genres.rows;
                if (genres.redraw && redrawEditorGenres) { redrawEditorGenres(); }
                // Keep the live controls, including text still being typed. Their callbacks read the rebased working document.
                toast(t('common.saved'));
                invalidate('users');
                users().then(function (list) {
                    if (!current(state, revision) || state.person !== editor) { return; }
                    const savedRow = list.find(function (row) { return sameId(row.UserId, u.UserId); });
                    const row = state.page.querySelector('[data-user="' + normId(u.UserId) + '"]');
                    if (!savedRow || !row) { return; }
                    const names = tableNames(list);
                    row.querySelector('.bf-people-audio').textContent = peopleCell(savedRow, 'audio', names);
                    row.querySelector('.bf-people-subtitles').textContent = peopleCell(savedRow, 'subtitles', names);
                }).catch(function () {
                    if (current(state, revision) && state.person === editor) { showErrors(userError(t('common.savedReload')), box, []); }
                });
                if (state.choiceStatus) { state.choiceStatus.refresh(); }
            }).catch(function (err) {
                if (!current(state, revision) || state.person !== editor) { return; }
                if (committed) {
                    state.person = { id: editor.id, dirty: false };
                    clear(host);
                    personEditor(host, u, committedDocument, defaults, libs, { rows: overrideRows, error: overridesError });
                    append(host, hint(t('common.savedReload')));
                } else if (err.status === 409 && err.problem && (err.problem.Code || err.problem.code) === 'field-conflict') {
                    const problem = err.problem, UI = ui(), paths = problem.ConflictingPaths || problem.conflictingPaths || [];
                    const panel = UI.conflictPanel({ t: t, problem: problem, actor: conflictActor(problem), label: conflictLabel,
                        reload: function () {
                            return api.get(userPath(u.UserId)).then(function (fresh) {
                                if (!current(state, revision) || state.person !== editor) { return; }
                                const latest = personDoc(fresh), nextBase = clone(original), draft = clone(work);
                                paths.forEach(function (path) {
                                    UI.setFieldValue(nextBase, path, UI.fieldValue(latest, path));
                                    UI.setFieldValue(draft, path, UI.fieldValue(latest, path));
                                });
                                clear(host); personEditor(host, u, draft, defaults, libs, { rows: overrideRows, error: overridesError }, nextBase);
                            });
                        },
                        keep: function () { return save(true); },
                        error: function (failure) { showErrors(failure, box, claims); }
                    });
                    clear(box); box.classList.remove('hide'); box.appendChild(panel); panel.focus();
                } else { showErrors(err, box, claims); }
            }).finally(function () {
                saving = false;
                if (!current(state, revision) || state.person !== editor) { return; }
                busy(saveBtn, false);
                changed();
            });
        }

        append(host, [
            h('div', { class: 'bf-person-head' }, [
                h('h4', { class: 'bf-person-name', text: name }),
                detected ? h('span', { class: 'bf-detected', text: t('people.detected') }) : null
            ]),
            enabled.node,
            langRow,
            groupTitle(t('people.audioAndSubtitles')),
            tableHost,
            overridesHost,
            box,
            h('div', { class: 'bf-actions' }, [
                saveBtn,
                smallButton(t('common.close'), function () { togglePerson(u, null); }),
                copyBtn
            ]),
            copyHost
        ]);
        mountLibraryTable(tableHost, {
            settings: work, modeField: 'PerLibrary', defaults: defaults, libraries: libs, audience: 'person', onChange: changed,
            onMount: function (table, genres, redrawGenres) { editorTable = table; editorGenres = genres; redrawEditorGenres = redrawGenres; }
        });
        changed();
    }

    /* ---------- Collapsible groups (Advanced and Troubleshooting) ---------- */

    /**
     * Adds a collapsible group to host: BabelfinUI.disclosure, closed by default and remembered per group, or a plain titled section with
     * an older ui.js. fill(body) builds the content at once, or on the first opening when lazy is true.
     */
    function foldGroup(host, id, title, fill, lazy) {
        function safeFill(body) {
            try {
                fill(body);
            } catch (e) {
                console.error('[Babelfin] ' + id, e);
                showLoadError(body, e);
            }
        }
        const UI = ui();
        if (UI && typeof UI.disclosure === 'function') {
            const group = UI.disclosure({ title: title, level: 4, storageKey: FOLD_KEY + 'group.' + id, className: 'bf-group', id: 'bf-group-' + id, build: lazy ? safeFill : null });
            append(host, group.element);
            if (!lazy) {
                safeFill(group.body);
            }
            return group;
        }
        const body = h('div', { class: 'bf-group-body' });
        append(host, h('section', { class: 'bf-group', id: 'bf-group-' + id }, [groupTitle(title), body]));
        safeFill(body);
        return null;
    }

    /* ---------- Advanced: Apps, New users, Track recognition, Activity, Restore defaults ---------- */

    function renderAdvanced() {
        S.rendered.advanced = true;
        S.restorers = [];
        S.appsReady = null;
        const body = clear(q('#bfAdvanced'));
        foldGroup(body, 'apps', t('apps.title'), appsGroup);
        foldGroup(body, 'newUsers', t('template.group'), templateGroup);
        foldGroup(body, 'recognition', t('recognition.title'), recognitionGroup);
        append(body, restoreRow());
    }

    /** Restore shipped Apps and Track recognition values after confirmation; Save applies them. */
    function restoreRow() {
        const box = errorBox();
        const btn = button(t('advanced.restore'), function () {
            confirmAsync(t('advanced.restoreConfirm')).then(function (ok) {
                if (!ok) {
                    return null;
                }
                clearProblem(box);
                busy(btn, true);
                return Promise.all([shippedSettings(), S.appsReady]).then(function (res) {
                    busy(btn, false);
                    const shipped = res[0] || {};
                    (S.restorers || []).forEach(function (restore) { restore(shipped); });
                    paintSaveBar();
                }, function (err) {
                    busy(btn, false);
                    showErrors(err, box, []);
                });
            });
        });
        btn.id = 'bfRestoreDefaults';
        return h('div', { class: 'bf-restore' }, [h('div', { class: 'bf-actions' }, btn), box]);
    }

    /** The shipped mode of an app: its row in the shipped list, else the shipped "Apps not listed" mode. */
    function shippedAppMode(shipped, client) {
        const name = String(client || '').trim().toLowerCase();
        const row = (shipped.AppModes || []).find(function (a) { return a && String(a.Client || '').trim().toLowerCase() === name; });
        return enumName('AppModeKind', row ? row.Mode : shipped.UnknownAppMode) || 'Trust';
    }

    /** The per-app table, "Apps not listed", and the two playback switches. */
    function appsGroup(host) {
        const state = S, revision = S.revision;
        const cfg = S.config;
        const list = h('div', { class: 'bf-apps' });
        loading(list);
        S.appsReady = api.get('Babelfin/Admin/Apps').then(null, function (err) {
            if (err && err.status === 404) {
                return (cfg.AppModes || []).slice();
            }
            throw err;
        }).then(function (apps) {
            if (S !== state || state.revision !== revision || state.shown === false) { return; }
            clear(list);
            const modeChoices = APP_MODES.map(function (m) { return [m, t('apps.mode.' + m)]; });
            const rows = (apps || []).map(function (app) {
                const initial = enumName('AppModeKind', app.Mode) || 'Trust';
                const sel = dropdown(modeChoices, initial, t('apps.mode') + ' · ' + app.Client);
                const rememberInitial = typeof app.RememberPrePlayChoices === 'boolean' ? app.RememberPrePlayChoices
                    : /^(jellyfin web|jellyfin media player|jellyfin desktop|jellyfin android)$/i.test(app.Client);
                const remember = h('input', { type: 'checkbox', checked: rememberInitial, 'aria-label': t('apps.rememberPrePlay') + ' · ' + app.Client });
                const tr = h('tr', null, [h('th', { scope: 'row' }, [h('span', { text: app.Client }), fieldErrorBox()]), h('td', { 'data-label': t('apps.mode') }, sel), h('td', { 'data-label': t('apps.rememberPrePlay') }, remember)]);
                const rememberChanged = function () {
                    const value = remember.checked;
                    track('app.remember.' + String(app.Client).toLowerCase(), rememberInitial, value, function (fresh) {
                        fresh.AppModes = fresh.AppModes || [];
                        let row = fresh.AppModes.find(function (a) { return a && String(a.Client).trim().toLowerCase() === String(app.Client).trim().toLowerCase(); });
                        if (!row) { row = { Client: app.Client, Mode: initial }; fresh.AppModes.push(row); }
                        row.RememberPrePlayChoices = value;
                        row.SeenOnly = false;
                    });
                };
                remember.addEventListener('change', rememberChanged);
                S.restorers.push(function (shipped) {
                    const row = (shipped.AppModes || []).find(function (a) { return a && String(a.Client).trim().toLowerCase() === String(app.Client).trim().toLowerCase(); });
                    remember.checked = row && typeof row.RememberPrePlayChoices === 'boolean' ? row.RememberPrePlayChoices
                        : /^(jellyfin web|jellyfin media player|jellyfin desktop|jellyfin android)$/i.test(app.Client);
                    rememberChanged();
                });
                const changed = function () {
                    const v = sel.value;
                    track('app.' + String(app.Client).toLowerCase(), initial, v, function (fresh) {
                        fresh.AppModes = fresh.AppModes || [];
                        let r = fresh.AppModes.find(function (a) { return a && String(a.Client).trim().toLowerCase() === String(app.Client).trim().toLowerCase(); });
                        if (!r) {
                            r = { Client: app.Client, Mode: v, Seen: !!app.Seen, LastSeen: app.LastSeen || null };
                            fresh.AppModes.push(r);
                        }
                        r.Mode = v;
                        r.SeenOnly = false;
                        claim('appModes[' + fresh.AppModes.indexOf(r) + ']', tr);
                    });
                };
                sel.addEventListener('change', changed);
                S.restorers.push(function (shipped) {
                    sel.value = shippedAppMode(shipped, app.Client);
                    changed();
                });
                return tr;
            });
            append(list, rows.length ? table([t('apps.app'), [t('apps.mode'), t('apps.mode.info')], [t('apps.rememberPrePlay'), t('apps.rememberPrePlay.info')]], rows, 'bf-stack bf-apps-table') : hint(t('apps.none')));
        }, function (err) {
            if (!current(state, revision)) { return; }
            showLoadError(list, err);
        });
        const unknownInitial = enumName('AppModeKind', cfg.UnknownAppMode) || 'Trust';
        const unknown = dropdown(APP_MODES.map(function (m) { return [m, t('apps.mode.' + m)]; }), unknownInitial);
        const unknownChanged = function () {
            const v = unknown.value;
            track('UnknownAppMode', unknownInitial, v, function (fresh) { fresh.UnknownAppMode = v; });
        };
        unknown.addEventListener('change', unknownChanged);
        S.restorers.push(function (shipped) {
            unknown.value = enumName('AppModeKind', shipped.UnknownAppMode) || 'Trust';
            unknownChanged();
        });
        const unknownRow = row(t('apps.notListed'), unknown, null, t('apps.notListed.info'));
        claim('unknownAppMode', unknownRow);
        // Fix tracks after start: stored as Auto, On or Off; the box is ticked unless Off. Ticking it again keeps the last "on" value.
        const correctInitial = enumName('CorrectAfterStartMode', cfg.CorrectAfterStart) || 'Auto';
        let correctValue = correctInitial;
        let lastOn = correctInitial === 'Off' ? 'Auto' : correctInitial;
        const correctChanged = function () {
            const v = correctValue;
            track('CorrectAfterStart', correctInitial, v, function (fresh) { fresh.CorrectAfterStart = v; });
        };
        const correct = checkbox(t('option.correctAfterStart'), correctInitial !== 'Off', function (on) {
            correctValue = on ? lastOn : 'Off';
            correctChanged();
        }, t('option.correctAfterStart.info'));
        S.restorers.push(function (shipped) {
            correctValue = enumName('CorrectAfterStartMode', shipped.CorrectAfterStart) || 'Auto';
            if (correctValue !== 'Off') {
                lastOn = correctValue;
            }
            correct.input.checked = correctValue !== 'Off';
            correctChanged();
        });
        const compat = checkRow(t('option.compatibility'), cfg.CompatibilityMode, 'CompatibilityMode', function (fresh, v) { fresh.CompatibilityMode = v; }, t('option.compatibility.info'));
        S.restorers.push(function (shipped) { compat.set(!!shipped.CompatibilityMode); });
        append(host, [
            list,
            unknownRow,
            correct.node,
            compat.node
        ]);
    }

    function blankTemplate() {
        return {
            SchemaVersion: 1,
            UserId: EMPTY_GUID,
            Enabled: true,
            UseEverydayEverywhere: false,
            PerLibrary: null,
            HandleNextEpisode: false,
            Everyday: {},
            Libraries: [],
            SmartRules: [],
            Overrides: [],
            CollectionOrder: [],
            BurnedIn: [],
            Locks: {}
        };
    }

    function templateCore(doc) {
        return {
            Enabled: doc.Enabled !== false,
            UseEverydayEverywhere: !!doc.UseEverydayEverywhere,
            PerLibrary: doc.PerLibrary === true || doc.PerLibrary === false ? doc.PerLibrary : null,
            Everyday: doc.Everyday,
            Libraries: (doc.Libraries || []).filter(function (l) { return l && !sameValue(l.Settings, {}); }),
            SmartRules: doc.SmartRules || [],
            Overrides: doc.Overrides || [],
            CollectionOrder: doc.CollectionOrder || [],
            BurnedIn: doc.BurnedIn || [],
            Locks: doc.Locks
        };
    }

    /**
     * "New users start with": only "My languages" for new people; an empty list means they're detected from each person's
     * Jellyfin settings. The top table ("Defaults for everyone") already applies to new people. On save only the template's languages are
     * written into the settings read fresh; a template left with nothing in it is stored as none.
     */
    function templateGroup(host) {
        const state = S, revision = state.revision;
        loading(host);
        languages().then(function () {
            if (!current(state, revision)) { return; }
            clear(host);
            const existing = S.config.NewUserTemplate;
            const stored = existing && existing.Everyday && Array.isArray(existing.Everyday.Understood) ? existing.Everyday.Understood.slice() : [];
            const langHost = h('div', { class: 'bf-row-control' });
            const langRow = h('div', { class: 'bf-row' }, [h('div', { class: 'bf-row-label', text: uiWord('languages.title') }), langHost, fieldErrorBox()]);
            const UI = ui();
            if (UI && typeof UI.languageList === 'function') {
                UI.languageList(langHost, stored.slice(), {
                    t: S.uiT,
                    languages: S.languageList || [],
                    uiLanguage: S.lang,
                    label: uiWord('languages.title'),
                    emptyText: t('template.fromJellyfin'),
                    onChange: function (items) {
                        const value = items.slice();
                        track('NewUserTemplate', stored, value, function (fresh) {
                            const template = fresh.NewUserTemplate && typeof fresh.NewUserTemplate === 'object' ? fresh.NewUserTemplate : blankTemplate();
                            template.Everyday = template.Everyday || {};
                            template.Everyday.Understood = value.length ? value.slice() : null;
                            fresh.NewUserTemplate = sameValue(templateCore(template), templateCore(blankTemplate())) ? null : template;
                        });
                    }
                });
            } else {
                append(langHost, h('span', { text: stored.length ? languagesText(stored) : t('template.fromJellyfin') }));
            }
            append(host, [titleWithInfo('h5', 'bf-sub-title', t('template.title'), t('template.info')), langRow]);
            claim('newUserTemplate', langRow);
        }, function (err) {
            if (!current(state, revision)) { return; }
            showLoadError(host, err);
        });
    }

    /** Track recognition: the track-name words, the language codes and the server's default for audio tracks without a language. */
    function recognitionGroup(host) {
        append(host, h('h5', { class: 'bf-sub-title', text: t('tracknames.title') }));
        trackNamesGroup(host);
        append(host, [h('h5', { class: 'bf-sub-title', text: t('codes.title') }), hint(t('codes.help'))]);
        codesGroup(host);
        append(host, unlabelledRow());
    }

    function trackNamesGroup(host) {
        const kw = S.config.Keywords || {};
        append(host, [
            hint(t('tracknames.help')),
            h('div', { class: 'bf-kw-grid' }, KEYWORD_LISTS.map(function (k) {
                const lines = linesRow(t('tracknames.' + k), kw[k], 'Keywords.' + k, 'keywords.' + k, function (fresh, v) {
                    fresh.Keywords = fresh.Keywords || {};
                    fresh.Keywords[k] = v;
                });
                S.restorers.push(function (shipped) { lines.set(((shipped.Keywords || {})[k]) || []); });
                return lines.node;
            }))
        ]);
    }

    /** "Language codes": how a spelling found on tracks maps to a language (no language = not a language). */
    function codesGroup(host) {
        ensureDatalist();
        const initial = (S.config.LanguageAliases || []).map(function (a) { return { Raw: a.Raw || '', Code: a.Code || '' }; });
        const rows = [];
        const tbody = h('tbody');
        const box = h('div', { class: 'bf-codes' });
        const current = function () {
            return rows.filter(function (r) { return !r.removed && r.raw.value.trim(); }).map(function (r) {
                return { Raw: r.raw.value.trim(), Code: r.code.value.trim() };
            });
        };
        const changed = function () {
            const value = current();
            track('LanguageAliases', initial, value, function (fresh) {
                fresh.LanguageAliases = value;
                rows.filter(function (r) { return !r.removed && r.raw.value.trim(); }).forEach(function (r, i) { claim('languageAliases[' + i + ']', r.tr); });
            });
        };
        const removeRow = function (r) {
            r.removed = true;
            if (r.tr.parentNode) {
                r.tr.parentNode.removeChild(r.tr);
            }
        };
        const add = function (a) {
            const raw = h('input', { type: 'text', class: 'bf-input', 'aria-label': t('codes.raw') });
            raw.value = a.Raw || '';
            const code = h('input', { type: 'text', class: 'bf-input', list: 'bfLangList', placeholder: t('codes.notALanguage'), 'aria-label': t('codes.code') });
            code.value = a.Code || '';
            const r = { raw: raw, code: code, removed: false, tr: null };
            r.tr = h('tr', null, [
                h('td', { 'data-label': t('codes.raw') }, [raw, fieldErrorBox()]),
                h('td', { 'data-label': t('codes.code') }, code),
                h('td', { class: 'bf-cell-action' }, smallButton(t('common.remove'), function () {
                    removeRow(r);
                    changed();
                }))
            ]);
            raw.addEventListener('input', changed);
            code.addEventListener('input', changed);
            rows.push(r);
            append(tbody, r.tr);
        };
        initial.forEach(add);
        S.restorers.push(function (shipped) {
            rows.forEach(function (r) { if (!r.removed) { removeRow(r); } });
            (shipped.LanguageAliases || []).forEach(function (a) { add({ Raw: (a && a.Raw) || '', Code: (a && a.Code) || '' }); });
            changed();
        });
        append(box, [
            h('div', { class: 'bf-table-wrap' }, h('table', { class: 'bf-table bf-stack bf-codes-table' }, [
                h('thead', null, h('tr', null, [h('th', { text: t('codes.raw') }), h('th', { text: t('codes.code') }), h('th')])),
                tbody
            ])),
            h('div', { class: 'bf-actions' }, smallButton(t('codes.add'), function () { add({}); }))
        ]);
        append(host, box);
        claim('languageAliases', box);
    }

    /**
     * What audio tracks with no language are taken to be, for everyone (only the admin sets this): plain options, the stored rule selected, else the shipped one. Picking the shipped rule stores what the shipped settings store: nothing (they
     * leave it unset, so the engine's built-in rule applies).
     */
    function unlabelledRow() {
        // What is stored, as a dropdown value ('' when nothing is).
        const storedInitial = unlabelledValue(S.config.Everyday);
        const fallback = shippedUnlabelled();
        const shippedStored = shippedUnlabelledStored();
        const choices = [['Unknown', t('unlabelled.Unknown')], ['OriginalLanguage', t('unlabelled.OriginalLanguage')], ['OriginalWhenOnlyTrack', t('unlabelled.OriginalWhenOnlyTrack')]];
        if (storedInitial === 'LibraryUsualOriginalLanguage' || fallback === 'LibraryUsualOriginalLanguage') {
            choices.push(['LibraryUsualOriginalLanguage', t('unlabelled.LibraryUsualOriginalLanguage')]);
        }
        const same = function (a, b) { return String(a || '').toLowerCase() === String(b || '').toLowerCase(); };
        const changed = function (v) {
            const value = same(v, fallback) ? shippedStored : v;
            track('Everyday.UnlabelledAudio', storedInitial, value, function (fresh) {
                const code = langOf(value);
                everyday(fresh).UnlabelledAudio = code ? 'SpecificLanguage' : (value || null);
                everyday(fresh).UnlabelledAudioLanguage = code || null;
            });
        };
        const unlabelled = languageDropdown({
            choices: choices,
            common: [],
            value: storedInitial || fallback,
            label: t('field.UnlabelledAudio'),
            pickText: t('common.specificLanguage'),
            onChange: changed
        });
        S.restorers.push(function (shipped) {
            const value = unlabelledValue(shipped && shipped.Everyday) || BUILTIN_UNLABELLED;
            unlabelled.setValue(value);
            changed(value);
        });
        const r = row(t('field.UnlabelledAudio'), unlabelled.node, null, t('field.UnlabelledAudio.info'));
        claim('everyday.unlabelledAudio', r);
        return r;
    }

    /* ---------- Troubleshooting: Check a title, Activity ---------- */

    function renderTools() {
        S.rendered.tools = true;
        const body = clear(q('#bfTools'));
        foldGroup(body, 'check', t('check.title'), checkTool);
        foldGroup(body, 'activity', t('activity.title'), activityTool, true);
    }

    /* ---------- Back up and restore: its own section, last on the page ---------- */

    function renderBackup() {
        S.rendered.backup = true;
        const body = clear(q('#bfBackup'));
        try {
            backupTool(body);
        } catch (e) {
            console.error('[Babelfin] backup', e);
            showLoadError(body, e);
        }
    }

    function personSelect(firstText) {
        const sel = h('select', { class: 'bf-select', id: uid(), 'aria-label': t('common.person') });
        append(sel, option('', firstText || t('common.choosePerson')));
        (S.userList || []).forEach(function (u) { append(sel, option(u.UserId, u.Name)); });
        return sel;
    }

    /** The Jellyfin API as BabelfinUI.itemSearch reads it (GET /Items with the dashboard's sign-in). */
    function searchApi() {
        const UI = ui();
        return UI.api(baseUrl(), function () {
            const headers = authHeaders(false);
            delete headers.Accept;
            return headers;
        });
    }

    /**
     * "Check a title": pick a person, search for a show, season, episode or movie as that person, and see what plays for each episode or
     * movie (POST Admin/Simulate, then GET Admin/Simulate/{jobId} until it is done): at most CHECK_ROWS rows.
     */
    function checkTool(host) {
        const state = S, revision = state.revision;
        loading(host);
        users().then(function () {
            if (!current(state, revision)) { return; }
            clear(host);
            const UI = ui();
            const person = personSelect();
            const searchHost = h('div', { class: 'bf-check-search' });
            const status = h('p', { class: 'bf-hint bf-check-status', role: 'status', 'aria-live': 'polite' });
            const box = errorBox();
            const results = h('div', { class: 'bf-check-results' });
            S.check = { seq: 0, status: status, box: box, results: results };
            const paintSearch = function () {
                if (state.searchDetach) { state.searchDetach(); state.searchDetach = null; }
                stopCheck();
                clear(searchHost);
                clear(results);
                clearProblem(box);
                status.textContent = '';
                if (!person.value) {
                    append(searchHost, hint(t('check.choosePerson')));
                    return;
                }
                if (!UI || typeof UI.itemSearch !== 'function' || typeof UI.api !== 'function') {
                    append(searchHost, hint(t('people.needsUpdate')));
                    return;
                }
                const userId = person.value;
                const requestApi = searchApi();
                let searchGeneration = 0;
                const search = UI.itemSearch(searchHost, {
                    t: S.uiT,
                    api: { getJSON: function () {
                        const generation = searchGeneration;
                        return requestApi.getJSON.apply(requestApi, arguments).then(function (answer) {
                            if (!current(state, revision) || generation !== searchGeneration || person.value !== userId) {
                                const aborted = new Error('Search was cleared'); aborted.name = 'AbortError'; throw aborted;
                            }
                            return answer;
                        }, function (error) {
                            if (!current(state, revision) || generation !== searchGeneration || person.value !== userId) {
                                const aborted = new Error('Search was cleared'); aborted.name = 'AbortError'; throw aborted;
                            }
                            throw error;
                        });
                    } },
                    userId: userId,
                    types: CHECK_TYPES,
                    label: t('check.search'),
                    onPick: function (item) {
                        search.clear();
                        runCheck(userId, item);
                    }
                });
                // Consume Escape before Jellyfin's document Back shortcut. First clear, then leave the search.
                const input = search.element.querySelector('input');
                const onEscape = function (event) {
                    if (!current(state, revision) || event.key !== 'Escape' || event.target !== input) { return; }
                    event.preventDefault();
                    event.stopImmediatePropagation();
                    if (input.value) {
                        searchGeneration++;
                        search.clear();
                    } else {
                        person.focus();
                    }
                };
                const detach = listen(state, document, 'keydown', onEscape, true);
                state.searchDetach = detach;
            };
            person.addEventListener('change', paintSearch);
            append(host, [row(t('common.person'), person), searchHost, status, box, results]);
            paintSearch();
        }, function (err) {
            if (!current(state, revision)) { return; }
            showLoadError(host, err);
        });
    }

    function stopCheck() {
        if (S.timers.check) {
            clearTimeout(S.timers.check);
            S.timers.check = null;
        }
        if (S.check) {
            S.check.seq++;
        }
    }

    function runCheck(userId, item) {
        const state = S, c = state.check;
        stopCheck();
        const seq = c.seq;
        clear(c.results);
        clearProblem(c.box);
        c.status.textContent = t('check.running', { name: itemLabel(item) });
        api.post('Babelfin/Admin/Simulate', { UserId: userId, ItemId: item.Id }).then(function (res) {
            if (current(state) && state.check === c && seq === c.seq) {
                pollCheck(c, seq, res && (res.JobId || res.jobId));
            }
        }, function (err) {
            if (!current(state) || state.check !== c || seq !== c.seq) {
                return;
            }
            c.status.textContent = '';
            showErrors(err.status === 409 ? userError(t('check.busy')) : err, c.box, []);
        });
    }

    function pollCheck(c, seq, jobId) {
        api.get('Babelfin/Admin/Simulate/' + encodeURIComponent(jobId)).then(function (job) {
            if (!S || !S.shown || S.check !== c || seq !== c.seq) {
                return;
            }
            if (!job || !job.Done) {
                S.timers.check = setTimeout(function () { pollCheck(c, seq, jobId); }, CHECK_POLL_MS);
                return;
            }
            S.timers.check = null;
            c.status.textContent = '';
            if (job.Error) {
                showErrors(userError(job.Error), c.box, []);
            }
            renderCheckRows(c, job.Rows || []);
        }, function (err) {
            if (!S || !S.shown || S.check !== c || seq !== c.seq) {
                return;
            }
            c.status.textContent = '';
            showErrors(err.status === 404 ? userError(t('check.expired')) : err, c.box, []);
        });
    }

    function checkTitle(r) {
        if (!r.SeriesName) {
            return r.Name || '';
        }
        const numbered = r.SeasonNumber !== null && r.SeasonNumber !== undefined && r.EpisodeNumber !== null && r.EpisodeNumber !== undefined;
        return r.SeriesName + (numbered ? ' S' + String(r.SeasonNumber).padStart(2, '0') + 'E' + String(r.EpisodeNumber).padStart(2, '0') : '') + ' · ' + (r.Name || '');
    }

    function renderCheckRows(c, rows) {
        clear(c.results);
        if (!rows.length) {
            append(c.results, hint(t('check.none')));
            return;
        }
        const shown = rows.slice(0, CHECK_ROWS);
        const trs = shown.map(function (r) {
            const audio = r.AudioIndex === null || r.AudioIndex === undefined ? '—' : (r.AudioLanguage ? langName(r.AudioLanguage) : t('common.unknownLanguage'));
            const kind = r.SubtitleKind && S.t.has('kind.' + r.SubtitleKind) ? ' · ' + t('kind.' + r.SubtitleKind) : '';
            const subtitles = r.SubtitleIndex === null || r.SubtitleIndex === undefined
                ? t(r.SubtitlesWanted ? 'check.noneFound' : 'check.off')
                : (r.SubtitleLanguage ? langName(r.SubtitleLanguage) : t('common.unknownLanguage')) + kind;
            // Applied (or the retired test mode) means Babelfin chose the tracks; anything else leaves them to the video and Jellyfin.
            const chosen = r.Outcome === 'Applied' || r.Outcome === 'DryRun';
            const mark = t(chosen ? 'check.chosen' : 'check.videoDefault');
            const why = r.Explanation
                ? h('details', { class: 'bf-why' }, [h('summary', { text: mark }), h('div', { text: r.Explanation })])
                : h('span', { text: mark });
            return h('tr', { 'data-outcome': chosen ? 'chosen' : 'video' }, [
                h('td', { class: 'bf-check-name', text: checkTitle(r) }),
                h('td', { text: audio, 'data-label': t('libraries.audio') }),
                h('td', { text: subtitles, 'data-label': t('libraries.subtitles') }),
                h('td', { 'data-label': t('check.why') }, why)
            ]);
        });
        append(c.results, [
            rows.length > shown.length ? hint(t('check.showing', { shown: shown.length, total: rows.length })) : null,
            table([t('common.title'), t('libraries.audio'), t('libraries.subtitles'), t('check.why')], trs, 'bf-stack bf-check-table')
        ]);
    }

    /** Activity: the newest entries (ACTIVITY_PAGE at a time, "Show more" for more), for everyone or one person. Loaded when opened. */
    function activityTool(host) {
        let limit = ACTIVITY_PAGE;
        const person = personSelect(t('activity.everyone'));
        const result = h('div', { class: 'bf-activity' });
        const more = smallButton(t('activity.more'), function () {
            limit = Math.min(limit + ACTIVITY_PAGE, ACTIVITY_MAX);
            load();
        });
        more.classList.add('hide');
        const load = function () {
            busy(more, true);
            api.get('Babelfin/Admin/Activity', { limit: limit, userId: person.value || null }).then(function (entries) {
                busy(more, false);
                const list = entries || [];
                clear(result);
                more.classList.toggle('hide', list.length < limit || limit >= ACTIVITY_MAX);
                if (!list.length) {
                    append(result, hint(t('activity.none')));
                    return;
                }
                append(result, table([t('activity.when'), t('activity.kind'), t('common.person'), t('activity.what')], list.map(function (e) {
                    return h('tr', null, [
                        h('td', { class: 'bf-activity-when', text: fmtDate(e.At) }),
                        h('td', { text: S.t.has('activityKind.' + e.Kind) ? t('activityKind.' + e.Kind) : e.Kind, 'data-label': t('activity.kind') }),
                        h('td', { text: e.UserId ? userName(e.UserId) : '—', 'data-label': t('common.person') }),
                        h('td', { text: e.Text || '', 'data-label': t('activity.what') })
                    ]);
                }), 'bf-stack bf-activity-table'));
            }, function (err) {
                busy(more, false);
                more.classList.add('hide');
                showLoadError(result, err);
            });
        };
        person.addEventListener('change', function () {
            limit = ACTIVITY_PAGE;
            load();
        });
        append(host, [
            hint(t('activity.sessionHint')),
            h('div', { class: 'bf-inline' }, [person, smallButton(t('common.refresh'), load)]),
            result,
            h('div', { class: 'bf-actions' }, more)
        ]);
        loading(result);
        users().then(function () {
            while (person.options.length > 1) {
                person.removeChild(person.options[person.options.length - 1]);
            }
            (S.userList || []).forEach(function (u) { append(person, option(u.UserId, u.Name)); });
        }, function () { /* the filter offers everyone only */ }).then(load);
    }

    /** Back up and restore: download every setting, or restore from a file after seeing what would change. */
    function backupTool(host) {
        const state = S;
        let previewSequence = 0;
        const box = errorBox();
        const preview = h('div');
        const exportBtn = button(t('backup.export'), function () {
            clearProblem(box);
            busy(exportBtn, true);
            api.blob('Babelfin/Admin/Export', { allowPartial: true }, 'babelfin-settings.json').then(function (file) {
                return file.blob.text().then(function (text) {
                    if (!current(state)) { return; }
                    let exported;
                    try { exported = JSON.parse(text); }
                    catch (error) { throw userError(t('backup.notJson')); }
                    saveBlob(file.blob, file.name);
                    const skipped = exported.SkippedUsers || exported.skippedUsers || [];
                    if (exported.Complete === false || exported.complete === false || skipped.length) {
                        showErrors(userError(incompleteBackup(skipped)), box, []);
                    }
                    busy(exportBtn, false);
                });
            }).catch(function (err) {
                if (!current(state)) { return; }
                busy(exportBtn, false);
                showErrors(err, box, []);
            });
        });
        const file = h('input', { type: 'file', accept: '.json,application/json', class: 'bf-file', id: uid() });
        file.addEventListener('change', function () {
            const sequence = ++previewSequence;
            clearProblem(box);
            clear(preview);
            const f = file.files && file.files[0];
            if (!f) {
                return;
            }
            const reader = new FileReader();
            reader.onload = function () {
                if (!current(state) || sequence !== previewSequence) { return; }
                let doc;
                try {
                    doc = JSON.parse(String(reader.result || ''));
                } catch (e) {
                    showErrors(userError(t('backup.notJson')), box, []);
                    return;
                }
                api.post('Babelfin/Admin/Import', doc, { dryRun: true }).then(function (result) {
                    if (current(state) && sequence === previewSequence) { renderImportPreview(preview, result, doc, box); }
                }, function (err) {
                    if (current(state) && sequence === previewSequence) { showErrors(err, box, []); }
                });
            };
            reader.onerror = function () { if (current(state) && sequence === previewSequence) { showErrors(userError(t('backup.readError')), box, []); } };
            reader.readAsText(f);
        });
        append(host, [
            h('div', { class: 'bf-actions' }, exportBtn),
            row(t('backup.import'), file, t('backup.importHelp')),
            box,
            preview
        ]);
        resetTool(host);
    }

    function accountProblems(rows) {
        return rows.map(function (account) {
            const name = account.UserName || account.userName || account.UserId || account.userId || '—';
            const reason = account.Reason || account.reason || '';
            return name + (reason ? ': ' + reason : '');
        });
    }

    function incompleteBackup(skipped) {
        const names = accountProblems(skipped).join('; ') || '—';
        const text = t('backup.incomplete', { names: names });
        return text === 'backup.incomplete' ? 'Incomplete backup. Skipped accounts: ' + names + '.' : text;
    }

    /**
     * "Reset all settings": after a confirmation, POST Admin/ResetAll resets readable accounts and the server settings.
     * It saves a recovery export first and reports every skipped or failed account. The page then reads the fresh settings.
     */
    function resetTool(host) {
        const state = S;
        const box = errorBox();
        const done = h('div', { class: 'bf-reset-done', role: 'status' });
        const resetBtn = button(t('reset.button'), function () {
            const revision = state.revision;
            clearProblem(box);
            clear(done);
            confirmAsync(t('reset.confirmText'), { title: t('reset.confirmTitle'), okText: t('reset.ok'), danger: true }).then(function (ok) {
                if (!ok || !current(state, revision)) {
                    return null;
                }
                busy(resetBtn, true);
                return api.post('Babelfin/Admin/ResetAll', undefined, { allowPartial: true }).then(function (res) {
                    if (!current(state, revision)) { return; }
                    busy(resetBtn, false);
                    return showResult(res);
                }, function (err) {
                    if (!current(state, revision)) { return; }
                    busy(resetBtn, false);
                    const result = err && err.problem;
                    if (result && (result.FailedNames || result.failedNames || result.SkippedUsers || result.skippedUsers || result.FailedUsers || result.failedUsers || result.AdminReset === false || result.adminReset === false)) {
                        return showResult(result);
                    }
                    showErrors(userError(t('reset.failed', { message: problemOf(err).message })), box, []);
                });
            });
        }, 'button-delete');
        function showResult(res) {
            const skipped = (res && (res.SkippedUsers || res.skippedUsers)) || [];
            const details = (res && (res.FailedUsers || res.failedUsers)) || [];
            const failed = accountProblems(skipped).concat(accountProblems(details));
            if (res && (res.AdminReset === false || res.adminReset === false)) {
                const reason = res.AdminResetReason || res.adminResetReason || '';
                failed.push(t('backup.adminSame') + (reason ? ' ' + reason : ''));
            }
            ((res && (res.FailedNames || res.failedNames)) || []).forEach(function (name) {
                if (!skipped.concat(details).some(function (account) { return (account.UserName || account.userName || account.UserId || account.userId) === name; })) { failed.push(name); }
            });
            const path = (res && (res.BackupPath || res.backupPath || res.BackupFile || res.backupFile)) || '';
            append(done, h('p', {
                class: 'bf-alert ' + (failed.length ? 'bf-alert-error' : 'bf-alert-ok'),
                role: failed.length ? 'alert' : 'status',
                text: failed.length ? t('reset.partial', { names: failed.join(', '), path: path }) : t('reset.done', { path: t('backup.relativePath', { path: path }) })
            }));
            if (skipped.length) { append(done, h('p', { class: 'bf-alert bf-alert-error', role: 'alert', text: incompleteBackup(skipped) })); }
            return refreshAfterMutation(done);
        }
        append(host, [
            h('h5', { class: 'bf-sub-title bf-reset-title', text: t('reset.title') }),
            hint(t('reset.help')),
            h('div', { class: 'bf-actions' }, resetBtn),
            box,
            done
        ]);
    }

    // A committed reset/import invalidates every editor, even when the following read fails.
    // A retry repeats only the reads; it must never submit the mutation again.
    function refreshAfterMutation(host) {
        const state = S;
        state.revision = (state.revision || 0) + 1;
        if (state.saveOperation) { state.saveOperation.finish(); }
        const revision = state.revision;
        stopCheck();
        if (state.searchDetach) { state.searchDetach(); state.searchDetach = null; }
        state.cache = {};
        state.pending = {};
        clearProblem(q('#bfSaveError'));
        state.person = null;
        state.claims = [];
        paintSaveBar();
        const enabled = q('#bfEnabled');
        enabled.disabled = true;
        ['#bfLibraries', '#bfPeople', '#bfAdvanced'].forEach(function (selector) { loading(q(selector)); });
        const status = h('div', { role: 'status', 'aria-busy': 'true' }, hint(t('common.loading')));
        append(host, status);
        function refresh() {
            clear(status);
            status.setAttribute('aria-busy', 'true');
            append(status, hint(t('common.loading')));
            return api.get('Babelfin/Admin/Settings').then(function (cfg) {
                if (!current(state, revision)) { return; }
                state.config = cfg;
                state.expectedBase = null;
                enabled.disabled = false;
                return Promise.all([renderSettings(), renderPeople()]).then(function () {
                    if (current(state, revision)) { status.remove(); }
                });
            }).catch(function (err) {
                if (!current(state, revision)) { return; }
                clear(status);
                status.setAttribute('aria-busy', 'false');
                append(status, [hint(t('page.loadError')), smallButton(S.uiT('app.retry'), refresh)]);
                console.warn('[Babelfin] refresh after reset/import failed', err);
            });
        }
        return refresh();
    }

    function renderImportPreview(host, result, doc, box) {
        clear(host);
        const admin = result.Admin || result.admin || {};
        const people = result.Users || result.users || [];
        const warnings = result.Warnings || result.warnings || [];
        const applied = result.Applied || result.applied;
        const sections = admin.ChangedSections || admin.changedSections || [];
        append(host, [
            h('h5', { class: 'bf-sub-title', text: applied ? t('backup.appliedTitle') : t('backup.previewTitle') }),
            h('p', { text: (admin.WillChange || admin.willChange) ? t('backup.adminChanges', { sections: sections.join(', ') }) : t('backup.adminSame') }),
            people.length ? table([t('common.person'), t('backup.matchedBy'), t('backup.action'), t('backup.reason')], people.map(function (u) {
                const action = u.Action || u.action || '';
                const matched = u.MatchedBy || u.matchedBy;
                return h('tr', null, [
                    h('td', { text: u.UserName || u.userName || '' }),
                    h('td', { text: matched ? t('backup.matched.' + matched) : '—' }),
                    h('td', { text: t('backup.action.' + action) }),
                    h('td', { text: u.Reason || u.reason || '' })
                ]);
            })) : hint(t('backup.noPeople')),
            warnings.length ? h('ul', { class: 'bf-warnings' }, warnings.map(function (w) { return h('li', { text: w }); })) : null
        ]);
        const storageBlocked = result.StorageBlocked === true || result.storageBlocked === true;
        if (storageBlocked) {
            const blockedPeople = people.filter(function (person) { return (person.Action || person.action) === 'skip' && (person.Reason || person.reason); });
            const names = blockedPeople.map(function (person) { return person.UserName || person.userName || person.UserId || person.userId; }).filter(Boolean);
            append(host, hint(t('backup.storageBlocked', { name: names.join(', ') || t('backup.blockedPeople') })));
        }
        const blocked = result.CanApply === false || result.canApply === false || result.StorageBlocked === true || result.storageBlocked === true;
        const applicable = !blocked && ((admin.WillChange || admin.willChange) || people.some(function (person) {
            const action = person.Action || person.action;
            return action === 'create' || action === 'update';
        }));
        if (applied || !applicable) {
            return;
        }
        const state = S, revision = state.revision;
        const apply = button(t('backup.apply'), function () {
            confirmAsync(t('backup.applyConfirm')).then(function (ok) {
                if (!ok || !current(state, revision)) {
                    return;
                }
                clearProblem(box);
                busy(apply, true);
                api.post('Babelfin/Admin/Import', doc, { dryRun: false }).then(function (res) {
                    if (!current(state, revision)) { return; }
                    busy(apply, false);
                    renderImportPreview(host, res, doc, box);
                    toast(t('backup.applied'));
                    return refreshAfterMutation(host);
                }).then(null, function (err) {
                    if (!current(state, revision)) { return; }
                    busy(apply, false);
                    const problem = err && err.problem;
                    const report = problem && (problem.Import || problem.import || problem.Preview || problem.preview);
                    if (report && (report.Applied !== undefined || report.applied !== undefined)) {
                        // A conflict may have committed earlier targets. Show that report and never offer a blind retry.
                        const outcome = Object.assign({}, report, { CanApply: false });
                        renderImportPreview(host, outcome, doc, box);
                        showErrors(err, box, []);
                        if (report.Applied === true || report.applied === true) { return refreshAfterMutation(host); }
                    } else {
                        showErrors(err, box, []);
                    }
                });
            });
        }, 'button-submit');
        append(host, h('div', { class: 'bf-actions' }, apply));
    }

    /* ---------- page wiring ---------- */

    const FOLDS = { advanced: renderAdvanced, tools: renderTools, backup: renderBackup };

    function onFold(details) {
        const name = details.getAttribute('data-fold');
        writeStore(FOLD_KEY + name, details.open ? '1' : '0');
        if (details.open && !S.rendered[name] && FOLDS[name]) {
            FOLDS[name]();
        }
    }

    function translatePage(page) {
        page.querySelectorAll('[data-i18n]').forEach(function (node) {
            const key = node.getAttribute('data-i18n');
            if (S.t.has(key)) {
                node.textContent = t(key);
            }
        });
    }

    function baseUrl() {
        return ApiClient.getUrl('Babelfin').replace(/\/Babelfin$/i, '');
    }

    /** Every temporary page handler has a matching detach; cached pages can be shown again. */
    function listen(state, target, name, handler, capture) {
        target.addEventListener(name, handler, capture);
        let attached = true;
        const detach = function () {
            if (attached) { target.removeEventListener(name, handler, capture); attached = false; }
        };
        state.cleanups.push(detach);
        return detach;
    }

    function hasChanges(state) {
        return current(state) && (Object.keys(state.pending).length > 0 || !!(state.person && state.person.dirty));
    }

    function confirmLeave(state) {
        if (!hasChanges(state)) { return Promise.resolve(true); }
        if (state.leaving) { return state.leaving; }
        state.leaving = confirmAsync(state.uiT('app.discardConfirm.text'), {
            title: state.uiT('app.discardConfirm.title'), okText: state.uiT('app.discard'), danger: true
        }).then(function (ok) {
            state.leaving = null;
            if (!current(state)) { return false; }
            if (ok) {
                state.pending = {};
                if (state.person) { state.person.dirty = false; }
                paintSaveBar();
            }
            return ok;
        });
        return state.leaving;
    }

    // All route sources share one decision and retain the first requested destination.
    function requestLeave(state, resume, ready) {
        if (state.routePending) { return state.routePending; }
        state.routePending = Promise.resolve(ready).then(function () {
            return current(state) ? confirmLeave(state) : false;
        }).then(function (ok) {
            // A second native event may be rolling back while the existing modal is answered.
            return Promise.resolve(state.restoreWait).then(function () {
                if (ok && current(state)) { return resume(); }
            });
        }).then(function (value) {
            state.routePending = null;
            return value;
        }, function (error) {
            state.routePending = null;
            throw error;
        });
        return state.routePending;
    }

    function guardLegacyHistory(state) {
        const history = window.history, location = window.location;
        if (!history || !location || typeof history.go !== 'function') { return; }
        const originalGo = history.go;
        function indexOf(value) {
            const index = value && value.idx;
            return typeof index === 'number' && isFinite(index) ? index : null;
        }
        const anchor = { url: location.href, index: indexOf(history.state), length: history.length };
        if (anchor.index === null) { anchor.index = history.length - 1; }
        let observedIndex = anchor.index, observedURL = anchor.url, pending = null, timer = null, resolveRestore = null, returning = false;
        function entryIndex() {
            const stored = indexOf(history.state);
            if (stored !== null) { return stored; }
            if (location.href === anchor.url) { return anchor.index; }
            return location.href === observedURL ? observedIndex : null;
        }
        function suppress(event) { event.preventDefault(); event.stopImmediatePropagation(); }
        function restored() {
            observedIndex = anchor.index;
            observedURL = anchor.url;
            if (resolveRestore) { const resolve = resolveRestore; resolveRestore = null; resolve(); }
        }
        function restore() {
            if (!resolveRestore) { state.restoreWait = new Promise(function (resolve) { resolveRestore = resolve; }); }
            if (timer !== null || returning) { return; }
            timer = setTimeout(function () {
                timer = null;
                if (!current(state) || !pending) { restored(); return; }
                const index = entryIndex();
                if (location.href === anchor.url && index === anchor.index) { restored(); return; }
                returning = true;
                originalGo.call(history, anchor.index - (index === null ? observedIndex : index));
            }, 0);
        }
        function onHistory(event) {
            const index = entryIndex();
            if (event.type === 'popstate' && returning && index !== null) { returning = false; }
            if (location.href === anchor.url && index === anchor.index && (pending || returning)) {
                suppress(event);
                if (event.type === 'hashchange') { returning = false; }
                restored();
                return;
            }
            if (!hasChanges(state) && !pending) { return; }
            suppress(event);
            // Hash events carry the actual first URL even if several setters ran before their tasks were delivered.
            if (event.type === 'hashchange' && pending && pending.kind === 'hash' && !pending.hashSeen) {
                pending.url = event.newURL;
                pending.hashSeen = true;
            }
            if (event.type === 'hashchange' && event.newURL !== location.href) { return; }
            if (location.href === anchor.url && index === anchor.index) { return; }
            let targetIndex = index, kind = 'traverse';
            if (targetIndex === null) {
                // A direct hash assignment creates a new entry with null state. Existing Jellyfin entries carry idx.
                targetIndex = history.length > anchor.length ? observedIndex + 1 : anchor.index - 1;
                kind = history.length > anchor.length ? 'hash' : 'traverse';
            }
            observedIndex = targetIndex;
            observedURL = location.href;
            if (!pending) {
                const destination = pending = { url: location.href, index: targetIndex, kind: kind, hashSeen: event.type === 'hashchange' };
                restore();
                requestLeave(state, function () {
                    pending = null;
                    if (timer !== null) { clearTimeout(timer); timer = null; }
                    if (destination.kind === 'hash') { location.href = destination.url; }
                    else { originalGo.call(history, destination.index - anchor.index); }
                }, state.restoreWait).then(function () { pending = null; }, function () { pending = null; });
            } else {
                restore();
            }
        }
        // Window-target listeners may run after an older host listener. Hold its pagehide until rollback/confirmation.
        state.legacyLeave = function () {
            if (!hasChanges(state)) { return false; }
            if (location.href !== anchor.url && !pending) {
                onHistory({ type: 'popstate', preventDefault: function () {}, stopImmediatePropagation: function () {} });
            }
            return true;
        };
        state.legacyReenter = function (page) {
            if (!hasChanges(state) || location.href !== anchor.url) { return false; }
            if (state.page !== page) {
                // Jellyfin may rebuild the configuration view during rollback. Keep the live controls and their draft closures.
                const previous = state.page;
                clear(page);
                while (previous.firstChild) { page.appendChild(previous.firstChild); }
                state.page = page;
                page.setAttribute('data-bf-wired', '1');
                state.cleanups.push(function () { page.removeAttribute('data-bf-wired'); });
            }
            return true;
        };
        listen(state, window, 'popstate', onHistory, true);
        listen(state, window, 'hashchange', onHistory, true);
        ['back', 'forward', 'go'].forEach(function (name) {
            const original = history[name];
            if (typeof original !== 'function') { return; }
            const wrapped = function () {
                const args = arguments;
                if (!hasChanges(state)) { return original.apply(history, args); }
                return requestLeave(state, function () { return original.apply(history, args); });
            };
            history[name] = wrapped;
            state.cleanups.push(function () { if (history[name] === wrapped) { history[name] = original; } });
        });
        state.cleanups.push(function () {
            if (timer !== null) { clearTimeout(timer); }
            pending = null;
            state.legacyLeave = null;
            state.legacyReenter = null;
            restored();
        });
    }

    function guardNavigation(state) {
        listen(state, window, 'beforeunload', function (event) {
            if (hasChanges(state)) { event.preventDefault(); event.returnValue = ''; }
        });
        // Jellyfin's delegated sidebar click may destroy the view before Navigation API fires. Stop it in capture, then replay only on Discard.
        listen(state, document, 'click', function (event) {
            if (!hasChanges(state) || event.defaultPrevented || (event.button !== undefined && event.button !== 0)
                || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) { return; }
            let link = event.target;
            while (link && link !== document && !(link.tagName && link.tagName.toLowerCase() === 'a')) { link = link.parentNode; }
            if (!link || link === document || link.hasAttribute('download') || (link.getAttribute('target') && link.getAttribute('target') !== '_self')) { return; }
            const href = link.getAttribute('href');
            if (!href || href === '#' || !window.location) { return; }
            let destination, currentURL;
            try { destination = new URL(href, window.location.href); currentURL = new URL(window.location.href); }
            catch (error) { return; }
            if (destination.origin !== currentURL.origin || destination.href === currentURL.href
                || (href.charAt(0) === '#' && href.indexOf('#/') !== 0)) { return; }
            event.preventDefault();
            event.stopImmediatePropagation();
            const backdrop = document.querySelector('.mdl-layout__obfuscator.is-visible, .MuiDrawer-root .MuiBackdrop-root, .mainDrawer-background, .drawerBackdrop');
            if (backdrop) { backdrop.click(); }
            requestLeave(state, function () { link.click(); });
        }, true);
        // Jellyfin exposes some router actions through Emby.Page; guard callers that don't dispatch a link click too.
        const router = window.Emby && window.Emby.Page;
        if (router) {
            ['show', 'back'].forEach(function (name) {
                const original = router[name];
                if (typeof original !== 'function') { return; }
                const wrapped = function () {
                    const receiver = this, args = arguments;
                    if (!hasChanges(state)) { return original.apply(receiver, args); }
                    return requestLeave(state, function () { return original.apply(receiver, args); });
                };
                router[name] = wrapped;
                state.cleanups.push(function () { if (router[name] === wrapped) { router[name] = original; } });
            });
        }
        // Browser history traversal does not call Emby.Page.back; Navigation API can cancel it before the host router sees it.
        const navigation = window.navigation;
        if (navigation && navigation.addEventListener) {
            listen(state, navigation, 'navigate', function (event) {
                if (!event.cancelable || !hasChanges(state) || event.downloadRequest != null) { return; }
                event.preventDefault();
                requestLeave(state, function () {
                    if (event.navigationType === 'traverse') { navigation.traverseTo(event.destination.key); }
                    else { navigation.navigate(event.destination.url, { history: event.navigationType === 'replace' ? 'replace' : 'push' }); }
                });
            });
        } else {
            guardLegacyHistory(state);
        }
    }

    function wire(page) {
        if (page.getAttribute('data-bf-wired') === '1') {
            return;
        }
        page.setAttribute('data-bf-wired', '1');
        const state = S;
        state.cleanups.push(function () { page.removeAttribute('data-bf-wired'); });
        const enabled = page.querySelector('#bfEnabled');
        listen(state, enabled, 'change', function () {
            if (!current(state)) {
                return;
            }
            const v = enabled.checked;
            track('Enabled', S.enabledInitial, v, function (fresh) { fresh.Enabled = v; });
        });
        listen(state, page.querySelector('#bfSave'), 'click', function () {
            if (current(state)) {
                savePage();
            }
        });
        listen(state, page.querySelector('#bfDiscard'), 'click', function () {
            if (current(state)) {
                discardPage();
            }
        });
        page.querySelectorAll('details[data-fold]').forEach(function (details) {
            listen(state, details, 'toggle', function () {
                if (current(state)) {
                    onFold(details);
                }
            });
        });
    }

    function show(page, options) {
        if (S && S.legacyReenter && S.legacyReenter(page)) { return; }
        if (S && S.page === page && S.shown) {
            return;
        }
        hide();
        S = {
            page: page,
            shown: true,
            cache: {},
            pending: {},
            claims: [],
            rendered: {},
            timers: {},
            status: options && options.status,
            config: null,
            t: identityT(),
            uiT: identityT(),
            lang: 'en',
            languageList: [],
            userList: [],
            shipped: null,
            restorers: [],
            appsReady: null,
            person: null,
            cleanups: [],
            revision: 0,
            saving: false
        };
        const state = S;
        guardNavigation(state);
        q('#bfEnabled').disabled = true;
        // A cached page may retain a disabled Save button after an abandoned save.
        busy(q('#bfSave'), false);
        clearProblem(q('#bfSaveError'));
        paintSaveBar();
        Dashboard.showLoadingMsg();
        const lang = window.BabelfinI18n ? window.BabelfinI18n.detectLanguage(ApiClient.getCurrentUserId()) : (navigator.language || 'en');
        const version = state.status && (state.status.version || state.status.Version);
        const loadT = function (folder) {
            return window.BabelfinI18n
                ? window.BabelfinI18n.load(baseUrl(), lang, folder, version).then(null, function () { return null; })
                : Promise.resolve(null);
        };
        // The shipped settings are read up front (a dropdown outside the table selects them when nothing is stored); without them the
        // built-in values stand in and "Restore defaults" asks again when used.
        const shipped = shippedSettings().then(null, function (err) {
            console.warn('[Babelfin] the shipped settings could not be loaded', err);
            return null;
        });
        Promise.all([loadT('i18n-admin'), loadT('i18n'), api.get('Babelfin/Admin/Settings'), languages(), shipped]).then(function (res) {
            if (!current(state)) {
                return;
            }
            S.t = res[0] || identityT();
            S.uiT = combineT(S.t, res[1]);
            S.lang = lang;
            S.config = res[2] || {};
            translatePage(page);
            const status = state.status;
            const started = !status || (status.wrapped !== false && status.Wrapped !== false);
            q('#bfNotStarted').classList.toggle('hide', started);
            q('#bfLoadError').classList.add('hide');
            wire(page);
            renderSettings();
            renderPeople();
            page.querySelectorAll('details[data-fold]').forEach(function (details) {
                details.open = readStore(FOLD_KEY + details.getAttribute('data-fold')) === '1';
                if (details.open) {
                    onFold(details);
                }
            });
            Dashboard.hideLoadingMsg();
        }, function (err) {
            if (!current(state)) { return; }
            Dashboard.hideLoadingMsg();
            console.error('[Babelfin] the settings could not be loaded', err);
            q('#bfLoadError').classList.remove('hide');
        });
    }

    function hide(page) {
        if (!S || (page && S.page !== page)) {
            return;
        }
        // A host pagehide belongs to that view; an explicit hide() still tears down every owned handler.
        if (page && S.legacyLeave && S.legacyLeave()) { return; }
        if (S.timers.check) {
            clearTimeout(S.timers.check);
            S.timers.check = null;
        }
        S.shown = false;
        if (S.check) { S.check.seq++; }
        S.cleanups.splice(0).forEach(function (detach) { detach(); });
    }

    Object.defineProperty(window, 'BabelfinDashboard', {
        configurable: true,
        value: {
            show: function (page, options) { show(page, options); },
            hide: function (page) { hide(page); }
        }
    });
})();
