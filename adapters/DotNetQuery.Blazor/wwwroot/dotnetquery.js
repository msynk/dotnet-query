// Browser-signal bridge for DotNetQuery.Blazor.
// Forwards focus / connectivity / visibility events to the .NET BrowserSignalService.

let handlers = null;

export function register(dotNetRef) {
    const onFocus = () => dotNetRef.invokeMethodAsync('OnFocus');
    const onOnline = () => dotNetRef.invokeMethodAsync('OnConnectivityChanged', true);
    const onOffline = () => dotNetRef.invokeMethodAsync('OnConnectivityChanged', false);
    const onVisibility = () => {
        if (document.visibilityState === 'visible') {
            dotNetRef.invokeMethodAsync('OnResume');
        }
    };

    window.addEventListener('focus', onFocus);
    window.addEventListener('online', onOnline);
    window.addEventListener('offline', onOffline);
    document.addEventListener('visibilitychange', onVisibility);

    handlers = { onFocus, onOnline, onOffline, onVisibility };

    // Report the initial connectivity state.
    dotNetRef.invokeMethodAsync('OnConnectivityChanged', navigator.onLine);
}

export function unregister() {
    if (!handlers) {
        return;
    }

    window.removeEventListener('focus', handlers.onFocus);
    window.removeEventListener('online', handlers.onOnline);
    window.removeEventListener('offline', handlers.onOffline);
    document.removeEventListener('visibilitychange', handlers.onVisibility);
    handlers = null;
}
