using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyShelf.Calibre;

/// <summary>
/// A Calibre series together with the UUIDs of the books that belong to it,
/// in series-index order.
/// </summary>
public class CalibreSeries
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CalibreSeries"/> class.
    /// </summary>
    /// <param name="name">The series name.</param>
    public CalibreSeries(string name)
    {
        Name = name;
    }

    /// <summary>Gets the series name.</summary>
    public string Name { get; }

    /// <summary>Gets the member book UUIDs, in series-index order.</summary>
    public List<string> BookUuids { get; } = new();
}
