using BoxPilot.Core.Infrastructure;

namespace BoxPilot.Core.Tests;

public sealed class CoreServiceFilesTests
{
    [Fact]
    public async Task ApplicationFingerprintTracksPayloadButIgnoresDebugSymbols()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"boxpilot-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "BoxPilot");
            await File.WriteAllTextAsync(executable, "app-v1");
            await File.WriteAllTextAsync(Path.Combine(directory, "BoxPilot.Core.dll"), "core-v1");
            await File.WriteAllTextAsync(Path.Combine(directory, "BoxPilot.pdb"), "debug-v1");

            var first = await CoreServiceFiles.ReadApplicationPayloadAsync(
                executable,
                CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "BoxPilot.pdb"), "debug-v2");
            var debugChanged = await CoreServiceFiles.ReadApplicationPayloadAsync(
                executable,
                CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "BoxPilot.Core.dll"), "core-v2");
            var payloadChanged = await CoreServiceFiles.ReadApplicationPayloadAsync(
                executable,
                CancellationToken.None);

            Assert.Equal(first.Fingerprint, debugChanged.Fingerprint);
            Assert.NotEqual(first.Fingerprint, payloadChanged.Fingerprint);
            Assert.DoesNotContain(first.Files, path => path.EndsWith(".pdb", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
