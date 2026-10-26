using System;
using System.Text;

namespace Jellyfin.Plugin.JellyShelf.Templating;

/// <summary>
/// Turns arbitrary field values into safe single path segments (no directory separators,
/// no characters illegal on Windows/Linux, bounded length).
/// </summary>
public static class PathSanitizer
{
    private const int MaxSegmentLength = 120;

    /// <summary>
    /// Sanitizes a single path segment. Returns <c>"_"</c> if the result would be empty.
    /// </summary>
    /// <param name="value">The raw value.</param>
    /// <returns>A safe path segment.</returns>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "_";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c))
            {
                sb.Append('-');
            }
            else
            {
                sb.Append(c);
            }
        }

        // Windows forbids trailing dots/spaces on a path component.
        var result = sb.ToString().Trim().TrimEnd('.', ' ').Trim();

        if (result.Length > MaxSegmentLength)
        {
            result = result.Substring(0, MaxSegmentLength).TrimEnd('.', ' ').Trim();
        }

        return result.Length == 0 ? "_" : result;
    }
}
