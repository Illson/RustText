using System.Globalization;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RustText.Demo;

public partial class MainWindow
{
    private static readonly ConcurrentDictionary<string, byte> ArchivedDraftOperations = new(StringComparer.Ordinal);
    private bool _archivingClosedDrafts;

    private sealed record DraftHistoryItem(SessionTab Tab, string Name, DraftArchiveEntry? Archive)
    {
        public string Label => Archive is null ? Name : $"📦 {Name} · архив";
    }

    private async Task LoadArchivedHistoryAsync()
    {
        try { await SessionStorage.DraftArchive.GetEntriesAsync(_lifetime.Token); }
        catch (Exception error) when (IsUserError(error)) { SetStatus($"Не удалось прочитать архив. {error.Message}"); }
    }

    private IEnumerable<DraftHistoryItem> DraftHistory(StorageService storage)
    {
        var live = _closedTabs.Where(tab => tab.Path is null);
        var rows = live.Select(tab => new DraftHistoryItem(tab, ClosedTabDisplayName(tab), null));
        if (!Settings.ShowArchivedDrafts || storage.ArchivedHistory.Count == 0) return rows;
        // A checkpoint can still contain the live draft if committing its transfer failed.
        var present = live.Select(tab => (tab.Id, tab.ClosedAtMs)).ToHashSet();
        return rows.Concat(storage.ArchivedHistory.Where(entry => !present.Contains((entry.Tab.Id, entry.ClosedAtMs)))
                .Select(entry => new DraftHistoryItem(ArchiveHistoryTab(entry.Tab), ArchivedDraftDisplayName(entry), entry)))
            .OrderByDescending(row => row.Archive?.ClosedAtMs ?? row.Tab.ClosedAtMs ?? long.MaxValue);
    }

    private string ArchivedDraftDisplayName(DraftArchiveEntry entry)
    {
        if (entry.Tab.CustomTitle is { Length: > 0 } title) return title;
        var saved = entry.Tab.UntitledName?.Trim();
        var preview = entry.DisplayName?.Trim();
        // Older archives contain a 40-character preview. Either field may hold the longer title.
        return LimitUntitledHistoryName(string.IsNullOrEmpty(saved) || preview?.StartsWith(saved, StringComparison.Ordinal) == true
            ? preview : saved);
    }

    private static SessionTab ArchiveHistoryTab(SessionTab tab)
    {
        if (tab.LastWorkedMs is { } timestamp && (timestamp < DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
            || timestamp > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()))
            return tab with { LastWorkedMs = null };
        return tab;
    }

    private async Task ArchiveClosedDraftsAsync()
    {
        if (_closed || _closing || _archivingClosedDrafts) return;
        _archivingClosedDrafts = true;
        try
        {
            await _workspace.WorkspaceSwitchGate.WaitAsync(_lifetime.Token);
            try
            {
                if (_closed || _closing) return;
                await ArchiveWorkspaceDraftsAsync(SessionStorage, _closedTabs, active: true);
                foreach (var (id, view) in _workspaceViews.ToArray())
                {
                    if ((id != EmptyStartupWindowId && _workspace.Catalog?.Workspaces.Any(entry => entry.Id == id) != true)
                        || _workspace.Windows.Any(window => !window._closed && ReferenceEquals(window.SessionStorage, view.Storage)))
                        continue;
                    await ArchiveWorkspaceDraftsAsync(view.Storage, view.ClosedTabs, active: false);
                }
            }
            finally { _workspace.WorkspaceSwitchGate.Release(); }
        }
        finally { _archivingClosedDrafts = false; }
    }

    private async Task ArchiveWorkspaceDraftsAsync(StorageService storage, List<SessionTab> history, bool active)
    {
        var settings = Settings.Normalize();
        var token = _lifetime.Token;
        if (await storage.DraftArchive.PruneExpiredAsync(settings.ArchivedDraftRetentionDays, token) > 0 && active)
            RefreshHistoryMenus();
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        for (var index = 0; index < history.Count; index++)
        {
            if (history[index].Path is null && history[index].ClosedAtMs is null)
            {
                history[index] = history[index] with { ClosedAtMs = now.ToUnixTimeMilliseconds() };
                changed = true;
            }
        }
        if (changed) await SaveHistoryAsync(null);
        if (settings.ArchiveClosedDraftAfterMinutes == 0) return;

        var cutoff = now.AddMinutes(-settings.ArchiveClosedDraftAfterMinutes).ToUnixTimeMilliseconds();
        foreach (var saved in history.Where(tab => tab.Path is null && tab.DraftRef is not null && tab.ClosedAtMs <= cutoff).ToArray())
        {
            if (!_workspace.ClosedTabOperations.Add(saved.Id)) continue;
            try
            {
                var verified = await storage.ResolveCurrentDraftAsync(saved, token);
                if (verified.DraftSha256 is null || storage.GetDraftPath(verified) is not { } draftPath || !File.Exists(draftPath))
                    continue;
                var archiveTab = string.IsNullOrWhiteSpace(verified.UntitledName) && string.IsNullOrEmpty(verified.CustomTitle)
                    ? verified with { UntitledName = ClosedTabDisplayName(verified, storage) } : verified;
                await storage.DraftArchive.ArchiveAsync(archiveTab, draftPath, token);
                await SaveHistoryAsync(verified);
                if (active)
                {
                    SetStatus($"Черновик «{ClosedTabDisplayName(verified)}» перенесён в архив.");
                    RefreshHistoryMenus();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (IsUserError(error))
            {
                SetStatus($"Не удалось архивировать черновик. {error.Message}");
            }
            finally { _workspace.ClosedTabOperations.Remove(saved.Id); }
        }

        async Task SaveHistoryAsync(SessionTab? archived)
        {
            if (active)
            {
                await SaveWorkspaceSessionAsync(excludeWindow: null, forgetClosedTab: archived);
                return;
            }

            // A hidden workspace was checkpointed when switching away. Preserve that layout and every open draft.
            await _workspace.SaveGate.WaitAsync(token);
            try
            {
                if (await storage.LoadSessionAsync(token) is not { } checkpoint) return;
                var remaining = history.Where(tab => tab.Id != archived?.Id).ToList();
                var updated = await storage.SaveSessionWithDraftReadersAsync(checkpoint with { ClosedTabs = remaining }, null, token,
                    preserveAllClosedTabs: PreserveEmptyHistory(storage));
                history.Clear();
                history.AddRange(updated.ClosedTabs ?? []);
                if (archived is not null)
                    await storage.RemoveHistoryFromPreviousCheckpointAsync(archived.Id, updated.Tabs.All(tab => tab.Id != archived.Id), token);
            }
            finally { _workspace.SaveGate.Release(); }
        }
    }

    private async Task ShowDraftArchiveAsync()
    {
        var storage = SessionStorage;
        var archive = storage.DraftArchive;
        var rows = new StackPanel { Spacing = 8 };
        var body = DialogPanel(new TextBlock { Text = "Архивные закрытые черновики" },
            new ScrollViewer { Content = rows, MaxHeight = 440, MinHeight = 100 });
        var dialog = Dialog("Архив черновиков", body, 620);
        async Task RefreshAsync()
        {
            rows.Children.Clear();
            IReadOnlyList<DraftArchiveEntry> entries;
            try { entries = await archive.GetEntriesAsync(_lifetime.Token); }
            catch (Exception error) when (IsUserError(error))
            {
                rows.Children.Add(new TextBlock { Text = error.Message, Foreground = FocusTheme.Danger, TextWrapping = TextWrapping.Wrap });
                return;
            }
            if (entries.Count == 0)
            {
                rows.Children.Add(new TextBlock { Text = "Архив пуст.", Foreground = FocusTheme.Muted });
                return;
            }
            foreach (var entry in entries)
            {
                var restore = FocusButton(Button("Восстановить", () => Run(async () =>
                {
                    await RestoreArchivedDraftAsync(storage, entry);
                    await RefreshAsync();
                })), primary: true);
                var remove = FocusButton(Button("Удалить", () => Run(async () =>
                {
                    await RemoveArchivedDraftAsync(storage, entry);
                    await RefreshAsync();
                })));
                var title = new TextBlock { Text = ArchivedDraftDisplayName(entry), FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis };
                var description = new TextBlock
                {
                    Text = $"{FormatArchiveSize(entry.Bytes)} · закрыт {DateTimeOffset.FromUnixTimeMilliseconds(entry.ClosedAtMs).ToLocalTime():g}",
                    Foreground = FocusTheme.Muted
                };
                rows.Children.Add(new Border
                {
                    Padding = new Thickness(10),
                    BorderBrush = FocusTheme.Border,
                    BorderThickness = new Thickness(1),
                    Child = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                        ColumnSpacing = 8,
                        Children = { new StackPanel { Spacing = 3, Children = { title, description } }, restore, remove }
                    }
                });
                Grid.SetColumn(restore, 1);
                Grid.SetColumn(remove, 2);
            }
        }

        await RefreshAsync();
        await dialog.ShowDialog(this);
    }

    private async Task RestoreArchivedDraftAsync(StorageService storage, DraftArchiveEntry entry)
    {
        await _workspace.WorkspaceSwitchGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!ReferenceEquals(storage, SessionStorage) || !ArchivedDraftOperations.TryAdd(entry.Id, 0)) return;
            SessionTab? restored = null;
            try
            {
                restored = ArchiveHistoryTab(await storage.DraftArchive.RestoreAsync(entry, _lifetime.Token)) with { Id = NextId() };
                _closedTabs.Insert(0, restored);
                await RestoreClosedWhileHoldingWorkspaceSwitchGateAsync(restored);
                if (_closedTabs.Any(tab => tab.Id == restored.Id)) return;
                await SaveSessionAsync();
                if (!storage.HasCommittedDraft(restored.Id, entry.Sha256))
                {
                    SetStatus("Черновик открыт. Архивная копия сохранена до подтверждения записи сессии.");
                    return;
                }
                await storage.DraftArchive.RemoveAsync(entry, _lifetime.Token);
            }
            catch (Exception error) when (IsUserError(error))
            {
                if (restored is not null) _closedTabs.RemoveAll(tab => tab.Id == restored.Id);
                SetStatus($"Не удалось восстановить черновик. {error.Message}");
            }
            finally
            {
                ArchivedDraftOperations.TryRemove(entry.Id, out _);
                RefreshHistoryMenus();
            }
        }
        finally { _workspace.WorkspaceSwitchGate.Release(); }
    }

    private async Task RemoveArchivedDraftAsync(StorageService storage, DraftArchiveEntry entry)
    {
        await _workspace.WorkspaceSwitchGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!ReferenceEquals(storage, SessionStorage)) return;
            await storage.DraftArchive.RemoveAsync(entry, _lifetime.Token);
            RefreshHistoryMenus();
        }
        finally { _workspace.WorkspaceSwitchGate.Release(); }
    }

    private static string FormatArchiveSize(long bytes)
    {
        var size = (double)bytes;
        var units = new[] { "Б", "КиБ", "МиБ", "ГиБ", "ТиБ" };
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))} {units[index]}";
    }
}
