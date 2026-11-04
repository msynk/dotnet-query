// Web Locks for DotNetQuery.Blazor: an exclusive lock shared by every tab of the same origin, used by
// WebLocksMutationQueueLock so one tab at a time replays a durable mutation queue stored in IndexedDB.
// acquire() resolves once the lock is held (true) or when the browser has no Web Locks API (false: no
// coordination); release() lets it go; cancel() abandons a request that is still waiting.

const held = new Map();
const waiting = new Map();

export function acquire(name, id) {
    if (!globalThis.navigator || !navigator.locks || typeof navigator.locks.request !== 'function') {
        return Promise.resolve(false);
    }

    return new Promise((resolve, reject) => {
        const controller = new AbortController();
        waiting.set(id, controller);
        navigator.locks.request(name, { mode: 'exclusive', signal: controller.signal }, () => {
            waiting.delete(id);
            // The lock is held until the returned promise settles, i.e. until release(id).
            return new Promise(release => {
                held.set(id, release);
                resolve(true);
            });
        }).catch(error => {
            waiting.delete(id);
            reject(error);
        });
    });
}

export function release(id) {
    const done = held.get(id);
    if (done) {
        held.delete(id);
        done();
    }
}

export function cancel(id) {
    const controller = waiting.get(id);
    if (controller) {
        waiting.delete(id);
        controller.abort();
    }

    release(id);
}
