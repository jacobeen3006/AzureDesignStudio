namespace AzureDesignStudio.Desktop;

/// <summary>
/// Attaches the server-API access token to gRPC calls on authenticated endpoints
/// ([Authorize] on Design/Deploy services). Requests go anonymous while the
/// user is not signed in, matching the WASM behavior before login. The token comes
/// from the host-wide <see cref="AdsTokenBroker"/> seam (plan 6.2).
/// </summary>
public sealed class MsalAuthTokenHandler(AdsTokenBroker broker) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await broker.GetServerTokenAsync();
        if (token != null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
