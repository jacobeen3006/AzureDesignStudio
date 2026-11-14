using System.Text.RegularExpressions;
using AzureDesignStudio.Server.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Server.Tests.Persistence;

/// <summary>
/// One uniquely named, throwaway database on the server <c>ADS_TEST_SQLSERVER</c> names. It is
/// created on construction and dropped on dispose; nothing outside that database is touched.
/// </summary>
public sealed class SqlServerDatabaseFixture : IDisposable
{
    private static readonly Regex GoLine = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _server;

    public SqlServerDatabaseFixture()
    {
        _server = Environment.GetEnvironmentVariable(SqlServerFactAttribute.VariableName)
            ?? throw new InvalidOperationException(SqlServerFactAttribute.VariableName + " is not set.");

        DatabaseName = "ads_t_" + Guid.NewGuid().ToString("N");
        MasterConnectionString = With(_server, b => b.InitialCatalog = "master");
        ConnectionString = With(_server, b => b.InitialCatalog = DatabaseName);
        CreateDatabase();
    }

    public string DatabaseName { get; }

    public string MasterConnectionString { get; }

    /// <summary>The connection string of the throwaway database.</summary>
    public string ConnectionString { get; }

    /// <summary>A connection string for another database name on the same server, for tests that
    /// need a database that does not exist yet.</summary>
    public string ConnectionStringFor(string databaseName) => With(_server, b => b.InitialCatalog = databaseName);

    public SqlServerDesignDbContext CreateContext() => CreateContext(ConnectionString);

    public static SqlServerDesignDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<SqlServerDesignDbContext>().UseSqlServer(connectionString).Options);

    /// <summary>Runs a script whose batches are separated by lines holding only GO.</summary>
    public void ExecuteScript(string sql) => ExecuteScript(ConnectionString, sql);

    public static void ExecuteScript(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        foreach (var batch in GoLine.Split(sql))
        {
            if (string.IsNullOrWhiteSpace(batch))
                continue;
            using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            command.ExecuteNonQuery();
        }
    }

    public static T Scalar<T>(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public static string LegacyScript() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "Fixtures", "sqlserver-legacy.sql"));

    /// <summary>Drops a database by name on the test server, ignoring errors.</summary>
    public void DropDatabase(string databaseName)
    {
        try
        {
            SqlConnection.ClearAllPools();
            using var connection = new SqlConnection(MasterConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
            command.ExecuteNonQuery();
        }
        catch
        {
            // A leftover throwaway database must not fail the run; the container is removed anyway.
        }
    }

    public void Dispose() => DropDatabase(DatabaseName);

    private static string With(string connectionString, Action<SqlConnectionStringBuilder> change)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        change(builder);
        return builder.ConnectionString;
    }

    /// <summary>TCP accepts connections well before the server accepts a login, so the first
    /// statement retries for up to 90 seconds.</summary>
    private void CreateDatabase()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                using var connection = new SqlConnection(MasterConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{DatabaseName}]";
                command.ExecuteNonQuery();
                return;
            }
            catch (SqlException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
            }
        }
    }
}
