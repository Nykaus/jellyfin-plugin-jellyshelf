using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyShelf.Templating;

/// <summary>
/// A named "view" that generates one symlink tree from a path template, e.g.
/// <c>Par type | par_type | {#type}/{series?}/{title}</c>.
/// </summary>
public class TemplateProfile
{
    /// <summary>Initializes a new instance of the <see cref="TemplateProfile"/> class.</summary>
    /// <param name="name">Display name.</param>
    /// <param name="outputSubfolder">Output subfolder under the template libraries root.</param>
    /// <param name="template">The path template.</param>
    public TemplateProfile(string name, string outputSubfolder, string template)
    {
        Name = name;
        OutputSubfolder = outputSubfolder;
        Template = template;
    }

    /// <summary>Gets the profile display name.</summary>
    public string Name { get; }

    /// <summary>Gets the output subfolder (relative to the template libraries root).</summary>
    public string OutputSubfolder { get; }

    /// <summary>Gets the path template.</summary>
    public string Template { get; }

    /// <summary>
    /// Parses a multi-line definition. Each non-empty, non-comment line is either
    /// <c>Name | template</c> or <c>Name | subfolder | template</c>. Lines starting with
    /// <c>#</c> are comments.
    /// </summary>
    /// <param name="definition">The raw profiles definition text.</param>
    /// <returns>The parsed profiles.</returns>
    public static IReadOnlyList<TemplateProfile> ParseAll(string? definition)
    {
        var profiles = new List<TemplateProfile>();
        if (string.IsNullOrWhiteSpace(definition))
        {
            return profiles;
        }

        var lines = definition.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split('|');
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }

            string name;
            string subfolder;
            string template;

            if (parts.Length == 2)
            {
                name = parts[0];
                template = parts[1];
                subfolder = PathSanitizer.Sanitize(name);
            }
            else if (parts.Length >= 3)
            {
                name = parts[0];
                subfolder = PathSanitizer.Sanitize(parts[1]);
                template = parts[2];
            }
            else
            {
                continue;
            }

            if (name.Length == 0 || template.Length == 0 || subfolder.Length == 0)
            {
                continue;
            }

            profiles.Add(new TemplateProfile(name, subfolder, template));
        }

        return profiles;
    }
}
