using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RustText.Demo.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private static string ExecutablePath => Environment.ProcessPath ?? throw new IOException("Путь программы недоступен.");

    private Task RegisterAssociationsAsync()
    {
        FileAssociations.RegisterAndOpenDefaultAppsSettings(ExecutablePath);
        SetStatus("Типы файлов зарегистрированы. Выберите RustText в параметрах Windows.");
        return Task.CompletedTask;
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
        var dialog = Dialog("Вывести окно на передний план", body, 650);
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

    private VersionHistory VersionStore() => VersionHistory.OpenDefault(_settings.VersionFileLimitBytes, _settings.VersionTotalLimitBytes);

    private async Task<string?> CaptureVersionAsync(string path, TextEncodingKind encoding, FileStamp? expectedStamp = null)
    {
        try
        {
            var store = VersionStore();
            if (expectedStamp is null)
                await store.CaptureAsync(path, encoding, _lifetime.Token);
            else
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
        VersionSnapshot? displayedSnapshot = null;
        string? displayedText = null;
        string? displayedDiffText = null;
        void RefreshVersionPreview()
        {
            var showDiff = compare.IsChecked == true && displayedSnapshot?.Encoding is not null && displayedDiffText is not null;
            preview.IsVisible = !showDiff;
            diffLegend.IsVisible = showDiff;
            diffPreview.IsVisible = showDiff;
            if (showDiff)
            {
                RenderVersionDiff(diffPreview, editor.Text, displayedDiffText!);
            }
        }
        compare.IsCheckedChanged += (_, _) => RefreshVersionPreview();
        var body = DialogPanel(new TextBlock { Text = path, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
            list, compare, diffLegend, preview, diffPreview);
        var dialog = Dialog("Версии файла", body, 760);
        list.SelectionChanged += (_, _) => Run(async () =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: VersionSnapshot selected })
            {
                var bytes = await versions.ReadAsync(path, selected, _lifetime.Token);
                if (list.SelectedItem is ListBoxItem { Tag: VersionSnapshot current } && current == selected)
                {
                    displayedSnapshot = selected;
                    if (selected.Encoding is { } encoding)
                    {
                        displayedText = DocumentFiles.DecodeText(bytes, encoding);
                        displayedDiffText = DocumentFiles.DecodeSnapshot(bytes, encoding).Text;
                        preview.Text = displayedText;
                        compare.IsEnabled = true;
                    }
                    else
                    {
                        displayedText = "У старой версии не записана кодировка. Показаны первые байты в HEX; восстановление текста отключено, чтобы не угадывать кодировку.\n\n"
                            + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 256)));
                        displayedDiffText = null;
                        preview.Text = displayedText;
                        compare.IsChecked = false;
                        compare.IsEnabled = false;
                    }
                    RefreshVersionPreview();
                }
            }
        });
        body.Children.Add(Row(FocusButton(Button("Восстановить в редакторе", () => Run(async () =>
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

                await versions.CaptureExpectedAsync(path, active.Encoding, expected, _lifetime.Token);
                var snapshot = DocumentFiles.DecodeSnapshot(bytes, encoding);
                if (await DocumentFiles.GetStampAsync(path, _lifetime.Token) != expected || active.Revision != revision)
                {
                    throw new IOException("Файл или текст изменились во время восстановления.");
                }

                active.Encoding = snapshot.Encoding;
                active.Ending = snapshot.Ending;
                active.OriginalEndings = snapshot.Endings;
                active.RestoredDirty = true;
                editor.Document.Replace(0, editor.Document.TextLength, snapshot.Text);
                dialog.Close();
                SetStatus("Версия восстановлена в редакторе. Ctrl+S сохранит её на диск.");
            }
        })), primary: true), FocusButton(Button("Лимиты…", () => Run(ShowSettingsAsync)))));
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

    private async Task CheckUpdatesAsync()
    {

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.2.2";
        SetStatus("Проверка обновлений…");
        var release = await UpdateService.CheckLatestAsync(version, _lifetime.Token);
        if (release is null)
        {
            SetStatus("Обновление Avalonia-сборки не найдено.");
            return;
        }

        var body = DialogPanel(new TextBlock { Text = $"Доступна версия {release.Version}.", Foreground = FocusTheme.Text });
        var dialog = Dialog("Обновление RustText", body);
        body.Children.Add(Row(FocusButton(Button("Скачать и перезапустить", () => dialog.Close(true)), primary: true),
            FocusButton(Button("Позже", () => dialog.Close(false)))));
        if (!await dialog.ShowDialog<bool>(this))
        {
            return;
        }

        SetStatus("Скачивание и проверка архива…");
        await using var stage = await UpdateService.StageAsync(release, ExecutablePath, _lifetime.Token);
        await SaveSessionAsync();
        await UpdateService.ApplyStagedAsync(stage, ExecutablePath, _lifetime.Token);
        Close();
    }
}
