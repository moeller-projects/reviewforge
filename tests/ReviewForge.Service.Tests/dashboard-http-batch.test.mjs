import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import test from 'node:test';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-http.js', import.meta.url), 'utf8');
const { send } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
test('batch mutation is exact, same-origin, CSRF protected, and retains structured partial errors', async () => {
    const route = '/reviews/01234567-89ab-cdef-0123-456789abcdef/decisions/batch';
    const calls = [];
    globalThis.fetch = async (path, options) => { calls.push([path, options]); return path === '/dashboard/session'
        ? { ok: true, status: 200, json: async () => ({ token: 'csrf' }) }
        : { ok: false, status: 409, json: async () => ({ errorCode: 'busy', items: [{ findingId: 'x', status: 3 }] }) }; };
    const body = { headSha: 'head', selections: [{ findingId: 'x', expectedVersion: 7, state: 1, reason: null }] };
    const result = await send('batch', 'POST', route, body);
    assert.equal(result.status, 409); assert.equal(result.data.errorCode, 'busy');
    assert.equal(calls.length, 2); const options = calls[1][1];
    assert.equal(options.credentials, 'same-origin'); assert.equal(options.mode, 'same-origin'); assert.equal(options.headers['X-CSRF-TOKEN'], 'csrf');
    assert.deepEqual(JSON.parse(options.body), body);
    for (const path of [route + '/', route + '?x=1', route + '#x', route.replace('/batch', '/batch/extra'), 'https://evil.test' + route])
        assert.deepEqual(await send('invalid', 'POST', path, body), { status: 400 });
    assert.deepEqual(await send('get', 'GET', route), { status: 400 }); assert.equal(calls.length, 2);
});
