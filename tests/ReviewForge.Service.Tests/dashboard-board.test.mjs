import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-board.js', import.meta.url), 'utf8').catch(() => '');
const board = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
function fixture(blocked = false) {
  const handlers = {}, calls = [];
  const root = { addEventListener: (n, f) => handlers[n] = f, removeEventListener: n => delete handlers[n], contains: () => true };
  const card = { dataset: { boardCard: 'a', boardBlocked: String(blocked) }, closest: s => s === '[hidden]' ? null : s === '[data-board-card]' ? card : null };
  const column = { dataset: { boardColumn: 'Approved' }, closest: s => s === '[hidden]' ? null : s === '[data-board-column]' ? column : null };
  const event = target => ({ target, prevented: false, preventDefault() { this.prevented = true; }, dataTransfer: { setData(type, value) { assert.equal(type, 'text/plain'); assert.equal(value, 'finding'); } } });
  return { root, card, column, event, handlers, calls, callback: { invokeMethodAsync: async (...args) => calls.push(args) } };
}
test('scoped drag moves one card by shared callback, no optimistic DOM or browser payload storage', async () => {
  assert.equal(typeof board.attach, 'function');
  const f = fixture(); board.attach(f.root, f.callback);
  f.handlers.dragstart(f.event(f.card));
  const over = f.event(f.column); f.handlers.dragover(over); assert.ok(over.prevented);
  await f.handlers.drop(f.event(f.column));
  assert.deepEqual(f.calls, [['MoveCard', 'a', 'Approved']]);
  await f.handlers.drop(f.event(f.column)); assert.equal(f.calls.length, 1);
  board.detach(f.root); assert.deepEqual(f.handlers, {});
});
test('unconfirmed circuit failures are visible and late completions do not touch a detached workspace', async () => {
  const f = fixture(); const status = { textContent: '' }; f.root.querySelector = () => status;
  board.attach(f.root, { invokeMethodAsync: async () => { throw Error('disconnected'); } });
  f.handlers.dragstart(f.event(f.card)); await f.handlers.drop(f.event(f.column));
  assert.match(status.textContent, /not confirmed/);
  let complete; board.attach(f.root, { invokeMethodAsync: () => new Promise(resolve => complete = resolve) });
  f.handlers.dragstart(f.event(f.card)); const pending = f.handlers.drop(f.event(f.column));
  assert.match(status.textContent, /awaiting confirmation/); board.detach(f.root); status.textContent = 'retired';
  complete(); await pending; assert.equal(status.textContent, 'retired');
});
test('blocked cards, external drags, canceled drags and detached handlers never write', async () => {
  assert.equal(typeof board.attach, 'function');
  const f = fixture(true); board.attach(f.root, f.callback);
  f.handlers.dragstart(f.event(f.card)); await f.handlers.drop(f.event(f.column)); assert.equal(f.calls.length, 0);
  f.card.dataset.boardBlocked = 'false'; f.handlers.dragstart(f.event(f.card)); f.handlers.dragend();
  await f.handlers.drop(f.event(f.column)); assert.equal(f.calls.length, 0);
  f.handlers.dragstart(f.event(f.card)); const drop = f.handlers.drop; board.detach(f.root);
  await drop(f.event(f.column)); assert.equal(f.calls.length, 0);
});
