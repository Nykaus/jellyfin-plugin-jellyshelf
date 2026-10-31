using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.JellyShelf.Integration;

/// <summary>
/// The two edits the integration makes to jellyfin-web as it is served: a script
/// tag for the launcher in <c>index.html</c>, and the launcher's player name in
/// <c>config.json</c>'s plugin list.
/// </summary>
public static class WebPatches
{
    /// <summary>
    /// The global the launcher defines and that <c>config.json</c> names.
    /// </summary>
    public const string PlayerName = "JellyShelfBookPlayer";

    // Relative to {base}/web/, so it resolves under any configured BaseUrl.
    private const string LauncherTag = "<script src=\"../JellyShelf/launcher.js\"></script>";

    /// <summary>
    /// Adds the launcher script to <c>index.html</c> (a classic script, which runs
    /// before jellyfin-web's deferred bundles).
    /// </summary>
    /// <param name="html">The served page.</param>
    /// <returns>The patched page, or null to leave it unchanged.</returns>
    public static string? PatchIndex(string html)
    {
        if (html.Contains(LauncherTag, StringComparison.Ordinal))
        {
            return null;
        }

        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? null : html.Insert(head, LauncherTag);
    }

    /// <summary>
    /// Adds the launcher's player to the plugins jellyfin-web loads at startup.
    /// </summary>
    /// <param name="json">The served config.</param>
    /// <returns>The patched config, or null to leave it unchanged.</returns>
    public static string? PatchConfig(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["plugins"] is not JsonArray plugins)
        {
            return null;
        }

        if (plugins.Any(p => p is JsonValue value && value.TryGetValue<string>(out var name) && name == PlayerName))
        {
            return null;
        }

        plugins.Add(PlayerName);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
