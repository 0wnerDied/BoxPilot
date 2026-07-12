using System.Diagnostics;

namespace BoxPilot.Packaging.Tests;

public sealed class PublishPackagingTests : IDisposable
{
    private readonly string temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"boxpilot-packaging-{Guid.NewGuid():N}");

    [Fact]
    public async Task MacPackageCreatesDmgWithApplicationsLink()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var publish = CreatePublishDirectory("mac");
        var executable = Path.Combine(publish, "BoxPilot");
        File.Copy("/usr/bin/true", executable);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var target = Path.Combine(temporaryRoot, "mac", "target");

        var packageResult = await RunAsync(
            "/bin/bash",
            PackageScript,
            "osx-arm64",
            publish,
            target);

        Assert.True(packageResult.IsSuccess, packageResult.CombinedOutput);
        var dmg = Path.Combine(target, "BoxPilot-osx-arm64.dmg");
        Assert.True(File.Exists(dmg));
        Assert.False(File.Exists(Path.Combine(target, "BoxPilot-osx-arm64.zip")));

        var mount = Path.Combine(temporaryRoot, "mac", "mount");
        Directory.CreateDirectory(mount);
        var attachResult = await RunAsync(
            "/usr/bin/hdiutil",
            "attach",
            "-readonly",
            "-nobrowse",
            "-mountpoint",
            mount,
            dmg);
        Assert.True(attachResult.IsSuccess, attachResult.CombinedOutput);

        try
        {
            Assert.True(Directory.Exists(Path.Combine(mount, "BoxPilot.app")));
            var applications = new DirectoryInfo(Path.Combine(mount, "Applications"));
            Assert.Equal("/Applications", applications.LinkTarget);
        }
        finally
        {
            var detachResult = await RunAsync(
                "/usr/bin/hdiutil",
                "detach",
                "-force",
                mount);
            Assert.True(detachResult.IsSuccess, detachResult.CombinedOutput);
        }
    }

    [Fact]
    public async Task WindowsSingleFilePackageOmitsZipArchive()
    {
        if (!File.Exists("/bin/bash"))
            return;

        var publish = CreatePublishDirectory("windows-single-file");
        await File.WriteAllTextAsync(Path.Combine(publish, "BoxPilot.exe"), "executable");
        var target = Path.Combine(temporaryRoot, "windows-single-file", "target");

        var packageResult = await RunAsync(
            "/bin/bash",
            PackageScript,
            "win-x64",
            publish,
            target);

        Assert.True(packageResult.IsSuccess, packageResult.CombinedOutput);
        Assert.True(File.Exists(Path.Combine(target, "BoxPilot.exe")));
        Assert.False(File.Exists(Path.Combine(target, "BoxPilot-win-x64.zip")));
        Assert.False(Directory.Exists(Path.Combine(target, "BoxPilot")));
    }

    [Fact]
    public async Task WindowsPackageRejectsMissingExecutable()
    {
        if (!File.Exists("/bin/bash"))
            return;

        var publish = CreatePublishDirectory("windows-missing-executable");
        var target = Path.Combine(temporaryRoot, "windows-missing-executable", "target");

        var packageResult = await RunAsync(
            "/bin/bash",
            PackageScript,
            "win-arm64",
            publish,
            target);

        Assert.False(packageResult.IsSuccess);
        Assert.Contains("missing BoxPilot.exe", packageResult.StandardError);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryRoot))
            Directory.Delete(temporaryRoot, true);
    }

    private static string PackageScript => Path.Combine(
        FindRepositoryRoot(),
        "scripts",
        "package.sh");

    private string CreatePublishDirectory(string testName)
    {
        var publish = Path.Combine(temporaryRoot, testName, "target", "publish");
        Directory.CreateDirectory(publish);
        return publish;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "package.sh")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the BoxPilot repository root.");
    }

    private static async Task<ProcessResult> RunAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Could not start {executable}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public bool IsSuccess => ExitCode == 0;

        public string CombinedOutput => $"{StandardOutput}\n{StandardError}";
    }
}
