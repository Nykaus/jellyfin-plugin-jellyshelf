using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Calibre.Calibre;

/// <summary>
/// A single book as read from a Calibre <c>metadata.db</c>.
/// </summary>
public class CalibreBook
{
    /// <summary>Gets or sets the Calibre numeric id.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the Calibre sort title.</summary>
    public string? SortTitle { get; set; }

    /// <summary>Gets or sets the publish date.</summary>
    public DateTime? PublishDate { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    public string? Series { get; set; }

    /// <summary>Gets or sets the position within the series.</summary>
    public double? SeriesIndex { get; set; }

    /// <summary>Gets or sets the publisher.</summary>
    public string? Publisher { get; set; }

    /// <summary>Gets or sets the Calibre UUID.</summary>
    public string? Uuid { get; set; }

    /// <summary>Gets or sets the HTML comment (synopsis).</summary>
    public string? Comment { get; set; }

    /// <summary>Gets or sets the rating on a 0-10 scale.</summary>
    public double? Rating { get; set; }

    /// <summary>Gets or sets a value indicating whether a cover exists.</summary>
    public bool HasCover { get; set; }

    /// <summary>Gets the authors in Calibre order.</summary>
    public List<string> Authors { get; } = new();

    /// <summary>Gets the tags.</summary>
    public List<string> Tags { get; } = new();

    /// <summary>Gets the language codes.</summary>
    public List<string> Languages { get; } = new();

    /// <summary>Gets the identifiers keyed by type (e.g. isbn, amazon, google).</summary>
    public Dictionary<string, string> Identifiers { get; } = new(StringComparer.OrdinalIgnoreCase);
}
