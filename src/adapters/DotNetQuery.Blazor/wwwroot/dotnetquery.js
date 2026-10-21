// Browser-signal bridge for DotNetQuery.Blazor.
// Forwards focus / connectivity / visibility events to the .NET BrowserSignalService.
// Each call to register() returns its own handle, so several circuits/apps in one page never clobber
// each other's listeners.

export function register(dotNetRef) {
    const invoke = (method, ...args) => {
        dotNetRef.invokeMethodAsync(method, ...args).catch(() => {
            // The .NET side is gone (circuit closed); the handle's dispose() cleans up.
        });
    };

    const onFocus = () => invoke('OnFocus');
    const onOnline = () => invoke('OnConnectivityChanged', true);
    const onOffline = () => invoke('OnConnectivityChanged', false);
    const onVisibility = () => {
        if (document.visibilityState === 'visible') {
            invoke('OnResume');
        }
    };

    window.addEventListener('focus', onFocus);
    window.addEventListener('online', onOnline);
    window.addEventListener('offline', onOffline);
    document.addEventListener('visibilitychange', onVisibility);

    // Report the initial connectivity state.
    invoke('OnConnectivityChanged', navigator.onLine);

    return {
        dispose() {
            window.removeEventListener('focus', onFocus);
            window.removeEventListener('online', onOnline);
            window.removeEventListener('offline', onOffline);
            document.removeEventListener('visibilitychange', onVisibility);
        },
    };
}
