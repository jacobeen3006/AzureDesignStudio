using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 12.3/[60][61]: the stack engine builds a canonical PUT body bound by the same
/// sha256 identity the approval gate uses, refuses delete-flavored unmanage policy until the
/// Phase 13 gates ship, reads managed ids from a stack GET without silent drops, and answers
/// the stack-out-of-sync guard case-insensitively and deterministically.
/// </summary>
public class DeploymentStackTests
{
    private const string Template = """
        {"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[]}
        """;

    private static readonly StackPolicy DefaultPolicy = new(StackUnmanageAction.Detach);

    private static string Codes(DeploymentStackBody body) =>
        string.Join(",", body.Diagnostics.Select(d => d.Code));

    [Fact]
    public void Build_IsDeterministic_AndParameterOrderIrrelevant()
    {
        var a = DeploymentStack.Build("stack-a", "westeurope", Template, DefaultPolicy, """{"b":{"value":2},"a":{"value":1}}""");
        var b = DeploymentStack.Build("stack-a", "westeurope", Template, DefaultPolicy, """{"a":{"value":1},"b":{"value":2}}""");

        Assert.True(a.Success, Codes(a));
        Assert.Equal(a.PlanHash, b.PlanHash);
        // Canonical order binds the hash; the emitted body stays first-appearance ordered.
        Assert.Contains("\"b\":", a.Json);
        Assert.NotEqual(a.Json, b.Json);
    }

    [Fact]
    public void Build_HashChangesWithEveryIdentityInput()
    {
        var baseBody = DeploymentStack.Build("stack-a", "westeurope", Template, DefaultPolicy);
        var renamed = DeploymentStack.Build("stack-b", "westeurope", Template, DefaultPolicy);
        var moved = DeploymentStack.Build("stack-a", "northeurope", Template, DefaultPolicy);
        var edited = DeploymentStack.Build("stack-a", "westeurope", Template.Replace("1.0.0.0", "2.0.0.0"), DefaultPolicy);
        var repolicyed = DeploymentStack.Build("stack-a", "westeurope", Template, new StackPolicy(StackUnmanageAction.Ignore));

        Assert.StartsWith("sha256:", baseBody.PlanHash);
        AssertDistinct(baseBody.PlanHash, renamed.PlanHash, moved.PlanHash, edited.PlanHash, repolicyed.PlanHash);
    }

    private static void AssertDistinct(params string[] values)
    {
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Theory]
    [InlineData("", "StackNameInvalid")]
    [InlineData("bad name!", "StackNameInvalid")]
    [InlineData("x", null)]
    public void Build_ValidatesName(string name, string? expectedCode)
    {
        var body = DeploymentStack.Build(name, "westeurope", Template, DefaultPolicy);
        Assert.Equal(expectedCode is null, body.Diagnostics.All(d => d.Severity != ArmDiagnosticSeverity.Error));
        if (expectedCode is not null)
            Assert.Contains(expectedCode, Codes(body));
    }

    [Fact]
    public void Build_RejectsMissingLocationAndMalformedTemplate()
    {
        var noLoc = DeploymentStack.Build("s", " ", Template, DefaultPolicy);
        Assert.Contains("StackLocationMissing", Codes(noLoc));

        var badJson = DeploymentStack.Build("s", "westeurope", "{oops", DefaultPolicy);
        Assert.Contains("StackTemplateMalformed", Codes(badJson));
        // A rejected input still yields parseable bytes (the hash step runs unconditionally):
        // the omitted template leaves no dangling comma behind.
        using var parsed = JsonDocument.Parse(badJson.Json);
        Assert.False(parsed.RootElement.GetProperty("properties").TryGetProperty("template", out _));

        var notObject = DeploymentStack.Build("s", "westeurope", "[1,2]", DefaultPolicy);
        Assert.Contains("StackTemplateMalformed", Codes(notObject));

        var badParams = DeploymentStack.Build("s", "westeurope", Template, DefaultPolicy, "null");
        Assert.Contains("StackParametersMalformed", Codes(badParams));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("resourceGroup")]
    [InlineData("deployment")]
    public void Build_RefusesDeleteActionsUntilPhase13(string slot)
    {
        var policy = slot switch
        {
            "missing" => new StackPolicy(StackUnmanageAction.Delete),
            "resourceGroup" => new StackPolicy(StackUnmanageAction.Detach, StackUnmanageAction.Delete),
            _ => new StackPolicy(StackUnmanageAction.Detach, StackUnmanageAction.Detach, StackUnmanageAction.Delete),
        };

        var body = DeploymentStack.Build("s", "westeurope", Template, policy);

        Assert.False(body.Success);
        var diag = Assert.Single(body.Diagnostics, d => d.Code == "StackDeleteActionLocked");
        Assert.Contains($"actionOnUnmanage.{slot}", diag.Message);
        Assert.Contains("13", diag.Message);
    }

    [Fact]
    public void Build_EnforcesDenySettingsContract()
    {
        var emptyLocks = DeploymentStack.Build("s", "westeurope", Template,
            DefaultPolicy with { DenySettings = new StackDenySettings(true, []) });
        Assert.Contains("StackDenyLocksRequired", Codes(emptyLocks));

        var locked = DeploymentStack.Build("s", "westeurope", Template,
            DefaultPolicy with { DenySettings = new StackDenySettings(true, [StackDenyLock.NotDeletionAndChildUsage, StackDenyLock.NotDeletion]) });
        Assert.True(locked.Success, Codes(locked));
        // Locks are emitted in a stable order regardless of input order.
        Assert.Contains("""locks":["NotDeletion","NotDeletionAndChildUsage"]""", locked.Json);
    }

    [Fact]
    public void Build_EmitsArmStackShape()
    {
        var body = DeploymentStack.Build("stack-a", "westeurope", Template,
            DefaultPolicy with { Missing = StackUnmanageAction.Ignore });
        Assert.True(body.Success, Codes(body));

        using var doc = JsonDocument.Parse(body.Json);
        var root = doc.RootElement;
        Assert.Equal("stack-a", root.GetProperty("name").GetString());
        Assert.Equal("westeurope", root.GetProperty("location").GetString());
        Assert.Equal("Microsoft.Resources/deploymentStacks", root.GetProperty("type").GetString());
        Assert.Equal(DeploymentStack.ApiVersion, root.GetProperty("apiVersion").GetString());

        var actions = root.GetProperty("properties").GetProperty("policy").GetProperty("actionOnUnmanage");
        Assert.Equal("Detach", actions.GetProperty("resourceGroup").GetString());
        Assert.Equal("Detach", actions.GetProperty("deployment").GetString());
        Assert.Equal("Ignore", actions.GetProperty("missing").GetString());
        Assert.Equal("1.0.0.0", root.GetProperty("properties").GetProperty("template").GetProperty("contentVersion").GetString());
        Assert.False(root.GetProperty("properties").TryGetProperty("denySettings", out _));
    }

    [Fact]
    public void ReadManagedResources_HonestAboutEveryPayloadShape()
    {
        var (ids, ok) = DeploymentStack.ReadManagedResources(
            """{"properties":{"managedResources":[{"resourceId":"/subscriptions/s1/resourceGroups/g1/providers/Microsoft.Storage/storageAccounts/stone"},{"resourceId":" /x/y "}]}}""");
        Assert.Equal(["/subscriptions/s1/resourceGroups/g1/providers/Microsoft.Storage/storageAccounts/stone", "/x/y"], ids);
        Assert.Empty(ok);

        (ids, ok) = DeploymentStack.ReadManagedResources("""{"properties":{"managedResources":[{"resourceId":"/x"},]},"}""");
        Assert.Empty(ids);
        Assert.Contains("StackPayloadMalformed", string.Join(",", ok.Select(d => d.Code)));
        (ids, ok) = DeploymentStack.ReadManagedResources(
            """{"properties":{"managedResources":[{"resourceId":"/subs/a"},{"name":"no-id"},{"resourceId":42}]}}""");
        Assert.Equal(["/subs/a"], ids);
        Assert.Equal(2, ok.Count);
        Assert.All(ok, d => Assert.Equal("StackManagedResourceInvalid", d.Code));
        Assert.Contains("managedResources[1]", ok[0].Message);
        Assert.Contains("managedResources[2]", ok[1].Message);

        (_, var missing) = DeploymentStack.ReadManagedResources("""{"properties":{}}""");
        Assert.Contains("StackManagedResourcesMissing", missing.Select(d => d.Code));

        var (emptyIds, emptyDiags) = DeploymentStack.ReadManagedResources("""{"properties":{"managedResources":[]}}""");
        Assert.Empty(emptyIds);
        Assert.Empty(emptyDiags);
    }

    [Fact]
    public void CheckDrift_ComparesArmIdsCaseInsensitively_AndReportsEveryDivergence()
    {
        var report = DeploymentStack.CheckDrift(
            ["/SUBSCRIPTIONS/s1/resourceGroups/g1", "/subscriptions/s1/missing"],
            ["/subscriptions/s1/resourceGroups/g1", "/subscriptions/s1/untracked"]);

        Assert.True(report.IsOutOfSync);
        Assert.Equal(1, report.SyncedCount);
        Assert.Equal(1, report.MissingCount);
        Assert.Equal(1, report.UntrackedCount);
        // Stacked first keeps first-appearance casing; rows sort ordinal by the lowercased key.
        Assert.Equal(
            new[] { "/SUBSCRIPTIONS/s1/resourceGroups/g1", "/subscriptions/s1/missing", "/subscriptions/s1/untracked" }
                .OrderBy(id => id.ToLowerInvariant(), System.StringComparer.Ordinal)
                .ToList(),
            report.Rows.Select(r => r.Id).ToList());
        Assert.Equal(StackDriftKind.Synced, report.Rows.Single(r => r.Id.ToLowerInvariant().Contains("resourcegroups")).Kind);
        Assert.Equal(StackDriftKind.Missing, report.Rows.Single(r => r.Id.Contains("missing")).Kind);
        Assert.Equal(StackDriftKind.Untracked, report.Rows.Single(r => r.Id.Contains("untracked")).Kind);
    }

    [Fact]
    public void CheckDrift_InSyncStackAndNoiseAreQuiet()
    {
        var same = DeploymentStack.CheckDrift(["/a", "/b"], ["/A", " /b ", "/b"]);
        Assert.False(same.IsOutOfSync);
        Assert.Equal(2, same.SyncedCount);
        Assert.Equal(2, same.Rows.Count); // live duplicate "/b" adds no row

        var noise = DeploymentStack.CheckDrift(["", "  ", "/a"], [null!, "/a"]);
        Assert.False(noise.IsOutOfSync);
        Assert.Single(noise.Rows);
    }
}
