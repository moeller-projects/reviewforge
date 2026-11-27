import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import test from 'node:test';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-http.js', import.meta.url), 'utf8');
const { send } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
test('history allows only bounded exact same-origin GET queries', async () => {
    const calls = [];
    globalThis.fetch = async (path, options) => { calls.push({ path, options }); return { ok: true, status: 200, json: async () => ({ items: [] }) }; };
    for (const path of ['/reviews/history', '/reviews/history?offset=0&count=50&attentionOnly=false', '/reviews/history?offset=10000&count=100&attentionOnly=true&org=o%26count%3D0&project=p%2F%23&repositoryId=r&prId=42'])
        assert.equal((await send(path, 'GET', path)).status, 200);
    const before = calls.length;
    for (const path of ['/reviews/history?', '/reviews/history/', '/reviews/history#x', '//other/reviews/history', 'https://other/reviews/history', '/reviews/history?offset=-1', '/reviews/history?offset=10001', '/reviews/history?count=101', '/reviews/history?count=0', '/reviews/history?count=50&count=1', '/reviews/history?attentionOnly=yes', '/reviews/history?other=1', '/reviews/history?org=o', '/reviews/history?org=o&project=p&repositoryId=r&prId=0', '/reviews/history?%63ount=50', '/reviews/history?count=50#x', '/reviews/history?org=%&project=p&repositoryId=r&prId=1', '/reviews/history\n'])
        assert.deepEqual(await send(path, 'GET', path), { status: 400 }, path);
    assert.deepEqual(await send('post', 'POST', '/reviews/history', {}), { status: 400 });
    assert.equal(calls.length, before);
    assert.ok(calls.every(c => c.options.credentials === 'same-origin' && c.options.mode === 'same-origin' && c.options.redirect === 'error'));
});
