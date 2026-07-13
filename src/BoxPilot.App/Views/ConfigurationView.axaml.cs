using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BoxPilot.App.ViewModels;
using BoxPilot.Core.Models;

namespace BoxPilot.App.Views;

public partial class ConfigurationView : UserControl
{
    public ConfigurationView()
    {
        InitializeComponent();
    }

    private async void ImportRuleSetClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ConfigurationViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = viewModel.RuleSetPickerTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(viewModel.RuleSetPickerTitle)
                {
                    Patterns = ["*.json", "*.srs"],
                },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            await viewModel.ImportLocalRuleSetAsync(path);
    }

    private async void RemoveRuleSetClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ConfigurationViewModel viewModel
            && sender is Button { DataContext: CustomRuleSet ruleSet })
        {
            await viewModel.RemoveRuleSetAsync(ruleSet);
        }
    }
}
