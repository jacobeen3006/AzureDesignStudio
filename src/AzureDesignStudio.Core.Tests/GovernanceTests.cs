using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Governance;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Wave B3: governance findings from the Resource Graph tables <c>advisorresources</c>,
/// <c>policyresources</c> and <c>securityresources</c>. The queries keep <c>$skipToken</c>
/// paging working (string columns only, ordered by id, no take/limit); the normalizer turns
/// one projected objectArray row into a finding, or null for a row it cannot use - it never
/// throws. Offline only: row fixtures mirror the projected shape.
/// </summary>
public class GovernanceTests
{
    private const string SubA = "00000000-0000-0000-0000-0000000000a1";
    private const string SubB = "00000000-0000-0000-0000-0000000000b2";
    private const string Vm = $"/subscriptions/{SubA}/resourceGroups/rg-app/providers/Microsoft.Compute/virtualMachines/vm1";
    private const string Storage = $"/subscriptions/{SubA}/resourceGroups/rg-app/providers/Microsoft.Storage/storageAccounts/stapp";
    private const string Assignment = $"/subscriptions/{SubA}/providers/Microsoft.Authorization/policyAssignments/deny-public-ip";
    private const string Definition = "/providers/Microsoft.Authorization/policyDefinitions/6c112d4e-5bc7-47ae-a041-ea2d9dccd749";
    private const string AssessmentId = Vm + "/providers/Microsoft.Security/assessments/d57a4221-a804-52ca-3dea-768284f06bb7";

    /// <summary>A column value that removes the column from the row.</summary>
    private const string Absent = "\u0000absent";

    private static readonly string[] Subs = [SubA, SubB];
    private const string SubFilter = $" | where subscriptionId in ('{SubA}','{SubB}') | ";

    // ---------- queries ----------

    [Fact]
    public void AdvisorQuery_ReadsRecommendations_StringColumnsOrderedById()
    {
        var q = GovernanceQueries.Advisor(Subs);

        AssertPageableArgQuery(q, "advisorresources", "microsoft.advisor/recommendations");
        Assert.Equal(
            "advisorresources | where type =~ 'microsoft.advisor/recommendations'" + SubFilter
            + "project id, subscriptionId, resourceId = tostring(properties.resourceMetadata.resourceId),"
            + " source = tostring(properties.resourceMetadata.source), category = tostring(properties.category),"
            + " impact = tostring(properties.impact), problem = tostring(properties.shortDescription.problem),"
            + " solution = tostring(properties.shortDescription.solution), status = tostring(properties.recommendationStatus),"
            + " suppressions = tostring(properties.suppressionIds) | order by id asc",
            q);
    }

    [Fact]
    public void PolicyQuery_ReadsNonCompliantStatesOnly_StringColumnsOrderedById()
    {
        var q = GovernanceQueries.Policy(Subs);

        AssertPageableArgQuery(q, "policyresources", "microsoft.policyinsights/policystates");
        Assert.Contains("| where tostring(properties.complianceState) =~ 'NonCompliant' |", q);
        Assert.Equal(
            "policyresources | where type =~ 'microsoft.policyinsights/policystates'" + SubFilter
            + "where tostring(properties.complianceState) =~ 'NonCompliant'"
            + " | project id, subscriptionId, resourceId = tostring(properties.resourceId),"
            + " assignmentId = tostring(properties.policyAssignmentId), assignmentName = tostring(properties.policyAssignmentName),"
            + " definitionId = tostring(properties.policyDefinitionId), referenceId = tostring(properties.policyDefinitionReferenceId)"
            + " | order by id asc",
            q);
    }

    [Fact]
    public void DefenderAssessmentsQuery_ReadsUnhealthyAssessmentsOnly_StringColumnsOrderedById()
    {
        var q = GovernanceQueries.DefenderAssessments(Subs);

        AssertPageableArgQuery(q, "securityresources", "microsoft.security/assessments");
        Assert.Contains("| where tostring(properties.status.code) =~ 'Unhealthy' |", q);
        Assert.Equal(
            "securityresources | where type =~ 'microsoft.security/assessments'" + SubFilter
            + "where tostring(properties.status.code) =~ 'Unhealthy'"
            + " | project id, subscriptionId, targetId = tostring(coalesce(properties.resourceDetails.Id, properties.resourceDetails.id)),"
            + " name = tostring(properties.displayName), severity = tostring(properties.metadata.severity)"
            + " | order by id asc",
            q);
    }

    [Fact]
    public void DefenderPlansQuery_ReadsPricingTiers_StringColumnsOrderedById()
    {
        var q = GovernanceQueries.DefenderPlans(Subs);

        AssertPageableArgQuery(q, "securityresources", "microsoft.security/pricings");
        Assert.Equal(
            "securityresources | where type =~ 'microsoft.security/pricings'" + SubFilter
            + "project id, subscriptionId, plan = tostring(name), tier = tostring(properties.pricingTier) | order by id asc",
            q);
    }

    [Fact]
    public void Sources_NameEveryGovernanceSource()
    {
        Assert.Equal(new[] { "advisor", "policy", "defender" }, GovernanceSources.All);
        Assert.Equal("advisor", GovernanceSources.Advisor);
        Assert.Equal("policy", GovernanceSources.Policy);
        Assert.Equal("defender", GovernanceSources.Defender);
    }

    [Fact]
    public void Caveats_SayZeroFindingsIsNotProofOfNone()
    {
        var caveats = GovernanceCaveats.All;

        Assert.Equal(4, caveats.Count);
        Assert.Contains(caveats, c => c.Contains("zero findings is not proof of none", StringComparison.Ordinal));
        Assert.Contains(caveats, c => c.Contains("about 15 minutes", StringComparison.Ordinal) && c.Contains("every 24 hours", StringComparison.Ordinal));
        Assert.Contains(caveats, c => c.Contains("depend on which plans are on", StringComparison.Ordinal));
        Assert.Contains(caveats, c => c.Contains("dismissed or postponed recommendations may still be listed", StringComparison.Ordinal));
        Assert.All(caveats, c => Assert.EndsWith(".", c));
    }

    // ---------- Advisor ----------

    [Fact]
    public void Advisor_MapsImpactCategoryProblemAndSolution()
    {
        var finding = GovernanceNormalizer.FromAdvisorRow(AdvisorRow());

        Assert.NotNull(finding);
        Assert.Equal(GovernanceSource.Advisor, finding!.Source);
        Assert.Equal(Vm + "/providers/Microsoft.Advisor/recommendations/1b9a8c2d-0000-0000-0000-000000000001", finding.RecordId);
        Assert.Equal(Vm, finding.ResourceId);
        Assert.Equal(SubA, finding.SubscriptionId);
        Assert.Equal(GovernanceSeverity.High, finding.Severity);
        Assert.Equal("HighAvailability", finding.Category);
        Assert.Equal("Enable Backups on your virtual machines", finding.Title);
        Assert.Equal("Protect your data from accidental deletion with Azure Backup", finding.Detail);
        Assert.Null(finding.PolicyAssignmentId);
        Assert.Null(finding.PolicyDefinitionId);
    }

    [Theory]
    [InlineData("High", GovernanceSeverity.High)]
    [InlineData("high", GovernanceSeverity.High)]
    [InlineData("Medium", GovernanceSeverity.Medium)]
    [InlineData("LOW", GovernanceSeverity.Low)]
    [InlineData("", GovernanceSeverity.Medium)]
    [InlineData("Critical", GovernanceSeverity.Medium)]
    [InlineData("2", GovernanceSeverity.Medium)]
    [InlineData(Absent, GovernanceSeverity.Medium)]
    [InlineData(null, GovernanceSeverity.Medium)]
    public void Advisor_SeverityFromImpact_ElseMedium(string? impact, GovernanceSeverity expected)
    {
        var finding = GovernanceNormalizer.FromAdvisorRow(AdvisorRow(("impact", impact)));

        Assert.Equal(expected, finding!.Severity);
    }

    [Fact]
    public void Advisor_WithoutProblemOrSolution_HasTheDefaultTitleAndNoDetail()
    {
        var finding = GovernanceNormalizer.FromAdvisorRow(AdvisorRow(("problem", ""), ("solution", Absent)));

        Assert.Equal("Advisor recommendation", finding!.Title);
        Assert.Null(finding.Detail);
    }

    [Theory]
    [InlineData("status", "Dismissed")]
    [InlineData("status", "postponed")]
    [InlineData("status", "COMPLETED")]
    [InlineData("suppressions", """["0f1c3c6e-8a9b-4d5e-9f00-112233445566"]""")]
    [InlineData("source", $"/subscriptions/{SubA}/providers/Microsoft.Security/assessments/d57a4221-a804-52ca-3dea-768284f06bb7")]
    [InlineData("source", $"/subscriptions/{SubA}/resourcegroups/rg-app/providers/microsoft.security/assessments/d57a4221-a804-52ca-3dea-768284f06bb7")]
    public void Advisor_SkipsDefenderSourcedDismissedAndSuppressed(string column, string value)
    {
        Assert.Null(GovernanceNormalizer.FromAdvisorRow(AdvisorRow((column, value))));
    }

    [Theory]
    [InlineData("status", "New")]
    [InlineData("status", "")]
    [InlineData("status", Absent)]
    [InlineData("suppressions", "[]")]
    [InlineData("suppressions", "")]
    [InlineData("suppressions", Absent)]
    [InlineData("suppressions", null)]
    [InlineData("source", "")]
    public void Advisor_KeepsOpenUnsuppressedRecommendations(string column, string? value)
    {
        Assert.NotNull(GovernanceNormalizer.FromAdvisorRow(AdvisorRow((column, value))));
    }

    // ---------- Policy ----------

    [Fact]
    public void Policy_MapsAssignmentDefinitionAndReference()
    {
        var finding = GovernanceNormalizer.FromPolicyRow(PolicyRow());

        Assert.NotNull(finding);
        Assert.Equal(GovernanceSource.Policy, finding!.Source);
        Assert.Equal($"/subscriptions/{SubA}/providers/Microsoft.PolicyInsights/policyStates/latest/1", finding.RecordId);
        Assert.Equal(Storage, finding.ResourceId);
        Assert.Equal(SubA, finding.SubscriptionId);
        Assert.Equal(GovernanceSeverity.Medium, finding.Severity);
        Assert.Equal("Policy", finding.Category);
        Assert.Equal("Deny public IP addresses", finding.Title);
        Assert.Equal("storage-public-access", finding.Detail);
        Assert.Equal(Assignment, finding.PolicyAssignmentId);
        Assert.Equal(Definition, finding.PolicyDefinitionId);
    }

    [Fact]
    public void Policy_TitleFallsBackToTheAssignmentIdsLastSegment_NoReferenceNoDetail()
    {
        var finding = GovernanceNormalizer.FromPolicyRow(PolicyRow(("assignmentName", ""), ("referenceId", Absent)));

        Assert.Equal("deny-public-ip", finding!.Title);
        Assert.Null(finding.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Absent)]
    [InlineData(null)]
    public void Policy_RecordIdFallback(string? id)
    {
        var finding = GovernanceNormalizer.FromPolicyRow(PolicyRow(("id", id)));

        Assert.Equal(
            "policy:" + $"{Storage}|{Assignment}|{Definition}|storage-public-access".ToLowerInvariant(),
            finding!.RecordId);
    }

    [Fact]
    public void Policy_RecordIdFallback_KeepsMissingPartsEmpty()
    {
        var finding = GovernanceNormalizer.FromPolicyRow(PolicyRow(("id", ""), ("definitionId", ""), ("referenceId", Absent)));

        Assert.Equal("policy:" + $"{Storage}|{Assignment}||".ToLowerInvariant(), finding!.RecordId);
        Assert.Null(finding.PolicyDefinitionId);
    }

    // ---------- Defender ----------

    [Fact]
    public void Defender_MapsTargetSeverityAndName()
    {
        var target = Vm.Replace("/subscriptions/", "/SUBSCRIPTIONS/", StringComparison.Ordinal);
        var finding = GovernanceNormalizer.FromDefenderRow(DefenderRow(("targetId", target)));

        Assert.NotNull(finding);
        Assert.Equal(GovernanceSource.Defender, finding!.Source);
        Assert.Equal(AssessmentId, finding.RecordId);
        Assert.Equal(target, finding.ResourceId);
        Assert.Equal(SubA, finding.SubscriptionId);
        Assert.Equal(GovernanceSeverity.High, finding.Severity);
        Assert.Equal("Security", finding.Category);
        Assert.Equal("Machines should have vulnerability findings resolved", finding.Title);
        Assert.Null(finding.Detail);
        Assert.Null(finding.PolicyAssignmentId);
        Assert.Null(finding.PolicyDefinitionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Absent)]
    [InlineData(null)]
    [InlineData("arn:aws:ec2:us-east-1:123456789012:instance/i-0abc")]
    public void Defender_TargetFromAssessmentId_WhenResourceDetailsMissing(string? targetId)
    {
        var lowerMarker = Vm + "/providers/microsoft.security/assessments/d57a4221-a804-52ca-3dea-768284f06bb7";
        var finding = GovernanceNormalizer.FromDefenderRow(DefenderRow(("targetId", targetId), ("id", lowerMarker)));

        Assert.Equal(Vm, finding!.ResourceId);
        Assert.Equal(lowerMarker, finding.RecordId);
    }

    [Theory]
    [InlineData("Low", GovernanceSeverity.Low)]
    [InlineData("medium", GovernanceSeverity.Medium)]
    [InlineData("High", GovernanceSeverity.High)]
    [InlineData("", GovernanceSeverity.Medium)]
    [InlineData("Informational", GovernanceSeverity.Medium)]
    public void Defender_SeverityFromAssessmentMetadata_ElseMedium(string severity, GovernanceSeverity expected)
    {
        Assert.Equal(expected, GovernanceNormalizer.FromDefenderRow(DefenderRow(("severity", severity)))!.Severity);
    }

    // ---------- rows the normalizer cannot use ----------

    [Fact]
    public void RowsWithoutAResourceId_AreNull()
    {
        Assert.Null(GovernanceNormalizer.FromAdvisorRow(AdvisorRow(("resourceId", ""))));
        Assert.Null(GovernanceNormalizer.FromAdvisorRow(AdvisorRow(("resourceId", Absent))));
        Assert.Null(GovernanceNormalizer.FromPolicyRow(PolicyRow(("resourceId", "  "))));
        Assert.Null(GovernanceNormalizer.FromPolicyRow(PolicyRow(("resourceId", null))));
        // no usable target and a row id that names no assessment
        Assert.Null(GovernanceNormalizer.FromDefenderRow(DefenderRow(("targetId", ""), ("id", Vm))));
        Assert.Null(GovernanceNormalizer.FromDefenderRow(DefenderRow(("targetId", ""), ("id", "/providers/Microsoft.Security/assessments/x"))));
        Assert.Null(GovernanceNormalizer.FromDefenderRow(DefenderRow(("targetId", Absent), ("id", Absent))));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"id":"x"}]""")]
    [InlineData("42")]
    [InlineData("\"row\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("""{"id":7,"subscriptionId":3,"resourceId":{"a":1},"targetId":[1],"plan":5,"tier":true,"problem":[],"assignmentName":{}}""")]
    public void MalformedRows_AreNull_NeverThrow(string json)
    {
        var row = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Null(GovernanceNormalizer.FromAdvisorRow(row));
        Assert.Null(GovernanceNormalizer.FromPolicyRow(row));
        Assert.Null(GovernanceNormalizer.FromDefenderRow(row));
        Assert.Null(GovernanceNormalizer.FromPlanRow(row));
    }

    [Fact]
    public void AnUndefinedRow_IsNull_NeverThrows()
    {
        var row = default(JsonElement);

        Assert.Null(GovernanceNormalizer.FromAdvisorRow(row));
        Assert.Null(GovernanceNormalizer.FromPolicyRow(row));
        Assert.Null(GovernanceNormalizer.FromDefenderRow(row));
        Assert.Null(GovernanceNormalizer.FromPlanRow(row));
    }

    [Fact]
    public void UnreadableStrings_ReadAsMissing_NeverThrow()
    {
        // A lone surrogate escape parses as JSON, but JsonElement.GetString() throws on it.
        const string Lone = "\\uD800";
        var unusable = JsonSerializer.Deserialize<JsonElement>(
            $$"""{"id":"{{Lone}}","subscriptionId":"{{Lone}}","resourceId":"{{Lone}}","targetId":"{{Lone}}","plan":"{{Lone}}","tier":"{{Lone}}"}""");

        Assert.Null(GovernanceNormalizer.FromAdvisorRow(unusable));
        Assert.Null(GovernanceNormalizer.FromPolicyRow(unusable));
        Assert.Null(GovernanceNormalizer.FromDefenderRow(unusable));
        Assert.Null(GovernanceNormalizer.FromPlanRow(unusable));

        var advisor = GovernanceNormalizer.FromAdvisorRow(JsonSerializer.Deserialize<JsonElement>(
            $$"""{"id":"rec-1","resourceId":"{{Vm}}","problem":"{{Lone}}","status":"{{Lone}}","suppressions":"{{Lone}}","source":"{{Lone}}"}"""));
        Assert.NotNull(advisor);
        Assert.Equal("Advisor recommendation", advisor!.Title);
        Assert.Equal(SubA, advisor.SubscriptionId);
    }

    [Fact]
    public void MistypedOptionalColumns_ReadAsMissing()
    {
        var row =JsonSerializer.Deserialize<JsonElement>(
            $$"""{"id":"rec-1","subscriptionId":"{{SubA}}","resourceId":"{{Vm}}","impact":3,"problem":{"text":"x"},"status":["Dismissed"],"category":null}""");
        var finding = GovernanceNormalizer.FromAdvisorRow(row);

        Assert.NotNull(finding);
        Assert.Equal(GovernanceSeverity.Medium, finding!.Severity);
        Assert.Equal("Advisor recommendation", finding.Title);
        Assert.Equal("", finding.Category);
    }

    // ---------- Defender plans ----------

    [Theory]
    [InlineData("Standard", "Standard")]
    [InlineData("standard", "Standard")]
    [InlineData("FREE", "Free")]
    [InlineData("Free", "Free")]
    public void Plans_ParseTier(string tier, string expected)
    {
        var plan = GovernanceNormalizer.FromPlanRow(PlanRow(("tier", tier)));

        Assert.Equal(new DefenderPlan(SubA, "VirtualMachines", expected), plan);
    }

    [Fact]
    public void Plans_SubscriptionFromTheRowId_WhenTheColumnIsMissing()
    {
        var plan = GovernanceNormalizer.FromPlanRow(PlanRow(("subscriptionId", "")));

        Assert.Equal(new DefenderPlan(SubA, "VirtualMachines", "Standard"), plan);
    }

    [Theory]
    [InlineData("plan", "")]
    [InlineData("plan", Absent)]
    [InlineData("tier", "")]
    [InlineData("tier", null)]
    public void Plans_WithoutPlanOrTier_AreNull(string column, string? value)
    {
        Assert.Null(GovernanceNormalizer.FromPlanRow(PlanRow((column, value))));
    }

    [Fact]
    public void Plans_WithoutAnySubscription_AreNull()
    {
        Assert.Null(GovernanceNormalizer.FromPlanRow(PlanRow(("subscriptionId", Absent), ("id", "pricing-1"))));
    }

    // ---------- dedupe ----------

    [Fact]
    public void Dedupe_IsCaseInsensitiveOnResourceId()
    {
        var first = PolicyFinding(Vm, "rec-1");
        var again = PolicyFinding(Vm.ToLowerInvariant(), "rec-2") with
        {
            PolicyAssignmentId = Assignment.ToLowerInvariant(),
            PolicyDefinitionId = Definition.ToUpperInvariant(),
        };

        var deduped = GovernanceNormalizer.Dedupe([first, again]);

        Assert.Single(deduped);
    }

    [Fact]
    public void Dedupe_KeepsFindingsThatDifferInSourceTitleDetailOrPolicy()
    {
        var baseline = PolicyFinding(Vm, "rec-1");
        var findings = new[]
        {
            baseline,
            baseline with { RecordId = "rec-2", Source = GovernanceSource.Advisor },
            baseline with { RecordId = "rec-3", Title = "deny public ip addresses" },
            baseline with { RecordId = "rec-4", Detail = "other-reference" },
            baseline with { RecordId = "rec-5", Detail = null },
            baseline with { RecordId = "rec-6", PolicyAssignmentId = Assignment + "-2" },
            baseline with { RecordId = "rec-7", PolicyDefinitionId = null },
        };

        var deduped = GovernanceNormalizer.Dedupe(findings);

        Assert.Equal(findings.Length, deduped.Count);
    }

    [Fact]
    public void Dedupe_OrdersByResourceSourceSeverityDescendingThenTitle()
    {
        var findings = new[]
        {
            Finding(GovernanceSource.Defender, Vm, GovernanceSeverity.Low, "b"),
            Finding(GovernanceSource.Advisor, Vm, GovernanceSeverity.Low, "a"),
            Finding(GovernanceSource.Advisor, Vm, GovernanceSeverity.High, "z"),
            Finding(GovernanceSource.Policy, Storage, GovernanceSeverity.Medium, "B"),
            Finding(GovernanceSource.Policy, Storage, GovernanceSeverity.Medium, "A"),
            Finding(GovernanceSource.Advisor, Vm, GovernanceSeverity.Low, "B"),
        };

        var ordered = GovernanceNormalizer.Dedupe(findings)
            .Select(f => $"{(f.ResourceId == Vm ? "vm" : "st")}:{f.Source}:{f.Severity}:{f.Title}")
            .ToArray();

        // ResourceId: .../Microsoft.Compute/... sorts before .../Microsoft.Storage/...
        Assert.Equal(
            new[] { "vm:Advisor:High:z", "vm:Advisor:Low:B", "vm:Advisor:Low:a", "vm:Defender:Low:b", "st:Policy:Medium:A", "st:Policy:Medium:B" },
            ordered);
    }

    [Fact]
    public void Dedupe_SortsResourceIdsCaseInsensitively()
    {
        var upper = Storage.ToUpperInvariant();
        var deduped = GovernanceNormalizer.Dedupe(
        [
            Finding(GovernanceSource.Policy, upper, GovernanceSeverity.Medium, "s"),
            Finding(GovernanceSource.Policy, Vm, GovernanceSeverity.Medium, "v"),
        ]);

        Assert.Equal(new[] { Vm, upper }, deduped.Select(f => f.ResourceId));
    }

    [Fact]
    public void Dedupe_KeepsTheMostSevereDuplicate_WhateverTheInputOrder()
    {
        var low = Finding(GovernanceSource.Advisor, Vm, GovernanceSeverity.Low, "Enable backups") with { RecordId = "rec-a" };
        var high = low with { RecordId = "rec-b", Severity = GovernanceSeverity.High };
        var highToo = low with { RecordId = "rec-c", Severity = GovernanceSeverity.High };

        var forward = Assert.Single(GovernanceNormalizer.Dedupe([low, high, highToo]));
        var backward = Assert.Single(GovernanceNormalizer.Dedupe([highToo, high, low]));

        Assert.Equal(high, forward);
        Assert.Equal(high, backward);
    }

    [Fact]
    public void Dedupe_OfNothing_IsEmpty()
    {
        Assert.Empty(GovernanceNormalizer.Dedupe([]));
    }

    // ---------- fixtures ----------

    private static GovernanceFinding PolicyFinding(string resourceId, string recordId) =>
        new(GovernanceSource.Policy, recordId, resourceId, SubA, GovernanceSeverity.Medium, "Policy",
            "Deny public IP addresses", "storage-public-access", Assignment, Definition);

    private static GovernanceFinding Finding(GovernanceSource source, string resourceId, GovernanceSeverity severity, string title) =>
        new(source, $"{source}:{resourceId}:{title}", resourceId, SubA, severity, "Category", title, null, null, null);

    private static JsonElement AdvisorRow(params (string Column, string? Value)[] changes) => Row(new()
    {
        ["id"] = Vm + "/providers/Microsoft.Advisor/recommendations/1b9a8c2d-0000-0000-0000-000000000001",
        ["subscriptionId"] = SubA,
        ["resourceId"] = Vm,
        ["source"] = Vm,
        ["category"] = "HighAvailability",
        ["impact"] = "High",
        ["problem"] = "Enable Backups on your virtual machines",
        ["solution"] = "Protect your data from accidental deletion with Azure Backup",
        // recommendationStatus is often absent and suppressionIds usually null: tostring() gives "".
        ["status"] = "",
        ["suppressions"] = "",
    }, changes);

    private static JsonElement PolicyRow(params (string Column, string? Value)[] changes) => Row(new()
    {
        ["id"] = $"/subscriptions/{SubA}/providers/Microsoft.PolicyInsights/policyStates/latest/1",
        ["subscriptionId"] = SubA,
        ["resourceId"] = Storage,
        ["assignmentId"] = Assignment,
        ["assignmentName"] = "Deny public IP addresses",
        ["definitionId"] = Definition,
        ["referenceId"] = "storage-public-access",
    }, changes);

    private static JsonElement DefenderRow(params (string Column, string? Value)[] changes) => Row(new()
    {
        ["id"] = AssessmentId,
        ["subscriptionId"] = SubA,
        ["targetId"] = Vm,
        ["name"] = "Machines should have vulnerability findings resolved",
        ["severity"] = "High",
    }, changes);

    private static JsonElement PlanRow(params (string Column, string? Value)[] changes) => Row(new()
    {
        ["id"] = $"/subscriptions/{SubA}/providers/Microsoft.Security/pricings/VirtualMachines",
        ["subscriptionId"] = SubA,
        ["plan"] = "VirtualMachines",
        ["tier"] = "Standard",
    }, changes);

    private static JsonElement Row(Dictionary<string, string?> columns, (string Column, string? Value)[] changes)
    {
        foreach (var (column, value) in changes)
        {
            if (value == Absent)
                columns.Remove(column);
            else
                columns[column] = value;
        }
        return JsonSerializer.SerializeToElement(columns);
    }

    /// <summary>
    /// Resource Graph pages a result on <c>$skipToken</c> only when some output column is not
    /// dynamic, and a deterministic order keeps the pages from overlapping: every projected
    /// column is a string column (id, subscriptionId) or wrapped in <c>tostring(...)</c>, the
    /// query ends ordered by id, and nothing caps it.
    /// </summary>
    private static void AssertPageableArgQuery(string query, string table, string type)
    {
        Assert.StartsWith($"{table} | where type =~ '{type}'{SubFilter}", query);
        Assert.EndsWith(" | order by id asc", query);
        Assert.DoesNotMatch(@"(?i)\b(take|limit|top)\b", query);

        var project = query.IndexOf("| project ", StringComparison.Ordinal);
        Assert.True(project > 0, "the query projects its columns");
        Assert.Equal(project, query.LastIndexOf("| project ", StringComparison.Ordinal));
        var columns = SplitTopLevel(query[(project + "| project ".Length)..^" | order by id asc".Length]);

        Assert.Equal(new[] { "id", "subscriptionId" }, columns.Take(2));
        Assert.True(columns.Count > 2);
        foreach (var column in columns.Skip(2))
            Assert.Matches(@"^[A-Za-z]+ = tostring\(.+\)$", column);
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(': depth++; break;
                case ')': depth--; break;
                case ',' when depth == 0:
                    parts.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        parts.Add(text[start..].Trim());
        return parts;
    }
}
