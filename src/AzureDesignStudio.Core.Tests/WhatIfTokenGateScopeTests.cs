using System;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>The gate binds a preview, its plan hash and its single-use token to the scope and the
/// requester that created them (D-07): another scope can neither peek at, approve nor consume them,
/// and is told the same thing it is told about an unknown token.</summary>
public class WhatIfTokenGateScopeTests
{
    private static readonly Guid ScopeA = new("a0a0a0a0-0000-4000-8000-00000000000a");
    private static readonly Guid ScopeB = new("b0b0b0b0-0000-4000-8000-00000000000b");
    private const string UserA = "11111111-1111-4111-8111-111111111111";
    private const string UserA2 = "22222222-2222-4222-8222-222222222222";
    private const string PlanHash = "sha256:plan";
    private const string StackHash = "sha256:stack-body-hash";

    private static WhatIfSummary Summary() => new()
    {
        Status = "Succeeded",
        Changes = [new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 0)],
    };

    private static string IssueDeployment(WhatIfTokenGate gate, Guid? scope, string? requester = UserA,
        string plan = PlanHash, string canonical = "{\"kind\":\"deployment\"}", string? revert = "{\"revert\":1}") =>
        gate.Issue("sub", "rg", "tpl", "prm", Summary(), canonical, plan, revert,
            requestedByUserId: requester, scopeId: scope);

    private static string IssueStack(WhatIfTokenGate gate, Guid? scope, string? requester = UserA, string plan = PlanHash) =>
        gate.IssueForStack("sub", "rg", StackHash, false, [], plan, "{\"kind\":\"stack\"}",
            requestedByUserId: requester, scopeId: scope);

    // --- an unscoped preview and an unscoped consume still pair; a scope never pairs with a missing one ---

    [Fact]
    public void IssueWithoutAScopeBehavesAsBefore()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.Issue("sub", "rg", "tpl", "prm", Summary(), "{}", PlanHash);

        Assert.True(gate.TryPeekPlan(PlanHash, out _));
        Assert.True(gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm").IsOk);
    }

    [Fact]
    public void AScopedTokenIsStaleWhenConsumedWithoutAScopeAndIsNotBurned()
    {
        // WR-01: forgetting the optional scope must fail closed, not skip the tenant check.
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = IssueDeployment(gate, ScopeA);

        var unscoped = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm");
        var unknown = gate.ValidateAndConsume("no-such-token", "sub", "rg", "tpl", "prm");

        Assert.Equal(WhatIfTokenStatus.Stale, unscoped.Status);
        Assert.Equal(unknown.Detail, unscoped.Detail);
        Assert.True(gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm", scopeId: ScopeA).IsOk, "the owning scope still consumes it");
    }

    [Fact]
    public void AScopedStackTokenIsStaleWhenConsumedWithoutAScope()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = IssueStack(gate, ScopeA);

        Assert.Equal(WhatIfTokenStatus.Stale, gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash).Status);
        Assert.True(gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash, scopeId: ScopeA).IsOk);
    }

    [Fact]
    public void TheScopeLessPeekNeverServesAScopedPreview()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        IssueDeployment(gate, ScopeA);
        IssueStack(gate, ScopeB, plan: "sha256:other");

        Assert.False(gate.TryPeekPlan(PlanHash, out _));
        Assert.False(gate.TryPeekPlan(PlanHash, out _, out _));
        Assert.False(gate.TryPeekPlan("sha256:other", out _));
    }

    // --- the scope-aware peek ---

    [Fact]
    public void ThePeekReturnsAPreviewOnlyToItsOwnScope()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        IssueDeployment(gate, ScopeA);

        Assert.True(gate.TryPeekPlan(PlanHash, ScopeA, out var canonical, out var revert, out var requester));
        Assert.Equal("{\"kind\":\"deployment\"}", canonical);
        Assert.Equal("{\"revert\":1}", revert);
        Assert.Equal(UserA, requester);
        Assert.False(gate.TryPeekPlan(PlanHash, ScopeB, out var other, out _, out var otherRequester));
        Assert.Equal(string.Empty, other);
        Assert.Null(otherRequester);
    }

    [Fact]
    public void APreviewIssuedWithoutAScopeIsInvisibleToTheScopeAwarePeek()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        IssueDeployment(gate, scope: null, requester: null);

        Assert.True(gate.TryPeekPlan(PlanHash, out _));
        Assert.False(gate.TryPeekPlan(PlanHash, ScopeA, out _, out _, out _));
    }

    [Fact]
    public void ADeploymentPreviewIsPreferredOverAStackPreviewInOneScope()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        IssueDeployment(gate, ScopeA, requester: UserA);
        IssueStack(gate, ScopeA, requester: UserA2); // newer, but a stack

        Assert.True(gate.TryPeekPlan(PlanHash, ScopeA, out var canonical, out var revert, out var requester));

        Assert.Equal("{\"kind\":\"deployment\"}", canonical);
        Assert.Equal("{\"revert\":1}", revert);
        Assert.Equal(UserA, requester);
    }

    [Fact]
    public void TheNewestPreviewWinsWithinTheSameKindAndScope()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        IssueDeployment(gate, ScopeA, requester: UserA);
        IssueDeployment(gate, ScopeA, requester: UserA2);
        IssueDeployment(gate, ScopeB, requester: "someone-else");

        Assert.True(gate.TryPeekPlan(PlanHash, ScopeA, out _, out _, out var requester));

        Assert.Equal(UserA2, requester);
    }

    [Fact]
    public void TheNewestStackPreviewWinsWhenThereIsNoDeploymentPreview()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        IssueStack(gate, ScopeA, requester: UserA);
        IssueStack(gate, ScopeA, requester: UserA2);

        Assert.True(gate.TryPeekPlan(PlanHash, ScopeA, out _, out _, out var requester));

        Assert.Equal(UserA2, requester);
    }

    [Fact]
    public void AnExpiredPreviewIsInvisibleToTheScopeAwarePeek()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new WhatIfTokenGate(TimeSpan.FromMinutes(1), () => now);
        IssueDeployment(gate, ScopeA);
        now = now.AddMinutes(2);

        Assert.False(gate.TryPeekPlan(PlanHash, ScopeA, out _, out _, out _));
    }

    // --- the scope-aware consume ---

    [Fact]
    public void ADeploymentTokenConsumedByAnotherScopeIsStaleAndNotBurned()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = IssueDeployment(gate, ScopeA);

        var foreign = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm", scopeId: ScopeB);
        var unknown = gate.ValidateAndConsume("no-such-token", "sub", "rg", "tpl", "prm", scopeId: ScopeB);

        Assert.Equal(WhatIfTokenStatus.Stale, foreign.Status);
        Assert.Equal(unknown.Status, foreign.Status);
        Assert.Equal(unknown.Detail, foreign.Detail);
        Assert.True(gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm", scopeId: ScopeA).IsOk, "the right scope still works");
        Assert.Equal(WhatIfTokenStatus.Stale, gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm", scopeId: ScopeA).Status);
    }

    [Fact]
    public void ADeploymentTokenIssuedWithoutAScopeIsStaleForAScopedConsume()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = IssueDeployment(gate, scope: null, requester: null);

        Assert.Equal(WhatIfTokenStatus.Stale, gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm", scopeId: ScopeA).Status);
        Assert.True(gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm").IsOk);
    }

    [Fact]
    public void AStackTokenConsumedByAnotherScopeIsStaleAndNotBurned()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = IssueStack(gate, ScopeA);

        var foreign = gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash, scopeId: ScopeB);
        var unknown = gate.ValidateAndConsumeForStack("no-such-token", "sub", "rg", StackHash, scopeId: ScopeB);

        Assert.Equal(WhatIfTokenStatus.Stale, foreign.Status);
        Assert.Equal(unknown.Detail, foreign.Detail);
        Assert.True(gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash, scopeId: ScopeA).IsOk);
        Assert.Equal(WhatIfTokenStatus.Stale, gate.ValidateAndConsumeForStack(token, "sub", "rg", StackHash, scopeId: ScopeA).Status);
    }

    [Fact]
    public void AForeignScopeCannotLearnThatAStackTokenIsOfTheOtherKind()
    {
        // A scope mismatch is reported before the kind or payload checks, so the answer reveals nothing.
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = IssueStack(gate, ScopeA);

        var check = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm", scopeId: ScopeB);

        Assert.Equal(WhatIfTokenStatus.Stale, check.Status);
    }

    [Fact]
    public void ADeleteBearingPreviewIsStillRefusedForItsOwnScope()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var withDelete = new WhatIfSummary
        {
            Status = "Succeeded",
            Changes = [new WhatIfChange(WhatIfChangeKind.Delete, "/x", null, null, 0)],
        };
        var token = gate.Issue("sub", "rg", "tpl", "prm", withDelete, "{}", PlanHash, scopeId: ScopeA);

        Assert.Equal(WhatIfTokenStatus.HasDelete, gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm", scopeId: ScopeA).Status);
    }
}
