using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using BoxPilot.App.Services;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BoxPilot.App.ViewModels;

public partial class AppSessionViewModel : ViewModelBase, IAsyncDisposable
{
    // sing-box publishes one traffic sample per second; one minute keeps redraw and memory bounded.
    private const int MaximumTrafficSamples = 60;
    private static readonly string[] TrafficRateUnits = ["B/s", "KiB/s", "MiB/s", "GiB/s"];
    private static readonly string[] TrafficTotalUnits = ["B", "KiB", "MiB", "GiB", "TiB"];
    private static readonly TimeSpan TrafficReconnectDelay = TimeSpan.FromSeconds(2);
    // /connections includes every active connection, so use it only to correct locally accumulated totals.
    private static readonly TimeSpan TrafficTotalsInterval = TimeSpan.FromSeconds(30);
    private readonly AppPaths paths;
    private readonly SettingsStore settingsStore;
    private readonly ProfileRepository profileRepository;
    private readonly SingBoxService singBox;
    private readonly SingBoxConfigService configService;
    private readonly ProfileImportService profileImporter;
    private readonly CustomRoutingService customRoutingService;
    private readonly ConfigurationFileService configurationFileService;
    private readonly LocalizationService localization;
    private readonly ConcurrentQueue<CoreLogEntry> pendingLogs = new();
    private readonly ConfigurationDraftStore configurationDrafts = new();
    private readonly DispatcherTimer logFlushTimer;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private CancellationTokenSource? trafficCancellation;
    private long uploadBytesPerSecond;
    private long downloadBytesPerSecond;
    private long uploadBytesTotal;
    private long downloadBytesTotal;
    private bool suppressConfigurationDirty;
    private bool suppressProfileLoad;
    private ClashApiConnection? clashApiConnection;
    private bool supportsStandardRoutingModes;
    private bool canAddStandardRoutingModes;
    private bool initialized;
    private bool disposed;

    public AppSessionViewModel(
        AppPaths paths,
        SettingsStore settingsStore,
        ProfileRepository profileRepository,
        SingBoxService singBox,
        SingBoxConfigService configService,
        ProfileImportService profileImporter,
        CustomRoutingService customRoutingService,
        ConfigurationFileService configurationFileService,
        LocalizationService localization)
    {
        this.paths = paths;
        this.settingsStore = settingsStore;
        this.profileRepository = profileRepository;
        this.singBox = singBox;
        this.configService = configService;
        this.profileImporter = profileImporter;
        this.customRoutingService = customRoutingService;
        this.configurationFileService = configurationFileService;
        this.localization = localization;

        singBox.LogReceived += OnLogReceived;
        singBox.StateChanged += OnCoreStateChanged;
        localization.LanguageChanged += OnLanguageChanged;
        logFlushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, FlushLogs);
        logFlushTimer.Start();
    }

    public ObservableCollection<Profile> Profiles { get; } = [];

    public ObservableCollection<CoreLogEntry> Logs { get; } = [];

    public ObservableCollection<CustomRuleSet> CustomRuleSets { get; } = [];

    public ObservableCollection<RoutingOutbound> RoutingOutbounds { get; } = [];

    public ObservableCollection<TrafficSnapshot> TrafficHistory { get; } = [];

    public ToastViewModel Toast { get; } = new();

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
    public partial bool IsBusy { get; private set; }

    public bool IsCoreRunning => CoreState == CoreState.Running;

    public bool CanStartCore => !IsBusy
                                && SelectedProfile is not null
                                && CoreState is CoreState.Stopped or CoreState.Faulted;

    public bool CanStopCore => !IsBusy && CoreState is CoreState.Running or CoreState.Starting;

    public bool CanModifyProfiles => !IsBusy && !IsCoreRunning;

    public bool CanDeleteProfile => CanModifyProfiles && SelectedProfile is not null;

    public bool CanExportConfiguration => !IsBusy && SelectedProfile is not null;

    public bool CanRefreshSubscription => !IsBusy
                                          && !IsConfigurationDirty
                                          && SelectedProfile?.Source == ProfileSource.Subscription
                                          && !string.IsNullOrWhiteSpace(SelectedProfile.SubscriptionUrl);

    public bool CanManageRuleSets => !IsBusy
                                     && !IsConfigurationDirty
                                     && SelectedProfile is not null;

    public bool HasClashApi => clashApiConnection is not null;

    public string? GlobalProxyGroup { get; private set; }

    public bool CanChangeRoutingMode => !IsBusy && HasClashApi && supportsStandardRoutingModes;

    public bool RequiresStandardRoutingModeConsent =>
        SelectedProfile is
        {
            Source: ProfileSource.Subscription,
            ManageStandardRoutingModes: null,
        }
        && !supportsStandardRoutingModes
        && canAddStandardRoutingModes;

    public string UploadRate => FormatRate(uploadBytesPerSecond);

    public string DownloadRate => FormatRate(downloadBytesPerSecond);

    public string UploadTotal => FormatBytes(uploadBytesTotal);

    public string DownloadTotal => FormatBytes(downloadBytesTotal);

    public string ActiveNodeDisplay => $"{SelectedProfile?.NodeCount ?? 0} {localization["Nodes"]}";

    public string CoreStateDisplay => localization[CoreState.ToString()];

    public string TunDisplay => localization[Settings.EnableTun ? "Enabled" : "Disabled"];

    public bool IsTunServiceInstalled => singBox.IsCoreServiceInstalled;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
                return;

            IsBusy = true;
            Settings = await settingsStore.LoadAsync(cancellationToken);
            localization.Apply(Settings.Language);
            ThemeService.Apply(Settings.Theme);
            try
            {
                var version = await singBox.InitializeAsync(Settings.SingBoxPath, cancellationToken);
                CoreVersion = version;
            }
            catch (FileNotFoundException)
            {
                CoreVersion = localization["CoreNotFound"];
                Toast.Show(localization["CoreNotFoundHelp"], ToastLevel.Error);
            }
            catch (Exception exception)
            {
                CoreVersion = localization["CoreNotFound"];
                Toast.Show(DescribeError(exception), ToastLevel.Error);
            }

            await ReloadProfilesAsync(cancellationToken);
            var selected = Settings.SelectedProfileId is { } selectedId
                ? Profiles.FirstOrDefault(profile => profile.Id == selectedId)
                : null;
            selected ??= Profiles.FirstOrDefault();
            if (selected is not null)
                await SelectProfileAsync(selected, cancellationToken);
            else
                await ClearProfileSelectionAsync(cancellationToken);

            initialized = true;
            if (Settings.StartCoreOnLaunch)
                await StartCoreAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
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
        storedConfiguration = await NormalizeSubscriptionConfigurationAsync(
            profile,
            storedConfiguration,
            cancellationToken);
        var selectedConfiguration = configurationDrafts.SwitchTo(
            profile.Id,
            storedConfiguration,
            ConfigurationText,
            IsConfigurationDirty);
        SetConfigurationText(
            selectedConfiguration.Configuration,
            selectedConfiguration.IsDirty);
        await ReloadCustomRoutingAsync(profile, cancellationToken);
        Settings = Settings with { SelectedProfileId = profile.Id };
        await settingsStore.SaveAsync(Settings, cancellationToken);
    }

    public Task StartCoreAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var profile = SelectedProfile
                ?? throw new InvalidOperationException(localization["NoProfile"]);
            await SaveAndValidateSelectedAsync(cancellationToken);
            await singBox.StartAsync(
                paths.GetProfileConfigPath(profile),
                profile.WorkingDirectory,
                cancellationToken);
            if (HasClashApi)
            {
                if (supportsStandardRoutingModes)
                    await ApplyRoutingModeToCoreAsync(cancellationToken);
                StartTrafficMonitor();
            }
        });
    }

    public Task StopCoreAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            StopTrafficMonitor();
            await singBox.StopAsync(cancellationToken);
        });
    }

    public Task RestartCoreAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var profile = SelectedProfile
                ?? throw new InvalidOperationException(localization["NoProfile"]);
            await SaveAndValidateSelectedAsync(cancellationToken);
            await RestartCoreForProfileAsync(profile, cancellationToken);
        });
    }

    public Task SaveConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            await SaveAndValidateSelectedAsync(cancellationToken);
            var message = IsCoreRunning
                ? localization["RestartToApply"]
                : localization["ChangesSaved"];
            Toast.Show(
                message,
                IsCoreRunning ? ToastLevel.Warning : ToastLevel.Success);
        });
    }

    public Task FormatConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(() =>
        {
            SetConfigurationText(configService.FormatJson(ConfigurationText), true);
            return Task.CompletedTask;
        });
    }

    public Task ValidateConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var result = await configService.ValidateAsync(
                ConfigurationText,
                SelectedProfile?.WorkingDirectory,
                cancellationToken);
            var message = result.IsSuccess
                ? $"✓ {localization["ConfigurationValid"]}"
                : result.CombinedOutput;
            if (!result.IsSuccess)
                throw new InvalidDataException(result.CombinedOutput);
            Toast.Show(message, ToastLevel.Success);
        });
    }

    public Task<ProfileImportOutcome?> ImportSubscriptionAsync(
        string name,
        string url,
        CancellationToken cancellationToken = default)
    {
        return RunBusyWithResultAsync(async () =>
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var subscriptionUrl))
                throw new ArgumentException(localization["InvalidSubscriptionUrl"], nameof(url));
            var restart = IsCoreRunning;

            var outcome = await profileImporter.ImportSubscriptionAsync(
                string.IsNullOrWhiteSpace(name) ? localization["Subscription"] : name.Trim(),
                subscriptionUrl,
                Settings,
                cancellationToken);
            await ReloadProfilesAsync(cancellationToken);
            var imported = Profiles.First(profile => profile.Id == outcome.Profile.Id);
            await SelectProfileAsync(imported, cancellationToken);
            if (restart)
                await RestartCoreForProfileAsync(imported, cancellationToken);
            var message = outcome.Warnings.Count == 0
                ? $"✓ {outcome.Profile.NodeCount} {localization["Nodes"]}"
                : $"✓ {outcome.Profile.NodeCount} {localization["Nodes"]} · "
                  + $"{outcome.Warnings.Count} {localization["Warnings"]}";
            ShowImportOutcomeToast(outcome, message);
            return outcome;
        });
    }

    public Task<Profile?> ImportConfigurationFileAsync(
        string name,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        return RunBusyWithResultAsync(async () =>
        {
            var profile = await configurationFileService.ImportAsync(
                name,
                paths,
                cancellationToken);
            await ReloadProfilesAsync(cancellationToken);
            var imported = Profiles.First(item => item.Id == profile.Id);
            await SelectProfileAsync(imported, cancellationToken);
            Toast.Show(localization["ConfigurationImported"], ToastLevel.Success);
            return imported;
        });
    }

    public Task<bool> EnableStandardRoutingModesAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyWithResultAsync(async () =>
        {
            if (!RequiresStandardRoutingModeConsent)
                return false;
            var profile = SelectedProfile
                ?? throw new InvalidOperationException(localization["NoProfile"]);
            if (IsConfigurationDirty)
                throw new InvalidOperationException(localization["SaveChangesBeforeRoutingModes"]);

            var configuration = configService.Serialize(
                configService.EnsureManagedStandardRoutingModes(
                    configService.Parse(ConfigurationText)));
            await configService.ValidateOrThrowAsync(
                configuration,
                profile.WorkingDirectory,
                cancellationToken);

            await profileRepository.WriteConfigurationAsync(
                profile,
                configuration,
                cancellationToken);
            await UpdateSelectedProfileAsync(
                profile with { ManageStandardRoutingModes = true },
                cancellationToken);
            SetConfigurationText(configuration, false);
            configurationDrafts.MarkSaved(profile.Id);
            await ReloadCustomRoutingAsync(SelectedProfile!, cancellationToken);
            if (IsCoreRunning)
                await RestartCoreForProfileAsync(SelectedProfile!, cancellationToken);
            Toast.Show(localization["StandardRoutingModesAdded"], ToastLevel.Success);
            return true;
        });
    }

    public Task<bool> KeepSubscriptionRoutingAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyWithResultAsync(async () =>
        {
            if (!RequiresStandardRoutingModeConsent)
                return false;
            var profile = SelectedProfile
                ?? throw new InvalidOperationException(localization["NoProfile"]);

            await UpdateSelectedProfileAsync(
                profile with { ManageStandardRoutingModes = false },
                cancellationToken);
            return true;
        });
    }

    public Task ExportSelectedConfigurationAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            if (SelectedProfile is null)
                throw new InvalidOperationException(localization["NoProfile"]);
            await configurationFileService.ExportAsync(
                ConfigurationText,
                path,
                cancellationToken);
            Toast.Show(localization["ConfigurationExported"], ToastLevel.Success);
        });
    }

    public Task<ProfileImportOutcome?> RefreshSelectedSubscriptionAsync(
        CancellationToken cancellationToken = default)
    {
        return RunBusyWithResultAsync(async () =>
        {
            var profile = SelectedProfile
                ?? throw new InvalidOperationException(localization["NoProfile"]);
            if (IsConfigurationDirty)
                throw new InvalidOperationException(localization["SaveChangesBeforeUpdate"]);
            var restart = IsCoreRunning;

            var outcome = await profileImporter.UpdateSubscriptionAsync(profile, Settings, cancellationToken);
            await ReloadProfilesAsync(cancellationToken);
            var updated = Profiles.First(item => item.Id == profile.Id);
            await SelectProfileAsync(updated, cancellationToken);

            if (restart && !outcome.NotModified)
                await RestartCoreForProfileAsync(updated, cancellationToken);
            var message = outcome.NotModified
                ? $"✓ {localization["SubscriptionCurrent"]}"
                : $"✓ {updated.NodeCount} {localization["Nodes"]}";
            ShowImportOutcomeToast(outcome, message);
            return outcome;
        });
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
            await customRoutingService.DeleteProfileAsync(profile.Id, cancellationToken);
            configurationDrafts.Remove(profile.Id);
            await ReloadProfilesAsync(cancellationToken);
            if (Profiles.Count == 0)
            {
                await ClearProfileSelectionAsync(cancellationToken);
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
            string? updatedConfiguration = null;
            if (SelectedProfile is { } selectedProfile
                && !string.IsNullOrWhiteSpace(ConfigurationText))
            {
                updatedConfiguration = await PrepareProfileConfigurationAsync(
                    selectedProfile,
                    ConfigurationText,
                    updated,
                    cancellationToken);
                await configService.ValidateOrThrowAsync(
                    updatedConfiguration,
                    selectedProfile.WorkingDirectory,
                    cancellationToken);
            }

            if (restart)
                await singBox.StopAsync(cancellationToken);

            Settings = updated with { SelectedProfileId = SelectedProfile?.Id };
            ThemeService.Apply(Settings.Theme);
            localization.Apply(Settings.Language);
            await settingsStore.SaveAsync(Settings, cancellationToken);

            if (SelectedProfile is not null && updatedConfiguration is not null)
            {
                await profileRepository.WriteConfigurationAsync(
                    SelectedProfile,
                    updatedConfiguration,
                    cancellationToken);
                SetConfigurationText(updatedConfiguration, false);
                configurationDrafts.MarkSaved(SelectedProfile.Id);
            }

            var version = await singBox.InitializeAsync(Settings.SingBoxPath, cancellationToken);
            CoreVersion = version;
            if (restart && SelectedProfile is not null)
            {
                await singBox.StartAsync(
                    paths.GetProfileConfigPath(SelectedProfile),
                    SelectedProfile.WorkingDirectory,
                    cancellationToken);
                if (supportsStandardRoutingModes)
                    await ApplyRoutingModeToCoreAsync(cancellationToken);
            }

            Toast.Show(localization["SettingsSaved"], ToastLevel.Success);
        });
    }

    public Task ImportRuleSetAsync(
        string path,
        string outbound,
        CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var profile = GetRuleSetProfile();
            var restart = IsCoreRunning;
            var change = await customRoutingService.ImportAsync(
                profile,
                path,
                outbound,
                ConfigurationText,
                cancellationToken);
            await ApplyRuleSetConfigurationAsync(
                profile,
                change.Configuration,
                restart,
                "RuleSetImported",
                cancellationToken);
        });
    }

    public Task RemoveRuleSetAsync(
        CustomRuleSet ruleSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        return RunBusyAsync(async () =>
        {
            var profile = GetRuleSetProfile();
            var restart = IsCoreRunning;
            var configuration = await customRoutingService.RemoveAsync(
                profile,
                ruleSet.Id,
                ConfigurationText,
                cancellationToken);
            await ApplyRuleSetConfigurationAsync(
                profile,
                configuration,
                restart,
                "RuleSetRemoved",
                cancellationToken);
        });
    }

    public Task AddRemoteRuleSetAsync(
        string url,
        string outbound,
        CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var profile = GetRuleSetProfile();
            var restart = IsCoreRunning;
            var change = await customRoutingService.AddRemoteAsync(
                profile,
                url,
                outbound,
                ConfigurationText,
                cancellationToken);
            await ApplyRuleSetConfigurationAsync(
                profile,
                change.Configuration,
                restart,
                "RuleSetImported",
                cancellationToken);
        });
    }

    public Task SetRoutingModeAsync(
        string mode,
        CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            var normalized = mode?.ToLowerInvariant() switch
            {
                "global" => "Global",
                "direct" => "Direct",
                _ => "Rule",
            };
            var updatedSettings = Settings with { RoutingMode = normalized };

            if (SelectedProfile is { } profile && !string.IsNullOrWhiteSpace(ConfigurationText))
            {
                var updatedConfiguration = await PrepareProfileConfigurationAsync(
                    profile,
                    ConfigurationText,
                    updatedSettings,
                    cancellationToken);
                await configService.ValidateOrThrowAsync(
                    updatedConfiguration,
                    profile.WorkingDirectory,
                    cancellationToken);
                await profileRepository.WriteConfigurationAsync(
                    profile,
                    updatedConfiguration,
                    cancellationToken);
                SetConfigurationText(updatedConfiguration, false);
                configurationDrafts.MarkSaved(profile.Id);
            }

            Settings = updatedSettings;
            await settingsStore.SaveAsync(Settings, cancellationToken);

            if (IsCoreRunning && HasClashApi)
            {
                using var client = CreateClashApiClient();
                await client.SetModeAsync(normalized, cancellationToken);
            }

            Toast.Show(localization["RoutingModeChanged"], ToastLevel.Success);
        });
    }

    public async Task<CommandResult> RunToolAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            return await singBox.RunToolAsync(
                command,
                SelectedProfile?.WorkingDirectory,
                cancellationToken);
        }
        catch (Exception exception)
        {
            Toast.Show(DescribeError(exception), ToastLevel.Error);
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public ClashApiClient CreateClashApiClient()
    {
        var connection = clashApiConnection
                         ?? throw new InvalidOperationException(localization["ClashApiUnavailable"]);
        return new ClashApiClient(connection.Port, connection.Secret);
    }

    public void ClearLogs()
    {
        while (pendingLogs.TryDequeue(out _))
        {
        }
        Logs.Clear();
    }

    public void OpenDataDirectory()
    {
        paths.EnsureCreated();
        Process.Start(CreateOpenDataDirectoryStartInfo(paths.RootDirectory))?.Dispose();
    }

    internal static ProcessStartInfo CreateOpenDataDirectoryStartInfo(string directory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? "explorer.exe"
                : OperatingSystem.IsMacOS()
                    ? "open"
                    : "xdg-open",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(directory);
        return startInfo;
    }

    public Task UninstallTunServiceAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async () =>
        {
            await singBox.UninstallCoreServiceAsync(cancellationToken);
            Toast.Show(localization["TunServiceRemoved"], ToastLevel.Success);
            OnPropertyChanged(nameof(IsTunServiceInstalled));
        });
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
            return ValueTask.CompletedTask;
        disposed = true;

        logFlushTimer.Stop();
        Toast.Dispose();
        StopTrafficMonitor();
        singBox.LogReceived -= OnLogReceived;
        singBox.StateChanged -= OnCoreStateChanged;
        localization.LanguageChanged -= OnLanguageChanged;
        initializationGate.Dispose();
        return ValueTask.CompletedTask;
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (!suppressProfileLoad && value is not null && initialized)
            _ = SelectProfileSafelyAsync(value);
        OnPropertyChanged(nameof(CanStartCore));
        OnPropertyChanged(nameof(CanDeleteProfile));
        OnPropertyChanged(nameof(CanExportConfiguration));
        OnPropertyChanged(nameof(CanRefreshSubscription));
        OnPropertyChanged(nameof(ActiveNodeDisplay));
        OnPropertyChanged(nameof(RequiresStandardRoutingModeConsent));
    }

    partial void OnSettingsChanged(AppSettings value)
    {
        OnPropertyChanged(nameof(TunDisplay));
    }

    partial void OnConfigurationTextChanged(string value)
    {
        if (!suppressConfigurationDirty && configurationDrafts.ActiveProfileId is not null)
            IsConfigurationDirty = true;
    }

    partial void OnIsConfigurationDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefreshSubscription));
        OnPropertyChanged(nameof(CanManageRuleSets));
    }

    partial void OnCoreStateChanged(CoreState value)
    {
        OnPropertyChanged(nameof(IsCoreRunning));
        OnPropertyChanged(nameof(CanStartCore));
        OnPropertyChanged(nameof(CanStopCore));
        OnPropertyChanged(nameof(CanModifyProfiles));
        OnPropertyChanged(nameof(CanDeleteProfile));
        OnPropertyChanged(nameof(CanExportConfiguration));
        OnPropertyChanged(nameof(CoreStateDisplay));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartCore));
        OnPropertyChanged(nameof(CanStopCore));
        OnPropertyChanged(nameof(CanModifyProfiles));
        OnPropertyChanged(nameof(CanDeleteProfile));
        OnPropertyChanged(nameof(CanExportConfiguration));
        OnPropertyChanged(nameof(CanRefreshSubscription));
        OnPropertyChanged(nameof(CanManageRuleSets));
        OnPropertyChanged(nameof(CanChangeRoutingMode));
    }

    private async Task SaveAndValidateSelectedAsync(CancellationToken cancellationToken)
    {
        var profile = SelectedProfile
            ?? throw new InvalidOperationException(localization["NoProfile"]);
        var formatted = await PrepareProfileConfigurationAsync(
            profile,
            ConfigurationText,
            Settings,
            cancellationToken);
        await configService.ValidateOrThrowAsync(
            formatted,
            profile.WorkingDirectory,
            cancellationToken);

        await profileRepository.WriteConfigurationAsync(profile, formatted, cancellationToken);
        await profileRepository.UpdateAsync(profile, cancellationToken);
        SetConfigurationText(formatted, false);
        configurationDrafts.MarkSaved(profile.Id);
    }

    private async Task ApplyRuleSetConfigurationAsync(
        Profile profile,
        string configuration,
        bool restart,
        string successMessageKey,
        CancellationToken cancellationToken)
    {
        await profileRepository.WriteConfigurationAsync(profile, configuration, cancellationToken);
        SetConfigurationText(configuration, false);
        configurationDrafts.MarkSaved(profile.Id);
        await ReloadCustomRoutingAsync(profile, cancellationToken);
        if (restart)
            await RestartCoreForProfileAsync(profile, cancellationToken);
        Toast.Show(localization[successMessageKey], ToastLevel.Success);
    }

    private Profile GetRuleSetProfile()
    {
        var profile = SelectedProfile
            ?? throw new InvalidOperationException(localization["NoProfile"]);
        if (IsConfigurationDirty)
            throw new InvalidOperationException(localization["SaveChangesBeforeRuleSet"]);
        return profile;
    }

    private async Task ReloadProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await profileRepository.GetAllAsync(cancellationToken);
        Profiles.Clear();
        foreach (var profile in profiles)
            Profiles.Add(profile);
    }

    private async Task<string> PrepareProfileConfigurationAsync(
        Profile profile,
        string configuration,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var parsed = configService.Parse(configuration);
        if (profile.Source != ProfileSource.ImportedFile)
            parsed = configService.ApplyRuntimeOptions(parsed, settings);
        parsed = ApplyConfiguredStandardRoutingModes(profile, parsed);
        return await customRoutingService.ApplyAsync(
            profile.Id,
            configService.Serialize(parsed),
            cancellationToken);
    }

    private async Task ClearProfileSelectionAsync(CancellationToken cancellationToken)
    {
        var clearPersistedSelection = Settings.SelectedProfileId is not null;
        SelectedProfile = null;
        SetConfigurationText(string.Empty, false);
        Settings = Settings with { SelectedProfileId = null };
        if (clearPersistedSelection)
            await settingsStore.SaveAsync(Settings, cancellationToken);
    }

    private async Task SelectProfileSafelyAsync(Profile profile)
    {
        try
        {
            await SelectProfileAsync(profile);
        }
        catch (Exception exception)
        {
            Toast.Show(DescribeError(exception), ToastLevel.Error);
        }
    }

    private void SetConfigurationText(string value, bool isDirty)
    {
        suppressConfigurationDirty = true;
        ConfigurationText = value;
        suppressConfigurationDirty = false;
        IsConfigurationDirty = isDirty;
        RefreshClashApiConnection(value);
    }

    private void RefreshClashApiConnection(string configuration)
    {
        try
        {
            var parsed = string.IsNullOrWhiteSpace(configuration)
                ? null
                : configService.Parse(configuration);
            clashApiConnection = parsed is null
                ? null
                : configService.GetClashApiConnection(parsed);
            supportsStandardRoutingModes = parsed is not null
                                           && configService.SupportsStandardRoutingModes(parsed);
            canAddStandardRoutingModes = parsed is not null
                                         && configService.CanAddStandardRoutingModes(parsed);
            GlobalProxyGroup = parsed is null
                ? null
                : configService.GetGlobalProxyGroup(parsed);
        }
        catch (InvalidDataException)
        {
            clashApiConnection = null;
            supportsStandardRoutingModes = false;
            canAddStandardRoutingModes = false;
            GlobalProxyGroup = null;
        }
        OnPropertyChanged(nameof(HasClashApi));
        OnPropertyChanged(nameof(CanChangeRoutingMode));
        OnPropertyChanged(nameof(RequiresStandardRoutingModeConsent));
        OnPropertyChanged(nameof(GlobalProxyGroup));
    }

    private async Task<string> NormalizeSubscriptionConfigurationAsync(
        Profile profile,
        string configuration,
        CancellationToken cancellationToken)
    {
        if (profile.Source != ProfileSource.Subscription)
            return await customRoutingService.ApplyAsync(
                profile.Id,
                configuration,
                cancellationToken);

        var shouldPrepare = !string.Equals(
            profile.SubscriptionFormat,
            nameof(SubscriptionFormat.SingBoxJson),
            StringComparison.Ordinal);
        var parsed = configService.Parse(configuration);
        if (shouldPrepare)
        {
            var cacheId = profile.Id.ToString("N");
            if (Uri.TryCreate(profile.SubscriptionUrl, UriKind.Absolute, out var subscriptionUrl))
                cacheId = ProfileImportService.CreateCacheId(subscriptionUrl);
            var preservePolicyGroups = string.Equals(
                profile.SubscriptionFormat,
                nameof(SubscriptionFormat.ClashYaml),
                StringComparison.Ordinal);
            parsed = configService.PrepareManagedSubscription(
                parsed,
                cacheId,
                preservePolicyGroups);
        }
        parsed = configService.ApplyRuntimeOptions(parsed, Settings);
        parsed = ApplyConfiguredStandardRoutingModes(profile, parsed);

        // Subscription profiles are app-owned; manual profile formatting remains untouched.
        var normalized = await customRoutingService.ApplyAsync(
            profile.Id,
            configService.Serialize(parsed),
            cancellationToken);
        if (!string.Equals(normalized, configuration, StringComparison.Ordinal))
        {
            await profileRepository.WriteConfigurationAsync(
                profile,
                normalized,
                cancellationToken);
        }

        return normalized;
    }

    private JsonObject ApplyConfiguredStandardRoutingModes(
        Profile profile,
        JsonObject configuration)
    {
        return profile.ManageStandardRoutingModes == true
            ? configService.EnsureManagedStandardRoutingModes(configuration)
            : configuration;
    }

    private async Task ReloadCustomRoutingAsync(
        Profile profile,
        CancellationToken cancellationToken)
    {
        var ruleSets = await customRoutingService.GetAsync(profile.Id, cancellationToken);
        CustomRuleSets.Clear();
        foreach (var ruleSet in ruleSets)
            CustomRuleSets.Add(ruleSet);

        RoutingOutbounds.Clear();
        foreach (var outbound in customRoutingService.GetRoutingOutbounds(ConfigurationText))
            RoutingOutbounds.Add(outbound);
        OnPropertyChanged(nameof(CanManageRuleSets));
    }

    private async Task UpdateSelectedProfileAsync(
        Profile profile,
        CancellationToken cancellationToken)
    {
        await profileRepository.UpdateAsync(profile, cancellationToken);
        await ReloadProfilesAsync(cancellationToken);
        var updated = Profiles.First(item => item.Id == profile.Id);
        suppressProfileLoad = true;
        SelectedProfile = updated;
        suppressProfileLoad = false;
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        await RunBusyWithResultAsync(async () =>
        {
            await operation();
            return true;
        });
    }

    private async Task RestartCoreForProfileAsync(
        Profile profile,
        CancellationToken cancellationToken)
    {
        StopTrafficMonitor();
        await singBox.RestartAsync(
            paths.GetProfileConfigPath(profile),
            profile.WorkingDirectory,
            cancellationToken);
        if (HasClashApi)
        {
            if (supportsStandardRoutingModes)
                await ApplyRoutingModeToCoreAsync(cancellationToken);
            StartTrafficMonitor();
        }
    }

    private async Task ApplyRoutingModeToCoreAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClashApiClient();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await client.SetModeAsync(Settings.RoutingMode, cancellationToken);
                return;
            }
            catch (HttpRequestException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private async Task<T?> RunBusyWithResultAsync<T>(Func<Task<T>> operation)
    {
        IsBusy = true;
        try
        {
            return await operation();
        }
        catch (Exception exception)
        {
            Toast.Show(DescribeError(exception), ToastLevel.Error);
            return default;
        }
        finally
        {
            IsBusy = false;
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
                Toast.Show(DescribeError(eventArgs.Error), ToastLevel.Error);
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
    }

    private void StartTrafficMonitor()
    {
        StopTrafficMonitor();
        ResetTrafficSession();
        var monitor = new CancellationTokenSource();
        var cancellationToken = monitor.Token;
        trafficCancellation = monitor;
        _ = Task.Run(
            () => MonitorTrafficRatesAsync(monitor, cancellationToken),
            cancellationToken);
        _ = Task.Run(
            () => MonitorTrafficTotalsAsync(monitor, cancellationToken),
            cancellationToken);
    }

    private async Task MonitorTrafficRatesAsync(
        CancellationTokenSource monitor,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<TrafficSnapshot>(snapshot => Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrentTrafficMonitor(monitor, cancellationToken))
                return;

            uploadBytesPerSecond = snapshot.UploadBytesPerSecond;
            downloadBytesPerSecond = snapshot.DownloadBytesPerSecond;
            uploadBytesTotal = AddTrafficBytes(uploadBytesTotal, snapshot.UploadBytesPerSecond);
            downloadBytesTotal = AddTrafficBytes(downloadBytesTotal, snapshot.DownloadBytesPerSecond);
            TrafficHistory.Add(snapshot);
            while (TrafficHistory.Count > MaximumTrafficSamples)
                TrafficHistory.RemoveAt(0);
            OnPropertyChanged(nameof(UploadRate));
            OnPropertyChanged(nameof(DownloadRate));
            OnPropertyChanged(nameof(UploadTotal));
            OnPropertyChanged(nameof(DownloadTotal));
        }));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = CreateClashApiClient();
                await client.StreamTrafficAsync(progress, cancellationToken);
                if (!await DelayTrafficReconnectAsync(cancellationToken))
                    return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (!await DelayTrafficReconnectAsync(cancellationToken))
                    return;
            }
        }
    }

    private async Task MonitorTrafficTotalsAsync(
        CancellationTokenSource monitor,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = CreateClashApiClient();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var totals = await client.GetTrafficTotalsAsync(cancellationToken);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsCurrentTrafficMonitor(monitor, cancellationToken))
                            return;

                        uploadBytesTotal = totals.UploadBytes;
                        downloadBytesTotal = totals.DownloadBytes;
                        OnPropertyChanged(nameof(UploadTotal));
                        OnPropertyChanged(nameof(DownloadTotal));
                    });
                    await Task.Delay(TrafficTotalsInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (!await DelayTrafficReconnectAsync(cancellationToken))
                    return;
            }
        }
    }

    private void StopTrafficMonitor()
    {
        trafficCancellation?.Cancel();
        trafficCancellation?.Dispose();
        trafficCancellation = null;
        uploadBytesPerSecond = 0;
        downloadBytesPerSecond = 0;
        OnPropertyChanged(nameof(UploadRate));
        OnPropertyChanged(nameof(DownloadRate));
    }

    private void ResetTrafficSession()
    {
        uploadBytesTotal = 0;
        downloadBytesTotal = 0;
        TrafficHistory.Clear();
        OnPropertyChanged(nameof(UploadTotal));
        OnPropertyChanged(nameof(DownloadTotal));
    }

    private bool IsCurrentTrafficMonitor(
        CancellationTokenSource monitor,
        CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
               && ReferenceEquals(trafficCancellation, monitor);
    }

    private static async Task<bool> DelayTrafficReconnectAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TrafficReconnectDelay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void ShowImportOutcomeToast(ProfileImportOutcome outcome, string successMessage)
    {
        if (outcome.Warnings.Count > 0)
        {
            foreach (var warning in outcome.Warnings)
                Toast.Show(warning, ToastLevel.Warning);
            return;
        }

        Toast.Show(successMessage, ToastLevel.Success);
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(ActiveNodeDisplay));
        OnPropertyChanged(nameof(CoreStateDisplay));
        OnPropertyChanged(nameof(TunDisplay));
    }

    private string DescribeError(Exception exception)
    {
        return exception is CoreServiceException serviceException
            ? serviceException.Failure switch
            {
                CoreServiceFailure.AuthorizationDenied => localization["ServiceAuthorizationDenied"],
                CoreServiceFailure.InstallationFailed => localization["ServiceInstallationFailed"],
                CoreServiceFailure.RemovalFailed => localization["ServiceRemovalFailed"],
                _ => localization["ServiceUnavailable"],
            }
            : DescribeError(exception.Message);
    }

    private string DescribeError(string error)
    {
        return error switch
        {
            CoreServiceErrorCodes.AuthorizationDenied => localization["ServiceAuthorizationDenied"],
            CoreServiceErrorCodes.InstallationFailed => localization["ServiceInstallationFailed"],
            CoreServiceErrorCodes.RemovalFailed => localization["ServiceRemovalFailed"],
            CoreServiceErrorCodes.Unavailable => localization["ServiceUnavailable"],
            CoreServiceErrorCodes.Disconnected => localization["ServiceDisconnected"],
            _ => error,
        };
    }

    private static string FormatRate(long bytesPerSecond)
    {
        return FormatTrafficValue(bytesPerSecond, TrafficRateUnits);
    }

    private static string FormatBytes(long bytes)
    {
        return FormatTrafficValue(bytes, TrafficTotalUnits);
    }

    private static string FormatTrafficValue(long bytes, IReadOnlyList<string> units)
    {
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Count - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.#} {units[unit]}";
    }

    private static long AddTrafficBytes(long total, long bytesPerSecond)
    {
        var increment = Math.Max(0, bytesPerSecond);
        return total > long.MaxValue - increment
            ? long.MaxValue
            : total + increment;
    }
}
