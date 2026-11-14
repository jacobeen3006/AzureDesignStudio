using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureDesignStudio.Server.Persistence;

/// <summary>
/// The design-time entry point for <c>dotnet ef</c>. The tool finds this factory without running
/// <c>Program</c>, so it needs neither the host's configuration nor a Key Vault.
/// </summary>
/// <remarks>
/// The SQL Server provider is chosen on purpose: the model it builds carries the ISJSON check
/// constraint on the designs table, and the deployed database is SQL Server. The connection string
/// is never opened for model snapshots and migration scripting, and it holds no credential -
/// integrated security against LocalDB only. The factory lives in the Server because
/// <c>AzureDesignStudio.Services</c> must not reference a database provider.
/// </remarks>
public sealed class DesignDbContextFactory : IDesignTimeDbContextFactory<SqlServerDesignDbContext>
{
    public SqlServerDesignDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerDesignDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ads-design-time;Integrated Security=true")
            .Options);
}
