using AzureDesignStudio.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureDesignStudio.Server.Persistence;

/// <summary>
/// The design-time entry point for the SQLite migration set that lives in
/// <c>AzureDesignStudio.Services</c> (the desktop app's <c>deploy.db</c>). The file named below is
/// never opened: model snapshots and migration scripting need only the provider.
/// </summary>
/// <remarks>
/// The Server hosts this factory because <c>dotnet ef</c> needs a startup project that references
/// the Design package, and the provider-free Services project must not. Use it as
/// <c>dotnet ef migrations add &lt;Name&gt; --context SqliteDesignDbContext --project src/AzureDesignStudio.Services
/// --startup-project src/AzureDesignStudio.Server --output-dir Migrations/Sqlite</c>.
/// </remarks>
public sealed class SqliteDesignDbContextFactory : IDesignTimeDbContextFactory<SqliteDesignDbContext>
{
    public SqliteDesignDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteDesignDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), "ads-design-time-deploy.db")}")
            .Options);
}
