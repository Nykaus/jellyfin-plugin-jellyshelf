using System;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyShelf.Storage;

/// <summary>
/// A tiny per-user key/value store for the reader (reading preferences and the
/// last position in each book), kept in a SQLite file so it follows the Jellyfin
/// account across devices. Values are opaque JSON written by the reader's
/// front-end; the store never interprets them.
///
/// <para>Microsoft.Data.Sqlite and its native library are provided by the
/// Jellyfin host (it uses them for its own database), so nothing is bundled.</para>
/// </summary>
public sealed class ReaderStateStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReaderStateStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">The host application paths (for the data directory).</param>
    public ReaderStateStore(IApplicationPaths applicationPaths)
    {
        // DataPath persists across plugin updates (unlike the versioned plugin
        // folder), so the reader's state survives upgrades.
        var dir = Path.Combine(applicationPaths.DataPath, "jellyshelf");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "reader-state.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>
    /// Gets the stored JSON for a user's key, or <see langword="null"/> if none.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="key">The state key.</param>
    /// <returns>The stored JSON, or null.</returns>
    public string? Get(Guid userId, string key)
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM reader_state WHERE user_id = $u AND key = $k;";
            command.Parameters.AddWithValue("$u", userId.ToString("N", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteScalar() as string;
        }
    }

    /// <summary>
    /// Stores (or replaces) the JSON for a user's key.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="key">The state key.</param>
    /// <param name="data">The JSON to store.</param>
    public void Set(Guid userId, string key, string data)
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO reader_state (user_id, key, data, updated_at) VALUES ($u, $k, $d, $t) "
                + "ON CONFLICT(user_id, key) DO UPDATE SET data = $d, updated_at = $t;";
            command.Parameters.AddWithValue("$u", userId.ToString("N", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$d", data);
            command.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS reader_state ("
            + "user_id TEXT NOT NULL, key TEXT NOT NULL, data TEXT NOT NULL, updated_at TEXT NOT NULL, "
            + "PRIMARY KEY (user_id, key));";
        command.ExecuteNonQuery();
        _initialized = true;
    }
}
