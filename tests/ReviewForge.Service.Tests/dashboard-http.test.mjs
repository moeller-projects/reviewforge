import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import test from 'node:test';

const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-http.js', import.meta.url), 'utf8');
const { send, abort } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('same-origin allowlist, credentials, CSRF, typed status and cancellation', async () => {
    const calls = [];
    globalThis.fetch = async (path, options) => {
        calls.push({ path, options });
        return { ok: true, status: path === '/reviews' ? 202 : 200,
            json: async () => path === '/dashboard/session' ? { token: 'csrf-test' } : { runId: 'run-test' } };
    };
    assert.deepEqual(await send('1', 'POST', 'https://other.test/reviews', {}), { status: 400 });
    assert.equal(calls.length, 0);
    assert.equal((await send('2', 'GET', '/reviews/dashboard')).status, 200);
    const forcedReview = { org: 'org', project: 'project', repositoryId: 'repo', prId: 7, forceReview: true };
    assert.equal((await send('3', 'POST', '/reviews', forcedReview)).status, 202);
    assert.deepEqual(calls.map(x => x.path), ['/reviews/dashboard', '/dashboard/session', '/reviews']);
    for (const { options } of calls) {
        assert.equal(options.credentials, 'same-origin');
        assert.equal(options.mode, 'same-origin');
        assert.equal(options.redirect, 'error');
        assert.equal(options.cache, 'no-store');
        assert.equal(options.headers['X-Api-Key'], undefined);
    }
    assert.equal(calls[2].options.headers['X-CSRF-TOKEN'], 'csrf-test');
    assert.deepEqual(JSON.parse(calls[2].options.body), forcedReview);
    assert.equal(JSON.parse(calls[2].options.body).headSha, undefined);
    assert.equal(JSON.parse(calls[2].options.body).runKind, undefined);
    globalThis.fetch = async () => ({ ok: false, status: 401 });
    assert.deepEqual(await send('4', 'POST', '/reviews', {}), { status: 401 });
    assert.deepEqual(await send('5', 'GET', '/reviews/dashboard'), { status: 401, data: null });
    globalThis.fetch = async () => { throw new Error('network'); };
    assert.deepEqual(await send('6', 'GET', '/reviews/dashboard'), { status: 0 });
    globalThis.fetch = (_, options) => new Promise((_, reject) => {
        options.signal.addEventListener('abort', () => reject(new Error('abort')));
    });
    const pending = send('7', 'GET', '/reviews/dashboard');
    abort('7');
    assert.deepEqual(await pending, { status: 0 });
    abort('missing');
    assert.doesNotMatch(source, /localStorage|sessionStorage|apiKey|X-Api-Key/);
});
