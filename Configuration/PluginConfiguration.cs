using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Calibre.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets an optional explicit path to the Calibre library root
    /// (the folder that contains <c>metadata.db</c>). When empty, the plugin
    /// walks up from each book folder to locate <c>metadata.db</c> automatically.
    /// </summary>
    public string CalibreLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether Calibre tags are mapped to
    /// Jellyfin genres (Calibre has no dedicated genre field; genres are
    /// conventionally stored as tags).
    /// </summary>
    public bool MapTagsToGenres { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Calibre tags are also kept as
    /// Jellyfin tags (in addition to being mapped to genres).
    /// </summary>
    public bool KeepTags { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the series name / index is imported.
    /// </summary>
    public bool ImportSeries { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the publisher is imported as a studio.
    /// </summary>
    public bool ImportPublisher { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Calibre rating (0-10) is imported
    /// as the community rating.
    /// </summary>
    public bool ImportRating { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether HTML is stripped from the Calibre
    /// comment when used as the overview.
    /// </summary>
    public bool StripHtmlFromOverview { get; set; } = true;
}
