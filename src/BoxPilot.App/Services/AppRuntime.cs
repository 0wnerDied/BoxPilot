using BoxPilot.App.ViewModels;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Services;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.App.Services;

public sealed class AppRuntime : IAsyncDisposable
{
    private readonly SingBoxService singBox;
    private readonly SubscriptionClient subscriptionClient;

    public AppRuntime()
        : this(AppPaths.CreateDefault())
    {
    }

    public AppRuntime(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var settingsStore = new SettingsStore(paths);
        var profileRepository = new ProfileRepository(paths);
        singBox = new SingBoxService(paths);
        var config = new SingBoxConfigService(paths, singBox);
        var customRouting = new CustomRoutingService(paths, config);
        var configurationFiles = new ConfigurationFileService(config, profileRepository);
        subscriptionClient = new SubscriptionClient();
        var subscriptionParser = new SubscriptionParser(config);
        var profileImporter = new ProfileImportService(
            subscriptionClient,
            subscriptionParser,
            config,
            profileRepository,
            customRouting);
        Localization = new LocalizationService();
        Session = new AppSessionViewModel(
            paths,
            settingsStore,
            profileRepository,
            singBox,
            config,
            profileImporter,
            customRouting,
            configurationFiles,
            Localization);
    }

    public LocalizationService Localization { get; }

    public AppSessionViewModel Session { get; }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        subscriptionClient.Dispose();
        await singBox.DisposeAsync();
    }
}
