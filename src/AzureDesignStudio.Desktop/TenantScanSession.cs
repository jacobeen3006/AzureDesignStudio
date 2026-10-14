using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Preflight;
using AzureDesignStudio.Discovery.Scan;
using AzureDesignStudio.Workspace;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Live scan state for the tenant-scan drawer: one run at a time, pause/cancel/progress
/// over the shared ScanEngine, history + snapshots straight from the SQLite workspace.
/// </summary>
public sealed class TenantScanSession(
    ScanEngine engine,
    IScanSink sink,
    WorkspaceDb db,
    TenantInventory inventory,
    PermissionPreflight preflight)
{
    private ScanControl? control;
    private CancellationTokenSource? cts;

    public WorkspaceScans Scans { get; } = new(db);

    public bool Running { get; private set; }
    public bool Paused => control?.Paused == true;
    public ScanProgress? Progress { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Fires off the UI thread; subscribers must marshal with InvokeAsync.</summary>
    public event Action? StateChanged;

    public Task<IReadOnlyList<SubscriptionInfo>> GetSubscriptionsAsync(CancellationToken ct = default) =>
        inventory.ListSubscriptionsAsync(ct);

    public Task<IReadOnlyList<DataSourceProbe>> RunPreflightAsync(string sampleSubscriptionId, CancellationToken ct = default) =>
        preflight.RunAsync(sampleSubscriptionId, ct);

    public Task<ScanRecord?> GetResumableAsync() => Scans.GetResumableAsync();
    public Task<IReadOnlyList<ScanRecord>> GetHistoryAsync() => Scans.ListAsync();

    public async Task<IReadOnlyList<ResourceSnapshotRecord>> GetSnapshotsAsync(string scanId)
    {
        var rows = await Scans.GetSnapshotsAsync(scanId);
        // a resumed scan can re-run query groups; last write for a resource id wins
        return rows.DistinctBy(r => r.ResourceId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Starts (or resumes) a scan in the background; false when one is already running.</summary>
    public async Task<bool> StartScanAsync(IReadOnlyList<string> subscriptionIds, bool resume = false)
    {
        if (Running || subscriptionIds.Count == 0) return false;
        Running = true;
        LastError = null;
        Progress = null;
        control = new ScanControl();
        cts = new CancellationTokenSource();
        StateChanged?.Invoke();

        var request = new ScanRequest(
            subscriptionIds.Count == 1 ? $"subscriptions/{subscriptionIds[0]}" : $"{subscriptionIds.Count} subscriptions",
            subscriptionIds.Count == 1 ? "Subscription" : "SubscriptionSet",
            subscriptionIds);

        ScanCheckpoint? resumePoint = null;
        if (resume)
        {
            var existing = await Scans.GetResumableAsync();
            if (existing?.CheckpointJson is { } json) resumePoint = ScanCheckpoint.FromJson(json);
        }

        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = p;
            StateChanged?.Invoke();
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await engine.RunAsync(request, sink, resumePoint, control, progress, cts.Token);
            }
            catch (OperationCanceledException) { /* manifest already says Canceled */ }
            catch (Exception e) { LastError = e.Message; }
            finally
            {
                Running = false;
                StateChanged?.Invoke();
            }
        });
        return true;
    }

    public async Task PauseAsync()
    {
        if (control is { } c)
            await c.PauseAsync();
        StateChanged?.Invoke();
    }

    public void ResumeRun()
    {
        control?.Resume();
        StateChanged?.Invoke();
    }

    public void Cancel() => cts?.Cancel();
}
