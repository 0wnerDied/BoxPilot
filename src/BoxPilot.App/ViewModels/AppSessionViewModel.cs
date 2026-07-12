using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using BoxPilot.App.Services;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BoxPilot.App.ViewModels;

public partial class AppSessionViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AppPaths paths;
    private readonly SettingsStore settingsStore;
    private readonly ProfileRepository profileRepository;
    private readonly SingBoxService singBox;
    private readonly SingBoxConfigService configService;
    private readonly ProfileImportService profileImporter;
    private readonly LocalizationService localization;
    private readonly ThemeService themes;
    private readonly ConcurrentQueue<CoreLogEntry> pendingLogs = new();
    private readonly ConfigurationDraftStore configurationDrafts = new();
    private readonly DispatcherTimer logFlushTimer;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private CancellationTokenSource? trafficCancellation;
    private bool suppressConfigurationDirty;
    private bool suppressProfileLoad;
    private bool disposed;

    public AppSessionViewModel(
        AppPaths paths,
        SettingsStore settingsStore,
        ProfileRepository profileRepository,
        SingBoxService singBox,
        SingBoxConfigService configService,
        ProfileImportService profileImporter,
        LocalizationService localization,
        ThemeService themes)
    {
        this.paths = paths;
        this.settingsStore = settingsStore;
        this.profileRepository = profileRepository;
        this.singBox = singBox;
        this.configService = configService;
        this.profileImporter = profileImporter;
        this.localization = localization;
        this.themes = themes;

        singBox.LogReceived += OnLogReceived;
        singBox.StateChanged += OnCoreStateChanged;
        localization.LanguageChanged += OnLanguageChanged;
        logFlushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, FlushLogs);
        logFlushTimer.Start();
    }

    public ObservableCollection<Profile> Profiles { get; } = [];

    public ObservableCollection<CoreLogEntry> Logs { get; } = [];

    [ObservableProperty]
    public partial AppSettings Settings { get; private set; } = new();

    [ObservableProperty]
    public partial Profile? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string ConfigurationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConfigurationDirty { get; private set; }

    [ObservableProperty]
    public partial CoreState CoreState { get; private set; } = CoreState.Stopped;

    [ObservableProperty]
    public partial string CoreVersion { get; private set; } = "—";

    [ObservableProperty]
    public partial string CorePlatform { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial long UploadBytesPerSecond { get; private set; }

    [ObservableProperty]
    public partial long DownloadBytesPerSecond { get; private set; }

    [ObservableProperty]
    public partial bool IsInitialized { get; private set; }

    public bool IsCoreRunning => CoreState == CoreState.Running;

    public bool HasLogs => Logs.Count > 0;

    public bool CanStartCore => !IsBusy
                                && SelectedProfile is not null
                                && CoreState is CoreState.Stopped or CoreState.Faulted;

    public bool CanStopCore => !IsBusy && CoreState is CoreState.Running or CoreState.Starting;

    public bool CanModifyProfiles => !IsBusy && !IsCoreRunning;

    public bool CanRefreshSubscription => !IsBusy
                                          && !IsConfigurationDirty
                                          && SelectedProfile?.Source == ProfileSource.Subscription
                                          && !string.IsNullOrWhiteSpace(SelectedProfile.SubscriptionUrl);

    public string UploadRate => FormatRate(UploadBytesPerSecond);

    public string DownloadRate => FormatRate(DownloadBytesPerSecond);

    public int ActiveNodeCount => SelectedProfile?.NodeCount ?? 0;

    public string ActiveNodeDisplay => $"{ActiveNodeCount} {localization["Nodes"]}";

    public string CoreStateDisplay => localization[CoreState.ToString()];

    public string TunDisplay => localization[Settings.EnableTun ? "Enabled" : "Disabled"];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsInitialized)
                return;

            IsBusy = true;
            Settings = await settingsStore.LoadAsync(cancellationToken);
            localization.Apply(Settings.Language);
            themes.Apply(Settings.Theme);
            StatusMessage = localization["Working"];

            try
            {
                var version = await singBox.InitializeAsync(Settings.SingBoxPath, cancellationToken);
                CoreVersion = version.Version;
                CorePlatform = version.Platform;
            }
            catch (FileNotFoundException)
            {
                CoreVersion = localization["CoreNotFound"];
                StatusMessage = localization["CoreNotFoundHelp"];
            }
            catch (Exception exception)
            {
                CoreVersion = localization["CoreNotFound"];
                StatusMessage = exception.Message;
            }

            await ReloadProfilesAsync(cancellationToken);
            if (Profiles.Count == 0)
            {
                var starter = configService.Serialize(configService.CreateStarterConfiguration(Settings));
                var profile = await profileRepository.CreateAsync(
                    localization["ManualProfile"],
                    starter,
                    ProfileSource.Manual,
                    cancellationToken);
                Profiles.Add(profile);
            }

            var selected = Settings.SelectedProfileId is { } selectedId
                ? Profiles.FirstOrDefault(profile => profile.Id == selectedId)
                : null;
            await SelectProfileAsync(selected ?? Profiles[0], cancellationToken);

            IsInitialized = true;
            StatusMessage = localization["Ready"];
            if (Settings.StartCoreOnLaunch)
                await StartCoreAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
            NotifyComputedState();
            initializationGate.Release();
        }
    }

    public async Task SelectProfileAsync(Profile? profile, CancellationToken cancellationToken = default)
    {
        if (profile is null)
            return;

        suppressProfileLoad = true;
        SelectedProfile = profile;
        suppressProfileLoad = false;
        var storedConfiguration = await profileRepository.ReadConfigurationAsync(profile, cancellationToken);
        var selectedConfiguration = configurationDrafts.SwitchTo(
            profile.Id,
            storedConfiguration,
            ConfigurationText,
            IsConfigurationDirty);
        SetConfigurationText(
            selectedConfiguration.Configuration,
            selectedConfiguration.IsDirty);
        Settings = Settings with { SelectedProfileId = profile.Id };
        await settingsStore.SaveAsync(Settings, cancellationToken);
        NotifyComputedState();
    }

    public Task StartCoreAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var profile = SelectedProfile
                ?? throw new InvalidOperationException(localization["NoProfile"]);
            await SaveAndValidateSelectedAsync(cancellationToken);
            await singBox.StartAsync(profileRepository.GetConfigurationPath(profile), cancellationToken);
            StartTrafficMonitor();
            StatusMessage = localization["CoreOnline"];
        });
    }

    public Task StopCoreAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            StopTrafficMonitor();
            await singBox.StopAsync(cancellationToken);
            StatusMessage = localization["CoreOffline"];
        });
    }

    public Task RestartCoreAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var profile = SelectedProfile
                ?? throw new InvalidOperationException(localization["NoProfile"]);
            await SaveAndValidateSelectedAsync(cancellationToken);
            StopTrafficMonitor();
            await singBox.RestartAsync(profileRepository.GetConfigurationPath(profile), cancellationToken);
            StartTrafficMonitor();
            StatusMessage = localization["CoreOnline"];
        });
    }

    public Task SaveConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            await SaveAndValidateSelectedAsync(cancellationToken);
            StatusMessage = IsCoreRunning
                ? localization["RestartToApply"]
                : localization["Ready"];
        });
    }

    public Task FormatConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(() =>
        {
            SetConfigurationText(configService.FormatJson(ConfigurationText), true);
            StatusMessage = localization["Ready"];
            return Task.CompletedTask;
        });
    }

    public Task ValidateConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var result = await configService.ValidateAsync(ConfigurationText, cancellationToken);
            StatusMessage = result.IsSuccess
                ? $"✓ {localization["ConfigurationValid"]}"
                : result.CombinedOutput;
            if (!result.IsSuccess)
                throw new InvalidDataException(result.CombinedOutput);
        });
    }

    public Task<ProfileImportOutcome?> ImportSubscriptionAsync(
        string name,
        string url,
        CancellationToken cancellationToken = default)
    {
        ProfileImportOutcome? outcome = null;
        return RunBusyWithResultAsync(async () =>
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var subscriptionUrl))
                throw new ArgumentException(localization["InvalidSubscriptionUrl"], nameof(url));

            outcome = await profileImporter.ImportSubscriptionAsync(
                string.IsNullOrWhiteSpace(name) ? localization["Subscription"] : name.Trim(),
                subscriptionUrl,
                Settings,
                cancellationToken);
            await ReloadProfilesAsync(cancellationToken);
            var imported = Profiles.First(profile => profile.Id == outcome.Profile.Id);
            await SelectProfileAsync(imported, cancellationToken);
            StatusMessage = outcome.Warnings.Count == 0
                ? $"✓ {outcome.Profile.NodeCount} {localization["Nodes"]}"
                : $"✓ {outcome.Profile.NodeCount} {localization["Nodes"]} · "
                  + $"{outcome.Warnings.Count} {localization["Warnings"]}";
            return outcome;
        }, () => outcome);
    }

    public Task<ProfileImportOutcome?> RefreshSelectedSubscriptionAsync(
        CancellationToken cancellationToken = default)
    {
        ProfileImportOutcome? outcome = null;
        return RunBusyWithResultAsync(async () =>
        {
            var profile = SelectedProfile
                ?? throw new InvalidOperationException(localization["NoProfile"]);
            if (IsConfigurationDirty)
                throw new InvalidOperationException(localization["SaveChangesBeforeUpdate"]);
            var restart = IsCoreRunning;

            outcome = await profileImporter.UpdateSubscriptionAsync(profile, Settings, cancellationToken);
            await ReloadProfilesAsync(cancellationToken);
            var updated = Profiles.First(item => item.Id == profile.Id);
            await SelectProfileAsync(updated, cancellationToken);

            if (restart)
            {
                StopTrafficMonitor();
                await singBox.RestartAsync(
                    profileRepository.GetConfigurationPath(updated),
                    cancellationToken);
                StartTrafficMonitor();
            }
            StatusMessage = outcome.NotModified
                ? $"✓ {localization["SubscriptionCurrent"]}"
                : $"✓ {updated.NodeCount} {localization["Nodes"]}";
            return outcome;
        }, () => outcome);
    }

    public Task CreateBlankProfileAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var configuration = configService.Serialize(configService.CreateStarterConfiguration(Settings));
            var profile = await profileRepository.CreateAsync(
                string.IsNullOrWhiteSpace(name) ? localization["ManualProfile"] : name.Trim(),
                configuration,
                ProfileSource.Manual,
                cancellationToken);
            await ReloadProfilesAsync(cancellationToken);
            await SelectProfileAsync(Profiles.First(item => item.Id == profile.Id), cancellationToken);
            StatusMessage = localization["Ready"];
        });
    }

    public Task DeleteSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var profile = SelectedProfile;
            if (profile is null)
                return;
            if (IsCoreRunning)
                await singBox.StopAsync(cancellationToken);

            await profileRepository.DeleteAsync(profile.Id, cancellationToken);
            configurationDrafts.Remove(profile.Id);
            await ReloadProfilesAsync(cancellationToken);
            if (Profiles.Count == 0)
            {
                var configuration = configService.Serialize(configService.CreateStarterConfiguration(Settings));
                var replacement = await profileRepository.CreateAsync(
                    localization["ManualProfile"],
                    configuration,
                    ProfileSource.Manual,
                    cancellationToken);
                await ReloadProfilesAsync(cancellationToken);
                await SelectProfileAsync(Profiles.First(item => item.Id == replacement.Id), cancellationToken);
            }
            else
            {
                await SelectProfileAsync(Profiles[0], cancellationToken);
            }
        });
    }

    public Task SaveSettingsAsync(AppSettings updated, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        return RunBusyAsync(async () =>
        {
            var restart = IsCoreRunning;
            if (restart)
                await singBox.StopAsync(cancellationToken);

            Settings = updated with { SelectedProfileId = SelectedProfile?.Id };
            themes.Apply(Settings.Theme);
            localization.Apply(Settings.Language);
            await settingsStore.SaveAsync(Settings, cancellationToken);

            if (SelectedProfile is not null && !string.IsNullOrWhiteSpace(ConfigurationText))
            {
                var parsed = configService.Parse(ConfigurationText);
                var updatedConfiguration = configService.Serialize(
                    configService.ApplyRuntimeOptions(parsed, Settings));
                await profileRepository.WriteConfigurationAsync(
                    SelectedProfile,
                    updatedConfiguration,
                    cancellationToken);
                SetConfigurationText(updatedConfiguration, false);
                configurationDrafts.MarkSaved(SelectedProfile.Id);
            }

            var version = await singBox.InitializeAsync(Settings.SingBoxPath, cancellationToken);
            CoreVersion = version.Version;
            CorePlatform = version.Platform;
            if (restart && SelectedProfile is not null)
                await singBox.StartAsync(profileRepository.GetConfigurationPath(SelectedProfile), cancellationToken);

            StatusMessage = localization["Ready"];
        });
    }

    public async Task<CommandResult> RunToolAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        NotifyComputedState();
        try
        {
            var result = await singBox.RunToolAsync(command, cancellationToken);
            StatusMessage = result.IsSuccess
                ? $"✓ {localization["ExitSuccess"]}"
                : string.Format(localization["ExitCode"], result.ExitCode);
            return result;
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            throw;
        }
        finally
        {
            IsBusy = false;
            NotifyComputedState();
        }
    }

    public void ClearLogs()
    {
        while (pendingLogs.TryDequeue(out _))
        {
        }
        Logs.Clear();
        OnPropertyChanged(nameof(HasLogs));
    }

    public void OpenDataDirectory()
    {
        paths.EnsureCreated();
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", paths.RootDirectory)
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", paths.RootDirectory)
                : new ProcessStartInfo("xdg-open", paths.RootDirectory);
        startInfo.UseShellExecute = false;
        Process.Start(startInfo)?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;

        logFlushTimer.Stop();
        StopTrafficMonitor();
        singBox.LogReceived -= OnLogReceived;
        singBox.StateChanged -= OnCoreStateChanged;
        localization.LanguageChanged -= OnLanguageChanged;
        initializationGate.Dispose();
        await Task.CompletedTask;
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (!suppressProfileLoad && value is not null && IsInitialized)
            _ = SelectProfileSafelyAsync(value);
        NotifyComputedState();
    }

    partial void OnConfigurationTextChanged(string value)
    {
        if (!suppressConfigurationDirty && configurationDrafts.ActiveProfileId is not null)
            IsConfigurationDirty = true;
    }

    partial void OnIsConfigurationDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefreshSubscription));
    }

    partial void OnCoreStateChanged(CoreState value)
    {
        OnPropertyChanged(nameof(IsCoreRunning));
        OnPropertyChanged(nameof(CanStartCore));
        OnPropertyChanged(nameof(CanStopCore));
        OnPropertyChanged(nameof(CanModifyProfiles));
        OnPropertyChanged(nameof(CoreStateDisplay));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartCore));
        OnPropertyChanged(nameof(CanStopCore));
        OnPropertyChanged(nameof(CanModifyProfiles));
        OnPropertyChanged(nameof(CanRefreshSubscription));
    }

    partial void OnUploadBytesPerSecondChanged(long value)
    {
        OnPropertyChanged(nameof(UploadRate));
    }

    partial void OnDownloadBytesPerSecondChanged(long value)
    {
        OnPropertyChanged(nameof(DownloadRate));
    }

    private async Task SaveAndValidateSelectedAsync(CancellationToken cancellationToken)
    {
        var profile = SelectedProfile
            ?? throw new InvalidOperationException(localization["NoProfile"]);
        var formatted = configService.FormatJson(ConfigurationText);
        var validation = await configService.ValidateAsync(formatted, cancellationToken);
        if (!validation.IsSuccess)
            throw new InvalidDataException(validation.CombinedOutput);

        await profileRepository.WriteConfigurationAsync(profile, formatted, cancellationToken);
        var updated = profile with
        {
            LastValidationMessage = validation.CombinedOutput,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await profileRepository.UpdateAsync(updated, cancellationToken);
        SetConfigurationText(formatted, false);
        configurationDrafts.MarkSaved(profile.Id);
    }

    private async Task ReloadProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await profileRepository.GetAllAsync(cancellationToken);
        Profiles.Clear();
        foreach (var profile in profiles)
            Profiles.Add(profile);
        OnPropertyChanged(nameof(ActiveNodeCount));
    }

    private async Task SelectProfileSafelyAsync(Profile profile)
    {
        try
        {
            await SelectProfileAsync(profile);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private void SetConfigurationText(string value, bool isDirty)
    {
        suppressConfigurationDirty = true;
        ConfigurationText = value;
        suppressConfigurationDirty = false;
        IsConfigurationDirty = isDirty;
    }

    private Task RunBusyAsync(Func<Task> operation)
    {
        return RunBusyWithResultAsync<object?>(async () =>
        {
            await operation();
            return null;
        }, static () => null);
    }

    private async Task<T?> RunBusyWithResultAsync<T>(Func<Task<T>> operation, Func<T?> fallback)
    {
        IsBusy = true;
        StatusMessage = localization["Working"];
        NotifyComputedState();
        try
        {
            return await operation();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            return fallback();
        }
        finally
        {
            IsBusy = false;
            NotifyComputedState();
        }
    }

    private void OnLogReceived(CoreLogEntry entry)
    {
        pendingLogs.Enqueue(entry);
    }

    private void OnCoreStateChanged(CoreStateChangedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CoreState = eventArgs.Current;
            if (!string.IsNullOrWhiteSpace(eventArgs.Error))
                StatusMessage = eventArgs.Error;
            if (eventArgs.Current is CoreState.Stopped or CoreState.Faulted)
                StopTrafficMonitor();
        });
    }

    private void FlushLogs(object? sender, EventArgs eventArgs)
    {
        var maximum = Math.Clamp(Settings.MaximumLogEntries, 200, 20_000);
        var drained = 0;
        while (drained < 200 && pendingLogs.TryDequeue(out var entry))
        {
            Logs.Add(entry);
            drained++;
        }

        var removeCount = Logs.Count - maximum;
        while (removeCount-- > 0)
            Logs.RemoveAt(0);
        if (drained > 0)
            OnPropertyChanged(nameof(HasLogs));
    }

    private void StartTrafficMonitor()
    {
        StopTrafficMonitor();
        trafficCancellation = new CancellationTokenSource();
        var cancellationToken = trafficCancellation.Token;

        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var client = new ClashApiClient(Settings.ClashApiPort, Settings.ClashApiSecret);
                    var progress = new Progress<TrafficSnapshot>(snapshot => Dispatcher.UIThread.Post(() =>
                    {
                        UploadBytesPerSecond = snapshot.UploadBytesPerSecond;
                        DownloadBytesPerSecond = snapshot.DownloadBytesPerSecond;
                    }));
                    await client.StreamTrafficAsync(progress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }, cancellationToken);
    }

    private void StopTrafficMonitor()
    {
        trafficCancellation?.Cancel();
        trafficCancellation?.Dispose();
        trafficCancellation = null;
        UploadBytesPerSecond = 0;
        DownloadBytesPerSecond = 0;
    }

    private void NotifyComputedState()
    {
        OnPropertyChanged(nameof(CanStartCore));
        OnPropertyChanged(nameof(CanStopCore));
        OnPropertyChanged(nameof(CanModifyProfiles));
        OnPropertyChanged(nameof(CanRefreshSubscription));
        OnPropertyChanged(nameof(IsCoreRunning));
        OnPropertyChanged(nameof(ActiveNodeCount));
        OnPropertyChanged(nameof(ActiveNodeDisplay));
        OnPropertyChanged(nameof(CoreStateDisplay));
        OnPropertyChanged(nameof(TunDisplay));
    }

    private void OnLanguageChanged()
    {
        NotifyComputedState();
    }

    private static string FormatRate(long bytesPerSecond)
    {
        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s"];
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.#} {units[unit]}";
    }
}
