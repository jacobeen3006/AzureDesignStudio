using System.Security.Claims;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Services.Auth;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// Who may read or change a tenant's guardrail policy (IDA-04, D-01, D-04): an organizational
/// caller needs the Guardrail.Admin app role, a personal Microsoft account administers its own
/// single-user scope, and the single-user Desktop host always may. A refusal is a 403 in the
/// response - never an RpcException - that names the role and the re-sign-in hint.
/// </summary>
public sealed class GuardrailAuthorizationTests
{
    private const string Policy = """{"scopes":[],"tags":[],"types":["Microsoft.KeyVault/vaults"]}""";
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantIdClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    private readonly DesignDbContext _db = TestData.NewDb();
    private readonly Guid _user = Guid.NewGuid();

    private DeployService Service => new(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(),
        new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl), new ScriptedHttp(_ => null));

    private async Task<(GuardrailPolicyResponse Get, GuardrailPolicyResponse Set)> Call(Func<ServerCallContext> caller) =>
        (await Service.GetGuardrailPolicy(new Empty(), caller()),
         await Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = Policy }, caller()));

    private static void AssertRefused(GuardrailPolicyResponse response)
    {
        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("GUARDRAIL_ADMIN_REQUIRED", response.ErrorMessage);
        Assert.Contains("Guardrail.Admin", response.ErrorMessage);
        Assert.Contains("sign out", response.ErrorMessage);
        Assert.Equal("", response.Json);
    }

    private static void AssertAllowed(GuardrailPolicyResponse response)
    {
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("", response.ErrorMessage);
    }

    // --- service level ---

    [Fact]
    public async Task AnOrganizationalCallerWithNoRoleIsRefusedOnBothRpcs()
    {
        var (get, set) = await Call(() => TestServerCallContext.ForUser(_user, "Ada"));

        AssertRefused(get);
        AssertRefused(set);
    }

    [Fact]
    public async Task ARefusedSetStoresNothing()
    {
        await Call(() => TestServerCallContext.ForUser(_user, "Ada"));

        Assert.Empty(_db.AdsGuardrailPolicies);
    }

    [Fact]
    public async Task TheMappedRoleClaimAdministers()
    {
        var (get, set) = await Call(() => TestServerCallContext.ForUser(_user, roles: [AdsIdentity.GuardrailAdminRole]));

        AssertAllowed(get);
        AssertAllowed(set);
    }

    [Fact]
    public async Task TheShortRolesClaimAdministers()
    {
        var (get, set) = await Call(() => TestServerCallContext.WithClaims(
            new Claim(ObjectIdClaim, _user.ToString()),
            new Claim(TenantIdClaim, TestTenants.Contoso.ToString()),
            new Claim(AdsIdentity.RolesShortClaim, AdsIdentity.GuardrailAdminRole)));

        AssertAllowed(get);
        AssertAllowed(set);
    }

    [Theory]
    [InlineData("guardrail.admin")]
    [InlineData("GUARDRAIL.ADMIN")]
    [InlineData("Deploy.Approver")]
    public async Task AnotherCasingOrAnotherRoleIsRefused(string role)
    {
        var (get, set) = await Call(() => TestServerCallContext.ForUser(_user, roles: [role]));

        AssertRefused(get);
        AssertRefused(set);
    }

    [Fact]
    public async Task APersonalAccountAdministersItsOwnScopeWithoutARole()
    {
        var (get, set) = await Call(() => TestServerCallContext.ForPersonalAccount(_user, "Ada"));

        AssertAllowed(get);
        AssertAllowed(set);
    }

    [Fact]
    public async Task TheSingleUserHostAlwaysAdministers()
    {
        var (get, set) = await Call(() => TestServerCallContext.ForSingleUserHost(_user, "Ada"));

        AssertAllowed(get);
        AssertAllowed(set);
    }

    [Fact]
    public async Task NoIdentityIsStill401()
    {
        var (get, set) = await Call(TestServerCallContext.WithoutIdentity);

        Assert.Equal(401, get.StatusCode);
        Assert.Equal(401, set.StatusCode);
        Assert.Empty(_db.AdsGuardrailPolicies);
    }

    [Fact]
    public async Task AMalformedSetFromARolelessCallerIsRefusedBeforeItIsRead()
    {
        var response = await Service.SetGuardrailPolicy(
            new GuardrailPolicyRequest { Json = "{ not json" }, TestServerCallContext.ForUser(_user));

        AssertRefused(response);
    }
}

/// <summary>The same matrix over the real host: the role comes from the token, and a refusal is a
/// 403 in the response rather than an RpcException (the client reads Unauthenticated as offline).</summary>
public sealed class GuardrailAuthorizationHostTests(AdsServerFactory factory) : IClassFixture<AdsServerFactory>
{
    private static readonly TestClaims AdminRole = new(Roles: [AdsIdentity.GuardrailAdminRole]);

    [Fact]
    public async Task ATokenWithTheRoleHeaderGets200OnBothRpcs()
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid(), claims: AdminRole);
        var client = new Deploy.DeployClient(channel);

        Assert.Equal(200, (await client.GetGuardrailPolicyAsync(new Empty())).StatusCode);
        Assert.Equal(200, (await client.SetGuardrailPolicyAsync(new GuardrailPolicyRequest { Json = "{}" })).StatusCode);
    }

    [Fact]
    public async Task ARolelessTokenGets403InTheResponseAndNoRpcException()
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid());
        var client = new Deploy.DeployClient(channel);

        var get = await client.GetGuardrailPolicyAsync(new Empty());
        var set = await client.SetGuardrailPolicyAsync(new GuardrailPolicyRequest { Json = "{}" });

        Assert.Equal(403, get.StatusCode);
        Assert.Equal(403, set.StatusCode);
        Assert.StartsWith("GUARDRAIL_ADMIN_REQUIRED", set.ErrorMessage);
    }

    [Fact]
    public async Task APersonalAccountTokenAdministersWithoutTheRole()
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid(), claims: new TestClaims(Tenant: TestTenants.Consumer));
        var client = new Deploy.DeployClient(channel);

        Assert.Equal(200, (await client.GetGuardrailPolicyAsync(new Empty())).StatusCode);
        Assert.Equal(200, (await client.SetGuardrailPolicyAsync(new GuardrailPolicyRequest { Json = "{}" })).StatusCode);
    }
}
