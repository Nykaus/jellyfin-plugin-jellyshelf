using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyShelf.Calibre;

/// <summary>
/// Read-only accessor for a Calibre <c>metadata.db</c>.
/// </summary>
public static class CalibreDatabase
{
    /// <summary>
    /// Looks up a single book by its folder path relative to the library root,
    /// which is exactly what Calibre stores in <c>books.path</c>
    /// (e.g. <c>Jane Austen/Pride and Prejudice (123)</c>).
    /// </summary>
    /// <param name="databasePath">Full path to <c>metadata.db</c>.</param>
    /// <param name="relativePath">Book folder path relative to the library root, using forward slashes.</param>
    /// <returns>The matched book, or <c>null</c> if no row matches.</returns>
    public static CalibreBook? GetBookByRelativePath(string databasePath, string relativePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        };

        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        var book = new CalibreBook();
        long id;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SELECT b.id, b.title, b.sort, b.pubdate, b.series_index, b.uuid, b.has_cover,
       (SELECT c.text FROM comments c WHERE c.book = b.id) AS comment,
       (SELECT s.name FROM books_series_link bsl JOIN series s ON s.id = bsl.series WHERE bsl.book = b.id LIMIT 1) AS series,
       (SELECT p.name FROM books_publishers_link bpl JOIN publishers p ON p.id = bpl.publisher WHERE bpl.book = b.id LIMIT 1) AS publisher,
       (SELECT r.rating FROM books_ratings_link brl JOIN ratings r ON r.id = brl.rating WHERE brl.book = b.id LIMIT 1) AS rating
FROM books b
WHERE b.path = $path
LIMIT 1;";
            cmd.Parameters.AddWithValue("$path", relativePath);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            id = reader.GetInt64(0);
            book.Id = id;
            book.Title = reader.IsDBNull(1) ? null : reader.GetString(1);
            book.SortTitle = reader.IsDBNull(2) ? null : reader.GetString(2);
            book.PublishDate = ParseCalibreDate(reader.IsDBNull(3) ? null : reader.GetString(3));
            book.SeriesIndex = reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4);
            book.Uuid = reader.IsDBNull(5) ? null : reader.GetString(5);
            book.HasCover = !reader.IsDBNull(6) && reader.GetInt64(6) != 0;
            book.Comment = reader.IsDBNull(7) ? null : reader.GetString(7);
            book.Series = reader.IsDBNull(8) ? null : reader.GetString(8);
            book.Publisher = reader.IsDBNull(9) ? null : reader.GetString(9);
            book.Rating = reader.IsDBNull(10) ? (double?)null : reader.GetDouble(10);
        }

        QueryInto(
            connection,
            "SELECT a.name FROM books_authors_link bal JOIN authors a ON a.id = bal.author WHERE bal.book = $id ORDER BY bal.id;",
            id,
            book.Authors);

        QueryInto(
            connection,
            "SELECT t.name FROM books_tags_link btl JOIN tags t ON t.id = btl.tag WHERE btl.book = $id ORDER BY t.name;",
            id,
            book.Tags);

        QueryInto(
            connection,
            "SELECT l.lang_code FROM books_languages_link bll JOIN languages l ON l.id = bll.lang_code WHERE bll.book = $id ORDER BY bll.item_order;",
            id,
            book.Languages);

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT type, val FROM identifiers WHERE book = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.IsDBNull(0) ? null : reader.GetString(0);
                var val = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(val))
                {
                    book.Identifiers[type] = val;
                }
            }
        }

        return book;
    }

    private static void QueryInto(SqliteConnection connection, string sql, long id, List<string> target)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                var value = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    target.Add(value);
                }
            }
        }
    }

    private static DateTime? ParseCalibreDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            // Calibre uses a sentinel like 0101-01-01 for "undefined".
            if (dto.Year < 1400)
            {
                return null;
            }

            return dto.UtcDateTime;
        }

        return null;
    }
}
