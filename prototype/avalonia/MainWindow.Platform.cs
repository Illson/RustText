using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
            Run(() => { ExplorerReveal.SelectFile(path); return Task.CompletedTask; });
        }
    }

    private async Task ShowWindowFinderAsync()
    {
        var query = new TextBox { PlaceholderText = "PID или часть заголовка окна" };
        var matches = new ListBox { MinHeight = 155, MaxHeight = 400 };
        var status = new TextBlock();
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

        body.Children.Add(Row(Button("Найти", Find), Button("Вывести", () =>
        {
            if (matches.SelectedItem is ListBoxItem { Tag: WindowIdentity window })
            {
                status.Text = WindowFinder.ActivateWindow(window)
                    ? "Окно выведено на передний план."
                    : "Windows отклонила переключение либо окно было закрыто.";
            }
        })));
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

    private async Task CaptureVersionAsync(string path, TextEncodingKind encoding)
    {
        try
        {
            await VersionStore().CaptureAsync(path, encoding, _lifetime.Token);
        }
        catch (Exception error) when (IsUserError(error))
        {
            SetStatus($"Файл сохранён. Создать снимок не удалось. {error.Message}");
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
            list.Items.Add(new ListBoxItem { Content = $"{when:g} · {version.Bytes:N0} байт", Tag = version });
        }

        var preview = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, MinHeight = 220, MaxHeight = 360,
            PlaceholderText = "Выберите снимок", FontFamily = editor.FontFamily
        };
        var body = DialogPanel(new TextBlock { Text = path, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
            list, preview);
        var dialog = Dialog("Версии файла", body, 760);
        list.SelectionChanged += (_, _) => Run(async () =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: VersionSnapshot selected })
            {
                var bytes = await versions.ReadAsync(path, selected.Id, _lifetime.Token);
                if (list.SelectedItem is ListBoxItem { Tag: VersionSnapshot current } && current.Id == selected.Id)
                {
                    preview.Text = DocumentFiles.DecodeText(bytes, selected.Encoding);
                }
            }
        });
        body.Children.Add(Row(Button("Восстановить в редакторе", () => Run(async () =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: VersionSnapshot selected } && _tabs.ContainsKey(active.Id))
            {
                var bytes = await versions.ReadAsync(path, selected.Id, _lifetime.Token);
                var revision = active.Revision;
                var expected = active.Stamp ?? throw new IOException("Перечитайте исходный файл перед восстановлением.");
                if (await DocumentFiles.GetStampAsync(path, _lifetime.Token) != expected)
                {
                    throw new IOException("Исходный файл изменился на диске. Откройте его заново.");
                }

                await versions.CaptureAsync(path, active.Encoding, _lifetime.Token);
                var snapshot = DocumentFiles.DecodeSnapshot(bytes, selected.Encoding);
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
        })), Button("Лимиты…", () => Run(ShowSettingsAsync))));
        await dialog.ShowDialog(this);
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

        var body = DialogPanel(new TextBlock { Text = $"Доступна версия {release.Version}." });
        var dialog = Dialog("Обновление RustText", body);
        body.Children.Add(Row(Button("Скачать и перезапустить", () => dialog.Close(true)), Button("Позже", () => dialog.Close(false))));
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
