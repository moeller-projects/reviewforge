import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-queue.js', import.meta.url), 'utf8');
globalThis.requestAnimationFrame = callback => callback();
const { attach, detach, returnToRow, enterInspector, restoreContinuity, rememberView, restoreView } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
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
test('reads restore a connected editor and its selection without stealing another control focus', () => {
  assert.equal(typeof restoreContinuity, 'function');
  let restored = 0, selection;
  const listeners = {};
  const input = { value: '40', addEventListener() {}, removeEventListener() {} };
  const body = {}, doc = { body, activeElement: body };
  const editor = { isConnected: true, disabled: false, selectionStart: 2, selectionEnd: 4,
    focus: options => { assert.equal(options.preventScroll, true); restored++; }, setSelectionRange: (...args) => selection = args };
  const root = { ownerDocument: doc, querySelector: () => input, contains: target => target === editor,
    style: { setProperty() {} }, addEventListener: (name, fn) => listeners[name] = fn, removeEventListener: name => delete listeners[name] };
  const scroller = { isConnected: true, scrollTop: 120, scrollLeft: 8 };
  attach(root); listeners.focusin({ target: editor }); listeners.scroll({ target: scroller });
  scroller.scrollTop = 0; scroller.scrollLeft = 0;
  restoreContinuity(root); assert.equal(restored, 1); assert.deepEqual(selection, [2, 4]);
  assert.equal(scroller.scrollTop, 120); assert.equal(scroller.scrollLeft, 8);
  doc.activeElement = {}; restoreContinuity(root); assert.equal(restored, 1);
  doc.activeElement = body; editor.isConnected = false; restoreContinuity(root); assert.equal(restored, 1);
  detach(root); assert.deepEqual(listeners, {}); restoreContinuity(root);
});
test('queue and board scroll positions are run-local and return skips hidden duplicate identities', () => {
  globalThis.requestAnimationFrame = cb => cb();
  const input = { value: '40', addEventListener() {}, removeEventListener() {} };
  const scroller = { scrollTop: 120, scrollLeft: 8 }; let focused;
  const row = (hidden, name) => ({ dataset: { findingId: 'a' }, closest: () => hidden ? {} : null, focus: () => focused = name, scrollIntoView() {} });
  const root = { querySelector: s => s === '[data-pane-size]' ? input : scroller, style: { setProperty() {} }, querySelectorAll: () => [row(true, 'hidden'), row(false, 'visible')] };
  attach(root); rememberView(root, 'queue'); scroller.scrollTop = 50; scroller.scrollLeft = 2; rememberView(root, 'board');
  restoreView(root, 'queue'); assert.equal(scroller.scrollTop, 120); assert.equal(scroller.scrollLeft, 8);
  restoreView(root, 'board'); assert.equal(scroller.scrollTop, 50); assert.equal(scroller.scrollLeft, 2);
  returnToRow(root, 'a'); assert.equal(focused, 'visible'); detach(root);
  scroller.scrollTop = 0; restoreView(root, 'queue'); assert.equal(scroller.scrollTop, 0);
});
test('storage denial and invalid preferences do not block resizing', () => {
  let width;
  const input = { value: '40', addEventListener() {}, removeEventListener() {} };
  const root = { querySelector: () => input, style: { setProperty: (n, v) => width = v } };
  globalThis.localStorage = { getItem: () => { throw Error('denied'); } };
  attach(root); assert.equal(width, '40%'); detach(root);
  globalThis.localStorage = { getItem: () => 'bad' }; attach(root); assert.equal(width, '40%'); detach(root);
});
