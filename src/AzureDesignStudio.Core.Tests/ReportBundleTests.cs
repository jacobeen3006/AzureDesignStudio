using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Cost;
using AzureDesignStudio.Discovery.Drift;
using AzureDesignStudio.Discovery.Exposure;
using AzureDesignStudio.Discovery.Report;
using AzureDesignStudio.Discovery.Unmanaged;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 9.6 - static report bundle: JSON/HTML/CSV determinism, escaping, framework mapping.</summary>
public class ReportBundleTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static ReportModel Model(string? svg = null) => new(
        "Tenant report", "subscriptions/s1", AsOf,
        [
            new ExposureFinding("/subs/s1/vm2", "microsoft.compute/virtualmachines", "nsg-open-admin-ports",
                ExposureSeverity.Critical, "SSH <script>alert(1)</script> open to Internet", "Use Bastion"),
        ],
        new ManagementAudit(
            [new ManagementFinding("/subs/s1/vnet1", "microsoft.network/virtualnetworks", "rg1",
                ManagementStatus.Unmanaged, "no-orchestration-marker")],
            [new ManagementFinding("/subs/s1/sa-prod", "microsoft.storage/storageaccounts", "rg2",
                ManagementStatus.Protected, "guardrail-tag:environment=prod")]),
        [
            new LiveDriftItem("/subs/s1/vm2", "microsoft.compute/virtualmachines", LiveDriftStatus.Modified,
                AsOf.AddHours(-3), "admin@corp", "Azure PowerShell", "Microsoft.Compute/virtualMachines/write"),
        ],
        [new SpendBucket("Compute", 100m, 0.8), new SpendBucket("Storage", 25m, 0.2)],
        new SpendProvenance("cost-management-query", "subscriptions/s1", "management.azure.com", "USD",
            AsOf.AddDays(-30), AsOf, AsOf.AddMinutes(5)),
        svg);

    // --- bundle ---

    [Fact]
    public void Build_ReturnsThreeFiles_PlusSvgOnlyWhenSafe()
    {
        Assert.Equal(["controls-audit.csv", "report.html", "report.json"],
            ReportBundle.Build(Model()).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var withSvg = ReportBundle.Build(Model("<svg viewBox=\"0 0 1 1\"></svg>"));
        Assert.Contains(ReportBundle.SvgFile, withSvg.Keys);

        Assert.DoesNotContain(ReportBundle.SvgFile,
            ReportBundle.Build(Model("<svg><script>alert(1)</script></svg>")).Keys);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var a = ReportBundle.Build(Model());
        var b = ReportBundle.Build(Model());
        foreach (var key in a.Keys) Assert.Equal(a[key], b[key]);
    }

    // --- json ---

    [Fact]
    public void Json_RoundTripsCamelCased()
    {
        using var doc = JsonDocument.Parse(ReportBundle.Build(Model())[ReportBundle.JsonFile]);
        Assert.Equal("subscriptions/s1", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("exposureCount").GetInt32());
        Assert.Equal("critical",
            doc.RootElement.GetProperty("exposure")[0].GetProperty("severity").GetString()); // enums as camel strings
        Assert.Equal(125m, doc.RootElement.GetProperty("spendTotal").GetDecimal());
    }

    // --- html ---

    [Fact]
    public void Html_EscapesHostileNames_ShowsProvenance()
    {
        var html = ReportBundle.Build(Model())[ReportBundle.HtmlFile];
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("&lt;script&gt;alert(1)", html);
        Assert.Contains("currency USD", html); // provenance travels with the figures
        Assert.Contains("cost-management-query", html);
        Assert.Contains("2026-09-14 08:00", html);
    }

    [Fact]
    public void Html_EmptySectionsAreAbsent()
    {
        var bare = new ReportModel("T", "s", AsOf, null, null, null, null, null, null);
        var html = ReportHtml.Render(bare);
        Assert.DoesNotContain("<h2>Exposure</h2>", html);
        Assert.DoesNotContain("<h2>Drift", html);
        Assert.DoesNotContain("<h2>Spend", html);
    }

    // --- controls csv ---

    [Fact]
    public void Csv_HeaderEscapedRowsAndMappings()
    {
        var csv = ReportBundle.Build(Model())[ReportBundle.CsvFile];
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("kind,resource_id,severity,framework,control,title,detail", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("nsg-open-admin-ports,/subs/s1/vm2,Critical,CIS Azure v2.0,3.1"));
        Assert.Contains(lines, l => l.Contains("unmanaged,/subs/s1/vnet1"));
        Assert.Contains(lines, l => l.Contains("drift-Modified,/subs/s1/vm2") && l.Contains("CM-3"));
    }

    [Fact]
    public void ControlsMap_UnknownKind_IsUnmappedNotGuessed()
    {
        var m = Assert.Single(ControlsMap.For("mystery-finding"));
        Assert.Equal("UNMAPPED", m.Framework);
        Assert.Equal("mystery-finding", m.Title);
    }

    [Fact]
    public void Csv_QuotesCommasAndDoubles()
    {
        var model = new ReportModel("T", "s", AsOf,
            [new ExposureFinding("/r/x", "t", "nsg-open-all-inbound", ExposureSeverity.High, "a,b \"c\"", "fix")],
            null, null, null, null, null);
        Assert.Contains("\"a,b \"\"c\"\"\"", ReportBundle.ControlsCsv(model));
    }
}
