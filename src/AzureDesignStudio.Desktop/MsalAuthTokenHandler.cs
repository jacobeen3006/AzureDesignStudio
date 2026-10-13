namespace AzureDesignStudio.Desktop;

/// <summary>
/// Attaches the MSAL access token to gRPC calls on authenticated endpoints
/// ([Authorize] on Design/Deploy services). Requests go anonymous while the
/// user is not signed in, matching the WASM behavior before login.
/// </summary>
public sealed class MsalAuthTokenHandler(MsalAccountService msal) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await msal.GetTokenSilentAsync();
        if (token != null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
