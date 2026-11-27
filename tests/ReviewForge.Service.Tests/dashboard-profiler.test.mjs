import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-profiler.js', import.meta.url), 'utf8');
const { attach } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
function fixture() {
    const handlers = {}, writes = [], commands = [], status = { textContent: '' };
    const rows = ['one', 'two', 'three'].map(id => ({ dataset: { profileRow: id }, focused: 0, clicks: 0,
        focus() { this.focused++; }, click() { this.clicks++; }, scrollIntoView() {}, closest(s) { return s === '[data-profile-row]' ? this : null; } }));
    const copy = { dataset: { copyKind: 'Record ID', copyValue: 'one' }, closest: s => s === '[data-copy-value]' ? copy : null };
    const scroll = { scrollTop: 0, scrollLeft: 0, addEventListener: (n, f) => handlers[n] = f, removeEventListener: n => delete handlers[n] };
    const root = { ownerDocument: { querySelector: () => null }, contains: t => [root, copy, ...rows].includes(t), closest: () => null,
        addEventListener: (n, f) => handlers[n] = f, removeEventListener: n => delete handlers[n], querySelectorAll: () => rows,
        querySelector: s => s === '[data-profile-scroll]' ? scroll : s === '[data-copy-feedback]' ? status : rows[0] };
    const event = (target, key = 'ArrowDown') => ({ target, key, prevented: false, preventDefault() { this.prevented = true; } });
    return { root, rows, copy, scroll, handlers, writes, commands, status, event,
        controller: () => attach(root, { invokeMethodAsync: async (...args) => commands.push(args) }, { writeText: async value => writes.push(value) }) };
}
test('profiler scroll positions are page-local, bounded, and restored without fetching', () => {
    const f = fixture(), c = f.controller();
    c.syncPage('0'); f.scroll.scrollTop = 400; f.scroll.scrollLeft = 60; f.handlers.scroll();
    c.syncPage('50'); assert.equal(f.scroll.scrollTop, 0);
    f.scroll.scrollTop = 150; f.handlers.scroll(); c.syncPage('0');
    assert.equal(f.scroll.scrollTop, 400); assert.equal(f.scroll.scrollLeft, 60);
    c.syncPage('0'); assert.equal(f.scroll.scrollTop, 400);
    c.dispose(); assert.deepEqual(f.handlers, {});
});
test('native controls and dialogs keep input, rows navigate and palette is scoped', async () => {
    const f = fixture(), c = f.controller();
    for (const key of ['ArrowDown', 'ArrowUp', 'Home', 'End']) { const e = f.event(f.rows[1], key); f.handlers.keydown(e); assert.equal(e.prevented, true); }
    c.focus('two'); assert.ok(f.rows[1].focused > 0); c.focus('search');
    const palette = Object.assign(f.event(f.copy, 'k'), { ctrlKey: true }); f.handlers.keydown(palette);
    assert.equal(palette.prevented, true); assert.deepEqual(f.commands[0], ['ExecuteProfileCommand', 'commands']);
    const search = f.event(f.root, '/'); f.handlers.keydown(search); assert.equal(search.prevented, true);
    for (const extra of [{ isComposing: true }, { keyCode: 229 }, { repeat: true }, { altKey: true }, { shiftKey: true }, { ctrlKey: true }, { metaKey: true }, { defaultPrevented: true }]) {
        const e = Object.assign(f.event(f.rows[0]), extra); f.handlers.keydown(e); assert.equal(e.prevented, false);
    }
    for (const target of [{ isContentEditable: true }, { closest: () => ({}) }]) {
        f.root.contains = () => true; const e = f.event(target); f.handlers.keydown(e); assert.equal(e.prevented, false);
    }
    f.root.ownerDocument.querySelector = () => ({}); const modal = f.event(f.rows[0]); f.handlers.keydown(modal); assert.equal(modal.prevented, false);
    c.focus('three'); f.root.ownerDocument.querySelector = () => null;
    await f.handlers.click(f.event(f.copy)); assert.deepEqual(f.writes, ['one']); assert.match(f.status.textContent, /copied/);
    const keydown = f.handlers.keydown; c.dispose(); const retired = f.event(f.rows[0]); keydown(retired); assert.equal(retired.prevented, false); c.focus('two');
});
test('safe copy failure and retired asynchronous copy do not reveal errors', async () => {
    const f = fixture(); let resolve;
    let c = attach(f.root, {}, { writeText: async () => { throw Error('secret'); } });
    await f.handlers.click(f.event(f.copy)); assert.match(f.status.textContent, /Copy unavailable/); assert.doesNotMatch(f.status.textContent, /secret/); c.dispose();
    c = attach(f.root, {}, null); await f.handlers.click(f.event(f.copy)); assert.match(f.status.textContent, /Copy unavailable/); c.dispose();
    c = attach(f.root, {}, { writeText: () => new Promise(r => resolve = r) });
    const pending = f.handlers.click(f.event(f.copy)); c.dispose(); f.status.textContent = 'retired'; resolve(); await pending; assert.equal(f.status.textContent, 'retired');
});
