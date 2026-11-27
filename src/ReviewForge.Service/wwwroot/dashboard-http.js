const requests = new Map();

export function abort(id) {
    requests.get(id)?.abort();
}

// Deliberately not a general URL proxy. Only these exact query names and bounded values
// are accepted, with every scope field present together and no duplicate/encoded keys.
function historyPath(path) {
    if (path === '/reviews/history') return true;
    if (!path.startsWith('/reviews/history?') || path.includes('#')) return false;
    const query = path.slice('/reviews/history?'.length);
    if (!query) return false;
    const values = new Map();
    try {
        for (const pair of query.split('&')) {
            const match = /^(offset|count|attentionOnly|org|project|repositoryId|prId)=([^=]*)$/.exec(pair);
            if (!match || values.has(match[1]) || /%(?![0-9a-fA-F]{2})/.test(match[2])) return false;
            values.set(match[1], decodeURIComponent(match[2]));
        }
    } catch { return false; }
    for (const [key, max, min] of [['offset', 10000, 0], ['count', 100, 1], ['prId', 2147483647, 1]]) {
        if (values.has(key) && (!/^(0|[1-9][0-9]*)$/.test(values.get(key)) || Number(values.get(key)) < min || Number(values.get(key)) > max)) return false;
    }
    if (values.has('attentionOnly') && !['true', 'false'].includes(values.get('attentionOnly'))) return false;
    const scope = ['org', 'project', 'repositoryId', 'prId'];
    return !scope.some(k => values.has(k)) || scope.every(k => values.has(k) && values.get(k).trim().length > 0);
}

function decisionHistoryPath(path, run) {
    const match = new RegExp(`^${run}/decisions/history\\?findingId=([^&#=]+)$`).exec(path);
    if (!match || /%(?![0-9a-fA-F]{2})/.test(match[1])) return false;
    try {
        const id = decodeURIComponent(match[1]);
        return id.trim().length > 0 && id.length <= 2048 && !/[\r\n\u0000]/.test(id);
    } catch { return false; }
}

function modelCallsPath(path, run) {
    const match = new RegExp(`^${run}/model-calls\\?after=(0|[1-9][0-9]*)&count=([1-9][0-9]*)$`).exec(path);
    return !!match && BigInt(match[1]) <= 9223372036854775807n && Number(match[2]) <= 100;
}

export async function send(id, method, path, body) {
    const run = '/reviews/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
    const technicianMutation = typeof path === 'string' && new RegExp(`^${run}/(decisions|decisions/batch|publish|reconcile)$`).test(path);
    if (typeof path !== 'string' || path !== path.trim() || !((method === 'GET' &&
          (path === '/reviews/dashboard' || path === '/reviews/drafts/capability' || historyPath(path) || decisionHistoryPath(path, run) || modelCallsPath(path, run) ||
           new RegExp(`^${run}(/details)?$`).test(path))) ||
          (method === 'POST' && (path === '/reviews' || path === '/reviews/drafts' || technicianMutation)))) {
        return { status: 400 };
    }
    const controller = new AbortController();
    requests.set(id, controller);
    const options = { method, credentials: 'same-origin', mode: 'same-origin',
        redirect: 'error', cache: 'no-store', signal: controller.signal, headers: {} };
    try {
        if (method === 'POST') {
            const session = await fetch('/dashboard/session', { ...options, method: 'GET' });
            if (!session.ok) return { status: session.status };
            const { token } = await session.json();
            options.headers['X-CSRF-TOKEN'] = token;
            options.headers['Content-Type'] = 'application/json';
            options.body = JSON.stringify(body);
        }
        const response = await fetch(path, options);
        // Only explicit technician mutations have safe typed error bodies for the browser.
        let data = null;
        if (response.ok) data = await response.json();
        else if (technicianMutation && [400, 404, 409].includes(response.status)) {
            try { data = await response.json(); }
            catch { /* Antiforgery/model binding can return an empty error body. */ }
        }
        // Interactive Server interop completions share SignalR's bounded message size.
        // Keep large finding/history payloads off that message; Blazor streams them in chunks.
        if (data !== null) {
            const bytes = new TextEncoder().encode(JSON.stringify(data));
            if (bytes.byteLength > 16 * 1024)
                return { status: response.status, data: null, dataStream: DotNet.createJSStreamReference(bytes) };
        }
        return { status: response.status, data };
    } catch {
        return { status: 0 };
    } finally {
        requests.delete(id);
    }
}
