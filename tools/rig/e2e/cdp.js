/* A small Chrome DevTools Protocol driver for Babelfin's browser tests (no npm packages: Node's global WebSocket and fetch).
   Chrome.launch() starts a headless Chrome with a fresh temporary profile and remote debugging on a free port (read from the
   profile's DevToolsActivePort file), then attaches to its page over one browser WebSocket ("flat" sessions).
   chrome.close() always kills Chrome (its whole process tree) and deletes the temporary profile; the same happens on process
   exit and Ctrl+C, so a failing run leaves nothing behind.

   Page methods: navigate(url), reload(), evaluate(fnOrExpr, ...args), waitFor(fnOrExpr, opts, ...args), waitForSelector(sel),
   click(selectorOrFn), focus(selector), type(text), press(key), setViewport(w, h, mobile), clearViewport(), screenshot(file).
   Collected while the page lives: page.console (console calls and browser log entries), page.exceptions (uncaught errors),
   page.badResponses (HTTP status >= 400), page.requests (every request sent: method and URL), page.dialogs (alert/confirm/
   beforeunload, all accepted). page.interceptResponses(pattern, transform) rewrites matching responses (Fetch domain). */
'use strict';

const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const CHROME_PATH = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Kills a process and its children (Chrome starts several). Synchronous so it also works from an 'exit' handler. */
function killTree(pid) {
    if (!pid) return true;
    try {
        if (process.platform === 'win32') {
            const result = spawnSync('taskkill', ['/F', '/T', '/PID', String(pid)], { stdio: 'ignore', windowsHide: true, timeout: 10000 });
            if (!result.error && result.status === 0) return true;
        } else {
            process.kill(-pid, 'SIGKILL');
            return true;
        }
    } catch (e) { /* Check whether the owned process has already exited. */ }
    try {
        process.kill(pid, 0);
        return false;
    } catch (e) {
        return e.code === 'ESRCH';
    }
}

function removeDir(dir) {
    for (let i = 0; i < 40; i++) {
        try {
            fs.rmSync(dir, { recursive: true, force: true });
        } catch (e) {
            // Chrome's children may still hold files for a moment after the kill.
        }
        if (!fs.existsSync(dir)) {
            return true;
        }
        const until = Date.now() + 250;
        while (Date.now() < until) { /* busy wait: this also runs from synchronous exit handlers */ }
    }
    return !fs.existsSync(dir);
}

const KEYS = {
    Enter: { key: 'Enter', code: 'Enter', keyCode: 13, text: '\r' },
    Escape: { key: 'Escape', code: 'Escape', keyCode: 27 },
    BrowserBack: { key: 'BrowserBack', code: 'BrowserBack', keyCode: 166 },
    GoBack: { key: 'GoBack', code: 'BrowserBack', keyCode: 166 },
    WebOSBack: { key: 'Unidentified', code: '', keyCode: 461 },
    TizenBack: { key: 'Unidentified', code: '', keyCode: 10009 },
    Tab: { key: 'Tab', code: 'Tab', keyCode: 9 },
    Space: { key: ' ', code: 'Space', keyCode: 32, text: ' ' },
    Backspace: { key: 'Backspace', code: 'Backspace', keyCode: 8 },
    ArrowDown: { key: 'ArrowDown', code: 'ArrowDown', keyCode: 40 },
    ArrowUp: { key: 'ArrowUp', code: 'ArrowUp', keyCode: 38 },
    ArrowLeft: { key: 'ArrowLeft', code: 'ArrowLeft', keyCode: 37 },
    ArrowRight: { key: 'ArrowRight', code: 'ArrowRight', keyCode: 39 },
    Home: { key: 'Home', code: 'Home', keyCode: 36 },
    End: { key: 'End', code: 'End', keyCode: 35 },
    ContextMenu: { key: 'ContextMenu', code: 'ContextMenu', keyCode: 93 },
    PageDown: { key: 'PageDown', code: 'PageDown', keyCode: 34 },
    PageUp: { key: 'PageUp', code: 'PageUp', keyCode: 33 }
};

/** Whether text matches a pattern where * stands for any run of characters (the Fetch domain's urlPattern rule). */
function globMatch(pattern, text) {
    const parts = String(pattern).split('*');
    if (parts.length === 1) {
        return text === parts[0];
    }
    if (!text.startsWith(parts[0])) {
        return false;
    }
    let pos = parts[0].length;
    for (let i = 1; i < parts.length - 1; i++) {
        const k = text.indexOf(parts[i], pos);
        if (k < 0) {
            return false;
        }
        pos = k + parts[i].length;
    }
    const last = parts[parts.length - 1];
    return text.length - last.length >= pos && text.endsWith(last);
}

/** Builds the expression for evaluate(): a function is called with JSON arguments, a string is evaluated as is. */
function expressionOf(fnOrExpr, args) {
    if (typeof fnOrExpr === 'function') {
        return '(' + fnOrExpr.toString() + ').apply(null, ' + JSON.stringify(args || []) + ')';
    }
    return String(fnOrExpr);
}

// Runs on the element being clicked: is the point (x, y) on it (or on its label), or what covers it?
// A point over a same-origin frame is looked up again inside that frame (x and y are in the viewport's coordinates; a frame has no border).
const HIT_TEST = 'function (x, y) {'
    + ' var at = document.elementFromPoint(x, y);'
    + ' if (!at) { return "nothing"; }'
    + ' var ox = x; var oy = y;'
    + ' while (at && at.tagName === "IFRAME" && at !== this) {'
    + '  var doc = null; try { doc = at.contentDocument; } catch (e) { doc = null; }'
    + '  if (!doc) { break; }'
    + '  var fr = at.getBoundingClientRect(); ox -= fr.left; oy -= fr.top;'
    + '  var inner = doc.elementFromPoint(ox, oy);'
    + '  if (!inner) { return "nothing"; }'
    + '  at = inner;'
    + ' }'
    + ' if (at === this || this.contains(at)) { return "ok"; }'
    + ' if (at.tagName === "LABEL" && at.control === this) { return "ok"; }'
    + ' var cls = typeof at.className === "string" && at.className.trim() ? "." + at.className.trim().split(/ +/).join(".") : "";'
    + ' return at.tagName.toLowerCase() + (at.id ? "#" + at.id : "") + cls; }';

class Page {
    constructor(chrome, targetId, sessionId) {
        this.chrome = chrome;
        this.targetId = targetId;
        this.sessionId = sessionId;
        this.console = [];
        this.exceptions = [];
        this.badResponses = [];
        this.failedRequests = [];
        this.requests = [];
        this.dialogs = [];
        this.interceptors = [];
        this.mockResponders = [];
        this.url = '';
        this.listeners = new Map();
        this.defaultTimeout = 20000;
    }

    send(method, params) {
        return this.chrome.send(method, params || {}, this.sessionId);
    }

    on(method, fn) {
        if (!this.listeners.has(method)) {
            this.listeners.set(method, []);
        }
        this.listeners.get(method).push(fn);
        return () => {
            const list = this.listeners.get(method) || [];
            const i = list.indexOf(fn);
            if (i >= 0) {
                list.splice(i, 1);
            }
        };
    }

    once(method, timeout, what) {
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                off();
                reject(new Error('timeout after ' + timeout + ' ms waiting for ' + (what || method)));
            }, timeout);
            const off = this.on(method, (params) => {
                clearTimeout(timer);
                off();
                resolve(params);
            });
        });
    }

    dispatch(method, params) {
        (this.listeners.get(method) || []).slice().forEach((fn) => {
            try { fn(params); } catch (e) { /* a listener's own problem */ }
        });
    }

    async init() {
        this.on('Runtime.consoleAPICalled', (p) => {
            const frames = (p.stackTrace && p.stackTrace.callFrames) || [];
            const text = (p.args || []).map((a) => (a.value !== undefined ? String(a.value) : (a.description || a.type))).join(' ');
            this.console.push({ type: p.type, text: text, url: frames.length ? frames[0].url : '', frames: frames.map((f) => f.url), page: this.url, at: Date.now() });
        });
        this.on('Runtime.exceptionThrown', (p) => {
            const d = p.exceptionDetails || {};
            const frames = (d.stackTrace && d.stackTrace.callFrames) || [];
            const text = (d.exception && d.exception.description) || d.text || 'exception';
            this.exceptions.push({ text: text, url: d.url || (frames.length ? frames[0].url : ''), frames: frames.map((f) => f.url), page: this.url, at: Date.now() });
        });
        this.on('Log.entryAdded', (p) => {
            const e = p.entry || {};
            this.console.push({ type: e.level, text: e.text || '', url: e.url || '', frames: [], source: e.source, page: this.url, at: Date.now() });
        });
        this.on('Network.responseReceived', (p) => {
            const r = p.response || {};
            if (r.status >= 400) {
                this.badResponses.push({ status: r.status, url: r.url, type: p.type, page: this.url, at: Date.now() });
            }
        });
        this.on('Network.requestWillBeSent', (p) => {
            const r = p.request || {};
            this.requests.push({ method: r.method, url: r.url, type: p.type, page: this.url, at: Date.now() });
        });
        this.on('Fetch.requestPaused', (p) => { this.onPaused(p); });
        this.on('Network.loadingFailed', (p) => {
            this.failedRequests.push({ error: p.errorText, canceled: !!p.canceled, type: p.type, requestId: p.requestId, page: this.url, at: Date.now() });
        });
        this.on('Page.javascriptDialogOpening', (p) => {
            this.dialogs.push({ type: p.type, message: p.message, page: this.url, at: Date.now() });
            this.send('Page.handleJavaScriptDialog', { accept: true }).catch(() => { /* already closed */ });
        });
        this.on('Page.frameNavigated', (p) => {
            if (p.frame && !p.frame.parentId) {
                this.url = p.frame.url + (p.frame.urlFragment || '');
            }
        });
        this.on('Page.navigatedWithinDocument', (p) => {
            this.url = p.url;
        });
        await Promise.all([
            this.send('Page.enable'),
            this.send('Runtime.enable'),
            this.send('Network.enable'),
            this.send('Log.enable'),
            this.send('DOM.enable')
        ]);
        // Headless tabs count as unfocused unless told otherwise; keep focus and timers behaving like a window in front.
        await this.send('Emulation.setFocusEmulationEnabled', { enabled: true }).catch(() => {});
    }

    /**
     * Rewrites the responses whose URL matches `pattern` (a Fetch urlPattern: * stands for any text, as in "http*Babelfin/Admin/Simulate*"):
     * transform(text, { url, method, status }) returns the new body text, or null/undefined to pass the response through.
     * Returns a function that stops the interception.
     */
    async interceptResponses(pattern, transform) {
        const entry = { pattern: pattern, transform: transform, hits: 0, rewritten: 0 };
        this.interceptors.push(entry);
        await this.applyInterception();
        return async () => {
            const i = this.interceptors.indexOf(entry);
            if (i >= 0) {
                this.interceptors.splice(i, 1);
            }
            await this.applyInterception();
            return entry;
        };
    }

    /** Fulfils requests locally, without contacting the server. respond({ url, method, body }) returns
     * { body, status (default 200) }, or null to pass through. A broken mock fails closed and is reported by hygiene.
     * Returns an idempotent stop function with hit/fulfilled counts. Used only by loopback regression fixtures. */
    async mockResponses(pattern, respond) {
        const entry = { pattern: pattern, respond: respond, hits: 0, fulfilled: 0 };
        this.mockResponders.push(entry);
        await this.applyInterception();
        return async () => {
            const i = this.mockResponders.indexOf(entry);
            if (i >= 0) this.mockResponders.splice(i, 1);
            await this.applyInterception();
            return entry;
        };
    }

    async applyInterception() {
        if (!this.interceptors.length && !this.mockResponders.length) {
            await this.send('Fetch.disable').catch(() => {});
            return;
        }
        const patterns = this.interceptors.map((x) => ({ urlPattern: x.pattern, requestStage: 'Response' }))
            .concat(this.mockResponders.map((x) => ({ urlPattern: x.pattern, requestStage: 'Request' })));
        await this.send('Fetch.enable', { patterns: patterns });
    }

    async onPaused(p) {
        const url = (p.request && p.request.url) || '';
        const mock = p.responseStatusCode === undefined && this.mockResponders.find((x) => globMatch(x.pattern, url));
        if (mock) {
            mock.hits += 1;
            try {
                const out = await mock.respond({ url: url, method: p.request.method, body: p.request.postData, headers: p.request.headers || {} });
                if (out === null || out === undefined) {
                    await this.send('Fetch.continueRequest', { requestId: p.requestId });
                } else {
                    await this.send('Fetch.fulfillRequest', {
                        requestId: p.requestId, responseCode: out.status === undefined ? 200 : out.status,
                        responseHeaders: [{ name: 'Content-Type', value: 'application/json; charset=utf-8' }],
                        body: Buffer.from(typeof out.body === 'string' ? out.body : JSON.stringify(out.body ?? null), 'utf8').toString('base64')
                    });
                    mock.fulfilled += 1;
                }
            } catch (e) {
                this.exceptions.push({ text: 'Babelfin test mock failed: ' + e.message, url: url, frames: [], page: this.url, at: Date.now() });
                await this.send('Fetch.failRequest', { requestId: p.requestId, errorReason: 'Failed' }).catch(() => {});
            }
            return;
        }
        const entry = this.interceptors.find((x) => globMatch(x.pattern, url));
        try {
            if (!entry || p.responseStatusCode === undefined || p.responseErrorReason) {
                await this.send('Fetch.continueRequest', { requestId: p.requestId });
                return;
            }
            entry.hits += 1;
            const got = await this.send('Fetch.getResponseBody', { requestId: p.requestId });
            const text = got.base64Encoded ? Buffer.from(got.body, 'base64').toString('utf8') : got.body;
            let out = null;
            try {
                out = entry.transform(text, { url: url, method: p.request.method, status: p.responseStatusCode });
            } catch (e) {
                out = null;
            }
            if (out === null || out === undefined) {
                await this.send('Fetch.continueResponse', { requestId: p.requestId }).catch(() => this.send('Fetch.continueRequest', { requestId: p.requestId }));
                return;
            }
            entry.rewritten += 1;
            const headers = (p.responseHeaders || []).filter((h) => !/^(content-length|content-encoding|etag)$/i.test(h.name));
            await this.send('Fetch.fulfillRequest', {
                requestId: p.requestId,
                responseCode: p.responseStatusCode,
                responseHeaders: headers,
                body: Buffer.from(String(out), 'utf8').toString('base64')
            });
        } catch (e) {
            await this.send('Fetch.continueRequest', { requestId: p.requestId }).catch(() => {});
        }
    }

    /** Goes to a URL and waits for its load event (a same-document change, such as a new #hash, resolves once committed). */
    async navigate(url, opts) {
        const timeout = (opts && opts.timeout) || 45000;
        let loaded;
        const navigate = () => {
            loaded = this.once('Page.loadEventFired', timeout, 'the load event of ' + url);
            loaded.catch(() => {});
            return this.send('Page.navigate', { url: url });
        };
        const result = await (this.chrome.coverage ? this.chrome.coverage.snapshotThen(navigate) : navigate());
        if (result.errorText) {
            throw new Error('navigation to ' + url + ' failed: ' + result.errorText);
        }
        if (result.loaderId) {
            await loaded;
        }
        this.url = url;
        return result;
    }

    async reload(opts) {
        const timeout = (opts && opts.timeout) || 45000;
        let loaded;
        const reload = () => {
            loaded = this.once('Page.loadEventFired', timeout, 'the load event after a reload');
            loaded.catch(() => {});
            return this.send('Page.reload', { ignoreCache: true });
        };
        await (this.chrome.coverage ? this.chrome.coverage.snapshotThen(reload) : reload());
        await loaded;
    }

    /** Runs a function (called with JSON arguments) or an expression in the page; promises are awaited; the result is returned by value. */
    async evaluate(fnOrExpr, ...args) {
        const result = await this.send('Runtime.evaluate', {
            expression: expressionOf(fnOrExpr, args),
            awaitPromise: true,
            returnByValue: true,
            userGesture: true
        });
        if (result.exceptionDetails) {
            const d = result.exceptionDetails;
            const message = (d.exception && (d.exception.description || d.exception.value)) || d.text || 'evaluation failed';
            throw new Error('in page: ' + message);
        }
        return result.result ? result.result.value : undefined;
    }

    /** Like evaluate, but returns a remote object id (for elements), or null. */
    async handle(fnOrExpr, ...args) {
        const result = await this.send('Runtime.evaluate', { expression: expressionOf(fnOrExpr, args), awaitPromise: true, returnByValue: false });
        if (result.exceptionDetails) {
            throw new Error('in page: ' + ((result.exceptionDetails.exception && result.exceptionDetails.exception.description) || result.exceptionDetails.text));
        }
        const r = result.result;
        return r && r.objectId && r.subtype !== 'null' ? r.objectId : null;
    }

    /**
     * Polls until the function/expression returns something truthy and returns it. Errors while the page navigates (a destroyed
     * context) are retried. opts: { timeout, interval, what }.
     */
    async waitFor(fnOrExpr, opts, ...args) {
        const o = Object.assign({ timeout: this.defaultTimeout, interval: 100, what: null }, opts || {});
        const deadline = Date.now() + o.timeout;
        // A DOM node (or a list of them) can't be returned by value: report it as true.
        const expression = 'Promise.resolve(' + expressionOf(fnOrExpr, args) + ').then(function (v) {'
            + ' return v && typeof v === "object" && (v.nodeType || typeof v.length === "number" && v.item) ? true : v; })';
        let lastError = null;
        for (;;) {
            try {
                const value = await this.evaluate(expression);
                if (value) {
                    return value;
                }
                lastError = null;
            } catch (e) {
                lastError = e;
            }
            if (Date.now() > deadline) {
                const what = o.what || (typeof fnOrExpr === 'function' ? fnOrExpr.toString().slice(0, 120) : String(fnOrExpr));
                throw new Error('timeout after ' + o.timeout + ' ms waiting for ' + what + (lastError ? ' (last error: ' + lastError.message + ')' : ''));
            }
            await sleep(o.interval);
        }
    }

    /** Waits for an element matching the CSS selector; visible (default true) also wants a rendered, non-hidden box. */
    waitForSelector(selector, opts) {
        const o = Object.assign({ visible: true }, opts || {});
        return this.waitFor(function (sel, visible) {
            const all = Array.prototype.slice.call(document.querySelectorAll(sel));
            return all.some(function (el) {
                if (!visible) {
                    return true;
                }
                const r = el.getBoundingClientRect();
                const cs = getComputedStyle(el);
                return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none';
            });
        }, Object.assign({ what: 'selector ' + selector + (o.visible ? ' (visible)' : '') }, o), selector, !!o.visible);
    }

    /**
     * Clicks an element like a person: scrolls it into view, finds its box (DOM.getBoxModel), checks the point isn't covered by
     * another element, then sends mouse move, press and release at the box centre. target: a CSS selector (first visible match)
     * or a page function returning the element (opts.args are its arguments; the element may sit in a same-origin frame, as the
     * settings page does at #/babelfin). opts: { timeout, force (skip the covered check) }.
     */
    async click(target, opts) {
        // A real pointer click targets the foreground tab; background renders throttle menu animations.
        await this.send('Page.bringToFront');
        const o = Object.assign({ timeout: 10000, force: false, args: [] }, opts || {});
        const finder = typeof target === 'function' ? target : function (sel) {
            const all = Array.prototype.slice.call(document.querySelectorAll(sel));
            return all.find(function (el) { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; }) || all[0] || null;
        };
        const args = typeof target === 'function' ? o.args : [target];
        const what = typeof target === 'function' ? (o.what || 'element') : target;
        const deadline = Date.now() + o.timeout;
        let lastProblem = 'not found';
        for (;;) {
            const objectId = await this.handle(finder, ...args).catch(() => null);
            if (objectId) {
                try {
                    await this.send('DOM.scrollIntoViewIfNeeded', { objectId: objectId });
                    const box = await this.send('DOM.getBoxModel', { objectId: objectId });
                    const q = box.model.border;
                    const x = (q[0] + q[2] + q[4] + q[6]) / 4;
                    const y = (q[1] + q[3] + q[5] + q[7]) / 4;
                    const hit = await this.send('Runtime.callFunctionOn', {
                        objectId: objectId,
                        functionDeclaration: HIT_TEST,
                        arguments: [{ value: x }, { value: y }],
                        returnByValue: true
                    });
                    const verdict = hit.result && hit.result.value;
                    if (verdict === 'ok' || o.force) {
                        await this.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x, y: y, button: 'none' });
                        // Hover can finish Jellyfin's opening transform or change layout. The old
                        // point may now hit another menu command: measure again before pressing.
                        await this.send('Runtime.callFunctionOn', {
                            objectId: objectId,
                            functionDeclaration: 'function () { return new Promise(function (resolve) { requestAnimationFrame(function () { requestAnimationFrame(resolve); }); }); }',
                            awaitPromise: true, returnByValue: true
                        });
                        const settled = await this.send('DOM.getBoxModel', { objectId: objectId });
                        const moved = settled.model.border.some((coordinate, i) => Math.abs(coordinate - q[i]) > 0.5);
                        if (moved) {
                            lastProblem = 'element moved after pointer movement';
                        } else {
                            const checked = await this.send('Runtime.callFunctionOn', {
                                objectId: objectId, functionDeclaration: HIT_TEST,
                                arguments: [{ value: x }, { value: y }], returnByValue: true
                            });
                            const freshVerdict = checked.result && checked.result.value;
                            if (freshVerdict === 'ok' || o.force) {
                                await this.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: x, y: y, button: 'left', buttons: 1, clickCount: 1 });
                                await this.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: x, y: y, button: 'left', buttons: 0, clickCount: 1 });
                                return { x: x, y: y };
                            }
                            lastProblem = 'covered after pointer movement by ' + freshVerdict;
                        }
                    } else {
                        lastProblem = 'covered by ' + verdict + ' at ' + Math.round(x) + ',' + Math.round(y);
                    }
                } catch (e) {
                    lastProblem = e.message;
                } finally {
                    this.send('Runtime.releaseObject', { objectId: objectId }).catch(() => {});
                }
            }
            if (Date.now() > deadline) {
                throw new Error('could not click ' + what + ': ' + lastProblem);
            }
            await sleep(150);
        }
    }

    /** Focuses the first element matching the selector (what a Tab press would land on) without clicking it. */
    async focus(selector) {
        const ok = await this.evaluate(function (sel) {
            const el = document.querySelector(sel);
            if (!el) {
                return false;
            }
            el.scrollIntoView({ block: 'center' });
            el.focus();
            return document.activeElement === el;
        }, selector);
        if (!ok) {
            throw new Error('could not focus ' + selector);
        }
    }

    /** Types text into the focused element, one key press per character. */
    async type(text) {
        for (const ch of String(text)) {
            await this.send('Input.dispatchKeyEvent', { type: 'keyDown', key: ch, text: ch, unmodifiedText: ch });
            await this.send('Input.dispatchKeyEvent', { type: 'keyUp', key: ch });
        }
    }

    /** Presses a named key (Enter, Escape, Tab, ArrowDown, ArrowUp, Home, End, Space, ContextMenu, ...); modifiers: 1 Alt, 2 Ctrl, 4 Meta, 8 Shift. */
    async press(name, opts) {
        const k = KEYS[name];
        if (!k) {
            throw new Error('unknown key ' + name);
        }
        const modifiers = (opts && opts.modifiers) || 0;
        const base = { key: k.key, code: k.code, windowsVirtualKeyCode: k.keyCode, nativeVirtualKeyCode: k.keyCode, modifiers: modifiers };
        await this.send('Input.dispatchKeyEvent', Object.assign({ type: k.text ? 'keyDown' : 'rawKeyDown' }, base, k.text ? { text: k.text, unmodifiedText: k.text } : {}));
        await this.send('Input.dispatchKeyEvent', Object.assign({ type: 'keyUp' }, base));
    }

    setViewport(width, height, mobile) {
        return this.send('Emulation.setDeviceMetricsOverride', { width: width, height: height, deviceScaleFactor: 1, mobile: !!mobile });
    }

    async clearViewport() {
        await this.send('Emulation.clearDeviceMetricsOverride');
    }

    /** Saves a PNG of the viewport; returns the file path, or null when the page can't be captured. */
    /** Saves a PNG of the viewport, or of the whole page when `full` (null when it fails). */
    async screenshot(file, full) {
        try {
            const params = { format: 'png' };
            if (full) {
                const m = await this.send('Page.getLayoutMetrics');
                const size = m.cssContentSize || m.contentSize;
                params.captureBeyondViewport = true;
                params.clip = { x: 0, y: 0, width: Math.ceil(size.width), height: Math.min(Math.ceil(size.height), 16000), scale: 1 };
            }
            const shot = await Promise.race([this.send('Page.captureScreenshot', params), sleep(10000).then(() => null)]);
            if (!shot) {
                return null;
            }
            fs.mkdirSync(path.dirname(file), { recursive: true });
            fs.writeFileSync(file, Buffer.from(shot.data, 'base64'));
            return file;
        } catch (e) {
            return null;
        }
    }
}

class Chrome {
    constructor() {
        this.proc = null;
        this.exited = false;
        this.profile = null;
        this.ws = null;
        this.nextId = 1;
        this.commandTimeout = 45000;
        this.pending = new Map();
        this.pages = new Map();
        this.closed = false;
        this.exitHandler = () => this.killNow();
        this.signalHandler = () => {
            this.killNow();
            process.exit(130);
        };
    }

    /** Starts Chrome. Only a DevTools port-file timeout is retried, with a new process and profile. */
    static async launch(opts) {
        const o = Object.assign({ headless: true, width: 1280, height: 900, chromePath: CHROME_PATH, args: [],
            startupTimeout: 30000, startupPollInterval: 100 }, opts || {});
        let firstTimeout;
        for (let attempt = 0; attempt < 2; attempt++) {
            const chrome = new Chrome();
            process.on('exit', chrome.exitHandler);
            process.on('SIGINT', chrome.signalHandler);
            process.on('SIGTERM', chrome.signalHandler);
            try {
                await chrome.start(o);
                return chrome;
            } catch (error) {
                chrome.killNow();
                if (!error.startupTimeout || attempt || chrome.profileRemoved === false || chrome.processCleanupSucceeded === false) {
                    if (chrome.processCleanupSucceeded === false) error.message += '\nChrome process tree cleanup could not be confirmed';
                    if (chrome.profileRemoved === false) error.message += '\nChrome profile cleanup failed: ' + chrome.profile;
                    if (firstTimeout) error.message += '\nFirst startup attempt: ' + firstTimeout.message;
                    throw error;
                }
                firstTimeout = error;
                console.warn('[browser] ' + error.message + '; retrying Chrome startup with a fresh profile');
            }
        }
    }

    async start(o) {
        if (!fs.existsSync(o.chromePath)) {
            throw new Error('Chrome not found at ' + o.chromePath + ' (set CHROME_PATH)');
        }
        this.killProcessTree = o._killTree || killTree;
        this.profile = fs.mkdtempSync(path.join(os.tmpdir(), 'babelfin-e2e-chrome-'));
        const args = [
            '--remote-debugging-port=0',
            '--user-data-dir=' + this.profile,
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-networking',
            '--disable-background-timer-throttling',
            '--disable-backgrounding-occluded-windows',
            '--disable-renderer-backgrounding',
            '--disable-component-update',
            '--disable-default-apps',
            '--disable-extensions',
            '--disable-sync',
            '--disable-features=Translate,MediaRouter,OptimizationHints',
            '--mute-audio',
            '--password-store=basic',
            '--lang=en-US',
            '--window-size=' + o.width + ',' + o.height
        ];
        if (o.headless) {
            args.unshift('--headless=new');
        }
        args.push(...o.args, 'about:blank');
        this.proc = (o._spawn || spawn)(o.chromePath, args, { stdio: ['ignore', 'ignore', 'pipe'], windowsHide: true, detached: process.platform !== 'win32' });
        let stderr = '';
        let exitDetails = '';
        this.proc.stderr.on('data', (chunk) => { stderr = (stderr + chunk.toString()).slice(-8192); });
        this.proc.on('exit', (code, signal) => {
            this.exited = true;
            exitDetails = 'exit code ' + code + (signal ? ', signal ' + signal : '');
        });
        this.proc.on('error', (error) => { this.exited = true; exitDetails = 'spawn error: ' + error.message; });
        const diagnostics = () => (exitDetails ? ' (' + exitDetails + ')' : '') + (stderr ? '\nChrome stderr (last 8192 characters):\n' + stderr : '');
        const portFile = path.join(this.profile, 'DevToolsActivePort');
        const deadline = Date.now() + o.startupTimeout;
        let lines = null;
        while (Date.now() < deadline) {
            if (this.exited) {
                throw new Error('Chrome exited during startup' + diagnostics());
            }
            try {
                const text = fs.readFileSync(portFile, 'utf8').trim().split(/\r?\n/);
                if (text.length >= 2 && Number(text[0]) > 0) {
                    lines = text;
                    break;
                }
            } catch (e) {
                // not written yet
            }
            await sleep(Math.min(o.startupPollInterval, Math.max(1, deadline - Date.now())));
        }
        if (!lines) {
            if (this.exited) throw new Error('Chrome exited during startup' + diagnostics());
            const error = new Error('Chrome did not write DevToolsActivePort within ' + o.startupTimeout + ' ms' + diagnostics());
            error.startupTimeout = true;
            throw error;
        }
        this.port = Number(lines[0]);
        await this.connect('ws://127.0.0.1:' + this.port + lines[1]);
        // Coverage is a private, opt-in sidecar; normal browser runs load no collector.
        if (process.env.BF_COVERAGE_DIR) {
            const { BrowserCoverage } = require('../../coverage/cdp-coverage');
            this.coverage = new BrowserCoverage(this, process.env.BF_COVERAGE_ROOT || path.resolve(__dirname, '../../..'), process.env.BF_COVERAGE_DIR);
            await this.coverage.start();
        }
    }

    connect(url) {
        return new Promise((resolve, reject) => {
            const ws = new WebSocket(url);
            this.ws = ws;
            ws.onopen = () => resolve();
            ws.onerror = (e) => reject(new Error('could not connect to Chrome: ' + (e && e.message ? e.message : 'socket error')));
            ws.onclose = () => {
                this.pending.forEach((p) => p.reject(new Error(p.method + ': the Chrome connection closed')));
                this.pending.clear();
            };
            ws.onmessage = (event) => {
                let msg;
                try {
                    msg = JSON.parse(typeof event.data === 'string' ? event.data : Buffer.from(event.data).toString('utf8'));
                } catch (e) {
                    return;
                }
                if (msg.id !== undefined) {
                    const p = this.pending.get(msg.id);
                    if (p) {
                        this.pending.delete(msg.id);
                        if (msg.error) {
                            p.reject(new Error(p.method + ': ' + msg.error.message + (msg.error.data ? ' (' + msg.error.data + ')' : '')));
                        } else {
                            p.resolve(msg.result || {});
                        }
                    }
                    return;
                }
                if (msg.method === 'Target.detachedFromTarget') {
                    const detached = msg.params && msg.params.sessionId;
                    for (const pending of this.pending.values()) {
                        if (detached && pending.sessionId === detached) pending.reject(new Error(pending.method + ': target session detached'));
                    }
                }
                if (this.coverage) this.coverage.event(msg);
                if (msg.sessionId && this.pages.has(msg.sessionId)) {
                    this.pages.get(msg.sessionId).dispatch(msg.method, msg.params || {});
                }
            };
        });
    }

    send(method, params, sessionId) {
        if (!this.ws || this.ws.readyState !== 1) {
            return Promise.reject(new Error(method + ': Chrome is not connected'));
        }
        const id = this.nextId++;
        const msg = { id: id, method: method, params: params || {} };
        if (sessionId) {
            msg.sessionId = sessionId;
        }
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => fail(new Error(method + ': command timed out after ' + this.commandTimeout + ' ms')), this.commandTimeout);
            const settle = (callback) => (value) => { clearTimeout(timer); this.pending.delete(id); callback(value); };
            const fail = settle(reject);
            this.pending.set(id, { resolve: settle(resolve), reject: fail, method: method, sessionId: sessionId });
            try { this.ws.send(JSON.stringify(msg)); } catch (error) { fail(error); }
        });
    }

    /** The tab Chrome opened at startup, attached and ready. */
    async firstPage() {
        const { targetInfos } = await this.send('Target.getTargets');
        let target = targetInfos.find((t) => t.type === 'page');
        if (!target) {
            const created = await this.send('Target.createTarget', { url: 'about:blank' });
            target = { targetId: created.targetId };
        }
        return this.attach(target.targetId);
    }

    /** A new tab. */
    async newPage() {
        const created = await this.send('Target.createTarget', { url: 'about:blank' });
        return this.attach(created.targetId);
    }

    async attach(targetId) {
        const { sessionId } = await this.send('Target.attachToTarget', { targetId: targetId, flatten: true });
        const page = new Page(this, targetId, sessionId);
        this.pages.set(sessionId, page);
        await page.init();
        return page;
    }

    async closePage(page) {
        const close = () => this.send('Target.closeTarget', { targetId: page.targetId }).catch(() => {});
        await (this.coverage ? this.coverage.snapshotThen(close) : close());
        this.pages.delete(page.sessionId);
    }

    /** Kills Chrome's process tree and deletes the temporary profile (synchronous; also the process-exit handler). */
    killNow() {
        if (this.closed) {
            return;
        }
        this.closed = true;
        if (this.coverage) {
            if (!this.coverageFinished) this.coverage.errors.push('Synchronous shutdown: final browser coverage snapshot unavailable');
            try { this.coverage.flush(); } catch (error) { console.warn('[coverage] final flush failed: ' + error.message); }
        }
        try { if (this.ws) { this.ws.close(); } } catch (e) { /* closed */ }
        if (this.proc && !this.exited) {
            this.processCleanupSucceeded = (this.killProcessTree || killTree)(this.proc.pid);
        }
        if (this.profile) {
            this.profileRemoved = removeDir(this.profile);
        }
        process.removeListener('exit', this.exitHandler);
        process.removeListener('SIGINT', this.signalHandler);
        process.removeListener('SIGTERM', this.signalHandler);
    }

    /** Asks Chrome to close, then kills whatever is left and deletes the temporary profile. Safe to call twice. */
    async close() {
        if (this.closed) return;
        try {
            if (this.coverage) {
                await this.coverage.take();
                this.coverageFinished = true;
            }
            try {
                if (this.ws && this.ws.readyState === 1) {
                    await Promise.race([this.send('Browser.close').catch(() => {}), sleep(3000)]);
                }
            } catch (e) { /* already gone */ }
            if (this.proc && !this.exited) {
                await Promise.race([new Promise((resolve) => this.proc.once('exit', resolve)), sleep(3000)]);
            }
        } finally {
            this.killNow();
        }
    }
}

module.exports = { Chrome, Page, sleep, KEYS };
