using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// Header-driven authentication for tests of the real host. It exists only in this assembly:
/// a scheme like this inside the server would be a credential bypass.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "x-test-user";

    /// <summary>Authenticated, but the token carries no scope claim.</summary>
    public const string NoScopeHeader = "x-test-no-scope";

    /// <summary>What <c>ServiceTools.GetUserId</c> reads first.</summary>
    public const string ObjectIdClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public const string TenantIdClaimType = "http://schemas.microsoft.com/identity/claims/tenantid";

    /// <summary>Tenant GUID text for this request; default is <c>TestTenants.Contoso</c>.</summary>
    public const string TenantHeader = "x-test-tenant";

    /// <summary>Authenticated, but the token carries no tenant claim.</summary>
    public const string NoTidHeader = "x-test-no-tid";

    /// <summary>Authenticated, but the token carries no object id claim.</summary>
    public const string NoOidHeader = "x-test-no-oid";

    /// <summary>Repeatable: each value becomes a role claim.</summary>
    public const string RoleHeader = "x-test-role";

    /// <summary>Repeatable: <c>type|value</c>, split at the first '|', adds any claim.</summary>
    public const string ClaimHeader = "x-test-claim";

    public const string ScopeClaimType = "http://schemas.microsoft.com/identity/claims/scope";

    /// <summary>The checked-in <c>AzureAd:Scopes</c>; a test ties the two together.</summary>
    public const string ScopeValue = "Server.Access";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header: no result, so the pipeline challenges and gRPC answers Unauthenticated.
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("name", "Tester") };
        if (!Request.Headers.ContainsKey(NoOidHeader))
            claims.Add(new Claim(ObjectIdClaimType, user.ToString()));
        if (!Request.Headers.ContainsKey(NoTidHeader))
        {
            var tenant = Request.Headers.TryGetValue(TenantHeader, out var chosen)
                ? chosen.ToString()
                : TestTenants.Contoso.ToString();
            claims.Add(new Claim(TenantIdClaimType, tenant));
        }
        foreach (var role in Request.Headers[RoleHeader])
            claims.Add(new Claim(ClaimTypes.Role, role!));
        foreach (var extra in Request.Headers[ClaimHeader])
        {
            var split = extra!.IndexOf('|');
            if (split > 0)
                claims.Add(new Claim(extra[..split], extra[(split + 1)..]));
        }
        if (!Request.Headers.ContainsKey(NoScopeHeader))
            claims.Add(new Claim(ScopeClaimType, ScopeValue));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
