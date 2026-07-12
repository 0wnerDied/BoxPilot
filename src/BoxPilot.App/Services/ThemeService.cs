using Avalonia;
using Avalonia.Styling;

namespace BoxPilot.App.Services;

public sealed class ThemeService
{
    public void Apply(string? theme)
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
