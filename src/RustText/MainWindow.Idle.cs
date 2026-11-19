using Avalonia.Threading;
using AvaloniaEdit.Document;

namespace RustText.Demo;

public partial class MainWindow
{
    private static readonly TimeSpan IdleEditorAge = TimeSpan.FromMinutes(5);
    private DispatcherTimer? _idleUnloadTimer;
    private bool _unloadingIdleEditors;

    private void EnsureIdleUnloadTimer()
    {
        if (_idleUnloadTimer is not null)
        {
            return;
        }

        _idleUnloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _idleUnloadTimer.Tick += IdleUnloadTimerTick;
        Closed += OnIdleUnloadWindowClosed;
        _idleUnloadTimer.Start();
    }

    private void IdleUnloadTimerTick(object? sender, EventArgs e) => Run(UnloadIdleEditorsAsync);

    private void OnIdleUnloadWindowClosed(object? sender, EventArgs e)
    {
        if (_idleUnloadTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= IdleUnloadTimerTick;
        }
        Closed -= OnIdleUnloadWindowClosed;
    }

    private async Task UnloadIdleEditorsAsync()
    {
        if (_closed || _closing || _unloadingIdleEditors)
        {
            return;
        }

        _unloadingIdleEditors = true;
        try
        {
            foreach (var tab in _tabs.Values.ToArray())
            {
                if (tab.Loading || tab.Closing || tab.Saving || tab.Log is { HasPendingEdit: true }
                    || tab.Log is { IsReadOnly: false, EditsPagesByDefault: false }
                    || tab.Deferred is not null || ReferenceEquals(tab, ActiveTab)
                    || ReferenceEquals(SelectedIn(tab.Group), tab) || DateTimeOffset.Now - tab.LastWorked < IdleEditorAge)
                {
                    continue;
                }

                if (tab.Editor is not { } editor)
                {
                    if (tab.Log is { } idleLog)
                    {
                        var logState = CaptureTab(tab);
                        idleLog.Close();
                        tab.Log = null;
                        SetDeferred(tab, logState);
                    }

                    continue;
                }

                if (editor.IsKeyboardFocusWithin)
                {
                    continue;
                }

                var document = editor.Document;
                var revision = tab.Revision;
                var needsDraft = tab.Dirty || tab.Path is null;
                SessionTab? saved = null;
                if (needsDraft)
                {
                    if (tab.Path is null && document.TextLength == 0)
                    {
                        continue;
                    }

                    try
                    {
                        await SaveSessionAsync();
                    }
                    catch
                    {
                        // Keep the live editor if its recovery draft could not be committed.
                        continue;
                    }

                    if (_closed || _closing || !_tabs.ContainsKey(tab.Id) || tab.Loading || tab.Closing
                        || ReferenceEquals(tab, ActiveTab) || ReferenceEquals(SelectedIn(tab.Group), tab)
                        || editor.IsKeyboardFocusWithin || !ReferenceEquals(tab.Editor?.Document, document)
                        || tab.Revision != revision || FindSavedLiveDraft(tab) is not { } draft)
                    {
                        continue;
                    }

                    saved = CaptureTab(tab) with { DraftRef = draft.DraftRef, DraftSha256 = draft.DraftSha256 };
                }
                else if (tab.Path is not null)
                {
                    saved = CaptureTab(tab);
                }

                if (saved is null || _closed || _closing || !_tabs.ContainsKey(tab.Id) || tab.Loading || tab.Closing
                    || ReferenceEquals(tab, ActiveTab) || ReferenceEquals(SelectedIn(tab.Group), tab)
                    || editor.IsKeyboardFocusWithin || !ReferenceEquals(tab.Editor?.Document, document) || tab.Revision != revision)
                {
                    continue;
                }

                _ = DetachEditor(tab);
                SetDeferred(tab, saved);
            }
        }
        finally
        {
            _unloadingIdleEditors = false;
        }
    }
}
