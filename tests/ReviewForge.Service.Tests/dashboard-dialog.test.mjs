import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import test from 'node:test';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-dialog.js', import.meta.url), 'utf8');
const { open, close } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('native modal owns containment, Escape cancels and disposal returns focus and removes listeners', () => {
    let returned = 0, focused = 0, cancelled = 0, prevented = 0; const listeners = new Map();
    const element = { open: false, ownerDocument: { activeElement: { isConnected: true, focus() { returned++; } } },
        addEventListener: (name, fn) => listeners.set(name, fn), removeEventListener: name => listeners.delete(name),
        showModal() { this.open = true; }, close() { this.open = false; },
        querySelector(selector) { return selector === '[autofocus]' ? { focus() { focused++; } } : { click() { cancelled++; } }; } };
    open(element); open(element); assert.equal(element.open, true); assert.equal(focused, 1);
    listeners.get('cancel')({ preventDefault() { prevented++; } });
    assert.equal(prevented, 1); assert.equal(cancelled, 1);
    close(element); close(element); assert.equal(element.open, false); assert.equal(returned, 1); assert.equal(listeners.size, 0);
});
test('explicit rerun return target survives disabling the initiating button', () => {
    let returned = 0;
    const target = { isConnected: true, focus() { returned++; } };
    const element = { open: false, ownerDocument: { activeElement: null, getElementById: id => id === 'rerun-context' ? target : null },
        addEventListener() {}, removeEventListener() {}, showModal() { this.open = true; }, close() {},
        querySelector() { return null; }, focus() {} };
    open(element, 'rerun-context'); close(element); assert.equal(returned, 1);
});
test('removed trigger is not focused and absence of autofocus focuses the modal', () => {
    let focused = 0;
    const element = { open: false, ownerDocument: { activeElement: { isConnected: false, focus() { throw Error('removed'); } } },
        addEventListener() {}, removeEventListener() {}, showModal() { this.open = true; }, close() {},
        querySelector() { return null; }, focus() { focused++; } };
    open(element); close(element); assert.equal(focused, 1);
});
