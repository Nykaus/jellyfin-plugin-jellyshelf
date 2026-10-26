using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShelf.Calibre;
using Jellyfin.Plugin.JellyShelf.Templating;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShelf.Tasks;

/// <summary>
/// Scheduled task that builds one symlink tree per configured template profile, giving a
/// dedicated book-only Jellyfin library (its own page, never mixed with films or collections)
/// whose folder hierarchy follows the template (e.g. <c>{#type}/{series?}/{title}</c>),
/// independent of the physical Calibre layout. Calibre is only read (<c>metadata.db</c> is
/// opened read-only); links live in a separate output folder and point to the original book
/// folders. Pruning never follows links, so originals are never touched.
/// </summary>
public class BuildTemplateLibrariesTask : IScheduledTask
{
    private readonly ILogger<BuildTemplateLibrariesTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="BuildTemplateLibrariesTask"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public BuildTemplateLibrariesTask(ILogger<BuildTemplateLibrariesTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyShelf — Générer le dossier miroir (liens)";

    /// <inheritdoc />
    public string Key => "JellyShelf.BuildTemplateLibraries";

    /// <inheritdoc />
    public string Description =>
        "Génère un dossier miroir de liens symboliques par profil, pour une bibliothèque Livres dédiée avec navigation par dossiers, indépendante de Calibre. Calibre est lu en lecture seule et n'est jamais modifié.";

    /// <inheritdoc />
    public string Category => "JellyShelf";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableTemplateLibraries)
        {
            _logger.LogInformation("Template libraries are disabled; nothing to do.");
            progress.Report(100);
            return Task.CompletedTask;
        }

        var calibreRoot = config.CalibreLibraryPath;
        if (string.IsNullOrWhiteSpace(calibreRoot) || !File.Exists(Path.Combine(calibreRoot, "metadata.db")))
        {
            _logger.LogWarning("Template libraries need the Calibre library path set to the folder containing metadata.db.");
            progress.Report(100);
            return Task.CompletedTask;
        }

        var outputRoot = config.TemplateLibrariesOutputRoot;
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            _logger.LogWarning("Template libraries need an output root folder (writable, mounted in the container).");
            progress.Report(100);
            return Task.CompletedTask;
        }

        if (!IsSafeOutputRoot(outputRoot, calibreRoot))
        {
            _logger.LogError(
                "Refusing to build: the output root {Output} overlaps the Calibre library {Calibre}. Choose a separate folder.",
                outputRoot,
                calibreRoot);
            progress.Report(100);
            return Task.CompletedTask;
        }

        var profiles = TemplateProfile.ParseAll(config.ProfilesDefinition);
        if (profiles.Count == 0)
        {
            _logger.LogWarning("No template profiles defined.");
            progress.Report(100);
            return Task.CompletedTask;
        }

        var dbPath = Path.Combine(calibreRoot, "metadata.db");
        var customLabels = new List<string>();
        foreach (var profile in profiles)
        {
            customLabels.AddRange(TemplateEngine.ExtractCustomColumnLabels(new[] { profile.Template }));
        }

        var books = CalibreDatabase.GetAllBooksForTemplating(dbPath, customLabels);
        _logger.LogInformation("Building {Profiles} profile(s) from {Books} books.", profiles.Count, books.Count);

        Directory.CreateDirectory(outputRoot);

        for (var p = 0; p < profiles.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var profile = profiles[p];
            var profileDir = Path.Combine(outputRoot, profile.OutputSubfolder);

            SafeDeleteTree(profileDir);
            Directory.CreateDirectory(profileDir);

            var links = 0;
            var missing = 0;

            for (var i = 0; i < books.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var book = books[i];
                var sourceFolder = Path.Combine(calibreRoot, book.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(sourceFolder))
                {
                    missing++;
                    continue;
                }

                foreach (var rel in TemplateEngine.Render(profile.Template, book))
                {
                    var linkPath = Path.Combine(profileDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    var parent = Path.GetDirectoryName(linkPath);
                    if (string.IsNullOrEmpty(parent))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(parent);

                    // Resolve name collisions (two books with the same type/series/title) so no
                    // book is silently dropped: append " (2)", " (3)", ...
                    var finalPath = linkPath;
                    var suffix = 2;
                    while ((Directory.Exists(finalPath) || File.Exists(finalPath)) && suffix <= 100)
                    {
                        finalPath = linkPath + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
                        suffix++;
                    }

                    if (Directory.Exists(finalPath) || File.Exists(finalPath))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.CreateSymbolicLink(finalPath, sourceFolder);
                        links++;

                        // Give each intermediate folder (type, series, …) a representative
                        // thumbnail: link a folder.jpg to this book's cover (first one wins).
                        SetFolderImages(profileDir, finalPath, Path.Combine(sourceFolder, "cover.jpg"));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not create link {Link} -> {Target}", finalPath, sourceFolder);
                    }
                }

                if (books.Count > 0)
                {
                    progress.Report(((double)p + ((double)(i + 1) / books.Count)) / profiles.Count * 100);
                }
            }

            _logger.LogInformation(
                "Profile '{Name}' ({Subfolder}): {Links} links created, {Missing} source folders missing.",
                profile.Name,
                profile.OutputSubfolder,
                links,
                missing);
        }

        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        // No default trigger: run manually, or add a schedule in Jellyfin's Scheduled Tasks UI.
        => Array.Empty<TaskTriggerInfo>();

    /// <summary>
    /// Guards against writing into — or deleting — the Calibre library: the output root must
    /// not equal, contain, or be contained by the Calibre root.
    /// </summary>
    private static bool IsSafeOutputRoot(string outputRoot, string calibreRoot)
    {
        var outFull = AppendSeparator(Path.GetFullPath(outputRoot));
        var calFull = AppendSeparator(Path.GetFullPath(calibreRoot));

        if (string.Equals(outFull, calFull, StringComparison.Ordinal))
        {
            return false;
        }

        return !outFull.StartsWith(calFull, StringComparison.Ordinal)
            && !calFull.StartsWith(outFull, StringComparison.Ordinal);
    }

    private static string AppendSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>
    /// Sets a <c>folder.jpg</c> (symlink to the given cover) on every intermediate directory
    /// between the book leaf and the profile root, so type/series folders show a representative
    /// thumbnail. The first processed book that has a cover wins for each directory.
    /// </summary>
    private void SetFolderImages(string profileDir, string leafPath, string coverPath)
    {
        if (!File.Exists(coverPath))
        {
            return;
        }

        var rootFull = AppendSeparator(Path.GetFullPath(profileDir));
        var dir = Path.GetDirectoryName(leafPath);

        while (!string.IsNullOrEmpty(dir))
        {
            var dirFull = AppendSeparator(Path.GetFullPath(dir));
            if (!dirFull.StartsWith(rootFull, StringComparison.Ordinal) || string.Equals(dirFull, rootFull, StringComparison.Ordinal))
            {
                break; // reached the profile root (or above): don't image the library root itself
            }

            var image = Path.Combine(dir, "folder.jpg");
            if (!File.Exists(image))
            {
                try
                {
                    File.CreateSymbolicLink(image, coverPath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not set folder image at {Dir}", dir);
                }
            }

            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>
    /// Recursively deletes a generated tree WITHOUT ever following symlinks: a reparse-point
    /// entry is unlinked (never descended into), so link targets (the Calibre originals) are
    /// never affected.
    /// </summary>
    private static void SafeDeleteTree(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var attr = File.GetAttributes(entry);

            if ((attr & FileAttributes.ReparsePoint) != 0)
            {
                // Symlink (to a dir or file): remove the link only.
                if ((attr & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            else if ((attr & FileAttributes.Directory) != 0)
            {
                SafeDeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(dir);
    }
}
