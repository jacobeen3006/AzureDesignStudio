namespace AzureDesignStudio.Desktop;

/// <summary>
/// Native page (no Blazor, no services) shown when the workspace could not be upgraded. The message
/// names the error, the premigration backup and, for a stuck migration lock, the recovery statement.
/// </summary>
public sealed class StartupFailurePage : ContentPage
{
    public StartupFailurePage(Exception? failure)
    {
        Title = "Azure Design Studio";

        var details = new Editor
        {
            IsReadOnly = true,
            AutoSize = EditorAutoSizeOption.TextChanges,
            Text = failure?.Message ?? "The workspace could not be opened.",
        };

        var openFolder = new Button { Text = "Open data folder" };
        openFolder.Clicked += (_, _) => FolderReveal.OpenFolder(MauiProgram.DataDirectory);

        var quit = new Button { Text = "Quit" };
        quit.Clicked += (_, _) => Application.Current?.Quit();

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new Label { Text = "Your workspace could not be opened", FontSize = 22, FontAttributes = FontAttributes.Bold },
                    details,
                    openFolder,
                    quit,
                },
            },
        };
    }
}
