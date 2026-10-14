using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Iac;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Discovery.ResourceGraph;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 11.1/11.3: what-if prediction parsing, the deploy preflight token, and the
/// approved-plan gate chain - all offline against scripted ARM responses.
/// </summary>
public class WhatIfTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? null : request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return Task.FromResult(responder(request, Uris.Count - 1));
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, string? location = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (location is not null) response.Headers.Location = new Uri(location);
        return response;
    }

    private const string ResultJson = """
        {
          "status": "Succeeded",
          "properties": {
            "changes": [
              { "changeType": "Create", "resourceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/newsa" },
              { "changeType": "Modify", "resourceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet1",
                "before": { "properties": { "subnets": [] } }, "after": { "properties": { "subnets": [{}] } },
                "delta": [ { "path": "properties.subnets", "propertyChangeType": "Array" } ] },
              { "changeType": "Delete", "resourceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Web/sites/oldapp" },
              { "changeType": "NoChange", "resourceId": "/subscriptions/s/resourceGroups/rg" },
              { "changeType": "Teleport", "resourceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Unknown/x" }
            ]
          }
        }
        """;

    // ---------- parser ----------

    [Fact]
    public void Parser_CountsEveryChangeType_UnknownNotSilentlyDropped()
    {
        var summary = WhatIfParser.Parse(ResultJson);

        Assert.Equal("Succeeded", summary.Status);
        Assert.Equal(1, summary.Creates);
        Assert.Equal(1, summary.Modifies);
        Assert.Equal(1, summary.Deletes);
        Assert.True(summary.HasDelete);
        Assert.Single(summary.DeleteResourceIds);
        Assert.Contains("oldapp", summary.DeleteResourceIds[0]);
        Assert.Equal(5, summary.Changes.Count); // NoChange + unrecognized Teleport still counted
        Assert.Contains(summary.Changes, c => c.Kind == WhatIfChangeKind.Unknown);
    }

    [Fact]
    public void Parser_KeepsPayloadAndDelta_ForModifyRow()
    {
        var modify = WhatIfParser.Parse(ResultJson).Changes.Single(c => c.Kind == WhatIfChangeKind.Modify);

        Assert.NotNull(modify.BeforeJson);
        Assert.NotNull(modify.AfterJson);
        Assert.Equal(1, modify.DeltaCount);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void Parser_MalformedDocument_ThrowsTypedNeverSilentNoChanges(string json)
    {
        var ex = Assert.Throws<WhatIfException>(() => WhatIfParser.Parse(json));
        Assert.Equal("MalformedWhatIf", ex.Code);
    }

    [Fact]
    public void Parser_MissingSections_YieldsUnknownStatusEmptyChanges()
    {
        var summary = WhatIfParser.Parse("{}");

        Assert.Equal("Unknown", summary.Status);
        Assert.Empty(summary.Changes);
        Assert.False(summary.HasDelete);
    }

    [Fact]
    public void ChangesJson_IsCompactDeterministicAndCapped()
    {
        var summary = WhatIfParser.Parse(ResultJson);
        var first = summary.ToChangesJson();
        var again = summary.ToChangesJson();
        Assert.Equal(first, again);

        using var rows = JsonDocument.Parse(first);
        var ids = rows.RootElement.EnumerateArray().Select(r => r.GetProperty("resourceId").GetString()!).ToList();
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal).ToList(), ids);

        var capped = summary.ToChangesJson(maxRows: 2);
        Assert.Equal(2, JsonDocument.Parse(capped).RootElement.GetArrayLength());
    }

    // ---------- token gate ----------

    private static WhatIfSummary SummaryWith(bool hasDelete) =>
        new()
        {
            Status = "Succeeded",
            Changes = hasDelete
                ? [new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 0), new WhatIfChange(WhatIfChangeKind.Delete, "/old", null, null, 0)]
                : [new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 0)],
        };


    [Fact]
    public void Gate_MissingToken_IsWhatIfRequired()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);

        var check = gate.ValidateAndConsume(null, "sub", "rg", "{}", "{}");

        Assert.Equal(WhatIfTokenStatus.Missing, check.Status);
        Assert.Equal("WHAT_IF_REQUIRED", check.Code);
    }

    [Fact]
    public void Gate_IssuedTokenValidatesOnce_ThenStale()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.Issue("sub", "rg", "tpl", "prm", SummaryWith(false), "{}", "sha256:" + new string('a', 64));

        Assert.True(gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm").IsOk);
        var replay = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm");
        Assert.Equal(WhatIfTokenStatus.Stale, replay.Status);
        Assert.Equal("WHAT_IF_STALE", replay.Code);
    }

    [Fact]
    public void Gate_EditedPayloadAfterReview_IsRejected_AndTokenSurvivesForRerun()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.Issue("sub", "rg", "tpl", "prm", SummaryWith(false), "{}", "sha256:" + new string('a', 64));

        var check = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "EDITED");

        Assert.Equal(WhatIfTokenStatus.PayloadChanged, check.Status);
        Assert.Equal("WHAT_IF_PAYLOAD_CHANGED", check.Code);
        Assert.True(gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm").IsOk, "failed check must not burn the token");
    }

    [Fact]
    public void Gate_ExpiresAfterTtl()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new WhatIfTokenGate(TimeSpan.FromMinutes(15), () => now);
        var token = gate.Issue("sub", "rg", "tpl", "prm", SummaryWith(false), "{}", "sha256:" + new string('a', 64));

        now = now.AddMinutes(16);
        var check = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm");

        Assert.Equal(WhatIfTokenStatus.Stale, check.Status);
        Assert.Contains("expired", check.Detail);
    }

    [Fact]
    public void Gate_DeleteBearingPrediction_IsNeverDeployable()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var token = gate.Issue("sub", "rg", "tpl", "prm", SummaryWith(true), "{}", "sha256:" + new string('a', 64));

        var check = gate.ValidateAndConsume(token, "sub", "rg", "tpl", "prm");

        Assert.Equal(WhatIfTokenStatus.HasDelete, check.Status);
        Assert.Equal("WHAT_IF_HAS_DELETE", check.Code);
        Assert.True(check.HasDelete);
        Assert.Contains("/old", check.Deletes);
    }

    [Fact]
    public void Gate_NeverIssuesTokenForIndeterminateReport()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var empty = WhatIfParser.Parse("{}"); // Status Unknown - never a green light

        var ex = Assert.Throws<WhatIfException>(() =>
            gate.Issue("sub", "rg", "tpl", "prm", empty, "{}", "sha256:" + new string('a', 64)));

        Assert.Equal("Indeterminate", ex.Code);
    }

    [Theory]
    [InlineData(WhatIfChangeKind.Bad)]
    [InlineData(WhatIfChangeKind.Unknown)]
    public void Gate_NeverIssuesTokenForUnclassifiedRows(WhatIfChangeKind kind)
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var summary = new WhatIfSummary { Status = "Succeeded", Changes = [new WhatIfChange(kind, "/x", null, null, 0)] };

        var ex = Assert.Throws<WhatIfException>(() =>
            gate.Issue("sub", "rg", "tpl", "prm", summary, "{}", "sha256:" + new string('a', 64)));

        Assert.Equal("Indeterminate", ex.Code);
    }

    [Fact]
    public void TeleportRow_MakesWholeReportIndeterminate_EndToEnd()
    {
        var summary = WhatIfParser.Parse(ResultJson); // 3 classified + NoChange + unrecognized Teleport

        Assert.NotNull(summary.IndeterminacyReason);
        Assert.Contains("Microsoft.Unknown/x", summary.IndeterminacyReason);
    }

    [Fact]
    public void ChangesRows_CarryPayloads_ForReviewInspection()
    {
        using var doc = JsonDocument.Parse(WhatIfParser.Parse(ResultJson).ToChangesJson());
        var modify = doc.RootElement.EnumerateArray()
            .Single(r => r.GetProperty("changeType").GetString() == "Modify");

        Assert.Contains("subnets", modify.GetProperty("before").GetString());
        Assert.Contains("subnets", modify.GetProperty("after").GetString());
        Assert.False(modify.GetProperty("beforeTruncated").GetBoolean());
    }

    [Fact]
    public void ChangesRows_TruncateOversizedPayloadsWithFlag()
    {
        var big = new string('x', WhatIfSummary.PayloadCap + 500);
        var summary = new WhatIfSummary
        {
            Status = "Succeeded",
            Changes = [new WhatIfChange(WhatIfChangeKind.Modify, "/x", big, null, 0)],
        };

        using var doc = JsonDocument.Parse(summary.ToChangesJson());
        var row = doc.RootElement.EnumerateArray().Single();

        Assert.Equal(WhatIfSummary.PayloadCap, row.GetProperty("before").GetString()!.Length);
        Assert.True(row.GetProperty("beforeTruncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("after").ValueKind);
    }

    [Fact]
    public void Gate_PlanApprovableOnlyWhilePreviewLive()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new WhatIfTokenGate(TimeSpan.FromMinutes(15), () => now);
        var hash = "sha256:" + new string('b', 64);
        gate.Issue("sub", "rg", "tpl", "prm", SummaryWith(false), "{\"plan\":1}", hash);

        Assert.True(gate.TryPeekPlan(hash, out var planJson));
        Assert.Equal("{\"plan\":1}", planJson);
        Assert.False(gate.TryPeekPlan("sha256:" + new string('c', 64), out _));

        now = now.AddMinutes(16);
        Assert.False(gate.TryPeekPlan(hash, out _), "expired preview is not approvable");
    }

    // ---------- approved-plan gate chain (server enforcement composition) ----------

    [Fact]
    public void PlanGate_ApprovedHashThenEditedBody_Denies()
    {
        var plan = DeployPlanArtifact.Create("sub", "rg", """{"resources":[]}""", "{}");
        var cleared = PlanApprovalGate.Evaluate("Approved", plan.CanonicalJson, plan.PlanHash, "Ada <oid>");
        Assert.True(cleared.Cleared);

        var edited = PlanApprovalGate.Evaluate("Approved", plan.CanonicalJson.Replace("[]", """[{"name":"sneaky","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"}]"""), plan.PlanHash, "Ada <oid>");
        Assert.False(edited.Cleared);
        Assert.Equal(PlanGateReason.BodyEditedAfterApproval, edited.Reason);
    }

    [Fact]
    public void PlanGate_RehashOfSamePayload_MatchesApprovedHash()
    {
        var approved = DeployPlanArtifact.Create("sub", "rg", """{"resources":[], "a":1}""", """{"p":{"value":1}}""").PlanHash;
        var redeployed = DeployPlanArtifact.Create("sub", "rg", """{ "a" : 1, "resources" : [] }""", """{ "p" : { "value" : 1 } }""").PlanHash;

        Assert.Equal(approved, redeployed);
    }

    [Fact]
    public void PlanGate_DifferentTarget_ChangesHash()
    {
        var original = DeployPlanArtifact.Create("sub", "rg", """{"resources":[]}""", "{}").PlanHash;
        var retargeted = DeployPlanArtifact.Create("sub", "rg-OTHER", """{"resources":[]}""", "{}").PlanHash;

        Assert.NotEqual(original, retargeted);
    }

    // ---------- client wire shape ----------

    [Fact]
    public async Task Client_PostsIncrementalWhatIf_AndFollowsLroLocation()
    {
        const string finalJson = """{"status":"Succeeded","properties":{"changes":[{"changeType":"Create","resourceId":"/x"}]}}""";
        var handler = new FakeHandler((req, n) => n switch
        {
            0 => Json("{}", HttpStatusCode.Accepted, "https://management.azure.com/whatifop/123?api-version=2025-04-01"),
            _ => Json(finalJson),
        });
        var client = new WhatIfClient(new ArmTransport(new HttpClient(handler), new FakeCredential(),
            ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));

        var summary = await client.WhatIfAsync("sub-1", "rg1", "westeurope", """{"resources":[]}""", """{"p":{"value":1}}""");

        Assert.Equal(1, summary.Creates);
        Assert.Contains("/subscriptions/sub-1/resourcegroups/rg1/providers/Microsoft.Resources/deployments/ads-deployment/whatIf", handler.Uris[0]);
        Assert.Contains("api-version=2025-04-01", handler.Uris[0]);
        Assert.Equal("https://management.azure.com/whatifop/123?api-version=2025-04-01", handler.Uris[1]);

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        Assert.Equal("westeurope", body.RootElement.GetProperty("location").GetString());
        Assert.Equal("Incremental", body.RootElement.GetProperty("properties").GetProperty("mode").GetString());
        Assert.Equal("resources", body.RootElement.GetProperty("properties").GetProperty("template").EnumerateObject().First().Name);
        Assert.Equal(1, body.RootElement.GetProperty("properties").GetProperty("parameters").GetProperty("p").GetProperty("value").GetInt32());
    }

    [Fact]
    public void Client_EmptyParameters_BecomesEmptyObject()
    {
        var body = WhatIfClient.BuildRequestBody("westeurope", """{"resources":[]}""", "");

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("properties").GetProperty("parameters").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("properties").GetProperty("parameters").EnumerateObject());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("[1]")]
    public void Client_MalformedTemplate_ThrowsBeforeAnyNetworkCall(string template)
    {
        Assert.Throws<WhatIfException>(() => WhatIfClient.BuildRequestBody("westeurope", template, "{}"));
    }

    [Fact]
    public async Task Client_ArmErrorBody_BecomesTypedExceptionNotNoChanges()
    {
        var handler = new FakeHandler((_, _) => Json("""{"error":{"code":"InvalidTemplateDeployment","message":"nope"}}""", HttpStatusCode.BadRequest));
        var client = new WhatIfClient(new ArmTransport(new HttpClient(handler), new FakeCredential(),
            ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));

        var ex = await Assert.ThrowsAsync<ResourceGraphException>(
            () => client.WhatIfAsync("sub", "rg", "westeurope", """{"resources":[]}""", "{}"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("InvalidTemplateDeployment", ex.Code);
    }

    [Fact]
    public async Task Client_ManagementGroupTarget_PostsUnderTheGroupScope()
    {
        const string finalJson = """{"status":"Succeeded","properties":{"changes":[]}}""";
        var handler = new FakeHandler((_, n) => n == 0 ? Json(finalJson) : Json(finalJson));
        var client = new WhatIfClient(new ArmTransport(new HttpClient(handler), new FakeCredential(),
            ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));

        await client.WhatIfAsync(DeployTargetScope.ManagementGroupScope("mg1"), "westeurope", """{"resources":[]}""", "{}");

        Assert.Contains("/providers/Microsoft.Management/managementGroups/mg1/providers/Microsoft.Resources/deployments/ads-deployment/whatIf", handler.Uris[0]);
        Assert.Contains("api-version=2025-04-01", handler.Uris[0]);
        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        Assert.Equal("westeurope", body.RootElement.GetProperty("location").GetString());
    }

    [Fact]
    public async Task Client_TenantTarget_PostsAtTheManagementEndpointRoot()
    {
        const string finalJson = """{"status":"Succeeded","properties":{"changes":[]}}""";
        var handler = new FakeHandler((_, n) => n == 0 ? Json(finalJson) : Json(finalJson));
        var client = new WhatIfClient(new ArmTransport(new HttpClient(handler), new FakeCredential(),
            ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));

        await client.WhatIfAsync(DeployTargetScope.TenantScope(), "northeurope", """{"resources":[]}""", "{}");

        Assert.Equal("https://management.azure.com/providers/Microsoft.Resources/deployments/ads-deployment/whatIf?api-version=2025-04-01", handler.Uris[0]);
    }
}
