using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.JellyShelf.Reader;

/// <summary>
/// The reader web app (HTML/JS/CSS + vendored pdf.js), embedded in the plugin
/// assembly under the <c>jellyshelf-web/</c> logical-name prefix.
/// </summary>
public class ReaderAssets
{
    /// <summary>
    /// The file served for the reader route itself (the trailing-slash URL).
    /// </summary>
    public const string IndexFile = "index.html";

    private const string ResourcePrefix = "jellyshelf-web/";

    private static readonly Dictionary<string, string> _contentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".woff2"] = "font/woff2",
        [".wasm"] = "application/wasm",
        [".map"] = "application/json; charset=utf-8",
    };

    // Maps a request path ("index.html", "pdf.worker.mjs"…) to its manifest resource name.
    private readonly Dictionary<string, string> _resources;

    // Optional dev override: a "web" folder beside the plugin DLL. When a
    // requested file is there, it is served from disk instead of the embedded
    // copy, so the reader can be iterated on without rebuilding or restarting.
    private readonly string? _overrideDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReaderAssets"/> class by
    /// indexing every embedded resource under the reader prefix.
    /// </summary>
    public ReaderAssets()
    {
        // MSBuild writes the path under web/ into the logical name; it uses '\'
        // when built on Windows, so normalise to '/' for matching request paths.
        _resources = typeof(ReaderAssets).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(
                name => name[ResourcePrefix.Length..].Replace('\\', '/'),
                name => name,
                StringComparer.OrdinalIgnoreCase);

        var location = typeof(ReaderAssets).Assembly.Location;
        var dir = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        var web = dir is null ? null : Path.Combine(dir, "web");
        _overrideDir = web is not null && Directory.Exists(web) ? Path.GetFullPath(web) : null;
    }

    /// <summary>
    /// Opens an embedded reader file by its path. Only indexed paths resolve, so
    /// a crafted request path can never escape the reader assets.
    /// </summary>
    /// <param name="path">The path from the request, relative to the reader route. Empty means the index.</param>
    /// <param name="content">The file content when found.</param>
    /// <param name="contentType">The file's content type when found.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    public bool TryOpen(string? path, out Stream content, out string contentType)
    {
        content = Stream.Null;
        contentType = string.Empty;

        var key = string.IsNullOrEmpty(path) ? IndexFile : path.Replace('\\', '/').TrimStart('/');

        // Dev override on disk takes precedence, if it holds this file.
        if (_overrideDir is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(_overrideDir, key));
            if (candidate.StartsWith(_overrideDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && File.Exists(candidate))
            {
                content = File.OpenRead(candidate);
                contentType = _contentTypes.GetValueOrDefault(Path.GetExtension(key), "application/octet-stream");
                return true;
            }
        }

        if (!_resources.TryGetValue(key, out var resourceName))
        {
            return false;
        }

        var stream = typeof(ReaderAssets).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return false;
        }

        content = stream;
        contentType = _contentTypes.GetValueOrDefault(Path.GetExtension(key), "application/octet-stream");
        return true;
    }
}
