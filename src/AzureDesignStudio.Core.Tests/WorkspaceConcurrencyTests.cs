using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Mcp;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Success criterion 3 in executable form: a scan writing pages and governance findings while readers (in this process
    /// and in a real second one) read the same WAL workspace never sees SQLITE_BUSY or SQLITE_LOCKED.
    /// </summary>
    public class WorkspaceConcurrencyTests : IDisposable
    {
        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        [Fact]
        public async Task WritersAndReadersRunWithoutBusyOrLocked()
        {
            var seconds = int.TryParse(Environment.GetEnvironmentVariable("ADS_STRESS_SECONDS"), NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                && s is >= 1 and <= 120 ? s : 15;
            var path = _temp.Path("workspace.db");
            var db = new WorkspaceDb(path);
            var scans = new WorkspaceScans(db);
            var scan = await scans.StartAsync("tenant", "Tenant");
            var reader = new AdsWorkspace(path);

            var errors = new ConcurrentBag<Exception>();
            var writes = 0L;
            var reads = 0L;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

            async Task Loop(Func<int, Task> body)
            {
                var n = 0;
                while (!stop.IsCancellationRequested)
                {
                    try { await body(n++); }
                    catch (Exception e) { errors.Add(e); }
                }
            }

            var workers = new List<Task>();
            for (var w = 0; w < 2; w++)
            {
                var writer = w;
                workers.Add(Task.Run(() => Loop(async n =>
                {
                    var rows = Enumerable.Range(0, 20).Select(i => new InventorySnapshotRow(
                        $"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Test/widgets/p{writer}-{n}-{i}",
                        $"p{writer}-{n}-{i}", "Microsoft.Test/widgets", "westeurope", "rg", "s", null, """{"size":3}""")).ToList();
                    await scans.PersistPageAsync(scan.Id, rows, $$"""{"page":{{n}}}""");
                    Interlocked.Increment(ref writes);
                })));
                workers.Add(Task.Run(() => Loop(async n =>
                {
                    var rows = Enumerable.Range(0, 10).Select(i => new GovernanceFindingRow(
                        "Policy", $"g{writer}-{n}-{i}", $"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Test/widgets/p{writer}-{n}-{i}",
                        "s", "Medium", "Policy", "require-tags", null, null, null)).ToList();
                    await scans.PersistSourcePageAsync(scan.Id, rows, $$"""{"source":{{n}}}""");
                    Interlocked.Increment(ref writes);
                })));
            }
            for (var r = 0; r < 2; r++)
            {
                workers.Add(Task.Run(() => Loop(async _ =>
                {
                    var list = await reader.ListScansAsync();
                    if (list.Count > 0) await reader.GetSnapshotsAsync(list[0].Id);
                    Interlocked.Increment(ref reads);
                })));
            }

            // A real second process reading the same file through the shipped reader code.
            var child = StartSelfCheck(path, Math.Max(1, seconds - 3));
            var childStderr = child.StandardError.ReadToEndAsync();
            var childExited = await Task.Run(() => child.WaitForExit(TimeSpan.FromSeconds(seconds + 60)));
            if (!childExited) child.Kill(entireProcessTree: true);
            var childLog = await childStderr;

            await Task.WhenAll(workers);

            Assert.True(errors.IsEmpty, Describe(errors));
            Assert.True(Interlocked.Read(ref writes) > 0, "no write completed");
            Assert.True(Interlocked.Read(ref reads) > 0, "no read completed");
            Assert.True(childExited, "the second process did not finish: " + childLog);
            Assert.True(child.ExitCode == 0, $"second process exit code {child.ExitCode}: {childLog}");
            Assert.Contains("errors=0", childLog);
            Assert.Matches(@"reads=[1-9]\d*", childLog);

            // WAL is persistent in the file; a busy checkpoint is acceptable (growth under a churning reader, RESEARCH Pitfall 9).
            await using var ctx = db.CreateContext();
            await ctx.Database.OpenConnectionAsync();
            await using (var mode = ctx.Database.GetDbConnection().CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode";
                Assert.Equal("wal", Convert.ToString(await mode.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
            }
            await using var checkpoint = ctx.Database.GetDbConnection().CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
            await checkpoint.ExecuteNonQueryAsync();
        }

        private static Process StartSelfCheck(string databasePath, int seconds)
        {
            var mcpDll = typeof(AdsWorkspace).Assembly.Location;
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AzureDesignStudio.Core.Tests.runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AzureDesignStudio.Core.Tests.deps.json"));
            start.ArgumentList.Add(mcpDll);
            start.ArgumentList.Add("--db");
            start.ArgumentList.Add(databasePath);
            start.ArgumentList.Add("--self-check");
            start.ArgumentList.Add(seconds.ToString(CultureInfo.InvariantCulture));

            var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("the process did not start");
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"dotnet exec could not start the ads-mcp child: {e.Message}", e);
            }
            // stdout is the MCP protocol channel; nothing should be written there in self-check mode, but drain it anyway.
            _ = process.StandardOutput.ReadToEndAsync();
            return process;
        }

        private static string Describe(IEnumerable<Exception> errors)
        {
            var list = errors.ToList();
            if (list.Count == 0) return "";
            var first = list[0];
            var code = first is SqliteException sqlite ? $" (SQLite {sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode})" : "";
            return $"{list.Count} worker exception(s); first{code}: {first}";
        }
    }
}
