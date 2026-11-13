using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Who is using the app. The account signed in inside the app (MSAL) when there is one;
/// otherwise the account the Azure CLI is signed in with (<c>az login</c>), read silently
/// from a management token - so the app is usable before an app registration exists, and
/// for people who live in the CLI. Signing out inside the app also stops the CLI fallback
/// until the next sign-in, so "signed out" means signed out.
///
/// The same principal is the sign-in state the UI shows and the caller the in-process
/// services see, so the two can never disagree.
/// </summary>
public sealed class DesktopIdentity(MsalAccountService msal, ILogger<DesktopIdentity> logger)
{
    public const string AzureCliAuthenticationType = "azure-cli";
    private static readonly TimeSpan CliCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly AzureCliCredential _cli = new(new AzureCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(20) });
    private readonly SemaphoreSlim _cliGate = new(1, 1);
    private (ClaimsPrincipal Principal, DateTimeOffset At)? _cliCache;
    private bool _signedOut;

    public static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());

    public async ValueTask<ClaimsPrincipal> GetAsync(CancellationToken cancellationToken = default)
    {
        if (await msal.GetCurrentAccountAsync() is { } account)
            return MsalAccountService.BuildPrincipal(account);

        return _signedOut ? Anonymous : await GetCliPrincipalAsync(cancellationToken);
    }

    public void MarkSignedIn() => _signedOut = false;

    public void MarkSignedOut()
    {
        _signedOut = true;
        _cliCache = null;
    }

    private async ValueTask<ClaimsPrincipal> GetCliPrincipalAsync(CancellationToken cancellationToken)
    {
        if (_cliCache is { } cached && DateTimeOffset.UtcNow - cached.At < CliCacheLifetime)
            return cached.Principal;

        await _cliGate.WaitAsync(cancellationToken);
        try
        {
            if (_cliCache is { } again && DateTimeOffset.UtcNow - again.At < CliCacheLifetime)
                return again.Principal;

            ClaimsPrincipal principal;
            try
            {
                var token = await _cli.GetTokenAsync(new TokenRequestContext([MsalAccountService.ArmScope]), cancellationToken);
                principal = FromAccessToken(token.Token) ?? Anonymous;
            }
            catch (Exception e) when (e is CredentialUnavailableException or AuthenticationFailedException)
            {
                // No CLI, or not signed in: simply anonymous.
                logger.LogDebug(e, "No Azure CLI sign-in to fall back on.");
                principal = Anonymous;
            }

            _cliCache = (principal, DateTimeOffset.UtcNow);
            return principal;
        }
        finally
        {
            _cliGate.Release();
        }
    }

    /// <summary>The identity claims of an Entra access token, unverified - the token came
    /// straight from the local CLI and is only read for who it was issued to.</summary>
    internal static ClaimsPrincipal? FromAccessToken(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = doc.RootElement;
            string? Claim(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var oid = Claim("oid");
            if (!Guid.TryParse(oid, out _))
                return null;
            var user = Claim("upn") ?? Claim("unique_name") ?? Claim("preferred_username") ?? Claim("email") ?? oid;
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user!),
                new("preferred_username", user!),
                new("oid", oid!),
            };
            if (Claim("name") is { } displayName)
                claims.Add(new Claim("name", displayName));
            if (Claim("tid") is { } tenant)
                claims.Add(new Claim("tid", tenant));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, AzureCliAuthenticationType));
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
