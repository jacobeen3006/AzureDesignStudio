using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Native MSAL public client (plan 4.2): work + personal accounts through the
/// Windows broker (WAM / Web Account Manager), with device-code fallback when
/// embedded interactive UI is unavailable. Tokens for the gRPC calls come from
/// the serialized account cache via silent acquisition.
/// </summary>
public sealed class MsalAccountService
{
    private readonly IPublicClientApplication _pca;
    private readonly string[] _scopes;

    /// <summary>ARM scope acquired through the same PCA (WAM interactive path) for Azure SDK clients.</summary>
    public const string ArmScope = "https://management.azure.com/.default";
    private readonly ILogger<MsalAccountService> _logger;

    /// <summary>Raised after sign-in/sign-out with the new user (null = anonymous).</summary>
    public event Func<ClaimsPrincipal?, Task>? AuthStateChanged;

    public MsalAccountService(IConfiguration config, ILogger<MsalAccountService> logger)
    {
        _logger = logger;

        var clientId = config["AzureAd:ClientId"];
        var authority = config["AzureAd:Authority"] ?? "https://login.microsoftonline.com/common";
        var scope = config["ServerScope"];
        _scopes = string.IsNullOrWhiteSpace(scope) ? Array.Empty<string>() : new[] { scope };

        if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith('<'))
        {
            // No app registration configured (placeholder in appsettings) - sign-in
            // stays disabled; everything else runs anonymous exactly like WASM unauthenticated.
            _pca = null!;
            return;
        }

        // Extension (not instance method) - shadows the deprecated WithBroker(bool).
        var builder = Microsoft.Identity.Client.Broker.BrokerExtension.WithBroker(
            PublicClientApplicationBuilder.Create(clientId).WithAuthority(authority),
            new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Azure Design Studio sign-in" });

        // Packaged (MSIX) identity needs the broker's msauth redirect; unpackaged
        // dev builds keep MSAL's default loopback redirect.
        try
        {
            var packageFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            builder = builder.WithRedirectUri($"msauth://{packageFamilyName}/auth");
        }
        catch (Exception)
        {
            // unpackaged
        }

        _pca = builder.Build();
    }

    public bool IsConfigured => _pca != null;

    /// <summary>The cached account used for silent token acquisition, if signed in.</summary>
    public async Task<IAccount?> GetCurrentAccountAsync()
        => IsConfigured ? (await _pca.GetAccountsAsync()).FirstOrDefault() : null;

    /// <summary>Access token for the server API from cache; null when anonymous or interactive consent required.</summary>
    public async Task<string?> GetTokenSilentAsync()
    {
        if (!IsConfigured || _scopes.Length == 0)
            return null;
        try
        {
            var account = (await _pca.GetAccountsAsync()).FirstOrDefault();
            if (account is null)
                return null;
            var result = await _pca.AcquireTokenSilent(_scopes, account)
                .ExecuteAsync();
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return null; // token can't refresh silently; UI shows sign-in prompt as in WASM
        }
        catch (MsalException e)
        {
            _logger.LogWarning(e, "Silent token acquisition failed");
            return null;
        }
    }

    /// <summary>ARM management token from the serialized cache only; never prompts.</summary>
    public async Task<string?> GetArmTokenSilentAsync()
    {
        if (!IsConfigured)
            return null;
        try
        {
            var account = (await _pca.GetAccountsAsync()).FirstOrDefault();
            if (account is null)
                return null;
            var result = await _pca.AcquireTokenSilent(new[] { ArmScope }, account)
                .ExecuteAsync();
            return result.AccessToken;
        }
        catch (MsalException)
        {
            return null; // cache miss / consent required; the broker chain moves on
        }
    }

    /// <summary>
    /// Interactive ARM token: WAM broker first, device-code fallback - the same UX as
    /// server-API sign-in. Null when the user cancels or MSAL is unconfigured.
    /// </summary>
    public async Task<string?> GetArmTokenInteractiveAsync()
    {
        if (!IsConfigured)
            return null;
        AuthenticationResult? result = null;
        try
        {
            var accounts = await _pca.GetAccountsAsync();
            result = await _pca.AcquireTokenInteractive(new[] { ArmScope })
                .WithAccount(accounts.FirstOrDefault())
                .WithParentActivityOrWindow(GetWindowHandle())
                .ExecuteAsync();
        }
        catch (MsalClientException e) when (e.ErrorCode == "authentication_canceled")
        {
            _logger.LogInformation("Interactive ARM consent canceled by user");
            return null;
        }
        catch (MsalException e)
        {
            _logger.LogWarning(e, "Interactive ARM acquisition failed, falling back to device code flow");
            try
            {
                result = await _pca.AcquireTokenWithDeviceCode(new[] { ArmScope }, ShowDeviceCodeAsync)
                    .ExecuteAsync();
            }
            catch (MsalException e2)
            {
                ReportSignInFailure(e2);
                return null;
            }
        }
        return result?.AccessToken;
    }

    /// <summary>
    /// Interactive sign-in: WAM broker first, device-code fallback (browser-less
    /// hosts / broker failures). Returns true when a token was obtained.
    /// </summary>
    public async Task<bool> SignInAsync()
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Sign-in requested but AzureAd:ClientId is not configured (see appsettings.json).");
            return false;
        }

        var accounts = await _pca.GetAccountsAsync();
        AuthenticationResult? result = null;

        try
        {
            var interactive = _pca.AcquireTokenInteractive(_scopes)
                .WithAccount(accounts.FirstOrDefault())
                .WithPrompt(Prompt.SelectAccount)
                .WithParentActivityOrWindow(GetWindowHandle());
            result = await interactive.ExecuteAsync();
        }
        catch (MsalClientException e) when (e.ErrorCode == "authentication_canceled")
        {
            _logger.LogInformation("Interactive sign-in canceled by user");
            return false;
        }
        catch (MsalException e)
        {
            // Broker/embedded UI unavailable or broken - fall back to device code.
            _logger.LogWarning(e, "Interactive sign-in failed, falling back to device code flow");
            try
            {
                result = await _pca.AcquireTokenWithDeviceCode(_scopes, ShowDeviceCodeAsync)
                    .ExecuteAsync();
            }
            catch (MsalException e2)
            {
                ReportSignInFailure(e2);
                return false;
            }
        }

        await NotifySignedInAsync(result!.Account);
        return true;
    }

    public async Task SignOutAsync()
    {
        if (!IsConfigured)
            return;
        foreach (var account in await _pca.GetAccountsAsync())
            await _pca.RemoveAsync(account);
        if (AuthStateChanged != null)
            await AuthStateChanged(null);
    }

    /// <summary>Rebuilds the auth state from the persisted cache on startup.</summary>
    public async Task<ClaimsPrincipal?> RestorePrincipalAsync()
    {
        var account = await GetCurrentAccountAsync();
        return account is null ? null : BuildPrincipal(account);
    }

    private async Task NotifySignedInAsync(IAccount account)
    {
        if (AuthStateChanged != null)
            await AuthStateChanged(BuildPrincipal(account));
    }

    private static ClaimsPrincipal BuildPrincipal(IAccount account)
    {
        var username = account.Username ?? account.HomeAccountId?.ObjectId ?? "signed-in";
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim("preferred_username", username),
        }, authenticationType: "sso");
        return new ClaimsPrincipal(identity);
    }

    private void ReportSignInFailure(MsalException e)
    {
        _logger.LogError(e, "Sign-in failed");
        SetWindowTitle($"Sign-in failed: {e.ErrorCode} - {Truncate(e.Message)}");
    }

    // Device-code prompt: the WebView has no place to render it, so surface the
    // code through the window title + clipboard (paste into the browser prompt).
    private Task ShowDeviceCodeAsync(DeviceCodeResult deviceCode)
    {
        var message = $"Sign in at {deviceCode.VerificationUrl} with code {deviceCode.UserCode}";
        _logger.LogInformation(message);
        SetWindowTitle(message);
        try
        {
            Clipboard.Default.SetTextAsync(deviceCode.UserCode!).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not copy device code to clipboard");
        }
        return Task.CompletedTask;
    }

    private static void SetWindowTitle(string title)
    {
        var window = Application.Current?.Windows?.FirstOrDefault();
        if (window != null)
            window.Title = title;
    }

    private static IntPtr GetWindowHandle()
    {
        var platformView = Application.Current?.Windows?.FirstOrDefault()?.Handler?.PlatformView;
        if (platformView is Microsoft.UI.Xaml.Window uiWindow)
            return WinRT.Interop.WindowNative.GetWindowHandle(uiWindow);
        return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "...";
}
