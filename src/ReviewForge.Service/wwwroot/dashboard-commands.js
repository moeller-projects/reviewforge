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
export function attach(root, callback) {
    detach(root);
    const scope = { preferences: preferences() };
    scope.keydown = event => {
        const target = event.target;
        if (!root.isConnected || !root.contains(target) || event.defaultPrevented || event.repeat || event.isComposing || event.keyCode === 229 || event.altKey
            || root.ownerDocument.querySelector('dialog[open]') || target?.isContentEditable
            || target?.closest?.('input, textarea, select, [contenteditable]')) return;
        const row = target?.closest?.('[data-finding-id]');
        if (!row && target?.closest?.('button, a[href], summary, [role="button"]')) return;
        let command;
        if ((event.ctrlKey || event.metaKey) && !event.shiftKey && event.key.toLowerCase() === 'k') command = 'commands';
        else if (event.ctrlKey || event.metaKey) return;
        else if (event.key === 'ArrowDown' && row) command = 'next';
        else if (event.key === 'ArrowUp' && row) command = 'previous';
        else if (!event.shiftKey) {
            command = ({ Enter: row ? 'inspect' : undefined, ' ': row ? 'select' : undefined, Escape: 'escape', '/': 'search' })[event.key];
            if (!command && scope.preferences.singleLetter) command = ({ j: 'next', k: 'previous', a: 'approve', r: 'reject', d: 'defer', '?': 'commands' })[event.key];
        } else if (event.key === '?' && scope.preferences.singleLetter) command = 'commands';
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
    if (scope) { root.removeEventListener('keydown', scope.keydown); scopes.delete(root); }
}
export function focusSearch(root) { root.querySelector('[data-queue-search]')?.focus(); }
export function focusReason(root) { root.querySelector('.finding-decision-editor textarea')?.focus(); }
