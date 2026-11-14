using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Auth;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// WR-08: SetGuardrailPolicy and SetTenantSettings retry only a failed INSERT (a concurrent first write
/// by the same scope), once. Every other failure, and a second failed insert, is logged and answered
/// in-band as 503 with nothing stored - never an unhandled RPC fault.
/// </summary>
public sealed class WriteFailureTests
{
    private const string Policy = """{"scopes":[],"tags":[],"types":["Microsoft.Storage/storageAccounts"]}""";
    private static readonly string[] Admin = [AdsIdentity.GuardrailAdminRole];

    private static ServerCallContext AdminOf(Guid tenant) =>
        TestServerCallContext.ForUser(Guid.NewGuid(), "Ada", tenant, Admin);

    /// <summary>Throws a <see cref="DbUpdateException"/> on the first <paramref name="failures"/> saves
    /// that carry a security event and counts every save attempt that does.</summary>
    private sealed class FailSaves(int failures) : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<SecurityEventModel>().Any(e => e.State == EntityState.Added))
            {
                Attempts++;
                if (Attempts <= failures)
                    throw new DbUpdateException("simulated store failure");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<DeployService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static DesignDbContext Failing(string store, FailSaves interceptor) => new(
        new DbContextOptionsBuilder<DesignDbContext>().UseInMemoryDatabase(store).AddInterceptors(interceptor).Options);

    private static DesignDbContext Plain(string store) => new(
        new DbContextOptionsBuilder<DesignDbContext>().UseInMemoryDatabase(store).Options);

    private static DeployService ServiceOn(DesignDbContext db, ILogger<DeployService> logger) => new(db, logger,
        new FakeCryptoService(), new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl), new ScriptedHttp(_ => null));

    // --- guardrail policy ---

    [Fact]
    public async Task AFirstInsertThatFailsOnceIsRetriedAndSucceeds()
    {
        var store = Guid.NewGuid().ToString();
        var interceptor = new FailSaves(failures: 1);
        await using var db = Failing(store, interceptor);

        var response = await ServiceOn(db, new CapturingLogger()).SetGuardrailPolicy(
            new GuardrailPolicyRequest { Json = Policy }, AdminOf(TestTenants.Contoso));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, interceptor.Attempts);
        await using var verify = Plain(store);
        Assert.Single(verify.AdsGuardrailPolicies);
        Assert.Single(verify.AdsSecurityEvents);
    }

    [Fact]
    public async Task ASecondFailedInsertIsAnsweredInBandLoggedAndStoresNothing()
    {
        var store = Guid.NewGuid().ToString();
        var interceptor = new FailSaves(failures: 2);
        var logger = new CapturingLogger();
        await using var db = Failing(store, interceptor);

        var response = await ServiceOn(db, logger).SetGuardrailPolicy(
            new GuardrailPolicyRequest { Json = Policy }, AdminOf(TestTenants.Contoso));

        Assert.Equal(503, response.StatusCode);
        Assert.StartsWith("GUARDRAIL_POLICY_NOT_SAVED", response.ErrorMessage);
        Assert.Equal("", response.Json);
        Assert.Equal(2, interceptor.Attempts);
        // The failed commit carried the audit event, so the line is critical.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("Guardrail policy"));
        await using var verify = Plain(store);
        Assert.Empty(verify.AdsGuardrailPolicies);
        Assert.Empty(verify.AdsSecurityEvents);
    }

    [Fact]
    public async Task AFailedUpdateIsNotRetriedAndIsAnsweredInBand()
    {
        var store = Guid.NewGuid().ToString();
        await using (var seed = Plain(store))
            await ServiceOn(seed, new CapturingLogger()).SetGuardrailPolicy(
                new GuardrailPolicyRequest { Json = Policy }, AdminOf(TestTenants.Contoso));
        var interceptor = new FailSaves(failures: 5);
        var logger = new CapturingLogger();
        await using var db = Failing(store, interceptor);

        var response = await ServiceOn(db, logger).SetGuardrailPolicy(
            new GuardrailPolicyRequest { Json = """{"types":["Microsoft.Sql/servers"]}""" }, AdminOf(TestTenants.Contoso));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal(1, interceptor.Attempts); // an update is not a key race: no second attempt
        await using var verify = Plain(store);
        Assert.Equal(Policy, (await verify.AdsGuardrailPolicies.SingleAsync()).Json);
    }

    // --- tenant settings ---

    [Fact]
    public async Task TenantSettingsRetryAFirstInsertOnce()
    {
        var store = Guid.NewGuid().ToString();
        var interceptor = new FailSaves(failures: 1);
        await using var db = Failing(store, interceptor);

        var response = await ServiceOn(db, new CapturingLogger()).SetTenantSettings(
            new TenantSettingsRequest { AllowSelfApproval = true, GuardrailPolicyMode = "merge" }, AdminOf(TestTenants.Contoso));

        Assert.Equal(200, response.StatusCode);
        Assert.True(response.AllowSelfApproval);
        Assert.Equal(2, interceptor.Attempts);
    }

    [Fact]
    public async Task TenantSettingsSecondFailureIsAnsweredInBandAndLogged()
    {
        var store = Guid.NewGuid().ToString();
        var interceptor = new FailSaves(failures: 2);
        var logger = new CapturingLogger();
        await using var db = Failing(store, interceptor);

        var response = await ServiceOn(db, logger).SetTenantSettings(
            new TenantSettingsRequest { AllowSelfApproval = true, GuardrailPolicyMode = "merge" }, AdminOf(TestTenants.Contoso));

        Assert.Equal(503, response.StatusCode);
        Assert.StartsWith("TENANT_SETTINGS_NOT_SAVED", response.ErrorMessage);
        Assert.False(response.AllowSelfApproval);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("Tenant settings"));
        await using var verify = Plain(store);
        Assert.Empty(verify.AdsTenantSettings);
        Assert.Empty(verify.AdsSecurityEvents);
    }

    [Fact]
    public async Task TenantSettingsFailedUpdateIsNotRetried()
    {
        var store = Guid.NewGuid().ToString();
        await using (var seed = Plain(store))
            await ServiceOn(seed, new CapturingLogger()).SetTenantSettings(
                new TenantSettingsRequest { AllowSelfApproval = true, GuardrailPolicyMode = "replace" }, AdminOf(TestTenants.Contoso));
        var interceptor = new FailSaves(failures: 5);
        await using var db = Failing(store, interceptor);

        var response = await ServiceOn(db, new CapturingLogger()).SetTenantSettings(
            new TenantSettingsRequest { AllowSelfApproval = false, GuardrailPolicyMode = "replace" }, AdminOf(TestTenants.Contoso));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal(1, interceptor.Attempts);
        await using var verify = Plain(store);
        Assert.True((await verify.AdsTenantSettings.SingleAsync()).AllowSelfApproval);
    }
}
