using AzureDesignStudio.Discovery.Unmanaged;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Auth;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.Server.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AzureDesignStudio.Discovery.Iac.WhatIf;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// Tenant isolation (IDA-05, success criterion 4), guardrail half: policies are stored and read per
/// scope (the tenant id of an organizational token, the user's object id for a personal account), a
/// scope with no row of its own is judged against the NULL-tenant default, and no RPC writes that
/// default row. Runs the service classes on the in-memory context; later plans extend this class with
/// the settings and the audit trail.
/// </summary>
public sealed class TenantIsolationTests
{
    private const string StoragePolicy = """{"scopes":[],"tags":[],"types":["Microsoft.Storage/storageAccounts"]}""";
    private const string KeyVaultPolicy = """{"scopes":[],"tags":[],"types":["Microsoft.KeyVault/vaults"]}""";
    private const string SqlPolicy = """{"scopes":[],"tags":[],"types":["Microsoft.Sql/servers"]}""";
    private const string DefaultPolicy = """{"scopes":[],"tags":[],"types":["Microsoft.Network/virtualNetworks"]}""";

    private static readonly string[] Admin = [AdsIdentity.GuardrailAdminRole];

    private readonly DesignDbContext _db = TestData.NewDb();

    private DeployService Service => new(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(),
        new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl), new ScriptedHttp(_ => null));

    private static ServerCallContext Member(Guid user, Guid tenant, string? name = null) =>
        TestServerCallContext.ForUser(user, name, tenant, Admin);

    private static ServerCallContext Personal(Guid user, string? name = null) =>
        TestServerCallContext.ForUser(user, name, TestTenants.Consumer, Admin);

    private Task<GuardrailPolicyResponse> Set(ServerCallContext caller, string json) =>
        Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = json }, caller);

    private Task<GuardrailPolicyResponse> Get(ServerCallContext caller) =>
        Service.GetGuardrailPolicy(new Empty(), caller);

    private async Task SeedDefault(string json = DefaultPolicy)
    {
        _db.AdsGuardrailPolicies.Add(new GuardrailPolicyModel
        {
            Id = 1, TenantId = null, Json = json, UpdatedBy = "operator",
            UpdatedByUserId = Guid.Empty.ToString("D"), UpdatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    // --- isolation ---

    [Fact]
    public async Task TwoOrganizationsKeepTheirOwnPolicies()
    {
        var contoso = Member(Guid.NewGuid(), TestTenants.Contoso);
        var fabrikam = Member(Guid.NewGuid(), TestTenants.Fabrikam);

        Assert.Equal(200, (await Set(contoso, StoragePolicy)).StatusCode);
        Assert.Equal(200, (await Set(fabrikam, KeyVaultPolicy)).StatusCode);

        Assert.Equal(StoragePolicy, (await Get(contoso)).Json);
        Assert.Equal(KeyVaultPolicy, (await Get(fabrikam)).Json);
        Assert.Equal(2, await _db.AdsGuardrailPolicies.CountAsync());
        Assert.Equal(StoragePolicy, (await _db.AdsGuardrailPolicies.SingleAsync(p => p.TenantId == TestTenants.Contoso)).Json);
        Assert.Equal(KeyVaultPolicy, (await _db.AdsGuardrailPolicies.SingleAsync(p => p.TenantId == TestTenants.Fabrikam)).Json);
    }

    [Fact]
    public async Task AnotherTenantsWriteNeverAltersARow()
    {
        var contoso = Member(Guid.NewGuid(), TestTenants.Contoso);
        var fabrikam = Member(Guid.NewGuid(), TestTenants.Fabrikam);
        await Set(contoso, StoragePolicy);
        var before = await _db.AdsGuardrailPolicies.AsNoTracking().SingleAsync(p => p.TenantId == TestTenants.Contoso);

        await Set(fabrikam, KeyVaultPolicy);
        await Set(fabrikam, SqlPolicy);

        var after = await _db.AdsGuardrailPolicies.AsNoTracking().SingleAsync(p => p.TenantId == TestTenants.Contoso);
        Assert.Equal(before, after);
        Assert.Equal(StoragePolicy, (await Get(contoso)).Json);
    }

    [Fact]
    public async Task TwoPersonalAccountsAreTwoScopesEvenThoughTheyShareATenant()
    {
        var ada = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await Set(Personal(ada), StoragePolicy);
        await Set(Personal(bob), KeyVaultPolicy);

        Assert.Equal(StoragePolicy, (await Get(Personal(ada))).Json);
        Assert.Equal(KeyVaultPolicy, (await Get(Personal(bob))).Json);
        Assert.Equal(StoragePolicy, (await _db.AdsGuardrailPolicies.SingleAsync(p => p.TenantId == ada)).Json);
        Assert.Equal(KeyVaultPolicy, (await _db.AdsGuardrailPolicies.SingleAsync(p => p.TenantId == bob)).Json);
        Assert.DoesNotContain(_db.AdsGuardrailPolicies, p => p.TenantId == TestTenants.Consumer);
    }

    [Fact]
    public async Task AnOrganizationAndAPersonalAccountAreTwoScopes()
    {
        var user = Guid.NewGuid();
        var organization = Member(user, TestTenants.Contoso);
        var personal = Personal(user);

        await Set(organization, StoragePolicy);

        Assert.Equal("", (await Get(personal)).Json);
        await Set(personal, KeyVaultPolicy);
        Assert.Equal(StoragePolicy, (await Get(organization)).Json);
        Assert.Equal(KeyVaultPolicy, (await Get(personal)).Json);
    }

    [Fact]
    public async Task TwoMembersOfOneOrganizationShareOnePolicy()
    {
        var ada = Member(Guid.NewGuid(), TestTenants.Contoso, "Ada");
        var bob = Member(Guid.NewGuid(), TestTenants.Contoso, "Bob");

        await Set(ada, StoragePolicy);
        var read = await Get(bob);
        await Set(bob, KeyVaultPolicy);

        Assert.Equal(StoragePolicy, read.Json);
        Assert.Equal(KeyVaultPolicy, (await Get(ada)).Json);
        Assert.Single(_db.AdsGuardrailPolicies);
    }

    [Fact]
    public async Task ASecondWriteByTheSameScopeUpdatesItsOneRow()
    {
        var contoso = Member(Guid.NewGuid(), TestTenants.Contoso, "Ada");

        await Set(contoso, StoragePolicy);
        var first = await _db.AdsGuardrailPolicies.AsNoTracking().SingleAsync();
        var response = await Set(contoso, KeyVaultPolicy);

        Assert.Equal(200, response.StatusCode);
        var row = await _db.AdsGuardrailPolicies.AsNoTracking().SingleAsync();
        Assert.Equal(first.Id, row.Id);
        Assert.Equal(TestTenants.Contoso, row.TenantId);
        Assert.Equal(KeyVaultPolicy, row.Json);
    }

    [Fact]
    public async Task TheRowRecordsTheCallerNotAnythingTheRequestSaid()
    {
        var user = Guid.NewGuid();

        await Set(Member(user, TestTenants.Contoso, "Ada"), StoragePolicy);

        var row = await _db.AdsGuardrailPolicies.SingleAsync();
        Assert.Equal($"Ada <{user}>", row.UpdatedBy);
        Assert.Equal(user.ToString("D"), row.UpdatedByUserId);
        Assert.Equal(TestTenants.Contoso, row.TenantId);
    }

    // --- the inherited default ---

    [Fact]
    public async Task AScopeWithNoRowOfItsOwnReadsTheDefaultAndItsOwnWriteReplacesIt()
    {
        await SeedDefault();
        var contoso = Member(Guid.NewGuid(), TestTenants.Contoso);

        var inherited = await Get(contoso);
        await Set(contoso, StoragePolicy);
        var own = await Get(contoso);

        Assert.Equal(DefaultPolicy, inherited.Json);
        Assert.Equal("operator", inherited.UpdatedBy);
        Assert.Equal(StoragePolicy, own.Json);
        Assert.Equal(2, await _db.AdsGuardrailPolicies.CountAsync());
    }

    [Fact]
    public async Task AnotherScopeKeepsInheritingTheDefaultWhileOneScopeHasItsOwn()
    {
        await SeedDefault();
        await Set(Member(Guid.NewGuid(), TestTenants.Contoso), StoragePolicy);

        Assert.Equal(DefaultPolicy, (await Get(Member(Guid.NewGuid(), TestTenants.Fabrikam))).Json);
        Assert.Equal(DefaultPolicy, (await Get(Personal(Guid.NewGuid()))).Json);
    }

    [Fact]
    public async Task SetNeverWritesTheNullTenantDefaultRow()
    {
        await SeedDefault();
        var before = await _db.AdsGuardrailPolicies.AsNoTracking().SingleAsync(p => p.TenantId == null);

        await Set(Member(Guid.NewGuid(), TestTenants.Contoso), StoragePolicy);
        await Set(Member(Guid.NewGuid(), TestTenants.Fabrikam), KeyVaultPolicy);
        await Set(Personal(Guid.NewGuid()), SqlPolicy);
        await Set(Member(Guid.NewGuid(), TestTenants.Contoso), "{}");

        var after = await _db.AdsGuardrailPolicies.AsNoTracking().SingleAsync(p => p.TenantId == null);
        Assert.Equal(before, after);
        Assert.Equal(1, after.Id);
        Assert.Equal(3, await _db.AdsGuardrailPolicies.CountAsync(p => p.TenantId != null));
        Assert.Equal(4, await _db.AdsGuardrailPolicies.CountAsync());
    }

    [Fact]
    public async Task TheFirstScopedWriteWithoutADefaultDoesNotTakeTheDefaultRowId()
    {
        await Set(Member(Guid.NewGuid(), TestTenants.Contoso), StoragePolicy);

        var row = await _db.AdsGuardrailPolicies.SingleAsync();
        Assert.NotEqual(1, row.Id);
        Assert.Equal(TestTenants.Contoso, row.TenantId);
    }

    [Fact]
    public async Task AMalformedWriteLeavesEveryRowAsItWas()
    {
        await SeedDefault();
        var contoso = Member(Guid.NewGuid(), TestTenants.Contoso);
        await Set(contoso, StoragePolicy);

        var response = await Set(contoso, "{ not json");

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(StoragePolicy, (await Get(contoso)).Json);
        Assert.Equal(DefaultPolicy, (await _db.AdsGuardrailPolicies.SingleAsync(p => p.TenantId == null)).Json);
    }

    // --- what enforcement loads ---

    [Fact]
    public async Task LoadGuardrailPolicyResolvesOwnRowThenDefaultThenEmpty()
    {
        var service = Service;
        var (none, noneError) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Null(noneError);
        Assert.Empty(none!.ProtectedTypes);

        await SeedDefault();
        var (inherited, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Equal(["Microsoft.Network/virtualNetworks"], inherited!.ProtectedTypes);

        await Set(Member(Guid.NewGuid(), TestTenants.Contoso), StoragePolicy);
        var (own, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        var (other, _) = await service.LoadGuardrailPolicy(TestTenants.Fabrikam, CancellationToken.None);
        Assert.Equal(["Microsoft.Storage/storageAccounts"], own!.ProtectedTypes);
        Assert.Equal(["Microsoft.Network/virtualNetworks"], other!.ProtectedTypes);
    }

    [Fact]
    public async Task AnEmptyStoredBodyIsTheEmptyPolicyNotTheDefault()
    {
        await SeedDefault();
        _db.AdsGuardrailPolicies.Add(new GuardrailPolicyModel
        {
            Id = 2, TenantId = TestTenants.Contoso, Json = " ", UpdatedBy = "x", UpdatedByUserId = "x", UpdatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var (policy, error) = await Service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(GuardrailPolicy.Empty, policy);
    }

    [Fact]
    public async Task AnUnreadableBodyFailsClosedForTheAffectedScopeOnly()
    {
        _db.AdsGuardrailPolicies.Add(new GuardrailPolicyModel
        {
            Id = 2, TenantId = TestTenants.Contoso, Json = "{ not json", UpdatedBy = "x", UpdatedByUserId = "x",
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        await Set(Member(Guid.NewGuid(), TestTenants.Fabrikam), KeyVaultPolicy);
        var service = Service;

        var (broken, error) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        var (healthy, healthyError) = await service.LoadGuardrailPolicy(TestTenants.Fabrikam, CancellationToken.None);

        Assert.Null(broken);
        Assert.StartsWith("GUARDRAIL_POLICY_UNREADABLE", error);
        Assert.Null(healthyError);
        Assert.Equal(["Microsoft.KeyVault/vaults"], healthy!.ProtectedTypes);
    }

    [Fact]
    public async Task AnUnreadableDefaultFailsClosedForAScopeThatInheritsItOnly()
    {
        await SeedDefault("{ not json");
        await Set(Member(Guid.NewGuid(), TestTenants.Contoso), StoragePolicy);
        var service = Service;

        var (_, inheritingError) = await service.LoadGuardrailPolicy(TestTenants.Fabrikam, CancellationToken.None);
        var (own, ownError) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);

        Assert.StartsWith("GUARDRAIL_POLICY_UNREADABLE", inheritingError);
        Assert.Null(ownError);
        Assert.NotNull(own);
    }

    // --- enforcement uses the caller's scope ---

    private const string ResourceGroup = "rg";
    private const string StackTemplate =
        """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[{"type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","name":"sa1","location":"westeurope","sku":{"name":"Standard_LRS"},"kind":"StorageV2"}]}""";

    [Fact]
    public async Task AStackWhatIfIsBlockedByTheCallersPolicyAndNotByAnotherScopes()
    {
        var subscription = Guid.NewGuid();
        var arm = new ScriptedArm(subscription.ToString(), ResourceGroup)
        {
            WhatIfResult = """{"properties":{"provisioningState":"succeeded","changes":{"resourceChanges":[{"id":"/subscriptions/"""
                + subscription + """/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1","type":"Microsoft.Storage/storageAccounts","changeType":"create","changeCertainty":"definite"}]}}}""",
        };
        var service = new DeployService(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(),
            new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl), arm, new AmbientCredential());
        var contoso = Member(Guid.NewGuid(), TestTenants.Contoso);
        var fabrikam = Member(Guid.NewGuid(), TestTenants.Fabrikam);
        await service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = StoragePolicy }, contoso);
        var request = new StackWhatIfRequest
        {
            SubscriptionId = subscription.ToString(), ResourceGroupName = ResourceGroup, StackName = "ads-stack",
            ArmTemplate = StackTemplate, Parameters = "{}", ResourcesAction = "detach",
        };

        var blocked = await service.StackWhatIf(request, contoso);
        var free = await service.StackWhatIf(request, fabrikam);

        Assert.Equal(200, blocked.StatusCode);
        Assert.True(blocked.GuardrailBlocked);
        Assert.Equal("", blocked.WhatIfToken);
        Assert.Equal(200, free.StatusCode);
        Assert.False(free.GuardrailBlocked);
        Assert.NotEqual("", free.WhatIfToken);
    }
}
