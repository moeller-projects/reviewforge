import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-commands.js', import.meta.url), 'utf8');
const { attach, detach, preferences, savePreferences, focusSearch, focusReason } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
function fixture() {
  const listeners = new Map(), calls = [];
  const row = { closest: selector => selector === '[data-finding-id]' ? row : null, isContentEditable: false };
  const root = { isConnected: true, contains: target => target !== 'outside', ownerDocument: { querySelector: () => null },
    addEventListener: (n, f) => listeners.set(n, f), removeEventListener: (n, f) => { if (listeners.get(n) === f) listeners.delete(n); } };
  const callback = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } };
  const fire = (key, extra = {}) => { let prevented = false; listeners.get('keydown')?.({ key, target: row, preventDefault: () => prevented = true, ...extra }); return prevented; };
  return { root, callback, calls, fire, listeners, row };
}
globalThis.localStorage = { getItem: () => null, setItem() {} };
test('scoped default navigation, range, inspect, selection, search, palette and escape; letters opt in', () => {
  const f = fixture(); attach(f.root, f.callback);
  for (const [key, extra, command] of [['ArrowDown', {}, 'next'], ['ArrowUp', { shiftKey: true }, 'previous'], ['Enter', {}, 'inspect'], [' ', {}, 'select'], ['/', {}, 'search'], ['k', { ctrlKey: true }, 'commands'], ['K', { metaKey: true }, 'commands'], ['Escape', {}, 'escape']]) {
    assert.equal(f.fire(key, extra), true); assert.deepEqual(f.calls.at(-1), ['ExecuteCommand', command, !!extra.shiftKey]);
  }
  for (const key of ['j', 'k', 'a', 'r', 'd', '?']) assert.equal(f.fire(key), false);
  savePreferences(f.root, true, false);
  for (const [key, command] of [['j', 'next'], ['k', 'previous'], ['a', 'approve'], ['r', 'reject'], ['d', 'defer'], ['?', 'commands']]) { assert.equal(f.fire(key), true); assert.equal(f.calls.at(-1)[1], command); }
  detach(f.root); assert.equal(f.listeners.size, 0); assert.equal(f.fire('ArrowDown'), false);
});
test('typing, IME, repeat, modifiers, native controls, wrong scope and unrelated modals stay native', () => {
  const f = fixture(); attach(f.root, f.callback);
  for (const extra of [{ repeat: true }, { isComposing: true }, { keyCode: 229 }, { altKey: true }, { ctrlKey: true }, { metaKey: true }, { shiftKey: true }, { target: 'outside' }, { defaultPrevented: true }]) assert.equal(f.fire('Enter', extra), false);
  for (const selector of ['input', 'textarea', 'select', '[contenteditable]', 'button', 'a[href]']) {
    const target = { closest: s => s.includes(selector) ? {} : null };
    assert.equal(f.fire('ArrowDown', { target }), false); assert.equal(f.fire('k', { target, ctrlKey: true }), false);
  }
  f.root.ownerDocument.querySelector = () => ({}); assert.equal(f.fire('/', {}), false);
  f.root.ownerDocument.querySelector = () => null; f.root.isConnected = false; assert.equal(f.fire('ArrowDown'), false);
  assert.equal(f.calls.length, 0);
});
test('disabled commands preserve browser defaults and focus utilities target only the workspace', () => {
  const f = fixture(); f.root.dataset = { disabledCommands: 'next approve escape' }; attach(f.root, f.callback);
  assert.equal(f.fire('ArrowDown'), false); assert.equal(f.fire('Escape'), false); assert.equal(f.calls.length, 0);
  let focused; f.root.querySelector = selector => ({ focus: () => focused = selector });
  focusSearch(f.root); assert.equal(focused, '[data-queue-search]');
  focusReason(f.root); assert.equal(focused, '.finding-decision-editor textarea');
  f.root.querySelector = () => null; focusSearch(f.root); focusReason(f.root); detach(f.root);
});
test('reattach replaces handlers; preferences are strict and only presentation data is stored', () => {
  const f = fixture(); attach(f.root, f.callback); attach(f.root, f.callback); f.fire('ArrowDown'); assert.equal(f.calls.length, 1);
  const writes = []; globalThis.localStorage = { getItem: () => '{"singleLetter":"true","advance":"false"}', setItem: (k, v) => writes.push([k, v]) };
  assert.deepEqual(preferences(), { singleLetter: false, advance: true });
  globalThis.localStorage.getItem = () => '{"singleLetter":true,"advance":false}'; assert.deepEqual(preferences(), { singleLetter: true, advance: false });
  savePreferences(f.root, true, false); assert.deepEqual(JSON.parse(writes[0][1]), { singleLetter: true, advance: false });
  globalThis.localStorage = { getItem: () => { throw Error(); }, setItem: () => { throw Error(); } }; assert.deepEqual(preferences(), { singleLetter: false, advance: true }); savePreferences(f.root, false, true);
  globalThis.localStorage.getItem = () => 'bad'; assert.deepEqual(preferences(), { singleLetter: false, advance: true }); detach(f.root);
});
