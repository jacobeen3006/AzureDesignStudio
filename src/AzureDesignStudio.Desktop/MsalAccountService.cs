using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Native MSAL public client (plan 4.2): work + personal accounts through the
/// Windows broker (WAM / Web Account Manager) on Windows and the system browser on
/// macOS, with device-code fallback when interactive UI is unavailable. Tokens for
/// the gRPC calls come from the serialized account cache via silent acquisition.
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
        // Sign-in asks for Azure management access: the app works against Azure directly,
        // with no server of its own. A ServerScope is only for a build that talks to the
        // hosted server; "<...>" placeholders count as unset.
        var scope = config["ServerScope"];
        _scopes = string.IsNullOrWhiteSpace(scope) || scope.Contains('<') ? new[] { ArmScope } : new[] { scope };

        if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith('<'))
        {
            // No app registration configured (placeholder in appsettings) - sign-in
            // stays disabled; everything else runs anonymous exactly like WASM unauthenticated.
            _pca = null!;
            return;
        }

#if WINDOWS
        // Extension (not instance method) - shadows the deprecated WithBroker(bool).
        // The Windows broker (WAM) answers on ms-appx-web://microsoft.aad.brokerplugin/{clientId},
        // packaged or not; MSAL picks that itself. The registration lists it next to
        // http://localhost (MSAL's loopback when the broker is unavailable) - no custom redirect here.
        var builder = Microsoft.Identity.Client.Broker.BrokerExtension.WithBroker(
            PublicClientApplicationBuilder.Create(clientId).WithAuthority(authority),
            new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Azure Design Studio sign-in" });

        _pca = builder.Build();
#else
        // macOS: no broker. Sign-in runs in the system browser and answers on the
        // http://localhost loopback the registration already lists; the account cache is
        // kept in the login Keychain so the next launch starts signed in.
        _pca = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(authority)
            .WithRedirectUri("http://localhost")
            .Build();
        KeychainTokenCache.Bind(_pca.UserTokenCache, logger);
#endif
    }

    public bool IsConfigured => _pca != null;

    /// <summary>The cached account used for silent token acquisition, if signed in; null also when the broker itself is unhealthy.</summary>
    public async Task<IAccount?> GetCurrentAccountAsync()
    {
        if (!IsConfigured)
            return null;
        try
        {
            return (await _pca.GetAccountsAsync()).FirstOrDefault();
        }
        catch (Exception e) when (IsBrokerOrMsalFailure(e))
        {
            // A broken Windows broker must not take the process down: the app keeps
            // running anonymous (exactly like WASM unauthenticated) and sign-in can
            // still succeed through the device-code fallback.
            _logger.LogWarning(e, "Account enumeration failed (broker unhealthy); continuing anonymous");
            return null;
        }
    }

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
        catch (Exception e) when (IsBrokerOrMsalFailure(e))
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
        catch (Exception e) when (IsBrokerOrMsalFailure(e))
        {
            return null; // cache miss / consent required / broker down; the chain moves on
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
                .WithPlatformUi()
                .ExecuteAsync();
        }
        catch (MsalClientException e) when (e.ErrorCode == "authentication_canceled")
        {
            _logger.LogInformation("Interactive ARM consent canceled by user");
            return null;
        }
        catch (Exception e) when (IsBrokerOrMsalFailure(e))
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

        AuthenticationResult? result = null;


        try
        {
            var accounts = await _pca.GetAccountsAsync();
            var interactive = _pca.AcquireTokenInteractive(_scopes)
                .WithAccount(accounts.FirstOrDefault())
                .WithPrompt(Prompt.SelectAccount)
                .WithPlatformUi();
            result = await interactive.ExecuteAsync();
        }
        catch (MsalClientException e) when (e.ErrorCode == "authentication_canceled")
        {
            _logger.LogInformation("Interactive sign-in canceled by user");
            return false;
        }
        catch (Exception e) when (IsBrokerOrMsalFailure(e))
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
        try
        {
            foreach (var account in await _pca.GetAccountsAsync())
                await _pca.RemoveAsync(account);
        }
        catch (Exception e) when (IsBrokerOrMsalFailure(e))
        {
            // Best-effort: a broker that cannot enumerate must still end the local
            // session - the UI state below is the authority for the running app.
            _logger.LogWarning(e, "Sign-out cache clear failed (broker unhealthy)");
        }
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

    /// <summary>The account as a principal. The object id ("oid") is what the services key a
    /// user's records by; without it every call answered "not signed in".</summary>
    internal static ClaimsPrincipal BuildPrincipal(IAccount account)
    {
        var username = account.Username ?? account.HomeAccountId?.ObjectId ?? "signed-in";
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new("preferred_username", username),
        };
        if (account.HomeAccountId?.ObjectId is { } objectId)
            claims.Add(new Claim("oid", objectId));
        if (account.HomeAccountId?.TenantId is { } tenantId)
            claims.Add(new Claim("tid", tenantId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "sso"));
    }

    private void ReportSignInFailure(Exception e)
    {
        _logger.LogError(e, "Sign-in failed");
        var code = e is MsalException m ? m.ErrorCode : e.GetType().Name;
        SetWindowTitle($"Sign-in failed: {code} - {Truncate(e.Message)}");
    }

    // Device-code prompt: the WebView has no place to render it, so surface the
    // code through the window title + clipboard (paste into the browser prompt).
    private Task ShowDeviceCodeAsync(DeviceCodeResult deviceCode)
    {
        var message = $"Sign in at {deviceCode.VerificationUrl} with code {deviceCode.UserCode}";
        // The code itself stays out of the log file: it is shown, not recorded.
        _logger.LogInformation("Device-code sign-in started; the code is in the window title and on the clipboard.");

        // MSAL calls this from a thread-pool thread. The window and the clipboard belong to
        // the UI thread: touching them from here threw (wrong-thread COM error), the catch
        // below swallowed only the clipboard half, and the fallback sign-in died without the
        // user ever seeing a code.
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            SetWindowTitle(message);
            try
            {
                await Clipboard.Default.SetTextAsync(deviceCode.UserCode!);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Could not copy device code to clipboard");
            }
        });
    }

    private static void SetWindowTitle(string title)
    {
        static void Set(string text)
        {
            var window = Application.Current?.Windows?.FirstOrDefault();
            if (window != null)
                window.Title = text;
        }

        // Callers include continuations after MSAL awaits, which need not be on the UI thread.
        if (MainThread.IsMainThread)
            Set(title);
        else
            MainThread.BeginInvokeOnMainThread(() => Set(title));
    }

#if WINDOWS
    internal static IntPtr GetWindowHandle()
    {
        var platformView = Application.Current?.Windows?.FirstOrDefault()?.Handler?.PlatformView;
        if (platformView is Microsoft.UI.Xaml.Window uiWindow)
            return WinRT.Interop.WindowNative.GetWindowHandle(uiWindow);
        return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
    }
#endif

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "...";

    // The WAM broker's native interop rethrows as an internal MSALErrorException that
    // derives from neither MsalException nor any other public type - name match is the
    // only way to keep it from escaping as an unhandled task failure.
    private static bool IsBrokerInteropFailure(Exception e) => e.GetType().Name == "MSALErrorException";
    private static bool IsBrokerOrMsalFailure(Exception e) => e is MsalException || IsBrokerInteropFailure(e);
}

internal static class InteractiveSignInExtensions
{
    /// <summary>Windows: the broker's account picker, parented to the app window. macOS: the
    /// system browser, opened through MAUI's launcher (MSAL's own browser start-up does not know
    /// Mac Catalyst).</summary>
    public static AcquireTokenInteractiveParameterBuilder WithPlatformUi(this AcquireTokenInteractiveParameterBuilder builder) =>
#if WINDOWS
        builder.WithParentActivityOrWindow(MsalAccountService.GetWindowHandle());
#else
        builder.WithUseEmbeddedWebView(false)
            .WithSystemWebViewOptions(new SystemWebViewOptions
            {
                OpenBrowserAsync = uri => MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.OpenAsync(uri)),
            });
#endif
}
