using System.Text.Json;
using System.Text.Json.Serialization;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

internal static class JsonDefaults
{
    public static JsonSerializerOptions SerializerOptions => BoxPilotJsonContext.Default.Options;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProfileIndex))]
internal sealed partial class BoxPilotJsonContext : JsonSerializerContext;
