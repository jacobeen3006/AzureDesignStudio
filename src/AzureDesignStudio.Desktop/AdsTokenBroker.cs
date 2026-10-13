using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// The single token seam of the desktop host (plan 6.2). Server-API tokens keep the
/// MSAL/WAM path consumed by the gRPC bearer handler; Azure SDK clients get ARM tokens
/// through one chain, first winner:
/// cached MSAL account (silent) → <c>az login</c> CLI → managed identity →
/// service principal (secret from Windows Credential Manager, never config) →
/// interactive WAM/device-code last.
/// </summary>
public sealed class AdsTokenBroker : TokenCredential
{
    private readonly MsalAccountService _msal;
    private readonly TokenCredential _armChain;

    public AdsTokenBroker(MsalAccountService msal, IConfiguration config, ILogger<AdsTokenBroker> logger)
    {
        _msal = msal;

        var chain = new List<TokenCredential>
        {
            new MsalArmSilentCredential(msal),      // signed-in WAM identity, no prompt
            new AzureCliCredential(),               // az login -> token without prompt
            new ManagedIdentityCredential(new ManagedIdentityCredentialOptions()), // Azure-hosted context
        };

        var tenantId = config["ArmServicePrincipal:TenantId"];
        var clientId = config["ArmServicePrincipal:ClientId"];
        var clientSecret = WindowsSecretStore.TryGet("ArmServicePrincipal"); // secret lives in Credential Manager
        if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
            chain.Add(new ClientSecretCredential(tenantId, clientId, clientSecret));
        else if (clientSecret != null)
            logger.LogWarning("Credential Manager has an ArmServicePrincipal secret but ArmServicePrincipal:TenantId/ClientId are not set; SP credential skipped.");

        chain.Add(new MsalArmInteractiveCredential(msal)); // interactive last; unavailable when MSAL unconfigured

        _armChain = new ChainedTokenCredential(chain.ToArray());
    }

    /// <summary>Bearer for the gRPC server APIs; null while anonymous (requests stay unauthenticated).</summary>
    public Task<string?> GetServerTokenAsync(CancellationToken cancellationToken = default)
        => _msal.GetTokenSilentAsync();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        => await _armChain.GetTokenAsync(requestContext, cancellationToken);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        => _armChain.GetToken(requestContext, cancellationToken);
}
