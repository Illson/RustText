using AvaloniaEdit.Document;

namespace RustText.Demo;

public partial class MainWindow
{
    private IEnumerable<(MainWindow Owner, WorkTab Tab)> FileViews(string path)
    {
        foreach (var window in _workspace.Windows)
        {
            if (window._closed) continue;
            foreach (var tab in window.LoadedWorkspaceTabs())
                if (!tab.Closing && !tab.IsLog && SamePath(tab.Path, path))
                    yield return (window, tab);
        }
    }

    private (MainWindow Owner, WorkTab Tab)[] DocumentViews(WorkTab tab)
    {
        if (tab.Path is not { } path || tab.Editor?.Document is not { } document)
            return [(this, tab)];
        return FileViews(path).Where(view => ReferenceEquals(view.Tab.Editor?.Document, document)).ToArray();
    }

    private bool TryShareFileEditor(WorkTab tab, TextEncodingKind encoding, string? recoveredText = null)
    {
        if (tab.Path is not { } path) return false;
        var other = FileViews(path).Select(view => view.Tab).FirstOrDefault(candidate =>
            !ReferenceEquals(candidate, tab) && candidate.Encoding == encoding && candidate.Editor is not null);
        if (other?.Editor is not { } editor) return false;
        var status = other.DiskStatus;
        if (recoveredText is not null && !string.Equals(editor.Text, recoveredText, StringComparison.Ordinal))
        {
            // A checkpoint can predate the live file in another workspace. Retain both versions.
            AddExternalDraft(tab, new TextDocument(recoveredText));
            status = "Открыт общий документ. Отличающаяся сохранённая версия восстановлена в отдельном черновике.";
        }

        tab.Format = other.Format;
        tab.DiskEncoding = other.DiskEncoding;
        tab.Stamp = other.Stamp;
        tab.Dirty = other.Dirty;
        tab.RestoredDirty = other.RestoredDirty;
        tab.DiskStatus = status;
        AttachEditor(tab, editor.Document);
        return true;
    }
}
