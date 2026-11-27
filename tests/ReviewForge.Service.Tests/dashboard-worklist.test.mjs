import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-commands.js', import.meta.url), 'utf8');
const commands = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
globalThis.localStorage = { getItem: () => null };
function fixture() {
    const listeners = new Map(), windowListeners = new Map(), calls = [], scrolls = [];
    const win = { scrollY: 40, scrollTo: (...args) => scrolls.push(args), addEventListener: (n, f) => windowListeners.set(n, f), removeEventListener: n => windowListeners.delete(n) };
    const root = { isConnected: true, contains: () => true, ownerDocument: { querySelector: () => null, defaultView: win },
        querySelectorAll: () => rows, addEventListener: (n, f) => listeners.set(n, f), removeEventListener: n => listeners.delete(n) };
    let active;
    const rows = Array.from({ length: 3 }, (_, i) => ({ isContentEditable: false,
        closest: selector => selector.includes('data-worklist-row') ? rows[i] : null,
        getAttribute: name => name === 'aria-expanded' && i === 0 ? expanded : null,
        focus: () => active = i, click: () => { expanded = expanded === 'true' ? 'false' : 'true'; } }));
    let expanded = 'false';
    const callback = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } };
    const fire = (key, index = 0, extra = {}) => { let prevented = false; listeners.get('keydown')?.({ key, target: rows[index], preventDefault: () => prevented = true, ...extra }); return prevented; };
    return { root, callback, fire, calls, listeners, windowListeners, win, scrolls, active: () => active, expanded: () => expanded };
}
test('worklist uses shared listener for arrows, expansion, palette and search, without mutations', () => {
    const f = fixture(); commands.attachWorklist(f.root, f.callback, 150);
    assert.equal(f.fire('ArrowDown'), true); assert.equal(f.active(), 1);
    assert.equal(f.fire('ArrowUp', 1), true); assert.equal(f.active(), 0);
    assert.equal(f.fire('End'), true); assert.equal(f.active(), 2);
    assert.equal(f.fire('Home', 2), true); assert.equal(f.active(), 0);
    assert.equal(f.fire('ArrowRight'), true); assert.equal(f.expanded(), 'true');
    assert.equal(f.fire('ArrowRight'), false);
    assert.equal(f.fire('ArrowLeft'), true); assert.equal(f.expanded(), 'false');
    assert.equal(f.fire('Enter', 2), false); // native link activation, never publication
    assert.equal(f.fire('k', 0, { ctrlKey: true }), true);
    assert.deepEqual(f.calls.at(-1), ['ExecuteCommand', 'commands', false]);
    assert.equal(f.fire('/'), true); assert.deepEqual(f.calls.at(-1), ['ExecuteCommand', 'search', false]);
    assert.equal(f.fire('a'), false);
    assert.equal(f.fire('ArrowDown', 0, { isComposing: true }), false);
    assert.equal(f.fire('ArrowDown', 0, { target: { closest: s => s.includes('input') ? {} : null } }), false);
    commands.detach(f.root); assert.equal(f.listeners.size, 0); assert.equal(f.windowListeners.size, 0);
});
test('scroll remains circuit callback state and is restored without browser persistence', () => {
    const f = fixture(); commands.attachWorklist(f.root, f.callback, 150);
    assert.deepEqual(f.scrolls, [[0, 150]]);
    f.windowListeners.get('scroll')();
    assert.deepEqual(f.calls.at(-1), ['RememberScroll', 40]);
    commands.detach(f.root);
    assert.equal(f.windowListeners.size, 0);
});
