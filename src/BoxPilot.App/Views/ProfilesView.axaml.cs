using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BoxPilot.App.ViewModels;

namespace BoxPilot.App.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
    }

    private async void ImportConfigurationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ProfilesViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = viewModel.ImportConfigurationTitle,
            AllowMultiple = true,
            FileTypeFilter = [CreateJsonFileType(viewModel.ImportConfigurationTitle)],
        });
        var paths = files.Select(static file => file.TryGetLocalPath())
            .Where(static path => path is not null)
            .Cast<string>()
            .ToArray();
        if (paths.Length > 0)
            await viewModel.ImportConfigurationAsync(paths);
    }

    private async void ExportConfigurationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ProfilesViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = viewModel.ExportConfigurationTitle,
            SuggestedFileName = viewModel.ExportFileName,
            DefaultExtension = "json",
            FileTypeChoices = [CreateJsonFileType(viewModel.ExportConfigurationTitle)],
        });
        var path = file?.TryGetLocalPath();
        if (path is not null)
            await viewModel.ExportConfigurationAsync(path);
    }

    private static FilePickerFileType CreateJsonFileType(string name)
    {
        return new FilePickerFileType(name)
        {
            Patterns = ["*.json"],
            MimeTypes = ["application/json"],
        };
    }
}
