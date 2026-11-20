using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace RustText.Demo.Editor;

internal sealed class JsonFolding(TextEditor editor, JsonColorizer colorizer) : IDisposable
{
    private FoldingManager? _manager;
    private CancellationTokenSource? _pending;

    public void Schedule(string? path)
    {
        _pending?.Cancel();
        var document = editor.Document;
        if (!colorizer.Enabled || document.LineCount < 2 || TextOperations.IsJsonLinesPath(path))
        {
            RemoveManager();
            return;
        }

        var source = new CancellationTokenSource();
        _pending = source;
        _ = UpdateAsync(document, source);
    }

    private async Task UpdateAsync(TextDocument document, CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            await Task.Delay(200, token);
            var snapshot = document.CreateSnapshot();
            var ranges = await Task.Run(() => JsonFoldRanges.Create(snapshot.Text, token), token);
            token.ThrowIfCancellationRequested();

            if (ranges.Count == 0)
            {
                RemoveManager();
                return;
            }

            _manager ??= FoldingManager.Install(editor.TextArea);
            _manager.UpdateFoldings(ranges, firstErrorOffset: -1);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_pending, source)) _pending = null;
            source.Dispose();
        }
    }

    public void Reveal(int offset)
    {
        if (_manager is null) return;
        foreach (var section in _manager.GetFoldingsContaining(offset))
        {
            if (section.IsFolded && offset < section.EndOffset) section.IsFolded = false;
        }
    }

    public void Dispose()
    {
        _pending?.Cancel();
        RemoveManager();
    }

    private void RemoveManager()
    {
        if (_manager is null) return;
        FoldingManager.Uninstall(_manager);
        _manager = null;
    }
}
