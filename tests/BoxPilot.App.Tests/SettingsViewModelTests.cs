using BoxPilot.App.Services;
using BoxPilot.App.ViewModels;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Services;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task SaveAllowsClearedOptionalTextFieldsWhenEnablingTun()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        var settingsStore = new SettingsStore(paths);
        var profileRepository = new ProfileRepository(paths);
        await using var singBox = new SingBoxService(paths);
        var config = new SingBoxConfigService(paths, singBox);
        var customRouting = new CustomRoutingService(paths, config);
        var configurationFiles = new ConfigurationFileService(config, profileRepository);
        using var subscriptionClient = new SubscriptionClient();
        var parser = new SubscriptionParser(config);
        var importer = new ProfileImportService(
            subscriptionClient,
            parser,
            config,
            profileRepository,
            customRouting);
        var localization = new LocalizationService();
        await using var session = new AppSessionViewModel(
            paths,
            settingsStore,
            profileRepository,
            singBox,
            config,
            importer,
            customRouting,
            configurationFiles,
            localization);
        var viewModel = new SettingsViewModel(session, localization)
        {
            CorePath = null,
            CustomDnsServer = null,
            EnableTun = true,
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(session.Settings.EnableTun);
        Assert.Equal(string.Empty, session.Settings.SingBoxPath);
        Assert.Equal(string.Empty, session.Settings.CustomDnsServer);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"boxpilot-app-tests-{Guid.NewGuid():N}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
