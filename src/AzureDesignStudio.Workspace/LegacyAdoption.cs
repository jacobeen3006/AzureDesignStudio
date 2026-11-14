using Microsoft.Data.Sqlite;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// The steps a workspace that predates migrations needs before the guarded baseline can adopt it.
/// SQLite has no conditional <c>ADD COLUMN</c>, so the baseline's <c>CREATE TABLE IF NOT EXISTS</c>
/// leaves an old <c>Scans</c> table without its later columns; this adds them first.
/// </summary>
internal static class LegacyAdoption
{
    /// <summary>
    /// Appends <c>ManifestJson</c> and <c>CheckpointJson</c> to <c>Scans</c> when missing. Appending
    /// matches the column order a fresh table has. Every name is a constant of this method.
    /// </summary>
    public static void AddMissingScanColumns(SqliteConnection connection)
    {
        foreach (var (table, column) in new[] { ("Scans", "ManifestJson"), ("Scans", "CheckpointJson") })
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
            check.Parameters.AddWithValue("$table", table);
            check.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt64(check.ExecuteScalar()) != 0) continue;

            using var add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT;";
            add.ExecuteNonQuery();
        }
    }

    /// <summary>Whether a table of that name exists (bound parameter, no string building).</summary>
    public static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
}
