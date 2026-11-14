using AzureDesignStudio.Discovery.Unmanaged;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Auth;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.Server.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    // --- guardrail policy mode (D-05): replace by default, merge selectable per tenant ---

    private async Task SeedOwn(Guid scope, string json, int id = 2)
    {
        _db.AdsGuardrailPolicies.Add(new GuardrailPolicyModel
        {
            Id = id, TenantId = scope, Json = json, UpdatedBy = "x", UpdatedByUserId = "x", UpdatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private async Task SeedMode(Guid scope, string mode)
    {
        _db.AdsTenantSettings.Add(new TenantSettingsModel
        {
            TenantId = scope, GuardrailPolicyMode = mode, UpdatedBy = "x", UpdatedByUserId = "x", UpdatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private static string[] Types(GuardrailPolicy? policy) => [.. policy!.ProtectedTypes];

    [Fact]
    public async Task WithNoSettingsRowTheScopeIsInReplaceMode()
    {
        await SeedDefault();
        await SeedOwn(TestTenants.Contoso, StoragePolicy);

        var (policy, error) = await Service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(["Microsoft.Storage/storageAccounts"], Types(policy));
    }

    [Fact]
    public async Task ReplaceModeUsesTheOwnPolicyElseTheDefaultElseEmpty()
    {
        await SeedMode(TestTenants.Contoso, GuardrailPolicyModes.Replace);
        var service = Service;
        var (empty, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Empty(Types(empty));

        await SeedDefault();
        var (inherited, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Equal(["Microsoft.Network/virtualNetworks"], Types(inherited));

        await SeedOwn(TestTenants.Contoso, StoragePolicy);
        var (own, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Equal(["Microsoft.Storage/storageAccounts"], Types(own));
    }

    [Fact]
    public async Task MergeModeJudgesTheUnionOfTheDefaultAndTheOwnPolicy()
    {
        await SeedDefault();
        await SeedOwn(TestTenants.Contoso, StoragePolicy);
        await SeedMode(TestTenants.Contoso, GuardrailPolicyModes.Merge);

        var (policy, error) = await Service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(["Microsoft.Network/virtualNetworks", "Microsoft.Storage/storageAccounts"], Types(policy));
    }

    [Fact]
    public async Task MergeModeWithNoOwnRowIsTheDefaultAndWithNoDefaultRowIsTheOwnRow()
    {
        await SeedMode(TestTenants.Contoso, GuardrailPolicyModes.Merge);
        await SeedMode(TestTenants.Fabrikam, GuardrailPolicyModes.Merge);
        await SeedDefault();
        await SeedOwn(TestTenants.Fabrikam, KeyVaultPolicy);
        var service = Service;

        var (noOwn, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Equal(["Microsoft.Network/virtualNetworks"], Types(noOwn));

        _db.AdsGuardrailPolicies.Remove(await _db.AdsGuardrailPolicies.SingleAsync(p => p.TenantId == null));
        await _db.SaveChangesAsync();
        var (noDefault, _) = await service.LoadGuardrailPolicy(TestTenants.Fabrikam, CancellationToken.None);
        Assert.Equal(["Microsoft.KeyVault/vaults"], Types(noDefault));
        var (neither, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Empty(Types(neither));
    }

    [Fact]
    public async Task AnUnrecognisedStoredModeIsJudgedAsMerge()
    {
        await SeedDefault();
        await SeedOwn(TestTenants.Contoso, StoragePolicy);
        await SeedMode(TestTenants.Contoso, "union");

        var (policy, error) = await Service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(["Microsoft.Network/virtualNetworks", "Microsoft.Storage/storageAccounts"], Types(policy));
    }

    [Fact]
    public async Task MergeModeFailsClosedWhenEitherBodyIsUnreadable()
    {
        await SeedMode(TestTenants.Contoso, GuardrailPolicyModes.Merge);
        await SeedMode(TestTenants.Fabrikam, GuardrailPolicyModes.Merge);
        await SeedDefault();
        await SeedOwn(TestTenants.Contoso, "{ not json");
        var service = Service;

        var (brokenOwn, ownError) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Null(brokenOwn);
        Assert.StartsWith("GUARDRAIL_POLICY_UNREADABLE", ownError);

        _db.AdsGuardrailPolicies.Remove(await _db.AdsGuardrailPolicies.SingleAsync(p => p.TenantId == null));
        await SeedDefault("{ not json");
        await SeedOwn(TestTenants.Fabrikam, KeyVaultPolicy, id: 3);
        var (brokenDefault, defaultError) = await service.LoadGuardrailPolicy(TestTenants.Fabrikam, CancellationToken.None);
        Assert.Null(brokenDefault);
        Assert.StartsWith("GUARDRAIL_POLICY_UNREADABLE", defaultError);
    }

    [Fact]
    public async Task AnotherScopesModeDoesNotChangeThisScope()
    {
        await SeedDefault();
        await SeedOwn(TestTenants.Contoso, StoragePolicy);
        await SeedOwn(TestTenants.Fabrikam, KeyVaultPolicy, id: 3);
        await SeedMode(TestTenants.Fabrikam, GuardrailPolicyModes.Merge);
        var service = Service;

        var (contoso, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        var (fabrikam, _) = await service.LoadGuardrailPolicy(TestTenants.Fabrikam, CancellationToken.None);

        Assert.Equal(["Microsoft.Storage/storageAccounts"], Types(contoso));
        Assert.Equal(["Microsoft.Network/virtualNetworks", "Microsoft.KeyVault/vaults"], Types(fabrikam));
    }

    [Fact]
    public async Task SettingTheModeThroughTheRpcThenGovernsTheNextLoad()
    {
        await SeedDefault();
        var admin = Member(Guid.NewGuid(), TestTenants.Contoso);
        await Set(admin, StoragePolicy);
        var service = Service;
        var (before, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Equal(["Microsoft.Storage/storageAccounts"], Types(before));

        var set = await service.SetTenantSettings(
            new TenantSettingsRequest { AllowSelfApproval = false, GuardrailPolicyMode = "Merge" }, Member(Guid.NewGuid(), TestTenants.Contoso));
        var (merged, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Equal(200, set.StatusCode);
        Assert.Equal(["Microsoft.Network/virtualNetworks", "Microsoft.Storage/storageAccounts"], Types(merged));

        await service.SetTenantSettings(
            new TenantSettingsRequest { AllowSelfApproval = false, GuardrailPolicyMode = "replace" }, Member(Guid.NewGuid(), TestTenants.Contoso));
        var (replaced, _) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
        Assert.Equal(["Microsoft.Storage/storageAccounts"], Types(replaced));
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

    // --- the audit trail of a policy change (D-15) ---

    [Fact]
    public async Task EachSetAppendsOneEventForTheWritersScopeWithThePrincipal()
    {
        var contosoUser = Guid.NewGuid();
        var fabrikamUser = Guid.NewGuid();
        var contoso = Member(contosoUser, TestTenants.Contoso, "Ada");
        var fabrikam = Member(fabrikamUser, TestTenants.Fabrikam, "Bob");

        await Set(contoso, StoragePolicy);
        await Set(fabrikam, KeyVaultPolicy);
        await Set(contoso, SqlPolicy);

        var events = await _db.AdsSecurityEvents.OrderBy(e => e.AtUtc).ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(SecurityEventTypes.GuardrailPolicyChanged, e.EventType));
        Assert.Equal(2, events.Count(e => e.TenantId == TestTenants.Contoso));
        var theirs = Assert.Single(events, e => e.TenantId == TestTenants.Fabrikam);
        Assert.Equal($"Bob <{fabrikamUser}>", theirs.Principal);
        Assert.Equal(fabrikamUser.ToString("D"), theirs.PrincipalUserId);
        Assert.All(events.Where(e => e.TenantId == TestTenants.Contoso), e =>
        {
            Assert.Equal($"Ada <{contosoUser}>", e.Principal);
            Assert.Equal(contosoUser.ToString("D"), e.PrincipalUserId);
        });
    }

    [Fact]
    public async Task ThePersonalAccountScopeIsTheEventsTenant()
    {
        var user = Guid.NewGuid();

        await Set(Personal(user), StoragePolicy);

        Assert.Equal(user, (await _db.AdsSecurityEvents.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task AGetAppendsNoEvent()
    {
        var contoso = Member(Guid.NewGuid(), TestTenants.Contoso);
        await Set(contoso, StoragePolicy);

        await Get(contoso);
        await Get(contoso);

        Assert.Single(_db.AdsSecurityEvents);
    }

    [Fact]
    public async Task ARefusedOrMalformedSetAppendsNoEvent()
    {
        var roleless = TestServerCallContext.ForUser(Guid.NewGuid(), tenantId: TestTenants.Contoso);
        var admin = Member(Guid.NewGuid(), TestTenants.Contoso);

        Assert.Equal(403, (await Set(roleless, StoragePolicy)).StatusCode);
        Assert.Equal(400, (await Set(admin, "{ not json")).StatusCode);
        Assert.Equal(400, (await Set(admin, "{\"types\":[\"" + new string('x', 70000) + "\"]}")).StatusCode);

        Assert.Empty(_db.AdsSecurityEvents);
        Assert.Empty(_db.AdsGuardrailPolicies);
    }

    [Fact]
    public async Task TheEventDetailHoldsALengthAndAHashPrefixNeverThePolicy()
    {
        await Set(Member(Guid.NewGuid(), TestTenants.Contoso), StoragePolicy);

        var detail = (await _db.AdsSecurityEvents.SingleAsync()).Detail!;

        var bytes = System.Text.Encoding.UTF8.GetBytes(StoragePolicy);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16].ToLowerInvariant();
        Assert.Equal($"bytes={bytes.Length}; sha256={hash}", detail);
        Assert.DoesNotContain("Microsoft.Storage", detail);
        Assert.DoesNotContain("types", detail);
        Assert.DoesNotContain("{", detail);
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
    public async Task WhenTheAuditInsertFailsThePolicyRowIsNotStored()
    {
        var store = Guid.NewGuid().ToString();
        await using var failing = new DesignDbContext(new DbContextOptionsBuilder<DesignDbContext>()
            .UseInMemoryDatabase(store).AddInterceptors(new FailOnSecurityEvent()).Options);
        var service = new DeployService(failing, NullLogger<DeployService>.Instance, new FakeCryptoService(),
            new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl), new ScriptedHttp(_ => null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetGuardrailPolicy(
            new GuardrailPolicyRequest { Json = StoragePolicy }, Member(Guid.NewGuid(), TestTenants.Contoso)));

        await using var verify = new DesignDbContext(new DbContextOptionsBuilder<DesignDbContext>()
            .UseInMemoryDatabase(store).Options);
        Assert.Empty(verify.AdsGuardrailPolicies);
        Assert.Empty(verify.AdsSecurityEvents);
    }

    // --- previews belong to the scope that created them (D-07) ---

    private static readonly string PlanHash = "sha256:" + new string('a', 64);

    private static WhatIfSummary OneCreate() => new()
    {
        Status = "Succeeded",
        Changes = [new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 0)],
    };

    private DeployService ServiceOn(WhatIfTokenGate gate) => new(_db, NullLogger<DeployService>.Instance,
        new FakeCryptoService(), gate, new ScriptedHttp(_ => null));

    private Task<PlanApprovalResponse> Approve(DeployService service, ServerCallContext caller) =>
        service.ApprovePlan(new PlanApprovalRequest { PlanHash = PlanHash }, caller);

    [Fact]
    public async Task AnotherScopesApprovalOfAPreviewIsStaleAndTheOwnersSucceeds()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var service = ServiceOn(gate);
        var contosoUser = Guid.NewGuid();
        gate.Issue("sub", "rg", "tpl", "prm", OneCreate(), "{\"plan\":1}", PlanHash,
            requestedByUserId: contosoUser.ToString("D"), scopeId: TestTenants.Contoso);

        var foreign = await Approve(service, Member(Guid.NewGuid(), TestTenants.Fabrikam));

        Assert.Equal(400, foreign.StatusCode);
        Assert.StartsWith("WHAT_IF_STALE", foreign.ErrorMessage);
        Assert.Empty(_db.AdsPlanApprovals);

        var own = await Approve(service, Member(contosoUser, TestTenants.Contoso));

        Assert.Equal(200, own.StatusCode);
        Assert.Single(_db.AdsPlanApprovals);
    }

    [Fact]
    public async Task APreviewIssuedWithoutAScopeCannotBeApprovedByAnyone()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        gate.Issue("sub", "rg", "tpl", "prm", OneCreate(), "{}", PlanHash);

        var response = await Approve(ServiceOn(gate), Member(Guid.NewGuid(), TestTenants.Contoso));

        Assert.StartsWith("WHAT_IF_STALE", response.ErrorMessage);
    }

    [Fact]
    public async Task TwoPersonalAccountsDoNotShareAPreview()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var ada = Guid.NewGuid();
        gate.Issue("sub", "rg", "tpl", "prm", OneCreate(), "{}", PlanHash, scopeId: ada);

        var other = await Approve(ServiceOn(gate), Personal(Guid.NewGuid()));
        var own = await Approve(ServiceOn(gate), Personal(ada));

        Assert.StartsWith("WHAT_IF_STALE", other.ErrorMessage);
        Assert.Equal(200, own.StatusCode);
    }
}
