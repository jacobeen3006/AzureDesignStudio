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

    public const string ScopeClaimType = "http://schemas.microsoft.com/identity/claims/scope";

    /// <summary>The checked-in <c>AzureAd:Scopes</c>; a test ties the two together.</summary>
    public const string ScopeValue = "Server.Access";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header: no result, so the pipeline challenges and gRPC answers Unauthenticated.
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(ObjectIdClaimType, user.ToString()),
            new("name", "Tester"),
        };
        if (!Request.Headers.ContainsKey(NoScopeHeader))
            claims.Add(new Claim(ScopeClaimType, ScopeValue));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
