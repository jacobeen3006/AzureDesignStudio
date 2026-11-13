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
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Iac;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>Deployment-stacks what-if (api 2025-07-01): request body, strict parser, offline client run.
/// Fixtures follow the deploymentStacksWhatIfResults REST reference.</summary>
public class StackWhatIfTests
{
    private const string Rg = "/subscriptions/s/resourceGroups/rg/providers/";

    private static string Result(string changes, string state = "succeeded", string? extra = null, string? properties = null) =>
        "{\"properties\":{\"provisioningState\":\"" + state + "\",\"changes\":{\"resourceChanges\":[" + changes + "]"
        + (extra is null ? "" : "," + extra) + "}" + (properties is null ? "" : "," + properties) + "}}";

    private static string Change(string id, string changeType, string certainty = "definite", string? extra = null) =>
        "{\"id\":\"" + id + "\",\"type\":\"Microsoft.Storage/storageAccounts\",\"changeType\":\"" + changeType
        + "\",\"changeCertainty\":\"" + certainty + "\"" + (extra is null ? "" : "," + extra) + "}";

    [Fact]
    public void Parse_MapsEveryChangeType()
    {
        var json = Result(string.Join(",",
            Change(Rg + "a/1", "create"), Change(Rg + "a/2", "modify"), Change(Rg + "a/3", "noChange"),
            Change(Rg + "a/4", "detach"), Change(Rg + "a/5", "DELETE"), Change(Rg + "a/6", "unsupported")));

        var summary = StackWhatIf.Parse(json);

        Assert.Equal(
            new[] { StackChangeKind.Create, StackChangeKind.Modify, StackChangeKind.NoChange,
                    StackChangeKind.Detach, StackChangeKind.Delete, StackChangeKind.Unsupported },
            summary.Changes.Select(c => c.Kind));
        Assert.Equal("succeeded", summary.ProvisioningState);
    }

    [Fact]
    public void Parse_UnknownChangeType_IsUnsupportedNotNoChange()
    {
        var summary = StackWhatIf.Parse(Result(Change(Rg + "a/1", "teleport")));
        Assert.Equal(StackChangeKind.Unsupported, Assert.Single(summary.Changes).Kind);
        Assert.NotNull(summary.IndeterminacyReason);
    }

    [Fact]
    public void Parse_DetachAndDelete_AreListed()
    {
        var summary = StackWhatIf.Parse(Result(string.Join(",",
            Change(Rg + "b/z", "delete"), Change(Rg + "b/a", "delete"), Change(Rg + "b/d", "detach"), Change(Rg + "b/c", "create"))));

        Assert.True(summary.HasDelete);
        Assert.Equal(new[] { Rg + "b/a", Rg + "b/z" }, summary.DeleteResourceIds);
        Assert.Equal(new[] { Rg + "b/d" }, summary.DetachResourceIds);
        Assert.False(StackWhatIf.Parse(Result(Change(Rg + "b/c", "create"))).HasDelete);
    }

    [Fact]
    public void Parse_ReadsManagementAndDenyStatus()
    {
        var summary = StackWhatIf.Parse(Result(
            Change(Rg + "a/1", "create", extra:
                "\"managementStatusChange\":{\"before\":\"unmanaged\",\"after\":\"managed\"},"
                + "\"denyStatusChange\":{\"before\":\"none\",\"after\":\"denyDelete\"}"),
            extra: "\"denySettingsChange\":{\"before\":{\"mode\":\"none\"},\"after\":{\"mode\":\"denyDelete\",\"applyToChildScopes\":true}}"));

        var change = Assert.Single(summary.Changes);
        Assert.Equal("Microsoft.Storage/storageAccounts", change.Type);
        Assert.Equal("unmanaged", change.ManagementBefore);
        Assert.Equal("managed", change.ManagementAfter);
        Assert.Equal("none", change.DenyBefore);
        Assert.Equal("denyDelete", change.DenyAfter);
        Assert.Equal("none", summary.DenyModeBefore);
        Assert.Equal("denyDelete", summary.DenyModeAfter);
    }

    [Fact]
    public void Parse_CleanResult_IsDeterminate()
    {
        var summary = StackWhatIf.Parse(Result(Change(Rg + "a/1", "create")));
        Assert.Null(summary.IndeterminacyReason);
        Assert.False(Assert.Single(summary.Changes).Potential);
    }

    [Fact]
    public void Parse_Unsupported_IsIndeterminate()
    {
        var summary = StackWhatIf.Parse(Result(Change(Rg + "a/1", "unsupported", extra: "\"unsupportedReason\":\"resourceTypeNotSupported\"")));
        Assert.Contains("resourceTypeNotSupported", summary.IndeterminacyReason);
        Assert.Equal("resourceTypeNotSupported", summary.Changes[0].UnsupportedReason);
    }

    [Fact]
    public void Parse_PotentialChange_IsIndeterminate()
    {
        var summary = StackWhatIf.Parse(Result(Change(Rg + "a/1", "modify", "Potential")));
        Assert.True(summary.Changes[0].Potential);
        Assert.NotNull(summary.IndeterminacyReason);
    }

    [Fact]
    public void Parse_ErrorDiagnostic_IsIndeterminate_ButWarningIsNot()
    {
        var diagnostics = "\"diagnostics\":[{\"level\":\"info\",\"code\":\"I\",\"message\":\"fyi\"},"
            + "{\"level\":\"warning\",\"code\":\"W\",\"message\":\"careful\",\"target\":\"x\"}]";
        var warned = StackWhatIf.Parse(Result(Change(Rg + "a/1", "create"), properties: diagnostics));
        Assert.Null(warned.IndeterminacyReason);
        Assert.Equal(2, warned.Diagnostics.Count);
        Assert.Contains("(x)", warned.Diagnostics[1].Message);

        var failed = StackWhatIf.Parse(Result(Change(Rg + "a/1", "create"),
            properties: "\"diagnostics\":[{\"level\":\"error\",\"code\":\"E1\",\"message\":\"boom\"}]"));
        Assert.Contains("E1", failed.IndeterminacyReason);
    }

    [Theory]
    [InlineData("initializing")]
    [InlineData("failed")]
    [InlineData("canceled")]
    public void Parse_StateNotSucceeded_IsIndeterminate(string state)
    {
        var summary = StackWhatIf.Parse(Result(Change(Rg + "a/1", "create"), state));
        Assert.Contains(state, summary.IndeterminacyReason);
    }

    [Fact]
    public void Parse_NoChangesMember_IsIndeterminate()
    {
        var summary = StackWhatIf.Parse("""{"properties":{"provisioningState":"succeeded"}}""");
        Assert.Empty(summary.Changes);
        Assert.NotNull(summary.IndeterminacyReason);
        Assert.Contains(summary.Diagnostics, d => d.Code == "StackChangesMissing");
    }

    [Fact]
    public void Parse_PropertiesError_Throws()
    {
        var error = Assert.Throws<WhatIfException>(() => StackWhatIf.Parse(
            """{"properties":{"provisioningState":"failed","error":{"code":"InvalidTemplate","message":"bad template"}}}"""));
        Assert.Equal("InvalidTemplate", error.Code);
        Assert.Contains("bad template", error.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Parse_Malformed_Throws(string json) =>
        Assert.Equal("MalformedStackWhatIf", Assert.Throws<WhatIfException>(() => StackWhatIf.Parse(json)).Code);

    private const string Template =
        """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[{"type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","name":"sa1"}]}""";

    [Fact]
    public void BuildRequest_CopiesPolicyAndTemplateFromTheStackBody()
    {
        var policy = new StackPolicy(StackUnmanageAction.Detach, DenySettings: new StackDenySettings(StackDenyMode.DenyDelete, true));
        var body = DeploymentStack.Build("ads-stack", Template, policy, parametersJson: """{"p":{"value":1}}""");
        const string id = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Resources/deploymentStacks/ads-stack";

        var request = StackWhatIf.BuildRequest(id, body.Json);

        using var sent = JsonDocument.Parse(request);
        using var original = JsonDocument.Parse(body.Json);
        var props = sent.RootElement.GetProperty("properties");
        var source = original.RootElement.GetProperty("properties");
        Assert.Equal(new[] { "properties" }, sent.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(id, props.GetProperty("deploymentStackResourceId").GetString());
        Assert.Equal("PT1H", props.GetProperty("retentionInterval").GetString());
        Assert.False(props.TryGetProperty("location", out _));
        Assert.False(sent.RootElement.TryGetProperty("location", out _));
        foreach (var member in new[] { "template", "parameters", "actionOnUnmanage", "denySettings" })
            Assert.Equal(source.GetProperty(member).GetRawText(), props.GetProperty(member).GetRawText());
    }

    [Fact]
    public void BuildRequest_LeavesOutParametersWhenTheStackHasNone_AndRefusesAMalformedBody()
    {
        var body = DeploymentStack.Build("ads-stack", Template, new StackPolicy(StackUnmanageAction.Detach));
        using var sent = JsonDocument.Parse(StackWhatIf.BuildRequest("/x", body.Json));
        Assert.False(sent.RootElement.GetProperty("properties").TryGetProperty("parameters", out _));

        Assert.Throws<ArgumentException>(() => StackWhatIf.BuildRequest("/x", "{}"));
        Assert.Throws<ArgumentException>(() => StackWhatIf.BuildRequest("/x", """{"properties":{"template":{}}}"""));
        Assert.Throws<ArgumentException>(() => StackWhatIf.BuildRequest("/x", "nope"));
    }

    [Fact]
    public void ResultName_IsStableAndValid()
    {
        var hash = "sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        var name = StackWhatIf.ResultName("ads-stack", hash);

        Assert.Equal("ads-abcdef0123456789", name);
        Assert.Equal(name, StackWhatIf.ResultName("ads-stack", hash));
        Assert.Matches(@"^[-\w\._\(\)]{1,90}$", name);
        Assert.Throws<ArgumentException>(() => StackWhatIf.ResultName("ads-stack", "sha256:xyz"));
        Assert.Throws<ArgumentException>(() => StackWhatIf.ResultName("bad name!", hash));
    }

    // ---- client, against scripted ARM ----

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public List<string> Methods { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.ToString());
            Methods.Add(request.Method.Method);
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

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, string? asyncOperation = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (asyncOperation is not null) response.Headers.TryAddWithoutValidation("Azure-AsyncOperation", asyncOperation);
        return response;
    }

    private static StackWhatIfClient Client(FakeHandler handler) =>
        new(new ArmTransport(new HttpClient(handler), new FakeCredential(), ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));

    private static (string Json, string Hash) StackBody()
    {
        var body = DeploymentStack.Build("ads-stack", Template, new StackPolicy(StackUnmanageAction.Detach));
        return (body.Json, body.PlanHash);
    }

    [Fact]
    public async Task Client_Run_PutsPollsReadsThenDeletesTheResult()
    {
        var (json, hash) = StackBody();
        var resultName = StackWhatIf.ResultName("ads-stack", hash);
        const string monitor = "https://management.azure.com/operationStatuses/op-9?api-version=2025-07-01";
        var handler = new FakeHandler((request, call) => call switch
        {
            0 => Json("""{"properties":{"provisioningState":"initializing"}}""", HttpStatusCode.Created, monitor),
            1 => Json("""{"status":"Succeeded"}"""),
            2 => Json(Result(Change(Rg + "Microsoft.Storage/storageAccounts/sa1", "create"))),
            _ => Json("", HttpStatusCode.OK),
        });

        var summary = await Client(handler).RunAsync("s", "rg", "ads-stack", json, hash);

        var url = "https://management.azure.com/subscriptions/s/resourceGroups/rg/providers/Microsoft.Resources/"
            + $"deploymentStacksWhatIfResults/{resultName}?api-version=2025-07-01";
        Assert.Equal(new[] { "PUT", "GET", "GET", "DELETE" }, handler.Methods);
        Assert.Equal(new[] { url, monitor, url, url }, handler.Uris);
        Assert.Contains("\"deploymentStackResourceId\":\"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Resources/deploymentStacks/ads-stack\"", handler.Bodies[0]);
        Assert.Contains("\"retentionInterval\":\"PT1H\"", handler.Bodies[0]);
        Assert.Equal(StackChangeKind.Create, Assert.Single(summary.Changes).Kind);
        Assert.Null(summary.IndeterminacyReason);
    }

    [Fact]
    public async Task Client_Run_UsesThePutBodyWhenItAlreadyCarriesChanges_AndIgnoresAFailingDelete()
    {
        var (json, hash) = StackBody();
        var handler = new FakeHandler((_, call) => call == 0
            ? Json(Result(Change(Rg + "a/1", "delete")))
            : Json("""{"error":{"code":"Boom","message":"cannot delete"}}""", HttpStatusCode.InternalServerError));

        var summary = await Client(handler).RunAsync("s", "rg", "ads-stack", json, hash);

        Assert.Equal(new[] { "PUT", "DELETE" }, handler.Methods);
        Assert.True(summary.HasDelete);
    }

    [Fact]
    public async Task Client_Run_ErrorResult_ThrowsButStillDeletes()
    {
        var (json, hash) = StackBody();
        var handler = new FakeHandler((_, call) => call == 0
            ? Json("""{"properties":{"provisioningState":"failed","error":{"code":"InvalidTemplate","message":"bad"}}}""")
            : Json("", HttpStatusCode.OK));

        var error = await Assert.ThrowsAsync<WhatIfException>(() => Client(handler).RunAsync("s", "rg", "ads-stack", json, hash));

        Assert.Equal("InvalidTemplate", error.Code);
        Assert.Equal(new[] { "PUT", "DELETE" }, handler.Methods);
    }
}
