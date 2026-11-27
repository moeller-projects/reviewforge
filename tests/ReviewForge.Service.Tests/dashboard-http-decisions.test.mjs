import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import test from 'node:test';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-http.js', import.meta.url), 'utf8');
const { send } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
test('history permits one exact encoded findingId and no other queries or methods', async () => {
    const route = '/reviews/01234567-89ab-cdef-0123-456789abcdef/decisions/history';
    let calls = 0;
    globalThis.fetch = async () => { calls++; return { ok: true, status: 200, json: async () => ({ events: [] }) }; };
    assert.equal((await send('ok', 'GET', `${route}?findingId=key%2Fwith%20spaces`)).status, 200);
    for (const query of ['', '?findingId=', '?findingId=x&findingId=y', '?findingId=x&count=101', '?%66indingId=x', '?findingId=x#fragment', '?findingId=%ZZ', '?findingId=x\n'])
        assert.deepEqual(await send(query, 'GET', route + query), { status: 400 });
    assert.deepEqual(await send('post', 'POST', `${route}?findingId=x`, {}), { status: 400 });
    assert.equal(calls, 1);
});
