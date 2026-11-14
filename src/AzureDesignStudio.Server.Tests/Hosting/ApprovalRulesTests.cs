using System.Security.Claims;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Auth;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// Who may approve a plan (IDA-06, D-02, D-04): an organizational caller needs the Deploy.Approver
/// app role, a personal Microsoft account implies it and the single-user Desktop host implies it. The
/// role is checked before any hash, preview or setting is looked at. Every approval here uses a
/// requester other than the approver, so the facts stay true next to the self-approval rule.
/// </summary>
public sealed class ApprovalRulesTests
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantIdClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    private readonly DesignDbContext _db = TestData.NewDb();
    private readonly WhatIfTokenGate _gate = new(WhatIfTokenGate.DefaultTtl);
    private readonly Guid _requester = Guid.NewGuid();

    private DeployService Service => new(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(),
        _gate, new ScriptedHttp(_ => null));

    internal static string NewHash() => "sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    internal static WhatIfSummary OneCreate() => new()
    {
        Status = "Succeeded",
        Changes = [new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 0)],
    };

    /// <summary>A live preview in <paramref name="scope"/>, requested by <paramref name="requester"/>.</summary>
    internal static string Preview(WhatIfTokenGate gate, Guid scope, Guid requester, string? hash = null)
    {
        hash ??= NewHash();
        gate.Issue("sub", "rg", "tpl", "prm", OneCreate(), "{\"plan\":1}", hash,
            requestedByUserId: requester.ToString("D"), scopeId: scope);
        return hash;
    }

    private Task<PlanApprovalResponse> Approve(string hash, ServerCallContext caller) =>
        Service.ApprovePlan(new PlanApprovalRequest { PlanHash = hash }, caller);

    private static ServerCallContext Organizational(Guid user, params string[] roles) =>
        TestServerCallContext.ForUser(user, "Ada", TestTenants.Contoso, roles);

    // --- the role (D-02) ---

    [Fact]
    public async Task AnOrganizationalCallerWithoutTheRoleIsRefusedEvenWhenAPreviewIsLive()
    {
        var hash = Preview(_gate, TestTenants.Contoso, _requester);

        var response = await Approve(hash, Organizational(Guid.NewGuid()));

        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("PLAN_APPROVER_ROLE_REQUIRED", response.ErrorMessage);
        Assert.Contains("Deploy.Approver", response.ErrorMessage);
        Assert.Contains("sign out", response.ErrorMessage);
        Assert.Empty(_db.AdsPlanApprovals);
    }

    [Fact]
    public async Task TheRefusalIsTheSameWhetherOrNotAPreviewExistsOrTheHashIsWellFormed()
    {
        var live = Preview(_gate, TestTenants.Contoso, _requester);
        var caller = Organizational(Guid.NewGuid());

        var withPreview = await Approve(live, caller);
        var withoutPreview = await Approve(NewHash(), caller);
        var malformed = await Approve("not-a-hash", caller);

        foreach (var response in new[] { withPreview, withoutPreview, malformed })
        {
            Assert.Equal(403, response.StatusCode);
            Assert.StartsWith("PLAN_APPROVER_ROLE_REQUIRED", response.ErrorMessage);
        }

        Assert.Equal(withPreview.ErrorMessage, withoutPreview.ErrorMessage);
        Assert.Equal(withPreview.ErrorMessage, malformed.ErrorMessage);
    }

    [Fact]
    public async Task TheGuardrailAdminRoleDoesNotApprove()
    {
        var hash = Preview(_gate, TestTenants.Contoso, _requester);

        var response = await Approve(hash, Organizational(Guid.NewGuid(), AdsIdentity.GuardrailAdminRole));

        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("PLAN_APPROVER_ROLE_REQUIRED", response.ErrorMessage);
    }

    [Fact]
    public async Task TheMappedRoleClaimApproves()
    {
        var hash = Preview(_gate, TestTenants.Contoso, _requester);

        var response = await Approve(hash, Organizational(Guid.NewGuid(), AdsIdentity.DeployApproverRole));

        Assert.Equal(200, response.StatusCode);
        Assert.Single(_db.AdsPlanApprovals);
    }

    [Fact]
    public async Task TheShortRolesClaimApproves()
    {
        var hash = Preview(_gate, TestTenants.Contoso, _requester);
        var caller = TestServerCallContext.WithClaims(
            new Claim(ObjectIdClaim, Guid.NewGuid().ToString()),
            new Claim(TenantIdClaim, TestTenants.Contoso.ToString()),
            new Claim(AdsIdentity.RolesShortClaim, AdsIdentity.DeployApproverRole));

        var response = await Approve(hash, caller);

        Assert.Equal(200, response.StatusCode);
    }

    [Theory]
    [InlineData("deploy.approver")]
    [InlineData("DEPLOY.APPROVER")]
    public async Task AnotherCasingOfTheRoleIsRefused(string role)
    {
        var hash = Preview(_gate, TestTenants.Contoso, _requester);

        var response = await Approve(hash, Organizational(Guid.NewGuid(), role));

        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("PLAN_APPROVER_ROLE_REQUIRED", response.ErrorMessage);
    }

    // --- implied rights (D-04) ---

    [Fact]
    public async Task APersonalAccountApprovesWithoutARole()
    {
        var ada = Guid.NewGuid();
        var hash = Preview(_gate, ada, _requester);

        var response = await Approve(hash, TestServerCallContext.ForPersonalAccount(ada, "Ada"));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task TheSingleUserHostApprovesWithoutARole()
    {
        var hash = Preview(_gate, TestTenants.Contoso, _requester);

        var response = await Approve(hash, TestServerCallContext.ForSingleUserHost(Guid.NewGuid(), "Ada"));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task NoIdentityIs401()
    {
        var response = await Approve(NewHash(), TestServerCallContext.WithoutIdentity());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(_db.AdsPlanApprovals);
    }
}

/// <summary>The role rule over the real host: the role comes from the token, the preview is seeded in
/// the host's own gate, and a refusal is a 403 in the response rather than an RpcException.</summary>
public sealed class ApprovalRulesHostTests(AdsServerFactory factory) : IClassFixture<AdsServerFactory>
{
    private static readonly TestClaims Approver = new(Roles: [AdsIdentity.DeployApproverRole]);

    private string SeedPreview()
    {
        var gate = factory.Services.GetRequiredService<WhatIfTokenGate>();
        return ApprovalRulesTests.Preview(gate, TestTenants.Contoso, Guid.NewGuid());
    }

    [Fact]
    public async Task ATokenWithTheRoleApprovesAPreviewInItsScope()
    {
        var hash = SeedPreview();
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid(), claims: Approver);

        var response = await new Deploy.DeployClient(channel).ApprovePlanAsync(new PlanApprovalRequest { PlanHash = hash });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(hash, response.PlanHash);
    }

    [Fact]
    public async Task ATokenWithoutTheRoleGets403InTheResponse()
    {
        var hash = SeedPreview();
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid());

        var response = await new Deploy.DeployClient(channel).ApprovePlanAsync(new PlanApprovalRequest { PlanHash = hash });

        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("PLAN_APPROVER_ROLE_REQUIRED", response.ErrorMessage);
    }
}
