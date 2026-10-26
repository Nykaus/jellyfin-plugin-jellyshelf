using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyShelf.Calibre;

/// <summary>
/// A lightweight projection of a Calibre book used to render template paths for the
/// symlink-tree ("template libraries") feature. <see cref="Fields"/> maps a lowercase
/// field key (e.g. <c>title</c>, <c>series</c>, <c>#type</c>) to its value(s).
/// </summary>
public class CalibreTemplateBook
{
    /// <summary>Gets or sets the Calibre UUID.</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the book folder path relative to the Calibre library root,
    /// as stored in <c>books.path</c> (e.g. <c>Jane Austen/Pride and Prejudice (123)</c>).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets the field values keyed by lowercase field name. Single-valued fields hold one
    /// entry; multi-valued fields (tags, authors, multi custom columns) hold several.
    /// </summary>
    public Dictionary<string, List<string>> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
}
