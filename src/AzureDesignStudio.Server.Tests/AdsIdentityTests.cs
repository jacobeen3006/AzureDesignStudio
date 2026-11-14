using System.Security.Claims;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Auth;
using Xunit;

namespace AzureDesignStudio.Server.Tests;

/// <summary>Table tests of the claim-to-identity derivation and of the caller rules built on it.</summary>
public class AdsIdentityTests
{
    private const string OidUri = AdsIdentity.ObjectIdUriClaim;
    private const string TidUri = AdsIdentity.TenantIdUriClaim;

    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Tenant = TestTenants.Contoso;

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static AdsIdentity Derive(params (string Type, string Value)[] claims)
    {
        Assert.True(AdsIdentity.TryFrom(Principal(claims), out var identity, out var failure), failure);
        return identity!;
    }

    private static string Refuse(params (string Type, string Value)[] claims)
    {
        Assert.False(AdsIdentity.TryFrom(Principal(claims), out var identity, out var failure));
        Assert.Null(identity);
        Assert.NotEmpty(failure);
        return failure;
    }

    // --- the accepted spellings ---

    public static TheoryData<string, string> AcceptedSpellings => new()
    {
        { OidUri, TidUri },
        { "oid", "tid" },
        { OidUri, "tid" },
        { "oid", TidUri },
    };

    [Theory]
    [MemberData(nameof(AcceptedSpellings))]
    public void EveryCombinationOfSpellingsIsTheSameCaller(string oid, string tid)
    {
        var identity = Derive((oid, User.ToString()), (tid, Tenant.ToString()));

        Assert.Equal(User, identity.UserId);
        Assert.Equal(Tenant, identity.TenantId);
        Assert.Equal(Tenant, identity.ScopeId);
        Assert.False(identity.IsPersonalAccount);
    }

    [Fact]
    public void TwoSpellingsThatAgreeSucceed()
    {
        var identity = Derive((OidUri, User.ToString()), ("oid", User.ToString().ToUpperInvariant()),
            (TidUri, Tenant.ToString()), ("tid", Tenant.ToString("N")));

        Assert.Equal(User, identity.UserId);
        Assert.Equal(Tenant, identity.TenantId);
    }

    // --- refusals ---

    [Fact]
    public void NoPrincipalIsRefused()
    {
        Assert.False(AdsIdentity.TryFrom(null, out _, out var failure));
        Assert.NotEmpty(failure);
    }

    [Fact]
    public void MissingOidIsRefusedAndNamed() =>
        Assert.Contains("object id", Refuse((TidUri, Tenant.ToString())));

    [Fact]
    public void MissingTidIsRefusedAndNamed() =>
        Assert.Contains("tenant id", Refuse((OidUri, User.ToString())));

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void UnusableOidIsRefused(string bad)
    {
        Refuse((OidUri, bad), (TidUri, Tenant.ToString()));
        Refuse(("oid", bad), (TidUri, Tenant.ToString()));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void UnusableTidIsRefused(string bad)
    {
        Refuse((OidUri, User.ToString()), (TidUri, bad));
        Refuse((OidUri, User.ToString()), ("tid", bad));
    }

    [Fact]
    public void ABadSpellingIsNotRescuedByAGoodOne()
    {
        Refuse((OidUri, User.ToString()), ("oid", "not-a-guid"), (TidUri, Tenant.ToString()));
        Refuse((OidUri, User.ToString()), (TidUri, Tenant.ToString()), ("tid", "not-a-guid"));
    }

    [Fact]
    public void DisagreeingObjectIdSpellingsAreRefused() =>
        Assert.Contains("disagree", Refuse(
            (OidUri, User.ToString()), ("oid", Guid.NewGuid().ToString()), (TidUri, Tenant.ToString())));

    [Fact]
    public void DisagreeingTenantSpellingsAreRefused() =>
        Assert.Contains("disagree", Refuse(
            (OidUri, User.ToString()), (TidUri, Tenant.ToString()), ("tid", TestTenants.Fabrikam.ToString())));

    [Theory]
    [InlineData("sub")]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("nameid")]
    public void TheSubjectNeverIsTheIdentity(string subjectClaim) =>
        Refuse((subjectClaim, User.ToString()), (TidUri, Tenant.ToString()));

    [Fact]
    public void TheSubjectDoesNotFillInForAMissingOid()
    {
        // Even with a valid oid in the other spelling absent, sub must not be consulted.
        var failure = Refuse(("sub", User.ToString()), ("tid", Tenant.ToString()));
        Assert.Contains("object id", failure);
    }

    [Fact]
    public void FailureTextNeverCarriesAClaimValue()
    {
        var secret = "secret-" + Guid.NewGuid();
        var texts = new[]
        {
            Refuse((OidUri, secret), (TidUri, Tenant.ToString())),
            Refuse((OidUri, User.ToString()), (TidUri, secret)),
            Refuse((OidUri, User.ToString()), ("oid", Guid.NewGuid().ToString()), (TidUri, Tenant.ToString())),
            Refuse((OidUri, User.ToString())),
        };

        foreach (var text in texts)
        {
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain(User.ToString(), text);
            Assert.DoesNotContain(Tenant.ToString(), text);
        }
    }

    // --- scope ---

    [Fact]
    public void ThePersonalAccountTenantScopesToTheUser()
    {
        var identity = Derive((OidUri, User.ToString()), (TidUri, TestTenants.Consumer.ToString()));

        Assert.True(identity.IsPersonalAccount);
        Assert.Equal(TestTenants.Consumer, identity.TenantId);
        Assert.Equal(User, identity.ScopeId);
        Assert.Equal(AdsIdentity.ConsumerTenantId, TestTenants.Consumer);
    }

    [Fact]
    public void TwoPersonalAccountsNeverShareAScope()
    {
        var a = Derive((OidUri, User.ToString()), (TidUri, TestTenants.Consumer.ToString()));
        var b = Derive((OidUri, Guid.NewGuid().ToString()), (TidUri, TestTenants.Consumer.ToString()));

        Assert.NotEqual(a.ScopeId, b.ScopeId);
    }

    [Fact]
    public void TwoMembersOfOneOrganizationShareAScope()
    {
        var a = Derive((OidUri, User.ToString()), (TidUri, Tenant.ToString()));
        var b = Derive((OidUri, Guid.NewGuid().ToString()), (TidUri, Tenant.ToString()));

        Assert.Equal(a.ScopeId, b.ScopeId);
        Assert.NotEqual(a.UserId, b.UserId);
    }

    // --- roles ---

    [Fact]
    public void RolesComeFromTheMappedAndTheShortClaim()
    {
        var identity = Derive((OidUri, User.ToString()), (TidUri, Tenant.ToString()),
            (ClaimTypes.Role, AdsIdentity.GuardrailAdminRole), ("roles", AdsIdentity.DeployApproverRole));

        Assert.Contains(AdsIdentity.GuardrailAdminRole, identity.Roles);
        Assert.Contains(AdsIdentity.DeployApproverRole, identity.Roles);
    }

    [Theory]
    [InlineData("guardrail.admin")]
    [InlineData("GUARDRAIL.ADMIN")]
    public void TheRoleMatchIsCaseSensitive(string role)
    {
        var identity = Derive((OidUri, User.ToString()), (TidUri, Tenant.ToString()), (ClaimTypes.Role, role));

        Assert.DoesNotContain(AdsIdentity.GuardrailAdminRole, identity.Roles);
        Assert.False(new AdsCaller(identity, false, null).CanAdministerGuardrails);
    }

    // --- SameUser ---

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    public void SameUserIgnoresFormat(string format)
    {
        Assert.True(AdsIdentity.SameUser(User.ToString(format), User));
        Assert.True(AdsIdentity.SameUser(User.ToString(format).ToUpperInvariant(), User));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ada")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TextThatIsNoGuidIsNoUser(string? stored) =>
        Assert.False(AdsIdentity.SameUser(stored, User));

    [Fact]
    public void AnotherGuidIsAnotherUser() =>
        Assert.False(AdsIdentity.SameUser(Guid.NewGuid().ToString(), User));

    // --- the caller rules ---

    private static AdsCaller Caller(Guid tenant, bool singleUser = false, string? name = null, params string[] roles)
    {
        var claims = new List<(string, string)> { (OidUri, User.ToString()), (TidUri, tenant.ToString()) };
        claims.AddRange(roles.Select(r => (ClaimTypes.Role, r)));
        return new AdsCaller(Derive([.. claims]), singleUser, name);
    }

    [Fact]
    public void ASingleUserHostIsItsOwnAdministratorAndApprover()
    {
        var caller = Caller(Tenant, singleUser: true);

        Assert.True(caller.CanAdministerGuardrails);
        Assert.True(caller.CanApprove);
        Assert.True(caller.MaySelfApprove(false));
        Assert.True(caller.MaySelfApprove(true));
    }

    [Fact]
    public void APersonalAccountIsAdministratorAndApproverButNotSelfApprover()
    {
        var caller = Caller(TestTenants.Consumer);

        Assert.True(caller.CanAdministerGuardrails);
        Assert.True(caller.CanApprove);
        Assert.False(caller.MaySelfApprove(false));
        Assert.True(caller.MaySelfApprove(true));
    }

    [Fact]
    public void AnOrganizationalCallerNeedsTheRoles()
    {
        var plain = Caller(Tenant);
        Assert.False(plain.CanAdministerGuardrails);
        Assert.False(plain.CanApprove);
        Assert.False(plain.MaySelfApprove(false));
        Assert.True(plain.MaySelfApprove(true));

        var admin = Caller(Tenant, roles: AdsIdentity.GuardrailAdminRole);
        Assert.True(admin.CanAdministerGuardrails);
        Assert.False(admin.CanApprove);

        var approver = Caller(Tenant, roles: AdsIdentity.DeployApproverRole);
        Assert.True(approver.CanApprove);
        Assert.False(approver.CanAdministerGuardrails);
    }

    [Fact]
    public void PrincipalIsNameAndIdOrJustTheId()
    {
        Assert.Equal($"Ada <{User:D}>", Caller(Tenant, name: "  Ada ").Principal);
        Assert.Equal(User.ToString("D"), Caller(Tenant).Principal);
        Assert.Equal(User.ToString("D"), Caller(Tenant, name: "   ").Principal);
        Assert.Equal(User.ToString("D"), Caller(Tenant).UserIdText);
    }

    [Fact]
    public void PrincipalNeverExceedsTheAuditColumnWidth()
    {
        var principal = Caller(Tenant, name: new string('x', 5000)).Principal;

        Assert.True(principal.Length <= 320);
        Assert.EndsWith($" <{User:D}>", principal);
    }

    [Theory]
    [InlineData("Ada\r\nFAKE LOG LINE", "Ada FAKE LOG LINE")]
    [InlineData("Ada\u0000\u0007Lovelace", "AdaLovelace")]
    [InlineData("Ada\u202Egnidner\u200B", "Adagnidner")]
    [InlineData("Ada\u2028Lovelace", "AdaLovelace")]
    [InlineData("  Ada \t  Lovelace  ", "Ada Lovelace")]
    public void ThePrincipalNameLosesControlAndFormatCharacters(string raw, string expected) =>
        Assert.Equal($"{expected} <{User:D}>", Caller(Tenant, name: raw).Principal);

    [Fact]
    public void ADisplayNameCanNeverForgeTheIdSuffix()
    {
        var victim = Guid.NewGuid();
        var principal = Caller(Tenant, name: $"Alice <{victim:D}>").Principal;

        Assert.Equal($"Alice {victim:D} <{User:D}>", principal);
        // The only angle brackets are the real suffix, and it is the caller's own id.
        Assert.Equal(1, principal.Count(c => c == '<'));
        Assert.Equal(1, principal.Count(c => c == '>'));
        Assert.EndsWith($" <{User:D}>", principal);
        Assert.Equal(User.ToString("D"), Caller(Tenant, name: "<>").Principal);
    }

    [Fact]
    public void ACutDisplayNameNeverSplitsASurrogatePairAndStaysUnderTheWidth()
    {
        var principal = Caller(Tenant, name: string.Concat(Enumerable.Repeat("\U0001F600", 5000))).Principal;
        var body = principal[..^($" <{User:D}>".Length)];

        Assert.True(principal.Length <= 320);
        Assert.True(body.Length > 0);
        Assert.False(char.IsHighSurrogate(body[^1]), "a cut must not leave a lone high surrogate");
        Assert.EndsWith($" <{User:D}>", principal);
    }

    [Fact]
    public void ASingleGraphemeLongerThanTheWidthFallsBackToTheIdAlone() =>
        Assert.Equal(User.ToString("D"), Caller(Tenant, name: "e" + new string('\u0301', 5000)).Principal);

    [Fact]
    public void AnUnpairedSurrogateInTheNameIsDropped() =>
        Assert.Equal($"AdaLovelace <{User:D}>", Caller(Tenant, name: "Ada\uD800Lovelace").Principal);

    // --- through the service accessor ---

    [Fact]
    public void TryGetCallerReadsThePrincipalAndTheDisplayName()
    {
        var context = TestServerCallContext.ForUser(User, "Ada", roles: [AdsIdentity.DeployApproverRole]);

        Assert.True(ServiceTools.TryGetCaller(context, out var caller, out _));
        Assert.Equal(User, caller.UserId);
        Assert.Equal("Ada", caller.DisplayName);
        Assert.True(caller.CanApprove);
        Assert.False(caller.IsSingleUserHost);
    }

    [Fact]
    public void TryGetCallerRefusesAnAnonymousAndATenantlessCaller()
    {
        Assert.False(ServiceTools.TryGetCaller(TestServerCallContext.WithoutIdentity(), out _, out _));
        Assert.False(ServiceTools.TryGetCaller(TestServerCallContext.WithoutTenant(User), out _, out _));
    }

    [Fact]
    public void TheHostMarkerCountsOnlyWhenItIsTheBooleanTrue()
    {
        Assert.True(ServiceTools.TryGetCaller(TestServerCallContext.ForSingleUserHost(User), out var marked, out _));
        Assert.True(marked.IsSingleUserHost);

        var forged = TestServerCallContext.ForUser(User);
        forged.UserState[ServiceTools.SingleUserHostKey] = "true";
        Assert.True(ServiceTools.TryGetCaller(forged, out var text, out _));
        Assert.False(text.IsSingleUserHost);
    }

    [Fact]
    public void NoClaimCanDeclareTheSingleUserHost()
    {
        var context = TestServerCallContext.WithClaims(
            new Claim(OidUri, User.ToString()), new Claim(TidUri, Tenant.ToString()),
            new Claim(ServiceTools.SingleUserHostKey, "true"), new Claim(ClaimTypes.Role, "SingleUserHost"));

        Assert.True(ServiceTools.TryGetCaller(context, out var caller, out _));
        Assert.False(caller.IsSingleUserHost);
        Assert.False(caller.CanAdministerGuardrails);
    }
}
