'use strict';
/* A small fake DOM, enough for Babelfin's ui.js and app.js: elements, text nodes, attributes, classList, dataset, events with
   bubbling, a selector engine (tag, #id, .class, [attr], [attr=value], :not(simple), descendant and child combinators, lists),
   <select>/<option> value semantics, <details>.open with a toggle event, and focus tracking.
   Includes isConnected, and a zero-sized layout (getBoundingClientRect, offsetWidth/offsetHeight) for BabelfinUI.infoTip's placement. */

class Event {
    constructor(type, init) {
        this.type = type;
        this.bubbles = !!(init && init.bubbles);
        this.defaultPrevented = false;
        this.propagationStopped = false;
        Object.assign(this, init || {});
    }
    preventDefault() { this.defaultPrevented = true; }
    stopPropagation() { this.propagationStopped = true; }
}

class Node {
    constructor(doc) {
        this.ownerDocument = doc;
        this.parentNode = null;
        this.childNodes = [];
        this.listeners = {};
    }
    get firstChild() { return this.childNodes[0] || null; }
    get lastChild() { return this.childNodes[this.childNodes.length - 1] || null; }
    get parentElement() { return this.parentNode && this.parentNode.nodeType === 1 ? this.parentNode : null; }
    get nextSibling() {
        if (!this.parentNode) { return null; }
        const list = this.parentNode.childNodes;
        return list[list.indexOf(this) + 1] || null;
    }
    appendChild(child) {
        if (child.nodeType === 11) {
            child.childNodes.slice().forEach((c) => this.appendChild(c));
            return child;
        }
        if (child.parentNode) { child.parentNode.removeChild(child); }
        child.parentNode = this;
        this.childNodes.push(child);
        return child;
    }
    insertBefore(child, ref) {
        if (!ref) { return this.appendChild(child); }
        if (child.parentNode) { child.parentNode.removeChild(child); }
        child.parentNode = this;
        this.childNodes.splice(this.childNodes.indexOf(ref), 0, child);
        return child;
    }
    removeChild(child) {
        const i = this.childNodes.indexOf(child);
        if (i < 0) { throw new Error('not a child'); }
        this.childNodes.splice(i, 1);
        child.parentNode = null;
        if (this.ownerDocument && this.ownerDocument.activeElement && child.contains(this.ownerDocument.activeElement)) {
            this.ownerDocument.activeElement = this.ownerDocument.body;
        }
        return child;
    }
    remove() { if (this.parentNode) { this.parentNode.removeChild(this); } }
    contains(other) {
        for (let n = other; n; n = n.parentNode) { if (n === this) { return true; } }
        return false;
    }
    get isConnected() {
        let n = this;
        while (n.parentNode) { n = n.parentNode; }
        return n.nodeType === 9;
    }
    addEventListener(type, fn) {
        (this.listeners[type] = this.listeners[type] || []).push(fn);
    }
    removeEventListener(type, fn) {
        const list = this.listeners[type] || [];
        const i = list.indexOf(fn);
        if (i >= 0) { list.splice(i, 1); }
    }
    dispatchEvent(event) {
        if (!event.target) { event.target = this; }
        let node = this;
        while (node) {
            event.currentTarget = node;
            (node.listeners[event.type] || []).slice().forEach((fn) => fn.call(node, event));
            if (!event.bubbles || event.propagationStopped) { break; }
            node = node.parentNode || (node.nodeType === 9 ? node.defaultView : null);
        }
        return !event.defaultPrevented;
    }
    get textContent() {
        return this.childNodes.map((c) => c.textContent).join('');
    }
    set textContent(value) {
        this.childNodes.slice().forEach((c) => this.removeChild(c));
        if (value !== '' && value !== null && value !== undefined) {
            this.appendChild(this.ownerDocument.createTextNode(String(value)));
        }
    }
}

class Text extends Node {
    constructor(doc, data) {
        super(doc);
        this.nodeType = 3;
        this.data = data;
    }
    get textContent() { return this.data; }
    set textContent(v) { this.data = String(v); }
}

class Fragment extends Node {
    constructor(doc) { super(doc); this.nodeType = 11; }
}

function parseSelector(text) {
    // Returns a list of alternatives; each is a list of { combinator, compound } from left to right.
    return splitTop(text, ',').map((alt) => {
        const parts = [];
        let rest = alt.trim();
        let combinator = ' ';
        while (rest.length) {
            const m = /^\s*(>)\s*/.exec(rest);
            if (m && parts.length) {
                combinator = '>';
                rest = rest.slice(m[0].length);
                continue;
            }
            let i = 0;
            let depth = 0;
            while (i < rest.length) {
                const ch = rest[i];
                if (ch === '[' || ch === '(') { depth++; }
                if (ch === ']' || ch === ')') { depth--; }
                if (depth === 0 && (ch === ' ' || ch === '>')) { break; }
                i++;
            }
            parts.push({ combinator: combinator, compound: parseCompound(rest.slice(0, i)) });
            combinator = ' ';
            rest = rest.slice(i);
            const ws = /^\s+/.exec(rest);
            if (ws) { rest = rest.slice(ws[0].length); }
        }
        return parts;
    });
}

function splitTop(text, sep) {
    const out = [];
    let depth = 0;
    let cur = '';
    for (const ch of text) {
        if (ch === '[' || ch === '(') { depth++; }
        if (ch === ']' || ch === ')') { depth--; }
        if (ch === sep && depth === 0) { out.push(cur); cur = ''; } else { cur += ch; }
    }
    out.push(cur);
    return out;
}

function parseCompound(text) {
    const c = { tag: null, id: null, classes: [], attrs: [], nots: [] };
    const re = /^([a-zA-Z][\w-]*|\*)|#([\w-]+)|\.([\w-]+)|\[([\w-]+)(?:=("[^"]*"|'[^']*'|[^\]]*))?\]|:not\(([^)]*)\)/g;
    let m;
    let pos = 0;
    re.lastIndex = 0;
    const s = text;
    const re2 = /([a-zA-Z][\w-]*|\*)|#([\w-]+)|\.([\w-]+)|\[([\w-]+)(?:=("[^"]*"|'[^']*'|[^\]]*))?\]|:not\(([^)]*)\)/y;
    while (pos < s.length) {
        re2.lastIndex = pos;
        m = re2.exec(s);
        if (!m) { throw new Error('selector not supported: ' + text); }
        if (m[1]) { c.tag = m[1] === '*' ? null : m[1].toUpperCase(); }
        else if (m[2]) { c.id = m[2]; }
        else if (m[3]) { c.classes.push(m[3]); }
        else if (m[4]) {
            let v = m[5];
            if (v !== undefined && /^["']/.test(v)) { v = v.slice(1, -1); }
            c.attrs.push({ name: m[4], value: v });
        } else if (m[6] !== undefined) { c.nots.push(parseCompound(m[6])); }
        pos = re2.lastIndex;
    }
    return c;
}

function matchCompound(el, c) {
    if (el.nodeType !== 1) { return false; }
    if (c.tag && el.tagName !== c.tag) { return false; }
    if (c.id && el.getAttribute('id') !== c.id) { return false; }
    for (const cl of c.classes) { if (!el.classList.contains(cl)) { return false; } }
    for (const a of c.attrs) {
        if (!el.hasAttribute(a.name)) { return false; }
        if (a.value !== undefined && el.getAttribute(a.name) !== a.value) { return false; }
    }
    for (const n of c.nots) { if (matchCompound(el, n)) { return false; } }
    return true;
}

function matchParts(el, parts, i) {
    if (!matchCompound(el, parts[i].compound)) { return false; }
    if (i === 0) { return true; }
    const comb = parts[i].combinator;
    if (comb === '>') {
        return !!el.parentElement && matchParts(el.parentElement, parts, i - 1);
    }
    for (let p = el.parentElement; p; p = p.parentElement) {
        if (matchParts(p, parts, i - 1)) { return true; }
    }
    return false;
}

function matches(el, selector) {
    return parseSelector(selector).some((parts) => matchParts(el, parts, parts.length - 1));
}

class ClassList {
    constructor(el) { this.el = el; }
    list() { return (this.el.getAttribute('class') || '').split(/\s+/).filter(Boolean); }
    contains(c) { return this.list().indexOf(c) >= 0; }
    add(...cs) { const l = this.list(); cs.forEach((c) => { if (l.indexOf(c) < 0) { l.push(c); } }); this.el.setAttribute('class', l.join(' ')); }
    remove(...cs) { this.el.setAttribute('class', this.list().filter((c) => cs.indexOf(c) < 0).join(' ')); }
    toggle(c, force) {
        const on = force === undefined ? !this.contains(c) : !!force;
        if (on) { this.add(c); } else { this.remove(c); }
        return on;
    }
}

class Element extends Node {
    constructor(doc, tag, ns) {
        super(doc);
        this.nodeType = 1;
        this.tagName = ns ? tag : tag.toUpperCase();
        this.namespaceURI = ns || 'http://www.w3.org/1999/xhtml';
        this.attributes = new Map();
        this.classList = new ClassList(this);
        this.style = {};
        this._value = undefined;
        this._checked = false;
        this._selected = undefined;
        this.files = null;
        const el = this;
        this.dataset = new Proxy({}, {
            get(_, key) { return typeof key === 'string' ? el.getAttribute('data-' + key.replace(/[A-Z]/g, (m) => '-' + m.toLowerCase())) || undefined : undefined; },
            set(_, key, value) { el.setAttribute('data-' + key.replace(/[A-Z]/g, (m) => '-' + m.toLowerCase()), String(value)); return true; },
            ownKeys() { return Array.from(el.attributes.keys()).filter((k) => k.indexOf('data-') === 0).map((k) => k.slice(5)); },
            getOwnPropertyDescriptor() { return { enumerable: true, configurable: true }; }
        });
    }
    get nodeName() { return this.tagName; }
    get localName() { return this.tagName.toLowerCase(); }
    get children() { return this.childNodes.filter((c) => c.nodeType === 1); }
    get firstElementChild() { return this.children[0] || null; }
    get lastElementChild() { const c = this.children; return c[c.length - 1] || null; }
    setAttribute(name, value) { this.attributes.set(name, String(value)); if (name === 'value' && this.tagName === 'INPUT' && this._value === undefined) { /* default value */ } }
    getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
    hasAttribute(name) { return this.attributes.has(name); }
    removeAttribute(name) { this.attributes.delete(name); }
    get id() { return this.getAttribute('id') || ''; }
    set id(v) { this.setAttribute('id', v); }
    get className() { return this.getAttribute('class') || ''; }
    set className(v) { this.setAttribute('class', v); }
    get hidden() { return this.hasAttribute('hidden'); }
    set hidden(v) { if (v) { this.setAttribute('hidden', ''); } else { this.removeAttribute('hidden'); } }
    get disabled() { return this.hasAttribute('disabled'); }
    set disabled(v) { if (v) { this.setAttribute('disabled', ''); } else { this.removeAttribute('disabled'); } }
    get open() { return this.hasAttribute('open'); }
    set open(v) {
        const was = this.open;
        if (v) { this.setAttribute('open', ''); } else { this.removeAttribute('open'); }
        if (was !== !!v && this.tagName === 'DETAILS') { this.dispatchEvent(new Event('toggle')); }
    }
    get multiple() { return this.hasAttribute('multiple'); }
    set multiple(v) { if (v) { this.setAttribute('multiple', ''); } else { this.removeAttribute('multiple'); } }
    get type() { return this.getAttribute('type') || (this.tagName === 'INPUT' ? 'text' : ''); }
    set type(v) { this.setAttribute('type', v); }
    get name() { return this.getAttribute('name') || ''; }
    get htmlFor() { return this.getAttribute('for') || ''; }
    get checked() { return this._checked; }
    set checked(v) { this._checked = !!v; }
    get options() { return this.querySelectorAll('option'); }
    get selected() {
        if (this._selected !== undefined) { return this._selected; }
        return false;
    }
    set selected(v) {
        const select = this.closest('select');
        if (select && v) { select.options.forEach((o) => { o._selected = false; }); }
        this._selected = !!v;
    }
    get selectedIndex() {
        if (this.tagName !== 'SELECT') { return undefined; }
        const opts = this.options;
        const i = opts.findIndex((o) => o._selected);
        if (i >= 0) { return i; }
        if (this._noSelection) { return -1; }
        return opts.length ? 0 : -1;
    }
    set selectedIndex(i) {
        this.options.forEach((o, j) => { o._selected = j === i; });
        this._noSelection = i < 0;
    }
    get value() {
        if (this.tagName === 'SELECT') {
            const opt = this.options[this.selectedIndex];
            return opt ? opt.value : '';
        }
        if (this.tagName === 'OPTION') {
            return this.hasAttribute('value') ? this.getAttribute('value') : this.textContent;
        }
        if (this._value === undefined) { return this.getAttribute('value') || ''; }
        return this._value;
    }
    set value(v) {
        if (this.tagName === 'SELECT') {
            const i = this.options.findIndex((o) => o.value === String(v));
            this.selectedIndex = i;
            return;
        }
        if (this.tagName === 'OPTION') { this.setAttribute('value', v); return; }
        this._value = String(v);
    }
    get innerHTML() { return ''; }
    set innerHTML(v) { if (v !== '') { throw new Error('innerHTML with content is not supported'); } this.textContent = ''; }
    matches(selector) { return matches(this, selector); }
    closest(selector) {
        for (let n = this; n && n.nodeType === 1; n = n.parentNode) { if (matches(n, selector)) { return n; } }
        return null;
    }
    querySelectorAll(selector) {
        const out = [];
        const walk = (node) => {
            node.childNodes.forEach((c) => {
                if (c.nodeType === 1) {
                    if (matches(c, selector)) { out.push(c); }
                    walk(c);
                }
            });
        };
        walk(this);
        return out;
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    focus() { this.ownerDocument.activeElement = this; }
    blur() { if (this.ownerDocument.activeElement === this) { this.ownerDocument.activeElement = this.ownerDocument.body; } }
    select() {}
    scrollIntoView() { this.ownerDocument.scrolledTo = this; }
    // No layout: every box is empty, at the top left.
    getBoundingClientRect() { return { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }; }
    get offsetWidth() { return 0; }
    get offsetHeight() { return 0; }
    click() {
        if (this.disabled) { return; }
        if (this.tagName === 'INPUT' && (this.type === 'checkbox' || this.type === 'radio')) {
            this._checked = this.type === 'radio' ? true : !this._checked;
            this.dispatchEvent(new Event('click', { bubbles: true }));
            this.dispatchEvent(new Event('change', { bubbles: true }));
            return;
        }
        this.dispatchEvent(new Event('click', { bubbles: true }));
    }
}

class Document extends Node {
    constructor() {
        super(null);
        this.ownerDocument = this;
        this.nodeType = 9;
        this.readyState = 'complete';
        this.title = '';
        this.documentElement = new Element(this, 'html');
        this.head = new Element(this, 'head');
        this.body = new Element(this, 'body');
        this.documentElement.parentNode = this;
        this.childNodes.push(this.documentElement);
        this.documentElement.appendChild(this.head);
        this.documentElement.appendChild(this.body);
        this.activeElement = this.body;
        this.referrer = '';
    }
    createElement(tag) { return new Element(this, tag); }
    createElementNS(ns, tag) { return new Element(this, tag, ns); }
    createTextNode(text) { return new Text(this, text); }
    createDocumentFragment() { return new Fragment(this); }
    getElementById(id) { return this.documentElement.querySelector('#' + id); }
    querySelector(s) { return this.documentElement.matches(s) ? this.documentElement : this.documentElement.querySelector(s); }
    querySelectorAll(s) { return this.documentElement.querySelectorAll(s); }
    createEvent() { return new Event(''); }
}

module.exports = { Document, Element, Event, matches };
