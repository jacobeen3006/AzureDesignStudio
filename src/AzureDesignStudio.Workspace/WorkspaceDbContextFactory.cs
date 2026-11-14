using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// The design-time entry point for <c>dotnet ef</c> and the pending-model-changes test. The tool
/// finds this factory without running any host, so it needs no workspace path and no app data.
/// </summary>
/// <remarks>
/// The file named here is never opened for model snapshots and migration scripting; it exists
/// only so the SQLite provider has a connection string to build the model from.
/// </remarks>
public sealed class WorkspaceDbContextFactory : IDesignTimeDbContextFactory<WorkspaceDbContext>
{
    public WorkspaceDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(Path.GetTempPath(), "ads-design-time-workspace.db"))
            .Options);
}
