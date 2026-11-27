import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import test from 'node:test';
const source = await readFile(new URL('../../src/ReviewForge.Service/wwwroot/dashboard-http.js', import.meta.url), 'utf8');
const { send } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const id = '01234567-89ab-cdef-0123-456789abcdef';
test('technician exact routes, methods and fresh CSRF for every mutation', async () => {
    const calls = [];
    let tokens = 0;
    globalThis.fetch = async (path, options) => {
        calls.push({ path, options });
        return { ok: true, status: 200, json: async () => path === '/dashboard/session' ? { token: `csrf-${++tokens}` } : {} };
    };
    for (const path of [`/reviews/${id}`, `/reviews/${id}/details`, '/reviews/drafts/capability'])
        assert.equal((await send(path, 'GET', path)).status, 200);
    for (const path of ['/reviews/drafts', `/reviews/${id}/decisions`, `/reviews/${id}/publish`, `/reviews/${id}/reconcile`])
        assert.equal((await send(path, 'POST', path, { findingId: '../anything' })).status, 200);
    const posts = calls.filter(c => c.options.method === 'POST');
    assert.equal(posts.length, 4);
    assert.equal(tokens, 4);
    assert.notEqual(posts[0].options.headers['X-CSRF-TOKEN'], posts[1].options.headers['X-CSRF-TOKEN']);
    const before = calls.length;
    for (const path of [`/reviews/${id}/details?x=1`, `/reviews/${id}/../dashboard`, `/reviews/${id}%2fdetails`, '//other/reviews', `/reviews/${id}/publish`, '/reviews/not-guid/details', `/reviews/${id}/details\n`, `/reviews/${id}/decisions\n`])
        assert.deepEqual(await send(path, 'GET', path), { status: 400 });
    for (const method of ['PUT', 'PATCH', 'DELETE', 'POST'])
        assert.deepEqual(await send(method, method, `/reviews/${id}/details`, {}), { status: 400 });
    for (const path of [`/reviews/${id}/publish?all=true`, `/reviews/${id}/publish/`, '/reviews/not-guid/publish', `/reviews/${id}/publish\n`, `/reviews/${id}/reconcile?all=true`, `/reviews/${id}/reconcile/`])
        assert.deepEqual(await send(path, 'POST', path, {}), { status: 400 });
    assert.equal(calls.length, before);
    globalThis.fetch = async (path) => path === '/dashboard/session'
        ? { ok: true, status: 200, json: async () => ({ token: 'fresh' }) }
        : { ok: false, status: 409, json: async () => ({ status: 1, currentVersion: 2, decision: null }) };
    assert.deepEqual(await send('conflict', 'POST', `/reviews/${id}/decisions`, {}),
        { status: 409, data: { status: 1, currentVersion: 2, decision: null } });
    globalThis.fetch = async (path) => path === '/dashboard/session'
        ? { ok: true, status: 200, json: async () => ({ token: 'fresh' }) }
        : { ok: false, status: 409, json: async () => ({ accepted: false, errorCode: 'publication-unknown', findings: [] }) };
    assert.deepEqual(await send('partial', 'POST', `/reviews/${id}/publish`, {}),
        { status: 409, data: { accepted: false, errorCode: 'publication-unknown', findings: [] } });
    globalThis.fetch = async (path) => path === '/dashboard/session'
        ? { ok: true, status: 200, json: async () => ({ token: 'fresh' }) }
        : { ok: false, status: 400, json: async () => { throw new SyntaxError('empty body'); } };
    assert.deepEqual(await send('empty', 'POST', `/reviews/${id}/decisions`, {}), { status: 400, data: null });
    globalThis.fetch = async () => ({ ok: false, status: 403 });
    assert.deepEqual(await send('blocked', 'POST', '/reviews/drafts', {}), { status: 403 });
});
