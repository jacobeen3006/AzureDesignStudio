using System;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>Stack what-if tokens are a different kind from deployment tokens: bound to the stack
/// body hash, single use, and refused by the other kind's validate.</summary>
public class StackTokenGateTests
{
    private const string StackHash = "sha256:stack-body-hash";

    private static WhatIfSummary Summary() => new()
    {
        Status = "Succeeded",
        Changes = [new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 0)],
    };

    [Fact]
    public void StackToken_IsSingleUse()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.IssueForStack("sub", "rg", StackHash, false, []);

        Assert.True(gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash).IsOk);
        Assert.Equal(WhatIfTokenStatus.Stale, gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash).Status);
    }

    [Fact]
    public void StackToken_RefusedForAnotherStackHash_AndSurvivesForTheRightOne()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.IssueForStack("sub", "rg", StackHash, false, []);

        var wrongHash = gate.ValidateAndConsumeForStack(token, "sub", "rg", "sha256:other");
        var wrongGroup = gate.ValidateAndConsumeForStack(token, "sub", "other-rg", StackHash);

        Assert.Equal(WhatIfTokenStatus.PayloadChanged, wrongHash.Status);
        Assert.Equal(WhatIfTokenStatus.PayloadChanged, wrongGroup.Status);
        Assert.True(gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash).IsOk, "a failed check must not burn the token");
    }

    [Fact]
    public void StackToken_RefusedByDeploymentValidate()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.IssueForStack("sub", "rg", StackHash, false, []);

        var check = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm");

        Assert.Equal(WhatIfTokenStatus.PayloadChanged, check.Status);
        Assert.True(gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash).IsOk, "the refusal must not burn the token");
    }

    [Fact]
    public void DeploymentToken_RefusedByStackValidate()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.Issue("sub", "rg", "tpl", "prm", Summary(), "{}", "sha256:" + new string('a', 64));

        var check = gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash);

        Assert.Equal(WhatIfTokenStatus.PayloadChanged, check.Status);
        Assert.True(gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm").IsOk, "the refusal must not burn the token");
    }

    [Fact]
    public void StackToken_Expires()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new WhatIfTokenGate(TimeSpan.FromMinutes(15), () => now);
        var token = gate.IssueForStack("sub", "rg", StackHash, false, []);

        now = now.AddMinutes(16);
        var check = gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash);

        Assert.Equal(WhatIfTokenStatus.Stale, check.Status);
        Assert.Contains("expired", check.Detail);
    }

    [Fact]
    public void StackToken_WithDelete_IsNeverDeployable_AndMissingTokenIsRequired()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.IssueForStack("sub", "rg", StackHash, true, ["/old"]);

        var check = gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash);

        Assert.Equal(WhatIfTokenStatus.HasDelete, check.Status);
        Assert.Equal(["/old"], check.Deletes);
        Assert.Equal(WhatIfTokenStatus.Missing, gate.ValidateAndConsumeForStack(null, "sub", "rg", StackHash).Status);
    }

    [Fact]
    public void StackToken_MakesTheDeploymentPlanApprovable_AndTheDeploymentPreviewWinsThePeek()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var plan = "sha256:" + new string('b', 64);
        gate.IssueForStack("sub", "rg", StackHash, false, [], plan, "{\"stack\":true}");
        Assert.True(gate.TryPeekPlan(plan, out var onlyStack));
        Assert.Equal("{\"stack\":true}", onlyStack);

        gate.Issue("sub", "rg", "tpl", "prm", Summary(), "{\"plain\":true}", plan, "{\"revert\":1}");
        Assert.True(gate.TryPeekPlan(plan, out var both, out var revert));
        Assert.Equal("{\"plain\":true}", both);
        Assert.Equal("{\"revert\":1}", revert);
    }
}
