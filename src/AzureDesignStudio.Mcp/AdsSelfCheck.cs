using System.Diagnostics;

namespace AzureDesignStudio.Mcp;

/// <summary>
/// A bounded read loop over the workspace, run by <c>ads-mcp --self-check &lt;seconds&gt;</c> instead of the MCP host.
/// It exists so a test (and an operator) can prove that a reader in its own process keeps reading while the desktop
/// app writes: every read is counted, every exception is counted and none stops the loop. Output goes to the log
/// writer (stderr in the host) - stdout is the MCP protocol channel.
/// </summary>
internal static class AdsSelfCheck
{
    public static async Task<int> RunAsync(AdsWorkspace workspace, TimeSpan duration, TextWriter log, CancellationToken ct)
    {
        var reads = 0;
        var errors = 0;
        string? firstError = null;
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < duration && !ct.IsCancellationRequested)
        {
            try
            {
                var scans = await workspace.ListScansAsync();
                reads++;
                if (scans.Count > 0)
                {
                    await workspace.GetSnapshotsAsync(scans[0].Id);
                    reads++;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                errors++;
                firstError ??= e.Message;
            }
            await Task.Yield();
        }

        await log.WriteLineAsync($"ads-mcp: self-check mode={workspace.OpenMode} reads={reads} errors={errors}");
        if (errors > 0)
            await log.WriteLineAsync($"ads-mcp: self-check first error: {firstError}");
        return errors == 0 ? 0 : 3;
    }
}
