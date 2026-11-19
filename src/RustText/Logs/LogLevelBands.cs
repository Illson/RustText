using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace RustText.Demo.Logs;

// Keep the native text box responsible for hit testing, selection and copying.
internal sealed class LogLevelBands(TextBox textBox) : Control
{
    private readonly List<int> _starts = [];
    private readonly List<LogLevel> _levels = [];

    private long _nextOffset = -1;

    internal void SetPage(LogPage page)
    {
        var carried = page.StartOffset == _nextOffset && _levels.Count > 0 ? _levels[^1] : LogLevel.Other;
        _nextOffset = page.NextOffset;
        _levels.Clear();
        _starts.Clear();
        var offset = 0;
        foreach (var row in page.Rows)
        {
            if (_starts.Count == 0 || !row.StartsWithinLine)
            {
                if (_starts.Count > 0) offset += Environment.NewLine.Length;
                _starts.Add(offset);
                _levels.Add(row.StartsWithinLine ? carried : LogLevels.Classify(row.Text));
            }
            offset += row.Text.Length;
        }
        InvalidateVisual();
    }

    internal string SetFilteredPage(FilterPage page)
    {
        _starts.Clear();
        _levels.Clear();
        _nextOffset = -1;
        var text = new StringBuilder();
        foreach (var row in page.Rows)
        {
            if (_starts.Count == 0 || !row.Continuation)
            {
                if (_starts.Count != 0) text.Append(Environment.NewLine);
                _starts.Add(text.Length);
                _levels.Add(row.Level);
            }
            text.Append(row.Text);
        }
        InvalidateVisual();
        return text.ToString();
    }

    internal void SetEditedText(string text)
    {
        _starts.Clear();
        _levels.Clear();
        _nextOffset = -1;
        foreach (var row in LogLevelFilter.EditorRows(text, 0, default))
        {
            _starts.Add((int)row.StartOffset);
            _levels.Add(LogLevels.Classify(row.Text));
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_starts.Count == 0 || textBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { } presenter
            || presenter.TranslatePoint(default, this) is not { } origin) return;
        var y = origin.Y;
        foreach (var line in presenter.TextLayout.TextLines)
        {
            if (y >= Bounds.Height) break;
            if (y + line.Height > 0)
            {
                var index = _starts.BinarySearch(line.FirstTextSourceIndex);
                if (index < 0) index = ~index - 1;
                if (index >= 0 && _levels[index] != LogLevel.Other)
                {
                    var brush = LogLevels.Brush(_levels[index]);
                    using (context.PushOpacity(0.16))
                        context.FillRectangle(brush, new Rect(0, y, Bounds.Width, line.Height));
                    context.FillRectangle(brush, new Rect(0, y, 2, line.Height));
                }
            }
            y += line.Height;
        }
    }
}
