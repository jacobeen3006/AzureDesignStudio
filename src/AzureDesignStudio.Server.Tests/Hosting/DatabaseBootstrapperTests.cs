using AzureDesignStudio.Server.Persistence;
using AzureDesignStudio.Services.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// <see cref="DatabaseBootstrapper.Initialize"/> wired the way Program.cs wires it (AddDbContext
/// with <see cref="DatabaseProvider.Configure"/>), on a real SQLite file in the temp directory.
/// </summary>
public sealed class DatabaseBootstrapperTests : IDisposable
{
    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), "ads-boot-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        // Pooled connections keep the file locked on Windows.
        SqliteConnection.ClearAllPools();

        var directory = Path.GetDirectoryName(_dbFile)!;
        var prefix = Path.GetFileName(_dbFile);
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

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void Dispose()
        {
        }

        private sealed class Recorder(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                    owner._entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static ServiceProvider BuildServices(IDictionary<string, string?> values, ILoggerProvider? logs = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            if (logs is not null)
                builder.AddProvider(logs);
        });
        services.AddSingleton(configuration);
        services.AddDbContext<DesignDbContext>((serviceProvider, options) =>
            DatabaseProvider.Configure(options, serviceProvider.GetRequiredService<IConfiguration>()));
        return services.BuildServiceProvider();
    }

    private Dictionary<string, string?> SqliteConfiguration() => new()
    {
        ["Database:Provider"] = "Sqlite",
        ["ConnectionStrings:ads-main"] = "Data Source=" + _dbFile,
    };

    [Fact]
    public void InitializeCreatesEverySchemaTableOnASqliteFileWithoutRunningTransactSql()
    {
        using var services = BuildServices(SqliteConfiguration());

        // A T-SQL statement on SQLite is a syntax error near IF.
        DatabaseBootstrapper.Initialize(services);

        Assert.True(File.Exists(_dbFile));

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
        Assert.Equal(0, db.AdsDesigns.Count());
        Assert.Equal(0, db.AzureSubscriptions.Count());
        Assert.Equal(0, db.AdsPlanApprovals.Count());
        Assert.Equal(0, db.AdsPlanApplyEvents.Count());
        Assert.Equal(0, db.AdsGuardrailPolicies.Count());
        Assert.Equal(0, db.AdsDecomApprovals.Count());
        Assert.Equal(0, db.AdsDecomWindowEvents.Count());
    }

    [Fact]
    public void InitializeTwiceKeepsTheExistingRows()
    {
        using var services = BuildServices(SqliteConfiguration());
        DatabaseBootstrapper.Initialize(services);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
            db.AdsDesigns.Add(new DesignModel
            {
                Name = "kept",
                UserId = Guid.NewGuid(),
                DesignData = "{}",
                LastModified = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        DatabaseBootstrapper.Initialize(services);

        using var check = services.CreateScope();
        Assert.Equal(1, check.ServiceProvider.GetRequiredService<DesignDbContext>().AdsDesigns.Count());
    }

    [Fact]
    public void InitializeOnSqliteWarnsThatTheDatabaseIsALocalFile()
    {
        var logs = new RecordingLoggerProvider();
        using var services = BuildServices(SqliteConfiguration(), logs);

        DatabaseBootstrapper.Initialize(services);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Database:Provider is Sqlite"));
    }

    [Fact]
    public void InitializeOnAnInMemoryDatabaseWarnsAndSucceeds()
    {
        var logs = new RecordingLoggerProvider();
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "InMemory",
            ["Database:InMemoryName"] = Guid.NewGuid().ToString(),
        }, logs);

        DatabaseBootstrapper.Initialize(services);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Database:Provider is InMemory"));
    }

    [Fact]
    public void InitializeWithoutAConnectionStringStopsWithTheOperatorMessage()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        var failure = Assert.Throws<InvalidOperationException>(() => DatabaseBootstrapper.Initialize(services));

        Assert.Contains("No database is configured", failure.Message);
        Assert.Contains("ConnectionStrings__ads-main", failure.Message);
    }

    [Fact]
    public void DesignTimeFactoryReportsSqlServerAndBuildsTheModelWithoutConnecting()
    {
        using var db = new DesignDbContextFactory().CreateDbContext([]);

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", db.Database.ProviderName);

        // Only inspected: nothing here opens a connection, so no LocalDB is needed.
        var connectionString = db.Database.GetConnectionString();
        Assert.NotNull(connectionString);
        Assert.DoesNotContain("Password", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User ID", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Integrated Security", connectionString, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(db.Model.FindEntityType(typeof(DesignModel)));

        var model = db.GetService<IDesignTimeModel>().Model;
        var designs = Assert.Single(model.GetRelationalModel().Tables, t => t.EntityTypeMappings.Any(m => m.TypeBase.ClrType == typeof(DesignModel)));
        Assert.Contains(designs.CheckConstraints, c => c.Name == "ADS_CHECK_JSON");
    }
}
