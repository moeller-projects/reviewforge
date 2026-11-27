const panes = new WeakMap();
const preferenceKey = 'reviewforge.queue-width';
function bounded(value) { const n = Number(value); return Number.isFinite(n) ? Math.max(25, Math.min(65, n)) : 40; }
export function attach(root) {
    detach(root);
    const input = root.querySelector('[data-pane-size]');
    let width = 40;
    try { const stored = localStorage.getItem(preferenceKey); if (stored !== null) width = bounded(stored); } catch { /* Presentation preference is optional. */ }
    const apply = () => { width = bounded(input.value); input.value = String(width); root.style.setProperty('--queue-width', `${width}%`); };
    input.value = String(width); apply();
    const resize = () => { apply(); try { localStorage.setItem(preferenceKey, String(width)); } catch { /* No finding data is stored. */ } };
    input.addEventListener('input', resize);
    panes.set(root, { input, resize });
}
export function detach(root) { const pane = panes.get(root); if (pane) { pane.input.removeEventListener('input', pane.resize); panes.delete(root); } }
function focusAfterLayout(root, resolveTarget) {
    // Called from OnAfterRenderAsync; wait for layout and retire superseded or detached transitions.
    const scope = panes.get(root);
    if (!scope) return;
    const transition = scope.transition = (scope.transition ?? 0) + 1;
    requestAnimationFrame(() => {
        if (panes.get(root) !== scope || scope.transition !== transition) return;
        const target = resolveTarget();
        target?.focus({ preventScroll: true });
        target?.scrollIntoView({ block: 'nearest' });
    });
}
export function enterInspector(root) {
    focusAfterLayout(root, () => root.querySelector('[data-inspector-focus]'));
}
export function returnToRow(root, id) {
    // Identity lookup avoids building CSS selectors from finding data; filtered rows fall back to visible search.
    focusAfterLayout(root, () => [...root.querySelectorAll('[data-finding-id]')].find(row => row.dataset.findingId === id)
        ?? root.querySelector('[data-queue-search]'));
}
