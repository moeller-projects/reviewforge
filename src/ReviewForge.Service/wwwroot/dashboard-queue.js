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
    const scope = { input, resize, scroll: new Map(), views: new Map() };
    scope.remember = event => {
        const target = event.target;
        scope.focus = { target, start: target.selectionStart, end: target.selectionEnd };
    };
    scope.scrolled = event => scope.scroll.set(event.target, [event.target.scrollTop, event.target.scrollLeft]);
    for (const name of ['focusin', 'input', 'select', 'keyup']) root.addEventListener?.(name, scope.remember);
    root.addEventListener?.('scroll', scope.scrolled, true);
    panes.set(root, scope);
}
export function detach(root) {
    const pane = panes.get(root);
    if (pane) {
        pane.input.removeEventListener('input', pane.resize);
        for (const name of ['focusin', 'input', 'select', 'keyup']) root.removeEventListener?.(name, pane.remember);
        root.removeEventListener?.('scroll', pane.scrolled, true);
        panes.delete(root);
    }
}
// Run-local DOM references only. Reads/saves may temporarily disable an editor; never steal focus from a dialog or another control.
export function restoreContinuity(root) {
    const scope = panes.get(root), saved = scope?.focus;
    if (!saved || !saved.target.isConnected || saved.target.disabled || !root.contains(saved.target)) return;
    const doc = root.ownerDocument;
    if (doc.querySelector?.('dialog[open]') || (doc.activeElement !== doc.body && doc.activeElement !== saved.target)) return;
    saved.target.focus({ preventScroll: true });
    if (typeof saved.start === 'number' && typeof saved.end === 'number') saved.target.setSelectionRange?.(saved.start, saved.end);
    for (const [target, [top, left]] of scope.scroll) {
        if (target.isConnected) { target.scrollTop = top; target.scrollLeft = left; }
        else scope.scroll.delete(target);
    }
}
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
export function rememberView(root, view) {
    const scope = panes.get(root), scroller = root.querySelector('.finding-queue');
    if (scope && scroller) scope.views.set(view, [scroller.scrollTop, scroller.scrollLeft]);
}
export function restoreView(root, view) {
    const scope = panes.get(root), scroller = root.querySelector('.finding-queue');
    if (!scope || !scroller) return;
    const [top, left] = scope.views.get(view) ?? [0, 0];
    scroller.scrollTop = top; scroller.scrollLeft = left;
}
export function enterInspector(root) {
    focusAfterLayout(root, () => root.querySelector('[data-inspector-focus]'));
}
export function returnToRow(root, id) {
    // Identity lookup avoids building CSS selectors from finding data; filtered rows fall back to visible search.
    focusAfterLayout(root, () => [...root.querySelectorAll('[data-finding-id]')].find(row => row.dataset.findingId === id && !row.closest?.('[hidden]'))
        ?? root.querySelector('[data-queue-search]'));
}
