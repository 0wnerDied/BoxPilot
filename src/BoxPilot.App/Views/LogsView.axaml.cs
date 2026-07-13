using System.Collections.Specialized;
using Avalonia.Controls;
using BoxPilot.App.ViewModels;

namespace BoxPilot.App.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? viewModel;
    private bool isSubscribed;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        Unsubscribe();
        viewModel = DataContext as LogsViewModel;
        Subscribe();
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (viewModel?.AutoScroll == true && viewModel.VisibleLogs.LastOrDefault() is { } latest)
            LogList.ScrollIntoView(latest);
    }

    private void Subscribe()
    {
        if (isSubscribed || viewModel is null)
            return;

        viewModel.VisibleLogs.CollectionChanged += OnLogsChanged;
        isSubscribed = true;
    }

    private void Unsubscribe()
    {
        if (!isSubscribed || viewModel is null)
            return;

        viewModel.VisibleLogs.CollectionChanged -= OnLogsChanged;
        isSubscribed = false;
    }
}
