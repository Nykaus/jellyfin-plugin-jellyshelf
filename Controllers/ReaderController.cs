using System;
using Jellyfin.Plugin.JellyShelf.Reader;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyShelf.Controllers;

/// <summary>
/// Serves the in-browser reader at <c>/JellyShelf/</c> and its static assets.
/// Everything here is anonymous: a plain browser navigation cannot attach a
/// Jellyfin Authorization header, so the reader's JavaScript authenticates
/// itself (it reads jellyfin-web's session from the same-origin local storage
/// and downloads the book through Jellyfin's own authorized endpoints).
/// </summary>
[ApiController]
[Route("JellyShelf")]
public class ReaderController : ControllerBase
{
    private readonly ReaderAssets _assets;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReaderController"/> class.
    /// </summary>
    /// <param name="assets">The embedded reader assets.</param>
    public ReaderController(ReaderAssets assets)
    {
        _assets = assets;
    }

    /// <summary>
    /// Serves the reader page. The page references its assets with relative
    /// URLs, so the request must end in a slash for those to resolve; a request
    /// without one is redirected, keeping any configured base URL and the query.
    /// </summary>
    /// <returns>The reader page, or a redirect to the trailing-slash URL.</returns>
    [HttpGet("")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public ActionResult GetReader()
    {
        if (!(Request.Path.Value ?? string.Empty).EndsWith('/'))
        {
            return Redirect("JellyShelf/" + Request.QueryString);
        }

        return Asset(ReaderAssets.IndexFile);
    }

    /// <summary>
    /// Serves one reader asset by its path under the reader route.
    /// </summary>
    /// <param name="path">The asset path (empty resolves to the index page).</param>
    /// <returns>The asset, or 404 when it is not part of the reader.</returns>
    [HttpGet("{**path}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetAsset([FromRoute] string? path)
    {
        return Asset(path);
    }

    private ActionResult Asset(string? path)
    {
        if (!_assets.TryOpen(path, out var content, out var contentType))
        {
            return NotFound();
        }

        return File(content, contentType);
    }
}
