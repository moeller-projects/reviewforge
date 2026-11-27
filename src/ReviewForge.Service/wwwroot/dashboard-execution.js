// Route-local stage navigation and explicit safe-identifier copy. No global listeners or storage.
export function attach(root, clipboard = globalThis.navigator?.clipboard) {
    let disposed = false;
    const keydown = event => {
        if (disposed || event.isComposing || event.repeat || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
        const stage = event.target.closest?.('[data-stage-index]');
        if (!stage || !root.contains(stage)) return;
        const rows = [...root.querySelectorAll('[data-stage-index]')];
        const current = rows.indexOf(stage);
        let next;
        switch (event.key) {
            case 'ArrowDown': next = Math.min(rows.length - 1, current + 1); break;
            case 'ArrowUp': next = Math.max(0, current - 1); break;
            case 'Home': next = 0; break;
            case 'End': next = rows.length - 1; break;
            default: return;
        }
        if (current < 0 || current === next) return;
        event.preventDefault();
        rows[next].focus();
        rows[next].click();
    };
    const click = async event => {
        if (disposed) return;
        const button = event.target.closest?.('[data-copy-value]');
        if (!button || !root.contains(button)) return;
        const feedback = root.querySelector('[data-copy-feedback]');
        if (feedback) feedback.textContent = '';
        try {
            if (!clipboard) throw new Error('clipboard unavailable');
            await clipboard.writeText(button.dataset.copyValue);
            if (!disposed && feedback) feedback.textContent = `${button.dataset.copyKind} copied.`;
        } catch {
            // Browser permission/availability failures never expose exception text.
            if (!disposed && feedback) feedback.textContent = 'Copy unavailable. Select the displayed identifier and copy it manually.';
        }
    };
    root.addEventListener('keydown', keydown);
    root.addEventListener('click', click);
    return {
        focusSelected() { if (!disposed) root.querySelector('[data-stage-index][aria-pressed="true"]')?.focus(); },
        dispose() {
            disposed = true;
            root.removeEventListener('keydown', keydown);
            root.removeEventListener('click', click);
        }
    };
}
