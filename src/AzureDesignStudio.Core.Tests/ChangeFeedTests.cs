using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AzureDesignStudio.Discovery.ChangeFeed;
using AzureDesignStudio.Workspace;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Change feed spine (plan 9.1): ARG row normalization and idempotent workspace
    /// persistence. Offline only - row fixtures mirror the projected objectArray shape.
    /// </summary>
    public class ChangeFeedTests : IDisposable
    {
        private readonly string _directory;
        private readonly WorkspaceDb _workspace;
        private readonly WorkspaceChanges _changes;

        public ChangeFeedTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "ads_changes_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _workspace = new WorkspaceDb(Path.Combine(_directory, "workspace.db"));
            _changes = new WorkspaceChanges(_workspace);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }

        private const string Vm = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1";

        [Fact]
        public void BuildQuery_WindowsByUtcTimestamp_AndOrdersNewestFirst()
        {
            var since = new DateTimeOffset(2026, 10, 7, 13, 5, 59, 500, TimeSpan.FromHours(2));
            var q = ChangeFeedCollector.BuildQuery(since);
            Assert.StartsWith("resourcechanges", q);
            Assert.Contains("| where changeTime > datetime(2026-10-07T11:05:59.500Z)", q);
            Assert.Contains("| order by changeTime desc", q);
            // attribution columns are projected, so rows come back flat
            Assert.Contains("changedBy = tostring(properties.changeAttributes.changedBy)", q);
            Assert.Contains("clientType = tostring(properties.changeAttributes.clientType)", q);
        }

        [Fact]
        public void ParseRow_NormalizesAttributionPlaceholders()
        {
            var row = JsonDocument.Parse("""
            {
              "id": "/subscriptions/s/providers/Microsoft.Resources/changes/42",
              "changeTime": "2026-10-12T08:00:00+00:00",
              "targetResourceId": "__VM__",
              "targetResourceType": "microsoft.compute/virtualmachines",
              "changeType": "Update",
              "correlationId": "c-1",
              "changedBy": "john@contoso.com",
              "changedByType": "User",
              "clientType": "Azure Portal",
              "operation": "microsoft.compute/virtualmachines/write",
              "changesCount": 2,
              "changedProperties": { "tags.env": { "newValue": "prod", "previousValue": "null" } }
            }
            """.Replace("__VM__", Vm)).RootElement;

            var entry = ChangeFeedCollector.ParseRow(row);

            Assert.Equal(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), entry!.ChangeTime);
            Assert.Equal(Vm, entry.TargetResourceId);
            Assert.Equal("Update", entry.ChangeType);
            Assert.Equal("john@contoso.com", entry.ChangedBy);
            Assert.Equal("Azure Portal", entry.ClientType);
            Assert.Equal(2, entry.ChangesCount);
            Assert.Equal(JsonValueKind.Object, entry.ChangedProperties!.Value.ValueKind);
        }

        [Fact]
        public void ParseRow_UnspecifiedAndUnknownBecomeNull()
        {
            var row = JsonDocument.Parse("""
            {
              "id": "chg-1", "changeTime": "2026-10-12T08:00:00+00:00",
              "targetResourceId": "__VM__", "changeType": "Create",
              "changedBy": "Unspecified", "changedByType": "Unspecified",
              "clientType": "Unknown", "operation": "Unspecified", "changesCount": 0
            }
            """.Replace("__VM__", Vm)).RootElement;

            var entry = ChangeFeedCollector.ParseRow(row);

            Assert.NotNull(entry);
            Assert.Null(entry!.ChangedBy);
            Assert.Null(entry.ClientType);
            Assert.Null(entry.Operation);
            Assert.Null(entry.ChangedProperties);
        }

        [Theory]
        [InlineData("""{"changeTime":"2026-10-12T08:00:00+00:00","targetResourceId":"x"}""")]  // no id
        [InlineData("""{"id":"chg-1","targetResourceId":"x"}""")]                              // no time
        [InlineData("""{"id":"chg-1","changeTime":"not-a-time","targetResourceId":"x"}""")]    // bad time
        [InlineData("""{"id":"chg-1","changeTime":"2026-10-12T08:00:00+00:00"}""")]            // no target
        public void ParseRow_DropsPartiallyWrittenRows(string json)
        {
            Assert.Null(ChangeFeedCollector.ParseRow(JsonDocument.Parse(json).RootElement));
        }

        [Fact]
        public async Task SaveBatch_IsIdempotent_OverlappingWindowsAddNothing()
        {
            var first = Rows(("chg-1", Vm, new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero)));
            Assert.Equal(1, await _changes.SaveBatchAsync("scan-1", first));

            // re-collect the same window plus one new change
            var again = Rows(
                ("chg-1", Vm, new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero)),
                ("chg-2", Vm, new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero)));
            Assert.Equal(1, await _changes.SaveBatchAsync("scan-1", again));

            var feed = await _changes.ListSinceAsync(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.Equal(2, feed.Count);
            Assert.Equal("chg-2", feed[0].ChangeKey); // newest first
        }

        [Fact]
        public async Task SameChangeKeyUnderDifferentResources_BothPersist()
        {
            var other = Vm.Replace("vm1", "vm2");
            var rows = Rows(
                ("chg-1", Vm, new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero)),
                ("chg-1", other, new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero)));

            Assert.Equal(2, await _changes.SaveBatchAsync(null, rows));
        }

        [Fact]
        public async Task LatestByResource_ReturnsNewestPerTarget_OnlyRequestedIds()
        {
            var other = Vm.Replace("vm1", "vm2");
            var t = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
            await _changes.SaveBatchAsync("scan-1", Rows(
                ("chg-1", Vm, t),
                ("chg-2", Vm, t.AddDays(1)),
                ("chg-3", other, t)));

            var latest = await _changes.LatestByResourceAsync(t.AddDays(-1), [Vm]);

            var only = Assert.Single(latest);
            Assert.Equal(Vm, only.Key);
            Assert.Equal("chg-2", only.Value.ChangeKey);
        }

        private static IReadOnlyList<ChangeFeedRecord> Rows(params (string Key, string Target, DateTimeOffset When)[] rows) =>
            rows.Select(r => new ChangeFeedRecord
            {
                ChangeKey = r.Key,
                TargetResourceId = r.Target,
                ChangeTime = r.When,
                ChangeType = "Update",
            }).ToList();
    }
}
