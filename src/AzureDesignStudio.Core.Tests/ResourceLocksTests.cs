using AzureDesignStudio.Discovery.Security;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>Offline rules for management-lock scope resolution (v2 plan 80): a lock binds
/// to its scope and everything under it; every applicable lock is enforced together; nothing is guessed.</summary>
public class ResourceLocksTests
{
    private const string Rg = "/subscriptions/s1/resourceGroups/rg1";
    private const string Vm = Rg + "/providers/Microsoft.Compute/virtualMachines/vm1";

    private static LockRow Lock(string atScope, string level, string? notes = null, string name = "l1") =>
        new(atScope + "/providers/Microsoft.Authorization/locks/" + name, name, level, notes);

    [Fact]
    public void ResourceGroupLock_CoversResourcesInsideIt()
    {
        var map = new LockMap([Lock(Rg, "CanNotDelete", "prod freeze")]);
        var eff = map.For(Vm);
        Assert.NotNull(eff);
        Assert.True(eff!.CanNotDelete);
        Assert.Equal("prod freeze", eff.Notes);
        Assert.Equal(Rg, eff.ScopeId);
    }

    [Fact]
    public void LocksOnEveryScope_Union_WithDeepestReported()
    {
        var map = new LockMap(
        [
            Lock(Rg, "CanNotDelete"),
            Lock(Vm, "ReadOnly", "config pinned"),
        ]);
        var eff = map.For(Vm);
        Assert.NotNull(eff);
        // ARM enforces BOTH: the group freeze and the per-VM read-only pin.
        Assert.Equal(["CanNotDelete", "ReadOnly"], eff!.Levels);
        Assert.Equal(Vm, eff.ScopeId);
        Assert.Equal("config pinned", eff.Notes);

        // the other VM in the same group only inherits the group-level lock
        var other = map.For(Rg + "/providers/Microsoft.Compute/virtualMachines/vm2");
        Assert.NotNull(other);
        Assert.Equal(["CanNotDelete"], other!.Levels);
    }

    [Fact]
    public void ReadOnlyOnBroaderScope_IsNeverLostUnderNarrowerCanNotDelete()
    {
        // regression: a per-resource CanNotDelete must not mask a ReadOnly bound to the
        // group - ARM keeps enforcing both, and dropping the ReadOnly lies about writability.
        var map = new LockMap([Lock(Rg, "ReadOnly"), Lock(Vm, "CanNotDelete")]);
        var eff = map.For(Vm);
        Assert.NotNull(eff);
        Assert.True(eff!.ReadOnly && eff.CanNotDelete);
    }

    [Fact]
    public void BothLocksOnSameScope_Combine()
    {
        var map = new LockMap([Lock(Rg, "CanNotDelete"), Lock(Rg, "ReadOnly", notes: null, name: "l2")]);
        var eff = map.For(Vm);
        Assert.NotNull(eff);
        Assert.True(eff!.CanNotDelete && eff.ReadOnly);
        Assert.Equal("CanNotDelete + ReadOnly", eff.Label);
    }

    [Fact]
    public void PrefixMatchIsSegmentBounded_SiblingsStayFree()
    {
        var map = new LockMap([Lock("/subscriptions/s1/resourceGroups/rg", "CanNotDelete")]);
        Assert.Null(map.For("/subscriptions/s1/resourceGroups/rg2/providers/Microsoft.Compute/virtualMachines/vm1"));
        Assert.NotNull(map.For("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st1"));
    }

    [Fact]
    public void SubscriptionLock_CoversEverythingUnderIt()
    {
        var map = new LockMap([Lock("/subscriptions/s1", "CanNotDelete")]);
        Assert.NotNull(map.For(Vm));
        Assert.NotNull(map.For("/subscriptions/s1/resourceGroups/other"));
    }

    [Fact]
    public void ArmIdsMatchCaseInsensitively()
    {
        var map = new LockMap([Lock("/subscriptions/S1/resourceGroups/RgX", "CanNotDelete")]);
        Assert.NotNull(map.For("/subscriptions/s1/resourcegroups/rgx/providers/microsoft.compute/virtualmachines/vm1"));
    }

    [Fact]
    public void UnrecognizedLevel_IsSurfacedNotDropped()
    {
        var map = new LockMap([Lock(Rg, "NotSpecified")]);
        var eff = map.For(Vm);
        Assert.NotNull(eff);
        Assert.Equal(["NotSpecified"], eff!.Levels);
        Assert.False(eff.CanNotDelete || eff.ReadOnly);
    }

    [Fact]
    public void LockRowWithoutProviderTail_IsDroppedAndCounted()
    {
        var map = new LockMap([new LockRow("/subscriptions/s1/whatever", "x", "CanNotDelete", null), Lock(Rg, "ReadOnly")]);
        Assert.Equal(2, map.TotalLocks);
        Assert.Equal(1, map.PlacedCount);
        Assert.Equal(1, map.DroppedCount);
        Assert.NotNull(map.For(Vm));
    }

    [Fact]
    public void UnlockedResource_HasNoEffectiveLock()
    {
        var map = new LockMap([Lock("/subscriptions/s1/resourceGroups/elsewhere", "ReadOnly")]);
        Assert.Null(map.For(Vm));
    }
}
