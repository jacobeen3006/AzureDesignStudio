using AzureDesignStudio.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

// This process's stdout is the MCP stdio protocol channel: every diagnostic goes to
// stderr (Console.Error and a stderr-bound logger) - never Console.WriteLine.
// The server is read-only by construction: the workspace database opens Mode=ReadOnly
// and no tool here mutates the workspace or calls ARM.
var dbPath = ResolveDbPath(args);
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"ads-mcp: workspace database not found: {dbPath}");
    Console.Error.WriteLine("ads-mcp: pass --db <path> or set ADS_WORKSPACE_DB to the desktop workspace.db.");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args);

// The default console logger writes to stdout (the protocol channel) - rebind all
// logging to stderr.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new AdsWorkspace(dbPath));
builder.Services.AddTransient<AdsMcpTools>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<AdsMcpTools>();

await builder.Build().RunAsync();
return 0;

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
