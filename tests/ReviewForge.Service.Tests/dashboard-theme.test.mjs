import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-theme.js', import.meta.url), 'utf8');
function boot(stored, dark = false, broken = false) {
    let listener, onReady, observe; const writes = []; const root = { dataset: {} }; const pickers = [];
    const media = { matches: dark, addEventListener: (_, fn) => listener = fn };
    const context = { document: { documentElement: root, querySelectorAll: () => pickers,
        addEventListener(name, fn) { if (name === 'DOMContentLoaded') onReady = fn; } },
        MutationObserver: class { constructor(fn) { observe = fn; } observe() {} },
        matchMedia: () => media, localStorage: {
            getItem() { if (broken) throw Error('denied'); return stored; },
            setItem: (...args) => writes.push(args), removeItem: key => writes.push([key])
        } };
    context.window = context; vm.runInNewContext(source, context);
    return { root, writes, pickers, ready() { onReady(); }, added() { observe(); }, api: context.reviewForgeTheme,
        system(value) { media.matches = value; listener(); } };
}
test('first paint follows system, validates storage, and tolerates denied storage', () => {
    for (const value of [null, 'unknown', 'system']) {
        const b = boot(value, true); assert.equal(b.root.dataset.theme, 'dark');
        b.system(false); assert.equal(b.root.dataset.theme, 'light');
    }
    assert.equal(boot(null, true, true).root.dataset.theme, 'dark');
});
test('theme controls inserted by the interactive circuit reflect the active override', () => {
    const b = boot('dark'); b.ready();
    const picker = { value: 'system' }; b.pickers.push(picker); b.added();
    assert.equal(picker.value, 'dark');
});
test('manual light/dark overrides persist, apply immediately and system mode removes override', () => {
    const b = boot('light', true); assert.equal(b.root.dataset.theme, 'light');
    b.system(true); assert.equal(b.root.dataset.theme, 'light');
    b.api.set('dark'); assert.equal(b.root.dataset.theme, 'dark');
    assert.deepEqual(b.writes[0], ['reviewforge.theme', 'dark']);
    b.api.set('system'); assert.deepEqual(b.writes[1], ['reviewforge.theme']);
    b.system(false); assert.equal(b.root.dataset.theme, 'light');
    b.api.set('invalid'); assert.equal(b.api.preference(), 'system');
});
