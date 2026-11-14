using AzureDesignStudio.Server.Persistence;
using AzureDesignStudio.Services.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>The rules of <see cref="DatabaseProvider"/>, one at a time. Nothing here opens a
/// connection: building the options and reading the provider name needs no database.</summary>
public class DatabaseProviderTests : IDisposable
{
    private readonly string _unusedFile = Path.Combine(Path.GetTempPath(), "ads-provider-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        var directory = Path.GetDirectoryName(_unusedFile)!;
        var prefix = Path.GetFileName(_unusedFile);
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

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static DesignDbContext Context(IConfiguration configuration)
    {
        var builder = new DbContextOptionsBuilder<DesignDbContext>();
        DatabaseProvider.Configure(builder, configuration);
        return new DesignDbContext(builder.Options);
    }

    private static DesignModel Design(string name) => new()
    {
        Name = name,
        UserId = Guid.NewGuid(),
        DesignData = "{}",
        LastModified = DateTime.UtcNow,
    };

    [Fact]
    public void DefaultInMemoryNameIsTheOperatorVisibleValue() =>
        Assert.Equal("adsdb-inmem", DatabaseProvider.DefaultInMemoryName);

    [Fact]
    public void KeysAreTheOperatorVisibleSettings()
    {
        Assert.Equal("Database:Provider", DatabaseProvider.Key);
        Assert.Equal("Database:InMemoryName", DatabaseProvider.InMemoryNameKey);
    }

    [Fact]
    public void UnsetProviderIsSqlServer() =>
        Assert.Equal(DatabaseKind.SqlServer, DatabaseProvider.Resolve(Config()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankProviderIsSqlServer(string value) =>
        Assert.Equal(DatabaseKind.SqlServer, DatabaseProvider.Resolve(Config((DatabaseProvider.Key, value))));

    [Theory]
    [InlineData("SqlServer", "SqlServer")]
    [InlineData("sqlserver", "SqlServer")]
    [InlineData("Sqlite", "Sqlite")]
    [InlineData("SQLITE", "Sqlite")]
    [InlineData("InMemory", "InMemory")]
    [InlineData("inmemory", "InMemory")]
    [InlineData("  Sqlite  ", "Sqlite")]
    public void ProviderNamesAreCaseInsensitiveAndTrimmed(string value, string expected) =>
        // DatabaseKind is internal, so a public test signature carries its name.
        Assert.Equal(expected, DatabaseProvider.Resolve(Config((DatabaseProvider.Key, value))).ToString());

    [Fact]
    public void UnknownProviderNamesTheKeyAndTheAcceptedValues()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => DatabaseProvider.Resolve(Config((DatabaseProvider.Key, "postgres"))));

        Assert.Contains("Database:Provider", failure.Message);
        Assert.Contains("postgres", failure.Message);
        Assert.Contains("SqlServer", failure.Message);
        Assert.Contains("Sqlite", failure.Message);
        Assert.Contains("InMemory", failure.Message);
    }

    [Fact]
    public void SqlServerProviderIsConfiguredFromTheConnectionString()
    {
        using var db = Context(Config(("ConnectionStrings:ads-main", "Server=localhost;Database=x")));

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", db.Database.ProviderName);
    }

    [Fact]
    public void SqliteProviderIsConfiguredFromTheConnectionString()
    {
        using var db = Context(Config(
            (DatabaseProvider.Key, "Sqlite"),
            ("ConnectionStrings:ads-main", "Data Source=" + _unusedFile)));

        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", db.Database.ProviderName);
    }

    [Fact]
    public void InMemoryProviderNeedsNoConnectionString()
    {
        using var db = Context(Config(
            (DatabaseProvider.Key, "InMemory"),
            (DatabaseProvider.InMemoryNameKey, Guid.NewGuid().ToString())));

        Assert.Equal("Microsoft.EntityFrameworkCore.InMemory", db.Database.ProviderName);
    }

    [Fact]
    public void TheVaultStyleKeyWinsOverTheConnectionStringsSection()
    {
        var vault = Path.Combine(Path.GetTempPath(), "ads-vault-" + Guid.NewGuid().ToString("N") + ".db");
        var section = Path.Combine(Path.GetTempPath(), "ads-section-" + Guid.NewGuid().ToString("N") + ".db");

        using var db = Context(Config(
            (DatabaseProvider.Key, "Sqlite"),
            ("ads-main", "Data Source=" + vault),
            ("ConnectionStrings:ads-main", "Data Source=" + section)));

        var connectionString = db.Database.GetConnectionString();
        Assert.NotNull(connectionString);
        Assert.Contains(Path.GetFileName(vault), connectionString);
        Assert.DoesNotContain(Path.GetFileName(section), connectionString);
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("Sqlite")]
    public void NoConnectionStringStopsWithTheOperatorMessage(string provider)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => Context(Config((DatabaseProvider.Key, provider))));

        Assert.Contains("No database is configured", failure.Message);
        Assert.Contains("ConnectionStrings__ads-main", failure.Message);
    }

    [Theory]
    [InlineData("SqlServer", "<connection string>")]
    [InlineData("Sqlite", "<connection string>")]
    [InlineData("SqlServer", "")]
    public void SampleConnectionStringsCountAsUnset(string provider, string connectionString)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => Context(Config((DatabaseProvider.Key, provider), ("ConnectionStrings:ads-main", connectionString))));

        Assert.Contains("No database is configured", failure.Message);
        Assert.Contains("ConnectionStrings__ads-main", failure.Message);
    }

    [Fact]
    public void InMemoryContextsWithTheSameNameShareData()
    {
        var configuration = Config(
            (DatabaseProvider.Key, "InMemory"),
            (DatabaseProvider.InMemoryNameKey, Guid.NewGuid().ToString()));

        using (var writer = Context(configuration))
        {
            writer.AdsDesigns.Add(Design("shared"));
            writer.SaveChanges();
        }

        using var reader = Context(configuration);
        Assert.Equal(1, reader.AdsDesigns.Count());
    }

    [Fact]
    public void InMemoryContextsWithDifferentNamesDoNotShareData()
    {
        using (var writer = Context(Config(
            (DatabaseProvider.Key, "InMemory"),
            (DatabaseProvider.InMemoryNameKey, Guid.NewGuid().ToString()))))
        {
            writer.AdsDesigns.Add(Design("mine"));
            writer.SaveChanges();
        }

        using var reader = Context(Config(
            (DatabaseProvider.Key, "InMemory"),
            (DatabaseProvider.InMemoryNameKey, Guid.NewGuid().ToString())));
        Assert.Equal(0, reader.AdsDesigns.Count());
    }
}
