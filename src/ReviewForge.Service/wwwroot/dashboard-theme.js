// Runs synchronously before CSS: only a presentation preference is stored.
(() => {
    const key = 'reviewforge.theme';
    const media = matchMedia('(prefers-color-scheme: dark)');
    const valid = value => value === 'light' || value === 'dark' ? value : 'system';
    let preference = 'system';
    try { preference = valid(localStorage.getItem(key)); } catch { /* Storage may be disabled. */ }
    function apply() {
        document.documentElement.dataset.theme = preference === 'system' ? (media.matches ? 'dark' : 'light') : preference;
        document.documentElement.dataset.themePreference = preference;
        for (const select of document.querySelectorAll?.('[data-theme-picker]') ?? []) select.value = preference;
    }
    window.reviewForgeTheme = {
        preference: () => preference,
        set(value) {
            preference = valid(value);
            try {
                if (preference === 'system') localStorage.removeItem(key);
                else localStorage.setItem(key, preference);
            } catch { /* Theme still works for this document. */ }
            apply();
        }
    };
    media.addEventListener('change', apply);
    document.addEventListener('change', event => {
        if (event.target.matches?.('[data-theme-picker]')) window.reviewForgeTheme.set(event.target.value);
    });
    document.addEventListener('DOMContentLoaded', () => {
        apply();
        // Interactive Server inserts the picker after the document's first paint.
        new MutationObserver(apply).observe(document.body, { childList: true, subtree: true });
    });
    // Enhanced navigation replaces controls, not this document's preference.
    document.addEventListener('enhancedload', apply);
    apply();
})();
