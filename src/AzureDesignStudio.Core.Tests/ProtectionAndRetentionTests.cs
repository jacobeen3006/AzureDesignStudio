using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureDesignStudio.Discovery.Cost;
using AzureDesignStudio.Discovery.Decommission;
using AzureDesignStudio.Discovery.Inventory;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// 13.3 retain-before-retire [66], idle-cost ranking [67] and protection windows [68].
/// The recurring contract under test is honesty: unknown coverage reports as unknown, missing
/// spend ranks without $, and a foreign lock can never be touched by a release run.
/// </summary>
public class ProtectionAndRetentionTests
{
    private const string Sub = "/subscriptions/sub1";
    private const string Rg = Sub + "/resourceGroups/rg";
    private const string Vm = Rg + "/providers/Microsoft.Compute/virtualMachines/vm1";
    private const string Nic1 = Rg + "/providers/Microsoft.Network/networkInterfaces/nic1";
    private const string Nic2 = Rg + "/providers/Microsoft.Network/networkInterfaces/nic2";
    private const string Disk1 = Rg + "/providers/Microsoft.Compute/disks/disk1";
    private const string Disk2 = Rg + "/providers/Microsoft.Compute/disks/disk2";
    private const string Pip1 = Rg + "/providers/Microsoft.Network/publicIPAddresses/pip1";
    private const string Pip2 = Rg + "/providers/Microsoft.Network/publicIPAddresses/pip2";
    private const string Nsg = Rg + "/providers/Microsoft.Network/networkSecurityGroups/nsg1";
    private const string Law = Rg + "/providers/Microsoft.OperationalInsights/workspaces/law1";
    private const string Storage = Rg + "/providers/Microsoft.Storage/storageaccounts/st1";
    private const string Vault = Rg + "/providers/Microsoft.RecoveryServices/vaults/rv1";

    private static InventoryItem Item(string id, string type, string? propsJson = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        JsonElement? raw = null;
        if (propsJson is not null)
            raw = JsonDocument.Parse(propsJson).RootElement.Clone();
        return new InventoryItem(id, id.Split('/')[^1], type, "westeurope", "rg", "sub1",
            tags ?? new Dictionary<string, string>(), raw);
    }

    // --- [66] retain-before-retire ---

    [Fact]
    public void NoBackupEvidence_CoverageStaysUnknown()
    {
        var manifest = RetainBeforeRetire.Build([Item(Vm, "microsoft.compute/virtualmachines")], new[] { Vm }, null);

        var row = Assert.Single(manifest.Rows);
        Assert.Null(row.BackupCovered);
        Assert.True(row.NeedsExport);
        Assert.Contains(manifest.Caveats, c => c.StartsWith("backup-coverage-unknown"));
    }

    [Fact]
    public void BackupEvidence_SplitsCoveredFromNeedsExport()
    {
        var items = new[]
        {
            Item(Vm, "microsoft.compute/virtualmachines"),
            Item(Disk1, "microsoft.compute/disks"),
        };
        var manifest = RetainBeforeRetire.Build(items, new[] { Vm, Disk1 },
            [new BackupProtectionItem(Vault, Vm)]);

        var vmRow = Assert.Single(manifest.Rows, r => r.ResourceId == Vm);
        Assert.True(vmRow.BackupCovered);
        Assert.False(vmRow.NeedsExport);
        var diskRow = Assert.Single(manifest.Rows, r => r.ResourceId == Disk1);
        Assert.False(diskRow.BackupCovered);
        Assert.True(diskRow.NeedsExport);
    }

    [Fact]
    public void ConfigExport_CarriesScanFacts()
    {
        var item = Item(Vm, "microsoft.compute/virtualmachines", """{"vmSize":"Standard_D2s_v3"}""",
            new Dictionary<string, string> { ["env"] = "prod" });
        var manifest = RetainBeforeRetire.Build([item], new[] { Vm }, []);

        using var doc = JsonDocument.Parse(Assert.Single(manifest.Rows).ConfigExportJson!);
        Assert.Equal("vm1", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("microsoft.compute/virtualmachines", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("westeurope", doc.RootElement.GetProperty("location").GetString());
        Assert.Equal("prod", doc.RootElement.GetProperty("tags").GetProperty("env").GetString());
        Assert.Equal("Standard_D2s_v3", doc.RootElement.GetProperty("properties").GetProperty("vmSize").GetString());
    }

    [Fact]
    public void DataLossKinds_FoldFromThePlanner()
    {
        var manifest = RetainBeforeRetire.Build(
            [Item(Storage, "microsoft.storage/storageaccounts")], new[] { Storage }, []);

        Assert.Contains("storage-data-destroyed", Assert.Single(manifest.Rows).DataLossKinds);
    }

    [Fact]
    public void WorkspaceCandidate_ListsItsDiagnosticsSources()
    {
        var items = new[]
        {
            Item(Law, "microsoft.operationalinsights/workspaces"),
            Item(Rg + "/providers/Microsoft.Insights/diagnosticSettings/ds1", "microsoft.insights/diagnosticsettings",
                $$"""{"resourceUri":"{{Vm}}","workspaceId":"{{Law}}"}"""),
        };
        var manifest = RetainBeforeRetire.Build(items, new[] { Law }, []);

        Assert.Contains(Vm, Assert.Single(manifest.Rows).DiagnosticsSources);
    }

    [Fact]
    public void UnknownCandidate_BecomesCaveatNotRow()
    {
        var manifest = RetainBeforeRetire.Build([Item(Vm, "microsoft.compute/virtualmachines")],
            new[] { Rg + "/providers/Microsoft.Compute/virtualMachines/ghost" }, []);

        Assert.Empty(manifest.Rows);
        Assert.Contains(manifest.Caveats, c => c.StartsWith("candidate-not-in-scan"));
    }

    [Fact]
    public void RetentionOutput_IsDeterministicAndScanCased()
    {
        var items = new[] { Item(Vm, "microsoft.compute/virtualmachines"), Item(Disk1, "microsoft.compute/disks") };

        var first = JsonSerializer.Serialize(RetainBeforeRetire.Build(items, new[] { Vm, Disk1 }, []));
        var second = JsonSerializer.Serialize(RetainBeforeRetire.Build(items.Reverse().ToList(),
            new[] { Disk1.ToUpperInvariant(), Vm }, []));

        Assert.Equal(first, second);
        Assert.Equal(Disk1, Assert.Single(RetainBeforeRetire
            .Build(items, new[] { Disk1.ToUpperInvariant() }, []).Rows).ResourceId);
    }

    // --- [67] idle-cost ranking ---

    private static ActualSpendResult Spend(params (string Id, decimal Cost)[] rows) =>
        new(rows.Select(r => new SpendRow(r.Id, "rg", null, null, "westeurope", r.Cost, "USD")).ToList(),
            new SpendProvenance("cost-management-query", "subscriptions/sub1", "westeurope", "USD",
                new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)));

    private static InventoryItem AttachedDisk() => Item(Disk2, "microsoft.compute/disks",
        $$"""{"managedBy":"{{Vm}}"}""");

    [Fact]
    public void RanksByIdleCapableType_CostDescendingThenIdleThenOrdinal()
    {
        var items = new[]
        {
            Item(Disk1, "microsoft.compute/disks"),                                   // idle, 12.50
            Item(Nic1, "microsoft.network/networkinterfaces"),                        // orphan, 4.25
            Item(Pip1, "microsoft.network/publicIPAddresses"),                        // idle, 2.00
            AttachedDisk(),                                                           // leased, no spend
            Item(Vm, "microsoft.compute/virtualmachines",                             // not idle-capable
                """{"networkProfile":{"networkInterfaces":[{"id":"__ID__"}]}}""".Replace("__ID__", Nic2)),
            Item(Nic2, "microsoft.network/networkinterfaces"),                        // referenced by the VM
        };
        var report = IdleCostRanker.Rank(items, Spend((Disk1, 12.50m), (Nic1, 4.25m), (Pip1, 2.00m)));

        Assert.Equal(new[] { Disk1, Nic1, Pip1, Disk2, Nic2 }, report.Rows.Select(r => r.ResourceId).ToArray());
        Assert.DoesNotContain(report.Rows, r => r.ResourceId == Vm);
        Assert.Equal(18.75m, report.TotalCost);
        Assert.Equal("USD", report.Rows[0].Currency);
        Assert.Equal("cost-management-query", report.Provenance!.Source);
    }

    [Fact]
    public void IdleSignals_AttachedLeasedAndReferencedResourcesStayClean()
    {
        var items = new[]
        {
            Item(Disk1, "microsoft.compute/disks"),                       // idle: nothing holds it
            AttachedDisk(),                                               // managedBy -> not idle
            Item(Pip1, "microsoft.network/publicIPAddresses"),            // idle: no references
            Item(Pip2, "microsoft.network/publicIPAddresses"),            // referenced by nic1 -> not idle
            Item(Nic1, "microsoft.network/networkinterfaces", """{"ipConfigurations":[{"properties":{"publicIPAddress":{"id":"__ID__"}}}]}""".Replace("__ID__", Pip2)),
            Item(Vm, "microsoft.compute/virtualmachines", """{"networkProfile":{"networkInterfaces":[{"id":"__ID__"}]}}""".Replace("__ID__", Nic1)),
        };
        var report = IdleCostRanker.Rank(items, null);

        Assert.True(ByIdle(report, Disk1));
        Assert.False(ByIdle(report, Disk2));
        Assert.True(ByIdle(report, Pip1));
        Assert.False(ByIdle(report, Pip2));
        Assert.False(ByIdle(report, Nic1)); // referenced by the VM
        Assert.Contains(Assert.Single(report.Rows, r => r.ResourceId == Disk1).IdleReasons,
            s => s.StartsWith("unattached-disk"));
    }

    private static bool ByIdle(IdleCostReport report, string id) =>
        Assert.Single(report.Rows, r => r.ResourceId == id).Idle;

    [Fact]
    public void MissingSpend_RanksWithoutFiguresAndSaysSo()
    {
        var report = IdleCostRanker.Rank([Item(Disk1, "microsoft.compute/disks")], null);

        Assert.Null(Assert.Single(report.Rows).Cost);
        Assert.Equal(0m, report.TotalCost);
        Assert.Contains(report.Caveats, c => c.StartsWith("spend-not-supplied"));
        Assert.Contains(report.Caveats, c => c.StartsWith("metrics-not-supplied"));
    }

    [Fact]
    public void UnmatchedSpendRows_AreCountedNotSilentlyDropped()
    {
        var report = IdleCostRanker.Rank([Item(Disk1, "microsoft.compute/disks")],
            Spend((Disk1, 5m), (Rg + "/providers/Microsoft.Compute/disks/elsewhere", 7m)));

        Assert.Contains(report.Caveats, c => c.StartsWith("spend-rows-unmatched:1"));
    }

    // --- [68] protection windows ---

    private static readonly ProtectionWindow Window =
        new("teardown-2026-10", new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Protect_LocksUnprotectedAndSkipsLocked()
    {
        var plan = ProtectionWindowPlanner.Protect(
            [Item(Vm, "microsoft.compute/virtualmachines"), Item(Nsg, "microsoft.network/networksecuritygroups")],
            new[] { Vm, Nsg }, Window, [new ArmLock(Nsg, ArmLockLevel.CanNotDelete, "operator-lock")]);

        var lockItem = Assert.Single(plan.Apply);
        Assert.Equal(Vm, lockItem.Scope);
        Assert.Equal(ArmLockLevel.CanNotDelete, lockItem.Level);
        Assert.Equal(plan.LockName, lockItem.Name);
        Assert.Contains("2026-10-20T08:00:00.0000000+00:00", lockItem.Notes);
        Assert.Equal(new[] { Nsg }, plan.AlreadyProtected);
    }

    [Fact]
    public void AncestorLockAndUnspecifiedLevel_CountAsProtected()
    {
        var plan = ProtectionWindowPlanner.Protect(
            [Item(Vm, "microsoft.compute/virtualmachines")], new[] { Vm }, Window,
            [new ArmLock(Rg, ArmLockLevel.NotSpecified, "rg-lock")]);

        Assert.Empty(plan.Apply);
        Assert.Equal(new[] { Vm }, plan.AlreadyProtected);
    }

    [Fact]
    public void DenyAssignmentsLock_DoesNotBlockDeleteSoProtectionStillApplies()
    {
        var plan = ProtectionWindowPlanner.Protect(
            [Item(Vm, "microsoft.compute/virtualmachines")], new[] { Vm }, Window,
            [new ArmLock(Vm, ArmLockLevel.DenyAssignments, "rbac-deny")]);

        Assert.Single(plan.Apply);
    }

    [Fact]
    public void LockName_IsDeterministicArmSafeAndWindowBound()
    {
        Assert.Matches(new Regex(@"^ads-decom-[0-9a-f]{8}$"), ProtectionWindowPlanner.LockNameFor(Window));
        Assert.Equal(ProtectionWindowPlanner.LockNameFor(Window), ProtectionWindowPlanner.LockNameFor("teardown-2026-10"));
        Assert.NotEqual(ProtectionWindowPlanner.LockNameFor(Window), ProtectionWindowPlanner.LockNameFor("other-window"));

        Assert.Throws<ArgumentOutOfRangeException>(() => ProtectionWindowPlanner.LockNameFor(
            new ProtectionWindow("bad", Window.End, Window.Start)));
        Assert.Throws<ArgumentException>(() => ProtectionWindowPlanner.LockNameFor(" "));
    }

    [Fact]
    public void Release_RemovesOwnPrefixOnly_ForeignLocksSurvive()
    {
        var ownA = ProtectionWindowPlanner.LockNameFor("teardown-2026-10");
        var ownB = ProtectionWindowPlanner.LockNameFor("other-window");
        var locks = new[]
        {
            new ArmLock(Vm, ArmLockLevel.CanNotDelete, ownA),
            new ArmLock(Disk1, ArmLockLevel.CanNotDelete, ownB),
            new ArmLock(Nsg, ArmLockLevel.CanNotDelete, "operator-lock"),
            new ArmLock(Law, ArmLockLevel.CanNotDelete, "ADS-decom-deadbeef"), // foreign by case
        };

        var release = ProtectionWindowPlanner.Release(locks);

        Assert.Equal(new[] { Disk1, Vm }, release.Remove.Select(l => l.Scope).ToArray());
        Assert.Equal(new[] { Nsg, Law }, release.LeftInPlace.Select(l => l.Scope).ToArray());
    }

    [Fact]
    public void Release_NarrowedToWindowLeavesOtherWindowsAlone()
    {
        var ownA = ProtectionWindowPlanner.LockNameFor("teardown-2026-10");
        var ownB = ProtectionWindowPlanner.LockNameFor("other-window");
        var release = ProtectionWindowPlanner.Release(
            [new ArmLock(Vm, ArmLockLevel.CanNotDelete, ownA), new ArmLock(Disk1, ArmLockLevel.CanNotDelete, ownB)],
            "teardown-2026-10");

        Assert.Equal(new[] { Vm }, release.Remove.Select(l => l.Scope).ToArray());
        Assert.Equal(new[] { Disk1 }, release.LeftInPlace.Select(l => l.Scope).ToArray());
    }

    [Fact]
    public void Protect_UnknownCandidatesGetNothing()
    {
        var plan = ProtectionWindowPlanner.Protect([Item(Vm, "microsoft.compute/virtualmachines")],
            new[] { Rg + "/providers/Microsoft.Compute/virtualMachines/ghost" }, Window, []);

        Assert.Empty(plan.Apply);
        Assert.Empty(plan.AlreadyProtected);
    }
}
