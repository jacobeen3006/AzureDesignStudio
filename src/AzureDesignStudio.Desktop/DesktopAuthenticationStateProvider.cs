using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Anonymous auth state until MSAL sign-in lands (plan 4.2). Index and the
/// server-backed drawers stay inert while unauthenticated.
/// </summary>
public class DesktopAuthenticationStateProvider : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
}
