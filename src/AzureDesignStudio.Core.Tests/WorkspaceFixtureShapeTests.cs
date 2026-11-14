using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Pins the four legacy workspace fixtures to what the shipped WorkspaceDb produces today.
    /// Later plans of the durability phase upgrade these files and compare the result with a
    /// fresh file; these tests are the anchor that the fixtures really are the legacy shapes.
    /// </summary>
    public class WorkspaceFixtureShapeTests : IDisposable
    {
        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        public static IEnumerable<object[]> AllFixtures() => WorkspaceFixtures.All.Select(n => new object[] { n });

        private string Loaded(string fixtureName)
        {
            var path = _temp.Path(fixtureName + ".db");
            WorkspaceFixtures.Load(fixtureName, path);
            return path;
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void EveryFixtureLoadsAndHasTheExpectedTables(string fixtureName)
        {
            var path = Loaded(fixtureName);

            Assert.Equal(WorkspaceFixtures.ExpectedCounts(fixtureName).Keys, SqliteSchemaDescriber.TableNames(path));
            Assert.DoesNotContain("__EFMigrationsHistory", SqliteSchemaDescriber.TableNames(path));
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void EveryTableIsSeededWithTheExpectedRowCount(string fixtureName)
        {
            var counts = WorkspaceFixtures.TableCounts(Loaded(fixtureName));

            Assert.Equal(WorkspaceFixtures.ExpectedCounts(fixtureName), counts);
            Assert.All(counts, pair => Assert.True(pair.Value >= 2, $"{pair.Key} holds {pair.Value} rows"));
        }

        [Fact]
        public void CurrentLegacyFixtureHasTheLogicalSchemaOfAFileTheShippedCodeCreates()
        {
            var fresh = _temp.Path("fresh.db");
            _ = new WorkspaceDb(fresh);
            SqliteConnection.ClearAllPools();

            var legacy = Loaded(WorkspaceFixtures.L3CurrentLegacy);

            Assert.Equal(SqliteSchemaDescriber.Describe(fresh), SqliteSchemaDescriber.Describe(legacy));
        }

        [Fact]
        public void PreGovernanceFixtureLacksOnlyTheGovernanceTable()
        {
            var l2 = Loaded(WorkspaceFixtures.L2PreGovernance);
            var l3 = Loaded(WorkspaceFixtures.L3CurrentLegacy);

            var missing = SqliteSchemaDescriber.TableNames(l3).Except(SqliteSchemaDescriber.TableNames(l2)).ToList();
            Assert.Equal(["GovernanceFindings"], missing);
            Assert.Empty(SqliteSchemaDescriber.CreatedIndexes(l2, "GovernanceFindings"));
            Assert.NotEmpty(SqliteSchemaDescriber.CreatedIndexes(l3, "GovernanceFindings"));
            foreach (var table in SqliteSchemaDescriber.TableNames(l2))
                Assert.Equal(SqliteSchemaDescriber.DescribeTable(l3, table), SqliteSchemaDescriber.DescribeTable(l2, table));
        }

        [Fact]
        public void OldestFixtureLacksTheScanColumnsAndTheAdditiveTables()
        {
            var l1 = Loaded(WorkspaceFixtures.L1Oldest);
            var l3 = Loaded(WorkspaceFixtures.L3CurrentLegacy);

            var scanColumns = SqliteSchemaDescriber.ColumnNames(l1, "Scans");
            Assert.DoesNotContain("ManifestJson", scanColumns);
            Assert.DoesNotContain("CheckpointJson", scanColumns);
            Assert.Equal(["ManifestJson", "CheckpointJson"],
                SqliteSchemaDescriber.ColumnNames(l3, "Scans").Except(scanColumns));

            var tables = SqliteSchemaDescriber.TableNames(l1);
            Assert.Equal(6, tables.Count);
            foreach (var additive in new[] { "NodePositions", "ChangeFeed", "DecomWindows", "GovernanceFindings" })
                Assert.DoesNotContain(additive, tables);
            foreach (var table in tables.Where(t => t != "Scans"))
                Assert.Equal(SqliteSchemaDescriber.DescribeTable(l3, table), SqliteSchemaDescriber.DescribeTable(l1, table));
        }

        [Fact]
        public void DuplicatesFixtureHasNoUniqueIndexesAndHoldsDuplicateKeysIncludingATie()
        {
            var l3 = Loaded(WorkspaceFixtures.L3CurrentLegacy);
            var l4 = Loaded(WorkspaceFixtures.L4Duplicates);

            Assert.Contains("unique IX_Designs_Name(Name)", SqliteSchemaDescriber.CreatedIndexes(l3, "Designs"));
            Assert.DoesNotContain(SqliteSchemaDescriber.CreatedIndexes(l4, "Designs"), i => i.StartsWith("unique ", StringComparison.Ordinal));
            Assert.DoesNotContain(SqliteSchemaDescriber.CreatedIndexes(l4, "ChangeFeed"), i => i.StartsWith("unique ", StringComparison.Ordinal));
            Assert.Equal(
                SqliteSchemaDescriber.TableNames(l3),
                SqliteSchemaDescriber.TableNames(l4));

            Assert.Equal(1, WorkspaceFixtures.Scalar(l4,
                "SELECT COUNT(*) FROM (SELECT Name FROM Designs GROUP BY Name HAVING COUNT(*) > 1)"));
            Assert.Equal(2, WorkspaceFixtures.Scalar(l4,
                "SELECT COUNT(*) FROM (SELECT 1 FROM ChangeFeed GROUP BY TargetResourceId, ChangeKey HAVING COUNT(*) > 1)"));
            Assert.Equal(1, WorkspaceFixtures.Scalar(l4,
                "SELECT COUNT(*) FROM (SELECT 1 FROM ChangeFeed GROUP BY TargetResourceId, ChangeKey " +
                "HAVING COUNT(*) > 1 AND COUNT(DISTINCT ChangeTime) = 1)"));
        }

        [Fact]
        public void LoaderRefusesATargetOutsideTheTempDirectory()
        {
            var temp = Path.GetFullPath(Path.GetTempPath());
            var outside = Path.Combine(temp, "..", "ads-fx-outside-" + Guid.NewGuid().ToString("N") + ".db");
            var sibling = temp.TrimEnd(Path.DirectorySeparatorChar) + "-sibling" + Path.DirectorySeparatorChar + "ads.db";

            foreach (var target in new[] { outside, sibling, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "workspace.db") })
            {
                Assert.Throws<InvalidOperationException>(() => WorkspaceFixtures.Load(WorkspaceFixtures.L3CurrentLegacy, target));
                Assert.False(File.Exists(Path.GetFullPath(target)));
            }
        }
    }
}
