using AzureDesignStudio.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureDesignStudio.Desktop;

public partial class MainPage : ContentPage
{
    // Once per process: a second window (Mac Catalyst) must not repeat the alert.
    private static int _recoveryShown;

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;

#if WINDOWS
        // window.open from app content (e.g. the topology inspector's portal link) must go to the system browser.
        blazorWebView.BlazorWebViewInitialized += (_, e) => e.WebView.CoreWebView2.NewWindowRequested += async (_, ev) =>
        {
            ev.Handled = true;
            if (ev.Uri is { } target)
            {
                await Browser.Default.OpenAsync(target);
            }
        };
#endif
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        // A MAUI lifecycle event handler cannot return a Task; every failure is contained here.
        if (Interlocked.Exchange(ref _recoveryShown, 1) == 1) return;
        try
        {
            var report = MauiProgram.Services.GetService<WorkspaceStartupReport>();
            if (report?.Notice is { } notice)
                await DisplayAlertAsync("Workspace recovered", notice.Message, "OK");
            if (report?.Upgrade.RemovedRowsMessage is { } removed)
                await DisplayAlertAsync("Duplicate rows removed", removed, "OK");
            if (report?.Restore is { Applied: true } restore)
            {
                var kept = restore.PreRestorePath is null ? string.Empty : $"\nThe previous workspace was kept at: {restore.PreRestorePath}";
                await DisplayAlertAsync("Workspace restored", $"Restored from the backup {restore.SourceName}.{kept}", "OK");
            }
            else if (report?.Restore is { Applied: false, Problem: { } problem } failed)
            {
                await DisplayAlertAsync("Restore not applied", $"The staged restore of {failed.SourceName} was not applied: {problem}", "OK");
            }
        }
        catch (Exception ex)
        {
            MauiProgram.Services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?
                .CreateLogger<MainPage>()
                .LogWarning(ex, "The workspace recovery alert could not be shown.");
        }
    }
}
