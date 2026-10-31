using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SharpCompress.Archives;

namespace Jellyfin.Plugin.JellyShelf.Controllers;

/// <summary>
/// Serves a book's content to the reader: tells it what kind of book an item is,
/// and streams one comic-archive page at a time (so a large CBZ is never fully
/// loaded in the browser). Authenticated like Jellyfin's own item endpoints.
/// </summary>
[ApiController]
[Route("JellyShelf/Books")]
[Authorize]
public class BookController : ControllerBase
{
    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp",
    };

    private static readonly HashSet<string> _comicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".zip", ".cbr", ".rar", ".cb7", ".7z", ".cbt", ".tar",
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookController"/> class.
    /// </summary>
    /// <param name="libraryManager">The host library manager.</param>
    /// <param name="authorizationContext">The host authorization context.</param>
    public BookController(ILibraryManager libraryManager, IAuthorizationContext authorizationContext)
    {
        _libraryManager = libraryManager;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// Describes a book to the reader: whether it is a PDF, a comic archive (with
    /// its page count) or something else.
    /// </summary>
    /// <param name="id">The book's item id.</param>
    /// <returns>The book kind, and the page count for a comic.</returns>
    [HttpGet("{id}/Info")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetInfo([FromRoute] Guid id)
    {
        var path = await ResolvePathAsync(id).ConfigureAwait(false);
        if (path is null)
        {
            return NotFound();
        }

        var ext = Path.GetExtension(path);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new { kind = "pdf" });
        }

        if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            // The reader reads the EPUB client-side (foliate-js unzips it in the
            // browser), so the server only needs to name the kind.
            return new JsonResult(new { kind = "epub" });
        }

        if (_comicExtensions.Contains(ext))
        {
            return new JsonResult(new { kind = "comic", pageCount = ComicPages(path).Count });
        }

        return new JsonResult(new { kind = "other" });
    }

    /// <summary>
    /// Streams one page (image) of a comic archive, by its zero-based index in the
    /// archive's natural page order.
    /// </summary>
    /// <param name="id">The book's item id.</param>
    /// <param name="index">The zero-based page index.</param>
    /// <returns>The page image.</returns>
    [HttpGet("{id}/Pages/{index:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPage([FromRoute] Guid id, [FromRoute] int index)
    {
        var path = await ResolvePathAsync(id).ConfigureAwait(false);
        if (path is null || !_comicExtensions.Contains(Path.GetExtension(path)))
        {
            return NotFound();
        }

        var pages = ComicPages(path);
        if (index < 0 || index >= pages.Count)
        {
            return NotFound();
        }

        using var archive = ArchiveFactory.Open(path);
        var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && e.Key == pages[index]);
        if (entry is null)
        {
            return NotFound();
        }

        // The archive entry stream is not seekable; copy it out so the result can
        // set Content-Length and the archive handle can be released right away.
        var buffer = new MemoryStream();
        await using (var source = entry.OpenEntryStream())
        {
            await source.CopyToAsync(buffer).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return File(buffer, ContentTypeFor(pages[index]));
    }

    // The image entries of a comic archive (ZIP/CBZ, RAR/CBR, 7z, tar), in natural
    // (page) order.
    private static List<string> ComicPages(string path)
    {
        using var archive = ArchiveFactory.Open(path);
        return archive.Entries
            .Where(e => !e.IsDirectory && e.Key is not null && _imageExtensions.Contains(Path.GetExtension(e.Key)))
            .Select(e => e.Key!)
            .OrderBy(name => name, NaturalComparer.Instance)
            .ToList();
    }

    // The item's file path, when the caller is signed in and allowed to see it.
    private async Task<string?> ResolvePathAsync(Guid id)
    {
        var info = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info.UserId.Equals(Guid.Empty))
        {
            return null;
        }

        BaseItem? item;
        try
        {
            item = _libraryManager.GetItemById<BaseItem>(id, info.UserId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var path = item?.Path;
        return string.IsNullOrEmpty(path) || !System.IO.File.Exists(path) ? null : path;
    }

    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };

    // Orders names so page2 comes before page10 (numeric runs compared as numbers).
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i]))
                    {
                        i++;
                    }

                    while (j < y.Length && char.IsDigit(y[j]))
                    {
                        j++;
                    }

                    var nx = x.AsSpan(si, i - si).TrimStart('0');
                    var ny = y.AsSpan(sj, j - sj).TrimStart('0');
                    if (nx.Length != ny.Length)
                    {
                        return nx.Length - ny.Length;
                    }

                    var cmp = nx.CompareTo(ny, StringComparison.Ordinal);
                    if (cmp != 0)
                    {
                        return cmp;
                    }
                }
                else
                {
                    var cmp = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (cmp != 0)
                    {
                        return cmp;
                    }

                    i++;
                    j++;
                }
            }

            return (x.Length - i) - (y.Length - j);
        }
    }
}
