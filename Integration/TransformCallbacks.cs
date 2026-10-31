namespace Jellyfin.Plugin.JellyShelf.Integration;

/// <summary>
/// The file being served, as File Transformation hands it to a callback.
/// </summary>
public class TransformInput
{
    /// <summary>
    /// Gets or sets the file's current contents.
    /// </summary>
    public string? Contents { get; set; }
}

/// <summary>
/// The methods File Transformation calls by name when it serves a registered file.
/// Returning null leaves the file unchanged.
/// </summary>
public static class TransformCallbacks
{
    /// <summary>
    /// Patches <c>index.html</c> to load the launcher.
    /// </summary>
    /// <param name="input">The served file.</param>
    /// <returns>The patched page, or null.</returns>
    public static string? Index(TransformInput input) =>
        Enabled && input.Contents is { } html ? WebPatches.PatchIndex(html) : null;

    /// <summary>
    /// Patches <c>config.json</c> to register the launcher's player.
    /// </summary>
    /// <param name="input">The served file.</param>
    /// <returns>The patched config, or null.</returns>
    public static string? Config(TransformInput input) =>
        Enabled && input.Contents is { } json ? WebPatches.PatchConfig(json) : null;

    // A registration can briefly outlive the setting while it is being removed.
    private static bool Enabled => Plugin.Instance?.Configuration.EnableWebIntegration == true;
}
