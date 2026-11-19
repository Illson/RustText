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
    private DraftArchiveService? _draftArchiveService;
    private bool _archivingClosedDrafts;

    private DraftArchiveService DraftArchive => _draftArchiveService ??= new(_storage.RootDirectory);

    private async Task ArchiveClosedDraftsAsync()
    {
        if (_closed || _closing || _archivingClosedDrafts) return;
        _archivingClosedDrafts = true;
        try
        {
            var settings = _settings.Normalize();
            await DraftArchive.PruneExpiredAsync(settings.ArchivedDraftRetentionDays, _lifetime.Token);
            if (settings.ArchiveClosedDraftAfterMinutes == 0) return;

            var now = DateTimeOffset.UtcNow;
            var changed = false;
            for (var index = 0; index < _closedTabs.Count; index++)
            {
                if (_closedTabs[index].Path is null && _closedTabs[index].ClosedAtMs is null)
                {
                    _closedTabs[index] = _closedTabs[index] with { ClosedAtMs = now.ToUnixTimeMilliseconds() };
                    changed = true;
                }
            }
            if (changed) await SaveSessionAsync();

            var threshold = TimeSpan.FromMinutes(settings.ArchiveClosedDraftAfterMinutes);
            foreach (var saved in _closedTabs.Where(tab => tab.Path is null && tab.DraftRef is not null
                && tab.ClosedAtMs is { } closedAt
                && now - DateTimeOffset.FromUnixTimeMilliseconds(closedAt) >= threshold).ToArray())
            {
                if (!_workspace.ClosedTabOperations.Add(saved.Id)) continue;
                try
                {
                    var verified = await _storage.ResolveCurrentDraftAsync(saved, _lifetime.Token);
                    if (verified.DraftRef is null || verified.DraftSha256 is null) continue;
                    var draftPath = _storage.GetDraftPath(verified);
                    if (draftPath is null || !File.Exists(draftPath)) continue;
                    await DraftArchive.ArchiveAsync(verified, draftPath, _lifetime.Token);
                    await SaveWorkspaceSessionAsync(excludeWindow: null, forgetClosedTab: verified);
                    SetStatus($"Черновик «{ClosedTabDisplayName(verified)}» перенесён в архив.");
                    RefreshHistoryMenus();
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception error) when (IsUserError(error))
                {
                    SetStatus($"Не удалось архивировать черновик. {error.Message}");
                }
                finally { _workspace.ClosedTabOperations.Remove(saved.Id); }
            }
        }
        finally { _archivingClosedDrafts = false; }
    }

    private async Task ShowDraftArchiveSettingsAsync()
    {
        var archivePresets = new[] { "1 час", "5 часов", "1 день", "7 дней", "Никогда", "Вручную" };
        var archivePreset = new ComboBox { ItemsSource = archivePresets, SelectedIndex = 5 };
        var archiveCount = new TextBox { Text = "5", Width = 78 };
        var archiveUnit = new ComboBox { ItemsSource = new[] { "минут", "часов", "дней" }, SelectedIndex = 1, Width = 100 };

        var retentionPresets = new[] { "1 день", "7 дней", "30 дней", "1 год", "Безлимит", "Вручную" };
        var retentionPreset = new ComboBox { ItemsSource = retentionPresets, SelectedIndex = 5 };
        var retentionCount = new TextBox { Text = "30", Width = 78 };
        var retentionUnit = new ComboBox { ItemsSource = new[] { "дней", "недель", "месяцев", "лет" }, SelectedIndex = 0, Width = 100 };

        ApplyArchivePreset(_settings.ArchiveClosedDraftAfterMinutes);
        ApplyRetentionPreset(_settings.ArchivedDraftRetentionDays);
        archivePreset.SelectionChanged += (_, _) =>
        {
            switch (archivePreset.SelectedIndex)
            {
                case 0: archiveCount.Text = "1"; archiveUnit.SelectedIndex = 1; break;
                case 1: archiveCount.Text = "5"; archiveUnit.SelectedIndex = 1; break;
                case 2: archiveCount.Text = "1"; archiveUnit.SelectedIndex = 2; break;
                case 3: archiveCount.Text = "7"; archiveUnit.SelectedIndex = 2; break;
                case 4: archiveCount.Text = "0"; archiveUnit.SelectedIndex = 1; break;
            }
        };
        retentionPreset.SelectionChanged += (_, _) =>
        {
            switch (retentionPreset.SelectedIndex)
            {
                case 0: retentionCount.Text = "1"; retentionUnit.SelectedIndex = 0; break;
                case 1: retentionCount.Text = "7"; retentionUnit.SelectedIndex = 0; break;
                case 2: retentionCount.Text = "30"; retentionUnit.SelectedIndex = 0; break;
                case 3: retentionCount.Text = "1"; retentionUnit.SelectedIndex = 3; break;
                case 4: retentionCount.Text = "0"; retentionUnit.SelectedIndex = 0; break;
            }
        };

        var body = DialogPanel(
            new TextBlock { Text = "Архивировать закрытые черновики после" }, archivePreset,
            Row(archiveCount, archiveUnit),
            new TextBlock { Text = "Хранить архивные черновики" }, retentionPreset,
            Row(retentionCount, retentionUnit),
            new TextBlock { Text = "Открытые вкладки не архивируются. Для старых записей срок начинается после первого сохранения новой версии.",
                TextWrapping = TextWrapping.Wrap, Foreground = FocusTheme.Muted });
        var dialog = Dialog("Черновики и архив", body);
        var warning = new TextBlock { Foreground = FocusTheme.Danger };
        body.Children.Add(warning);
        body.Children.Add(FocusButton(Button("Сохранить", () =>
        {
            if (!TryParseArchiveMinutes(archiveCount.Text, archiveUnit.SelectedIndex, out var archiveMinutes)
                || !TryParseRetentionDays(retentionCount.Text, retentionUnit.SelectedIndex, out var retentionDays))
            {
                warning.Text = "Введите целое значение в допустимом диапазоне.";
                return;
            }
            _settings = _settings with
            {
                ArchiveClosedDraftAfterMinutes = archiveMinutes,
                ArchivedDraftRetentionDays = retentionDays
            };
            dialog.Close(true);
        }), primary: true));

        if (await dialog.ShowDialog<bool>(this))
        {
            await SaveWorkspaceSettingsAsync(_settings);
            await ArchiveClosedDraftsAsync();
        }

        void ApplyArchivePreset(int minutes)
        {
            if (minutes == 60) { archivePreset.SelectedIndex = 0; archiveCount.Text = "1"; archiveUnit.SelectedIndex = 1; }
            else if (minutes == 300) { archivePreset.SelectedIndex = 1; archiveCount.Text = "5"; archiveUnit.SelectedIndex = 1; }
            else if (minutes == 1440) { archivePreset.SelectedIndex = 2; archiveCount.Text = "1"; archiveUnit.SelectedIndex = 2; }
            else if (minutes == 10_080) { archivePreset.SelectedIndex = 3; archiveCount.Text = "7"; archiveUnit.SelectedIndex = 2; }
            else if (minutes == 0) { archivePreset.SelectedIndex = 4; archiveCount.Text = "0"; archiveUnit.SelectedIndex = 1; }
            else
            {
                archivePreset.SelectedIndex = 5;
                archiveCount.Text = minutes.ToString(CultureInfo.CurrentCulture);
                archiveUnit.SelectedIndex = 0;
            }
        }

        void ApplyRetentionPreset(int days)
        {
            if (days == 1) retentionPreset.SelectedIndex = 0;
            else if (days == 7) retentionPreset.SelectedIndex = 1;
            else if (days == 30) retentionPreset.SelectedIndex = 2;
            else if (days == 365) { retentionPreset.SelectedIndex = 3; retentionUnit.SelectedIndex = 3; retentionCount.Text = "1"; return; }
            else if (days == 0) { retentionPreset.SelectedIndex = 4; retentionCount.Text = "0"; return; }
            else
            {
                retentionPreset.SelectedIndex = 5;
                retentionCount.Text = days.ToString(CultureInfo.CurrentCulture);
            }
            retentionUnit.SelectedIndex = 0;
            retentionCount.Text = days.ToString(CultureInfo.CurrentCulture);
        }
    }

    private async Task ShowDraftArchiveAsync()
    {
        var rows = new StackPanel { Spacing = 8 };
        var body = DialogPanel(new TextBlock { Text = "Архивные закрытые черновики" },
            new ScrollViewer { Content = rows, MaxHeight = 440, MinHeight = 100 });
        var dialog = Dialog("Архив черновиков", body, 620);
        async Task RefreshAsync()
        {
            rows.Children.Clear();
            IReadOnlyList<DraftArchiveEntry> entries;
            try { entries = await DraftArchive.GetEntriesAsync(_lifetime.Token); }
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
                    if (!ArchivedDraftOperations.TryAdd(entry.Id, 0)) return;
                    SessionTab? restoredTab = null;
                    try
                    {
                        restoredTab = (await DraftArchive.RestoreAsync(entry, _lifetime.Token)) with { Id = NextId() };
                        _closedTabs.Insert(0, restoredTab);
                        await RestoreClosedAsync(restoredTab);
                        if (!_closedTabs.Any(tab => tab.Id == restoredTab.Id))
                        {
                            await SaveSessionAsync();
                            await DraftArchive.RemoveAsync(entry, _lifetime.Token);
                            await RefreshAsync();
                        }
                    }
                    catch (Exception error) when (IsUserError(error))
                    {
                        if (restoredTab is not null) _closedTabs.RemoveAll(tab => tab.Id == restoredTab.Id);
                        SetStatus($"Не удалось восстановить черновик. {error.Message}");
                    }
                    finally { ArchivedDraftOperations.TryRemove(entry.Id, out _); }
                })), primary: true);
                var remove = FocusButton(Button("Удалить", () => Run(async () =>
                {
                    await DraftArchive.RemoveAsync(entry, _lifetime.Token);
                    await RefreshAsync();
                })));
                var title = new TextBlock { Text = entry.DisplayName, FontWeight = FontWeight.SemiBold,
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

    private static bool TryParseArchiveMinutes(string? text, int unit, out int minutes)
    {
        minutes = 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.CurrentCulture, out var value) || value < 0) return false;
        var multiplier = unit switch { 0 => 1L, 1 => 60L, 2 => 1440L, _ => 0L };
        var result = value * multiplier;
        if (result > 525_600) return false;
        minutes = (int)result;
        return true;
    }

    private static bool TryParseRetentionDays(string? text, int unit, out int days)
    {
        days = 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.CurrentCulture, out var value) || value < 0) return false;
        var multiplier = unit switch { 0 => 1L, 1 => 7L, 2 => 30L, 3 => 365L, _ => 0L };
        var result = value * multiplier;
        if (result > 3650) return false;
        days = (int)result;
        return true;
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
