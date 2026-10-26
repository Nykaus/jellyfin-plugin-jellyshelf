using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyShelf.Configuration;

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
    /// Gets or sets the label of the Calibre custom column used as the Jellyfin genre
    /// (first navigation level, e.g. Romans / Mangas / BD). Matches the custom column's
    /// <c>label</c> in Calibre (the <c>#label</c> without the leading <c>#</c>).
    /// </summary>
    public string TypeColumnLabel { get; set; } = "type";

    /// <summary>
    /// Gets or sets a value indicating whether the Calibre custom "type" column
    /// (<see cref="TypeColumnLabel"/>) is mapped to the Jellyfin genre.
    /// </summary>
    public bool MapTypeColumnToGenre { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Calibre tags are mapped to
    /// Jellyfin genres. Off by default: with <see cref="MapTypeColumnToGenre"/> on,
    /// the genre comes from the type column and tags would otherwise pollute it.
    /// </summary>
    public bool MapTagsToGenres { get; set; }

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

    /// <summary>
    /// Gets or sets a value indicating whether the "Build series collections" scheduled
    /// task is allowed to create/update a Jellyfin collection (BoxSet) per Calibre series.
    /// </summary>
    public bool CreateSeriesCollections { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional prefix prepended to every series collection name
    /// (e.g. <c>"Série — "</c>) so they are easy to distinguish. Empty by default.
    /// </summary>
    public string SeriesCollectionPrefix { get; set; } = string.Empty;
}
