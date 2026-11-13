using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Sign-in state for the shared UI, from <see cref="DesktopIdentity"/>: the account signed in
/// inside the app, or else the Azure CLI's account. Restored on first use; follows sign-in and
/// sign-out raised by <see cref="MsalAccountService"/>.
/// </summary>
public class DesktopAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly DesktopIdentity _identity;
    private AuthenticationState _current = new(DesktopIdentity.Anonymous);
    private bool _restored;

    public DesktopAuthenticationStateProvider(MsalAccountService msal, DesktopIdentity identity)
    {
        _identity = identity;
        msal.AuthStateChanged += user =>
        {
            if (user is null)
                _identity.MarkSignedOut();
            else
                _identity.MarkSignedIn();

            _current = new AuthenticationState(user ?? DesktopIdentity.Anonymous);
            NotifyAuthenticationStateChanged(Task.FromResult(_current));
            return Task.CompletedTask;
        };
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (!_restored)
        {
            _restored = true;
            _current = new AuthenticationState(await _identity.GetAsync());
        }
        return _current;
    }
}
