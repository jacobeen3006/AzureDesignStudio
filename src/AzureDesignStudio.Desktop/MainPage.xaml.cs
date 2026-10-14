namespace AzureDesignStudio.Desktop;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
		// Telemetry stays off on desktop until AI wiring lands (plan 4.2).
		blazorWebView.RootComponents[0].Parameters = new Dictionary<string, object?> { ["EnableTelemetry"] = false };
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
}
