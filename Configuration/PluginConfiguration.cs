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
    /// Gets or sets a value indicating whether the "mirror folder" scheduled task may generate
    /// symlink trees from <see cref="ProfilesDefinition"/>. This gives a dedicated book-only
    /// library (its own page), never mixed with films or collections. Off by default.
    /// </summary>
    public bool EnableTemplateLibraries { get; set; }

    /// <summary>
    /// Gets or sets the writable root folder (as seen by the Jellyfin container) under which
    /// the symlink trees are generated. Must be a separate folder from the Calibre library.
    /// </summary>
    public string TemplateLibrariesOutputRoot { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the mirror-folder profiles, one per line, formatted as
    /// <c>Name | subfolder | template</c> (or <c>Name | template</c>). Template tokens:
    /// <c>{field}</c>, optional <c>{field?}</c>, multi-value <c>{field*}</c>; fields include
    /// title, sorttitle, author, series, seriesindex, publisher, year, rating, tag and any
    /// custom column <c>#label</c>. Lines starting with <c>#</c> are comments.
    /// </summary>
    public string ProfilesDefinition { get; set; } =
        "Par type | par_type | {#type}/{series?}/{title}";

    /// <summary>
    /// Gets or sets a value indicating whether the Play/Read button in jellyfin-web
    /// opens the JellyShelf reader for supported books (PDF and comics). Requires the
    /// File Transformation plugin; when it is absent the reader still works via its
    /// own URL (<c>/JellyShelf/?id=...</c>). On by default.
    /// </summary>
    public bool EnableWebIntegration { get; set; } = true;
}
