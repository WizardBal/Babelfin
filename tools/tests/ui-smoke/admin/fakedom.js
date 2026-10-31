// A small fake DOM for the Babelfin admin page smoke test: elements, text, events, a CSS selector subset and an HTML parser.
'use strict';

class Event {
    constructor(type, init) {
        this.type = type;
        this.bubbles = !!(init && init.bubbles);
        this.defaultPrevented = false;
        this.target = null;
        this.currentTarget = null;
        this._stop = false;
        Object.assign(this, init, init && init.extra);
    }
    preventDefault() { this.defaultPrevented = true; }
    stopPropagation() { this._stop = true; }
    stopImmediatePropagation() { this._stop = true; this._immediate = true; }
}

class Node {
    constructor(doc) {
        this.ownerDocument = doc;
        this.parentNode = null;
        this.childNodes = [];
        this._listeners = {};
        this._captureListeners = {};
    }
    get firstChild() { return this.childNodes[0] || null; }
    get lastChild() { return this.childNodes[this.childNodes.length - 1] || null; }
    get nextSibling() {
        if (!this.parentNode) { return null; }
        const s = this.parentNode.childNodes;
        return s[s.indexOf(this) + 1] || null;
    }
    get previousSibling() {
        if (!this.parentNode) { return null; }
        const s = this.parentNode.childNodes;
        return s[s.indexOf(this) - 1] || null;
    }
    get parentElement() { return this.parentNode && this.parentNode.nodeType === 1 ? this.parentNode : null; }
    appendChild(n) {
        if (n.nodeType === 11) {
            n.childNodes.slice().forEach((c) => this.appendChild(c));
            return n;
        }
        if (n.contains && n.contains(this)) { throw new Error('HierarchyRequestError'); }
        if (n.parentNode) { n.parentNode.removeChild(n); }
        n.parentNode = this;
        this.childNodes.push(n);
        if (this.ownerDocument && this.ownerDocument._onInsert) { this.ownerDocument._onInsert(n, this); }
        return n;
    }
    insertBefore(n, ref) {
        if (!ref) { return this.appendChild(n); }
        if (n.nodeType === 11) {
            n.childNodes.slice().forEach((c) => this.insertBefore(c, ref));
            return n;
        }
        if (n.parentNode) { n.parentNode.removeChild(n); }
        const i = this.childNodes.indexOf(ref);
        if (i < 0) { throw new Error('NotFoundError: ref is not a child'); }
        n.parentNode = this;
        this.childNodes.splice(i, 0, n);
        if (this.ownerDocument && this.ownerDocument._onInsert) { this.ownerDocument._onInsert(n, this); }
        return n;
    }
    removeChild(n) {
        const i = this.childNodes.indexOf(n);
        if (i < 0) { throw new Error('NotFoundError: not a child'); }
        this.childNodes.splice(i, 1);
        n.parentNode = null;
        return n;
    }
    replaceChild(n, old) {
        this.insertBefore(n, old);
        this.removeChild(old);
        return old;
    }
    remove() { if (this.parentNode) { this.parentNode.removeChild(this); } }
    contains(n) {
        while (n) {
            if (n === this) { return true; }
            n = n.parentNode;
        }
        return false;
    }
    get isConnected() {
        let n = this;
        while (n.parentNode) { n = n.parentNode; }
        return n === this.ownerDocument;
    }
    addEventListener(type, fn, opts) {
        if (!fn) { return; }
        const listeners = opts === true || (opts && opts.capture) ? this._captureListeners : this._listeners;
        const list = listeners[type] = listeners[type] || [];
        if (!list.includes(fn)) { list.push(fn); }
    }
    removeEventListener(type, fn, opts) {
        const listeners = opts === true || (opts && opts.capture) ? this._captureListeners : this._listeners;
        const l = listeners[type];
        if (l) { const i = l.indexOf(fn); if (i >= 0) { l.splice(i, 1); } }
    }
    dispatchEvent(ev) {
        ev.target = ev.target || this;
        const path = [];
        for (let n = this; n; n = n.parentNode) { path.push(n); }
        function invoke(node, capture) {
            ev.currentTarget = node;
            ev.eventPhase = node === ev.target ? 2 : capture ? 1 : 3;
            for (const fn of ((capture ? node._captureListeners : node._listeners)[ev.type] || []).slice()) {
                if (typeof fn === 'function') { fn.call(node, ev); } else if (fn && fn.handleEvent) { fn.handleEvent(ev); }
                if (ev._immediate) { break; }
            }
            const prop = node['on' + ev.type];
            if (!capture && !ev._immediate && typeof prop === 'function' && node.nodeType === 1 && !node._listeners['__prop_' + ev.type]) { prop.call(node, ev); }
        }
        for (const node of path.slice().reverse()) {
            invoke(node, true);
            if (ev._stop) { return !ev.defaultPrevented; }
        }
        for (const node of path) {
            invoke(node, false);
            if (!ev.bubbles || ev._stop) { break; }
        }
        ev.eventPhase = 0;
        return !ev.defaultPrevented;
    }
    get textContent() { return this.childNodes.map((c) => c.textContent).join(''); }
    set textContent(v) {
        this.childNodes.forEach((c) => { c.parentNode = null; });
        this.childNodes = [];
        if (v !== '' && v !== null && v !== undefined) { this.appendChild(this.ownerDocument.createTextNode(String(v))); }
    }
}

class Text extends Node {
    constructor(doc, data) { super(doc); this.nodeType = 3; this.data = String(data); this.nodeName = '#text'; }
    get textContent() { return this.data; }
    set textContent(v) { this.data = String(v); }
    get nodeValue() { return this.data; }
}

class Fragment extends Node {
    constructor(doc) { super(doc); this.nodeType = 11; }
}

const VOID = new Set(['input', 'br', 'meta', 'link', 'img', 'hr', 'source', 'col', 'wbr', 'area', 'base']);

class ClassList {
    constructor(el) { this.el = el; }
    _get() { return (this.el.getAttribute('class') || '').split(/\s+/).filter(Boolean); }
    _set(list) { this.el.setAttribute('class', list.join(' ')); }
    add(...c) { const l = this._get(); c.forEach((x) => { if (l.indexOf(x) < 0) { l.push(x); } }); this._set(l); }
    remove(...c) { this._set(this._get().filter((x) => c.indexOf(x) < 0)); }
    contains(c) { return this._get().indexOf(c) >= 0; }
    toggle(c, force) {
        const has = this.contains(c);
        const want = force === undefined ? !has : !!force;
        if (want && !has) { this.add(c); } else if (!want && has) { this.remove(c); }
        return want;
    }
    get length() { return this._get().length; }
}

class Element extends Node {
    constructor(doc, tag) {
        super(doc);
        this.nodeType = 1;
        this.localName = String(tag).toLowerCase();
        this.tagName = this.localName.toUpperCase();
        this.attributes = new Map();
        this.style = {};
        this._value = null;
        this._checked = false;
        this._selectedIndex = -1;
        this._dataset = null;
    }
    get nodeName() { return this.tagName; }
    setAttribute(k, v) {
        k = String(k).toLowerCase();
        this.attributes.set(k, String(v));
        if (k === 'checked' && this.localName === 'input') { this._checked = true; }
    }
    getAttribute(k) { k = String(k).toLowerCase(); return this.attributes.has(k) ? this.attributes.get(k) : null; }
    hasAttribute(k) { return this.attributes.has(String(k).toLowerCase()); }
    removeAttribute(k) { this.attributes.delete(String(k).toLowerCase()); }
    toggleAttribute(k, force) { const want = force === undefined ? !this.hasAttribute(k) : force; if (want) { this.setAttribute(k, ''); } else { this.removeAttribute(k); } return want; }
    get id() { return this.getAttribute('id') || ''; }
    set id(v) { this.setAttribute('id', v); }
    get className() { return this.getAttribute('class') || ''; }
    set className(v) { this.setAttribute('class', v); }
    get classList() { return new ClassList(this); }
    get dataset() {
        const el = this;
        return new Proxy({}, {
            get(_, p) { return el.getAttribute('data-' + String(p).replace(/[A-Z]/g, (m) => '-' + m.toLowerCase())) ?? undefined; },
            set(_, p, v) { el.setAttribute('data-' + String(p).replace(/[A-Z]/g, (m) => '-' + m.toLowerCase()), v); return true; },
            ownKeys() { return [...el.attributes.keys()].filter((k) => k.startsWith('data-')).map((k) => k.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())); },
            getOwnPropertyDescriptor() { return { enumerable: true, configurable: true }; }
        });
    }
    get hidden() { return this.hasAttribute('hidden'); }
    set hidden(v) { if (v) { this.setAttribute('hidden', ''); } else { this.removeAttribute('hidden'); } }
    get disabled() { return this.hasAttribute('disabled'); }
    set disabled(v) { if (v) { this.setAttribute('disabled', ''); } else { this.removeAttribute('disabled'); } }
    get open() { return this.hasAttribute('open'); }
    set open(v) {
        const was = this.open;
        if (v) { this.setAttribute('open', ''); } else { this.removeAttribute('open'); }
        if (was !== !!v && this.localName === 'details') {
            setTimeout(() => this.dispatchEvent(new Event('toggle')), 0);
        }
    }
    get type() { return (this.getAttribute('type') || (this.localName === 'button' ? 'submit' : 'text')).toLowerCase(); }
    set type(v) { this.setAttribute('type', v); }
    get href() { return this.getAttribute('href') || ''; }
    set href(v) { this.setAttribute('href', v); }
    get src() { return this.getAttribute('src') || ''; }
    set src(v) { this.setAttribute('src', v); }
    get rel() { return this.getAttribute('rel') || ''; }
    set rel(v) { this.setAttribute('rel', v); }
    get placeholder() { return this.getAttribute('placeholder') || ''; }
    set placeholder(v) { this.setAttribute('placeholder', v); }
    get title() { return this.getAttribute('title') || ''; }
    set title(v) { this.setAttribute('title', v); }
    get name() { return this.getAttribute('name') || ''; }
    set name(v) { this.setAttribute('name', v); }
    get htmlFor() { return this.getAttribute('for') || ''; }
    set htmlFor(v) { this.setAttribute('for', v); }
    get checked() { return this._checked; }
    set checked(v) { this._checked = !!v; }
    get multiple() { return this.hasAttribute('multiple'); }
    set multiple(v) { if (v) { this.setAttribute('multiple', ''); } else { this.removeAttribute('multiple'); } }
    get selected() {
        if (this.localName !== 'option') { return false; }
        const sel = this.closest('select');
        return sel ? sel.options[sel.selectedIndex] === this : !!this._sel;
    }
    set selected(v) {
        const sel = this.closest('select');
        if (sel && v) { sel.selectedIndex = sel.options.indexOf(this); } else { this._sel = !!v; }
    }
    get options() { return this.querySelectorAll('option'); }
    get selectedIndex() {
        const opts = this.options;
        if (this._selectedIndex >= opts.length) { this._selectedIndex = opts.length ? 0 : -1; }
        if (this._selectedIndex < 0 && opts.length && !this._explicitNone) { return 0; }
        return this._selectedIndex;
    }
    set selectedIndex(i) { this._selectedIndex = i; this._explicitNone = i < 0; }
    get value() {
        if (this.localName === 'select') {
            const o = this.options[this.selectedIndex];
            return o ? o.value : '';
        }
        if (this.localName === 'option') {
            return this.hasAttribute('value') ? this.getAttribute('value') : this.textContent;
        }
        if (this.localName === 'input' && (this.type === 'checkbox' || this.type === 'radio')) {
            return this.hasAttribute('value') ? this.getAttribute('value') : 'on';
        }
        if (this._value === null) { return this.localName === 'textarea' ? this.textContent : (this.getAttribute('value') || ''); }
        return this._value;
    }
    set value(v) {
        if (this.localName === 'select') {
            const i = this.options.findIndex((o) => o.value === String(v));
            this.selectedIndex = i;
            return;
        }
        if (this.localName === 'option') { this.setAttribute('value', v); return; }
        this._value = v === null || v === undefined ? '' : String(v);
    }
    get text() { return this.textContent; }
    set text(v) { this.textContent = v; }
    get children() { return this.childNodes.filter((n) => n.nodeType === 1); }
    get firstElementChild() { return this.children[0] || null; }
    get lastElementChild() { const c = this.children; return c[c.length - 1] || null; }
    get nextElementSibling() {
        if (!this.parentNode) { return null; }
        const s = this.parentNode.childNodes.filter((n) => n.nodeType === 1);
        return s[s.indexOf(this) + 1] || null;
    }
    get innerHTML() { return this.childNodes.map(serialize).join(''); }
    set innerHTML(html) {
        this.textContent = '';
        parseInto(this.ownerDocument, String(html), this);
    }
    get outerHTML() { return serialize(this); }
    get innerText() { return this.textContent; }
    set innerText(v) { this.textContent = v; }
    focus() { this.ownerDocument.activeElement = this; }
    blur() { if (this.ownerDocument.activeElement === this) { this.ownerDocument.activeElement = this.ownerDocument.body; } }
    click() {
        if (this.disabled) { return; }
        if (this.localName === 'input' && this.type === 'checkbox') {
            this._checked = !this._checked;
            this.dispatchEvent(new Event('click', { bubbles: true }));
            this.dispatchEvent(new Event('input', { bubbles: true }));
            this.dispatchEvent(new Event('change', { bubbles: true }));
            return;
        }
        this.dispatchEvent(new Event('click', { bubbles: true }));
    }
    scrollIntoView() { }
    getBoundingClientRect() { return { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0 }; }
    // No layout (for BabelfinUI.infoTip's placement): every box is empty.
    get offsetWidth() { return 0; }
    get offsetHeight() { return 0; }
    get files() { return this._files || null; }
    querySelectorAll(sel) {
        const groups = parseSelector(sel);
        const out = [];
        walk(this, (el) => { if (groups.some((g) => matchComplex(el, g, this))) { out.push(el); } });
        return out;
    }
    querySelector(sel) { return this.querySelectorAll(sel)[0] || null; }
    matches(sel) { return parseSelector(sel).some((g) => matchComplex(this, g, null)); }
    closest(sel) {
        for (let n = this; n && n.nodeType === 1; n = n.parentNode) {
            if (n.matches(sel)) { return n; }
        }
        return null;
    }
}

function walk(root, fn) {
    for (const c of root.childNodes) {
        if (c.nodeType === 1) { fn(c); walk(c, fn); }
    }
}

/* ---------- selectors: compound (tag #id .class [attr] [attr=v] :not(..) :checked :disabled) joined by ' ' or '>' ---------- */

function splitTop(s, sepChar) {
    const out = [];
    let depth = 0;
    let quote = null;
    let cur = '';
    for (const ch of s) {
        if (quote) { cur += ch; if (ch === quote) { quote = null; } continue; }
        if (ch === '"' || ch === "'") { quote = ch; cur += ch; continue; }
        if (ch === '(' || ch === '[') { depth++; }
        if (ch === ')' || ch === ']') { depth--; }
        if (ch === sepChar && depth === 0) { out.push(cur); cur = ''; continue; }
        cur += ch;
    }
    out.push(cur);
    return out;
}

const selectorCache = new Map();
function parseSelector(sel) {
    if (selectorCache.has(sel)) { return selectorCache.get(sel); }
    const groups = splitTop(sel, ',').map((g) => parseComplex(g.trim()));
    selectorCache.set(sel, groups);
    return groups;
}

function parseComplex(s) {
    // tokens: compound, combinator
    const parts = [];
    let i = 0;
    let cur = '';
    let depth = 0;
    let quote = null;
    const push = (comb) => {
        if (cur.trim()) { parts.push({ compound: parseCompound(cur.trim()), comb: null }); cur = ''; }
        if (comb && parts.length) { parts[parts.length - 1].comb = comb; }
    };
    for (; i < s.length; i++) {
        const ch = s[i];
        if (quote) { cur += ch; if (ch === quote) { quote = null; } continue; }
        if (ch === '"' || ch === "'") { quote = ch; cur += ch; continue; }
        if (ch === '(' || ch === '[') { depth++; }
        if (ch === ')' || ch === ']') { depth--; }
        if (depth === 0 && (ch === '>' || ch === ' ' || ch === '+' || ch === '~')) {
            if (ch === ' ') {
                // look ahead for an explicit combinator
                let j = i;
                while (s[j] === ' ') { j++; }
                if (s[j] === '>' || s[j] === '+' || s[j] === '~') { push(null); i = j - 1; continue; }
                push(' ');
                i = j - 1;
                continue;
            }
            push(ch);
            while (s[i + 1] === ' ') { i++; }
            continue;
        }
        cur += ch;
    }
    push(null);
    return parts;
}

function parseCompound(s) {
    const c = { tag: null, ids: [], classes: [], attrs: [], nots: [], pseudos: [] };
    const re = /^([a-zA-Z*][a-zA-Z0-9-]*)/;
    let m = re.exec(s);
    let i = 0;
    if (m) { c.tag = m[1].toLowerCase(); i = m[1].length; }
    while (i < s.length) {
        const ch = s[i];
        if (ch === '#' || ch === '.') {
            const mm = /^[#.]([a-zA-Z0-9_-]+)/.exec(s.slice(i));
            (ch === '#' ? c.ids : c.classes).push(mm[1]);
            i += mm[0].length;
        } else if (ch === '[') {
            const end = findClose(s, i, '[', ']');
            const inner = s.slice(i + 1, end);
            const am = /^\s*([a-zA-Z0-9_:-]+)\s*(?:([~|^$*]?=)\s*(?:"([^"]*)"|'([^']*)'|([^\]\s]*)))?\s*$/.exec(inner);
            c.attrs.push({ name: am[1].toLowerCase(), op: am[2] || null, value: am[3] !== undefined ? am[3] : am[4] !== undefined ? am[4] : am[5] });
            i = end + 1;
        } else if (ch === ':') {
            const pm = /^:([a-zA-Z-]+)/.exec(s.slice(i));
            const name = pm[1];
            i += pm[0].length;
            if (s[i] === '(') {
                const end = findClose(s, i, '(', ')');
                const arg = s.slice(i + 1, end);
                i = end + 1;
                if (name === 'not') { c.nots.push(parseSelector(arg)); } else { c.pseudos.push({ name: name, arg: arg }); }
            } else {
                c.pseudos.push({ name: name });
            }
        } else {
            throw new Error('Unsupported selector: ' + s);
        }
    }
    return c;
}

function findClose(s, i, open, close) {
    let depth = 0;
    for (let j = i; j < s.length; j++) {
        if (s[j] === open) { depth++; }
        if (s[j] === close) { depth--; if (depth === 0) { return j; } }
    }
    throw new Error('Unclosed ' + open + ' in ' + s);
}

function matchCompound(el, c) {
    if (c.tag && c.tag !== '*' && el.localName !== c.tag) { return false; }
    for (const id of c.ids) { if (el.id !== id) { return false; } }
    if (c.classes.length) { const cl = el.classList; for (const x of c.classes) { if (!cl.contains(x)) { return false; } } }
    for (const a of c.attrs) {
        if (!el.hasAttribute(a.name)) { return false; }
        const v = el.getAttribute(a.name);
        if (a.op === '=' && v !== a.value) { return false; }
        if (a.op === '~=' && v.split(/\s+/).indexOf(a.value) < 0) { return false; }
        if (a.op === '^=' && v.indexOf(a.value) !== 0) { return false; }
        if (a.op === '*=' && v.indexOf(a.value) < 0) { return false; }
    }
    for (const n of c.nots) { if (n.some((g) => matchComplex(el, g, null))) { return false; } }
    for (const p of c.pseudos) {
        if (p.name === 'checked' && !el.checked) { return false; }
        if (p.name === 'disabled' && !el.disabled) { return false; }
        if (p.name === 'first-child' && el.parentNode && el.parentNode.children && el.parentNode.children[0] !== el) { return false; }
        if (p.name === 'scope') { /* matches the root: handled loosely */ }
    }
    return true;
}

function matchComplex(el, parts, scope) {
    // right to left
    let idx = parts.length - 1;
    if (!matchCompound(el, parts[idx].compound)) { return false; }
    let node = el;
    while (idx > 0) {
        const comb = parts[idx - 1].comb || ' ';
        const want = parts[idx - 1].compound;
        if (comb === '>') {
            node = node.parentNode;
            if (!node || node.nodeType !== 1 || !matchCompound(node, want)) { return false; }
        } else if (comb === ' ') {
            node = node.parentNode;
            while (node && node.nodeType === 1 && !matchCompound(node, want)) { node = node.parentNode; }
            if (!node || node.nodeType !== 1) { return false; }
        } else if (comb === '+') {
            node = node.previousSibling;
            while (node && node.nodeType !== 1) { node = node.previousSibling; }
            if (!node || !matchCompound(node, want)) { return false; }
        } else {
            return false;
        }
        idx--;
    }
    return true;
}

/* ---------- serializer and HTML parser ---------- */

function escapeText(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }

function serialize(n) {
    if (n.nodeType === 3) { return escapeText(n.data); }
    if (n.nodeType !== 1) { return ''; }
    const attrs = [...n.attributes].map(([k, v]) => ' ' + k + '="' + String(v).replace(/"/g, '&quot;') + '"').join('');
    if (VOID.has(n.localName)) { return '<' + n.localName + attrs + '>'; }
    return '<' + n.localName + attrs + '>' + n.childNodes.map(serialize).join('') + '</' + n.localName + '>';
}

function decodeEntities(s) {
    return s.replace(/&(#\d+|#x[0-9a-f]+|amp|lt|gt|quot|apos|times|nbsp);/gi, (m, e) => {
        const map = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", times: '×', nbsp: ' ' };
        if (e[0] === '#') { return String.fromCodePoint(e[1] === 'x' || e[1] === 'X' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10)); }
        return map[e.toLowerCase()] || m;
    });
}

function parseInto(doc, html, root) {
    const stack = [root];
    let i = 0;
    const top = () => stack[stack.length - 1];
    while (i < html.length) {
        if (html.startsWith('<!--', i)) {
            const end = html.indexOf('-->', i);
            i = end < 0 ? html.length : end + 3;
            continue;
        }
        if (html.startsWith('<!', i)) {
            i = html.indexOf('>', i) + 1;
            continue;
        }
        if (html[i] === '<' && html[i + 1] === '/') {
            const end = html.indexOf('>', i);
            const tag = html.slice(i + 2, end).trim().toLowerCase();
            for (let k = stack.length - 1; k > 0; k--) {
                if (stack[k].localName === tag) { stack.length = k; break; }
            }
            i = end + 1;
            continue;
        }
        if (html[i] === '<' && /[a-zA-Z]/.test(html[i + 1] || '')) {
            const m = /^<([a-zA-Z][a-zA-Z0-9-]*)/.exec(html.slice(i));
            const tag = m[1].toLowerCase();
            let j = i + m[0].length;
            const el = doc.createElement(tag);
            // attributes
            for (;;) {
                while (/\s/.test(html[j])) { j++; }
                if (html[j] === '>' || (html[j] === '/' && html[j + 1] === '>')) { break; }
                const am = /^([^\s=>\/]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/.exec(html.slice(j));
                if (!am) { j++; continue; }
                const value = am[2] !== undefined ? am[2] : am[3] !== undefined ? am[3] : am[4] !== undefined ? am[4] : '';
                el.setAttribute(am[1], decodeEntities(value));
                j += am[0].length;
            }
            const selfClose = html[j] === '/';
            j = html.indexOf('>', j) + 1;
            top().appendChild(el);
            if (tag === 'script' || tag === 'style') {
                const end = html.toLowerCase().indexOf('</' + tag, j);
                const text = html.slice(j, end);
                if (text) { el.appendChild(doc.createTextNode(text)); }
                j = html.indexOf('>', end) + 1;
            } else if (!VOID.has(tag) && !selfClose) {
                stack.push(el);
            }
            i = j;
            continue;
        }
        const next = html.indexOf('<', i + 1);
        const text = html.slice(i, next < 0 ? html.length : next);
        if (text) { top().appendChild(doc.createTextNode(decodeEntities(text))); }
        i = next < 0 ? html.length : next;
    }
}

class Document extends Node {
    constructor() {
        super(null);
        this.ownerDocument = this;
        this.nodeType = 9;
        this.documentElement = new Element(this, 'html');
        this.head = new Element(this, 'head');
        this.body = new Element(this, 'body');
        this.appendChild(this.documentElement);
        this.documentElement.appendChild(this.head);
        this.documentElement.appendChild(this.body);
        this.activeElement = this.body;
        this.hidden = false;
        this.createElementCalls = [];
    }
    createElement(tag, options) {
        // jellyfin-web's custom-elements polyfill throws on the options form; mirror that.
        if (options && typeof options === 'object') {
            throw new TypeError("Failed to execute 'createElement': the options form isn't supported by the polyfill");
        }
        const el = new Element(this, tag);
        if (typeof options === 'string') { el.setAttribute('is', options); }
        return el;
    }
    createTextNode(text) { return new Text(this, text); }
    createDocumentFragment() { return new Fragment(this); }
    getElementById(id) { return this.documentElement.querySelector('#' + id); }
    querySelector(s) { return this.documentElement.matches(s) ? this.documentElement : this.documentElement.querySelector(s); }
    querySelectorAll(s) { return this.documentElement.querySelectorAll(s); }
    get textContent() { return this.documentElement.textContent; }
}

module.exports = { Document, Element, Text, Event, parseInto };
