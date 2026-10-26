using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
/// Scheduled task that creates/updates Jellyfin collections from Calibre field variables.
/// Each definition line (<c>Prefix | {field}</c>) produces one collection per distinct value
/// of that field (named <c>Prefix + value</c>), containing the matching books. Everything is
/// done through the Jellyfin API using data read from <c>metadata.db</c> (read-only); nothing
/// is ever written to disk and Calibre is never modified. Books are matched to Jellyfin items
/// via the <c>calibre</c> provider id, so a library scan must have run first.
/// </summary>
public class BuildCollectionsTask : IScheduledTask
{
    private const string CalibreProviderKey = "calibre";
    private const int MaxWalkUpLevels = 6;

    private static readonly Regex FieldRegex = new(@"\{(#?[A-Za-z0-9_]+)[?*]*\}", RegexOptions.Compiled);

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<BuildCollectionsTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="BuildCollectionsTask"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="logger">The logger.</param>
    public BuildCollectionsTask(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<BuildCollectionsTask> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyShelf — Construire les collections";

    /// <inheritdoc />
    public string Key => "JellyShelf.BuildCollections";

    /// <inheritdoc />
    public string Description =>
        "Crée/met à jour des collections Jellyfin par variable Calibre (ex. Type — | {#type}, Série — | {series}). Nécessite qu'une analyse de la bibliothèque ait déjà été effectuée.";

    /// <inheritdoc />
    public string Category => "JellyShelf";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableCollections)
        {
            _logger.LogInformation("Collections are disabled; nothing to do.");
            progress.Report(100);
            return;
        }

        var definitions = ParseDefinitions(config.CollectionDefinitions);
        if (definitions.Count == 0)
        {
            _logger.LogWarning("No collection definitions configured.");
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

        var customLabels = definitions
            .Where(d => d.Field.StartsWith('#') && d.Field.Length > 1)
            .Select(d => d.Field.Substring(1));

        var books = CalibreDatabase.GetAllBooksForTemplating(dbPath, customLabels);
        _logger.LogInformation("Building {Count} collection definition(s) from {Books} books.", definitions.Count, books.Count);

        var totalCreated = 0;
        var totalUpdated = 0;

        for (var d = 0; d < definitions.Count; d++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = definitions[d];

            // Group book UUIDs by the field value.
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var book in books)
            {
                if (!book.Fields.TryGetValue(definition.Field, out var values))
                {
                    continue;
                }

                foreach (var value in values.Distinct(StringComparer.Ordinal))
                {
                    if (!groups.TryGetValue(value, out var list))
                    {
                        list = new List<string>();
                        groups[value] = list;
                    }

                    list.Add(book.Uuid);
                }
            }

            var gi = 0;
            foreach (var (value, uuids) in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                gi++;

                var itemIds = ResolveJellyfinItemIds(uuids);
                if (itemIds.Count == 0)
                {
                    continue;
                }

                var name = definition.Prefix + value;
                var existing = FindCollectionByName(name);

                if (existing is null)
                {
                    await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                    {
                        Name = name,
                        ItemIdList = itemIds.Select(id => id.ToString("N")).ToList(),
                    }).ConfigureAwait(false);
                    totalCreated++;
                }
                else
                {
                    await _collectionManager.AddToCollectionAsync(existing.Id, itemIds).ConfigureAwait(false);
                    totalUpdated++;
                }

                progress.Report((d + ((double)gi / Math.Max(groups.Count, 1))) / definitions.Count * 100);
            }

            _logger.LogInformation(
                "Definition '{Prefix}{Field}': {Groups} values processed.",
                definition.Prefix,
                definition.Field,
                groups.Count);
        }

        _logger.LogInformation("Collections done: {Created} created, {Updated} updated.", totalCreated, totalUpdated);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        // No default trigger: run manually, or add a schedule in Jellyfin's Scheduled Tasks UI.
        => Array.Empty<TaskTriggerInfo>();

    private static IReadOnlyList<CollectionDefinition> ParseDefinitions(string? text)
    {
        var result = new List<CollectionDefinition>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' && !line.Contains('{', StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length < 2)
            {
                continue;
            }

            var prefix = parts[0].Trim();
            var match = FieldRegex.Match(parts[1]);
            if (!match.Success)
            {
                continue;
            }

            result.Add(new CollectionDefinition(prefix.Length == 0 ? string.Empty : prefix + " ", match.Groups[1].Value));
        }

        return result;
    }

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

    private sealed record CollectionDefinition(string Prefix, string Field);
}
