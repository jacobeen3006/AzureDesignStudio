using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace AzureDesignStudio.Core.Tests.Support
{
    /// <summary>
    /// The legacy workspace.db fixtures of the durability phase (plain SQL scripts under
    /// Fixtures/, copied next to the test assembly) and the helpers that load and measure them.
    /// Nothing here can write outside the system temp directory, so no test can touch a
    /// developer's real workspace.db or deploy.db.
    /// </summary>
    public static class WorkspaceFixtures
    {
        /// <summary>Oldest shape: six model tables, Scans without the two later columns.</summary>
        public const string L1Oldest = "workspace-legacy-L1-oldest";
        /// <summary>Everything the shipped code created except the governance findings table.</summary>
        public const string L2PreGovernance = "workspace-legacy-L2-pre-governance";
        /// <summary>The file as the shipped code leaves it today, without any migration history.</summary>
        public const string L3CurrentLegacy = "workspace-legacy-L3-current-legacy";
        /// <summary>L3 without the two unique indexes, holding duplicate keys.</summary>
        public const string L4Duplicates = "workspace-legacy-L4-duplicates";

        public static IReadOnlyList<string> All { get; } = [L1Oldest, L2PreGovernance, L3CurrentLegacy, L4Duplicates];

        /// <summary>
        /// Executes Fixtures/&lt;fixtureName&gt;.sql into a new database file at
        /// <paramref name="targetPath"/>. The file is left in delete journal mode on purpose, so a
        /// later test can prove the upgrade also converts it to WAL.
        /// </summary>
        /// <exception cref="InvalidOperationException">The target is not under the temp directory.</exception>
        public static void Load(string fixtureName, string targetPath)
        {
            var full = Path.GetFullPath(targetPath);
            if (!IsUnderTemp(full))
                throw new InvalidOperationException(
                    $"Refusing to write a fixture database outside the temp directory: {full}");

            var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName + ".sql"));
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = script;
            command.ExecuteNonQuery();
        }

        /// <summary>Table name to row count for every table except SQLite's own and migration bookkeeping.</summary>
        public static SortedDictionary<string, long> TableCounts(string path)
        {
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in SqliteSchemaDescriber.TableNames(path))
                counts[table] = Scalar(path, $"SELECT COUNT(*) FROM \"{table}\"");
            return counts;
        }

        /// <summary>The seeded row count per table of a fixture, as the scripts insert them.</summary>
        public static SortedDictionary<string, long> ExpectedCounts(string fixtureName)
        {
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                ["AuditRecords"] = 3,
                ["CostCache"] = 2,
                ["Designs"] = 3,
                ["ExecutionPlans"] = 2,
                ["ResourceSnapshots"] = 4,
                ["Scans"] = 2,
            };
            if (fixtureName == L1Oldest) return counts;

            counts["ChangeFeed"] = fixtureName == L4Duplicates ? 5 : 3;
            counts["DecomWindows"] = 2;
            counts["NodePositions"] = 2;
            if (fixtureName != L2PreGovernance) counts["GovernanceFindings"] = 3;
            return counts;
        }

        /// <summary>First column of the first row of <paramref name="sql"/> as a number.</summary>
        public static long Scalar(string path, string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static bool IsUnderTemp(string fullPath)
        {
            var root = Path.GetFullPath(Path.GetTempPath());
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullPath.StartsWith(root, comparison);
        }
    }

    /// <summary>A scratch directory under the system temp path, deleted on dispose.</summary>
    public sealed class TempWorkspaceDirectory : IDisposable
    {
        private readonly string _directory;

        public TempWorkspaceDirectory()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ads-fx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        /// <summary>The directory itself.</summary>
        public string Root => _directory;

        /// <summary>A file path inside the directory.</summary>
        public string Path(string name) => System.IO.Path.Combine(_directory, name);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }
}
