using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BoxPilot.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class LogsViewModel : ViewModelBase
{
    public LogsViewModel(AppSessionViewModel session)
    {
        Session = session;
        Session.Logs.CollectionChanged += OnLogsChanged;
        RebuildVisibleLogs();
    }

    public AppSessionViewModel Session { get; }

    public ObservableCollection<CoreLogEntry> VisibleLogs { get; } = [];

    [ObservableProperty]
    public partial bool AutoScroll { get; set; } = true;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public string ResultCount => string.IsNullOrWhiteSpace(SearchText)
        ? VisibleLogs.Count.ToString()
        : $"{VisibleLogs.Count}/{Session.Logs.Count}";

    [RelayCommand]
    private void Clear() => Session.ClearLogs();

    partial void OnSearchTextChanged(string value)
    {
        RebuildVisibleLogs();
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action == NotifyCollectionChangedAction.Add && eventArgs.NewItems is not null)
        {
            foreach (var entry in eventArgs.NewItems.OfType<CoreLogEntry>().Where(MatchesSearch))
                VisibleLogs.Add(entry);
        }
        else
        {
            RebuildVisibleLogs();
        }
        OnPropertyChanged(nameof(ResultCount));
    }

    private void RebuildVisibleLogs()
    {
        VisibleLogs.Clear();
        foreach (var entry in Session.Logs.Where(MatchesSearch))
            VisibleLogs.Add(entry);
        OnPropertyChanged(nameof(ResultCount));
    }

    private bool MatchesSearch(CoreLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var query = SearchText.Trim();
        return entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
               || entry.LevelLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
               || entry.Stream.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
