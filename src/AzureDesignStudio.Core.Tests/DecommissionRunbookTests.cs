using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Decommission;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Unmanaged;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// 13.4 runbook-as-code + two-stage destroy ([69][71]). The invariants pinned here are the safety
/// ones: a refused plan generates nothing; purge is inert before T+N; the lock lifecycle brackets
/// the purge; only staged ids can ever appear in a delete line; and everything is deterministic.
/// </summary>
public class DecommissionRunbookTests
{
    private const string Sub = "/subscriptions/sub1";
    private const string Rg = Sub + "/resourceGroups/rg";
    private const string Vnet = Rg + "/providers/Microsoft.Network/virtualNetworks/vnet1";
    private const string SubnetA = Vnet + "/subnets/subnetA";
    private const string Nic = Rg + "/providers/Microsoft.Network/networkInterfaces/nic1";
    private const string Vm = Rg + "/providers/Microsoft.Compute/virtualMachines/vm1";
    private const string Disk = Rg + "/providers/Microsoft.Compute/disks/disk1";
    private const string Law = Rg + "/providers/Microsoft.OperationalInsights/workspaces/law1";
    private const string Kv = Rg + "/providers/Microsoft.KeyVault/vaults/kv1";

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static InventoryItem Item(string id, string type, string? propsJson = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        JsonElement? raw = null;
        if (propsJson is not null)
            raw = JsonDocument.Parse(propsJson).RootElement.Clone();
        return new InventoryItem(id, id.Split('/')[^1], type, "westeurope", "rg", "sub1",
            tags ?? new Dictionary<string, string>(), raw);
    }

    private static string Fill(string template) => template
        .Replace("__VM__", Vm).Replace("__NIC__", Nic).Replace("__DISK__", Disk)
        .Replace("__VNET__", Vnet).Replace("__SUBNET__", SubnetA).Replace("__LAW__", Law);

    private static InventoryItem VmItem() => Item(Vm, "microsoft.compute/virtualmachines",
        Fill("""{"networkProfile":{"networkInterfaces":[{"id":"__NIC__"}]},"storageProfile":{"dataDisks":[{"managedDisk":{"name":"disk1"}}]}}"""));

    private static InventoryItem NicItem() => Item(Nic, "microsoft.network/networkinterfaces",
        Fill("""{"ipConfigurations":[{"properties":{"subnet":{"id":"__SUBNET__"}}}]}"""));

    private static InventoryItem DiskItem() => Item(Disk, "microsoft.compute/disks",
        Fill("""{"managedBy":"__VM__"}"""));

    private static InventoryItem VnetItem() => Item(Vnet, "microsoft.network/virtualNetworks",
        Fill("""{"subnets":[{"name":"subnetA","properties":{}}]}"""));

    private static DecommissionPlan PlanAll(IReadOnlyList<InventoryItem> inventory, IEnumerable<string> candidates) =>
        DecommissionPlanner.Plan(inventory, candidates,
            guardrails: GuardrailPolicy.Empty, locks: [], roleAssignments: [], backupItems: []);

    private static Runbook GenerateFor(DecommissionPlan plan, IReadOnlyList<InventoryItem> inventory,
        RunbookOptions? options = null) =>
        DecommissionRunbook.Generate(plan, inventory, options ?? new RunbookOptions(Now, TimeSpan.FromDays(7), ExistingLocks: []));

    // --- [71] two-stage destroy shape ---

    [Fact]
    public void AllowedChain_ProtectThenDetachThenReleaseThenStagedPurge()
    {
        var inventory = new[] { VmItem(), NicItem(), DiskItem(), VnetItem() };
        var plan = PlanAll(inventory, new[] { Vm, Nic, Disk, Vnet });
        var cli = GenerateFor(plan, inventory).CliScript();

        var protect = cli.IndexOf("az lock create", StringComparison.Ordinal);
        var detach = cli.IndexOf("az resource exists", StringComparison.Ordinal);
        var release = cli.IndexOf("az lock delete", StringComparison.Ordinal);
        var purgeVm = cli.IndexOf($"az resource delete --id \"{Vm}\"", StringComparison.Ordinal);
        var purgeNic = cli.IndexOf($"az resource delete --id \"{Nic}\"", StringComparison.Ordinal);
        var purgeVnet = cli.IndexOf($"az resource delete --id \"{Vnet}\"", StringComparison.Ordinal);

        Assert.True(protect >= 0 && protect < detach);
        Assert.True(detach < release);
        // lock release precedes any purge, and purge follows the planner's own stage order
        Assert.True(release < purgeVm && purgeVm < purgeNic && purgeNic < purgeVnet);
    }

    [Fact]
    public void Purge_IsInertBeforeTheUndoWindowOpens()
    {
        var inventory = new[] { VmItem(), NicItem() };
        var plan = PlanAll(inventory, new[] { Vm, Nic });
        var rb = GenerateFor(plan, inventory);

        Assert.Equal(Now + TimeSpan.FromDays(7), rb.PurgeNotBefore);
        Assert.Contains($"-lt {rb.PurgeNotBefore.ToUnixTimeSeconds()}", rb.CliScript());
        Assert.Contains($"purge window not open before {rb.PurgeNotBefore:O}", rb.CliScript());
        Assert.Contains($"purge window not open before {rb.PurgeNotBefore:O}", rb.PowerShellScript());
        Assert.Contains($"inert before {rb.PurgeNotBefore:O}", rb.Markdown());
    }

    [Fact]
    public void WindowLockLifecycle_BracketsThePurge()
    {
        var inventory = new[] { VmItem(), NicItem() };
        var plan = PlanAll(inventory, new[] { Vm, Nic });
        var rb = GenerateFor(plan, inventory);
        var lockName = ProtectionWindowPlanner.LockNameFor(rb.Window);

        Assert.StartsWith(ProtectionWindowPlanner.LockPrefix, lockName);
        Assert.Equal(rb.PurgeNotBefore, rb.Window.End);

        var protect = rb.Stages.Single(s => s.Title.StartsWith("Stage 0", StringComparison.Ordinal));
        var purgeRelease = rb.Stages.Single(s => s.Title.StartsWith("Stage 2a", StringComparison.Ordinal));
        var abort = rb.Stages.Single(s => s.Title.StartsWith("Abort", StringComparison.Ordinal));

        // one create per staged id before anything else, and the same ids get their release
        // both in the purge flow and in the abort path
        Assert.Equal(2, rb.Stages.Count(s => s.Lines.All(l => l.Cli.StartsWith("az lock delete", StringComparison.Ordinal))));
        foreach (var id in new[] { Vm, Nic })
        {
            Assert.Single(protect.Lines, l => l.ResourceId == id && l.Cli.Contains($"--name \"{lockName}\""));
            Assert.Single(purgeRelease.Lines, l => l.ResourceId == id && l.Cli.Contains($"--name \"{lockName}\""));
            Assert.Single(abort.Lines, l => l.ResourceId == id);
        }
    }

    // --- [69] the refusal oracle ---

    [Fact]
    public void RefusedPlan_GeneratesNothing()
    {
        var inventory = new[] { VmItem(), NicItem() };
        var plan = PlanAll(inventory, new[] { Nic }); // external VM still uses the nic -> refusal

        var ex = Assert.Throws<InvalidOperationException>(() => GenerateFor(plan, inventory));
        Assert.Contains("refusal", ex.Message);
    }

    [Fact]
    public void CascadeAndRefusedIds_NeverInDeleteLines_CascadeNamedInChecklist()
    {
        var inventory = new[] { VnetItem() };
        var plan = PlanAll(inventory, new[] { Vnet });
        Assert.Contains(plan.ImplicitChildren, c => c.ChildId == SubnetA);

        var rb = GenerateFor(plan, inventory);
        var cli = rb.CliScript();
        Assert.Contains($"az resource delete --id \"{Vnet}\"", cli);
        Assert.DoesNotContain($"resource delete --id \"{SubnetA}\"", cli);
        Assert.Contains($"{SubnetA} dies with {Vnet}", rb.Markdown());
    }

    // --- determinism + plan-hash binding ---

    [Fact]
    public void SameInputs_ByteIdenticalRunbooks()
    {
        var inventory = new[] { VmItem(), NicItem(), DiskItem(), VnetItem() };
        var plan = PlanAll(inventory, new[] { Vm, Nic, Disk, Vnet });
        var a = GenerateFor(plan, inventory);
        var b = GenerateFor(plan, inventory);

        Assert.Equal(a.PlanHash, b.PlanHash);
        Assert.Equal(a.Markdown(), b.Markdown());
        Assert.Equal(a.CliScript(), b.CliScript());
        Assert.Equal(a.PowerShellScript(), b.PowerShellScript());
    }

    [Fact]
    public void PlanHash_BindsTheUndoWindow()
    {
        var inventory = new[] { VmItem(), NicItem() };
        var plan = PlanAll(inventory, new[] { Vm, Nic });
        var a = GenerateFor(plan, inventory);
        var b = GenerateFor(plan, inventory, new RunbookOptions(Now, TimeSpan.FromDays(14), ExistingLocks: []));

        Assert.StartsWith("sha256:", a.PlanHash);
        Assert.NotEqual(a.PlanHash, b.PlanHash);
        Assert.Contains(a.PlanHash, a.Markdown());
        Assert.Contains(a.PlanHash, a.CliScript());
    }

    [Fact]
    public void ExecutionHash_TheServerReplayOfTheWiredOStringsAndStageZeroScopes_ReproducesTheClientSignature()
    {
        // The desktop signs the execution hash over the runbook's own identity + the Stage 0
        // line scopes; the server re-parses the round-trip "O" strings from the wire (the exact
        // DateTimeStyles ExecuteDecomWindow uses) and re-sorts the scopes before recomputing.
        // Any drift on either side means every real execute 409s - this pins the whole seam.
        var inventory = new[] { VmItem(), NicItem() };
        var rb = GenerateFor(PlanAll(inventory, new[] { Vm, Nic }), inventory);

        var stage0 = rb.Stages.Single(s => s.Title.StartsWith("Stage 0", StringComparison.Ordinal));
        var scopes = stage0.Lines.Select(l => l.ResourceId)
            .OrderBy(s => s.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        // The approval binds exactly the staged lock set - the scopes are the staged ids.
        Assert.Equal(new[] { Vm, Nic }, scopes);

        var client = DecomExecution.ExecutionHash(rb.Window.WindowId, rb.PlanHash, "sub1",
            rb.PurgeNotBefore, scopes);

        var purge = DateTimeOffset.Parse(rb.PurgeNotBefore.ToString("O"), null,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.RoundtripKind);
        var server = DecomExecution.ExecutionHash(rb.Window.WindowId, rb.PlanHash, "sub1", purge,
            // reversed input: the server sorts itself, order must not leak into the hash
            scopes.OrderByDescending(s => s, StringComparer.Ordinal));

        Assert.Equal(client, server);
    }

    // --- checklist honesty ---

    [Fact]
    public void StackNamed_DetachBodyCarriesDetachPolicy()
    {
        var inventory = new[] { VmItem(), NicItem() };
        var plan = PlanAll(inventory, new[] { Vm, Nic });
        var rb = GenerateFor(plan, inventory,
            new RunbookOptions(Now, TimeSpan.FromDays(7), StackName: "ads-decom-stack", StackLocation: "westeurope", ExistingLocks: []));

        Assert.NotNull(rb.StackDetachJson);
        Assert.Contains("\"Detach\"", rb.StackDetachJson);
        Assert.DoesNotContain("\"Delete\"", rb.StackDetachJson);
        Assert.Contains("ads-decom-stack", rb.Markdown());
    }

    [Fact]
    public void StackNotNamed_HonestManualUnmanageCaveat()
    {
        var inventory = new[] { VmItem(), NicItem() };
        var plan = PlanAll(inventory, new[] { Vm, Nic });
        var rb = GenerateFor(plan, inventory);

        Assert.Null(rb.StackDetachJson);
        Assert.Contains(rb.Caveats, c => c.StartsWith("stack-not-named"));
    }

    [Fact]
    public void OwnersAndCmdbAndMonitoring_AreChecklistLines()
    {
        var ownedDisk = Item(Disk, "microsoft.compute/disks", Fill("""{"managedBy":"__VM__"}"""),
            new Dictionary<string, string> { ["owner"] = "alice@contoso.com" });
        var inventory = new[] { VmItem(), NicItem(), ownedDisk };
        var plan = PlanAll(inventory, new[] { Vm, Nic, Disk });
        var rb = GenerateFor(plan, inventory);

        Assert.Contains(rb.Checklist, c => c.StartsWith("Peer-review") && c.Contains(rb.PlanHash));
        Assert.Contains(rb.Checklist, c => c.Contains("disk1 (alice@contoso.com)"));
        Assert.Contains(rb.Checklist, c => c.Contains("carry no owner tag"));
        Assert.Contains(rb.Checklist, c => c.Contains("CMDB change record"));
    }

    [Fact]
    public void PlanFindings_CarryIntoTheChecklist()
    {
        var items = new[]
        {
            Item(Law, "microsoft.operationalinsights/workspaces"),
            Item(Rg + "/providers/Microsoft.Insights/diagnosticSettings/ds1", "microsoft.insights/diagnosticsettings",
                Fill("""{"resourceUri":"__VM__","workspaceId":"__LAW__"}""")),
            Item(Kv, "microsoft.keyvault/vaults"),
        };
        var plan = PlanAll(items, new[] { Law, Kv });
        var rb = GenerateFor(plan, items);

        Assert.Contains(rb.Checklist, c => c.StartsWith("Teardown monitoring") && c.Contains(Law));
        Assert.Contains(rb.Checklist, c => c.StartsWith("Key Vault") && c.Contains(Kv) && c.Contains("90 days"));
    }

    [Fact]
    public void WarningBlockers_AreCarriedNotDropped()
    {
        var inventory = new[] { VmItem(), NicItem() };
        var plan = PlanAll(inventory, new[] { Vm }); // survivor-orphan warning on the nic

        Assert.True(plan.Allowed);
        var rb = GenerateFor(plan, inventory);
        Assert.Contains(rb.Checklist, c => c.StartsWith("Warning carried:") && c.Contains("survivor-orphan"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
