using System.Text;
using Avalonia;
using Avalonia.Data;
using Avalonia.Media;
using AvaloniaEdit;

namespace BoxPilot.App.Controls;

public enum CodeLanguage
{
    Json,
    Console,
}

public sealed class CodeEditor : TextEditor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static readonly StyledProperty<CodeLanguage> LanguageProperty =
        AvaloniaProperty.Register<CodeEditor, CodeLanguage>(nameof(Language));

    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<CodeEditor, string>(
            nameof(Value),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<CodeEditor, string>(nameof(Placeholder), string.Empty);

    static CodeEditor()
    {
        LanguageProperty.Changed.AddClassHandler<CodeEditor>(
            static (editor, _) => editor.ApplyLanguage());
        ValueProperty.Changed.AddClassHandler<CodeEditor>(
            static (editor, _) => editor.ApplyValue(editor.Value));
        PlaceholderProperty.Changed.AddClassHandler<CodeEditor>(
            static (editor, _) => editor.Watermark = editor.Placeholder);
    }

    public CodeEditor()
    {
        Classes.Add("code-editor");
        Encoding = StrictUtf8;
        LineNumbersForeground = new SolidColorBrush(Color.Parse("#81786E"));
        SearchResultsBrush = new SolidColorBrush(Color.Parse("#5E493A"));
        TextArea.CaretBrush = new SolidColorBrush(Color.Parse("#F2ECE2"));
        TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#6F4936"));
        TextArea.SelectionForeground = new SolidColorBrush(Color.Parse("#FFF9EF"));
        TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse("#2E2923"));
        TextArea.TextView.CurrentLineBorder = new Pen(
            new SolidColorBrush(Color.Parse("#5E493A")));
        Options.AllowScrollBelowDocument = true;
        Options.ConvertTabsToSpaces = true;
        Options.EnableEmailHyperlinks = false;
        Options.EnableHyperlinks = false;
        Options.IndentationSize = 2;
        TextChanged += OnEditorTextChanged;
        ApplyLanguage();
    }

    public CodeLanguage Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    private void ApplyLanguage()
    {
        SyntaxHighlighting = CodeHighlighting.GetDefinition(Language);
        Options.AcceptsTab = Language == CodeLanguage.Json;
        Options.HighlightCurrentLine = Language == CodeLanguage.Json;
    }

    private void ApplyValue(string value)
    {
        if (!string.Equals(Text, value, StringComparison.Ordinal))
            Text = value;
    }

    private void OnEditorTextChanged(object? sender, EventArgs eventArgs)
    {
        if (!string.Equals(Value, Text, StringComparison.Ordinal))
            SetCurrentValue(ValueProperty, Text);
    }
}
