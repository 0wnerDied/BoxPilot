using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed partial class ToastItemViewModel : ViewModelBase
{
    private readonly Action<ToastItemViewModel> dismiss;

    public ToastItemViewModel(
        string message,
        ToastLevel level,
        Action<ToastItemViewModel> dismiss)
    {
        Message = message;
        Level = level;
        this.dismiss = dismiss;
    }

    public string Message { get; }

    public ToastLevel Level { get; }

    public bool IsSuccess => Level == ToastLevel.Success;

    public bool IsError => Level == ToastLevel.Error;

    [ObservableProperty]
    public partial bool IsDismissing { get; set; }

    [RelayCommand]
    private void Dismiss()
    {
        dismiss(this);
    }
}

public sealed class ToastViewModel : IDisposable
{
    private const int MaximumQueuedItems = 8;
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan OpaqueDuration = DisplayDuration - FadeDuration;
    private readonly DispatcherTimer fadeTimer;
    private readonly DispatcherTimer removalTimer;
    private bool disposed;

    public ToastViewModel()
    {
        fadeTimer = new DispatcherTimer(
            OpaqueDuration,
            DispatcherPriority.Normal,
            BeginCurrentFade);
        removalTimer = new DispatcherTimer(
            FadeDuration,
            DispatcherPriority.Normal,
            RemoveCurrent);
    }

    public ObservableCollection<ToastItemViewModel> Items { get; } = [];

    public void Show(string message, ToastLevel level = ToastLevel.Info)
    {
        if (disposed || string.IsNullOrWhiteSpace(message))
            return;

        if (Items.Count == MaximumQueuedItems)
            Items.RemoveAt(Items.Count - 1);

        Items.Add(new ToastItemViewModel(message.Trim(), level, Dismiss));
        if (Items.Count == 1)
            StartCurrentLifetime();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        fadeTimer.Stop();
        removalTimer.Stop();
        Items.Clear();
    }

    private void Dismiss(ToastItemViewModel item)
    {
        if (Items.Count == 0)
            return;

        if (!ReferenceEquals(Items[0], item))
        {
            Items.Remove(item);
            return;
        }

        if (item.IsDismissing)
        {
            RemoveCurrent(this, EventArgs.Empty);
            return;
        }

        fadeTimer.Stop();
        item.IsDismissing = true;
        removalTimer.Start();
    }

    private void StartCurrentLifetime()
    {
        if (disposed || Items.Count == 0)
            return;

        fadeTimer.Stop();
        removalTimer.Stop();
        Items[0].IsDismissing = false;
        fadeTimer.Start();
    }

    private void BeginCurrentFade(object? sender, EventArgs eventArgs)
    {
        fadeTimer.Stop();
        if (Items.Count == 0)
            return;

        Items[0].IsDismissing = true;
        removalTimer.Start();
    }

    private void RemoveCurrent(object? sender, EventArgs eventArgs)
    {
        fadeTimer.Stop();
        removalTimer.Stop();
        if (Items.Count == 0)
            return;

        Items.RemoveAt(0);
        StartCurrentLifetime();
    }
}
