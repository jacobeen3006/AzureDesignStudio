using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Governance;
using AzureDesignStudio.Discovery.Report;
using AzureDesignStudio.Discovery.Scan;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Wave B3 in the static report bundle: governance findings and the state of each source ride
/// in report.json only when the scan has them, the HTML summary and section read "collected,
/// zero findings", "unavailable", "not requested" and "not collected" (a scan that predates
/// governance) differently, and the controls CSV lists every finding as UNMAPPED.
/// </summary>
public class ReportGovernanceTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
    private const string Vm = "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1";
    private const string Sa = "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/sa1";

    private static readonly GovernanceFinding Advisor = new(GovernanceSource.Advisor, "/rec/1", Vm, "s1",
        GovernanceSeverity.High, "Security", "Enable <script>alert(1)</script> disk encryption", "Turn on encryption at host", null, null);

    private static readonly GovernanceFinding Policy = new(GovernanceSource.Policy, "/state/1", Sa, "s1",
        GovernanceSeverity.Medium, "Policy", "require-https", null,
        "/subscriptions/s1/providers/Microsoft.Authorization/policyAssignments/require-https",
        "/providers/Microsoft.Authorization/policyDefinitions/404c3081");

    private static readonly GovernanceFinding Defender = new(GovernanceSource.Defender, Vm + "/providers/Microsoft.Security/assessments/a1", Vm, "s1",
        GovernanceSeverity.Low, "Security", "Install endpoint protection", null, null, null);

    private static ReportModel Bare() => new("Tenant report", "subscriptions/s1", AsOf, null, null, null, null, null, null);

    private static ReportModel With(IReadOnlyList<GovernanceFinding> findings, params (string Source, SourceState State)[] states) => Bare() with
    {
        Governance = findings,
        GovernanceSources = states.ToDictionary(s => s.Source, s => s.State, StringComparer.OrdinalIgnoreCase),
    };

    private static SourceState Collected(int rows) => new(SourceState.Collected, rows, null);
    private static readonly SourceState NotRequested = new(SourceState.NotRequested, 0, null);

    // --- json ---

    [Fact]
    public void Json_CarriesGovernanceAndSources_OnlyWhenCollected()
    {
        using (var bare = JsonDocument.Parse(ReportBundle.Build(Bare())[ReportBundle.JsonFile]))
        {
            Assert.False(bare.RootElement.TryGetProperty("governance", out _));
            Assert.False(bare.RootElement.TryGetProperty("governanceSources", out _));
        }

        var model = With([Advisor],
            (GovernanceSources.Advisor, Collected(1)),
            (GovernanceSources.Policy, new SourceState(SourceState.Unavailable, 0, "Forbidden: no access")),
            (GovernanceSources.Defender, NotRequested));
        using var doc = JsonDocument.Parse(ReportBundle.Build(model)[ReportBundle.JsonFile]);

        var sources = doc.RootElement.GetProperty("governanceSources");
        Assert.Equal("collected", sources.GetProperty("advisor").GetProperty("state").GetString());
        Assert.Equal(1, sources.GetProperty("advisor").GetProperty("rows").GetInt32());
        Assert.Equal("unavailable", sources.GetProperty("policy").GetProperty("state").GetString());
        Assert.Equal("Forbidden: no access", sources.GetProperty("policy").GetProperty("detail").GetString());
        Assert.Equal("not-requested", sources.GetProperty("defender").GetProperty("state").GetString());

        var finding = Assert.Single(doc.RootElement.GetProperty("governance").EnumerateArray());
        Assert.Equal("advisor", finding.GetProperty("source").GetString()); // the stored source key
        Assert.Equal("high", finding.GetProperty("severity").GetString());
        Assert.Equal(Vm, finding.GetProperty("resourceId").GetString());
        Assert.Equal("Turn on encryption at host", finding.GetProperty("detail").GetString());
    }

    [Fact]
    public void Json_CarriesTheUnreadableRowCount_OnlyWhenThereAreSome()
    {
        var collected = With([], (GovernanceSources.Advisor, Collected(0)), (GovernanceSources.Policy, Collected(0)),
            (GovernanceSources.Defender, Collected(0)));
        using (var clean = JsonDocument.Parse(ReportBundle.Build(collected)[ReportBundle.JsonFile]))
            Assert.False(clean.RootElement.TryGetProperty("governanceMalformed", out _));

        using var doc = JsonDocument.Parse(ReportBundle.Build(collected with { GovernanceMalformed = 2 })[ReportBundle.JsonFile]);
        Assert.Equal(2, doc.RootElement.GetProperty("governanceMalformed").GetInt32());
    }

    [Fact]
    public void Json_NotRequestedScan_StillSaysWhy()
    {
        var model = With([], (GovernanceSources.Advisor, NotRequested), (GovernanceSources.Policy, NotRequested),
            (GovernanceSources.Defender, NotRequested));
        using var doc = JsonDocument.Parse(ReportBundle.Build(model)[ReportBundle.JsonFile]);
        Assert.Empty(doc.RootElement.GetProperty("governance").EnumerateArray());
        Assert.Equal("not-requested", doc.RootElement.GetProperty("governanceSources").GetProperty("policy").GetProperty("state").GetString());
    }

    // --- html ---

    [Fact]
    public void Html_CollectedWithZeroFindings_SaysCollected()
    {
        var html = ReportHtml.Render(With([],
            (GovernanceSources.Advisor, Collected(0)), (GovernanceSources.Policy, Collected(0)), (GovernanceSources.Defender, Collected(0))));

        Assert.Contains("<tr><td>Advisor findings</td><td class=\"num\">0</td></tr>", html);
        Assert.Contains("<tr><td>Policy findings</td><td class=\"num\">0</td></tr>", html);
        Assert.Contains("<tr><td>Defender findings</td><td class=\"num\">0</td></tr>", html);
        Assert.Contains("<h2>Governance</h2>", html);
        Assert.Contains("<tr><td>Advisor</td><td>Collected: 0 findings</td></tr>", html);
        Assert.Contains("No governance findings were found in the collected sources.", html);
        Assert.Contains(GovernanceCaveats.All[0], html); // zero findings is not proof of none
        Assert.DoesNotContain("Not collected", html);
        Assert.DoesNotContain("Unavailable", html);
    }

    [Fact]
    public void Html_UnavailableSource_ShowsTheReason_AndTheFindingsOfTheOthers()
    {
        var html = ReportHtml.Render(With([Defender, Advisor],
            (GovernanceSources.Advisor, Collected(1)),
            (GovernanceSources.Policy, new SourceState(SourceState.Unavailable, 0, "Forbidden: no access to policy states")),
            (GovernanceSources.Defender, Collected(1))));

        Assert.Contains("<tr><td>Advisor findings</td><td class=\"num\">1</td></tr>", html);
        Assert.Contains("<tr><td>Policy findings</td><td class=\"num\">unavailable</td></tr>", html);
        Assert.Contains("<tr><td>Policy</td><td>Unavailable: Forbidden: no access to policy states</td></tr>", html);
        Assert.Contains("<tr><td>Advisor</td><td>Collected: 1 finding</td></tr>", html);

        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("<td class=\"sev-High\">High</td>", html);
        // most severe first: the High Advisor finding before the Low Defender one, whatever the input order
        Assert.True(html.IndexOf("disk encryption", StringComparison.Ordinal) < html.IndexOf("endpoint protection", StringComparison.Ordinal));
    }

    [Fact]
    public void Html_UnavailableWithStoredFindings_CountsWhatWasStored()
    {
        var html = ReportHtml.Render(With([Policy],
            (GovernanceSources.Advisor, NotRequested),
            (GovernanceSources.Policy, new SourceState(SourceState.Unavailable, 1, "Truncated: Resource Graph cut the policy states listing short.")),
            (GovernanceSources.Defender, NotRequested)));

        Assert.Contains("<tr><td>Policy findings</td><td class=\"num\">unavailable (1 stored)</td></tr>", html);
        Assert.Contains("require-https", html);
    }

    [Fact]
    public void Html_IncompleteSource_SaysItStoppedEarly()
    {
        var html = ReportHtml.Render(With([Advisor],
            (GovernanceSources.Advisor, new SourceState(SourceState.Incomplete, 1, "1 of 2 subscription groups were read.")),
            (GovernanceSources.Policy, Collected(0)), (GovernanceSources.Defender, Collected(0))));

        Assert.Contains("<tr><td>Advisor findings</td><td class=\"num\">incomplete (1 stored)</td></tr>", html);
        Assert.Contains("<tr><td>Advisor</td><td>Incomplete: the scan stopped before this source finished; 1 of 2 subscription groups were read.</td></tr>", html);
        Assert.Contains(GovernanceCaveats.All[0], html);
    }

    [Fact]
    public void Html_NotRequested_SaysSo_WithoutCaveats()
    {
        var html = ReportHtml.Render(With([],
            (GovernanceSources.Advisor, NotRequested), (GovernanceSources.Policy, NotRequested), (GovernanceSources.Defender, NotRequested)));

        Assert.Contains("<tr><td>Advisor findings</td><td class=\"num\">not requested</td></tr>", html);
        Assert.Contains("<tr><td>Defender</td><td>Not requested for this scan</td></tr>", html);
        Assert.DoesNotContain("No governance findings were found", html);
        Assert.DoesNotContain(GovernanceCaveats.All[0], html);
    }

    [Fact]
    public void Html_UnreadableRows_AreCounted_NeverNoFindings()
    {
        // A stored row this build cannot read (e.g. a source or severity a newer version wrote) is
        // not shown, so the report must say it exists instead of "no governance findings".
        var html = ReportHtml.Render(With([],
            (GovernanceSources.Advisor, Collected(2)), (GovernanceSources.Policy, Collected(0)), (GovernanceSources.Defender, Collected(0)))
            with { GovernanceMalformed = 2 });

        Assert.Contains("<p>2 stored governance row(s) could not be read (unknown source or severity) and are not shown.</p>", html);
        Assert.DoesNotContain("No governance findings were found", html);
        Assert.Contains(GovernanceCaveats.All[0], html);

        var clean = ReportHtml.Render(With([],
            (GovernanceSources.Advisor, Collected(0)), (GovernanceSources.Policy, Collected(0)), (GovernanceSources.Defender, Collected(0))));
        Assert.DoesNotContain("could not be read", clean);
    }

    [Fact]
    public void Html_LegacyScan_ReadsNotCollected_NeverZero()
    {
        var html = ReportHtml.Render(Bare());

        Assert.Contains("<tr><td>Advisor findings</td><td class=\"num\">not collected</td></tr>", html);
        Assert.Contains("<tr><td>Policy findings</td><td class=\"num\">not collected</td></tr>", html);
        Assert.Contains("<tr><td>Defender findings</td><td class=\"num\">not collected</td></tr>", html);
        Assert.Contains("<tr><td>Policy</td><td>Not collected: this scan predates governance collection</td></tr>", html);
        Assert.DoesNotContain("Collected:", html);
        Assert.DoesNotContain("No governance findings were found", html);
    }

    [Fact]
    public void Html_Governance_DoesNotDependOnInputOrder()
    {
        var states = new[] { (GovernanceSources.Advisor, Collected(1)), (GovernanceSources.Policy, Collected(1)), (GovernanceSources.Defender, Collected(1)) };
        Assert.Equal(ReportHtml.Render(With([Advisor, Policy, Defender], states)),
            ReportHtml.Render(With([Defender, Policy, Advisor], states)));
        Assert.Equal(ReportBundle.ControlsCsv(With([Advisor, Policy, Defender], states)),
            ReportBundle.ControlsCsv(With([Defender, Policy, Advisor], states)));
    }

    // --- controls csv ---

    [Fact]
    public void Csv_GovernanceKinds_AreUnmapped()
    {
        var uncategorized = Advisor with { RecordId = "/rec/2", Category = "", Title = "Right-size", Detail = null };
        var csv = ReportBundle.ControlsCsv(With([Advisor, Policy, Defender, uncategorized],
            (GovernanceSources.Advisor, Collected(2)), (GovernanceSources.Policy, Collected(1)), (GovernanceSources.Defender, Collected(1))));
        var lines = csv.TrimEnd('\n').Split('\n');

        Assert.Contains($"advisor-Security,{Vm},High,UNMAPPED,-,advisor-Security,Enable <script>alert(1)</script> disk encryption - Turn on encryption at host", lines);
        Assert.Contains($"advisor-uncategorized,{Vm},High,UNMAPPED,-,advisor-uncategorized,Right-size", lines);
        Assert.Contains($"policy-noncompliant,{Sa},Medium,UNMAPPED,-,policy-noncompliant,require-https", lines);
        Assert.Contains($"defender-assessment,{Vm},Low,UNMAPPED,-,defender-assessment,Install endpoint protection", lines);
        Assert.Equal(5, lines.Length); // header + one row per finding
    }

    [Fact]
    public void Csv_TenantText_NeverStartsAFormula()
    {
        // A policy assignment name may start with "=" (ARM refuses only <>*%&:\?.+/ there), and a
        // custom Defender assessment's display name is free text.
        var formula = Policy with { Title = "=cmd|'calc'!A0" };
        var custom = Defender with { RecordId = "/a/2", Title = "@SUM(1)\r=1+1" };
        var csv = ReportBundle.ControlsCsv(With([formula, custom],
            (GovernanceSources.Policy, Collected(1)), (GovernanceSources.Defender, Collected(1))));

        Assert.Contains($"policy-noncompliant,{Sa},Medium,UNMAPPED,-,policy-noncompliant,'=cmd|'calc'!A0\n", csv);
        // A bare CR is quoted too, so it cannot start a row whose first cell is a formula.
        Assert.Contains($"defender-assessment,{Vm},Low,UNMAPPED,-,defender-assessment,\"'@SUM(1)\r=1+1\"\n", csv);
    }

    [Fact]
    public void Csv_LegacyScan_HasNoGovernanceRows()
    {
        var lines = ReportBundle.ControlsCsv(Bare()).TrimEnd('\n').Split('\n');
        Assert.Equal(["kind,resource_id,severity,framework,control,title,detail"], lines);
    }

    // --- status lines (drawer, Markdown and HTML) ---

    [Fact]
    public void StatusLine_EachState_ReadsDifferently()
    {
        var sources = new Dictionary<string, SourceState>(StringComparer.OrdinalIgnoreCase)
        {
            [GovernanceSources.Advisor] = Collected(42),
            [GovernanceSources.Policy] = new(SourceState.Incomplete, 3, "1 of 3 subscription groups were read."),
            [GovernanceSources.Defender] = new(SourceState.Unavailable, 0, "Forbidden: denied"),
        };

        Assert.Equal("Collected: 42 findings", GovernanceStatus.Line(sources, GovernanceSources.Advisor, 42));
        Assert.Equal("Collected: 1 finding", GovernanceStatus.Line(sources, GovernanceSources.Advisor, 1));
        Assert.Equal("Incomplete: the scan stopped before this source finished; 1 of 3 subscription groups were read.",
            GovernanceStatus.Line(sources, GovernanceSources.Policy, 3));
        Assert.Equal("Unavailable: Forbidden: denied", GovernanceStatus.Line(sources, GovernanceSources.Defender, 0));
        Assert.Equal("Not requested for this scan",
            GovernanceStatus.Line(new Dictionary<string, SourceState> { [GovernanceSources.Policy] = NotRequested }, GovernanceSources.Policy, 0));
        Assert.Equal("Not collected: this scan predates governance collection", GovernanceStatus.Line(null, GovernanceSources.Advisor, 0));
        Assert.Equal("Unknown: the scan manifest does not mention this source",
            GovernanceStatus.Line(new Dictionary<string, SourceState>(), GovernanceSources.Advisor, 0));

        Assert.Equal("42", GovernanceStatus.Brief(sources, GovernanceSources.Advisor, 42));
        Assert.Equal("incomplete (3 stored)", GovernanceStatus.Brief(sources, GovernanceSources.Policy, 3));
        Assert.Equal("unavailable", GovernanceStatus.Brief(sources, GovernanceSources.Defender, 0));
        Assert.Equal("not collected", GovernanceStatus.Brief(null, GovernanceSources.Defender, 0));
        Assert.Equal("unknown", GovernanceStatus.Brief(new Dictionary<string, SourceState>(), GovernanceSources.Defender, 0));

        Assert.Equal(["Advisor", "Policy", "Defender"], GovernanceSources.All.Select(GovernanceStatus.Label).ToArray());
    }

    [Fact]
    public void MalformedNote_NamesTheUnreadableRows_OnlyWhenThereAreSome()
    {
        Assert.Null(GovernanceStatus.MalformedNote(0));
        Assert.Equal("3 stored governance row(s) could not be read (unknown source or severity) and are not shown.",
            GovernanceStatus.MalformedNote(3));
    }

    // --- Defender plans (drawer) ---

    private static readonly DefenderPlan VmPlan = new("s1", "VirtualMachines", "Standard");

    private static Dictionary<string, SourceState> DefenderIs(SourceState state) =>
        new(StringComparer.OrdinalIgnoreCase) { [GovernanceSources.Defender] = state };

    [Fact]
    public void DefenderPlans_NotRead_SayNotRead()
    {
        Assert.Equal("Defender plans were not read for this scan.", GovernanceStatus.DefenderPlansNote(null, null));
        Assert.Equal("Defender plans were not read for this scan.", GovernanceStatus.DefenderPlansNote(DefenderIs(NotRequested), null));
    }

    [Fact]
    public void DefenderPlans_Collected_SayNoneOnlyWhenNoneWereListed()
    {
        Assert.Equal("Resource Graph listed no Defender plans for the scanned subscriptions.",
            GovernanceStatus.DefenderPlansNote(DefenderIs(Collected(0)), []));
        Assert.Null(GovernanceStatus.DefenderPlansNote(DefenderIs(Collected(0)), [VmPlan])); // the list speaks for itself
    }

    [Fact]
    public void DefenderPlans_Unavailable_AreNotReadInFull_NeverNone()
    {
        // The Defender worker keeps a group's plans only when both of its queries succeed, so a
        // failed Defender source leaves an empty list that is not "Resource Graph listed none".
        Assert.Equal("Defender plans were not read in full. Unavailable: Forbidden: denied",
            GovernanceStatus.DefenderPlansNote(DefenderIs(new SourceState(SourceState.Unavailable, 0, "Forbidden: denied")), []));
        Assert.Equal("Defender plans were not read in full; the plans listed are only those that were read. Unavailable: Forbidden: denied",
            GovernanceStatus.DefenderPlansNote(DefenderIs(new SourceState(SourceState.Unavailable, 0, "Forbidden: denied")), [VmPlan]));
    }

    [Fact]
    public void DefenderPlans_Incomplete_QualifyThePartialList()
    {
        var incomplete = DefenderIs(new SourceState(SourceState.Incomplete, 0, "1 of 3 subscription groups were read."));

        Assert.Equal("Defender plans were not read in full; the plans listed are only those that were read. "
            + "Incomplete: the scan stopped before this source finished; 1 of 3 subscription groups were read.",
            GovernanceStatus.DefenderPlansNote(incomplete, [VmPlan]));
        Assert.Equal("Defender plans were not read in full. "
            + "Incomplete: the scan stopped before this source finished; 1 of 3 subscription groups were read.",
            GovernanceStatus.DefenderPlansNote(incomplete, []));
    }

    [Fact]
    public void DefenderPlans_AnyStateButCollected_NeverSaysNoneWereListed()
    {
        IReadOnlyDictionary<string, SourceState>?[] notCollected =
        [
            null,
            new Dictionary<string, SourceState>(),
            DefenderIs(NotRequested),
            DefenderIs(new SourceState("some-future-state", 0, null)),
            DefenderIs(new SourceState(SourceState.Unavailable, 0, null)),
            DefenderIs(new SourceState(SourceState.Incomplete, 0, null)),
        ];

        foreach (var sources in notCollected)
        {
            var note = GovernanceStatus.DefenderPlansNote(sources, []);
            Assert.NotNull(note);
            Assert.StartsWith("Defender plans were not read in full. ", note);
            Assert.DoesNotContain("listed no", note);
        }
    }
}
