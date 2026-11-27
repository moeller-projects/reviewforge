import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import test from 'node:test';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-http.js', import.meta.url), 'utf8');
const { send } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
test('model call pages have an exact bounded metadata-only route', async () => {
    let calls = 0;
    globalThis.fetch = async () => { calls++; return {ok: true, status: 200, json: async () => ({state: 'empty'})}; };
    const base = '/reviews/01234567-89ab-cdef-0123-456789abcdef/model-calls';
    assert.equal((await send('p', 'GET', `${base}?after=0&count=100`)).status, 200);
    assert.equal(calls, 1);
    for (const query of ['after=-1&count=1', 'after=0&count=101', 'after=0&count=0', 'after=1&count=1&prompt=x',
        'after=0&count=1#fragment', 'after=0&count=1&count=1', 'after=01&count=1', 'after=9223372036854775808&count=1']) {
        assert.equal((await send('bad', 'GET', `${base}?${query}`)).status, 400, query);
    }
    assert.equal((await send('post', 'POST', `${base}?after=0&count=1`)).status, 400);
    assert.equal(calls, 1);
});
