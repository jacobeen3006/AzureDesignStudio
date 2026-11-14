using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AzureDesignStudio.Server.Tests.Persistence;

/// <summary>
/// Describes a SQL Server database's logical schema as one stable string so two databases can be
/// compared. Column order is ignored on purpose (a column added by ALTER lands last while a fresh
/// table has model order), and the suffixes SQL Server generates for unnamed constraints are
/// normalised away.
/// </summary>
public static class SqlServerSchemaDescriber
{
    public const string SystemName = "<system-name>";

    private static readonly Regex GeneratedName = new(@"^(PK|DF)__.+__[0-9A-F]{8}$", RegexOptions.Compiled);

    public static string Normalise(string name) => GeneratedName.IsMatch(name) ? SystemName : name;

    public static string Describe(string connectionString)
    {
        var lines = new List<string>();
        lines.AddRange(Columns(connectionString));
        lines.AddRange(PrimaryKeys(connectionString));
        lines.AddRange(Indexes(connectionString));
        lines.AddRange(Defaults(connectionString));
        lines.AddRange(Checks(connectionString));
        lines.AddRange(ForeignKeys(connectionString));
        return string.Join('\n', lines);
    }

    /// <summary>Default constraints as <c>table.column = definition (name)</c>, names normalised.</summary>
    public static IReadOnlyList<string> Defaults(string connectionString) =>
        Query(connectionString, """
            SELECT t.name, c.name, d.definition, d.name
            FROM sys.default_constraints d
            JOIN sys.tables t ON t.object_id = d.parent_object_id
            JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
            WHERE t.name <> N'__EFMigrationsHistory'
            """,
            r => $"default {r.GetString(0)}.{r.GetString(1)} = {r.GetString(2)} [{Normalise(r.GetString(3))}]");

    private static IReadOnlyList<string> Columns(string connectionString) =>
        Query(connectionString, """
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME <> N'__EFMigrationsHistory'
            """,
            r => $"column {r.GetString(0)}.{r.GetString(1)} {r.GetString(2)}({(r.IsDBNull(3) ? "-" : r.GetInt32(3).ToString())}) null={r.GetString(4)}");

    private static IReadOnlyList<string> PrimaryKeys(string connectionString) =>
        Query(connectionString, """
            SELECT t.name, i.name,
                   STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 1 AND t.name <> N'__EFMigrationsHistory'
            GROUP BY t.name, i.name
            """,
            r => $"pk {r.GetString(0)} ({r.GetString(2)}) [{Normalise(r.GetString(1))}]");

    private static IReadOnlyList<string> Indexes(string connectionString) =>
        Query(connectionString, """
            SELECT t.name, i.name, i.is_unique,
                   STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 0 AND i.type > 0 AND t.name <> N'__EFMigrationsHistory'
            GROUP BY t.name, i.name, i.is_unique
            """,
            r => $"index {r.GetString(0)}.{r.GetString(1)} unique={r.GetBoolean(2)} ({r.GetString(3)})");

    private static IReadOnlyList<string> Checks(string connectionString) =>
        Query(connectionString, """
            SELECT t.name, k.name, k.definition
            FROM sys.check_constraints k
            JOIN sys.tables t ON t.object_id = k.parent_object_id
            WHERE t.name <> N'__EFMigrationsHistory'
            """,
            r => $"check {r.GetString(0)}.{r.GetString(1)} {r.GetString(2)}");

    private static IReadOnlyList<string> ForeignKeys(string connectionString) =>
        Query(connectionString, """
            SELECT p.name, f.name, r.name
            FROM sys.foreign_keys f
            JOIN sys.tables p ON p.object_id = f.parent_object_id
            JOIN sys.tables r ON r.object_id = f.referenced_object_id
            WHERE p.name <> N'__EFMigrationsHistory'
            """,
            r => $"fk {r.GetString(0)}.{r.GetString(1)} -> {r.GetString(2)}");

    private static List<string> Query(string connectionString, string sql, Func<SqlDataReader, string> row)
    {
        var rows = new List<string>();
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(row(reader));
        // Ordinal order keeps the description independent of the server collation.
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }
}
