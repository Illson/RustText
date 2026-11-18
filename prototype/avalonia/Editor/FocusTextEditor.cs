using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed class FocusTextEditor : TextEditor
{
    protected override Type StyleKeyOverride => typeof(TextEditor);

    protected override IVisualLineTransformer CreateColorizer(IHighlightingDefinition definition) =>
        definition.Name == "Json" ? new FocusJsonColorizer(definition) : base.CreateColorizer(definition);

    private sealed class FocusJsonColorizer(IHighlightingDefinition definition) : HighlightingColorizer(definition)
    {
        private static readonly IBrush Key = new ImmutableSolidColorBrush(Color.Parse("#AEC9E1"));
        private static readonly IBrush StringValue = new ImmutableSolidColorBrush(Color.Parse("#BDD3AA"));
        private static readonly IBrush Literal = new ImmutableSolidColorBrush(Color.Parse("#CDB9E1"));

        protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
        {
            base.ApplyColorToElement(element, color);
            var foreground = color.Name switch
            {
                "FieldName" => Key,
                "String" => StringValue,
                "Bool" or "Null" => Literal,
                "Number" => FocusTheme.Warning,
                "Punctuation" => FocusTheme.Text,
                _ => null
            };
            if (foreground is not null)
            {
                element.TextRunProperties.SetForegroundBrush(foreground);
            }
        }
    }
}
