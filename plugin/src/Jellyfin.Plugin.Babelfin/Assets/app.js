/* Babelfin: the person's own settings page, served at {BaseUrl}/Babelfin/app.
   It uses the parent ApiClient in jellyfin-web, or this server's stored token and original device identity standalone, loads the person's
   document (GET Babelfin/Me/Settings) and the language table (GET Babelfin/Languages), and edits the whole document in memory;
   Save sends changed leaf intent with PUT Babelfin/Me/Settings?keepOverrides=true&fields=… and shows validation problems next to the controls they concern.
   The main view is the languages line, the "Audio and subtitles" table (BabelfinUI.libraryTable: the same choice for every library,
   or a choice per library, stored in PerLibrary) and the collapsed "Show and movie overrides (N)" section
   (BabelfinUI.overridesTable over GET Babelfin/Me/Overrides; a removal acts at once, with DELETE, and is made in the page's copy of the
   document and its saved baseline too, so it never shows as an unsaved change and a Save can't bring it back); everything else sits in
   the collapsed Advanced section, in collapsible groups, followed by "Restore defaults". Settings without
   a control on this page are kept and saved unchanged.
   Advanced's dropdowns list plain option names (no "Default (…)" entry): the selected one is the stored value, else the value that applies
   without it (AdvancedDefaults from the server); picking that value clears the stored field, so later changes to the defaults still apply.
   Inside jellyfin-web (client.js shows this page in a frame under Jellyfin's own header at #/babelfin) the page drops
   its own "back to Jellyfin" link, opens links to Jellyfin in the top window, and offers window.BabelfinApp for the unsaved-changes check.
   Strings come from Assets/i18n/<lang>.json. */
(function () {
    'use strict';

    const UI = window.BabelfinUI;
    const I18n = window.BabelfinI18n;
    const h = UI.h;
    const ADVANCED_KEY = 'babelfin-app-advanced';
    const GROUP_KEY = 'babelfin-app-group-';
    // The engine's built-in defaults, including RestOfShow for track changes: used only for a field the server's
    // AdvancedDefaults doesn't carry (Memory) or when an older server sends no AdvancedDefaults.
    const BUILTIN = Object.freeze({
        UsesAudioDescription: false, PrefersSdh: false, AvoidCommentary: true, UnknownAudioCountsAsUnderstood: false,
        Sdh: 'Neutral', Format: 'PreferText', Embedded: 'Neutral', IfNothingMatches: 'ShowNothing', Memory: 'RestOfShow'
    });
    // What "Restore defaults" clears: every option of the Subtitles, Audio and Track changes groups (HandleNextEpisode goes
    // back to false too), and a person-level UnlabelledAudio left from before it became an admin-only setting.
    // My languages, Languages for subtitles, the on/off switch, the table and shows and movies are left alone.
    const RESTORED_FIELDS = Object.freeze(['IfNothingMatches', 'Format', 'Embedded', 'Sdh', 'PrefersSdh', 'UnlabelledAudio', 'UsesAudioDescription', 'AvoidCommentary',
        'UnknownAudioCountsAsUnderstood', 'Memory']);

    // Shown in a frame inside jellyfin-web (or asked to look that way with ?embedded=1).
    const embedded = (function () {
        try {
            return window.top !== window || /[?&]embedded=1(?:&|$)/.test(location.search);
        } catch (e) {
            return true;
        }
    })();

    const baseUrl = (function () {
        const path = location.pathname;
        const at = path.toLowerCase().lastIndexOf('/babelfin/app');
        return at >= 0 ? path.slice(0, at) : '';
    })();

    const state = {
        credentials: null,
        version: null,
        ping: null,
        uiLanguage: 'en',
        languages: [],
        doc: null,
        base: null,
        inherited: null,
        advancedDefaults: null,
        defaults: null,
        advancedOpen: null,
        groupsOpen: {},
        libraries: [],
        assumed: [],
        initiallySet: new Set(),
        saving: false,
        keepSaveFocus: false,
        burnedRemoving: new Set(),
        burnedFailures: new Map(),
        needsSignIn: false,
        errors: [],
        overrides: null,
        itemNames: new Map(),
        itemTypes: new Map(),
        targets: [],
        clearers: [],
        checks: []
    };

    let t = function (key) { return key; };
    let root = null;
    let saveBar = null;
    let table = null;
    let genres = null;
    let overridesGroup = null;
    let overridesView = null;
    let paintBurnedIn = null;
    let burnedGroup = null;
    let paintStatus = null;

    // ----- Sign-in, API and small helpers -----

    let parentClient = null;

    function serverAddress(value) {
        try {
            const url = new URL(value);
            return /^https?:$/.test(url.protocol) && !url.username && !url.password && !url.search && !url.hash
                ? url.origin + url.pathname.replace(/\/+$/, '') : null;
        } catch (e) {
            return null;
        }
    }

    function frameClient() {
        try {
            if (window.parent !== window) {
                const client = window.parent.ApiClient;
                if (client && typeof client.getUrl === 'function' && typeof client.ajax === 'function' && typeof client.getCurrentUserId === 'function'
                    && new URL(client.getUrl('Babelfin/Ping')).href === location.origin + baseUrl + '/Babelfin/Ping') {
                    return client;
                }
            }
        } catch (e) {
            // A cross-origin parent cannot share its ApiClient.
        }
        return null;
    }

    async function readyFrameClient() {
        let parent;
        try {
            parent = window.parent;
            if (!parent || parent === window) return null;
            if (parent.location && parent.location.origin !== location.origin) return null;
        } catch (e) { return null; }
        let client = frameClient();
        if (client && client.getCurrentUserId()) return client;
        // Reloading jellyfin-web can load the frame before its connection/user state is ready.
        // Only wait for a known same-origin parent; never use a different server's client.
        if (!parent.location || parent.location.origin !== location.origin) return client;
        const deadline = Date.now() + 10000;
        while (Date.now() < deadline) {
            await new Promise(function (resolve) { setTimeout(resolve, 50); });
            client = frameClient();
            if (client && client.getCurrentUserId()) return client;
        }
        return null;
    }

    function storedDeviceId() {
        try { return localStorage.getItem('_deviceId2'); } catch (e) { return null; }
    }

    async function readCredentials() {
        try {
            const raw = localStorage.getItem('jellyfin_credentials');
            const saved = raw ? JSON.parse(raw) : {};
            const servers = Array.isArray(saved.Servers) ? saved.Servers.filter(function (s) { return s && s.AccessToken; }) : [];
            const address = location.origin + baseUrl;
            let matches = servers.filter(function (s) {
                return ['ManualAddress', 'LocalAddress', 'RemoteAddress', 'LastConnectionAddress'].some(function (key) {
                    return typeof s[key] === 'string' && serverAddress(s[key]) === address;
                });
            });
            if (!matches.length && servers.length) {
                // Public server identity resolves alternate addresses without trying any stored token.
                const info = await UI.api(baseUrl).getJSON('/System/Info/Public');
                const id = info && (info.Id || info.id);
                matches = id ? servers.filter(function (s) { return s.Id === id; }) : [];
            }
            matches.sort(function (a, b) { return (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0); });
            const server = matches[0];
            if (!server) {
                return null;
            }
            return {
                token: server.AccessToken, userId: server.UserId || null,
                client: server.AppName, deviceName: server.DeviceName,
                deviceId: server.DeviceId || storedDeviceId(), version: server.AppVersion
            };
        } catch (e) {
            return null;
        }
    }

    function authHeader() {
        if (!state.credentials || !state.credentials.token) {
            return null;
        }
        // Never repair identity strings by changing their contents: omit unsafe/missing values so Jellyfin
        // inherits the original token identity (AuthorizationContext, Jellyfin 12.2).
        function safe(value) { return typeof value === 'string' && value && !/["\\,\x00-\x1f\x7f]/.test(value); }
        if (!safe(state.credentials.token)) {
            return null;
        }
        const values = ['Token="' + state.credentials.token + '"'];
        [['Client', 'client'], ['Device', 'deviceName'], ['DeviceId', 'deviceId'], ['Version', 'version']].forEach(function (pair) {
            const value = state.credentials[pair[1]];
            if (safe(value)) {
                values.push(pair[0] + '="' + value + '"');
            }
        });
        return 'MediaBrowser ' + values.join(', ');
    }

    const standaloneApi = UI.api(baseUrl, authHeader);

    function responseData(value) {
        if (value === '' || value === undefined || value === null) { return null; }
        if (typeof value !== 'string') { return value; }
        try { return JSON.parse(value); } catch (e) { return value; }
    }

    async function request(method, path, body, query, init) {
        if (!parentClient) {
            return standaloneApi.request(method, path, body, query, init);
        }
        if (init && init.signal && init.signal.aborted) {
            throw new DOMException('The request was aborted.', 'AbortError');
        }
        const options = { headers: init && init.headers, type: method, url: parentClient.getUrl(path.replace(/^\//, ''), query), dataType: 'text' };
        if (body !== undefined) {
            options.data = JSON.stringify(body);
            options.contentType = 'application/json';
        }
        const signal = init && init.signal;
        let onAbort = null;
        try {
            // Text also handles successful 204s and text responses; JSON-only ApiClient calls do not.
            const pending = parentClient.ajax(options);
            const answer = signal ? new Promise(function (resolve, reject) {
                onAbort = function () { reject(new DOMException('The request was aborted.', 'AbortError')); };
                signal.addEventListener('abort', onAbort, { once: true });
                Promise.resolve(pending).then(resolve, reject);
                if (signal.aborted) { onAbort(); }
            }) : pending;
            return responseData(await answer);
        } catch (e) {
            if (e && e.name === 'AbortError') { throw e; }
            let data = null;
            try {
                data = responseData(e && typeof e.text === 'function' ? await e.text()
                    : e && typeof e.json === 'function' ? await e.json() : e && (e.responseJSON || e.responseText || e.body));
            } catch (ignored) { /* No readable response body. */ }
            const status = e && typeof e.status === 'number' ? e.status : 0;
            const message = data && typeof data === 'object' && (data.Message || data.message || data.title);
            const error = new Error(message || (status === 0 && e && e.message) || ('HTTP ' + status));
            error.name = 'BabelfinApiError';
            error.status = status;
            error.body = data;
            error.errors = UI.problemErrors(data);
            error.itemIds = (data && typeof data === 'object' && (data.ItemIds || data.itemIds)) || null;
            throw error;
        } finally {
            if (signal && onAbort) { signal.removeEventListener('abort', onAbort); }
        }
    }

    const api = {
        url: function (path, query) { return parentClient ? parentClient.getUrl(path.replace(/^\//, ''), query) : standaloneApi.url(path, query); },
        request: request,
        getJSON: function (path, query, init) { return request('GET', path, undefined, query, init); },
        putJSON: function (path, body, query, init) { return request('PUT', path, body === undefined ? null : body, query, init); },
        postJSON: function (path, body, query) { return request('POST', path, body === undefined ? null : body, query); },
        deleteJSON: function (path, query) { return request('DELETE', path, undefined, query); }
    };

    // Canonical casing ("pt-br" → "pt-BR"), and the script form when a region implies a non-default script ("zh-TW" → "zh-Hant"),
    // so the translation files named by BCP-47 tags are found. Generic Intl.Locale logic; no language is named here.
    function translationTag(raw) {
        let tag = String(raw || 'en').replace(/_/g, '-');
        try {
            tag = Intl.getCanonicalLocales(tag)[0] || tag;
            const max = new Intl.Locale(tag).maximize();
            if (max.script && max.script !== new Intl.Locale(max.language).maximize().script) {
                return max.language + '-' + max.script;
            }
        } catch (e) {
            // an unknown tag: use it as given
        }
        return tag;
    }

    function ctx() {
        return { t: t, languages: state.languages, uiLanguage: state.uiLanguage };
    }

    function compactId(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function pick(obj, name) {
        if (!obj) {
            return undefined;
        }
        return obj[name] !== undefined ? obj[name] : obj[name.charAt(0).toLowerCase() + name.slice(1)];
    }

    function camel(name) {
        return name.charAt(0).toLowerCase() + name.slice(1);
    }

    function deepCopy(value) {
        return value === null || value === undefined ? value : JSON.parse(JSON.stringify(value));
    }

    // A stable text form of the document: keys sorted, null and missing treated alike (decides whether there is anything to save).
    function canonical(value) {
        if (Array.isArray(value)) {
            return '[' + value.map(canonical).join(',') + ']';
        }
        if (value && typeof value === 'object') {
            return '{' + Object.keys(value).filter(function (k) { return value[k] !== null && value[k] !== undefined; }).sort()
                .map(function (k) { return JSON.stringify(k) + ':' + canonical(value[k]); }).join(',') + '}';
        }
        return JSON.stringify(value === undefined ? null : value);
    }

    function listText(items) {
        try {
            if (typeof Intl !== 'undefined' && typeof Intl.ListFormat === 'function') {
                return new Intl.ListFormat(state.uiLanguage, { style: 'short', type: 'unit' }).format(items);
            }
        } catch (e) {
            // fall through
        }
        return items.join(', ');
    }

    function languageNames(codes) {
        return listText((codes || []).map(function (code) { return UI.languageName(code, ctx()).name; }));
    }

    /** A translation that may be missing (help texts): empty when the files lack the key. */
    function optional(key) {
        return typeof t.has === 'function' && !t.has(key) ? '' : t(key);
    }

    function inheritedProfile() {
        return pick(state.inherited, 'Profile') || null;
    }

    function advancedProfile() {
        return pick(state.advancedDefaults, 'Profile') || null;
    }

    /**
     * The value an everyday field has when the person doesn't set it (null when unknown): AdvancedDefaults (resolved without the person's own
     * everyday fields), else the resolved profile for a field the person didn't set when the page loaded, else the built-in value.
     * A field missing from the server's JSON counts as not sent.
     */
    function defaultValue(name) {
        const advanced = advancedProfile();
        const own = pick(advanced, name);
        if (own !== undefined && own !== null) {
            return own;
        }
        const inherited = inheritedProfile();
        const resolved = pick(inherited, name);
        if (!advanced && resolved !== undefined && resolved !== null && !state.initiallySet.has(name)) {
            return resolved;
        }
        return BUILTIN[name] === undefined ? null : BUILTIN[name];
    }

    /** The value a field has on the page now: the admin's lock, else the person's own, else the default. */
    function fieldValue(name) {
        const holder = isLocked(name) ? state.doc.Locks : state.doc.Everyday;
        return UI.isFieldSet(holder, name) ? holder[name] : defaultValue(name);
    }

    function textOf(value) {
        return value === null || value === undefined ? null : String(value);
    }

    function isLocked(name) {
        return UI.isFieldSet(state.doc.Locks, name);
    }

    function fieldInfo(name) {
        return UI.FIELDS.find(function (f) { return f.name === name; });
    }

    // ----- Boot -----

    function tvLayout() {
        try { return embedded && window.parent.document.documentElement.classList.contains('layout-tv'); }
        catch (e) { return false; }
    }

    function tvControls() {
        const dialogs = document.querySelectorAll('.bfui-dialog');
        const scope = dialogs[dialogs.length - 1] || root;
        return Array.from(scope.querySelectorAll('a[href], button, input, select, textarea, summary, [tabindex]')).filter(function (node) {
            if (node.disabled || node.tabIndex < 0 || node.type === 'hidden') { return false; }
            for (let at = node; at && at !== document; at = at.parentElement) {
                if (at.hidden || at.classList.contains('hide')) { return false; }
                if (at.tagName === 'DETAILS' && !at.open && node !== at.querySelector('summary')) { return false; }
                if (window.getComputedStyle) {
                    const style = window.getComputedStyle(at);
                    if (style.display === 'none' || style.visibility === 'hidden') { return false; }
                }
            }
            return true;
        }).sort(function (a, b) {
            const aSave = !!a.closest('.bf-savebar'), bSave = !!b.closest('.bf-savebar');
            if (aSave !== bSave) { return aSave ? 1 : -1; }
            const ar = a.getBoundingClientRect(), br = b.getBoundingClientRect();
            return Math.abs(ar.top - br.top) > 3 ? ar.top - br.top : ar.left - br.left;
        });
    }

    function focusFirst() {
        if (!tvLayout()) { return; }
        document.documentElement.classList.add('bf-tv');
        const controls = tvControls();
        if (controls.length) { controls[0].focus(); }
    }

    function tvKey(e) {
        if (!tvLayout() || e.defaultPrevented) { return; }
        const key = e.key || '', code = e.keyCode || 0;
        document.documentElement.classList.add('bf-tv');
        if (UI.handleLanguagePickerKey(e)) {
            if (e.defaultPrevented) { e.stopPropagation(); }
            return;
        }
        const back = code === 27 || key === 'Escape' || key === 'Esc' || key === 'BrowserBack' || key === 'GoBack' || code === 461 || code === 10009;
        if (back && !document.querySelector('.bfui-dialog')) {
            e.preventDefault(); e.stopPropagation();
            if (window.parent.Babelfin && window.parent.Babelfin.closeSettings) { window.parent.Babelfin.closeSettings(); }
            return;
        }
        const controls = tvControls(), active = document.activeElement;
        let next = null;
        if (key === 'ArrowDown' || key === 'ArrowUp') {
            const index = controls.indexOf(active), step = key === 'ArrowDown' ? 1 : -1;
            next = controls[Math.max(0, Math.min(controls.length - 1, index + step))];
        } else if (key === 'ArrowLeft' || key === 'ArrowRight') {
            if (active && (active.tagName === 'TEXTAREA' || (active.tagName === 'INPUT' && (active.type === 'text' || active.type === 'search')))) { return; }
            const step = key === 'ArrowRight' ? 1 : -1;
            if (active && active.tagName === 'SELECT') {
                const options = Array.from(active.options).filter(function (option) { return !option.disabled && !(option.parentElement && option.parentElement.disabled); });
                const index = options.findIndex(function (option) { return option.value === active.value; });
                const option = options[Math.max(0, Math.min(options.length - 1, index + step))];
                if (option && option.value !== active.value) { active.value = option.value; active.dispatchEvent(new Event('change', { bubbles: true })); }
                next = active;
            } else if (active && active.getAttribute('role') === 'radio' && active.closest('[role="radiogroup"]')) {
                const radios = Array.from(active.closest('[role="radiogroup"]').querySelectorAll('[role="radio"]')).filter(function (radio) { return !radio.disabled && !radio.hidden; });
                next = radios[(radios.indexOf(active) + step + radios.length) % radios.length];
                if (next && next !== active) { next.click(); }
            } else if (active) {
                const rect = active.getBoundingClientRect();
                const row = controls.filter(function (node) { return Math.abs(node.getBoundingClientRect().top - rect.top) < 4; });
                const index = row.indexOf(active);
                if (index >= 0) { next = row[index + step]; }
            }
        } else if ((key === 'Enter' || key === 'OK' || code === 13) && active && active.tagName !== 'SELECT' && active.tagName !== 'TEXTAREA' && active.type !== 'text' && active.type !== 'search' && active.type !== 'number') {
            e.preventDefault(); e.stopPropagation(); active.click(); return;
        }
        if (next) {
            e.preventDefault(); e.stopPropagation(); next.focus();
            if (next.scrollIntoView) { next.scrollIntoView({ block: 'nearest' }); }
        }
    }

    async function boot() {
        root = document.getElementById('bf-app');
        root.classList.add('bfui'); // the shared components' colours and spacing apply to the whole page
        if (embedded) {
            document.documentElement.classList.add('bf-embedded');
        }
        // For client.js: whether there is something unsaved, and asking before the page is left.
        window.BabelfinApp = Object.freeze({
            hasChanges: function () { return isDirty(); },
            focusFirst: focusFirst,
            confirmLeave: function () {
                return isDirty()
                    ? UI.confirm({ t: t, title: t('app.discardConfirm.title'), message: t('app.discardConfirm.text'), okText: t('app.discard'), danger: true })
                    : Promise.resolve(true);
            }
        });
        parentClient = await readyFrameClient();
        state.credentials = parentClient
            ? (parentClient.getCurrentUserId() ? { userId: parentClient.getCurrentUserId() } : null)
            : await readCredentials();
        let pingError = null;
        if (state.credentials) {
            try {
                state.ping = await api.getJSON('/Babelfin/Ping');
                state.version = state.ping && (state.ping.version || state.ping.Version) || null;
            } catch (e) {
                pingError = e;
            }
        }
        state.uiLanguage = translationTag(I18n.detectLanguage(state.credentials && state.credentials.userId));
        t = await I18n.load(baseUrl, state.uiLanguage, 'i18n', state.version);
        UI.setTranslator(t);
        document.documentElement.lang = state.uiLanguage;
        document.title = t('app.title');
        if (!state.credentials || (pingError && (pingError.status === 401 || pingError.status === 403))) {
            renderSignIn();
            return;
        }
        if (pingError) {
            renderFailure(pingError, boot);
            return;
        }
        await load();
        focusFirst();
    }

    async function load() {
        renderMessage(t('app.loading'), null, true);
        try {
            const answers = await Promise.all([
                api.getJSON('/Babelfin/Me/Settings'),
                state.languages.length ? Promise.resolve(state.languages) : api.getJSON('/Babelfin/Languages'),
                // The overrides list is secondary: a failure other than a lost sign-in leaves the rest of the page usable (null = not loaded).
                fetchOverrides().then(null, function (e) {
                    if (e && (e.status === 401 || e.status === 403)) {
                        throw e;
                    }
                    return null;
                })
            ]);
            state.languages = Array.isArray(answers[1]) ? answers[1] : [];
            state.overrides = answers[2];
            state.keepSaveFocus = false;
            applyResponse(answers[0]);
            renderPage();
        } catch (e) {
            handleFailure(e, load);
        }
    }

    // Until the server sends Defaults, every row of the table falls back to the resolved everyday profile.
    function fallbackDefaults() {
        const p = inheritedProfile();
        if (!p) {
            return null;
        }
        return {
            AllLibraries: {
                AudioChain: p.AudioChain || [],
                WhenAudioUnderstood: p.WhenAudioUnderstood,
                WhenAudioNotUnderstood: p.WhenAudioNotUnderstood,
                SubtitleLanguages: p.SubtitleLanguages || [],
                Understood: p.Understood || [],
                Reads: p.Reads || [],
                AudioFromLibrary: false,
                SubtitlesFromLibrary: false
            },
            Libraries: {}
        };
    }

    function applyResponse(response) {
        const doc = deepCopy(pick(response, 'Settings')) || {};
        doc.Everyday = doc.Everyday || {};
        ['Libraries', 'SmartRules', 'Overrides', 'CollectionOrder', 'BurnedIn'].forEach(function (key) {
            doc[key] = Array.isArray(doc[key]) ? doc[key] : [];
        });
        doc.Locks = doc.Locks || {};
        doc.Overrides.forEach(function (o) { o.Settings = o.Settings || {}; });
        doc.Libraries.forEach(function (l) { l.Settings = l.Settings || {}; });
        state.doc = doc;
        state.base = deepCopy(doc);
        state.inherited = pick(response, 'Inherited') || null;
        state.advancedDefaults = pick(response, 'AdvancedDefaults') || null;
        state.libraries = pick(response, 'Libraries') || [];
        state.assumed = pick(response, 'Assumed') || [];
        const defaults = pick(response, 'Defaults');
        state.defaults = defaults || fallbackDefaults();
        state.initiallySet = new Set(UI.setFields(doc.Everyday));
        state.errors = [];
    }

    function handleFailure(error, retry) {
        if (error && (error.status === 401 || error.status === 403)) {
            renderSignIn();
        } else {
            renderFailure(error, retry);
        }
    }

    function isDirty() {
        return !!state.doc && canonical(state.doc) !== canonical(state.base);
    }

    function changed() {
        paintSaveBar();
    }

    // ----- Whole-page states -----

    function renderMessage(text, actions, busy) {
        saveBar = null;
        document.body.classList.remove('bf-has-savebar');
        UI.clear(root);
        root.appendChild(header());
        root.appendChild(h('main', { class: 'bf-main' }, h('div', { class: 'bf-section bf-message', role: busy ? 'status' : 'alert', 'aria-busy': busy ? 'true' : null },
            h('p', { text: text }), actions ? h('div', { class: 'bf-actions' }, actions) : null)));
    }

    async function retrySignIn() {
        if (state.saving) { return; }
        state.saving = true;
        const retryButton = root.querySelector('.bf-message button');
        if (retryButton) { retryButton.disabled = true; retryButton.setAttribute('aria-busy', 'true'); }
        try {
            const draftUserId = state.doc && (state.doc.UserId || (state.credentials && state.credentials.userId));
            // The initial frame wait belongs to boot; an explicit retry uses what is available now.
            parentClient = frameClient();
            if (parentClient && !parentClient.getCurrentUserId()) { parentClient = null; }
            const credentials = parentClient
                ? (parentClient.getCurrentUserId() ? { userId: parentClient.getCurrentUserId() } : null)
                : await readCredentials();
            // An in-memory draft belongs to the person who loaded it, even if another person signs in.
            if (!credentials || (state.doc && (!draftUserId || compactId(credentials.userId) !== compactId(draftUserId)))) {
                renderSignIn();
                return;
            }
            state.credentials = credentials;
            try {
                state.ping = await api.getJSON('/Babelfin/Ping');
            } catch (e) {
                if (!parentClient || !e || e.status !== 401) { throw e; }
                // Signing in in another tab may leave this frame's ApiClient holding the expired token.
                const saved = await readCredentials();
                if (!saved || compactId(saved.userId) !== compactId(credentials.userId)) { throw e; }
                parentClient = null;
                state.credentials = saved;
                state.ping = await api.getJSON('/Babelfin/Ping');
            }
            state.needsSignIn = false;
            if (state.doc) {
                renderPage();
            } else {
                await load();
            }
        } catch (e) {
            handleFailure(e, retrySignIn);
        } finally {
            state.saving = false;
            if (retryButton) { retryButton.disabled = false; retryButton.removeAttribute('aria-busy'); }
            paintSaveBar();
            const focus = root.querySelector('.bf-message button') || root.querySelector('.bf-savebar .bfui-btn-primary')
                || root.querySelector('.bf-languages-line button:not([disabled])') || root.querySelector('.bf-advanced-summary');
            if (focus) { focus.focus(); }
        }
    }

    function renderSignIn() {
        state.needsSignIn = true;
        saveBar = null;
        document.body.classList.remove('bf-has-savebar');
        const retainDraft = isDirty();
        UI.clear(root);
        root.appendChild(header());
        root.appendChild(h('main', { class: 'bf-main' }, h('div', { class: 'bf-section bf-message', role: 'alert', 'aria-atomic': 'true' },
            h('h2', { id: 'bf-signin-title', class: 'bf-section-title', text: t('app.signIn.title') }),
            h('p', { id: 'bf-signin-text', text: t('app.signIn.text') }),
            h('div', { class: 'bf-actions' },
                h('a', { class: 'bfui-btn bfui-btn-primary bf-link-button', href: baseUrl + '/web/', target: retainDraft ? '_blank' : (embedded ? '_top' : null), rel: retainDraft ? 'noopener' : null, 'aria-describedby': 'bf-signin-title bf-signin-text', text: t('app.signIn.link') }),
                state.doc ? h('button', { type: 'button', class: 'bfui-btn', text: t('app.retry'), 'aria-describedby': 'bf-signin-title bf-signin-text', onclick: retrySignIn }) : null))));
        root.querySelector('.bf-message a').focus();
    }

    function renderFailure(error, retry) {
        const status = error && error.status;
        let text;
        if (status === 503) {
            text = t('app.unavailable');
        } else if (status === 0) {
            text = t('app.offline');
        } else {
            text = t('app.loadError', { status: status || '?' });
        }
        // A schema conflict explains how the person can recover (for example, upgrade the plugin).
        // Use text nodes so server-provided detail can never become page markup.
        const known = UI.problemMessage(t, error && error.body);
        if (known) { text = known; }
        else if (status === 409 && error && error.message) {
            text += ' ' + error.message;
        }
        renderMessage(text, h('button', { type: 'button', class: 'bfui-btn bfui-btn-primary', text: t('app.retry'), onclick: function () { retry(); } }));
    }

    function goBack(e) {
        const web = location.origin + baseUrl + '/web';
        if (document.referrer && document.referrer.indexOf(web) === 0 && history.length > 1) {
            e.preventDefault();
            history.back();
        }
    }

    function header(withStatus) {
        const status = h('div', { class: 'bf-status' });
        const choiceStatus = withStatus ? UI.choiceStatus({
            className: 'bf-note', text: t('app.noChoices'),
            load: function () { return api.getJSON('/Babelfin/Me/ChoiceStatus'); }
        }) : null;
        paintStatus = function () {
            UI.clear(status);
            if (!withStatus || !state.doc) {
                return;
            }
            if (state.ping && state.ping.enabled === false) {
                status.appendChild(h('p', { class: 'bf-note', text: t('app.serverOff') }));
            }
            if (state.doc.Enabled === false) {
                status.appendChild(h('p', { class: 'bf-note', text: t('app.personOff') }));
            }
            if (choiceStatus) { status.appendChild(choiceStatus.element); }
        };
        state.choiceStatus = choiceStatus;
        paintStatus();
        return h('header', { class: 'bf-header' },
            embedded ? null : h('a', { class: 'bf-back', href: baseUrl + '/web/', text: t('app.back'), onclick: goBack }),
            h('div', { class: 'bf-heading' },
                h('h1', { text: t('app.title') }),
                h('p', { class: 'bf-subtitle', text: t('app.subtitle') }),
                t.machineTranslated ? h('a', { class: 'bf-translation-notice', href: 'https://github.com/WizardBal/Babelfin/blob/main/CONTRIBUTING.md#translations', target: '_blank', rel: 'noopener', text: t('common.machineTranslated') }) : null,
                status));
    }

    // ----- Error targets -----

    function matchesPrefix(path, prefix) {
        const p = String(path || '').toLowerCase();
        const q = prefix.toLowerCase();
        return p === q || p.indexOf(q + '.') === 0 || p.indexOf(q + '[') === 0;
    }

    /** Shows errors whose path starts with one of the prefixes ("everyday.memory") next to a control with showError/clearError. */
    function addFieldTarget(prefixes, control) {
        state.targets.push(function (errors) {
            return errors.filter(function (e) {
                if (prefixes.some(function (p) { return matchesPrefix(e.path, p); })) {
                    control.showError(e.message);
                    return false;
                }
                return true;
            });
        });
        state.clearers.push(control.clearError);
    }

    function errorLine() {
        const box = h('div', { class: 'bfui-field-error', role: 'alert', hidden: true });
        return {
            element: box,
            showError: function (message) {
                box.hidden = false;
                box.textContent = box.textContent ? box.textContent + ' · ' + message : String(message);
            },
            clearError: function () {
                box.hidden = true;
                box.textContent = '';
            }
        };
    }

    // Opens Advanced and the group around a node, so an error inside them can be seen.
    function revealNode(node) {
        UI.reveal(node);
    }

    // ----- The page -----

    function renderPage() {
        const scroll = window.scrollY;
        UI.clear(root);
        state.targets = [];
        state.clearers = [];
        state.checks = [];
        burnedGroup = null;
        root.appendChild(header(true));
        const main = h('main', { class: 'bf-main' });
        main.appendChild(languagesSection());
        main.appendChild(tableSection());
        main.appendChild(overridesSection());
        main.appendChild(advancedSection());
        root.appendChild(main);
        saveBar = h('div', { class: 'bf-savebar', role: 'region', 'aria-label': t('app.saveBar') });
        saveBar.addEventListener('focusout', function (event) {
            if (state.keepSaveFocus && !saveBar.contains(event.relatedTarget)) {
                state.keepSaveFocus = false;
                paintSaveBar();
            }
        });
        root.appendChild(saveBar);
        paintSaveBar();
        window.scrollTo(0, scroll);
        lookupNames();
    }

    function confirmSeed() {
        if (state.doc.Seed && !state.doc.Seed.Confirmed) {
            state.doc.Seed.Confirmed = true;
        }
        const line = document.getElementById('bf-detected');
        if (line) {
            line.hidden = true;
        }
    }

    // The list shown for Understood or Reads: the person's own (edited in place) or, while unset, a copy of what applies now.
    function workingList(name) {
        const own = state.doc.Everyday;
        if (isLocked(name)) {
            return state.doc.Locks[name].slice();
        }
        if (UI.isFieldSet(own, name)) {
            return own[name];
        }
        const row = state.defaults && state.defaults.AllLibraries;
        const profile = inheritedProfile();
        return ((row && row[name]) || (profile && profile[name]) || []).slice();
    }

    function languageListControl(name, label, after) {
        const locked = isLocked(name);
        const list = workingList(name);
        const error = errorLine();
        const box = h('div', { class: 'bf-list-field' });
        UI.languageList(box, list, Object.assign(ctx(), {
            label: label,
            disabled: locked,
            onChange: function (items) {
                state.doc.Everyday[name] = items;
                confirmSeed();
                if (after) {
                    after(items);
                }
                if (table) {
                    table.refresh();
                }
                if (genres) {
                    genres.refreshLanguages();
                }
                if (overridesView) {
                    overridesView.refresh();
                }
                changed();
            }
        }));
        if (locked) {
            box.appendChild(h('div', { class: 'bfui-help', text: t('ui.lockedByAdmin') }));
        }
        box.appendChild(error.element);
        addFieldTarget(['everyday.' + camel(name)], error);
        return { element: box, list: list, error: error };
    }

    function languagesSection() {
        const section = h('section', { class: 'bf-section bf-languages', 'aria-label': t('languages.title') });
        const summary = h('span', { class: 'bf-languages-summary' });
        const editorId = UI.uid('bf-understood');
        const editor = h('div', { class: 'bf-languages-editor', id: editorId, hidden: true });
        const control = languageListControl('Understood', t('languages.title'), function () { paint(); });
        function paint() {
            const codes = control.list;
            summary.textContent = t('languages.summary', { languages: codes.length ? languageNames(codes) : t('languages.none') });
        }
        paint();
        editor.appendChild(control.element);
        const change = h('button', { type: 'button', class: 'bfui-btn', 'aria-expanded': 'false', 'aria-controls': editorId, text: t('languages.change') });
        change.addEventListener('click', function () {
            editor.hidden = !editor.hidden;
            change.setAttribute('aria-expanded', editor.hidden ? 'false' : 'true');
            change.textContent = t(editor.hidden ? 'languages.change' : 'languages.done');
            if (!editor.hidden) {
                const input = editor.querySelector('input');
                if (input) {
                    input.focus();
                }
            }
        });
        section.appendChild(h('div', { class: 'bf-languages-line' }, h('span', { class: 'bf-languages-text' }, summary), isLocked('Understood') ? null : change));
        section.appendChild(editor);
        state.clearers.push(function () { /* the list's own error line is cleared through its target */ });
        state.targets.push(function (errors) {
            const mine = errors.filter(function (e) { return matchesPrefix(e.path, 'everyday.understood'); });
            if (mine.length) {
                editor.hidden = false;
                change.setAttribute('aria-expanded', 'true');
                change.textContent = t('languages.done');
            }
            return errors;
        });
        const confirmed = state.doc.Seed && state.doc.Seed.Confirmed;
        if (!confirmed && state.assumed.length) {
            const fromUi = state.assumed.every(function (a) { return (a.From || a.from) === 'ui'; });
            section.appendChild(h('p', { class: 'bf-detected', id: 'bf-detected' },
                h('span', { text: t(fromUi ? 'languages.detected.ui' : 'languages.detected.server') }), ' ',
                h('button', {
                    type: 'button', class: 'bfui-btn bfui-btn-primary', text: t('languages.confirm'),
                    onclick: function () {
                        state.doc.Seed = state.doc.Seed || {};
                        state.doc.Seed.Confirmed = true;
                        save();
                    }
                })));
        }
        return section;
    }

    // ----- Audio and subtitles -----

    function tableSection() {
        const titleId = UI.uid('bf-table-title');
        const section = h('section', { class: 'bf-section bf-table-section', 'aria-labelledby': titleId },
            h('h2', { class: 'bf-section-title', id: titleId, text: t('table.title') }));
        table = UI.libraryTable(section, {
            settings: state.doc,
            defaults: state.defaults,
            libraries: state.libraries,
            languages: state.languages,
            translate: t,
            uiLanguage: state.uiLanguage,
            modeField: 'PerLibrary',
            onChange: changed
        });
        genres = UI.genreRows(section, {
            settings: state.doc, t: t, languages: state.languages, uiLanguage: state.uiLanguage,
            mine: function () { return workingList('Understood'); },
            reads: function () { return workingList('Reads'); },
            loadGenres: function () {
                if (!state.credentials || !state.credentials.userId) { return Promise.reject(new Error('No viewer')); }
                return api.getJSON('/Genres', { UserId: state.credentials.userId });
            },
            onChange: changed
        });
        state.targets.push(function (errors) { return table.showErrors(errors); });
        state.clearers.push(function () { table.clearErrors(); });
        return section;
    }

    // ----- Names of stored titles (burned-in subtitles) -----

    function displayName(itemId, fallback) {
        return state.itemNames.get(compactId(itemId)) || fallback || '';
    }

    function scopePhrase(scope, itemId) {
        if (scope === 'Title') {
            const type = state.itemTypes.get(compactId(itemId));
            return t(type === 'Episode' ? 'titles.scope.episode' : type === 'Movie' ? 'titles.scope.movie' : 'titles.scope.title');
        }
        return t({ Season: 'titles.scope.season', Series: 'titles.scope.show', Collection: 'titles.scope.collection' }[scope] || 'titles.scope.title');
    }

    function itemRow(name, meta, button, error) {
        return h('li', { class: 'bf-item' },
            h('div', { class: 'bf-item-text' },
                h('span', { class: 'bf-item-name', text: name }),
                h('span', { class: 'bf-item-meta', text: meta }),
                error ? error.element : null),
            button);
    }

    // ----- Show and movie overrides -----

    /** "Show and movie overrides (N)": the count is left off while the list couldn't be loaded. */
    function overridesTitle() {
        return state.overrides ? t('overrides.title') + ' (' + state.overrides.length + ')' : t('overrides.title');
    }

    function paintOverridesTitle() {
        const title = overridesGroup && overridesGroup.button.querySelector('.bfui-disclosure-title');
        if (title) {
            title.textContent = overridesTitle();
        }
    }

    /** The person's overrides as GET Me/Overrides sends them: one summary each (names, library, what it sets, the remembered track change). */
    async function fetchOverrides() {
        const rows = await api.getJSON('/Babelfin/Me/Overrides');
        return Array.isArray(rows) ? rows : [];
    }

    /**
     * Fetches the summaries again (after a removal or a save, so an override added meanwhile from the ⋮ menu shows too) and redraws the
     * section. A failure keeps what the page shows: the removal that came before it has happened anyway.
     */
    async function reloadOverrides() {
        if (state.choiceStatus) { state.choiceStatus.refresh(); }
        try {
            state.overrides = await fetchOverrides();
        } catch (e) {
            return;
        }
        paintOverridesTitle();
        if (overridesView) {
            overridesView.refresh(state.overrides);
        } else if (overridesGroup && overridesGroup.body.firstChild) {
            // Built while the list had not loaded (the error and "Try again" are showing): the body gets its table now, like the title.
            fillOverrides(overridesGroup.body);
        }
    }

    // Takes a removed override out of the page's copy of the document and out of its saved baseline: nothing is left to save for it, and
    // the next Save (which keeps the stored overrides) can't bring it back.
    function forgetStored(row) {
        [state.doc, state.base].forEach(function (doc) {
            doc.Overrides = (doc.Overrides || []).filter(function (o) {
                return !(compactId(o.ItemId) === compactId(row.ItemId) && String(o.Scope) === String(row.Scope));
            });
        });
    }

    // Removal acts at once (no save bar): DELETE Me/Overrides/{itemId}?scope=…, which takes out the explicit settings and the remembered change.
    // A row without a real id (32 hex digits) is refused before making a single-override request.
    async function removeOverride(row) {
        const id = compactId(row.ItemId);
        if (!/^[0-9a-f]{32}$/.test(id)) {
            throw new Error('the override has no valid id');
        }
        try {
            await api.deleteJSON('/Babelfin/Me/Overrides/' + id, { scope: row.Scope });
        } catch (e) {
            // Gone already (removed on another device): the list is brought up to date below all the same.
            if (!e || e.status !== 404) {
                throw e;
            }
        }
        forgetStored(row);
        state.overrides = (state.overrides || []).filter(function (each) {
            return !(compactId(each.ItemId) === compactId(row.ItemId) && String(each.Scope) === String(row.Scope));
        });
        paintOverridesTitle();
        UI.toast(t('overrides.removed'));
        await reloadOverrides();
    }

    async function removeAllOverrides() {
        await api.deleteJSON('/Babelfin/Me/Overrides/All');
        state.doc.Overrides = [];
        state.base.Overrides = [];
        state.overrides = [];
        paintOverridesTitle();
        UI.toast(t('overrides.removeAll.done'));
        await reloadOverrides();
    }

    // The section's body: the help line, then the table (or, when the list couldn't be loaded, a line saying so with a way to try again).
    function fillOverrides(body) {
        UI.clear(body);
        overridesView = null;
        body.appendChild(h('p', { class: 'bfui-help bf-overrides-help', text: t('overrides.help') }));
        if (!state.overrides) {
            body.appendChild(h('div', { class: 'bfui-field-error', role: 'alert', text: t('common.error') }));
            body.appendChild(h('div', { class: 'bf-actions' }, h('button', {
                type: 'button', class: 'bfui-btn', text: t('app.retry'),
                onclick: async function () {
                    await reloadOverrides();
                    // A list that loaded has been drawn by reloadOverrides; one that didn't gets its message drawn again.
                    if (!state.overrides) {
                        fillOverrides(body);
                    }
                }
            })));
            return;
        }
        // My languages and Languages for subtitles are given as functions, asked at each drawing, so the words follow edits not saved yet.
        overridesView = UI.overridesTable(body, Object.assign(ctx(), {
            rows: state.overrides,
            mine: function () { return workingList('Understood'); },
            reads: function () { return workingList('Reads'); },
            onRemove: removeOverride,
            onRemoveAll: removeAllOverrides,
            onError: reportError,
            confirmText: t('overrides.removeAll.confirmText')
        }));
    }

    /**
     * "Show and movie overrides (N)": a collapsible section between "Audio and subtitles" and Advanced, closed by default; its state is
     * remembered like the groups of Advanced (localStorage, and in memory when storage is blocked). The table is built on the first opening.
     */
    function overridesSection() {
        overridesView = null;
        overridesGroup = UI.disclosure({
            title: overridesTitle(), level: 2, storageKey: GROUP_KEY + 'overrides', className: 'bf-section bf-overrides', id: 'bf-overrides',
            open: !!state.groupsOpen.overrides,
            onToggle: function (open) { state.groupsOpen.overrides = open; },
            build: fillOverrides
        });
        return overridesGroup.element;
    }

    // Fills in readable names ("Show · S1E2 · Name") and item types for the burned-in subtitle flags' ids, in one request.
    async function lookupNames() {
        const ids = new Set();
        state.doc.BurnedIn.forEach(function (b) { ids.add(compactId(b.ItemId)); });
        const missing = Array.from(ids).filter(function (id) { return id && !state.itemNames.has(id); });
        if (!missing.length) {
            return;
        }
        for (let i = 0; i < missing.length; i += 100) {
            try {
                const answer = await api.getJSON('/Items', { ids: missing.slice(i, i + 100).join(','), userId: state.credentials.userId, enableImages: false, enableUserData: false });
                ((answer && answer.Items) || []).forEach(function (item) {
                    state.itemNames.set(compactId(item.Id), UI.describeItem(item, t));
                    state.itemTypes.set(compactId(item.Id), item.Type);
                });
            } catch (e) {
                return;
            }
        }
        if (paintBurnedIn) {
            paintBurnedIn();
        }
    }

    // ----- Advanced -----

    // Advanced's open state: kept in memory too, so a re-render (after a save or "Restore defaults") keeps it even with storage blocked.
    function readAdvancedOpen() {
        if (state.advancedOpen !== null) {
            return state.advancedOpen;
        }
        try {
            return localStorage.getItem(ADVANCED_KEY) === '1';
        } catch (e) {
            return false;
        }
    }

    function writeAdvancedOpen(open) {
        state.advancedOpen = !!open;
        try {
            localStorage.setItem(ADVANCED_KEY, open ? '1' : '0');
        } catch (e) {
            // storage blocked: the section simply starts closed on the next visit
        }
    }

    /**
     * A dropdown for one everyday field: the plain options, with the stored value selected, else the value that applies
     * without it (defaultValue). Picking that value clears the stored field. A locked field shows the admin's value, disabled.
     * opts: { label, help, values, rare (values offered only while stored or the default), paths, after(value, control) }.
     */
    function everydaySelect(name, opts) {
        const o = opts || {};
        const field = fieldInfo(name);
        const bool = field.type === 'bool';
        const locked = isLocked(name);
        const holder = locked ? state.doc.Locks : state.doc.Everyday;
        const stored = UI.isFieldSet(holder, name) ? String(holder[name]) : null;
        const fallback = locked ? null : textOf(defaultValue(name));
        const current = stored !== null ? stored : fallback;
        const known = bool ? ['true', 'false'] : (UI.ENUMS[field.enumName] || []);
        const values = (o.values || known).slice();
        (o.rare || []).concat([current]).forEach(function (v) {
            if (v !== null && values.indexOf(v) < 0 && known.indexOf(v) >= 0 && (v === stored || v === fallback)) {
                values.push(v);
            }
        });
        const choices = values.map(function (v) {
            return [v, bool ? optional('field.' + name + '.' + v) || t(v === 'true' ? 'ui.yes' : 'ui.no') : t('enum.' + field.enumName + '.' + v)];
        });
        const control = UI.fieldSelect({
            t: t,
            label: o.label || t('field.' + name + '.label'),
            help: o.help || optional('field.' + name + '.help'),
            info: optional('field.' + name + '.info'),
            choices: choices,
            value: current,
            disabled: locked,
            lockedText: locked ? t('ui.lockedByAdmin') : '',
            onChange: function (value) {
                const everyday = state.doc.Everyday;
                if (value === null || value === fallback) {
                    UI.clearField(everyday, name);
                } else {
                    everyday[name] = bool ? value === 'true' : value;
                }
                if (o.after) {
                    o.after(value, control);
                }
                changed();
            }
        });
        control.element.setAttribute('data-field', name);
        addFieldTarget(['everyday.' + camel(name)].concat(o.paths || []), control);
        return control;
    }

    function switchRow(label, help, checked, onChange, info) {
        return UI.switchControl({ t: t, label: label, help: help, info: info || '', checked: checked, onChange: function (on) { onChange(on); changed(); } }).element;
    }

    /** One collapsible group of Advanced: closed by default, its state remembered per group. */
    function advancedGroup(id, title, help, children) {
        const group = UI.disclosure({
            title: title, level: 3, storageKey: GROUP_KEY + id, className: 'bf-group', id: 'bf-group-' + id,
            open: !!state.groupsOpen[id],
            onToggle: function (open) { state.groupsOpen[id] = open; }
        });
        if (help) {
            group.body.appendChild(h('p', { class: 'bfui-help bf-group-help', text: help }));
        }
        children.forEach(function (child) {
            if (child) {
                group.body.appendChild(child);
            }
        });
        return group;
    }

    /**
     * Advanced: the on/off switch at the top, then the groups Languages for subtitles, Subtitles, Audio,
     * Track changes, Burned-in subtitles (only when there are some) and Start over, then the "Restore defaults" button. Every option has an
     * infoTip saying what it does. Settings without a control here (rules, the retired "use my settings everywhere", the track order rules,
     * how the original language is found, audio tracks without a language, which only the admin sets, ...) stay in the document as they
     * are and are saved unchanged.
     */
    function advancedSection() {
        const body = h('div', { class: 'bf-advanced-body' });
        const titleId = UI.uid('bf-advanced-title');
        const details = h('details', { class: 'bf-section bf-advanced', id: 'bf-advanced', open: readAdvancedOpen() },
            h('summary', { class: 'bf-advanced-summary' }, h('h2', { class: 'bf-section-title', id: titleId, text: t('advanced.title') })),
            body);
        details.addEventListener('toggle', function () { writeAdvancedOpen(details.open); });

        body.appendChild(h('div', { class: 'bf-advanced-top' }, switchRow(t('advanced.enabled'), '', state.doc.Enabled !== false, function (on) {
            state.doc.Enabled = on;
            paintStatus();
        }, t('advanced.enabled.info'))));
        const reads = languageListControl('Reads', t('field.Reads.label'));
        body.appendChild(advancedGroup('reads', t('field.Reads.label'), optional('field.Reads.help'), [reads.element]).element);
        const sdh = everydaySelect('Sdh', { values: ['Neutral', 'Prefer', 'Avoid'] });
        const always = everydaySelect('PrefersSdh', { values: ['false', 'true'], after: function () { paintSdh(); } });
        // "Always show subtitles" picks SDH subtitles (ResolvedProfile.PrefersSdh), so while it is on the SDH menu shows that and waits.
        function paintSdh() {
            const on = String(fieldValue('PrefersSdh')) === 'true';
            sdh.select.disabled = on || isLocked('Sdh');
            sdh.select.value = on ? 'Prefer' : textOf(fieldValue('Sdh')) || '';
            UI.clear(sdh.extra);
            if (on) {
                sdh.extra.appendChild(h('div', { class: 'bfui-help', text: t('field.Sdh.byAlways') }));
            }
        }
        paintSdh();
        body.appendChild(advancedGroup('subtitles', t('advanced.subtitles'), '', [
            everydaySelect('IfNothingMatches', { values: ['ShowNothing', 'PartialInMyLanguage'], rare: ['FullInAnyBackupLanguage'] }).element,
            everydaySelect('Format', { values: ['PreferText', 'TextOnly', 'Neutral'] }).element,
            everydaySelect('Embedded', { values: ['Neutral', 'PreferEmbedded', 'PreferExternal'] }).element,
            always.element,
            sdh.element,
            everydaySelect('UnknownAudioCountsAsUnderstood', { values: ['false', 'true'] }).element
        ]).element);
        body.appendChild(advancedGroup('audio', t('advanced.audio'), '', [
            everydaySelect('UsesAudioDescription', { values: ['false', 'true'] }).element,
            everydaySelect('AvoidCommentary', { values: ['true', 'false'] }).element
        ]).element);
        // "Replace Jellyfin's remembered tracks" is shown only to a person who has it on, so they can give Jellyfin its settings back: the
        // app modes already replace the tracks Jellyfin copies to the next episode.
        body.appendChild(advancedGroup('trackChanges', t('advanced.trackChanges'), t('advanced.trackChanges.help'), [
            everydaySelect('Memory').element,
            state.base.HandleNextEpisode
                ? switchRow(t('advanced.nextEpisode'), '', !!state.doc.HandleNextEpisode, function (on) { state.doc.HandleNextEpisode = on; }, t('advanced.nextEpisode.help'))
                : null
        ]).element);
        const burned = advancedGroup('burnedIn', t('advanced.burnedIn'), t('advanced.burnedIn.help'), [buildBurnedIn()]);
        burnedGroup = burned.element;
        burnedGroup.hidden = !state.doc.BurnedIn.length;
        body.appendChild(burnedGroup);
        body.appendChild(advancedGroup('reset', t('advanced.startOver'), '', buildReset()).element);
        body.appendChild(h('div', { class: 'bf-actions bf-advanced-restore' },
            h('button', { type: 'button', class: 'bfui-btn', id: 'bf-restore-defaults', text: t('restore.button'), onclick: restoreDefaults })));
        return details;
    }

    // ----- Restore defaults -----

    /**
     * "Restore defaults": after a confirmation, clears every option of the Subtitles, Audio and Track changes groups
     * (RESTORED_FIELDS, and HandleNextEpisode back to false) in the unsaved document and shows the page again; nothing is sent until Save.
     */
    async function restoreDefaults() {
        const ok = await UI.confirm({ t: t, title: t('restore.confirm.title'), message: t('restore.confirm.text'), okText: t('restore.confirm.ok') });
        if (!ok) {
            return;
        }
        const everyday = state.doc.Everyday;
        RESTORED_FIELDS.forEach(function (name) { UI.clearField(everyday, name); });
        if (state.doc.HandleNextEpisode) {
            state.doc.HandleNextEpisode = false;
        }
        renderPage();
        const again = document.getElementById('bf-restore-defaults');
        if (again) {
            again.focus();
        }
    }

    // ----- Burned-in subtitles -----

    function burnedKey(flag) {
        return compactId(flag.ItemId) + ':' + flag.Scope;
    }

    function canRemoveBurned(flag) {
        const id = compactId(flag.ItemId);
        return /^[0-9a-f]{32}$/.test(id) && id !== '00000000000000000000000000000000'
            && ['Title', 'Season', 'Series', 'Collection'].indexOf(flag.Scope) >= 0;
    }

    // Unlike Save with keepOverrides=true, this endpoint changes the stored flag immediately.
    // Filter the current copies only after success: edits made while DELETE was pending stay intact.
    async function removeBurnedIn(flag, button) {
        const key = burnedKey(flag);
        if (state.saving || state.needsSignIn || state.burnedRemoving.has(key) || !canRemoveBurned(flag)) { return; }
        const hadFocus = button && document.activeElement === button;
        const focusAt = state.doc.BurnedIn.findIndex(function (each) { return burnedKey(each) === key; });
        state.burnedRemoving.add(key);
        state.burnedFailures.delete(key);
        if (button) { button.disabled = true; button.setAttribute('aria-busy', 'true'); }
        paintSaveBar();
        try {
            const id = compactId(flag.ItemId);
            const query = { scope: flag.Scope };
            if (flag.Scope === 'Collection') { query.collectionId = id; }
            await api.deleteJSON('/Babelfin/Me/BurnedIn/' + id, query);
            [state.doc, state.base].forEach(function (doc) {
                doc.BurnedIn = doc.BurnedIn.filter(function (each) { return burnedKey(each) !== key; });
            });
        } catch (e) {
            state.burnedFailures.set(key, (e && e.message) || t('common.error'));
            reportError(e);
        } finally {
            state.burnedRemoving.delete(key);
            if (!state.needsSignIn && paintBurnedIn) {
                const active = document.activeElement;
                const resume = hadFocus && (!active || active === button || active === document.body || !active.isConnected);
                paintBurnedIn(resume ? focusAt : undefined);
            }
            paintSaveBar();
        }
    }

    function buildBurnedIn() {
        const box = h('div', { class: 'bf-burned' });
        const rowErrors = new Map();
        paintBurnedIn = function (resumeAt) {
            const active = document.activeElement;
            const focusedKey = active && active.getAttribute ? active.getAttribute('data-bf-burned-key') : null;
            const focusAt = typeof resumeAt === 'number' ? resumeAt
                : Array.prototype.indexOf.call(box.querySelectorAll('.bf-item button'), active);
            UI.clear(box);
            rowErrors.clear();
            if (burnedGroup) {
                burnedGroup.hidden = !state.doc.BurnedIn.length;
            }
            const ul = h('ul', { class: 'bf-items' });
            state.doc.BurnedIn.forEach(function (flag) {
                const error = errorLine();
                rowErrors.set(flag, error);
                const key = burnedKey(flag);
                if (state.burnedFailures.has(key)) { error.showError(state.burnedFailures.get(key)); }
                const meta = [languageNames(flag.Languages || []), scopePhrase(flag.Scope, flag.ItemId)].filter(Boolean).join(' · ');
                ul.appendChild(itemRow(displayName(flag.ItemId, flag.Name), meta, h('button', {
                    type: 'button', class: 'bfui-btn', text: t('common.remove'), 'aria-label': t('titles.removeFor', { name: displayName(flag.ItemId, flag.Name) }),
                    disabled: state.saving || state.needsSignIn || state.burnedRemoving.has(key) || !canRemoveBurned(flag),
                    'aria-busy': state.burnedRemoving.has(key) ? 'true' : null,
                    'data-bf-burned-key': key,
                    onclick: function (e) { removeBurnedIn(flag, e.currentTarget); }
                }), error));
            });
            box.appendChild(ul);
            if (focusAt >= 0) {
                const buttons = Array.prototype.slice.call(box.querySelectorAll('.bf-item button'));
                const next = buttons.find(function (b) { return !b.disabled && b.getAttribute('data-bf-burned-key') === focusedKey; })
                    || buttons.slice(focusAt).find(function (b) { return !b.disabled; })
                    || buttons.slice(0, focusAt).reverse().find(function (b) { return !b.disabled; })
                    || root.querySelector('#bf-group-reset .bfui-disclosure-button');
                if (next) { next.focus(); }
            }
        };
        paintBurnedIn();
        state.targets.push(function (errors) {
            return errors.filter(function (e) {
                const m = /^burnedIn\[(\d+)\]/i.exec(String(e.path || ''));
                const flag = m ? state.doc.BurnedIn[Number(m[1])] : null;
                const error = flag ? rowErrors.get(flag) : null;
                if (error) {
                    error.showError(e.message);
                    return false;
                }
                return true;
            });
        });
        state.clearers.push(function () { rowErrors.forEach(function (error) { error.clearError(); }); });
        return box;
    }

    // ----- Start over -----

    function buildReset() {
        const reset = h('button', {
            type: 'button', class: 'bfui-btn bfui-btn-danger', text: t('startOver.button'),
            onclick: async function () {
                const ok = await UI.confirm({ t: t, title: t('startOver.confirm.title'), message: t('startOver.confirm.text'), okText: t('startOver.confirm.ok'), danger: true });
                if (!ok) {
                    return;
                }
                try {
                    await api.postJSON('/Babelfin/Me/Reset');
                    UI.toast(t('startOver.done'));
                    await load();
                } catch (e) {
                    reportError(e);
                }
            }
        });
        return [h('div', { class: 'bf-actions' }, reset)];
    }

    // ----- Saving -----

    function paintSaveBar() {
        if (!saveBar) {
            return;
        }
        UI.clear(saveBar);
        const dirty = isDirty();
        const keepFocus = tvLayout() && state.keepSaveFocus;
        const visible = dirty || state.saving || state.errors.length > 0 || keepFocus;
        saveBar.hidden = !visible;
        document.body.classList.toggle('bf-has-savebar', visible);
        if (!visible) {
            return;
        }
        if (state.errors.length) {
            saveBar.appendChild(h('div', { class: 'bf-savebar-errors', role: 'alert' },
                h('p', { text: t('app.validation') }),
                h('ul', null, state.errors.map(function (e) { return h('li', { text: e.message }); }))));
        }
        saveBar.appendChild(h('div', { class: 'bf-savebar-row' },
            h('span', { class: 'bf-savebar-status', role: 'status', text: state.saving ? t('app.saving') : (dirty ? t('app.unsaved') : (keepFocus ? t('app.saved') : '')) }),
            h('div', { class: 'bf-actions' },
                h('button', { type: 'button', class: 'bfui-btn', text: t('app.discard'), disabled: state.saving || state.burnedRemoving.size > 0 || !dirty, onclick: discard }),
                h('button', { type: 'button', class: 'bfui-btn bfui-btn-primary', text: t('app.save'), disabled: state.saving || state.burnedRemoving.size > 0 || (!dirty && !keepFocus), 'aria-disabled': !dirty ? 'true' : 'false', onclick: save }))));
    }

    async function discard() {
        if (state.saving || state.burnedRemoving.size > 0) { return; }
        const ok = await UI.confirm({ t: t, title: t('app.discardConfirm.title'), message: t('app.discardConfirm.text'), okText: t('app.discard'), danger: true });
        if (ok) {
            state.doc = deepCopy(state.base);
            renderPage();
        }
    }

    function reportError(e) {
        if (e && e.status === 401) {
            renderSignIn();
            return;
        }
        let message;
        const known = e && e.body && (e.body.Code || e.body.code) ? UI.problemMessage(t, e.body) : '';
        if (known) {
            message = known;
        } else if (e && e.status === 503) {
            message = t('app.unavailable');
        } else if (e && e.status === 0) {
            message = t('app.offline');
        } else {
            message = t('app.requestFailed', { message: (e && e.message) || '?' });
        }
        UI.toast(message, { kind: 'error' });
    }

    function clearErrors() {
        state.errors = [];
        state.clearers.forEach(function (fn) { fn(); });
    }

    function firstError() {
        const marked = root.querySelector('.bfui-has-error');
        if (marked) {
            return marked;
        }
        return Array.prototype.find.call(root.querySelectorAll('.bfui-field-error'), function (box) { return !box.hidden && box.textContent; }) || null;
    }

    function showErrors(errors, itemIds) {
        let rest = errors.slice();
        state.targets.forEach(function (target) { rest = target(rest); });
        if (itemIds && itemIds.length) {
            rest.unshift({ path: '', message: t('app.inaccessibleItems') });
        }
        state.errors = rest;
        paintSaveBar();
        root.querySelectorAll('.bfui-has-error, .bfui-field-error').forEach(function (node) {
            if (!node.hidden && (node.classList.contains('bfui-has-error') || node.textContent)) {
                revealNode(node);
            }
        });
        const target = firstError();
        if (target) {
            if (typeof target.scrollIntoView === 'function') {
                target.scrollIntoView({ behavior: 'smooth', block: 'center' });
            }
        }
    }

    // Only editable paths that differ from the saved baseline express write intent.
    // The backend accepts everyday leaves, never the whole everyday object.
    const PERSON_FIELDS = ['Enabled', 'UseEverydayEverywhere', 'PerLibrary', 'HandleNextEpisode', 'Libraries', 'SmartRules', 'CollectionOrder'];
    function changedFields(base, work) {
        const fields = [];
        PERSON_FIELDS.filter(function (key) { return key !== 'Libraries'; }).forEach(function (key) {
            if (canonical(base[key]) !== canonical(work[key])) { fields.push(camel(key)); }
        });
        const before = base.Libraries || [], after = work.Libraries || [];
        new Set(before.concat(after).map(function (row) { return compactId(row.LibraryId); })).forEach(function (id) {
            if (!/^[0-9a-f]{32}$/.test(id) || id === '00000000000000000000000000000000') { return; }
            const old = before.find(function (row) { return compactId(row.LibraryId) === id; });
            const draft = after.find(function (row) { return compactId(row.LibraryId) === id; });
            UI.FIELDS.forEach(function (field) {
                const previous = old && old.Settings && old.Settings[field.name];
                const value = draft && draft.Settings && draft.Settings[field.name];
                if (canonical(previous) !== canonical(value)) { fields.push('libraries.' + id + '.settings.' + camel(field.name)); }
            });
        });
        UI.FIELDS.forEach(function (field) {
            if (canonical((base.Everyday || {})[field.name]) !== canonical((work.Everyday || {})[field.name])) {
                fields.push('everyday.' + camel(field.name));
            }
        });
        if (canonical((base.Seed || {}).Confirmed) !== canonical((work.Seed || {}).Confirmed)) { fields.push('seed.confirmed'); }
        return fields.sort();
    }

    // Reapply just a local object delta onto a fresh copy. Missing leaves mean clear;
    // arrays express replacement intent, and unchanged/unknown leaves stay fresh.
    function mergeEdits(base, work, fresh) {
        const result = deepCopy(fresh || {});
        const before = base || {}, after = work || {};
        new Set(Object.keys(before).concat(Object.keys(after))).forEach(function (key) {
            if (canonical(before[key]) === canonical(after[key])) { return; }
            const value = after[key];
            if (value && typeof value === 'object' && !Array.isArray(value)) {
                result[key] = mergeEdits(before[key], value, result[key]);
            } else if (value === undefined) {
                delete result[key];
            } else {
                result[key] = deepCopy(value);
            }
        });
        return result;
    }

    // Reapply newer in-flight draft edits by stable row identity after a successful Save.
    // The outgoing request uses backend library leaves, so other rows/options stay server-owned.
    function mergeLibraries(base, work, fresh) {
        const result = deepCopy(fresh || []);
        const before = base || [], after = work || [];
        const idOf = function (row) { return compactId(row.LibraryId); };
        const ids = new Set(before.concat(after).map(idOf));
        ids.forEach(function (id) {
            const old = before.find(function (row) { return idOf(row) === id; });
            const draft = after.find(function (row) { return idOf(row) === id; });
            if (canonical(old) === canonical(draft)) { return; }
            const at = result.findIndex(function (row) { return idOf(row) === id; });
            if (!draft && at < 0) { return; }
            // A removed row needs its identity back only when a newer choice recreates it.
            // Existing row metadata and hidden settings always come from the response.
            const latest = at < 0 ? { LibraryId: draft.LibraryId, Name: draft.Name } : result[at];
            const settings = deepCopy(latest.Settings || {});
            UI.FIELDS.forEach(function (field) {
                const previous = old && old.Settings && old.Settings[field.name];
                const value = draft && draft.Settings && draft.Settings[field.name];
                if (canonical(previous) === canonical(value)) { return; }
                if (value === undefined) { delete settings[field.name]; }
                else { settings[field.name] = deepCopy(value); }
            });
            // Clearing an already-removed row is satisfied; an existing empty row keeps
            // its identity, so null/missing defaults cannot leave an unwritable dirty draft.
            if (at < 0 && !Object.keys(settings).some(function (key) { return settings[key] !== null && settings[key] !== undefined; })) { return; }
            const row = Object.assign({}, latest, { Settings: settings });
            if (at < 0) { result.push(row); }
            else { result[at] = row; }
        });
        return result;
    }

    function applyDraftEdits(base, work, fresh) {
        const result = deepCopy(fresh);
        PERSON_FIELDS.forEach(function (key) {
            if (canonical(base[key]) === canonical(work[key])) { return; }
            if (key === 'Libraries') { result[key] = mergeLibraries(base[key], work[key], result[key]); }
            else if (work[key] === undefined) { delete result[key]; }
            else { result[key] = deepCopy(work[key]); }
        });
        result.Everyday = mergeEdits(base.Everyday, work.Everyday, result.Everyday);
        if (canonical((base.Seed || {}).Confirmed) !== canonical((work.Seed || {}).Confirmed)) {
            result.Seed = Object.assign({}, result.Seed, { Confirmed: (work.Seed || {}).Confirmed });
        }
        return result;
    }

    function intentBody(work, fields) {
        const body = deepCopy(work);
        fields.forEach(function (path) {
            if (path.indexOf('libraries.') === 0) {
                const parts = path.split('.');
                const row = body.Libraries.find(function (entry) { return compactId(entry.LibraryId) === parts[1]; });
                // An absent row is the backend's explicit clear for this named leaf.
                if (row) {
                    row.Settings = row.Settings || {};
                    const key = parts[3].charAt(0).toUpperCase() + parts[3].slice(1);
                    if (row.Settings[key] === undefined) { row.Settings[key] = null; }
                }
                return;
            }
            const keys = path.split('.').map(function (key) { return key.charAt(0).toUpperCase() + key.slice(1); });
            let holder = body;
            keys.slice(0, -1).forEach(function (key) { holder[key] = holder[key] || {}; holder = holder[key]; });
            const key = keys[keys.length - 1];
            if (holder[key] === undefined) { holder[key] = null; }
        });
        return body;
    }

    function showConflict(problem) {
        const old = root.querySelector('.bf-conflict');
        if (old) { old.parentNode.removeChild(old); }
        const paths = problem.ConflictingPaths || problem.conflictingPaths || [];
        const panel = UI.conflictPanel({ t: t, problem: problem,
            label: function (path) {
                if (/understood$/.test(path)) { return t('languages.title'); }
                if (/reads$|subtitle/i.test(path)) { return t('ui.table.subtitles'); }
                if (/audio/i.test(path)) { return t('ui.table.audio'); }
                return t('conflict.settings');
            },
            reload: async function () {
                const response = await api.getJSON('/Babelfin/Me/Settings');
                const fresh = response.Settings || response.settings || response;
                paths.forEach(function (path) {
                    UI.setFieldValue(state.doc, path, UI.fieldValue(fresh, path));
                    UI.setFieldValue(state.base, path, UI.fieldValue(fresh, path));
                });
                renderPage();
            },
            keep: function () { panel.parentNode.removeChild(panel); return save(true); },
            error: reportError
        });
        root.appendChild(panel); panel.focus();
    }

    async function save(force) {
        if (state.saving || state.needsSignIn || state.burnedRemoving.size > 0) {
            return;
        }
        if (tvLayout() && state.keepSaveFocus && !isDirty()) { return; }
        clearErrors();
        const blocked = state.checks.map(function (check) { return check(); }).filter(Boolean);
        if (blocked.length) {
            revealNode(blocked[0]);
            if (typeof blocked[0].scrollIntoView === 'function') {
                blocked[0].scrollIntoView({ behavior: 'smooth', block: 'center' });
            }
            paintSaveBar();
            return;
        }
        const baseline = deepCopy(state.base), work = deepCopy(state.doc);
        const fields = changedFields(baseline, work);
        if (!fields.length) { return; }
        state.saving = true;
        if (paintBurnedIn) { paintBurnedIn(); }
        paintSaveBar();
        try {
            const body = intentBody(work, fields);
            // Field intent preserves server changes outside the edited paths. keepOverrides
            // also protects the stored overrides and burned-in flags from this page's copy.
            const response = await api.putJSON('/Babelfin/Me/Settings', force === true ? body : UI.withExpected(body, baseline, fields), { keepOverrides: true, fields: fields.join(',') });
            const later = deepCopy(state.doc);
            state.saving = false;
            applyResponse(response);
            state.doc = applyDraftEdits(work, later, state.doc);
            state.keepSaveFocus = tvLayout();
            renderPage();
            UI.toast(t('app.saved'));
            if (state.keepSaveFocus) { const button = root.querySelector('.bf-savebar .bfui-btn-primary'); if (button) { button.focus(); } }
            reloadOverrides();
        } catch (e) {
            state.saving = false;
            if (e.status === 409 && e.body && (e.body.Code || e.body.code) === 'field-conflict') {
                paintSaveBar();
                showConflict(e.body);
                return;
            }
            if (e.status === 401) {
                renderSignIn();
                return;
            }
            if (paintBurnedIn) { paintBurnedIn(); }
            if (e.status === 400 || e.status === 403) {
                showErrors(e.errors && e.errors.length ? e.errors : [{ path: '', message: UI.problemMessage(t, e.body, e.message) }], e.itemIds);
                return;
            }
            paintSaveBar();
            reportError(e);
        }
    }

        window.addEventListener('beforeunload', function (e) {
            if (isDirty()) {
                e.preventDefault();
                e.returnValue = '';
            }
        });
        document.addEventListener('keydown', function (e) {
            tvKey(e);
            if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S') && isDirty()) {
                e.preventDefault();
                save();
            }
        }, true);

    function start() {
        boot().catch(function (e) {
            if (!root) {
                root = document.getElementById('bf-app') || document.body;
            }
            renderFailure(e, start);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
