using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Mcp;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// The tracer for the connection discipline: the writer and the MCP reader come from one factory, there is no
    /// shared cache, the pragmas hold on a pooled second connection, and a cold workspace opens read-only.
    /// </summary>
    public class WorkspaceConnectionTests : IDisposable
    {
        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        [Fact]
        public async Task PooledConnectionsCarryThePragmas_AndAColdWorkspaceOpensReadOnly()
        {
            var path = _temp.Path("workspace.db");
            var db = new WorkspaceDb(path);
            await using (var ctx = db.CreateContext())
            {
                ctx.Designs.Add(new DesignRecord { Name = "Seeded", Data = "{}", SavedAt = DateTimeOffset.UtcNow });
                await ctx.SaveChangesAsync();
            }

            Assert.DoesNotContain("Cache", WorkspaceConnection.WriterConnectionString(path), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cache", db.ConnectionString, StringComparison.OrdinalIgnoreCase);

            // The first context above is closed, so this connection comes from the pool.
            await using (var ctx = db.CreateContext())
            {
                await ctx.Database.OpenConnectionAsync();
                Assert.Equal("wal", await Pragma(ctx, "journal_mode"));
                Assert.Equal("5000", await Pragma(ctx, "busy_timeout"));
                Assert.Equal("1", await Pragma(ctx, "foreign_keys"));
                Assert.Equal("2", await Pragma(ctx, "synchronous"));   // FULL: durability is the point of this phase
            }

            // Cold: nothing left in the log, no -wal/-shm beside the file.
            await using (var ctx = db.CreateContext())
                await ctx.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)");
            SqliteConnection.ClearAllPools();
            foreach (var sidecar in new[] { path + "-wal", path + "-shm" })
                if (File.Exists(sidecar)) File.Delete(sidecar);

            var workspace = new AdsWorkspace(path);
            Assert.Equal(WorkspaceOpenMode.ReadOnly, workspace.OpenMode);
            var designs = await workspace.ListDesignsAsync();
            Assert.Equal("Seeded", Assert.Single(designs).Name);

            var reader = WorkspaceConnection.OpenReader(path);
            Assert.DoesNotContain("Cache", reader.ConnectionString, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> Pragma(DbContext ctx, string name)
        {
            await using var command = ctx.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA " + name;
            return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }
}
