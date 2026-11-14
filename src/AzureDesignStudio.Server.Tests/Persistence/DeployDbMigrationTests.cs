using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Server.Persistence;
using AzureDesignStudio.Services.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Persistence;

/// <summary>
/// The SQLite migration set of the desktop <c>deploy.db</c>, on real files in a temp directory.
/// Test code may call <c>Migrate()</c> and the model-creation method; the source guards scan only
/// the production projects.
/// </summary>
public sealed class DeployDbMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ads-deploydb-" + Guid.NewGuid().ToString("N"));

    public DeployDbMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string NewPath(string name = "deploy.db") => Path.Combine(_dir, name);

    private static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    /// <summary>The file a build before migrations produced: created from the model.</summary>
    private static void CreateFromModel(string path)
    {
        using var db = new DesignDbContext(
            new DbContextOptionsBuilder<DesignDbContext>().UseSqlite(ConnectionString(path)).Options);
        db.Database.EnsureCreated();
    }

    private static void MigrateWithSqliteSet(string path)
    {
        using var db = new SqliteDesignDbContext(
            new DbContextOptionsBuilder<SqliteDesignDbContext>().UseSqlite(ConnectionString(path)).Options);
        db.Database.Migrate();
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void FreshFile_MigratesAndMatchesAModelCreatedFile()
    {
        var migrated = NewPath("migrated.db");
        var modelBuilt = NewPath("model.db");

        MigrateWithSqliteSet(migrated);
        CreateFromModel(modelBuilt);

        Assert.Equal(SqliteSchemaDescriber.Describe(modelBuilt), SqliteSchemaDescriber.Describe(migrated));
        Assert.Equal(7, SqliteSchemaDescriber.TableNames(migrated).Count);
    }

    [Fact]
    public void ModelCreatedFile_AdoptsTheBaseline()
    {
        var path = NewPath();
        CreateFromModel(path);
        using (var connection = new SqliteConnection(ConnectionString(path)))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO AdsDesigns (Name, UserId, DesignData, LastModified) " +
                "VALUES ('keep me', '11111111-1111-1111-1111-111111111111', '{}', '2026-01-01 00:00:00')";
            insert.ExecuteNonQuery();
        }

        MigrateWithSqliteSet(path);

        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.EndsWith("_Baseline", ScalarText(path, "SELECT MigrationId FROM __EFMigrationsHistory"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM AdsDesigns WHERE Name = 'keep me'"));
    }

    [Fact]
    public void PendingModelChanges_AreNone()
    {
        using var db = new SqliteDesignDbContextFactory().CreateDbContext([]);

        // The SQLite snapshot and the model agree: a model edit without a new migration fails here.
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static string ScalarText(string path, string sql)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }
}
