/* Babelfin translation loader. Shared by the person's page, the injected script and the dashboard.
   Usage: BabelfinI18n.load(baseUrl, language, 'i18n' | 'i18n-admin', version).then(function (t) { t('key', { name: 'x' }); });
   A key missing in the language falls back: variant ("pt-BR") → base ("pt") → English. Files are JSON objects of
   key → string; "{name}" placeholders are replaced from the params; "_machineTranslated": true marks AI-drafted files. */
(function (global) {
    'use strict';

    var cache = {};

    function fetchJson(url) {
        return fetch(url, { credentials: 'same-origin' }).then(function (r) {
            if (!r.ok) {
                throw new Error('HTTP ' + r.status);
            }
            return r.json();
        });
    }

    /* Canonical form of a tag: "pt-br" -> "pt-BR", "zh-tw" -> "zh-Hant" (the script comes from Intl.Locale's likely subtags; nothing is named here). */
    function canonical(language) {
        var raw = String(language || 'en').replace(/_/g, '-').trim();
        try {
            if (typeof Intl !== 'undefined' && Intl.Locale) {
                var loc = new Intl.Locale(raw);
                var base = (loc.language || 'en').toLowerCase();
                var script = loc.script || null;
                if (!script && loc.region) {
                    // A region that implies a script different from the base's usual one (zh-TW -> Hant) becomes language-script.
                    var withRegion = new Intl.Locale(base + '-' + loc.region).maximize().script;
                    var plain = new Intl.Locale(base).maximize().script;
                    if (withRegion && plain && withRegion !== plain) {
                        return { tag: base + '-' + withRegion, base: base };
                    }
                }
                var tag = base + (script ? '-' + script : '') + (loc.region ? '-' + loc.region.toUpperCase() : '');
                return { tag: tag, base: base };
            }
        } catch (e) {
            // fall through to the plain split
        }
        var parts = raw.split('-');
        var b = parts[0].toLowerCase();
        var rest = parts.slice(1).map(function (p) { return p.length === 4 ? p.charAt(0).toUpperCase() + p.slice(1).toLowerCase() : p.toUpperCase(); });
        return { tag: rest.length ? b + '-' + rest.join('-') : b, base: b };
    }

    // The translation files shipped in i18n/ and i18n-admin/ (tools/package.py checks this list against the folders), so the loader
    // never asks for a file that doesn't exist: a regional locale such as en-US would otherwise log a 404 on every page.
    var SHIPPED = ['de', 'en', 'es', 'fr', 'it', 'ja', 'ko', 'nl', 'pl', 'pt', 'pt-BR', 'pt-PT', 'ru', 'zh', 'zh-Hans', 'zh-Hant'];

    function shipped(code) {
        var lower = String(code || '').toLowerCase();
        for (var i = 0; i < SHIPPED.length; i++) {
            if (SHIPPED[i].toLowerCase() === lower) {
                return SHIPPED[i];
            }
        }
        return null;
    }

    function candidates(language) {
        var all = rawCandidates(language);
        var list = [];
        for (var i = 0; i < all.length; i++) {
            var file = shipped(all[i]);
            if (file && list.indexOf(file) < 0) {
                list.push(file);
            }
        }
        return list.length ? list : ['en'];
    }

    function rawCandidates(language) {
        var c = canonical(language);
        var list = [];
        if (c.tag !== c.base) {
            list.push(c.tag);
            var parts = c.tag.split('-');
            if (parts.length > 2 && parts[1].length === 4) {
                list.push(parts[0] + '-' + parts[1]);
            }
        }
        list.push(c.base);
        if (list.indexOf('en') < 0) {
            list.push('en');
        }
        return list;
    }

    function load(baseUrl, language, folder, version) {
        var key = baseUrl + '|' + language + '|' + folder;
        if (cache[key]) {
            return cache[key];
        }
        var files = candidates(language);
        cache[key] = Promise.all(files.map(function (code) {
            var url = baseUrl + '/Babelfin/assets/' + folder + '/' + encodeURIComponent(code) + '.json' + (version ? '?v=' + encodeURIComponent(version) : '');
            return fetchJson(url).catch(function () { return null; });
        })).then(function (tables) {
            var loaded = tables.filter(Boolean);
            var machine = loaded.length > 0 && loaded[0] && loaded[0]._machineTranslated === true && files[0] !== 'en';
            function t(k, params) {
                var text;
                for (var i = 0; i < loaded.length; i++) {
                    if (loaded[i] && Object.prototype.hasOwnProperty.call(loaded[i], k)) {
                        text = loaded[i][k];
                        break;
                    }
                }
                if (typeof text !== 'string') {
                    text = k;
                }
                if (params) {
                    Object.keys(params).forEach(function (p) {
                        text = text.split('{' + p + '}').join(String(params[p]));
                    });
                }
                return text;
            }
            t.language = files[0];
            t.machineTranslated = machine;
            t.has = function (k) {
                return loaded.some(function (table) { return table && Object.prototype.hasOwnProperty.call(table, k); });
            };
            return t;
        });
        return cache[key];
    }

    function detectLanguage(userId) {
        try {
            var stored = userId ? localStorage.getItem(userId + '-language') : null;
            if (stored) {
                return stored;
            }
        } catch (e) {
            // storage blocked
        }
        return (navigator.language || 'en');
    }

    global.BabelfinI18n = { load: load, detectLanguage: detectLanguage, candidates: candidates, canonical: canonical };
})(typeof window !== 'undefined' ? window : this);
