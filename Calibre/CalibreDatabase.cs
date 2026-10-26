using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// <param name="typeColumnLabel">
    /// Optional label of the Calibre custom text column to read into <see cref="CalibreBook.Type"/>
    /// (e.g. <c>type</c>). When <c>null</c>/empty the type is not read.
    /// </param>
    /// <returns>The matched book, or <c>null</c> if no row matches.</returns>
    public static CalibreBook? GetBookByRelativePath(string databasePath, string relativePath, string? typeColumnLabel = null)
    {
        using var connection = OpenReadOnly(databasePath);

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

        if (!string.IsNullOrWhiteSpace(typeColumnLabel))
        {
            book.Type = ReadCustomTextColumn(connection, typeColumnLabel!, id);
        }

        return book;
    }

    /// <summary>
    /// Returns every Calibre series together with the UUIDs of the books it contains.
    /// Used to build one Jellyfin collection per series.
    /// </summary>
    /// <param name="databasePath">Full path to <c>metadata.db</c>.</param>
    /// <returns>The list of series, each with its member book UUIDs.</returns>
    public static IReadOnlyList<CalibreSeries> GetAllSeries(string databasePath)
    {
        using var connection = OpenReadOnly(databasePath);

        var byName = new Dictionary<string, CalibreSeries>(StringComparer.Ordinal);
        var ordered = new List<CalibreSeries>();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT s.name, b.uuid
FROM books_series_link bsl
JOIN series s ON s.id = bsl.series
JOIN books b ON b.id = bsl.book
WHERE b.uuid IS NOT NULL AND b.uuid <> ''
ORDER BY s.name, b.series_index;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(0) ? null : reader.GetString(0);
            var uuid = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uuid))
            {
                continue;
            }

            if (!byName.TryGetValue(name, out var series))
            {
                series = new CalibreSeries(name);
                byName[name] = series;
                ordered.Add(series);
            }

            series.BookUuids.Add(uuid);
        }

        return ordered;
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Reads a Calibre custom text column value for a single book, resolving the column id
    /// from its <c>label</c>. Handles both "normalized" columns (value table + link table)
    /// and plain per-book value tables.
    /// </summary>
    private static string? ReadCustomTextColumn(SqliteConnection connection, string label, long bookId)
    {
        var columnId = ResolveCustomColumnId(connection, label);
        if (columnId is null)
        {
            return null;
        }

        // Column id comes from the DB (an integer), so interpolating it into the table
        // name is safe (SQLite cannot parameterize identifiers).
        var valueTable = $"custom_column_{columnId}";
        var linkTable = $"books_custom_column_{columnId}_link";

        var sql = TableExists(connection, linkTable)
            ? $"SELECT c.value FROM {linkTable} l JOIN {valueTable} c ON c.id = l.value WHERE l.book = $id LIMIT 1;"
            : $"SELECT value FROM {valueTable} WHERE book = $id LIMIT 1;";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", bookId);

        var result = cmd.ExecuteScalar();
        return result is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    private static long? ResolveCustomColumnId(SqliteConnection connection, string label)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM custom_columns WHERE label = $label LIMIT 1;";
        cmd.Parameters.AddWithValue("$label", label);

        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull)
        {
            return null;
        }

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Reads every book with the fields needed to render template paths (title, sort,
    /// authors, series, series index, publisher, year, rating, tags) plus the requested
    /// custom columns (by label, exposed as <c>#label</c>). Calibre is read-only.
    /// </summary>
    /// <param name="databasePath">Full path to <c>metadata.db</c>.</param>
    /// <param name="customColumnLabels">Custom column labels to include (without the <c>#</c>).</param>
    /// <returns>All books with their field values.</returns>
    public static IReadOnlyList<CalibreTemplateBook> GetAllBooksForTemplating(
        string databasePath,
        IEnumerable<string> customColumnLabels)
    {
        using var connection = OpenReadOnly(databasePath);

        var byId = new Dictionary<long, CalibreTemplateBook>();
        var order = new List<long>();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SELECT b.id, b.uuid, b.path, b.title, b.sort, b.pubdate, b.series_index
FROM books b
WHERE b.uuid IS NOT NULL AND b.uuid <> '' AND b.path IS NOT NULL AND b.path <> '';";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var book = new CalibreTemplateBook
                {
                    Uuid = reader.GetString(1),
                    Path = reader.GetString(2),
                };

                AddField(book, "title", reader.IsDBNull(3) ? null : reader.GetString(3));
                AddField(book, "sorttitle", reader.IsDBNull(4) ? null : reader.GetString(4));

                var date = ParseCalibreDate(reader.IsDBNull(5) ? null : reader.GetString(5));
                if (date.HasValue)
                {
                    AddField(book, "year", date.Value.Year.ToString(CultureInfo.InvariantCulture));
                }

                if (!reader.IsDBNull(6))
                {
                    AddField(book, "seriesindex", FormatIndex(reader.GetDouble(6)));
                }

                byId[id] = book;
                order.Add(id);
            }
        }

        FillMulti(connection, byId, "author", "SELECT bal.book, a.name FROM books_authors_link bal JOIN authors a ON a.id = bal.author ORDER BY bal.book, bal.id;");
        FillMulti(connection, byId, "tag", "SELECT btl.book, t.name FROM books_tags_link btl JOIN tags t ON t.id = btl.tag ORDER BY btl.book, t.name;");
        FillMulti(connection, byId, "series", "SELECT bsl.book, s.name FROM books_series_link bsl JOIN series s ON s.id = bsl.series ORDER BY bsl.book;");
        FillMulti(connection, byId, "publisher", "SELECT bpl.book, p.name FROM books_publishers_link bpl JOIN publishers p ON p.id = bpl.publisher ORDER BY bpl.book;");
        FillMulti(connection, byId, "rating", "SELECT brl.book, r.rating FROM books_ratings_link brl JOIN ratings r ON r.id = brl.rating ORDER BY brl.book;");

        foreach (var label in customColumnLabels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var columnId = ResolveCustomColumnId(connection, label);
            if (columnId is null)
            {
                continue;
            }

            var valueTable = $"custom_column_{columnId}";
            var linkTable = $"books_custom_column_{columnId}_link";
            var sql = TableExists(connection, linkTable)
                ? $"SELECT l.book, c.value FROM {linkTable} l JOIN {valueTable} c ON c.id = l.value ORDER BY l.book;"
                : $"SELECT book, value FROM {valueTable} ORDER BY book;";

            FillMulti(connection, byId, "#" + label, sql);
        }

        return order.Select(id => byId[id]).ToList();
    }

    private static void AddField(CalibreTemplateBook book, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!book.Fields.TryGetValue(key, out var list))
        {
            list = new List<string>();
            book.Fields[key] = list;
        }

        list.Add(value);
    }

    private static void FillMulti(
        SqliteConnection connection,
        IReadOnlyDictionary<long, CalibreTemplateBook> byId,
        string key,
        string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            var bookId = reader.GetInt64(0);
            if (!byId.TryGetValue(bookId, out var book))
            {
                continue;
            }

            var value = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
            AddField(book, key, value);
        }
    }

    private static string FormatIndex(double value)
    {
        return value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
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
