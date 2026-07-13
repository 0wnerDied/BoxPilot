using Avalonia;
using Avalonia.Styling;

namespace BoxPilot.App.Services;

internal static class ThemeService
{
    public static void Apply(string? theme)
    {
        if (Application.Current is not { } application)
            return;

        application.RequestedThemeVariant = theme?.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
