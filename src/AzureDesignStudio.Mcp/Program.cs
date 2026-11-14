using AzureDesignStudio.Mcp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

// This process's stdout is the MCP stdio protocol channel: every diagnostic goes to
// stderr (Console.Error and a stderr-bound logger) - never Console.WriteLine.
// The server is read-only by construction: the workspace database opens read-only (or, when the
// platform refuses that, read-write with query_only) and no tool here mutates the workspace or
// calls ARM. The rung that opened it is reported on stderr.
var dbPath = ResolveDbPath(args);
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"ads-mcp: workspace database not found: {dbPath}");
    Console.Error.WriteLine("ads-mcp: pass --db <path> or set ADS_WORKSPACE_DB to the desktop workspace.db.");
    return 2;
}

AdsWorkspace workspace;
try
{
    workspace = new AdsWorkspace(dbPath);
}
catch (Exception e) when (e is InvalidOperationException or SqliteException)
{
    Console.Error.WriteLine($"ads-mcp: cannot open workspace: {e.Message}");
    return 2;
}
Console.Error.WriteLine($"ads-mcp: workspace opened {workspace.OpenMode}: {workspace.OpenDetail}");

// ads-mcp --db <path> --self-check <seconds>: a bounded read loop instead of the MCP host.
var selfCheckSeconds = ResolveSelfCheckSeconds(args);
if (selfCheckSeconds is { } seconds)
    return await AdsSelfCheck.RunAsync(workspace, TimeSpan.FromSeconds(seconds), Console.Error, CancellationToken.None);

var builder = Host.CreateApplicationBuilder(args);

// The default console logger writes to stdout (the protocol channel) - rebind all
// logging to stderr.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(workspace);
builder.Services.AddTransient<AdsMcpTools>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<AdsMcpTools>();

await builder.Build().RunAsync();
return 0;

static int? ResolveSelfCheckSeconds(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (!string.Equals(args[i], "--self-check", StringComparison.Ordinal)) continue;
        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds) && seconds is >= 1 and <= 600)
            return seconds;
        Console.Error.WriteLine("ads-mcp: --self-check needs a number of seconds from 1 to 600.");
        Environment.Exit(2);
    }
    return null;
}

static string ResolveDbPath(string[] args)
{
    for (var i = 0; i + 1 < args.Length; i++)
        if (string.Equals(args[i], "--db", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[i + 1]))
            return Path.GetFullPath(args[i + 1]);

    var env = Environment.GetEnvironmentVariable("ADS_WORKSPACE_DB");
    if (!string.IsNullOrWhiteSpace(env))
        return Path.GetFullPath(env);

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Azure Design Studio", "AzureDesignStudio.Desktop", "Data", "workspace.db");
}
