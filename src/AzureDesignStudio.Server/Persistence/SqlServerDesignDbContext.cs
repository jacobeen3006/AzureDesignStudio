using AzureDesignStudio.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Server.Persistence;

/// <summary>
/// The SQL Server flavour of <see cref="DesignDbContext"/>. It exists to own the SQL Server
/// migrations: the generated model snapshot and designer code use the SqlServer provider's
/// builder extensions, which cannot compile in the provider-free <c>AzureDesignStudio.Services</c>
/// project (CONTEXT autonomous decision [Research C1]), and EF looks for migrations in the
/// assembly of the context type. The model itself is the base class's, so services keep asking
/// for <see cref="DesignDbContext"/>; <see cref="DatabaseProvider.AddDesignDbContext"/> maps that
/// request to this type when the provider is SQL Server.
/// </summary>
public sealed class SqlServerDesignDbContext(DbContextOptions<SqlServerDesignDbContext> options)
    : DesignDbContext(options);
