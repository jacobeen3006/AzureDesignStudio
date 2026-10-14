using System.Security.Cryptography;
using System.Text;
using AzureDesignStudio.Discovery.Iac;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Decommission;

/// <summary>Inputs for runbook generation. <see cref="Now"/> is injected, never read from a clock:
/// the engine is pure, so the same inputs always produce the same runbook and the same plan hash.</summary>
public sealed record RunbookOptions(
    DateTimeOffset Now,
    TimeSpan PurgeDelay,
    string? StackName = null,
    string? StackLocation = null,
    IReadOnlyList<ArmLock>? ExistingLocks = null)
{
    /// <summary>Undo window defaults to 7 days ([71]: the T in T+N).</summary>
    public static RunbookOptions Default(DateTimeOffset now) => new(now, TimeSpan.FromDays(7));
}

/// <summary>One reviewable command row: the az line, the PowerShell line, and what proves it worked.</summary>
public sealed record RunbookLine(string ResourceId, string Cli, string PowerShell, string Expect);

/// <summary>A numbered runbook stage: prose checklist first, then the exact commands.</summary>
public sealed record RunbookStage(string Title, IReadOnlyList<string> Checklist, IReadOnlyList<RunbookLine> Lines);

/// <summary>
/// The generated runbook. <see cref="PlanHash"/> binds the reviewed bytes: the [62] approval must
/// name this hash, and any edit to candidates, order or window changes it. Renders to a Markdown
/// review document plus executable-but-guarded CLI and PowerShell scripts ([69]).
/// </summary>
public sealed class Runbook
{
    public required string PlanHash { get; init; }
    public required ProtectionWindow Window { get; init; }
    public required DateTimeOffset PurgeNotBefore { get; init; }
    public required IReadOnlyList<RunbookStage> Stages { get; init; }
    public required IReadOnlyList<string> Checklist { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }

    /// <summary>Empty-template stack PUT body with actionOnUnmanage=detach (present when the caller
    /// named the stack); the honest detach move: unmanage everything, delete nothing.</summary>
    public string? StackDetachJson { get; init; }

    public string Markdown() => RunbookRenderer.Markdown(this);
    public string CliScript() => RunbookRenderer.CliScript(this);
    public string PowerShellScript() => RunbookRenderer.PowerShellScript(this);
}

/// <summary>
/// Runbook-as-code generator (v2 plan 13.4, [69][71]) - pure and offline. Consumes a
/// <see cref="DecommissionPlan"/> (the planner is the refusal oracle: a plan with any refusal
/// generates nothing) and emits a two-stage destroy: protect the undo window with
/// <c>ads-decom-</c> locks, detach from IaC control (stacks unmanage with Detach - resources
/// keep running), and a purge phase that is inert before T+N. Purge releases the window locks
/// first, then deletes in the planner's staged order, verifying each removal. Refused and
/// cascade ids can never appear in a delete line: staging already excludes refusals, and
/// cascade children die with their parent. Deterministic: same inputs, byte-identical output.
/// </summary>
public static class DecommissionRunbook
{
    private const string EmptyTemplate =
        """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[]}""";

    public static Runbook Generate(DecommissionPlan plan, IReadOnlyList<InventoryItem> inventory, RunbookOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(options);
        if (options.PurgeDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "the purge delay (T in T+N) must be positive - the undo window is the point");
        if (!plan.Allowed)
        {
            var refusals = plan.Blockers.Count(b => b.Severity == DecommissionSeverity.Refusal);
            throw new InvalidOperationException(
                $"the plan carries {refusals} refusal(s); a runbook is generated only for a plan the planner allows - resolve the blockers and re-plan");
        }

        var byId = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory)
            byId.TryAdd(item.Id.Trim(), item);

        string Canonical(string id) => byId.TryGetValue(id.Trim(), out var hit) ? hit.Id : id.Trim();

        // Staged candidates are the executable set: refusals never stage (planner invariant) and
        // cascade children are not staged (ARM removes them with the parent).
        var staged = plan.Stages.SelectMany(s => s).Select(Canonical).ToList();
        var protectedIds = staged.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id.ToLowerInvariant(), StringComparer.Ordinal).ToList();

        var purgeAt = options.Now.ToUniversalTime() + options.PurgeDelay;

        // --- stack detach body: empty managed set + Detach policy (the reversible stage) ---
        // A failed body is a caveat, not a hash input: the hash binds only bytes the runbook renders.
        string? stackJson = null;
        string? stackHash = null;
        var caveats = new List<string>(plan.Caveats);
        if (options.StackName is { Length: > 0 } && options.StackLocation is { Length: > 0 })
        {
            var body = DeploymentStack.Build(options.StackName, options.StackLocation, EmptyTemplate,
                new StackPolicy(Missing: StackUnmanageAction.Detach));
            if (body.Success)
            {
                stackJson = body.Json;
                stackHash = body.PlanHash;
            }
            else
                caveats.Add("stack-detach-refused: " + string.Join("; ",
                    body.Diagnostics.Where(d => d.Severity == ArmDiagnosticSeverity.Error).Select(d => d.Code)));
        }
        else
            caveats.Add("stack-not-named: no deployment stack was named, so the detach stage lists manual unmanage only - resources under IaC control that are not detached stay managed while purge deletes them");

        // Deterministic window identity: the window belongs to exactly this plan shape.
        var canonical = new StringBuilder()
            .Append("purgeNotBefore=").Append(purgeAt.ToString("O")).Append('\n')
            .Append("stages=").Append(string.Join("|", plan.Stages.Select(s => string.Join(",", s.Select(Canonical))))).Append('\n')
            .Append("protected=").Append(string.Join(",", protectedIds)).Append('\n')
            .Append("detach=").Append(stackJson ?? "none")
            .ToString();
        var windowId = "decom-" + Sha256Hex(canonical)[..16];
        var planHash = "sha256:" + Sha256Hex(canonical + "\nwindowId=" + windowId);
        var window = new ProtectionWindow(windowId, options.Now.ToUniversalTime(), purgeAt);

        // --- protection window: the undo window is only real if the locks land first ---
        var locksSupplied = options.ExistingLocks is not null;
        if (!locksSupplied)
            caveats.Add("protect-window-unverified: existing lock state was not supplied - check `az lock list` before applying the window locks");
        var protection = ProtectionWindowPlanner.Protect(inventory, protectedIds, window, options.ExistingLocks ?? []);

        var stages = new List<RunbookStage>();

        stages.Add(new RunbookStage(
            "Stage 0 - Protect the undo window (apply now)",
            [
                $"Lock name {protection.LockName} is owned by window {windowId}; release matches it by name only - foreign locks survive.",
                locksSupplied
                    ? "Already-protected resources are skipped, never double-locked."
                    : "Lock evidence was not supplied: verify with `az lock list` before applying.",
            ],
            protection.Apply.Select(p => new RunbookLine(p.Scope,
                $"az lock create --name \"{p.Name}\" --lock CanNotDelete --notes \"{Quote(p.Notes)}\" --scope \"{p.Scope}\"",
                $"New-AzResourceLock -Scope \"{p.Scope}\" -LockName \"{p.Name}\" -Level CanNotDelete -Notes \"{Quote(p.Notes)}\"",
                $"lock {p.Name} exists on the resource")).ToList()));

        var detachChecklist = new List<string>
        {
            "Detach removes resources from IaC control; it deletes nothing. Every resource must still exist when this stage ends.",
            stackJson is not null
                ? $"PUT the empty-managed-set stack body below to Microsoft.Resources/deploymentStacks/{options.StackName} (api-version {DeploymentStack.ApiVersion}) - exact bytes; its own hash is {stackHash}."
                : "No stack named: unmanage each staged resource from whatever pipeline manages it (Terraform state, Bicep deployment, hand), and record the unmanage here.",
            "If anything must be spared, abort now: the abort stage releases the window and nothing was destroyed.",
        };
        stages.Add(new RunbookStage("Stage 1 - Detach (unmanage; resources keep running)", detachChecklist,
            staged.Select(id => new RunbookLine(id,
                $"az resource exists --id \"{id}\"",
                $"$null -ne (Get-AzResource -ResourceId \"{id}\" -ErrorAction SilentlyContinue)",
                "exists = true (detach must not destroy)")).ToList()));

        stages.Add(new RunbookStage(
            "Stage 2a - Release the decommission window locks",
            [$"Run only after the purge window opens and the [62] approval naming {planHash} is live. Only the window lock {protection.LockName} is released - operator locks are left in place by name."],
            protectedIds.Select(id => new RunbookLine(id,
                $"az lock delete --name \"{protection.LockName}\" --scope \"{id}\"",
                $"Remove-AzResourceLock -Scope \"{id}\" -LockName \"{protection.LockName}\" -Force",
                "window lock gone; foreign locks untouched")).ToList()));

        for (var i = 0; i < plan.Stages.Count; i++)
        {
            var batch = plan.Stages[i];
            stages.Add(new RunbookStage(
                $"Stage 2b.{i + 1} - Purge batch {i + 1} of {plan.Stages.Count} (irreversible)",
                [$"Every id in this batch is safe to delete once all earlier batches are done. {batch.Count} resource(s)."],
                batch.Select(id => new RunbookLine(Canonical(id),
                    $"az resource delete --id \"{Canonical(id)}\"",
                    $"Remove-AzResource -ResourceId \"{Canonical(id)}\" -Force",
                    "exists = false")).ToList()));
        }

        stages.Add(new RunbookStage(
            "Abort path - release the window, destroy nothing",
            [
                "Run any time before a purge batch: the resources stay alive and only the window locks come off.",
                "Re-attach: redeploy the original stack/template so IaC manages the resources again.",
            ],
            protectedIds.Select(id => new RunbookLine(id,
                $"az lock delete --name \"{protection.LockName}\" --scope \"{id}\"",
                $"Remove-AzResourceLock -Scope \"{id}\" -LockName \"{protection.LockName}\" -Force",
                "window lock gone")).ToList()));

        // --- human checklist ([69]): owner notification, CMDB, monitoring teardown, exports ---
        var checklist = new List<string>
        {
            $"Peer-review this runbook in git - destruction is reviewed, not clicked. The approval [62] must name plan hash {planHash}.",
            "Retention manifest [66] exported and verified for every staged candidate before purge (Decommission tab - Export manifest).",
        };

        var owned = staged.Select(id => byId.TryGetValue(id, out var it) ? it : null)
            .Where(it => it is not null && it.Owner is not null)
            .Select(it => it!)
            .ToList();
        var unowned = staged.Count - owned.Count;
        if (owned.Count > 0)
            checklist.Add($"Notify owners: {string.Join(", ", owned.Select(o => $"{o.Name} ({o.Owner})"))}.");
        if (unowned > 0)
            checklist.Add($"{unowned} staged resource(s) carry no owner tag - name a human owner before purge.");
        checklist.Add("Open a CMDB change record for every staged resource and reference the plan hash.");

        var diagBreaks = plan.Impacts.Where(im => im.Kind == "diagnostics-break").ToList();
        checklist.Add(diagBreaks.Count > 0
            ? $"Teardown monitoring: alerts, dashboards and diagnostics settings referencing {string.Join(", ", diagBreaks.Select(d => d.ResourceId))} must be removed or re-pointed before purge."
            : "Teardown monitoring: check for alerts or dashboards referencing the staged resources before purge.");
        foreach (var kv in plan.Impacts.Where(im => im.Kind == "keyvault-soft-delete"))
            checklist.Add($"Key Vault {kv.ResourceId}: soft-deleted for 90 days after purge - record the purge date; recovery is possible until a purge, and earlier recovery needs purge protection to be off.");
        if (plan.ImplicitChildren.Count > 0)
            checklist.Add($"Cascade: {string.Join("; ", plan.ImplicitChildren.Select(c => $"{c.ChildId} dies with {c.ParentId}"))} - no separate delete, but their loss is real and must be in the change record.");
        foreach (var warning in plan.Blockers.Where(b => b.Severity == DecommissionSeverity.Warning))
            checklist.Add($"Warning carried: {warning.ResourceId} [{warning.Kind}] {warning.Detail}");
        foreach (var warning in plan.Impacts.Where(im => im.Severity == DecommissionSeverity.Warning))
            checklist.Add($"Warning carried: {warning.ResourceId} [{warning.Kind}] {warning.Detail}");

        return new Runbook
        {
            PlanHash = planHash,
            Window = window,
            PurgeNotBefore = purgeAt,
            Stages = stages,
            Checklist = checklist,
            Caveats = caveats,
            StackDetachJson = stackJson,
        };
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>Escape for the double-quoted note strings shared by bash and PowerShell; ids and
    /// window notes carry no $ or backticks, so only quote and backslash need it.</summary>
    internal static string Quote(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>Renders the runbook to the three review artifacts ([69]).</summary>
internal static class RunbookRenderer
{
    public static string Markdown(Runbook r)
    {
        var sb = new StringBuilder();
        sb.Append("# Decommission runbook\n\n");
        sb.Append($"Plan hash: `{r.PlanHash}` - the approval binds to this hash.\n\n");
        sb.Append("> **Two-stage destroy [71].** Stages 0-1 (protect + detach) run now and are reversible. ");
        sb.Append($"**Stage 2 (purge) is inert before {r.PurgeNotBefore:O}** - the undo window ends then; ");
        sb.Append("until it does, nothing staged is destroyed.\n\n");

        foreach (var stage in r.Stages)
        {
            sb.Append("## ").Append(stage.Title).Append("\n\n");
            foreach (var c in stage.Checklist)
                sb.Append("- ").Append(c).Append('\n');
            if (stage.Lines.Count > 0)
            {
                sb.Append("\n```bash\n");
                foreach (var l in stage.Lines)
                    sb.Append(l.Cli).Append("   # ").Append(l.ResourceId).Append(": expect ").Append(l.Expect).Append('\n');
                sb.Append("```\n\n```powershell\n");
                foreach (var l in stage.Lines)
                    sb.Append(l.PowerShell).Append("   # ").Append(l.ResourceId).Append(": expect ").Append(l.Expect).Append('\n');
                sb.Append("```\n\n");
            }
        }

        if (r.StackDetachJson is not null)
        {
            sb.Append("## Stack detach body (empty managed set, actionOnUnmanage=detach)\n\n```json\n");
            sb.Append(r.StackDetachJson).Append('\n');
            sb.Append("```\n\n");
        }

        sb.Append("## Human checklist\n\n");
        foreach (var c in r.Checklist)
            sb.Append("- [ ] ").Append(c).Append('\n');

        if (r.Caveats.Count > 0)
        {
            sb.Append("\n## Caveats (what was not evaluated)\n\n");
            foreach (var c in r.Caveats)
                sb.Append("- ").Append(c).Append('\n');
        }

        var w = r.Window;
        sb.Append("\n## Window\n\n");
        sb.Append($"- id: `{w.WindowId}` (locks named `{ProtectionWindowPlanner.LockNameFor(w)}`)\n");
        sb.Append($"- undo window: {w.Start:O} .. {w.End:O}\n");

        return sb.ToString();
    }

    public static string CliScript(Runbook r)
    {
        var sb = new StringBuilder();
        sb.Append("#!/usr/bin/env bash\nset -euo pipefail\n\n");
        AppendScript(sb, r, cli: true);
        return sb.ToString();
    }

    public static string PowerShellScript(Runbook r)
    {
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference = 'Stop'\n\n");
        AppendScript(sb, r, cli: false);
        return sb.ToString();
    }

    private static void AppendScript(StringBuilder sb, Runbook r, bool cli)
    {
        void Comment(string text) => sb.Append("# ").Append(text).Append('\n');

        Comment($"Two-stage destroy: stages 0-1 now (reversible); purge not before {r.PurgeNotBefore:O}.");
        Comment($"Plan hash {r.PlanHash} - the approval must name it.");
        foreach (var c in r.Caveats)
            Comment($"caveat: {c}");
        sb.Append('\n');

        foreach (var stage in r.Stages)
        {
            Comment(stage.Title);
            if (stage.Title.StartsWith("Stage 2", StringComparison.Ordinal))
                sb.Append(cli
                    ? $"if [ \"$(date -u +%s)\" -lt {r.PurgeNotBefore.ToUnixTimeSeconds()} ]; then echo \"purge window not open before {r.PurgeNotBefore:O}\"; exit 1; fi\n"
                    : $"if ((Get-Date).ToUniversalTime() -lt [DateTime]::Parse(\"{r.PurgeNotBefore:O}\")) {{ throw \"purge window not open before {r.PurgeNotBefore:O}\" }}\n");
            else if (stage.Title.StartsWith("Abort", StringComparison.Ordinal))
                Comment("Abort path: run instead of any purge batch.");
            foreach (var l in stage.Lines)
                sb.Append(cli ? l.Cli : l.PowerShell).Append("   # ").Append(l.ResourceId).Append('\n');
            sb.Append('\n');
        }
    }
}
