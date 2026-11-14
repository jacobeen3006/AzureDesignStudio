using System;
using System.Collections.Generic;
using System.Globalization;
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

        // The importer used to delete each file as it went and commit once at the end, so one
        // bad entry (two files with the same design name, or no data) threw at the commit -
        // after every file was gone - and all pending designs were lost.
        [Fact]
        public void LegacyImportKeepsEveryGoodDesignWhenOneEntryCannotBeStored()
        {
            var legacy = Path.Combine(_directory, "designs");
            Directory.CreateDirectory(legacy);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            void Write(string file, object entry) =>
                File.WriteAllText(Path.Combine(legacy, file), JsonSerializer.Serialize(entry, options));

            Write("a.adsdesign.json", new { Name = "alpha", Data = "{\"x\":1}", SavedAt = DateTimeOffset.UtcNow });
            Write("b.adsdesign.json", new { Name = "alpha", Data = "{\"x\":2}", SavedAt = DateTimeOffset.UtcNow }); // same name
            Write("c.adsdesign.json", new { Name = "no-data", SavedAt = DateTimeOffset.UtcNow });                   // Data missing
            Write("d.adsdesign.json", new { Name = "delta", Data = "{\"x\":4}", SavedAt = DateTimeOffset.UtcNow });

            var imported = LegacyDesignImporter.ImportIfNeeded(_workspace, legacy);

            Assert.Equal(2, imported);
            using var db = _workspace.CreateContext();
            Assert.Equal(new[] { "alpha", "delta" }, db.Designs.Select(d => d.Name).OrderBy(n => n).ToArray());
        }

        [Fact]
        public async Task LatestDecomWindowIsTheMostRecentlyUpdated()
        {
            var windows = new WorkspaceDecomWindows(_workspace);
            Assert.Null(await windows.LatestAsync());

            DecomWindowModel Window(string id, DateTimeOffset updated) => new()
            {
                WindowId = id,
                ScanId = "scan",
                SubscriptionId = "sub",
                PlanHash = "p",
                ExecutionHash = "e",
                ScopesJson = "[]",
                State = "Locked",
                RowsJson = "[]",
                UpdatedAtUtc = updated,
            };
            var now = DateTimeOffset.UtcNow;
            await windows.UpsertAsync(Window("older", now.AddHours(-2)));
            await windows.UpsertAsync(Window("newest", now));
            await windows.UpsertAsync(Window("middle", now.AddHours(-1)));

            Assert.Equal("newest", (await windows.LatestAsync())!.WindowId);
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

        [Fact]
        public async Task ScanCheckpoint_SurvivesKillAndResumesSameRow()
        {
            var scans = new WorkspaceScans(_workspace);
            var scan = await scans.StartAsync("tenant", "Tenant");
            await scans.PersistPageAsync(scan.Id,
                [new InventorySnapshotRow("/subs/s/rg/a/providers/Microsoft.Test/w/one", "one", "Microsoft.Test/widgets", "westeurope", "rg", "s", null, """{"size":3}""")],
                """{"completedQueries":["gA"],"rows":1}""");

            // Simulated process kill: FinishAsync never ran, yet the page and checkpoint are durable.
            var resumable = await scans.GetResumableAsync();
            Assert.Equal(scan.Id, resumable!.Id);
            Assert.Contains("gA", resumable.CheckpointJson);

            var resumed = await scans.StartAsync("tenant", "Tenant", resumeScanId: scan.Id);
            Assert.Equal(scan.Id, resumed.Id); // one history row, not a fresh one
            await scans.PersistPageAsync(scan.Id,
                [new InventorySnapshotRow("/subs/s/rg/b/providers/Microsoft.Test/w/two", "two", "Microsoft.Test/widgets", "westeurope", "rg", "s", null, null)],
                """{"completedQueries":["gA","gB"],"rows":2,"contentHash":"HASH"}""");
            await scans.FinishAsync(scan.Id, "Completed", """{"completedQueries":["gA","gB"],"rows":2,"contentHash":"HASH"}""", """{"groupsCompleted":2}""", null);

            var latest = await scans.GetLatestCompletedAsync();
            Assert.Equal(scan.Id, latest!.Id);
            Assert.Equal(2, latest.Snapshots.Count);
            Assert.Equal("HASH", latest.ContentHash);
            Assert.Equal(2, latest.ResourceCount);
            Assert.Equal("""{"size":3}""", latest.Snapshots.Single(s => s.Name == "one").PropertiesJson); // raw ARM JSON verbatim
            Assert.Null(await scans.GetResumableAsync());
        }

        [Fact]
        public async Task Start_WritesInitialCheckpoint_AndRetiresStaleRunningScans()
        {
            var scans = new WorkspaceScans(_workspace);
            var crashed = await scans.StartAsync("tenant", "Tenant", checkpointJson: """{"rows":0}""");

            // A crash before the first persisted page still leaves a resumable row:
            // the checkpoint lands at start, not at first page.
            var resumable = await scans.GetResumableAsync();
            Assert.Equal(crashed.Id, resumable!.Id);

            // a fresh start must not shadow the crashed row - it becomes Interrupted
            var fresh = await scans.StartAsync("tenant", "Tenant", checkpointJson: "{}");
            Assert.NotEqual(crashed.Id, fresh.Id);
            using (var db = _workspace.CreateContext())
            {
                var retired = db.Scans.Single(s => s.Id == crashed.Id);
                Assert.Equal("Interrupted", retired.Status);
                Assert.NotNull(retired.FinishedAt);
            }

            // and the fresh scan is now the single resumable candidate
            var current = await scans.GetResumableAsync();
            Assert.Equal(fresh.Id, current!.Id);
        }

        // Wave B3: governance findings are stored with the scan that collected them.

        [Fact]
        public async Task PersistSourcePage_UpsertsAndKeepsResourceCount()
        {
            var scans = new WorkspaceScans(_workspace);
            var scan = await scans.StartAsync("tenant", "Tenant");
            await scans.PersistPageAsync(scan.Id, [Snapshot("one"), Snapshot("two")], """{"rows":2}""");

            var assignment = "/subscriptions/s/providers/Microsoft.Authorization/policyAssignments/require-tags";
            var policy = new GovernanceFindingRow("Policy", "state-1", Widget("one"), "s", "Medium", "Policy", "require-tags",
                "tagRule", assignment, "/providers/Microsoft.Authorization/policyDefinitions/d1");
            await scans.PersistSourcePageAsync(scan.Id,
                [Finding("Advisor", "rec-1", title: "Enable backup"), policy],
                """{"rows":2}""");

            // The same records stored again (a group queried a second time) replace their rows
            // on (source, record id); the same record id under another source is another row,
            // and a record listed twice in one page is stored once, the later one winning.
            await scans.PersistSourcePageAsync(scan.Id,
                [
                    Finding("Advisor", "rec-1", title: "Enable soft delete", severity: "High"),
                    Finding("Advisor", "rec-2", title: "first"),
                    Finding("Advisor", "rec-2", title: "second"),
                    Finding("Defender", "state-1"),
                ],
                """{"rows":2}""");

            var stored = await scans.GetGovernanceAsync(scan.Id);
            Assert.Equal(4, stored.Count);
            var rec1 = stored.Single(r => r.Source == "Advisor" && r.RecordId == "rec-1");
            Assert.Equal(("Enable soft delete", "High"), (rec1.Title, rec1.Severity));
            Assert.Equal("second", stored.Single(r => r.RecordId == "rec-2").Title);
            Assert.Equal(policy, stored.Single(r => r.Source == "Policy")); // every field round-trips
            Assert.Single(stored, r => r.Source == "Defender" && r.RecordId == "state-1");

            // ResourceCount counts the inventory; findings never change it.
            Assert.Equal(2, Assert.Single(await scans.ListAsync()).ResourceCount);
        }

        [Fact]
        public async Task PersistSourcePage_WritesTheCheckpoint()
        {
            var scans = new WorkspaceScans(_workspace);
            var scan = await scans.StartAsync("tenant", "Tenant", checkpointJson: """{"completedQueries":[],"rows":0}""");

            const string afterAdvisor = """{"completedQueries":["advisor:gA"],"rows":0,"sourceRows":{"advisor":1}}""";
            await scans.PersistSourcePageAsync(scan.Id, [Finding("Advisor", "rec-1")], afterAdvisor);
            var resumable = await scans.GetResumableAsync();
            Assert.Equal(scan.Id, resumable!.Id);
            Assert.Equal(afterAdvisor, resumable.CheckpointJson);

            // A group collected with no findings still records that it was collected.
            const string afterPolicy = """{"completedQueries":["advisor:gA","policy:gA"],"rows":0,"sourceRows":{"advisor":1,"policy":0}}""";
            await scans.PersistSourcePageAsync(scan.Id, [], afterPolicy);
            Assert.Equal(afterPolicy, (await scans.GetResumableAsync())!.CheckpointJson);
            Assert.Single(await scans.GetGovernanceAsync(scan.Id));
        }

        [Fact]
        public async Task PersistSourcePage_PageThatCannotBeStored_LeavesRowsAndCheckpointAsTheyWere()
        {
            var scans = new WorkspaceScans(_workspace);
            var scan = await scans.StartAsync("tenant", "Tenant");
            const string before = """{"completedQueries":["advisor:gA"],"rows":0}""";
            await scans.PersistSourcePageAsync(scan.Id, [Finding("Advisor", "rec-1", title: "kept")], before);

            // One row breaks a NOT NULL column: the whole page and its checkpoint are refused,
            // including the replacement of rec-1 that came before it.
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => scans.PersistSourcePageAsync(scan.Id,
                [Finding("Advisor", "rec-1", title: "replaced"), Finding("Advisor", "rec-2") with { Title = null! }],
                """{"completedQueries":["advisor:gA","advisor:gB"],"rows":0}"""));

            Assert.Equal("kept", Assert.Single(await scans.GetGovernanceAsync(scan.Id)).Title);
            Assert.Equal(before, (await scans.GetResumableAsync())!.CheckpointJson);
        }

        [Fact]
        public async Task GetGovernance_ReturnsOnlyThatScan()
        {
            var scans = new WorkspaceScans(_workspace);
            var first = await scans.StartAsync("tenant", "Tenant");
            await scans.PersistSourcePageAsync(first.Id,
                [Finding("Advisor", "rec-1", resourceId: Widget("b")), Finding("Policy", "state-1", resourceId: Widget("a"))],
                "{}");
            var second = await scans.StartAsync("tenant", "Tenant");
            await scans.PersistSourcePageAsync(second.Id,
                [Finding("Advisor", "rec-1", title: "second scan"), Finding("Defender", "assessment-9")],
                "{}");

            // Ordered by resource id; the same record id in another scan is that scan's own row.
            Assert.Equal([("Policy", "state-1"), ("Advisor", "rec-1")],
                (await scans.GetGovernanceAsync(first.Id)).Select(r => (r.Source, r.RecordId)));
            Assert.All(await scans.GetGovernanceAsync(first.Id), r => Assert.NotEqual("second scan", r.Title));
            Assert.Equal(2, (await scans.GetGovernanceAsync(second.Id)).Count);
            Assert.Empty(await scans.GetGovernanceAsync("no-such-scan"));
        }

        private static string Widget(string name) =>
            "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Test/widgets/" + name;

        private static InventorySnapshotRow Snapshot(string name) =>
            new(Widget(name), name, "Microsoft.Test/widgets", "westeurope", "rg", "s", null, null);

        private static GovernanceFindingRow Finding(string source, string recordId, string? resourceId = null,
            string title = "Enable soft delete", string severity = "Medium") =>
            new(source, recordId, resourceId ?? Widget("one"), "s", severity, "Security", title,
                Detail: null, PolicyAssignmentId: null, PolicyDefinitionId: null);

        /// <summary>Each result row of <paramref name="sql"/> as its columns joined with '|'.</summary>
        private static List<string> Query(WorkspaceDb workspace, string sql)
        {
            using var db = workspace.CreateContext();
            var connection = db.Database.GetDbConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                    .Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            return rows;
        }
    }
}
