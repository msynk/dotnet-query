// Cross-tab synchronization transport for DotNetQuery.Blazor: a BroadcastChannel carrying opaque string
// messages between browser tabs of the same origin. Each open() call returns its own handle.

export function open(name, dotNetRef) {
    if (typeof BroadcastChannel === 'undefined') {
        return null; // very old browsers: synchronization is simply unavailable
    }

    const channel = new BroadcastChannel(name);
    channel.onmessage = event => {
        if (typeof event.data === 'string') {
            dotNetRef.invokeMethodAsync('OnMessage', event.data).catch(() => {
                // The .NET side is gone (circuit closed); dispose() cleans up.
            });
        }
    };

    return {
        post(message) {
            channel.postMessage(message);
        },
        dispose() {
            channel.onmessage = null;
            channel.close();
        },
    };
}
