using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyShelf.Calibre;

namespace Jellyfin.Plugin.JellyShelf.Templating;

/// <summary>
/// Renders relative symlink paths from a template and a <see cref="CalibreTemplateBook"/>.
/// Tokens: <c>{field}</c>, optional <c>{field?}</c> (segment dropped when empty),
/// multi-value <c>{field*}</c> (one path per value). Fields are case-insensitive and
/// include <c>title</c>, <c>sorttitle</c>, <c>author</c>, <c>series</c>, <c>seriesindex</c>,
/// <c>publisher</c>, <c>year</c>, <c>rating</c>, <c>tag</c>, and any custom column <c>#label</c>.
/// </summary>
public static class TemplateEngine
{
    private const int MaxVariantsPerBook = 50;
    private const string UnknownSegment = "_Inconnu";

    private static readonly Regex TokenRegex = new(@"\{(#?[A-Za-z0-9_]+)([?*]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Returns the set of custom column labels (without the leading <c>#</c>) referenced by
    /// the given templates, so the database read can fetch exactly those columns.
    /// </summary>
    /// <param name="templates">Template strings.</param>
    /// <returns>The distinct custom column labels.</returns>
    public static IReadOnlyCollection<string> ExtractCustomColumnLabels(IEnumerable<string> templates)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            foreach (Match m in TokenRegex.Matches(template ?? string.Empty))
            {
                var field = m.Groups[1].Value;
                if (field.StartsWith('#') && field.Length > 1)
                {
                    labels.Add(field.Substring(1));
                }
            }
        }

        return labels;
    }

    /// <summary>
    /// Renders the relative paths (forward-slash separated) for a book. Usually one path;
    /// more than one only when a <c>{field*}</c> token expands a multi-valued field.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <param name="book">The book.</param>
    /// <returns>The relative paths.</returns>
    public static IReadOnlyList<string> Render(string template, CalibreTemplateBook book)
    {
        var segments = template.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        var variants = new List<List<string>> { new() };

        foreach (var segment in segments)
        {
            var rendered = RenderSegment(segment, book);
            if (rendered is null)
            {
                // Optional token empty -> drop this whole segment.
                continue;
            }

            var next = new List<List<string>>();
            foreach (var variant in variants)
            {
                foreach (var piece in rendered)
                {
                    if (next.Count >= MaxVariantsPerBook)
                    {
                        break;
                    }

                    var copy = new List<string>(variant) { piece };
                    next.Add(copy);
                }
            }

            variants = next;
        }

        return variants
            .Where(v => v.Count > 0)
            .Select(v => string.Join('/', v))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Renders one template segment to one or more strings, or <c>null</c> when an optional
    /// token is empty (meaning the segment should be skipped).
    /// </summary>
    private static List<string>? RenderSegment(string segment, CalibreTemplateBook book)
    {
        // Each "part" contributes a list of alternative strings; the segment is the cartesian
        // product of its parts. Literals contribute a single-element list.
        var partAlternatives = new List<List<string>>();
        var lastIndex = 0;

        foreach (Match m in TokenRegex.Matches(segment))
        {
            if (m.Index > lastIndex)
            {
                partAlternatives.Add(new List<string> { segment.Substring(lastIndex, m.Index - lastIndex) });
            }

            var field = m.Groups[1].Value;
            var flags = m.Groups[2].Value;
            var optional = flags.Contains('?', StringComparison.Ordinal);
            var expand = flags.Contains('*', StringComparison.Ordinal);

            var values = book.Fields.TryGetValue(field, out var list) ? list : null;

            if (values is null || values.Count == 0)
            {
                if (optional)
                {
                    return null;
                }

                partAlternatives.Add(new List<string> { UnknownSegment });
            }
            else
            {
                var chosen = expand ? values : new List<string> { values[0] };
                partAlternatives.Add(chosen.Select(PathSanitizer.Sanitize).ToList());
            }

            lastIndex = m.Index + m.Length;
        }

        if (lastIndex < segment.Length)
        {
            partAlternatives.Add(new List<string> { segment.Substring(lastIndex) });
        }

        if (partAlternatives.Count == 0)
        {
            return new List<string>();
        }

        // Cartesian product of the parts.
        IEnumerable<string> combos = new[] { string.Empty };
        foreach (var alternatives in partAlternatives)
        {
            combos = combos.SelectMany(prefix => alternatives.Select(a => prefix + a));
        }

        return combos
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(PathSanitizer.Sanitize)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
