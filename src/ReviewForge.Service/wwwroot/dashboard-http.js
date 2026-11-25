const requests = new Map();

export function abort(id) {
    requests.get(id)?.abort();
}

export async function send(id, method, path, body) {
    if (!((method === 'GET' && path === '/reviews/dashboard') ||
          (method === 'POST' && path === '/reviews'))) {
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
        return { status: response.status, data: response.ok ? await response.json() : null };
    } catch {
        return { status: 0 };
    } finally {
        requests.delete(id);
    }
}
