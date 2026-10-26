using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyShelf.Calibre;
using Jellyfin.Plugin.JellyShelf.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShelf.Providers;

/// <summary>
/// Local metadata provider that enriches <see cref="Book"/> items from a Calibre
/// <c>metadata.db</c>. The database is opened read-only and the original files are
/// never moved or modified.
/// </summary>
public class CalibreBookMetadataProvider : ILocalMetadataProvider<Book>, IHasOrder
{
    private const int MaxWalkUpLevels = 6;

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    private readonly ILogger<CalibreBookMetadataProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CalibreBookMetadataProvider"/> class.
    /// </summary>
    public CalibreBookMetadataProvider(ILogger<CalibreBookMetadataProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Calibre";

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public Task<MetadataResult<Book>> GetMetadata(
        ItemInfo info,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Book>();

        try
        {
            var folder = info.ContainingFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                folder = Path.GetDirectoryName(info.Path);
            }

            if (string.IsNullOrEmpty(folder))
            {
                return Task.FromResult(result);
            }

            // When the library scans a generated symlink tree (template libraries), the book
            // folder is a symlink to the real Calibre folder; resolve it so the books.path
            // match below works against the real Calibre layout.
            folder = ResolveRealFolder(folder);

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            var root = ResolveLibraryRoot(folder, config.CalibreLibraryPath);
            if (root is null)
            {
                _logger.LogDebug("No Calibre metadata.db found for {Folder}", folder);
                return Task.FromResult(result);
            }

            var dbPath = Path.Combine(root, "metadata.db");
            var relative = Path.GetRelativePath(root, folder)
                .Replace('\\', '/')
                .TrimEnd('/');

            var typeLabel = config.MapTypeColumnToGenre ? config.TypeColumnLabel : null;
            var book = CalibreDatabase.GetBookByRelativePath(dbPath, relative, typeLabel);
            if (book is null)
            {
                _logger.LogDebug("No Calibre row matched path {RelativePath}", relative);
                return Task.FromResult(result);
            }

            result.Item = BuildBook(book, config);
            result.HasMetadata = true;

            foreach (var author in book.Authors)
            {
                result.AddPerson(new PersonInfo
                {
                    Name = author,
                    Type = PersonKind.Author,
                });
            }

            _logger.LogDebug("Matched Calibre book #{Id} ({Title})", book.Id, book.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read Calibre metadata for {Path}", info.Path);
        }

        return Task.FromResult(result);
    }

    private static Book BuildBook(CalibreBook source, PluginConfiguration config)
    {
        var book = new Book
        {
            Name = source.Title,
            ForcedSortName = source.SortTitle,
            Overview = BuildOverview(source.Comment, config.StripHtmlFromOverview),
            PremiereDate = source.PublishDate,
            ProductionYear = source.PublishDate?.Year,
        };

        if (config.ImportSeries && !string.IsNullOrEmpty(source.Series))
        {
            book.SeriesName = source.Series;
            if (source.SeriesIndex.HasValue)
            {
                book.IndexNumber = (int)Math.Round(source.SeriesIndex.Value, MidpointRounding.AwayFromZero);
            }
        }

        var genres = new List<string>();
        if (config.MapTypeColumnToGenre && !string.IsNullOrWhiteSpace(source.Type))
        {
            genres.Add(source.Type!);
        }

        if (config.MapTagsToGenres)
        {
            genres.AddRange(source.Tags);
        }

        if (genres.Count > 0)
        {
            book.Genres = genres.ToArray();
        }

        if (config.KeepTags)
        {
            book.Tags = source.Tags.ToArray();
        }

        if (config.ImportPublisher && !string.IsNullOrEmpty(source.Publisher))
        {
            book.Studios = new[] { source.Publisher };
        }

        if (config.ImportRating && source.Rating.HasValue)
        {
            // Calibre stores 0-10 (10 == 5 stars); Jellyfin community rating is also 0-10.
            book.CommunityRating = (float)source.Rating.Value;
        }

        if (!string.IsNullOrEmpty(source.Uuid))
        {
            book.SetProviderId("calibre", source.Uuid);
        }

        if (source.Identifiers.TryGetValue("isbn", out var isbn) && !string.IsNullOrEmpty(isbn))
        {
            book.SetProviderId("ISBN", isbn);
        }

        if (source.Identifiers.TryGetValue("amazon", out var asin) && !string.IsNullOrEmpty(asin))
        {
            book.SetProviderId("Amazon", asin);
        }

        if (source.Identifiers.TryGetValue("google", out var google) && !string.IsNullOrEmpty(google))
        {
            book.SetProviderId("GoogleBooks", google);
        }

        return book;
    }

    /// <summary>
    /// Resolves a folder to its real path when it is a symlink (as produced by the template
    /// libraries feature); otherwise returns it unchanged.
    /// </summary>
    private static string ResolveRealFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = Directory.ResolveLinkTarget(folder, returnFinalTarget: true);
                if (target is not null)
                {
                    return target.FullName;
                }
            }
        }
        catch (Exception)
        {
            // Fall back to the original path on any IO/permission error.
        }

        return folder;
    }

    private static string? BuildOverview(string? comment, bool stripHtml)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        if (!stripHtml)
        {
            return comment;
        }

        var text = HtmlTagRegex.Replace(comment, string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }

    /// <summary>
    /// Resolves the Calibre library root. Uses the configured path when it contains
    /// a <c>metadata.db</c>; otherwise walks up from the book folder.
    /// </summary>
    private static string? ResolveLibraryRoot(string bookFolder, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)
            && File.Exists(Path.Combine(configuredPath, "metadata.db")))
        {
            return configuredPath;
        }

        var current = new DirectoryInfo(bookFolder);
        for (var level = 0; level < MaxWalkUpLevels && current is not null; level++)
        {
            if (File.Exists(Path.Combine(current.FullName, "metadata.db")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
