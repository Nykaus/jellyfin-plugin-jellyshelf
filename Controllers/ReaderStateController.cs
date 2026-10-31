using System;
using System.IO;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShelf.Storage;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyShelf.Controllers;

/// <summary>
/// A small per-account store for the reader: reading preferences and the last
/// position in each book, so they follow the Jellyfin account across devices.
/// Each user only ever reads and writes their own keys (the key is scoped to the
/// authenticated user), so one account can never see another's state.
/// </summary>
[ApiController]
[Route("JellyShelf/State")]
[Authorize]
public class ReaderStateController : ControllerBase
{
    // A reading position or a preferences blob is tiny; cap the body so a key
    // can never be used to stash large amounts of data.
    private const int MaxBodyLength = 64 * 1024;

    private readonly ReaderStateStore _store;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReaderStateController"/> class.
    /// </summary>
    /// <param name="store">The reader state store.</param>
    /// <param name="authorizationContext">The host authorization context.</param>
    public ReaderStateController(ReaderStateStore store, IAuthorizationContext authorizationContext)
    {
        _store = store;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// Returns the stored JSON for the signed-in user's key, or 204 when empty.
    /// </summary>
    /// <param name="key">The state key (e.g. <c>prefs/epub</c> or <c>pos/{itemId}</c>).</param>
    /// <returns>The stored JSON, or an empty response.</returns>
    [HttpGet("{**key}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> Get([FromRoute] string? key)
    {
        var userId = await UserIdAsync().ConfigureAwait(false);
        if (userId.Equals(Guid.Empty))
        {
            return Unauthorized();
        }

        if (string.IsNullOrEmpty(key))
        {
            return BadRequest();
        }

        var data = _store.Get(userId, key);
        return data is null ? NoContent() : Content(data, MediaTypeNames.Application.Json);
    }

    /// <summary>
    /// Stores (or replaces) the JSON for the signed-in user's key.
    /// </summary>
    /// <param name="key">The state key (e.g. <c>prefs/epub</c> or <c>pos/{itemId}</c>).</param>
    /// <returns>No content on success.</returns>
    [HttpPut("{**key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> Put([FromRoute] string? key)
    {
        var userId = await UserIdAsync().ConfigureAwait(false);
        if (userId.Equals(Guid.Empty))
        {
            return Unauthorized();
        }

        if (string.IsNullOrEmpty(key))
        {
            return BadRequest();
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (body.Length == 0 || body.Length > MaxBodyLength)
        {
            return BadRequest();
        }

        // Only ever store well-formed JSON, so the store can't be filled with junk.
        try
        {
            using var _ = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return BadRequest();
        }

        _store.Set(userId, key, body);
        return NoContent();
    }

    private async Task<Guid> UserIdAsync()
    {
        var info = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return info.UserId;
    }
}
