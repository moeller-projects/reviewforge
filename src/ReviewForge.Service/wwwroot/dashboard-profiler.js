// Bounded-page, route-owned navigation. No storage, network reads or mutations.
export function attach(root, callback, clipboard = globalThis.navigator?.clipboard) {
    let disposed = false, page;
    const positions = new Map();
    const scroll = root.querySelector('[data-profile-scroll]');
    const remember = () => {
        if (disposed || page === undefined || !scroll) return;
        if (positions.size >= 10000 && !positions.has(page)) positions.delete(positions.keys().next().value);
        positions.set(page, [scroll.scrollTop, scroll.scrollLeft]);
    };
    scroll?.addEventListener('scroll', remember, { passive: true });
    const keydown = event => {
        const target = event.target;
        if (disposed || event.defaultPrevented || event.isComposing || event.keyCode === 229 || event.repeat
            || event.altKey || event.shiftKey || !root.contains(target) || target?.isContentEditable
            || target?.closest?.('input, textarea, select, [contenteditable]')
            || root.ownerDocument.querySelector('dialog[open]')) return;
        const palette = (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k';
        if (palette) { event.preventDefault(); callback.invokeMethodAsync('ExecuteProfileCommand', 'commands'); return; }
        if (event.ctrlKey || event.metaKey) return;
        const row = target.closest?.('[data-profile-row]');
        if (!row && target.closest?.('button, a[href], summary, [role="button"]')) return;
        if (event.key === '/') { event.preventDefault(); callback.invokeMethodAsync('ExecuteProfileCommand', 'search'); return; }
        if (!row) return;
        const rows = [...root.querySelectorAll('[data-profile-row]')], index = rows.indexOf(row);
        const next = ({ ArrowDown: Math.min(rows.length - 1, index + 1), ArrowUp: Math.max(0, index - 1), Home: 0, End: rows.length - 1 })[event.key];
        if (next === undefined || next === index || index < 0) return;
        event.preventDefault(); rows[next].focus({ preventScroll: true }); rows[next].click();
        rows[next].scrollIntoView?.({ block: 'nearest' });
    };
    const click = async event => {
        const button = event.target.closest?.('[data-copy-value]');
        if (disposed || !button || !root.contains(button)) return;
        const feedback = root.querySelector('[data-copy-feedback]');
        try {
            if (!clipboard) throw Error('unavailable');
            await clipboard.writeText(button.dataset.copyValue);
            if (!disposed && feedback) feedback.textContent = `${button.dataset.copyKind} copied.`;
        } catch {
            if (!disposed && feedback) feedback.textContent = 'Copy unavailable. Select the displayed identifier and copy it manually.';
        }
    };
    root.addEventListener('keydown', keydown);
    root.addEventListener('click', click);
    return {
        syncPage(key) {
            if (disposed || page === key) return;
            page = key;
            const position = positions.get(key) ?? [0, 0];
            if (scroll) { scroll.scrollTop = position[0]; scroll.scrollLeft = position[1]; }
        },
        focus(target) {
            if (disposed || root.ownerDocument.querySelector('dialog[open]')) return;
            const element = target === 'search' ? root.querySelector('[data-profile-search]')
                : [...root.querySelectorAll('[data-profile-row]')].find(r => r.dataset.profileRow === target);
            element?.focus({ preventScroll: true });
            element?.scrollIntoView?.({ block: 'nearest' });
        },
        dispose() {
            disposed = true;
            root.removeEventListener('keydown', keydown);
            root.removeEventListener('click', click);
            scroll?.removeEventListener('scroll', remember);
            positions.clear();
        }
    };
}
