using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 11.2 - per-resource apply state from ARM deployment operations: mapping, folding, Unknown honesty.</summary>
public class DeploymentOperationsParserTests
{
    private static string Payload(params string[] operations) =>
        $$"""{ "value": [{{string.Join(",", operations)}}] }""";

    private static string Operation(string type, string name, string display, string? timestamp = null, string statusCode = "OK")
    {
        var timestampPart = timestamp is null ? "" : ",\"timestamp\":\"" + timestamp + "\"";
        return "{\"displayStatus\":{\"display\":\"" + display + "\"},\"targetResource\":{\"resourceType\":\"" + type
            + "\",\"resourceName\":\"" + name + "\"},\"properties\":{\"statusCode\":\"" + statusCode + "\"}" + timestampPart + "}";
    }

    [Fact]
    public void DisplayStatuses_MapToResourceStates()
    {
        var (operations, diagnostics) = DeploymentOperationsParser.Parse(Payload(
            Operation("Microsoft.Storage/storageAccounts", "created1", "Created"),
            Operation("Microsoft.Network/virtualNetworks", "running1", "Started"),
            Operation("Microsoft.Web/sites", "ok1", "Succeeded", statusCode: "Created"),
            Operation("Microsoft.Compute/virtualMachines", "bad1", "Failed", statusCode: "Conflict"),
            Operation("Microsoft.Network/virtualNetworks", "gone1", "Canceled")));

        Assert.Empty(diagnostics);
        Assert.Equal(
            [("microsoft.compute/virtualmachines/bad1", ResourceApplyState.Failed),
             ("microsoft.network/virtualnetworks/gone1", ResourceApplyState.Canceled),
             ("microsoft.network/virtualnetworks/running1", ResourceApplyState.Running),
             ("microsoft.storage/storageaccounts/created1", ResourceApplyState.Accepted),
             ("microsoft.web/sites/ok1", ResourceApplyState.Succeeded)],
            operations.Select(o => (o.ResourceKey, o.State)).ToList());
        Assert.Equal("Conflict", operations.Single(o => o.State == ResourceApplyState.Failed).StatusCode);
    }

    [Fact]
    public void NewestTimestampWinsPerResource()
    {
        var (operations, _) = DeploymentOperationsParser.Parse(Payload(
            Operation("Microsoft.Storage/storageAccounts", "sa1", "Failed", "2026-10-14T10:00:00Z", statusCode: "Conflict"),
            Operation("Microsoft.Storage/storageAccounts", "sa1", "Running", "2026-10-14T10:05:00Z", statusCode: "Accepted")));

        var folded = Assert.Single(operations);
        Assert.Equal(ResourceApplyState.Running, folded.State);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 10, 5, 0, TimeSpan.Zero), folded.TimestampUtc);
    }

    [Fact]
    public void UnrecognizedDisplay_StaysUnknownWithWarning()
    {
        var (operations, diagnostics) = DeploymentOperationsParser.Parse(Payload(
            Operation("Microsoft.Storage/storageAccounts", "sa1", "RebootingSomewhere")));

        Assert.Equal(ResourceApplyState.Unknown, Assert.Single(operations).State);
        Assert.Contains(diagnostics, d => d.Code == "UnrecognizedOperationState" && d.Severity == ArmDiagnosticSeverity.Warning);
    }

    [Fact]
    public void DeploymentLevelOperations_AreSkipped()
    {
        var (operations, _) = DeploymentOperationsParser.Parse(
            """{"value":[{"displayStatus":{"display":"Succeeded"},"properties":{"statusCode":"OK"}},{"displayStatus":{"display":"Succeeded"},"targetResource":{"resourceType":"Microsoft.Storage/storageAccounts","resourceName":"sa1"},"properties":{"statusCode":"OK"}}]}""");

        Assert.Equal("microsoft.storage/storageaccounts/sa1", Assert.Single(operations).ResourceKey);
    }

    [Fact]
    public void EntriesWithoutTimestamps_FoldInPayloadOrder()
    {
        var (operations, _) = DeploymentOperationsParser.Parse(Payload(
            Operation("Microsoft.Storage/storageAccounts", "sa1", "Running"),
            Operation("Microsoft.Storage/storageAccounts", "sa1", "Succeeded")));

        Assert.Equal(ResourceApplyState.Succeeded, Assert.Single(operations).State);
    }

    [Fact]
    public void UntimestampedEntry_DoesNotDateAnOlderTimestampedOne()
    {
        var (operations, _) = DeploymentOperationsParser.Parse(Payload(
            Operation("Microsoft.Storage/storageAccounts", "sa1", "Running", "2026-10-14T10:00:00Z"),
            Operation("Microsoft.Storage/storageAccounts", "sa1", "Failed")));

        Assert.Equal(ResourceApplyState.Running, Assert.Single(operations).State);
    }

    [Fact]
    public void InvalidPayloads_ProduceDiagnosticsNotExceptions()
    {
        var (garbageOperations, garbageDiagnostics) = DeploymentOperationsParser.Parse("not json");
        Assert.Empty(garbageOperations);
        Assert.Contains(garbageDiagnostics, d => d.Code == "InvalidOperationsPayload" && d.Severity == ArmDiagnosticSeverity.Error);

        var (shapelessOperations, shapelessDiagnostics) = DeploymentOperationsParser.Parse("""{"value":true}""");
        Assert.Empty(shapelessOperations);
        Assert.Contains("InvalidOperationsPayload", shapelessDiagnostics.Select(d => d.Code));
    }

    [Fact]
    public void OutputIsOrdinalStableAndIdempotent()
    {
        var payload = Payload(
            Operation("Microsoft.Web/sites", "zeta", "Succeeded"),
            Operation("Microsoft.Storage/storageAccounts", "alpha", "Succeeded"));

        var first = DeploymentOperationsParser.Parse(payload).Operations.Select(o => o.ResourceKey).ToList();
        var second = DeploymentOperationsParser.Parse(payload).Operations.Select(o => o.ResourceKey).ToList();

        Assert.Equal(first, second);
        Assert.Equal(["microsoft.storage/storageaccounts/alpha", "microsoft.web/sites/zeta"], first);
    }

    [Fact]
    public void ParsePages_FoldsAcrossPagesAndReportsMalformedEntry()
    {
        var (operations, diagnostics) = DeploymentOperationsParser.ParsePages(new[]
        {
            Operation("Microsoft.Storage/storageAccounts", "sa1", "Running", "2026-10-14T10:00:00Z"),
            "not json",
            Operation("Microsoft.Storage/storageAccounts", "sa1", "Succeeded", "2026-10-14T10:01:00Z"),
        });

        Assert.Equal(ResourceApplyState.Succeeded, Assert.Single(operations).State);
        Assert.Contains(diagnostics, d => d.Code == "InvalidOperationsPayload" && d.Severity == ArmDiagnosticSeverity.Error);
    }
}
