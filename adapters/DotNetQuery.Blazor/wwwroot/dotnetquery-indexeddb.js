// Minimal IndexedDB key/value store for DotNetQuery's IndexedDbQueryPersister.
// The C# side serializes each PersistedQuery to a JSON string and this module stores/returns that string
// keyed by the query key - so JS stays a dumb string store and all shaping happens in .NET.
//
// Reference it once from your Blazor host page:
//   <script src="_content/DotNetQuery.Blazor/dotnetquery-indexeddb.js"></script>
(function () {
    "use strict";

    function openDb(dbName, storeName) {
        return new Promise(function (resolve, reject) {
            const req = indexedDB.open(dbName, 1);
            req.onupgradeneeded = function () {
                const db = req.result;
                if (!db.objectStoreNames.contains(storeName)) {
                    db.createObjectStore(storeName);
                }
            };
            req.onsuccess = function () { resolve(req.result); };
            req.onerror = function () { reject(req.error); };
        });
    }

    // Runs a write and resolves when the transaction commits.
    async function write(dbName, storeName, action) {
        const db = await openDb(dbName, storeName);
        try {
            await new Promise(function (resolve, reject) {
                const tx = db.transaction(storeName, "readwrite");
                action(tx.objectStore(storeName));
                tx.oncomplete = function () { resolve(); };
                tx.onerror = function () { reject(tx.error); };
                tx.onabort = function () { reject(tx.error); };
            });
        } finally {
            db.close();
        }
    }

    // Runs a read and resolves with the request's result.
    async function read(dbName, storeName, request) {
        const db = await openDb(dbName, storeName);
        try {
            return await new Promise(function (resolve, reject) {
                const tx = db.transaction(storeName, "readonly");
                const req = request(tx.objectStore(storeName));
                req.onsuccess = function () { resolve(req.result); };
                req.onerror = function () { reject(req.error); };
            });
        } finally {
            db.close();
        }
    }

    window.dotnetQueryIndexedDb = {
        save: function (dbName, storeName, key, json) {
            return write(dbName, storeName, function (store) { store.put(json, key); });
        },
        load: async function (dbName, storeName, key) {
            const value = await read(dbName, storeName, function (store) { return store.get(key); });
            return value === undefined ? null : value;
        },
        loadAll: async function (dbName, storeName) {
            const values = await read(dbName, storeName, function (store) { return store.getAll(); });
            return values || [];
        },
        remove: function (dbName, storeName, key) {
            return write(dbName, storeName, function (store) { store.delete(key); });
        },
        clear: function (dbName, storeName) {
            return write(dbName, storeName, function (store) { store.clear(); });
        }
    };
})();
