using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Authentication state backed by the native MSAL account cache. Starts
/// anonymous (or restores the persisted account on first call); reacts to
/// sign-in/sign-out raised by <see cref="MsalAccountService"/>.
/// </summary>
public class DesktopAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly MsalAccountService _msal;
    private AuthenticationState _current = new(new ClaimsPrincipal(new ClaimsIdentity()));
    private bool _restored;

    public DesktopAuthenticationStateProvider(MsalAccountService msal)
    {
        _msal = msal;
        _msal.AuthStateChanged += user =>
        {
            _current = new AuthenticationState(user is null
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : user);
            NotifyAuthenticationStateChanged(Task.FromResult(_current));
            return Task.CompletedTask;
        };
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (!_restored)
        {
            _restored = true;
            var principal = await _msal.RestorePrincipalAsync();
            if (principal != null)
                _current = new AuthenticationState(principal);
        }
        return _current;
    }
}
