using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AzureDesignStudio.Core.Tests.Support
{
    /// <summary>
    /// Describes the LOGICAL schema of a SQLite file: columns, the indexes a CREATE INDEX made,
    /// and foreign keys, all read through the pragma table-valued functions. It never reads the
    /// text in sqlite_master, because a hand-written unnamed primary key and the model's
    /// PK_&lt;Table&gt; differ only in that text; every parity test of the durability phase
    /// (a legacy file and a fresh file end with the same schema) compares through this class.
    /// Migration bookkeeping tables and SQLite's own tables are ignored.
    /// </summary>
    public static class SqliteSchemaDescriber
    {
        private const string UserTables =
            "SELECT name FROM sqlite_master WHERE type = 'table' " +
            "AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' " +
            "AND name NOT LIKE '\\_\\_EFMigrations%' ESCAPE '\\' " +
            "ORDER BY name";

        /// <summary>The whole schema as one stable string (tables ordered by name).</summary>
        public static string Describe(string path)
        {
            using var connection = Open(path);
            var sb = new StringBuilder();
            foreach (var table in Names(connection, UserTables))
                sb.Append(DescribeTable(connection, table));
            return sb.ToString();
        }

        /// <summary>One table's description, or an empty string when the table does not exist.</summary>
        public static string DescribeTable(string path, string table)
        {
            using var connection = Open(path);
            return Names(connection, UserTables).Contains(table, StringComparer.Ordinal)
                ? DescribeTable(connection, table)
                : string.Empty;
        }

        /// <summary>Names of the user tables, sorted ordinally.</summary>
        public static IReadOnlyList<string> TableNames(string path)
        {
            using var connection = Open(path);
            return Names(connection, UserTables);
        }

        /// <summary>Column names of a table in column order (empty when the table is missing).</summary>
        public static IReadOnlyList<string> ColumnNames(string path, string table)
        {
            using var connection = Open(path);
            return Names(connection, "SELECT name FROM pragma_table_info($t) ORDER BY cid", table);
        }

        /// <summary>
        /// Indexes created by CREATE INDEX on the table as name plus column list, flagged when
        /// unique, for example <c>unique IX_Designs_Name(Name)</c>. Sorted by name.
        /// </summary>
        public static IReadOnlyList<string> CreatedIndexes(string path, string table)
        {
            using var connection = Open(path);
            return CreatedIndexes(connection, table).Select(i => i.Line).ToList();
        }

        private static string DescribeTable(SqliteConnection connection, string table)
        {
            var sb = new StringBuilder();
            sb.Append("TABLE ").Append(table).Append('\n');

            using (var columns = connection.CreateCommand())
            {
                columns.CommandText =
                    "SELECT cid, name, type, \"notnull\", dflt_value, pk FROM pragma_table_info($t) ORDER BY cid";
                columns.Parameters.AddWithValue("$t", table);
                using var reader = columns.ExecuteReader();
                while (reader.Read())
                {
                    sb.Append("  COLUMN ")
                      .Append(Text(reader, 0)).Append('|').Append(Text(reader, 1)).Append('|')
                      .Append(Text(reader, 2)).Append("|notnull=").Append(Text(reader, 3))
                      .Append("|default=").Append(reader.IsDBNull(4) ? "<none>" : Text(reader, 4))
                      .Append("|pk=").Append(Text(reader, 5)).Append('\n');
                }
            }

            foreach (var index in CreatedIndexes(connection, table))
                sb.Append("  INDEX ").Append(index.Line).Append(index.Partial ? " partial" : string.Empty).Append('\n');

            var foreignKeys = new List<string>();
            using (var fks = connection.CreateCommand())
            {
                fks.CommandText =
                    "SELECT \"table\", \"from\", \"to\", on_delete FROM pragma_foreign_key_list($t)";
                fks.Parameters.AddWithValue("$t", table);
                using var reader = fks.ExecuteReader();
                while (reader.Read())
                    foreignKeys.Add($"{Text(reader, 0)}|{Text(reader, 1)}->{Text(reader, 2)}|on_delete={Text(reader, 3)}");
            }
            foreach (var fk in foreignKeys.OrderBy(f => f, StringComparer.Ordinal))
                sb.Append("  FK ").Append(fk).Append('\n');

            return sb.ToString();
        }

        private static List<(string Line, bool Partial)> CreatedIndexes(SqliteConnection connection, string table)
        {
            var indexes = new List<(string Name, bool Unique, bool Partial)>();
            using (var list = connection.CreateCommand())
            {
                list.CommandText =
                    "SELECT name, \"unique\", partial FROM pragma_index_list($t) WHERE origin = 'c' ORDER BY name";
                list.Parameters.AddWithValue("$t", table);
                using var reader = list.ExecuteReader();
                while (reader.Read())
                    indexes.Add((reader.GetString(0), reader.GetInt64(1) == 1, reader.GetInt64(2) == 1));
            }

            var result = new List<(string, bool)>();
            foreach (var (name, unique, partial) in indexes)
            {
                var columns = Names(connection, "SELECT name FROM pragma_index_info($t) ORDER BY seqno", name);
                var line = (unique ? "unique " : string.Empty) + name + "(" + string.Join(",", columns) + ")";
                result.Add((line, partial));
            }
            return result;
        }

        private static List<string> Names(SqliteConnection connection, string sql, string? parameter = null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (parameter != null) command.Parameters.AddWithValue("$t", parameter);
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        }

        private static string Text(SqliteDataReader reader, int ordinal) =>
            Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

        // Read-only and unpooled: describing a file must neither change it nor keep it locked.
        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }
    }
}
