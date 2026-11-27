import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-execution.js', import.meta.url), 'utf8').catch(() => '');
const execution = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
function fixture() {
  const handlers = {}, status = { textContent: '' }, writes = [];
  const rows = [0, 1, 2].map(index => ({ dataset: { stageIndex: String(index) }, focused: 0, clicks: 0,
    focus() { this.focused++; }, click() { this.clicks++; }, getAttribute: () => index === 1 ? 'true' : 'false',
    closest(s) { return s === '[data-stage-index]' ? this : null; } }));
  const root = { addEventListener: (n, f) => handlers[n] = f, removeEventListener: n => delete handlers[n], contains: t => rows.includes(t) || t === copy,
    querySelectorAll: () => rows, querySelector: s => s === '[data-copy-feedback]' ? status : rows[1] };
  const copy = { dataset: { copyKind: 'Run ID', copyValue: 'safe-id' }, closest: s => s === '[data-copy-value]' ? copy : null };
  const event = (target, key = 'ArrowDown') => ({ target, key, prevented: false, preventDefault() { this.prevented = true; } });
  return { root, rows, handlers, copy, event, status, writes, clipboard: { writeText: async v => writes.push(v) } };
}
test('execution owns keyboard selection only on its stage buttons and disposes listeners', () => {
  assert.equal(typeof execution.attach, 'function');
  const f = fixture(), controller = execution.attach(f.root, f.clipboard);
  const next = f.event(f.rows[0]); f.handlers.keydown(next);
  assert.equal(next.prevented, true); assert.equal(f.rows[1].clicks, 1); assert.equal(f.rows[1].focused, 1);
  f.handlers.keydown(f.event(f.rows[1], 'Home')); assert.equal(f.rows[0].clicks, 1);
  f.handlers.keydown(f.event(f.rows[0], 'End')); assert.equal(f.rows[2].clicks, 1);
  f.handlers.keydown(f.event(f.rows[2], 'ArrowUp')); assert.equal(f.rows[1].clicks, 2);
  controller.focusSelected(); assert.equal(f.rows[1].focused, 3);
  for (const extra of [{ isComposing: true }, { repeat: true }, { ctrlKey: true }, { altKey: true }, { metaKey: true }, { shiftKey: true }]) {
    const e = Object.assign(f.event(f.rows[0]), extra); f.handlers.keydown(e); assert.equal(e.prevented, false);
  }
  for (const e of [f.event(f.copy), f.event(f.rows[0], 'a'), f.event(f.rows[0], 'ArrowUp')]) {
    f.handlers.keydown(e); assert.equal(e.prevented, false);
  }
  const keydown = f.handlers.keydown; controller.dispose(); assert.deepEqual(f.handlers, {});
  const retired = f.event(f.rows[0]); keydown(retired); assert.equal(retired.prevented, false);
});
test('copy is explicit, reports success/failure without raw errors, and ignores retired completions', async () => {
  assert.equal(typeof execution.attach, 'function');
  const f = fixture(); let controller = execution.attach(f.root, f.clipboard);
  await f.handlers.click(f.event(f.copy)); assert.deepEqual(f.writes, ['safe-id']); assert.equal(f.status.textContent, 'Run ID copied.');
  await f.handlers.click(f.event(f.rows[0])); assert.equal(f.writes.length, 1);
  controller.dispose();
  controller = execution.attach(f.root, { writeText: async () => { throw Error('private-secret'); } });
  await f.handlers.click(f.event(f.copy)); assert.match(f.status.textContent, /Copy unavailable/); assert.doesNotMatch(f.status.textContent, /private-secret/);
  controller.dispose();
  controller = execution.attach(f.root, null); await f.handlers.click(f.event(f.copy)); assert.match(f.status.textContent, /Copy unavailable/); controller.dispose();
  let resolve; controller = execution.attach(f.root, { writeText: () => new Promise(r => resolve = r) });
  const pending = f.handlers.click(f.event(f.copy)); controller.dispose(); f.status.textContent = 'retired'; resolve(); await pending;
  assert.equal(f.status.textContent, 'retired');
});
