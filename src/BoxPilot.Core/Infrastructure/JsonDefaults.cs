using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

internal static class JsonDefaults
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static JsonSerializerOptions SerializerOptions => Options;

    public static BoxPilotJsonContext Context { get; } = new(Options);

    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions(BoxPilotJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProfileIndex))]
[JsonSerializable(typeof(CoreServiceMessage))]
[JsonSerializable(typeof(CoreServiceInstallRequest))]
[JsonSerializable(typeof(CoreServiceConfiguration))]
[JsonSerializable(typeof(CoreServiceUninstallRequest))]
internal sealed partial class BoxPilotJsonContext : JsonSerializerContext;
