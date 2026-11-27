const dialogs = new WeakMap();
export function open(element, returnFocusId) {
    if (dialogs.has(element)) return;
    const previous = (returnFocusId ? element.ownerDocument.getElementById(returnFocusId) : null)
        ?? element.ownerDocument.activeElement;
    const cancel = event => {
        // Escape uses the same Blazor cancellation control and never confirms.
        event.preventDefault();
        element.querySelector('[data-dialog-cancel]')?.click();
    };
    dialogs.set(element, { previous, cancel });
    element.addEventListener('cancel', cancel);
    element.showModal();
    (element.querySelector('[autofocus]') ?? element).focus();
}
export function close(element) {
    const state = dialogs.get(element);
    if (!state) return;
    element.removeEventListener('cancel', state.cancel);
    if (element.open) element.close();
    dialogs.delete(element);
    if (state.previous?.isConnected) state.previous.focus();
}
