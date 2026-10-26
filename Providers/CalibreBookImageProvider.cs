using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Calibre.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Calibre.Providers;

/// <summary>
/// Supplies the primary cover image for a book from the <c>cover.jpg</c> that
/// Calibre stores inside each book folder.
/// </summary>
public class CalibreBookImageProvider : ILocalImageProvider, IHasOrder
{
    /// <inheritdoc />
    public string Name => "Calibre";

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Book;

    /// <inheritdoc />
    public IEnumerable<LocalImageInfo> GetImages(BaseItem item, IDirectoryService directoryService)
    {
        var folder = item.ContainingFolderPath;
        if (string.IsNullOrEmpty(folder))
        {
            yield break;
        }

        var coverPath = Path.Combine(folder, "cover.jpg");
        var file = directoryService.GetFile(coverPath);
        if (file is not null && file.Exists)
        {
            yield return new LocalImageInfo
            {
                FileInfo = file,
                Type = ImageType.Primary,
            };
        }
    }
}
