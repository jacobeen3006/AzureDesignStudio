using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// The real server host with only the authentication scheme replaced. It boots on the checked-in
/// sample configuration: no Key Vault name, no Azure credential, no developer database. By default
/// it runs on its own temporary SQLite file; an InMemory host gets its own store name.
/// </summary>
public sealed class AdsServerFactory : WebApplicationFactory<Program>
{
    private int _cleaned;

    /// <summary>"Sqlite" (default) or "InMemory".</summary>
    public string DatabaseProviderName { get; init; } = "Sqlite";

    /// <summary>The SQLite database file of this host, in the temp directory.</summary>
    public string DbFile { get; } = Path.Combine(Path.GetTempPath(), $"ads-test-{Guid.NewGuid():N}.db");

    /// <summary>The InMemory store name of this host; never shared with another host.</summary>
    public string InMemoryName { get; } = $"ads-test-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The factory default is Development, which switches on development-only wiring.
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration: top-level Program.cs code cannot see the latter.
        switch (DatabaseProviderName)
        {
            case "Sqlite":
                builder.UseSetting("Database:Provider", "Sqlite");
                // Read lazily when the first context is built, so this route is fine.
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:ads-main"] = $"Data Source={DbFile}",
                    }));
                break;
            case "InMemory":
                builder.UseSetting("Database:Provider", "InMemory");
                builder.UseSetting("Database:InMemoryName", InMemoryName);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported {nameof(DatabaseProviderName)} \"{DatabaseProviderName}\". Use \"Sqlite\" or \"InMemory\".");
        }

        // Pin every ambient input the tests assume, so a shell or CI job that exports a real vault,
        // a telemetry resource or another database provider cannot leak into a test host. Empty
        // counts as unset (ServiceTools.IsConfigured). UseSetting covers what Program.cs reads at
        // top level; the in-memory layer below is added last, so it also beats environment variables
        // for the values read lazily (CryptoService reads the vault names on first use).
        builder.UseSetting("KeyVaultName", "");
        builder.UseSetting("KeyVaultKeyName", "");
        builder.UseSetting("ApplicationInsights:ConnectionString", "");
        builder.UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING", "");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeyVaultName"] = "",
                ["KeyVaultKeyName"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "",
            }));

        builder.ConfigureTestServices(services =>
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            DeleteDatabaseFiles();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        DeleteDatabaseFiles();
    }

    /// <summary>Closes pooled connections (they keep the file locked on Windows) and deletes the
    /// database and its -wal, -shm and -journal side files. Idempotent.</summary>
    private void DeleteDatabaseFiles()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0)
            return;

        SqliteConnection.ClearAllPools();

        var directory = Path.GetDirectoryName(DbFile)!;
        var prefix = Path.GetFileName(DbFile);
        foreach (var file in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leftover temp file must not fail the test run.
            }
        }
    }
}
