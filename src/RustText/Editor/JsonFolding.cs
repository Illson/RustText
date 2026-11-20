using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace RustText.Demo.Editor;

internal sealed class JsonFolding(TextEditor editor, JsonColorizer colorizer) : IDisposable
{
    private FoldingManager? _manager;
    private CancellationTokenSource? _pending;

    public Task<string?>? Schedule(string? path)
    {
        _pending?.Cancel();
        var document = editor.Document;
        if (!colorizer.Enabled || document.LineCount < 2 || TextOperations.IsJsonLinesPath(path))
        {
            RemoveManager();
            return null;
        }

        var source = new CancellationTokenSource();
        _pending = source;
        var textTask = ReadTextAsync(document, source.Token);
        _ = UpdateAsync(textTask, source);
        return textTask;
    }

    private static async Task<string?> ReadTextAsync(TextDocument document, CancellationToken token)
    {
        await Task.Delay(200, token);
        var snapshot = document.CreateSnapshot();
        return await Task.Run(() =>
        {
            using var reader = snapshot.CreateReader();
            return reader.ReadToEnd();
        }, token);
    }

    private async Task UpdateAsync(Task<string?> textTask, CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            var text = await textTask;
            token.ThrowIfCancellationRequested();
            if (text is null) return;

            var ranges = await Task.Run(() => JsonFoldRanges.Create(text, token), token);

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
