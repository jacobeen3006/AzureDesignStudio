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
    public TopologyPositions Positions { get; } = new(db);

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
        // A resumed scan can re-run query groups; the last write for a resource id wins.
        // Rows arrive ordered by type and name, so the newest is picked by row id - taking
        // the first of each group kept the OLDEST copy.
        return rows
            .GroupBy(r => r.ResourceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(r => r.Id)!)
            .ToList();
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
            try
            {
                var existing = await Scans.GetResumableAsync();
                if (existing?.CheckpointJson is { } json) resumePoint = ScanCheckpoint.FromJson(json);
            }
            catch (Exception e)
            {
                // Running was already set: an unreadable checkpoint or a locked database
                // here left it set, and every later scan was refused until restart.
                Running = false;
                LastError = $"The previous scan cannot be resumed: {e.Message}";
                StateChanged?.Invoke();
                return false;
            }
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
