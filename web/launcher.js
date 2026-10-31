// Loaded by jellyfin-web (as a classic script, before its own bundles) when the
// JellyShelf web integration is on. It defines a "book player" that jellyfin-web
// picks up from config.json; at priority 0 it is chosen ahead of the built-in
// book player for the formats JellyShelf reads, and opens them in the JellyShelf
// reader instead. When the integration is off the file is never injected.
(function () {
    'use strict';

    var PLAYER_NAME = 'JellyShelfBookPlayer';
    var LOAD_WAIT_MS = 10000;
    var requested = false;

    // jellyfin-web lives at {base}/web/, the reader at {base}/JellyShelf/.
    function readerBase() {
        var path = window.location.pathname;
        var web = path.toLowerCase().lastIndexOf('/web/');
        return (web >= 0 ? path.slice(0, web) : '') + '/JellyShelf/';
    }

    function readerUrl(itemId) {
        return readerBase() + '?id=' + encodeURIComponent(itemId);
    }

    function JellyShelfBookPlayer() {
        this.name = 'JellyShelf';
        this.type = 'mediaplayer';
        this.id = 'jellyshelfbookplayer';
        this.priority = 0;
        this.isLocalPlayer = true;
    }

    JellyShelfBookPlayer.prototype.canPlayMediaType = function (mediaType) {
        return (mediaType || '').toLowerCase() === 'book';
    };

    // The formats JellyShelf opens: PDF and comic archives.
    JellyShelfBookPlayer.prototype.canPlayItem = function (item) {
        return !!(item && typeof item.Path === 'string'
            && /\.(pdf|cbz|cbr|zip|rar|cb7|cbt)$/i.test(item.Path));
    };

    JellyShelfBookPlayer.prototype.play = function (options) {
        var item = options && options.items && options.items[0];
        if (!item || !item.Id) {
            return Promise.reject(new Error('JellyShelf: no book to open'));
        }
        window.location.assign(readerUrl(item.Id));
        return Promise.resolve();
    };

    // The rest of the player interface; the page navigates away before it matters.
    JellyShelfBookPlayer.prototype.stop = function () { return Promise.resolve(); };
    JellyShelfBookPlayer.prototype.destroy = function () {};
    JellyShelfBookPlayer.prototype.currentItem = function () { return null; };
    JellyShelfBookPlayer.prototype.currentTime = function () { return 0; };
    JellyShelfBookPlayer.prototype.duration = function () { return 0; };
    JellyShelfBookPlayer.prototype.paused = function () { return false; };
    JellyShelfBookPlayer.prototype.volume = function () { return 100; };
    JellyShelfBookPlayer.prototype.isMuted = function () { return false; };

    // jellyfin-web loads the player named in config.json by calling this global.
    window[PLAYER_NAME] = function () {
        requested = true;
        return Promise.resolve(JellyShelfBookPlayer);
    };

    // If jellyfin-web never asks for the player, its plugin loading changed; say so
    // once and leave the built-in book player alone.
    window.addEventListener('load', function () {
        setTimeout(function () {
            if (!requested) {
                console.warn('JellyShelf: jellyfin-web did not load ' + PLAYER_NAME
                    + '; Play keeps the built-in book reader.');
            }
        }, LOAD_WAIT_MS);
    });
})();
