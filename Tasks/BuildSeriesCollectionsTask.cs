using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyShelf.Calibre;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShelf.Tasks;

/// <summary>
/// Scheduled task that creates (or updates) one Jellyfin collection (BoxSet) per Calibre
/// series, so series can be browsed as navigable boxes regardless of the folder layout.
/// Books are matched to Jellyfin items via the <c>calibre</c> provider id (the Calibre UUID)
/// set by <see cref="Providers.CalibreBookMetadataProvider"/> — so a library scan must have
/// run first.
/// </summary>
public class BuildSeriesCollectionsTask : IScheduledTask
{
    private const string CalibreProviderKey = "calibre";
    private const int MaxWalkUpLevels = 6;

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<BuildSeriesCollectionsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BuildSeriesCollectionsTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="logger">The logger.</param>
    public BuildSeriesCollectionsTask(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<BuildSeriesCollectionsTask> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyShelf — Construire les collections de séries";

    /// <inheritdoc />
    public string Key => "JellyShelf.BuildSeriesCollections";

    /// <inheritdoc />
    public string Description =>
        "Crée ou met à jour une collection Jellyfin par série Calibre. Nécessite qu'une analyse de la bibliothèque ait déjà été effectuée.";

    /// <inheritdoc />
    public string Category => "JellyShelf";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.CreateSeriesCollections)
        {
            _logger.LogInformation("Series collections are disabled; nothing to do.");
            progress.Report(100);
            return;
        }

        var dbPath = ResolveDatabasePath(config.CalibreLibraryPath);
        if (dbPath is null)
        {
            _logger.LogWarning(
                "Could not locate metadata.db. Set the Calibre library path in the JellyShelf settings, then run a library scan before this task.");
            progress.Report(100);
            return;
        }

        var seriesList = CalibreDatabase.GetAllSeries(dbPath);
        if (seriesList.Count == 0)
        {
            _logger.LogInformation("No Calibre series found in {DbPath}.", dbPath);
            progress.Report(100);
            return;
        }

        var created = 0;
        var updated = 0;

        for (var i = 0; i < seriesList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var series = seriesList[i];
            var itemIds = ResolveJellyfinItemIds(series.BookUuids);
            if (itemIds.Count == 0)
            {
                _logger.LogDebug("No matched Jellyfin items for series {Series}; skipped.", series.Name);
            }
            else
            {
                var name = config.SeriesCollectionPrefix + series.Name;
                var existing = FindCollectionByName(name);

                if (existing is null)
                {
                    await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                    {
                        Name = name,
                        ItemIdList = itemIds.Select(id => id.ToString("N")).ToList(),
                    }).ConfigureAwait(false);
                    created++;
                }
                else
                {
                    await _collectionManager.AddToCollectionAsync(existing.Id, itemIds).ConfigureAwait(false);
                    updated++;
                }
            }

            progress.Report((double)(i + 1) / seriesList.Count * 100);
        }

        _logger.LogInformation(
            "Series collections done: {Created} created, {Updated} updated, out of {Total} series.",
            created,
            updated,
            seriesList.Count);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        // No default trigger: run manually from Dashboard → Scheduled Tasks, or add your own trigger.
        => Array.Empty<TaskTriggerInfo>();

    private IReadOnlyList<Guid> ResolveJellyfinItemIds(IEnumerable<string> uuids)
    {
        var ids = new List<Guid>();
        foreach (var uuid in uuids)
        {
            var matches = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Book },
                HasAnyProviderId = new Dictionary<string, string> { [CalibreProviderKey] = uuid },
            });

            foreach (var item in matches)
            {
                if (!ids.Contains(item.Id))
                {
                    ids.Add(item.Id);
                }
            }
        }

        return ids;
    }

    private BoxSet? FindCollectionByName(string name)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Name = name,
        }).OfType<BoxSet>().FirstOrDefault();
    }

    /// <summary>
    /// Locates <c>metadata.db</c>: uses the configured library path when it holds one,
    /// otherwise walks up from a sample of Book items in the library.
    /// </summary>
    private string? ResolveDatabasePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var candidate = Path.Combine(configuredPath, "metadata.db");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var sample = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Book },
            Limit = 50,
        });

        foreach (var item in sample)
        {
            var folder = item.ContainingFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                continue;
            }

            var current = new DirectoryInfo(folder);
            for (var level = 0; level < MaxWalkUpLevels && current is not null; level++)
            {
                var candidate = Path.Combine(current.FullName, "metadata.db");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }
}
