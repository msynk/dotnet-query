// Scroll restoration for DotNetQuery.Blazor: remembers a scroll position per id in sessionStorage and puts it back
// when the same view renders again (for example navigating back to a long, cached list).

const storageKey = id => `dnq-scroll:${id}`;
const target = selector => (selector ? document.querySelector(selector) : null);

export function restore(id, selector) {
    const saved = sessionStorage.getItem(storageKey(id));
    if (saved === null) {
        return false;
    }

    const top = Number(saved);
    const element = target(selector);
    if (element) {
        element.scrollTop = top;
    } else {
        window.scrollTo(0, top);
    }

    return true;
}

export function track(id, selector) {
    const element = target(selector);
    const source = element ?? window;
    let pending = false;
    const save = () => {
        if (pending) {
            return;
        }

        pending = true;
        requestAnimationFrame(() => {
            pending = false;
            const top = element ? element.scrollTop : window.scrollY;
            sessionStorage.setItem(storageKey(id), String(top));
        });
    };

    source.addEventListener('scroll', save, { passive: true });
    return {
        dispose() {
            source.removeEventListener('scroll', save);
        },
    };
}
