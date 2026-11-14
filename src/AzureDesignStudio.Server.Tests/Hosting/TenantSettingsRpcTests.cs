using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Auth;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// The two audited per-tenant settings (IDA-05, IDA-06, D-02, D-05, D-15): allow-self-approval and
/// the guardrail policy mode. Only a Guardrail.Admin (or a personal account, or the single-user
/// host) reads or changes them, the tenant is always the caller's scope, and every change is one
/// audit event in the same commit as the setting row.
/// </summary>
public sealed class TenantSettingsRpcTests
{
    private static readonly string[] Admin = [AdsIdentity.GuardrailAdminRole];

    private readonly DesignDbContext _db = TestData.NewDb();
    private readonly Guid _user = Guid.NewGuid();

    private static DeployService ServiceOn(DesignDbContext db) => new(db, NullLogger<DeployService>.Instance,
        new FakeCryptoService(), new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl), new ScriptedHttp(_ => null));

    private DeployService Service => ServiceOn(_db);

    private ServerCallContext AdminOf(Guid tenant, Guid? user = null, string name = "Ada") =>
        TestServerCallContext.ForUser(user ?? _user, name, tenant, Admin);

    private Task<TenantSettingsResponse> Set(ServerCallContext caller, bool selfApproval, string mode) =>
        Service.SetTenantSettings(new TenantSettingsRequest { AllowSelfApproval = selfApproval, GuardrailPolicyMode = mode }, caller);

    private Task<TenantSettingsResponse> Get(ServerCallContext caller) => Service.GetTenantSettings(new Empty(), caller);

    private static void AssertRefused(TenantSettingsResponse response)
    {
        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("TENANT_SETTINGS_ADMIN_REQUIRED", response.ErrorMessage);
        Assert.Contains("Guardrail.Admin", response.ErrorMessage);
        Assert.False(response.AllowSelfApproval);
        Assert.Equal("", response.GuardrailPolicyMode);
    }

    // --- defaults and round trip ---

    [Fact]
    public async Task WithoutAStoredRowGetAnswersTheDefaults()
    {
        var response = await Get(AdminOf(TestTenants.Contoso));

        Assert.Equal(200, response.StatusCode);
        Assert.False(response.AllowSelfApproval);
        Assert.Equal(GuardrailPolicyModes.Replace, response.GuardrailPolicyMode);
        Assert.Equal("", response.UpdatedBy);
        Assert.Equal("", response.UpdatedAt);
        Assert.Empty(_db.AdsTenantSettings);
    }

    [Fact]
    public async Task SetStoresTheRowAnswersTheStoredValuesAndGetReturnsThem()
    {
        var set = await Set(AdminOf(TestTenants.Contoso), true, "merge");

        Assert.Equal(200, set.StatusCode);
        Assert.True(set.AllowSelfApproval);
        Assert.Equal(GuardrailPolicyModes.Merge, set.GuardrailPolicyMode);
        Assert.Equal($"Ada <{_user}>", set.UpdatedBy);
        Assert.True(DateTime.TryParse(set.UpdatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out _));

        var get = await Get(AdminOf(TestTenants.Contoso));
        Assert.Equal(200, get.StatusCode);
        Assert.True(get.AllowSelfApproval);
        Assert.Equal(GuardrailPolicyModes.Merge, get.GuardrailPolicyMode);
        Assert.Equal($"Ada <{_user}>", get.UpdatedBy);

        var row = await _db.AdsTenantSettings.SingleAsync();
        Assert.Equal(TestTenants.Contoso, row.TenantId);
        Assert.Equal(_user.ToString("D"), row.UpdatedByUserId);
    }

    [Theory]
    [InlineData("MERGE", "merge")]
    [InlineData("  Merge ", "merge")]
    [InlineData("Replace", "replace")]
    public async Task TheModeIsNormalizedBeforeItIsStored(string requested, string stored)
    {
        var set = await Set(AdminOf(TestTenants.Contoso), false, requested);

        Assert.Equal(200, set.StatusCode);
        Assert.Equal(stored, set.GuardrailPolicyMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("union")]
    [InlineData("merge,replace")]
    public async Task AnUnknownModeIs400AndNothingIsStored(string mode)
    {
        var set = await Set(AdminOf(TestTenants.Contoso), true, mode);

        Assert.Equal(400, set.StatusCode);
        Assert.StartsWith("TENANT_SETTINGS_INVALID", set.ErrorMessage);
        Assert.Empty(_db.AdsTenantSettings);
        Assert.Empty(_db.AdsSecurityEvents);
    }

    // --- who may call ---

    [Fact]
    public async Task AnOrganizationalCallerWithNoRoleIsRefusedOnBothRpcsAndNothingIsStored()
    {
        var caller = () => TestServerCallContext.ForUser(_user, "Ada");

        AssertRefused(await Get(caller()));
        AssertRefused(await Set(caller(), true, "merge"));
        Assert.Empty(_db.AdsTenantSettings);
        Assert.Empty(_db.AdsSecurityEvents);
    }

    [Theory]
    [InlineData("guardrail.admin")]
    [InlineData("Deploy.Approver")]
    public async Task AnotherCasingOrAnotherRoleIsRefused(string role)
    {
        var caller = () => TestServerCallContext.ForUser(_user, "Ada", roles: [role]);

        AssertRefused(await Get(caller()));
        AssertRefused(await Set(caller(), true, "merge"));
    }

    [Fact]
    public async Task APersonalAccountAndTheSingleUserHostNeedNoRole()
    {
        var personal = TestServerCallContext.ForPersonalAccount(_user, "Ada");
        var host = TestServerCallContext.ForSingleUserHost(Guid.NewGuid(), "Bob");

        Assert.Equal(200, (await Set(personal, true, "replace")).StatusCode);
        Assert.Equal(200, (await Get(TestServerCallContext.ForPersonalAccount(_user, "Ada"))).StatusCode);
        Assert.Equal(200, (await Set(host, false, "merge")).StatusCode);
        Assert.Equal(200, (await Get(TestServerCallContext.ForSingleUserHost(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task NoIdentityIs401OnBothRpcs()
    {
        var get = await Get(TestServerCallContext.WithoutIdentity());
        var set = await Set(TestServerCallContext.WithoutIdentity(), true, "merge");

        Assert.Equal(401, get.StatusCode);
        Assert.Equal(401, set.StatusCode);
        Assert.Empty(_db.AdsTenantSettings);
    }

    // --- audit ---

    [Fact]
    public async Task ChangingSelfApprovalWritesOneEventNamingThePrincipal()
    {
        await Set(AdminOf(TestTenants.Contoso), true, "replace");

        var events = await _db.AdsSecurityEvents.ToListAsync();
        var only = Assert.Single(events);
        Assert.Equal(SecurityEventTypes.SelfApprovalChanged, only.EventType);
        Assert.Equal("False -> True", only.Detail);
        Assert.Equal(TestTenants.Contoso, only.TenantId);
        Assert.Equal($"Ada <{_user}>", only.Principal);
        Assert.Equal(_user.ToString("D"), only.PrincipalUserId);
    }

    [Fact]
    public async Task ChangingTheModeWritesOnePolicyModeChangedEvent()
    {
        await Set(AdminOf(TestTenants.Contoso), false, "merge");

        var only = Assert.Single(await _db.AdsSecurityEvents.ToListAsync());
        Assert.Equal(SecurityEventTypes.PolicyModeChanged, only.EventType);
        Assert.Equal("replace -> merge", only.Detail);
        Assert.Equal(TestTenants.Contoso, only.TenantId);
    }

    [Fact]
    public async Task ChangingBothWritesTwoEventsAndChangingNothingWritesNone()
    {
        await Set(AdminOf(TestTenants.Contoso), true, "merge");
        Assert.Equal(2, await _db.AdsSecurityEvents.CountAsync());
        Assert.Contains(await _db.AdsSecurityEvents.ToListAsync(), e => e.EventType == SecurityEventTypes.SelfApprovalChanged);
        Assert.Contains(await _db.AdsSecurityEvents.ToListAsync(), e => e.EventType == SecurityEventTypes.PolicyModeChanged);

        var again = await Set(AdminOf(TestTenants.Contoso), true, "MERGE");

        Assert.Equal(200, again.StatusCode);
        Assert.Equal(2, await _db.AdsSecurityEvents.CountAsync());
    }

    [Fact]
    public async Task SettingTheDefaultsWhenNoRowExistsChangesNothingAndWritesNothing()
    {
        var set = await Set(AdminOf(TestTenants.Contoso), false, "replace");

        Assert.Equal(200, set.StatusCode);
        Assert.False(set.AllowSelfApproval);
        Assert.Equal(GuardrailPolicyModes.Replace, set.GuardrailPolicyMode);
        Assert.Empty(_db.AdsTenantSettings);
        Assert.Empty(_db.AdsSecurityEvents);
    }

    [Fact]
    public async Task ARefusedOrFailedCallWritesNoEventAndAGetWritesNone()
    {
        await Get(AdminOf(TestTenants.Contoso));
        await Set(TestServerCallContext.ForUser(_user), true, "merge");
        await Set(AdminOf(TestTenants.Contoso), true, "bogus");

        Assert.Empty(_db.AdsSecurityEvents);
    }

    [Fact]
    public async Task AnEventNeverHoldsAPolicyBody()
    {
        await Set(AdminOf(TestTenants.Contoso), true, "merge");

        foreach (var e in await _db.AdsSecurityEvents.ToListAsync())
        {
            Assert.DoesNotContain("{", e.Detail);
            Assert.True(e.Detail!.Length < 64);
        }
    }

    /// <summary>Fails the save that carries a security event, to prove the event and the row are one commit.</summary>
    private sealed class FailOnSecurityEvent : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<SecurityEventModel>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("audit insert failed");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task WhenTheAuditInsertFailsTheSettingRowIsNotStored()
    {
        var store = Guid.NewGuid().ToString();
        await using var failing = new DesignDbContext(new DbContextOptionsBuilder<DesignDbContext>()
            .UseInMemoryDatabase(store).AddInterceptors(new FailOnSecurityEvent()).Options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceOn(failing).SetTenantSettings(
            new TenantSettingsRequest { AllowSelfApproval = true, GuardrailPolicyMode = "merge" }, AdminOf(TestTenants.Contoso)));

        await using var verify = new DesignDbContext(new DbContextOptionsBuilder<DesignDbContext>()
            .UseInMemoryDatabase(store).Options);
        Assert.Empty(verify.AdsTenantSettings);
        Assert.Empty(verify.AdsSecurityEvents);
    }

    // --- isolation ---

    [Fact]
    public async Task TheSettingsOfOneScopeAreInvisibleToAnother()
    {
        await Set(AdminOf(TestTenants.Contoso), true, "merge");

        var other = await Get(AdminOf(TestTenants.Fabrikam, Guid.NewGuid(), "Grace"));

        Assert.Equal(200, other.StatusCode);
        Assert.False(other.AllowSelfApproval);
        Assert.Equal(GuardrailPolicyModes.Replace, other.GuardrailPolicyMode);
        Assert.Equal("", other.UpdatedBy);
    }

    [Fact]
    public async Task AWriteByOneScopeNeverAltersAnothersRowOrEvents()
    {
        await Set(AdminOf(TestTenants.Contoso), true, "merge");
        await Set(AdminOf(TestTenants.Fabrikam, Guid.NewGuid(), "Grace"), false, "merge");

        var contoso = await _db.AdsTenantSettings.SingleAsync(s => s.TenantId == TestTenants.Contoso);
        Assert.True(contoso.AllowSelfApproval);
        Assert.Equal($"Ada <{_user}>", contoso.UpdatedBy);
        Assert.Equal(2, await _db.AdsSecurityEvents.CountAsync(e => e.TenantId == TestTenants.Contoso));
        Assert.Equal(1, await _db.AdsSecurityEvents.CountAsync(e => e.TenantId == TestTenants.Fabrikam));
    }

    [Fact]
    public async Task ASecondChangeUpdatesTheSameRowAndRecordsTheOldValue()
    {
        await Set(AdminOf(TestTenants.Contoso), true, "replace");
        var bob = Guid.NewGuid();
        await Set(AdminOf(TestTenants.Contoso, bob, "Bob"), false, "replace");

        var row = await _db.AdsTenantSettings.SingleAsync();
        Assert.False(row.AllowSelfApproval);
        Assert.Equal($"Bob <{bob}>", row.UpdatedBy);
        var events = await _db.AdsSecurityEvents.Where(e => e.EventType == SecurityEventTypes.SelfApprovalChanged)
            .OrderBy(e => e.AtUtc).ToListAsync();
        Assert.Equal("False -> True; True -> False", string.Join("; ", events.Select(e => e.Detail)));
    }
}

/// <summary>The same RPCs over the real host: the role comes from the token and a refusal is a 403
/// in the response, never an RpcException.</summary>
public sealed class TenantSettingsHostTests(AdsServerFactory factory) : IClassFixture<AdsServerFactory>
{
    private static readonly TestClaims AdminRole = new(Roles: [AdsIdentity.GuardrailAdminRole]);

    [Fact]
    public async Task ATokenWithTheRoleGets200AndCanChangeASetting()
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid(), claims: AdminRole);
        var client = new Deploy.DeployClient(channel);

        Assert.Equal(200, (await client.GetTenantSettingsAsync(new Empty())).StatusCode);
        var set = await client.SetTenantSettingsAsync(new TenantSettingsRequest { AllowSelfApproval = true, GuardrailPolicyMode = "replace" });

        Assert.Equal(200, set.StatusCode);
        Assert.True(set.AllowSelfApproval);
    }

    [Fact]
    public async Task ARolelessTokenGets403InTheResponseAndNoRpcException()
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid());
        var client = new Deploy.DeployClient(channel);

        var get = await client.GetTenantSettingsAsync(new Empty());
        var set = await client.SetTenantSettingsAsync(new TenantSettingsRequest { AllowSelfApproval = true, GuardrailPolicyMode = "replace" });

        Assert.Equal(403, get.StatusCode);
        Assert.Equal(403, set.StatusCode);
        Assert.StartsWith("TENANT_SETTINGS_ADMIN_REQUIRED", set.ErrorMessage);
    }
}
