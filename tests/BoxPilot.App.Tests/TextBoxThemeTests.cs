using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace BoxPilot.App.Tests;

public sealed class TextBoxThemeTests
{
    [AvaloniaFact]
    public void EditorKeepsCodePaletteWhenFocusedInLightTheme()
    {
        var editor = new TextBox { Text = "{}" };
        editor.Classes.Add("editor");

        AssertFocusedPalette(editor, "#211E1A", "#EEE8DD");
    }

    [AvaloniaFact]
    public void CommandKeepsWarmInputPaletteWhenFocusedInLightTheme()
    {
        var command = new TextBox { Text = "version" };
        command.Classes.Add("command");

        AssertFocusedPalette(command, "#FDFBF7", "#231F1B");
    }

    private static void AssertFocusedPalette(
        TextBox textBox,
        string expectedBackground,
        string expectedForeground)
    {
        var application = Assert.IsType<App>(Application.Current);
        application.RequestedThemeVariant = ThemeVariant.Light;

        var window = new Window { Content = textBox };
        try
        {
            window.Show();
            textBox.Focus();

            var border = textBox.GetVisualDescendants()
                .OfType<Border>()
                .Single(control => control.Name == "PART_BorderElement");

            Assert.True(textBox.IsFocused);
            AssertBrush(expectedBackground, border.Background);
            AssertBrush("#9B6243", border.BorderBrush);
            AssertBrush(expectedForeground, textBox.Foreground);
            AssertBrush(expectedForeground, textBox.CaretBrush);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertBrush(string expected, IBrush? actual)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(actual);
        Assert.Equal(Color.Parse(expected), brush.Color);
    }
}
