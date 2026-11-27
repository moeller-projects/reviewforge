// Drag identity is circuit-local memory only. DataTransfer carries no finding/source/reason payload.
const scopes = new WeakMap();
export function attach(root, callback) {
    detach(root);
    const scope = { id: null };
    const column = event => event.target.closest?.('[data-board-column]');
    const valid = target => target && root.contains(target) && !target.closest('[hidden]');
    scope.start = event => {
        const card = event.target.closest?.('[data-board-card]');
        scope.id = null;
        if (!valid(card) || card.dataset.boardBlocked === 'true' || event.target.closest?.('input, select, textarea, label')) return;
        scope.id = card.dataset.boardCard;
        event.dataTransfer.setData('text/plain', 'finding');
        event.dataTransfer.effectAllowed = 'move';
    };
    scope.over = event => {
        if (scope.id && valid(column(event))) { event.preventDefault(); event.dataTransfer.dropEffect = 'move'; }
    };
    scope.end = () => { scope.id = null; };
    scope.drop = async event => {
        const target = column(event), id = scope.id;
        scope.id = null;
        if (scopes.get(root) !== scope || !id || !valid(target)) return;
        event.preventDefault();
        // Blazor rechecks current membership/version/eligibility. No DOM relocation or replay.
        const status = root.querySelector?.('[data-board-status]');
        if (status) status.textContent = 'Move requested… awaiting confirmation.';
        try {
            await callback.invokeMethodAsync('MoveCard', id, target.dataset.boardColumn);
            if (scopes.get(root) === scope && status) status.textContent = '';
        } catch {
            // A disconnected result is visibly unconfirmed, not a saved move or automatic retry.
            if (scopes.get(root) === scope && status) status.textContent = 'Move was not confirmed. Refresh before deciding again.';
        }
    };
    for (const [name, fn] of [['dragstart', scope.start], ['dragover', scope.over], ['dragend', scope.end], ['drop', scope.drop]]) root.addEventListener(name, fn);
    scopes.set(root, scope);
}
export function detach(root) {
    const scope = scopes.get(root);
    if (!scope) return;
    scope.id = null;
    scopes.delete(root);
    for (const [name, fn] of [['dragstart', scope.start], ['dragover', scope.over], ['dragend', scope.end], ['drop', scope.drop]]) root.removeEventListener(name, fn);
}
