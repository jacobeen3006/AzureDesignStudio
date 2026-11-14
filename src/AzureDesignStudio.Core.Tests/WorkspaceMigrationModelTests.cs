using System.Linq;
using System.Text.RegularExpressions;
using AzureDesignStudio.Workspace;
using AzureDesignStudio.Workspace.Migrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Guards the workspace migration set against the model: a forgotten migration after a model
    /// edit fails here instead of crashing the app at launch (EF 9+ throws on pending model
    /// changes). No database file is opened.
    /// </summary>
    public class WorkspaceMigrationModelTests
    {
        private static WorkspaceDbContext NewContext() => new WorkspaceDbContextFactory().CreateDbContext([]);

        [Fact]
        public void PendingModelChanges_AreNone()
        {
            using var db = NewContext();

            Assert.False(db.Database.HasPendingModelChanges());
        }

        [Fact]
        public void BaselineIsTheFirstMigrationAndCoversEveryModelTable()
        {
            using var db = NewContext();

            Assert.EndsWith("_Baseline", db.Database.GetMigrations().First());
            foreach (var table in db.Model.GetEntityTypes().Select(e => e.GetTableName()))
                Assert.Contains("\"" + table + "\"", Baseline.GuardedDdl);
        }

        [Fact]
        public void EveryBaselineStatementIsGuarded()
        {
            var create = new Regex(@"CREATE\s+(UNIQUE\s+)?(TABLE|INDEX)\s+(?!IF NOT EXISTS)", RegexOptions.IgnoreCase);

            Assert.DoesNotMatch(create, Baseline.GuardedDdl);
            Assert.Matches(new Regex(@"CREATE\s+TABLE\s+IF NOT EXISTS"), Baseline.GuardedDdl);
            Assert.Matches(new Regex(@"CREATE\s+UNIQUE\s+INDEX\s+IF NOT EXISTS"), Baseline.GuardedDdl);
        }
    }
}
