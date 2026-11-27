// Route-owned listener: no document/global key handlers, no finding data in storage.
const scopes = new WeakMap();
const key = 'reviewforge.commands';
export function preferences() {
    try {
        const value = JSON.parse(localStorage.getItem(key));
        return { singleLetter: value?.singleLetter === true, advance: value?.advance !== false };
    } catch { return { singleLetter: false, advance: true }; }
}
export function savePreferences(root, singleLetter, advance) {
    const value = { singleLetter: singleLetter === true, advance: advance !== false };
    const scope = scopes.get(root);
    if (scope) scope.preferences = value;
    try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* Optional presentation preferences only. */ }
}
export function attach(root, callback, mode = 'findings') {
    detach(root);
    const scope = { preferences: preferences() };
    scope.keydown = event => {
        const target = event.target;
        if (!root.isConnected || !root.contains(target) || event.defaultPrevented || event.repeat || event.isComposing || event.keyCode === 229 || event.altKey
            || root.ownerDocument.querySelector('dialog[open]') || target?.isContentEditable
            || target?.closest?.('input, textarea, select, [contenteditable]')) return;
        const worklist = mode === 'worklist';
        const row = target?.closest?.(worklist ? '[data-worklist-row]' : '[data-finding-id]');
        // Worklist controls keep native activation; only row navigation/expansion is intercepted.
        const palette = (event.ctrlKey || event.metaKey) && !event.shiftKey && event.key.toLowerCase() === 'k';
        if (!row && target?.closest?.('button, a[href], summary, [role="button"]') && !(worklist && palette)) return;
        if (worklist && row && !event.ctrlKey && !event.metaKey && !event.shiftKey) {
            const rows = [...root.querySelectorAll('[data-worklist-row]')];
            const index = rows.indexOf(row);
            let next;
            if (event.key === 'ArrowDown') next = Math.min(rows.length - 1, index + 1);
            else if (event.key === 'ArrowUp') next = Math.max(0, index - 1);
            else if (event.key === 'Home') next = 0;
            else if (event.key === 'End') next = rows.length - 1;
            if (next !== undefined && rows[next]) { event.preventDefault(); rows[next].focus(); return; }
            const expanded = row.getAttribute('aria-expanded');
            if ((event.key === 'ArrowRight' && expanded === 'false') || (event.key === 'ArrowLeft' && expanded === 'true')) {
                event.preventDefault(); row.click(); return;
            }
        }
        let command;
        if ((event.ctrlKey || event.metaKey) && !event.shiftKey && event.key.toLowerCase() === 'k') command = 'commands';
        else if (event.ctrlKey || event.metaKey) return;
        else if (!worklist && event.key === 'ArrowDown' && row) command = 'next';
        else if (!worklist && event.key === 'ArrowUp' && row) command = 'previous';
        else if (!event.shiftKey) {
            command = worklist ? ({ '/': 'search' })[event.key]
                : ({ Enter: row ? 'inspect' : undefined, ' ': row ? 'select' : undefined, Escape: 'escape', '/': 'search' })[event.key];
            if (!worklist && !command && scope.preferences.singleLetter) command = ({ j: 'next', k: 'previous', a: 'approve', r: 'reject', d: 'defer', '?': 'commands' })[event.key];
        } else if (!worklist && event.key === '?' && scope.preferences.singleLetter) command = 'commands';
        if (!command || root.dataset?.disabledCommands?.split(' ').includes(command)) return;
        event.preventDefault();
        callback.invokeMethodAsync('ExecuteCommand', command, !!event.shiftKey);
    };
    scopes.set(root, scope);
    root.addEventListener('keydown', scope.keydown);
    return scope.preferences;
}
export function detach(root) {
    const scope = scopes.get(root);
    if (scope) {
        root.removeEventListener('keydown', scope.keydown);
        if (scope.scroll) scope.window.removeEventListener('scroll', scope.scroll);
        scopes.delete(root);
    }
}
// Only circuit memory receives scroll; no payload or session storage is involved.
export function attachWorklist(root, callback, scrollY) {
    attach(root, callback, 'worklist');
    const scope = scopes.get(root);
    scope.window = root.ownerDocument.defaultView;
    scope.window.scrollTo(0, Math.max(0, scrollY));
    scope.scroll = () => { if (root.isConnected) callback.invokeMethodAsync('RememberScroll', scope.window.scrollY); };
    scope.window.addEventListener('scroll', scope.scroll, { passive: true });
}
export function focusSearch(root) { root.querySelector('[data-queue-search]')?.focus(); }
export function focusReason(root) { root.querySelector('.finding-decision-editor textarea')?.focus(); }
