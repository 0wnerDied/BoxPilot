using BoxPilot.App.ViewModels;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Services;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.App.Services;

public sealed class AppRuntime : IAsyncDisposable
{
    public AppRuntime()
    {
        Paths = AppPaths.CreateDefault();
        SettingsStore = new SettingsStore(Paths);
        ProfileRepository = new ProfileRepository(Paths);
        SingBox = new SingBoxService(Paths);
        Config = new SingBoxConfigService(Paths, SingBox);
        SubscriptionClient = new SubscriptionClient();
        SubscriptionParser = new SubscriptionParser(Config);
        ProfileImporter = new ProfileImportService(
            SubscriptionClient,
            SubscriptionParser,
            Config,
            ProfileRepository);
        Localization = new LocalizationService();
        Themes = new ThemeService();
        Session = new AppSessionViewModel(
            Paths,
            SettingsStore,
            ProfileRepository,
            SingBox,
            Config,
            ProfileImporter,
            Localization,
            Themes);
    }

    public AppPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    public ProfileRepository ProfileRepository { get; }

    public SingBoxService SingBox { get; }

    public SingBoxConfigService Config { get; }

    public SubscriptionClient SubscriptionClient { get; }

    public SubscriptionParser SubscriptionParser { get; }

    public ProfileImportService ProfileImporter { get; }

    public LocalizationService Localization { get; }

    public ThemeService Themes { get; }

    public AppSessionViewModel Session { get; }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        SubscriptionClient.Dispose();
        await SingBox.DisposeAsync();
    }
}
