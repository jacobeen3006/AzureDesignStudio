using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Workspace;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// SQLite workspace (plan 6.3): offline design store semantics and schema
    /// behavior against a real (temp-file) SQLite database. No network involved.
    /// </summary>
    public class WorkspaceTests : IDisposable
    {
        private readonly string _directory;
        private readonly WorkspaceDb _workspace;

        public WorkspaceTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "ads-ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _workspace = new WorkspaceDb(Path.Combine(_directory, "workspace.db"));
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public async Task SaveLoadListDeleteRoundTrip()
        {
            IDesignOfflineStore store = new SqliteDesignOfflineStore(_workspace);

            await store.SaveAsync("prod-network", "{\"vnet\":1}");
            Assert.Equal("{\"vnet\":1}", await store.TryLoadAsync("prod-network"));
            Assert.Null(await store.TryLoadAsync("missing"));

            await store.SaveAsync("prod-network", "{\"vnet\":2}"); // same name overwrites, no duplicate row
            Assert.Equal("{\"vnet\":2}", await store.TryLoadAsync("prod-network"));
            Assert.Single(await store.ListAsync());

            await store.SaveAsync("dev", "{}");
            Assert.Equal(new[] { "dev", "prod-network" }, (await store.ListAsync()).ToArray());

            await store.DeleteAsync("dev");
            Assert.Equal(new[] { "prod-network" }, (await store.ListAsync()).ToArray());
        }

        [Fact]
        public async Task PendingLifecycleFollowsSyncContract()
        {
            IDesignOfflineStore store = new SqliteDesignOfflineStore(_workspace);

            await store.SaveAsync("draft", "{\"a\":1}");
            var pending = await store.ListPendingAsync();
            Assert.Equal(new[] { ("draft", "{\"a\":1}") }, pending.ToArray());

            await store.ClearPendingAsync("draft"); // server sync succeeded
            Assert.Empty(await store.ListPendingAsync());
            Assert.Null(await store.TryLoadAsync("draft"));

            await store.SaveAsync("draft", "{\"a\":2}"); // re-edit after sync marks pending again
            Assert.Single(await store.ListPendingAsync());
        }

        [Fact]
        public async Task ReEditKeepsSingleRowAndMarksPendingAgain()
        {
            await using (var db = _workspace.CreateContext())
            {
                db.Designs.Add(new DesignRecord { Name = "synced", Data = "old", SavedAt = DateTimeOffset.UtcNow, SyncedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }

            IDesignOfflineStore store = new SqliteDesignOfflineStore(_workspace);
            await store.SaveAsync("synced", "new");

            await using (var db = _workspace.CreateContext())
            {
                var rows = await db.Designs.Where(d => d.Name == "synced").ToListAsync();
                Assert.Single(rows);
                Assert.Equal("new", rows[0].Data);
                Assert.Null(rows[0].SyncedAt);
            }
        }

        [Fact]
        public void DuplicateDesignNamesAreRejectedByIndex()
        {
            using var db = _workspace.CreateContext();
            db.Designs.Add(new DesignRecord { Name = "twin", Data = "1", SavedAt = DateTimeOffset.UtcNow });
            db.Designs.Add(new DesignRecord { Name = "twin", Data = "2", SavedAt = DateTimeOffset.UtcNow });
            Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
        }

        [Fact]
        public void LegacyFileCacheImportsIntoWorkspaceOnce()
        {
            var legacy = Path.Combine(_directory, "designs");
            Directory.CreateDirectory(legacy);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            File.WriteAllText(Path.Combine(legacy, "a.adsdesign.json"),
                JsonSerializer.Serialize(new { Name = "alpha", Data = "{\"x\":1}", SavedAt = DateTimeOffset.UtcNow.AddDays(-3) }, options));
            File.WriteAllText(Path.Combine(legacy, "b.adsdesign.json"),
                JsonSerializer.Serialize(new { Name = "beta", Data = "{\"x\":2}", SavedAt = DateTimeOffset.UtcNow.AddDays(-2) }, options));
            File.WriteAllText(Path.Combine(legacy, "broken.adsdesign.json"), "{not json");

            var imported = LegacyDesignImporter.ImportIfNeeded(_workspace, legacy);

            Assert.Equal(2, imported);
            using (var db = _workspace.CreateContext())
            {
                var alpha = db.Designs.Single(d => d.Name == "alpha");
                Assert.Equal("{\"x\":1}", alpha.Data);
                Assert.Null(alpha.SyncedAt); // imported designs stay pending
                Assert.Equal(2, db.Designs.Count());
            }
            Assert.False(Directory.Exists(legacy)); // files consumed, directory cleaned
            Assert.Equal(0, LegacyDesignImporter.ImportIfNeeded(_workspace, legacy)); // idempotent
        }

        [Fact]
        public void FullSchemaRoundTripsAndScanDeleteCascades()
        {
            using (var db = _workspace.CreateContext())
            {
                db.Scans.Add(new ScanRecord
                {
                    Scope = "/subscriptions/sub-1",
                    ScopeType = "Subscription",
                    Status = "Completed",
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    FinishedAt = DateTimeOffset.UtcNow,
                    ContentHash = "sha256:abc",
                    ResourceCount = 2,
                    Snapshots =
                    {
                        new ResourceSnapshotRecord { ResourceId = "/vnet/a", ResourceType = "Microsoft.Network/virtualNetworks", Name = "a", Location = "westeurope", TakenAt = DateTimeOffset.UtcNow },
                        new ResourceSnapshotRecord { ResourceId = "/vnet/b", ResourceType = "Microsoft.Network/virtualNetworks", Name = "b", TakenAt = DateTimeOffset.UtcNow },
                    },
                });
                db.CostCache.Add(new CostCacheEntry { Key = "meter|westeurope|standard", Json = "[{\"retailPrice\":0.1}]", FetchedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
                db.ExecutionPlans.Add(new ExecutionPlanRecord { DesignName = "prod", Json = "{}", PlanHash = "h1", Status = "Draft", CreatedAt = DateTimeOffset.UtcNow });
                db.AuditRecords.Add(new AuditRecord { Timestamp = DateTimeOffset.UtcNow, Actor = "jakub", Action = "PlanCreate", Target = "prod", PlanHash = "h1" });
                db.SaveChanges();
            }

            using (var db = _workspace.CreateContext())
            {
                var scan = db.Scans.Include(s => s.Snapshots).Single();
                Assert.Equal(2, scan.Snapshots.Count);
                Assert.NotNull(db.CostCache.Find("meter|westeurope|standard"));
                Assert.Equal("Draft", db.ExecutionPlans.Single().Status);
                Assert.Equal("PlanCreate", db.AuditRecords.Single().Action);

                db.Scans.Remove(scan);
                db.SaveChanges();
                Assert.Empty(db.ResourceSnapshots); // cascade delete keeps history consistent
            }
        }
    }
}
