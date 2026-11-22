using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RustText.Demo.Editor;
using RustText.Demo.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private UpdateRelease? _availableUpdate;

    private static string CurrentUpdateVersion => typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
    private static string ExecutablePath => Environment.ProcessPath ?? throw new IOException("Путь программы недоступен.");
    internal static TimeSpan AutomaticUpdateCheckInterval => TimeSpan.FromHours(1);

    internal static bool ShouldRunAutomaticUpdateCheck(bool enabled, bool isWindows, string? profileOverride) =>
        enabled && isWindows && string.IsNullOrWhiteSpace(profileOverride);

    internal static bool ShouldPresentBackgroundUpdate(UpdateRelease? release, string? dismissedVersion) =>
        release is not null && release.Version != dismissedVersion;

    private bool AutomaticUpdateChecksEnabled => ShouldRunAutomaticUpdateCheck(
        Settings.AutoCheckUpdates, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("RUSTTEXT_PROFILE_DIR"));

    private void SynchronizeAutomaticUpdateTimer(bool runImmediately, bool forceImmediateCheck = false)
    {
        var timer = _workspace.AutomaticUpdateTimer;
        if (!AutomaticUpdateChecksEnabled)
        {
            timer?.Stop();
            _workspace.AutomaticUpdateTimer = null;
            return;
        }

        if (timer is null)
        {
            var workspace = _workspace;
            timer = new DispatcherTimer { Interval = AutomaticUpdateCheckInterval };
            timer.Tick += (_, _) =>
            {
                var owner = workspace.Windows
                    .Where(window => !window._closed && !window._closing && window._restoreFinished)
                    .OrderBy(window => window._windowId == WorkspaceCatalogStore.PrimaryWindowId ? 0 : 1)
                    .FirstOrDefault();
                if (owner is null)
                {
                    return;
                }

                owner.Run(() => owner.CheckUpdatesAsync(manual: false));
            };
            _workspace.AutomaticUpdateTimer = timer;
            timer.Start();
        }

        if (runImmediately && (forceImmediateCheck || !_workspace.AutomaticUpdateStartupCheckAttempted))
        {
            _workspace.AutomaticUpdateStartupCheckAttempted = true;
            Run(() => CheckUpdatesAsync(manual: false));
        }
    }

    private void ShowKnownUpdate()
    {
        var release = _workspace.KnownUpdate;
        UpdateNoticePresent(ShouldPresentBackgroundUpdate(release, _workspace.DismissedUpdateVersion) ? release : null);
    }

    private async Task RegisterAssociationsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        var extensions = await ChooseFileAssociationsAsync(this, FileAssociations.ReadSelectedExtensions());
        if (extensions is null) return;
        FileAssociations.RegisterAndOpenDefaultAppsSettings(ExecutablePath, extensions);
        SetStatus("Выберите приложение по умолчанию в параметрах Windows.");
    }

    internal static async Task<IReadOnlySet<string>?> ChooseFileAssociationsAsync(Window owner, IReadOnlySet<string> selected)
    {
        var choices = FileAssociations.SupportedExtensions.Select(extension =>
            (Extension: extension, Control: new CheckBox { Content = extension, IsChecked = selected.Contains(extension) })).ToArray();
        var list = new WrapPanel { ItemSpacing = 12 };
        foreach (var (_, control) in choices) list.Children.Add(control);
        var body = DialogPanel(new TextBlock
        {
            Text = "Отметьте форматы, для которых хотите назначить RustText. Подтвердите выбор в параметрах Windows. "
                + "Снятие галочки не меняет уже назначенное приложение; RustText остаётся в меню «Открыть с помощью».",
            TextWrapping = TextWrapping.Wrap
        }, list);
        var dialog = Dialog("Открытие файлов в Windows", body, 490);
        body.Children.Add(Row(FocusButton(Button("Отмена", () => dialog.Close(false))),
            FocusButton(Button("Сохранить и открыть параметры", () => dialog.Close(true)), primary: true)));

        return await dialog.ShowDialog<bool>(owner)
            ? choices.Where(choice => choice.Control.IsChecked == true).Select(choice => choice.Extension)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
    }

    private void RevealTab(WorkTab tab)
    {
        if (tab.Path is { } path)
        {
            Run(() => ExplorerReveal.SelectFileAsync(path));
        }
    }

    private async Task ShowWindowFinderAsync()
    {
        var query = new TextBox { PlaceholderText = "PID или часть заголовка окна" };
        var matches = new ListBox { MinHeight = 155, MaxHeight = 400 };
        var status = new TextBlock { Foreground = FocusTheme.Muted };
        var body = DialogPanel(query, matches, status);
        var dialog = Dialog("Найти окно процесса", body, 650);
        void Find()
        {
            var term = query.Text?.Trim() ?? "";
            var byPid = uint.TryParse(term, out var pid);
            matches.Items.Clear();
            foreach (var window in WindowFinder.EnumerateVisibleWindows()
                .Where(window => byPid ? window.ProcessId == pid : window.Title.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Items.Add(new ListBoxItem { Content = $"{window.ProcessId} · {window.Title}", Tag = window });
            }

            status.Text = $"Окон найдено · {matches.Items.Count}";
        }

        body.Children.Add(Row(FocusButton(Button("Найти", Find), primary: true), FocusButton(Button("Вывести", () =>
        {
            if (matches.SelectedItem is ListBoxItem { Tag: WindowIdentity window })
            {
                status.Text = WindowFinder.ActivateWindow(window)
                    ? "Окно выведено на передний план."
                    : "Windows отклонила переключение либо окно было закрыто.";
            }
        }))));
        query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Find();
            }
        };
        await dialog.ShowDialog(this);
    }

    private VersionHistory VersionStore() => VersionHistory.OpenDefault(Settings.VersionFileLimitBytes, Settings.VersionTotalLimitBytes);

    private async Task<string?> CaptureVersionAsync(string path, TextEncodingKind? encoding, FileStamp expectedStamp)
    {
        try
        {
            var store = VersionStore();
            await store.CaptureExpectedAsync(path, encoding, expectedStamp, _lifetime.Token);
            return null;
        }
        catch (Exception error) when (IsUserError(error))
        {
            return error.Message;
        }
    }

    private async Task ShowVersionsAsync()
    {
        if (ActiveTab is not { Path: { } path, Editor: { } editor } active)
        {
            return;
        }

        var versions = VersionStore();
        var items = await versions.ListAsync(path, _lifetime.Token);
        var list = new ListBox { MinHeight = 110, MaxHeight = 230 };
        foreach (var version in items)
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(version.CapturedMillis).LocalDateTime;
            var source = version.IsLegacy ? " · Rust" : string.Empty;
            var encoding = version.Encoding is { } value ? $" · {value}" : " · кодировка неизвестна";
            list.Items.Add(new ListBoxItem { Content = $"{when:g} · {version.Bytes:N0} байт{encoding}{source}", Tag = version });
        }

        var preview = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, MinHeight = 220, MaxHeight = 360,
            PlaceholderText = "Выберите снимок", FontFamily = editor.FontFamily
        };
        var compare = new CheckBox { Content = "Подсветить отличия от текущего текста", IsEnabled = false };
        var diffLegend = new TextBlock { Text = "Зелёный «+» — только в текущем тексте; красный «−» — только в версии.", IsVisible = false };
        var diffPreview = new TextBlock { MinHeight = 220, MaxHeight = 360, FontFamily = editor.FontFamily, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        string? displayedDiffText = null;
        void RefreshVersionPreview()
        {
            var showDiff = compare.IsChecked == true && displayedDiffText is not null;
            preview.IsVisible = !showDiff;
            diffLegend.IsVisible = showDiff;
            diffPreview.IsVisible = showDiff;
            if (showDiff) RenderVersionDiff(diffPreview, editor.Text, displayedDiffText!);
        }
        compare.IsCheckedChanged += (_, _) => RefreshVersionPreview();
        var body = DialogPanel(new TextBlock { Text = path, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
            list, compare, diffLegend, preview, diffPreview);
        var dialog = Dialog("Версии файла", body, 760);
        var restoreButton = Button("Восстановить в редакторе", () => Run(RestoreVersionAsync));
        restoreButton.IsEnabled = false;
        list.SelectionChanged += (_, _) => Run(async () =>
        {
            restoreButton.IsEnabled = compare.IsEnabled = false;
            displayedDiffText = null;
            preview.Text = null;
            RefreshVersionPreview();
            if (list.SelectedItem is not ListBoxItem { Tag: VersionSnapshot selected } item) return;
            var bytes = await versions.ReadAsync(path, selected, _lifetime.Token);
            if (!ReferenceEquals(list.SelectedItem, item)) return;
            if (selected.Encoding is { } encoding)
            {
                try
                {
                    preview.Text = DocumentFiles.DecodeText(bytes, encoding);
                    displayedDiffText = DocumentFiles.DecodeSnapshot(bytes, encoding).Text;
                    restoreButton.IsEnabled = compare.IsEnabled = true;
                    RefreshVersionPreview();
                    return;
                }
                catch (InvalidDataException) { /* Older snapshots can carry an incorrect encoding. */ }
            }
            compare.IsChecked = false;
            preview.Text = "Кодировка снимка неизвестна. Первые байты в HEX:\n\n"
                + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 256)));
        });

        async Task RestoreVersionAsync()
        {
            if (list.SelectedItem is ListBoxItem { Tag: VersionSnapshot selected } && _tabs.ContainsKey(active.Id))
            {
                if (selected.Encoding is not { } encoding)
                    throw new IOException("Кодировка старой версии неизвестна. Выберите другую версию.");
                var bytes = await versions.ReadAsync(path, selected, _lifetime.Token);
                var revision = active.Revision;
                var expected = active.Stamp ?? throw new IOException("Перечитайте исходный файл перед восстановлением.");
                if (await DocumentFiles.GetStampAsync(path, _lifetime.Token) != expected)
                {
                    throw new IOException("Исходный файл изменился на диске. Откройте его заново.");
                }

                await versions.CaptureExpectedAsync(path, active.DiskEncoding ?? active.Encoding, expected, _lifetime.Token);
                var snapshot = DocumentFiles.DecodeSnapshot(bytes, encoding);
                if (await DocumentFiles.GetStampAsync(path, _lifetime.Token) != expected || active.Revision != revision)
                {
                    throw new IOException("Файл или текст изменились во время восстановления.");
                }

                if (!ReferenceEquals(active.Editor, editor)) return;
                DocumentFormatHistory.For(editor.Document, active.Format).Restore(snapshot.Text,
                    EditorFileFormat.From(snapshot.Encoding, snapshot.Ending, snapshot.Endings));
                dialog.Close();
                SetStatus("Версия восстановлена в редакторе. Ctrl+S сохранит её на диск.");
            }
        }
        body.Children.Add(Row(FocusButton(restoreButton, primary: true), FocusButton(Button("Лимиты…", () => Run(ShowSettingsAsync)))));
        await dialog.ShowDialog(this);
    }

    private static void RenderVersionDiff(TextBlock target, string currentText, string snapshotText)
    {
        var lines = VersionDiff.Compare(currentText, snapshotText);
        var inlines = target.Inlines;
        if (inlines is null)
        {
            target.Text = string.Join(Environment.NewLine, lines.Select(line =>
                (line.Kind == VersionDiffKind.Added ? "+ " : line.Kind == VersionDiffKind.Removed ? "− " : "  ") + line.Text));
            return;
        }

        inlines.Clear();
        foreach (var line in lines)
        {
            var (marker, foreground) = line.Kind switch
            {
                VersionDiffKind.Added => ("+ ", FocusTheme.Success),
                VersionDiffKind.Removed => ("− ", FocusTheme.Danger),
                VersionDiffKind.Info => ("· ", FocusTheme.Warning),
                _ => ("  ", FocusTheme.Muted)
            };
            inlines.Add(new Run(marker + line.Text) { Foreground = foreground });
            inlines.Add(new LineBreak());
        }
    }

    private async Task SaveDraftCopyAsync(string sourcePath)
    {
        using var item = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить копию черновика", SuggestedFileName = "черновик.txt", FileTypeChoices = [TextFiles]
        });
        if (item?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".rusttext-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = File.OpenRead(sourcePath))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await input.CopyToAsync(output, _lifetime.Token);
                await output.FlushAsync(_lifetime.Token);
                output.Flush(true);
            }

            _lifetime.Token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            SetStatus($"Копия сохранена · {path}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_workspace.UpdateCheckInProgress || _workspace.UpdateHandling || !manual && !AutomaticUpdateChecksEnabled)
        {
            return;
        }

        const string checking = "Проверка обновлений…";
        _workspace.UpdateCheckInProgress = true;
        if (manual)
        {
            SetStatus(checking);
        }

        try
        {
            var release = await UpdateService.CheckLatestAsync(CurrentUpdateVersion, _lifetime.Token);
            if (_closed || _closing)
            {
                return;
            }

            if (!manual && !AutomaticUpdateChecksEnabled) return;
            if (!manual && !ShouldPresentBackgroundUpdate(release, _workspace.DismissedUpdateVersion))
            {
                if (release is not null) _workspace.KnownUpdate = release;
                else PresentUpdate(null);
            }
            else
            {
                if (manual) _workspace.DismissedUpdateVersion = null;
                PresentUpdate(release);
            }
            if (manual && StatusText.Text == checking)
            {
                SetStatus(release is null ? "Обновление Avalonia-сборки не найдено." : null);
            }
        }
        catch (Exception error) when (!manual && (error is OperationCanceledException || IsUserError(error)))
        {
            // Automatic checks are best-effort; keep the hourly timer alive and leave the UI quiet.
        }
        finally
        {
            _workspace.UpdateCheckInProgress = false;
        }
    }

    private void PresentUpdate(UpdateRelease? release)
    {
        _workspace.KnownUpdate = release;
        if (release is null) _workspace.DismissedUpdateVersion = null;
        foreach (var window in _workspace.Windows.Where(window => !window._closed))
            window.UpdateNoticePresent(release);
    }

    private void UpdateNoticePresent(UpdateRelease? release)
    {
        _availableUpdate = release;
        UpdateNoticeText.Text = release is null ? null : $"Обновление {release.Version}";
        AutomationProperties.SetName(UpdateNoticeDetails, UpdateNoticeText.Text ?? "Проверить обновления");
        UpdateNotice.IsVisible = release is not null;
    }

    private void DismissUpdateNotice()
    {
        if (_workspace.KnownUpdate is { } release)
            _workspace.DismissedUpdateVersion = release.Version;
        foreach (var window in _workspace.Windows.Where(window => !window._closed))
            window.UpdateNoticePresent(null);
    }

    private void UpdateNoticeDismissClicked(object? sender, RoutedEventArgs e) => DismissUpdateNotice();

    private void UpdateNoticeDetailsClicked(object? sender, RoutedEventArgs e) => Run(OpenUpdateNoticeAsync);

    private async Task ShowReleaseHistoryAsync() => await ShowUpdateDialogAsync(null);

    private async Task OpenUpdateNoticeAsync()
    {
        if (_availableUpdate is not { } release || _workspace.UpdateHandling)
        {
            return;
        }

        _workspace.UpdateHandling = true;
        try
        {
            if (!await ShowUpdateDialogAsync(release))
            {
                DismissUpdateNotice();
                return;
            }

            UpdateNotice.IsVisible = false;
            SetStatus("Скачивание и проверка архива…");
            await using var stage = await UpdateService.StageAsync(release, ExecutablePath, _lifetime.Token);
            await RestartForUpdateAsync(() => UpdateService.ApplyStagedAsync(stage, ExecutablePath, _lifetime.Token));
        }
        finally
        {
            _workspace.UpdateHandling = false;
            if (!_closed && !_closing)
            {
                UpdateNotice.IsVisible = _availableUpdate is not null;
            }
        }
    }

    private async Task RestartForUpdateAsync(Func<Task> startHelper)
    {
        if (!await _workspace.CloseGate.WaitAsync(0))
            throw new IOException("Дождитесь завершения закрытия окна.");

        var switchGateHeld = false;
        MainWindow[] windows = [];
        try
        {
            if (!await _workspace.WorkspaceSwitchGate.WaitAsync(0))
                throw new IOException("Дождитесь завершения переключения рабочего пространства.");
            switchGateHeld = true;
            windows = _workspace.Windows.Where(window => !window._closed).ToArray();

            foreach (var window in windows)
            {
                if (!window._restoreFinished || window._closing || window._workspaceSwitchPending || window.OwnedWindows.Count != 0
                    || window._tabs.Values.Any(tab => tab.Saving || tab.Loading || tab.Closing || tab.Log?.HasPendingEdit == true))
                {
                    window.Activate();
                    throw new IOException("Завершите операции во всех окнах и сохраните или отмените правки страницы перед обновлением.");
                }
                if (_workspace.UnavailableSessions.Contains(window.WorkspaceCacheKey)
                    || window._workspaceViews.Values.Any(view => _workspace.UnavailableSessions.Contains(StorageCacheKey(view.Storage))))
                {
                    window.Activate();
                    throw new IOException("Не удалось сохранить сеанс одного из окон. Сохраните открытые документы перед обновлением.");
                }
            }

            foreach (var window in windows)
            {
                window._closing = true;
                window.IsEnabled = false;
                window.CancelTabDrag();
                window._checkpoint.Stop();
            }

            try
            {
                foreach (var window in windows)
                    await window.SaveSessionAsync();

                // Assigned windows restore their own checkpoints and retain draft tracking there.
                var restartStores = RestorableAssignedStores(windows);
                foreach (var window in windows)
                    await window.PreserveDetachedDraftsOnCloseAsync(restartStores);

                await startHelper();
            }
            catch
            {
                foreach (var window in windows)
                {
                    window._closing = false;
                    window.IsEnabled = true;
                    window._checkpoint.Start();
                }
                throw;
            }

            foreach (var window in windows)
                window._restartingForUpdate = true;

            foreach (var window in windows)
            {
                window.CloseWorkspaceContents();
                window.Close();
            }
        }
        finally
        {
            if (switchGateHeld) _workspace.WorkspaceSwitchGate.Release();
            _workspace.CloseGate.Release();
        }
    }
}
