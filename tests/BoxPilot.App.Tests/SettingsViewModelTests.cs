using BoxPilot.App.Services;
using BoxPilot.App.ViewModels;
using BoxPilot.Core.Infrastructure;

namespace BoxPilot.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void OpenDataDirectoryPreservesWhitespaceInPath()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BoxPilot App Tests",
            "data");

        var startInfo = AppSessionViewModel.CreateOpenDataDirectoryStartInfo(directory);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(directory, Assert.Single(startInfo.ArgumentList));
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public async Task SaveAllowsClearedOptionalTextFieldsWhenEnablingTun()
    {
        using var directory = new TemporaryDirectory();
        await using var runtime = new AppRuntime(new AppPaths(directory.Path));
        var viewModel = new SettingsViewModel(runtime.Session, runtime.Localization)
        {
            CorePath = null,
            CustomDnsServer = null,
            EnableTun = true,
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(runtime.Session.Settings.EnableTun);
        Assert.Equal(string.Empty, runtime.Session.Settings.SingBoxPath);
        Assert.Equal(string.Empty, runtime.Session.Settings.CustomDnsServer);
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
