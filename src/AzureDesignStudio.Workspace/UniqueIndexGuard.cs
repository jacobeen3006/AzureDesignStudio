using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// One unique index the model requires. Every member is a compile-time constant of this assembly:
/// the guard quotes the identifiers into its SQL, so a spec must never be built from user input.
/// </summary>
/// <param name="Table">The table the index is on.</param>
/// <param name="IndexName">The name the baseline gives the unique index.</param>
/// <param name="KeyColumns">The indexed columns.</param>
/// <param name="KeepOrderBy">
/// An ORDER BY expression list; within a group of rows sharing a key, the FIRST row by this order is kept
/// and the rest are removed. End it with <c>rowid DESC</c> so a tie is still deterministic.
/// </param>
public sealed record UniqueIndexSpec(string Table, string IndexName, IReadOnlyList<string> KeyColumns, string KeepOrderBy);

/// <summary>What the guard did for one spec that actually changed something.</summary>
/// <param name="Table">The table whose duplicates were removed.</param>
/// <param name="IndexName">The unique index those duplicates would have violated.</param>
/// <param name="DuplicateGroups">How many keys were held by more than one row.</param>
/// <param name="RowsRemoved">How many rows were deleted (all but one per group).</param>
/// <param name="ReportPath">The jsonl file that holds every removed row.</param>
public sealed record UniqueIndexRepair(string Table, string IndexName, int DuplicateGroups, int RowsRemoved, string ReportPath);

/// <summary>
/// Pre-flight of the first migration: rows that violate a unique index the model requires would make the
/// baseline's <c>CREATE UNIQUE INDEX</c> fail, so the most recently updated row per key is kept, the others are
/// written to a report and deleted. Table-driven: a later phase that adds a unique index adds a spec row.
/// The deletion is one way; the premigration backup, which must exist before this runs, is the undo.
/// </summary>
public static class UniqueIndexGuard
{
    /// <summary>The unique indexes of the shipped model.</summary>
    public static IReadOnlyList<UniqueIndexSpec> Specs { get; } =
    [
        new("Designs", "IX_Designs_Name", ["Name"], "SavedAt DESC, rowid DESC"),
        new("ChangeFeed", "IX_ChangeFeed_TargetResourceId_ChangeKey", ["TargetResourceId", "ChangeKey"], "ChangeTime DESC, rowid DESC"),
    ];

    private const string ReportPrefix = "upgrade-removed-rows-";

    /// <summary>
    /// Removes the duplicate-key rows of every spec whose table exists and whose unique index is not in place yet.
    /// </summary>
    /// <param name="connection">An open read-write connection to the workspace database.</param>
    /// <param name="specs">The unique indexes to enforce.</param>
    /// <param name="reportDirectory">Where the removed-rows report is written (created when missing).</param>
    /// <param name="nowUtc">Stamps the report file name.</param>
    /// <param name="requiredBackupPath">A backup that must already exist; nothing is touched without it.</param>
    /// <returns>The repairs that changed something (empty when the data was already clean).</returns>
    /// <exception cref="ArgumentException">The backup path is empty or the file does not exist.</exception>
    public static IReadOnlyList<UniqueIndexRepair> Repair(
        SqliteConnection connection, IReadOnlyList<UniqueIndexSpec> specs, string reportDirectory,
        DateTimeOffset nowUtc, string requiredBackupPath)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        if (string.IsNullOrWhiteSpace(requiredBackupPath) || !File.Exists(requiredBackupPath))
            throw new ArgumentException(
                "Rows are never removed without an existing backup: the backup path is empty or the file does not exist.",
                nameof(requiredBackupPath));

        var work = new List<SpecWork>();
        foreach (var spec in specs)
        {
            if (!TableExists(connection, spec.Table)) continue;
            if (HasUniqueIndex(connection, spec)) continue;

            var removals = FindRemovals(connection, spec);
            if (removals.Count == 0) continue;
            work.Add(new SpecWork(spec, removals));
        }

        if (work.Count == 0) return [];

        // The report is complete and on disk before the first row is deleted.
        var reportPath = Path.Combine(
            Path.GetFullPath(reportDirectory),
            ReportPrefix + nowUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".jsonl");
        WriteReport(reportPath, work);

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var item in work)
            {
                // The baseline recreates the index as unique with IF NOT EXISTS, which a same-named plain index would defeat.
                if (IndexExists(connection, transaction, item.Spec))
                    Execute(connection, transaction, $"DROP INDEX {Quote(item.Spec.IndexName)}");

                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {Quote(item.Spec.Table)} WHERE rowid = $rowid";
                var parameter = delete.Parameters.Add("$rowid", SqliteType.Integer);
                foreach (var removal in item.Removals)
                {
                    parameter.Value = removal.RemovedRowId;
                    delete.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        return work
            .Select(w => new UniqueIndexRepair(
                w.Spec.Table, w.Spec.IndexName, w.Removals.Select(r => r.KeptRowId).Distinct().Count(),
                w.Removals.Count, reportPath))
            .ToList();
    }

    private sealed record Removal(long KeptRowId, long RemovedRowId, IReadOnlyList<KeyValuePair<string, object?>> Row);

    private sealed record SpecWork(UniqueIndexSpec Spec, IReadOnlyList<Removal> Removals);

    private static List<Removal> FindRemovals(SqliteConnection connection, UniqueIndexSpec spec)
    {
        var keys = string.Join(", ", spec.KeyColumns.Select(Quote));
        // A unique index permits any number of NULL keys, so a row with a NULL key column is never a duplicate.
        var notNull = string.Join(" AND ", spec.KeyColumns.Select(c => Quote(c) + " IS NOT NULL"));

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT * FROM (" +
            "SELECT rowid AS \"_ads_rowid\", " +
            $"FIRST_VALUE(rowid) OVER w AS \"_ads_kept\", ROW_NUMBER() OVER w AS \"_ads_rn\", * FROM {Quote(spec.Table)} " +
            $"WHERE {notNull} " +
            $"WINDOW w AS (PARTITION BY {keys} ORDER BY {spec.KeepOrderBy})" +
            ") WHERE \"_ads_rn\" > 1 ORDER BY \"_ads_rowid\"";

        var removals = new List<Removal>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new List<KeyValuePair<string, object?>>();
            for (var i = 3; i < reader.FieldCount; i++)
                row.Add(new(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i)));
            removals.Add(new Removal(reader.GetInt64(1), reader.GetInt64(0), row));
        }

        return removals;
    }

    private static void WriteReport(string reportPath, List<SpecWork> work)
    {
        var directory = Path.GetDirectoryName(reportPath)!;
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(reportPath, options);
        foreach (var item in work)
        {
            foreach (var removal in item.Removals)
            {
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer))
                {
                    json.WriteStartObject();
                    json.WriteString("table", item.Spec.Table);
                    json.WriteString("index", item.Spec.IndexName);
                    json.WriteStartArray("keyColumns");
                    foreach (var column in item.Spec.KeyColumns) json.WriteStringValue(column);
                    json.WriteEndArray();
                    json.WriteNumber("keptRowId", removal.KeptRowId);
                    json.WriteNumber("removedRowId", removal.RemovedRowId);
                    json.WriteStartObject("row");
                    foreach (var (name, value) in removal.Row)
                    {
                        json.WritePropertyName(name);
                        WriteValue(json, value);
                    }

                    json.WriteEndObject();
                    json.WriteEndObject();
                }

                stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                stream.WriteByte((byte)'\n');
            }
        }

        stream.Flush(flushToDisk: true);
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null: json.WriteNullValue(); break;
            case long l: json.WriteNumberValue(l); break;
            case double d when double.IsFinite(d): json.WriteNumberValue(d); break;
            case string s: json.WriteStringValue(s); break;
            case byte[] bytes: json.WriteStringValue(Convert.ToBase64String(bytes)); break;
            default: json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static bool TableExists(SqliteConnection connection, string table) =>
        Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info($name)", table) > 0;

    private static bool HasUniqueIndex(SqliteConnection connection, UniqueIndexSpec spec) =>
        Scalar(connection, null, "SELECT COUNT(*) FROM pragma_index_list($table) WHERE name = $name AND \"unique\" = 1",
            spec.IndexName, spec.Table) > 0;

    private static bool IndexExists(SqliteConnection connection, SqliteTransaction transaction, UniqueIndexSpec spec) =>
        Scalar(connection, transaction, "SELECT COUNT(*) FROM pragma_index_list($table) WHERE name = $name",
            spec.IndexName, spec.Table) > 0;

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, string name, string? table = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (table is null)
        {
            command.Parameters.AddWithValue("$name", name);
        }
        else
        {
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$table", table);
        }

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
