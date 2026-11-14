using System.Net;
using System.Security.Claims;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Auth;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
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

    // --- WR-06: an approval is never silently taken over ---

    [Fact]
    public async Task AnotherApproverCannotReplaceAnExistingApproval()
    {
        var hash = Preview(_gate, TestTenants.Contoso, _requester);
        var first = Guid.NewGuid();
        Assert.Equal(200, (await Approve(hash, Organizational(first, AdsIdentity.DeployApproverRole))).StatusCode);

        var second = await Approve(hash, Organizational(Guid.NewGuid(), AdsIdentity.DeployApproverRole));

        Assert.Equal(409, second.StatusCode);
        Assert.StartsWith("PLAN_ALREADY_APPROVED", second.ErrorMessage);
        var stored = Assert.Single(_db.AdsPlanApprovals);
        Assert.Equal(first.ToString("D"), stored.ApprovedByUserId);
        Assert.Equal(_requester.ToString("D"), stored.RequestedByUserId);
        Assert.Empty(_db.AdsSecurityEvents);
    }

    [Fact]
    public async Task TheSameApproverReplacingTheirApprovalLeavesAnAuditEventNamingTheRequesterChange()
    {
        var hash = Preview(_gate, TestTenants.Contoso, _requester);
        var approver = Guid.NewGuid();
        Assert.Equal(200, (await Approve(hash, Organizational(approver, AdsIdentity.DeployApproverRole))).StatusCode);

        // Another user of the same scope re-runs the What-If before the approver approves again.
        var capturer = Guid.NewGuid();
        Preview(_gate, TestTenants.Contoso, capturer, hash);
        Assert.Equal(200, (await Approve(hash, Organizational(approver, AdsIdentity.DeployApproverRole))).StatusCode);

        var stored = Assert.Single(_db.AdsPlanApprovals);
        Assert.Equal(capturer.ToString("D"), stored.RequestedByUserId);
        var audit = Assert.Single(_db.AdsSecurityEvents);
        Assert.Equal(SecurityEventTypes.PlanApprovalReplaced, audit.EventType);
        Assert.Equal(TestTenants.Contoso, audit.TenantId);
        Assert.Equal(approver.ToString("D"), audit.PrincipalUserId);
        Assert.Contains(_requester.ToString("D"), audit.Detail);
        Assert.Contains(capturer.ToString("D"), audit.Detail);
    }

    [Fact]
    public async Task AFirstApprovalWritesNoReplacementEvent()
    {
        var hash = Preview(_gate, TestTenants.Contoso, _requester);

        await Approve(hash, Organizational(Guid.NewGuid(), AdsIdentity.DeployApproverRole));

        Assert.Empty(_db.AdsSecurityEvents);
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

/// <summary>
/// Self-approval is its own rule (D-02, D-07): the principal whose what-if produced a preview cannot
/// approve it unless the tenant allows that (the audited setting, default off) or the caller is the
/// single-user Desktop host. It runs after the role rule and has its own code. The requester - not a
/// third party - then deploys the approval (D-07).
/// </summary>
public sealed class SelfApprovalAndRequesterTests
{
    private const string ResourceGroup = "rg";
    private const string StackTemplate =
        """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[{"type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","name":"sa1","location":"westeurope","sku":{"name":"Standard_LRS"},"kind":"StorageV2"}]}""";

    private readonly DesignDbContext _db = TestData.NewDb();
    private readonly WhatIfTokenGate _gate = new(WhatIfTokenGate.DefaultTtl);

    private DeployService Service => new(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(),
        _gate, new ScriptedHttp(_ => null));

    private static ServerCallContext Admin(Guid scope) =>
        TestServerCallContext.ForUser(Guid.NewGuid(), "Admin", scope, [AdsIdentity.GuardrailAdminRole]);

    private static ServerCallContext Approver(Guid user, Guid? tenant = null, string? name = null) =>
        TestServerCallContext.ForUser(user, name ?? "Ada", tenant ?? TestTenants.Contoso, [AdsIdentity.DeployApproverRole]);

    /// <summary>Turns the tenant setting on through the audited RPC, as an administrator of that scope.</summary>
    private async Task AllowSelfApproval(ServerCallContext administrator)
    {
        var set = await Service.SetTenantSettings(
            new TenantSettingsRequest { AllowSelfApproval = true, GuardrailPolicyMode = "replace" }, administrator);
        Assert.Equal(200, set.StatusCode);
    }

    // --- the approval matrix ---

    public static TheoryData<string, bool, bool, int, string?> Matrix => new()
    {
        { "org-approver", false, false, 200, null },
        { "org-approver", true, false, 403, "PLAN_SELF_APPROVAL_REFUSED" },      // role plus requester with setting off
        { "org-approver", true, true, 200, null },
        { "org-approver", false, true, 200, null },
        { "org-no-role", false, false, 403, "PLAN_APPROVER_ROLE_REQUIRED" },
        { "org-no-role", true, false, 403, "PLAN_APPROVER_ROLE_REQUIRED" },      // no role plus requester: the role comes first
        { "org-no-role", true, true, 403, "PLAN_APPROVER_ROLE_REQUIRED" },       // the setting grants no role
        { "personal", false, false, 200, null },
        { "personal", true, false, 403, "PLAN_SELF_APPROVAL_REFUSED" },          // the implied role does not imply self-approval
        { "personal", true, true, 200, null },
        { "single-user", false, false, 200, null },
        { "single-user", true, false, 200, null },                               // the Desktop host approves its own preview
        { "single-user", true, true, 200, null },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task TheApprovalMatrix(string kind, bool callerIsRequester, bool settingOn, int status, string? code)
    {
        var user = Guid.NewGuid();
        var requester = callerIsRequester ? user : Guid.NewGuid();
        var (scope, caller, administrator) = kind switch
        {
            "org-approver" => (TestTenants.Contoso, Approver(user), Admin(TestTenants.Contoso)),
            "org-no-role" => (TestTenants.Contoso, TestServerCallContext.ForUser(user, "Ada", TestTenants.Contoso), Admin(TestTenants.Contoso)),
            "personal" => (user, TestServerCallContext.ForPersonalAccount(user, "Ada"), TestServerCallContext.ForPersonalAccount(user, "Ada")),
            "single-user" => (TestTenants.Contoso, TestServerCallContext.ForSingleUserHost(user, "Ada"), TestServerCallContext.ForSingleUserHost(user, "Ada")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var hash = ApprovalRulesTests.Preview(_gate, scope, requester);
        if (settingOn)
            await AllowSelfApproval(administrator);

        var response = await Service.ApprovePlan(new PlanApprovalRequest { PlanHash = hash }, caller);

        Assert.Equal(status, response.StatusCode);
        if (code is null)
        {
            Assert.Equal("", response.ErrorMessage);
            var row = await _db.AdsPlanApprovals.AsNoTracking().SingleAsync();
            Assert.Equal(requester.ToString("D"), row.RequestedByUserId);
            Assert.Equal(user.ToString("D"), row.ApprovedByUserId);
        }
        else
        {
            Assert.StartsWith(code, response.ErrorMessage);
            Assert.Empty(_db.AdsPlanApprovals);
        }
    }

    [Fact]
    public async Task TheSelfApprovalRefusalNamesItsWayOut()
    {
        var user = Guid.NewGuid();
        var hash = ApprovalRulesTests.Preview(_gate, TestTenants.Contoso, user);

        var response = await Service.ApprovePlan(new PlanApprovalRequest { PlanHash = hash }, Approver(user));

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("allow self-approval", response.ErrorMessage);
    }

    [Fact]
    public async Task ARefusedSelfApprovalLeavesThePreviewForAnotherApprover()
    {
        var requester = Guid.NewGuid();
        var hash = ApprovalRulesTests.Preview(_gate, TestTenants.Contoso, requester);

        var refused = await Service.ApprovePlan(new PlanApprovalRequest { PlanHash = hash }, Approver(requester));
        var other = await Service.ApprovePlan(new PlanApprovalRequest { PlanHash = hash }, Approver(Guid.NewGuid()));

        Assert.Equal(403, refused.StatusCode);
        Assert.Equal(200, other.StatusCode);
        Assert.Single(_db.AdsPlanApprovals);
    }

    [Fact]
    public async Task AnotherTenantsSettingDoesNotLiftTheRefusal()
    {
        var user = Guid.NewGuid();
        var hash = ApprovalRulesTests.Preview(_gate, TestTenants.Contoso, user);
        await AllowSelfApproval(Admin(TestTenants.Fabrikam));

        var response = await Service.ApprovePlan(new PlanApprovalRequest { PlanHash = hash }, Approver(user));

        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("PLAN_SELF_APPROVAL_REFUSED", response.ErrorMessage);
    }

    [Fact]
    public async Task ASecondApproverCannotReplaceTheRowSoTheRequesterOfTheFirstApprovalStands()
    {
        // WR-06: the second approver is refused (409), the first approver's row and its requester stay.
        var requester = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var hash = ApprovalRulesTests.Preview(_gate, TestTenants.Contoso, requester);

        await Service.ApprovePlan(new PlanApprovalRequest { PlanHash = hash }, Approver(first));
        var refused = await Service.ApprovePlan(new PlanApprovalRequest { PlanHash = hash }, Approver(second));

        Assert.Equal(409, refused.StatusCode);
        var row = await _db.AdsPlanApprovals.AsNoTracking().SingleAsync();
        Assert.Equal(first.ToString("D"), row.ApprovedByUserId);
        Assert.Equal(requester.ToString("D"), row.RequestedByUserId);
    }

    [Fact]
    public async Task APreviewWithNoRecordedRequesterCannotBeSelfApproved()
    {
        var hash = ApprovalRulesTests.NewHash();
        _gate.Issue("sub", "rg", "tpl", "prm", ApprovalRulesTests.OneCreate(), "{}", hash, scopeId: TestTenants.Contoso);

        var response = await Service.ApprovePlan(new PlanApprovalRequest { PlanHash = hash }, Approver(Guid.NewGuid()));

        Assert.Equal(200, response.StatusCode);
        Assert.Null((await _db.AdsPlanApprovals.AsNoTracking().SingleAsync()).RequestedByUserId);
    }

    // --- the requester deploys (stack) ---

    private sealed class StackRig
    {
        public required ScriptedArm Arm { get; init; }
        public required DeployService Service { get; init; }
        public required Guid Subscription { get; init; }

        public StackWhatIfRequest Preview() => new()
        {
            SubscriptionId = Subscription.ToString(), ResourceGroupName = ResourceGroup, StackName = "ads-stack",
            ArmTemplate = StackTemplate, Parameters = "{}", ResourcesAction = "detach",
        };

        public CreateStackRequest Create(StackWhatIfResponse preview, bool withToken = true) => new()
        {
            SubscriptionId = Subscription.ToString(), ResourceGroupName = ResourceGroup, StackName = "ads-stack",
            ArmTemplate = StackTemplate, Parameters = "{}", ResourcesAction = "detach",
            WhatIfToken = withToken ? preview.WhatIfToken : "", PlanHash = preview.PlanHash,
        };
    }

    private StackRig NewStackRig()
    {
        var subscription = Guid.NewGuid();
        var arm = new ScriptedArm(subscription.ToString(), ResourceGroup)
        {
            WhatIfResult = """{"properties":{"provisioningState":"succeeded","changes":{"resourceChanges":[{"id":"/subscriptions/"""
                + subscription + """/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1","type":"Microsoft.Storage/storageAccounts","changeType":"create","changeCertainty":"definite"}]}}}""",
        };
        return new StackRig
        {
            Arm = arm, Subscription = subscription,
            Service = new DeployService(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(),
                _gate, arm, new AmbientCredential()),
        };
    }

    [Fact]
    public async Task TheRequesterDeploysAStackSomeoneElseApprovedAndNobodyElseDoes()
    {
        var rig = NewStackRig();
        var requester = Approver(Guid.NewGuid(), name: "Ray");
        var approver = Approver(Guid.NewGuid(), name: "Ada");
        var third = Approver(Guid.NewGuid(), name: "Tom");
        var preview = await rig.Service.StackWhatIf(rig.Preview(), requester);
        Assert.NotEqual("", preview.WhatIfToken);
        Assert.Equal(200, (await rig.Service.ApprovePlan(new PlanApprovalRequest { PlanHash = preview.PlanHash }, approver)).StatusCode);

        var byApprover = await rig.Service.CreateStack(rig.Create(preview), approver);
        var byThird = await rig.Service.CreateStack(rig.Create(preview), third);

        Assert.Equal(403, byApprover.StatusCode);
        Assert.StartsWith("PLAN_REQUESTER_MISMATCH", byApprover.ErrorMessage);
        Assert.Equal(403, byThird.StatusCode);
        Assert.StartsWith("PLAN_REQUESTER_MISMATCH", byThird.ErrorMessage);
        Assert.Single(_db.AdsPlanApprovals);   // a refused call consumed nothing

        var byRequester = await rig.Service.CreateStack(rig.Create(preview), requester);

        Assert.True(byRequester.StatusCode == 200, byRequester.ErrorMessage);
    }

    [Fact]
    public async Task AStackApprovalWithNoRequesterKeepsTheOldApproverOnlyRule()
    {
        var rig = NewStackRig();
        var requester = Approver(Guid.NewGuid(), name: "Ray");
        var approver = Approver(Guid.NewGuid(), name: "Ada");
        var preview = await rig.Service.StackWhatIf(rig.Preview(), requester);
        await rig.Service.ApprovePlan(new PlanApprovalRequest { PlanHash = preview.PlanHash }, approver);
        var row = await _db.AdsPlanApprovals.SingleAsync();
        row.RequestedByUserId = null;
        await _db.SaveChangesAsync();

        var byRequester = await rig.Service.CreateStack(rig.Create(preview), requester);
        var byApprover = await rig.Service.CreateStack(rig.Create(preview), approver);

        Assert.Equal(403, byRequester.StatusCode);
        Assert.StartsWith("PLAN_APPROVER_MISMATCH", byRequester.ErrorMessage);
        Assert.True(byApprover.StatusCode == 200, byApprover.ErrorMessage);
    }

    // --- the requester deploys (deployment) ---

    private sealed class Collector<T> : IServerStreamWriter<T>
    {
        public List<T> Items { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message)
        {
            Items.Add(message);
            return Task.CompletedTask;
        }
    }

    private async Task<(DeployService Service, DeploymentRequest Request, WhatIfResponse WhatIf)> PreviewDeployment(ServerCallContext requester)
    {
        var subscription = Guid.NewGuid();
        var group = $"/subscriptions/{subscription}/resourceGroups/rg";
        var storage = $"{group}/providers/Microsoft.Storage/storageAccounts/sa1";
        var arm = new RouteTableHttp();
        arm.Answer(HttpMethod.Get, $"/subscriptions/{subscription}", HttpStatusCode.OK,
                "{\"id\":\"/subscriptions/" + subscription + "\",\"subscriptionId\":\"" + subscription + "\",\"displayName\":\"t\",\"state\":\"Enabled\"}")
            .Answer(HttpMethod.Get, group, HttpStatusCode.OK,
                "{\"id\":\"" + group + "\",\"name\":\"rg\",\"location\":\"westeurope\",\"properties\":{\"provisioningState\":\"Succeeded\"}}")
            .Answer(HttpMethod.Post, $"{group}/providers/Microsoft.Resources/deployments/{WhatIfClient.DeploymentName}/whatIf",
                HttpStatusCode.OK,
                "{\"status\":\"Succeeded\",\"properties\":{\"changes\":[{\"resourceId\":\"" + storage
                + "\",\"changeType\":\"Create\",\"after\":{\"id\":\"" + storage
                + "\",\"name\":\"sa1\",\"type\":\"Microsoft.Storage/storageAccounts\",\"apiVersion\":\"2023-01-01\","
                + "\"location\":\"westeurope\",\"sku\":{\"name\":\"Standard_LRS\"},\"kind\":\"StorageV2\"}}]}}");
        var service = new DeployService(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(),
            _gate, arm, new AmbientCredential());
        var whatIf = await service.WhatIf(new DeploymentRequest
        {
            SubscriptionId = subscription.ToString(), ResourceGroupName = "rg", ArmTemplate = StackTemplate, Parameters = "{}",
        }, requester);
        Assert.True(whatIf.StatusCode == 200, whatIf.ErrorMessage);
        return (service, new DeploymentRequest
        {
            SubscriptionId = subscription.ToString(), ResourceGroupName = "rg", ArmTemplate = StackTemplate, Parameters = "{}",
            PlanHash = whatIf.PlanHash,
        }, whatIf);
    }

    private static async Task<DeploymentResponse> Deploy(DeployService service, DeploymentRequest request, ServerCallContext caller)
    {
        var writer = new Collector<DeploymentResponse>();
        await service.CreateDeployment(request, writer, caller);
        return Assert.Single(writer.Items);
    }

    [Fact]
    public async Task TheRequesterGetsPastTheCheckForADeploymentSomeoneElseApprovedAndNobodyElseDoes()
    {
        var requester = Approver(Guid.NewGuid(), name: "Ray");
        var approver = Approver(Guid.NewGuid(), name: "Ada");
        var third = Approver(Guid.NewGuid(), name: "Tom");
        var (service, request, whatIf) = await PreviewDeployment(requester);
        Assert.Equal(200, (await service.ApprovePlan(new PlanApprovalRequest { PlanHash = whatIf.PlanHash }, approver)).StatusCode);

        var byApprover = await Deploy(service, request, approver);
        var byThird = await Deploy(service, request, third);
        var byRequester = await Deploy(service, request, requester);

        Assert.Equal(403, byApprover.StatusCode);
        Assert.StartsWith("PLAN_REQUESTER_MISMATCH", byApprover.ErrorMessage);
        Assert.Equal(403, byThird.StatusCode);
        Assert.StartsWith("PLAN_REQUESTER_MISMATCH", byThird.ErrorMessage);
        // No token was sent, so the requester stops at the review token - past the approval comparison,
        // before anything is submitted to Azure.
        Assert.Equal(400, byRequester.StatusCode);
        Assert.DoesNotContain("MISMATCH", byRequester.ErrorMessage);
        Assert.DoesNotContain("PLAN_NOT_APPROVED", byRequester.ErrorMessage);
    }

    [Fact]
    public async Task ADeploymentApprovalWithNoRequesterKeepsTheOldApproverOnlyRule()
    {
        var requester = Approver(Guid.NewGuid(), name: "Ray");
        var approver = Approver(Guid.NewGuid(), name: "Ada");
        var (service, request, whatIf) = await PreviewDeployment(requester);
        await service.ApprovePlan(new PlanApprovalRequest { PlanHash = whatIf.PlanHash }, approver);
        var row = await _db.AdsPlanApprovals.SingleAsync();
        row.RequestedByUserId = null;
        await _db.SaveChangesAsync();

        var byRequester = await Deploy(service, request, requester);
        var byApprover = await Deploy(service, request, approver);

        Assert.Equal(403, byRequester.StatusCode);
        Assert.StartsWith("PLAN_APPROVER_MISMATCH", byRequester.ErrorMessage);
        Assert.Equal(400, byApprover.StatusCode);
        Assert.DoesNotContain("MISMATCH", byApprover.ErrorMessage);
    }
}

/// <summary>
/// Decommission approvals (D-06): tenant scope and Deploy.Approver only, with no separate
/// self-approval rule. The execute step keeps its approver-only ownership check.
/// </summary>
public sealed class DecommissionApprovalRulesTests
{
    private static readonly string PlanHash = "sha256:" + new string('c', 64);
    private static readonly string ExecutionHash = "sha256:" + new string('b', 64);

    private readonly DesignDbContext _db = TestData.NewDb();
    private readonly Guid _subscription = Guid.NewGuid();

    private DeployService Service => new(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(),
        new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl), new ScriptedHttp(_ => null), new AmbientCredential());

    private ApproveDecomWindowRequest Approval(string? planHash = null) => new()
    {
        SubscriptionId = _subscription.ToString(), WindowId = "w1", PlanHash = planHash ?? PlanHash, ExecutionHash = ExecutionHash,
    };

    private ExecuteDecomWindowRequest Execution() => new()
    {
        SubscriptionId = _subscription.ToString(), ScanId = Guid.NewGuid().ToString(), WindowId = "w1",
        PlanHash = PlanHash, ExecutionHash = ExecutionHash,
        WindowStartUtc = "2026-01-01T00:00:00.0000000+00:00", WindowEndUtc = "2026-01-02T00:00:00.0000000+00:00",
        PurgeNotBeforeUtc = "2026-01-03T00:00:00.0000000+00:00",
    };

    private static ServerCallContext Organizational(Guid user, params string[] roles) =>
        TestServerCallContext.ForUser(user, "Ada", TestTenants.Contoso, roles);

    [Fact]
    public async Task AnOrganizationalCallerWithoutTheRoleIsRefused()
    {
        var response = await Service.ApproveDecomWindow(Approval(), Organizational(Guid.NewGuid()));

        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("DECOM_APPROVER_ROLE_REQUIRED", response.ErrorMessage);
        Assert.Contains("Deploy.Approver", response.ErrorMessage);
        Assert.Empty(_db.AdsDecomApprovals);
    }

    [Fact]
    public async Task TheRoleIsCheckedBeforeTheHashesAreRead()
    {
        var response = await Service.ApproveDecomWindow(Approval(planHash: "not-a-hash"), Organizational(Guid.NewGuid()));

        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("DECOM_APPROVER_ROLE_REQUIRED", response.ErrorMessage);
    }

    [Theory]
    [InlineData("Guardrail.Admin")]
    [InlineData("deploy.approver")]
    public async Task AnotherRoleOrCasingIsRefused(string role)
    {
        var response = await Service.ApproveDecomWindow(Approval(), Organizational(Guid.NewGuid(), role));

        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("DECOM_APPROVER_ROLE_REQUIRED", response.ErrorMessage);
    }

    [Fact]
    public async Task AnApproverApproves()
    {
        var response = await Service.ApproveDecomWindow(Approval(), Organizational(Guid.NewGuid(), AdsIdentity.DeployApproverRole));

        Assert.Equal(200, response.StatusCode);
        Assert.Single(_db.AdsDecomApprovals);
    }

    [Fact]
    public async Task APersonalAccountApprovesWithoutARole()
    {
        var response = await Service.ApproveDecomWindow(Approval(), TestServerCallContext.ForPersonalAccount(Guid.NewGuid(), "Ada"));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task TheSingleUserHostApprovesWithoutARole()
    {
        var response = await Service.ApproveDecomWindow(Approval(), TestServerCallContext.ForSingleUserHost(Guid.NewGuid(), "Ada"));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task NoIdentityIs401()
    {
        var response = await Service.ApproveDecomWindow(Approval(), TestServerCallContext.WithoutIdentity());

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task AnApproverWhoExecutesTheirOwnApprovalIsNotRefusedForSelfApproval()
    {
        var ada = Organizational(Guid.NewGuid(), AdsIdentity.DeployApproverRole);
        Assert.Equal(200, (await Service.ApproveDecomWindow(Approval(), ada)).StatusCode);

        var response = await Service.ExecuteDecomWindow(Execution(), ada);

        // The ownership check passes; this request names no scopes, so a later check ends it.
        Assert.NotEqual(403, response.StatusCode);
        Assert.DoesNotContain("SELF_APPROVAL", response.ErrorMessage);
        Assert.DoesNotContain("DECOM_APPROVER_MISMATCH", response.ErrorMessage);
    }

    [Fact]
    public async Task AnotherPrincipalStillCannotExecuteTheApproval()
    {
        Assert.Equal(200, (await Service.ApproveDecomWindow(Approval(),
            Organizational(Guid.NewGuid(), AdsIdentity.DeployApproverRole))).StatusCode);

        var response = await Service.ExecuteDecomWindow(Execution(),
            Organizational(Guid.NewGuid(), AdsIdentity.DeployApproverRole));

        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("DECOM_APPROVER_MISMATCH", response.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteLooksRowsUpInTheCallersScopeOnly()
    {
        Assert.Equal(200, (await Service.ApproveDecomWindow(Approval(),
            Organizational(Guid.NewGuid(), AdsIdentity.DeployApproverRole))).StatusCode);

        var response = await Service.ExecuteDecomWindow(Execution(),
            TestServerCallContext.ForUser(Guid.NewGuid(), "Bob", TestTenants.Fabrikam, [AdsIdentity.DeployApproverRole]));

        Assert.Equal(412, response.StatusCode);
        Assert.StartsWith("DECOM_APPROVAL_MISSING", response.ErrorMessage);
    }
}
