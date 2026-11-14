using Xunit;

namespace AzureDesignStudio.Server.Tests.Persistence;

/// <summary>
/// A fact that runs only when <c>ADS_TEST_SQLSERVER</c> names a throwaway SQL Server (a container,
/// see src/RunSqlServerTests.sh). Without it the test is skipped, never failed. xUnit 2.9 has no
/// runtime skip API, so the skip reason is decided when the attribute is constructed.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string VariableName = "ADS_TEST_SQLSERVER";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariableName)))
            Skip = VariableName + " not set";
    }
}
