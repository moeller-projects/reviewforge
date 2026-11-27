import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-queue.js', import.meta.url), 'utf8');
globalThis.requestAnimationFrame = callback => callback();
const { attach, detach, returnToRow, enterInspector } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
test('pane preference is bounded, scoped, restored and detached; return targets identity', () => {
  let listener, removed, width, focused = false, scrolled = false;
  const input = { value: '40', addEventListener: (n, f) => listener = f, removeEventListener: (n, f) => removed = f };
  const row = { dataset: { findingId: 'a' }, focus: () => focused = true, scrollIntoView: () => scrolled = true };
  const root = { querySelector: selector => selector === '[data-pane-size]' ? input : null, style: { setProperty: (n, v) => width = v }, querySelectorAll: () => [row] };
  globalThis.localStorage = { getItem: () => '52', setItem: (k, v) => assert.equal(v, '65') };
  attach(root); assert.equal(width, '52%'); input.value = '90'; listener(); assert.equal(width, '65%');
  returnToRow(root, 'missing'); assert.equal(focused, false);
  returnToRow(root, 'a'); assert.ok(focused && scrolled);
  detach(root); assert.equal(removed, listener); detach(root);
});
test('pending focus return is retired on detach', () => {
  let callback, focused = false;
  const input = { value: '40', addEventListener() {}, removeEventListener() {} };
  const row = { dataset: { findingId: 'a' }, focus: () => focused = true, scrollIntoView() {} };
  const root = { querySelector: () => input, style: { setProperty() {} }, querySelectorAll: () => [row] };
  globalThis.requestAnimationFrame = cb => callback = cb;
  attach(root); returnToRow(root, 'a'); detach(root); callback(); assert.equal(focused, false);
});
test('filtered return falls back to search and inspector entry targets visible heading', () => {
  let callback, focused, scrolled;
  globalThis.requestAnimationFrame = cb => callback = cb;
  const input = { value: '40', addEventListener() {}, removeEventListener() {} };
  const target = name => ({ focus: () => focused = name, scrollIntoView: () => scrolled = name });
  const search = target('search'), heading = target('inspector');
  const root = { querySelector: selector => selector === '[data-pane-size]' ? input : selector === '[data-queue-search]' ? search : heading,
    style: { setProperty() {} }, querySelectorAll: () => [] };
  attach(root);
  returnToRow(root, 'filtered-out'); assert.equal(focused, undefined); callback();
  assert.equal(focused, 'search'); assert.equal(scrolled, 'search');
  assert.equal(typeof enterInspector, 'function');
  enterInspector(root); callback(); assert.equal(focused, 'inspector'); assert.equal(scrolled, 'inspector');
  detach(root);
});
test('storage denial and invalid preferences do not block resizing', () => {
  let width;
  const input = { value: '40', addEventListener() {}, removeEventListener() {} };
  const root = { querySelector: () => input, style: { setProperty: (n, v) => width = v } };
  globalThis.localStorage = { getItem: () => { throw Error('denied'); } };
  attach(root); assert.equal(width, '40%'); detach(root);
  globalThis.localStorage = { getItem: () => 'bad' }; attach(root); assert.equal(width, '40%'); detach(root);
});
