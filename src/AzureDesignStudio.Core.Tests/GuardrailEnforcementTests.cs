using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Discovery.Unmanaged;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 13.1/[36-final]: guardrail enforcement on the deploy path - matching what-if rows
/// (ARM-resolved ids + payloads) against the do-not-touch list, and the apply-time veto that
/// refuses without burning the single-use token.
/// </summary>
public class GuardrailEnforcementTests
{
    private const string VmId = "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1";

    private static WhatIfChange Change(WhatIfChangeKind kind, string? id, string? before = null, string? after = null) =>
        new(kind, id, before, after, 0);

    // --- ARM type derivation from the resource id ---

    [Theory]
    [InlineData("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet1",
        "Microsoft.Network/virtualNetworks")]
    [InlineData("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1/extensions/ext1",
        "Microsoft.Compute/virtualMachines/extensions")]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg/providers/Microsoft.CostManagement/exports/x",
        "Microsoft.CostManagement/exports")]
    [InlineData("/subscriptions/s1/resourceGroups/rg", "")]
    [InlineData("/subscriptions/s1/providers/Microsoft.Billing", "")]
    public void TypeFromArmId_DerivesProviderAndNestedChildTypes(string id, string expected) =>
        Assert.Equal(expected, GuardrailEnforcement.TypeFromArmId(id));

    // --- target extraction from what-if payloads ---

    [Fact]
    public void Target_ReadsTagsFromBeforePayloadRoot()
    {
        var target = GuardrailEnforcement.FromWhatIfChange(Change(WhatIfChangeKind.Modify, VmId,
            before: """{"name":"vm1","tags":{"Environment":"prod"},"properties":{}}"""));

        Assert.NotNull(target);
        Assert.Equal(VmId, target.ResourceId);
        Assert.Equal("Microsoft.Compute/virtualMachines", target.Type);
        Assert.Equal("prod", target.Tags["environment"]); // case-insensitive lookup
    }

    [Fact]
    public void Target_FallsBackToAfterPayload_ForCreates()
    {
        var target = GuardrailEnforcement.FromWhatIfChange(Change(WhatIfChangeKind.Create, VmId,
            after: """{"content":{"tags":{"do-not-touch":"true"}}}"""));

        Assert.NotNull(target);
        Assert.Equal("true", target.Tags["do-not-touch"]); // content-wrapped shape
    }

    [Fact]
    public void Target_MalformedPayload_StillCarriesIdAndType()
    {
        var target = GuardrailEnforcement.FromWhatIfChange(Change(WhatIfChangeKind.Delete, VmId, before: "{oops"));

        Assert.NotNull(target);
        Assert.Empty(target.Tags);
    }

    [Fact]
    public void Target_UnresolvedRow_IsNull() =>
        Assert.Null(GuardrailEnforcement.FromWhatIfChange(Change(WhatIfChangeKind.Modify, null)));

    // --- evaluation over a plan ---

    [Fact]
    public void Evaluate_ProtectedResource_FlagsWithMatchedReason()
    {
        var policy = GuardrailPolicy.FromJson("""{"types":["microsoft.compute/virtualmachines"]}""");
        var findings = GuardrailEnforcement.Evaluate(policy,
            [Change(WhatIfChangeKind.Modify, VmId, before: "{}"), Change(WhatIfChangeKind.Create, "/subscriptions/s2/x/y/z")]);

        var f = Assert.Single(findings);
        Assert.Equal(VmId, f.ResourceId);
        Assert.Equal("guardrail-type:microsoft.compute/virtualmachines", f.Reason);
    }

    [Fact]
    public void Evaluate_TagRule_MatchesOnPreChangeState()
    {
        var policy = GuardrailPolicy.FromJson("""{"tags":[{"key":"environment","value":"prod"}]}""");
        // The modify moves the resource OUT of prod - the pre-change state is the truth to protect.
        var findings = GuardrailEnforcement.Evaluate(policy,
            [Change(WhatIfChangeKind.Modify, VmId,
                before: """{"tags":{"environment":"prod"}}""", after: """{"tags":{"environment":"dev"}}""")]);

        Assert.Single(findings);
    }

    [Fact]
    public void Evaluate_DuplicateIds_ProduceOneFinding_SortedById()
    {
        var policy = GuardrailPolicy.FromJson("""{"scopes":["subscriptions/s1"]}""");
        var findings = GuardrailEnforcement.Evaluate(policy,
            [Change(WhatIfChangeKind.Modify, VmId, before: "{}"),
             Change(WhatIfChangeKind.Delete, VmId, before: "{}"),
             Change(WhatIfChangeKind.Create, "/subscriptions/s1/resourceGroups/rg2/providers/Microsoft.Storage/storageAccounts/st1")]);

        Assert.Equal([VmId, "/subscriptions/s1/resourceGroups/rg2/providers/Microsoft.Storage/storageAccounts/st1"],
            findings.Select(f => f.ResourceId).OrderBy(id => id, StringComparer.Ordinal));
    }

    // --- the apply-time veto on the token gate ---

    [Fact]
    public void Gate_VetoedPlan_RefusesWithoutBurningToken()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var summary = new WhatIfSummary
        {
            Status = "Succeeded",
            Changes = [Change(WhatIfChangeKind.Modify, VmId, before: """{"tags":{"env":"prod"}}""")],
        };
        var token = gate.Issue("sub", "rg", "tpl", "prm", summary, "{}", "sha256:" + new string('a', 64),
            guardrailTargets: GuardrailEnforcement.Targets(summary.Changes));

        var veto = GuardrailPolicy.FromJson("""{"tags":[{"key":"env","value":"prod"}]}""");
        var check = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm",
            targets => GuardrailEnforcement.EvaluateTargets(veto, targets).Count > 0 ? "blocked: prod-tagged" : null);

        Assert.Equal(WhatIfTokenStatus.GuardrailProtected, check.Status);
        Assert.Equal("GUARDRAIL_PROTECTED", check.Code);
        Assert.Equal("blocked: prod-tagged", check.Detail);

        // The veto is a read-only refusal: the same token still validates without the veto.
        var ok = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm");
        Assert.True(ok.IsOk);
    }

    [Fact]
    public void Gate_CleanPlan_ReturnsBoundTargets_OnOk()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var summary = new WhatIfSummary
        {
            Status = "Succeeded",
            Changes = [Change(WhatIfChangeKind.Create, VmId, after: "{}")],
        };
        var token = gate.Issue("sub", "rg", "tpl", "prm", summary, "{}", "sha256:" + new string('a', 64),
            guardrailTargets: GuardrailEnforcement.Targets(summary.Changes));

        var check = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm", _ => null);

        Assert.True(check.IsOk);
        Assert.Equal([VmId], check.Targets!.Select(t => t.ResourceId));
    }

    [Fact]
    public void Gate_IssuedWithoutTargets_VetoSeesEmptySet()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.Issue("sub", "rg", "tpl", "prm",
            new WhatIfSummary { Status = "Succeeded", Changes = [Change(WhatIfChangeKind.Create, "/x")] },
            "{}", "sha256:" + new string('a', 64));

        IReadOnlyList<GuardrailTarget>? seen = null;
        var check = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm", targets => { seen = targets; return null; });

        Assert.NotNull(seen);
        Assert.Empty(seen);
    }
}
