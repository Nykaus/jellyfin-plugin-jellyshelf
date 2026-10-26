using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Calibre.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Calibre;

/// <summary>
/// The Calibre Library plugin. Reads a Calibre <c>metadata.db</c> (read-only) and
/// enriches Jellyfin book items with genres, tags, authors, series and covers
/// without moving the original Calibre files.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Calibre Library";

    /// <inheritdoc />
    public override string Description =>
        "Reads a Calibre metadata.db (read-only) and populates genres, tags, authors, series, titles and covers for the book library.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("e7f3b8d2-4c6a-4f1e-9b3a-2d5c7e9a1f40");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace),
        };
    }
}
