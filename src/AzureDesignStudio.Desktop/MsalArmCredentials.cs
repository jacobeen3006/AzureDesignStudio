using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// <see cref="TokenCredential"/> adapters over <see cref="MsalAccountService"/> so the
/// WAM/device-code path can participate in the broker's chain (plan 6.2). Sync acquisition
/// blocks because MSAL only exposes async APIs.
/// </summary>
internal sealed class MsalArmSilentCredential(MsalAccountService msal) : TokenCredential
{
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
    {
        EnsureArmScope(requestContext);
        var token = await msal.GetArmTokenSilentAsync();
        if (token == null)
            throw new CredentialUnavailableException("No cached MSAL account for silent ARM token.");
        return new AccessToken(token, JwtExpiry(token));
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    // The MSAL PCA only pre-authorizes the ARM scope; other scopes must fall through
    // the chain instead of being served a wrong-scope token.
    internal static void EnsureArmScope(TokenRequestContext requestContext)
    {
        if (requestContext.Scopes.Length != 1 || requestContext.Scopes[0] != MsalAccountService.ArmScope)
            throw new CredentialUnavailableException($"MSAL credential only serves '{MsalAccountService.ArmScope}'.");
    }

    /// <summary>Expiry from the JWT `exp` claim; falls back to an hour when unparseable.</summary>
    internal static DateTimeOffset JwtExpiry(string jwt)
    {
        try
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
            return DateTimeOffset.FromUnixTimeSeconds(doc.RootElement.GetProperty("exp").GetInt64());
        }
        catch
        {
            return DateTimeOffset.UtcNow.AddHours(1);
        }
    }
}

internal sealed class MsalArmInteractiveCredential(MsalAccountService msal) : TokenCredential
{
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
    {
        MsalArmSilentCredential.EnsureArmScope(requestContext);
        var token = await msal.GetArmTokenInteractiveAsync();
        if (token == null)
            throw new CredentialUnavailableException("Interactive ARM acquisition was canceled or MSAL is unconfigured.");
        return new AccessToken(token, MsalArmSilentCredential.JwtExpiry(token));
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
}
