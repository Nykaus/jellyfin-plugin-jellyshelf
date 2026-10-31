// Per-account reader state client.
//
// Talks to the plugin's /JellyShelf/State/{key} endpoints. Each value is opaque
// JSON scoped to the signed-in Jellyfin account, so reading preferences and the
// last position in each book follow the user across devices. All calls fail
// silently (returning null / doing nothing) so the reader still works when the
// store is briefly unreachable.

export function makeStateClient(base, auth) {
    const url = (key) => `${base}/JellyShelf/State/`
        + key.split('/').map(encodeURIComponent).join('/');
    const timers = new Map();

    async function load(key) {
        try {
            const res = await fetch(url(key), { headers: { Authorization: auth } });
            if (res.status === 204 || !res.ok) {
                return null;
            }
            return await res.json();
        } catch {
            return null;
        }
    }

    function put(key, obj, keepalive) {
        return fetch(url(key), {
            method: 'PUT',
            headers: { Authorization: auth, 'Content-Type': 'application/json' },
            body: JSON.stringify(obj),
            keepalive: Boolean(keepalive),
        }).catch(() => {});
    }

    // Debounced save: coalesces rapid changes (page turns, zoom steps) into one
    // write. A delay of 0 writes on the next tick.
    function save(key, obj, delay = 800) {
        clearTimeout(timers.get(key));
        timers.set(key, setTimeout(() => { put(key, obj); }, delay));
    }

    // Immediate save that survives the page being closed (uses keepalive so the
    // request outlives a pagehide/visibilitychange).
    function saveNow(key, obj) {
        clearTimeout(timers.get(key));
        put(key, obj, true);
    }

    return { load, save, saveNow };
}
