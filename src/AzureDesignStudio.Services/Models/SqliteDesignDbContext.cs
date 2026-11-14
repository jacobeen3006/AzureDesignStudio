using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Services.Models;

/// <summary>
/// The SQLite flavour of <see cref="DesignDbContext"/>, used by the desktop app's <c>deploy.db</c>.
/// It exists so the SQLite migration set is attached to a type in this provider-free assembly: EF
/// looks for migrations in the assembly of the context type, and the SQLite model snapshot compiles
/// without a provider package (the SQL Server snapshot does not, so it lives in the Server project).
/// The model is the base class's, so services keep asking for <see cref="DesignDbContext"/>.
/// </summary>
public sealed class SqliteDesignDbContext(DbContextOptions<SqliteDesignDbContext> options)
    : DesignDbContext(options);
