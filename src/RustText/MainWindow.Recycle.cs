using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using RustText.Demo.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private bool _recycling;

    private async Task RecycleFromPickerAsync(bool folder)
    {
        if (!OperatingSystem.IsWindows() || _recycling) return;
        _recycling = true;
        try
        {
            var items = folder
                ? (IReadOnlyList<IStorageItem>)await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    { Title = "Выберите папку для удаления", AllowMultiple = false })
                : await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    { Title = "Выберите файл для удаления", AllowMultiple = false });
            if (items.Count == 0) return;
            using var item = items[0];
            if (item.TryGetLocalPath() is { } path) await RecyclePathAsync(path);
        }
        finally { _recycling = false; }
    }

    [SupportedOSPlatform("windows")]
    internal async Task RecyclePathAsync(string path)
    {
        var token = _lifetime.Token;
        var target = await Task.Run(() => LockedFileInspection.Select(path), token);
        EnsureCanRecycle(target);
        if (!await ConfirmRecycleAsync("Удалить с диска?", target.Path,
            "Проводник переместит объект в корзину. Если корзина недоступна, Windows может предложить удаление без восстановления.",
            "Удалить")) return;

        var (blocked, owners) = await ScanAsync();
        if (blocked.Count != 0)
        {
            if (!CanReleaseAll())
            {
                await ShowStillBlockedAsync();
                return;
            }
            if (!await ConfirmRecycleAsync("Объект занят", target.Path,
                $"{Describe()}\n\nRustText попросит доступные приложения закрыться штатно. "
                + "Если приложение предлагает сохранить изменения, сначала сохраните их.", "Освободить и удалить")) return;

            EnsureCanRecycle(target);
            var graceful = owners.ToHashSet();
            (blocked, owners) = await ScanAsync();
            EnsureCanRecycle(target);
            if (!CanReleaseAll(graceful))
            {
                await ShowStillBlockedAsync();
                return;
            }
            await Task.Run(() => LocalProcesses.RequestClose(owners, token), token);
            (blocked, owners) = await WaitForLocksAsync();
            if (blocked.Count != 0 && CanReleaseAll(graceful))
            {
                if (!await ConfirmRecycleAsync("Принудительное завершение", target.Path,
                    $"{Describe()}\n\nПринудительное завершение может уничтожить несохранённые данные в этих приложениях.",
                    "Завершить и удалить")) return;

                EnsureCanRecycle(target);
                var approved = owners.ToHashSet();
                (blocked, owners) = await ScanAsync();
                EnsureCanRecycle(target);
                if (!CanReleaseAll(approved))
                {
                    await ShowStillBlockedAsync();
                    return;
                }
                await Task.Run(() => LocalProcesses.ForceClose(owners, token), token);
                (blocked, owners) = await WaitForLocksAsync();
            }
            if (blocked.Count != 0)
            {
                await ShowStillBlockedAsync();
                return;
            }
        }
        EnsureCanRecycle(target);
        try
        {
            if (await ShellRecycle.MoveAsync(target, this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, token))
                SetStatus("Объект удалён с исходного пути.");
            else if (File.Exists(target.Path) || Directory.Exists(target.Path))
            {
                (blocked, owners) = await ScanAsync();
                if (blocked.Count != 0) await ShowStillBlockedAsync();
                else SetStatus("Удаление отменено или Проводник не смог его выполнить.");
            }
            else SetStatus("Объект больше не находится по исходному пути.");
        }
        catch (IOException)
        {
            (blocked, owners) = await ScanAsync();
            if (blocked.Count == 0) throw;
            await ShowStillBlockedAsync();
        }

        Task<(IReadOnlyList<LockedFileInspection.BlockedItem>, IReadOnlyList<LocalProcesses.Owner>)> ScanAsync() =>
            Task.Run(() =>
            {
                var items = LockedFileInspection.Inspect(target, token);
                return (items, LocalProcesses.Describe(items));
            }, token);

        async Task<(IReadOnlyList<LockedFileInspection.BlockedItem>, IReadOnlyList<LocalProcesses.Owner>)> WaitForLocksAsync()
        {
            var result = await ScanAsync();
            for (var attempt = 0; attempt < 6 && result.Item1.Count != 0; attempt++)
            {
                await Task.Delay(350, token);
                result = await ScanAsync();
            }
            return result;
        }

        bool CanReleaseAll(HashSet<LocalProcesses.Owner>? approved = null)
        {
            var eligible = owners.Where(owner => owner.CanClose && (approved is null || approved.Contains(owner)))
                .Select(owner => owner.Id).ToHashSet();
            return blocked.All(item => item.ProcessIds.Count == 1 && eligible.Contains(item.ProcessIds[0]));
        }

        string Describe()
        {
            var offered = owners.Where(owner => owner.CanClose).Select(owner => owner.Label).ToArray();
            var others = owners.Where(owner => !owner.CanClose).Select(owner => owner.Label).ToArray();
            var text = offered.Length == 0 ? "" : "Можно закрыть: " + string.Join(", ", offered);
            if (others.Length != 0) text += "\nНельзя закрыть автоматически: " + string.Join(", ", others);
            if (blocked.Any(item => item.ProcessIds.Count == 0)) text += "\nНе все владельцы блокировки определены.";
            return text.Trim();
        }

        Task<bool> ShowStillBlockedAsync() => ConfirmRecycleAsync("Объект остаётся занят", target.Path,
            "Не удалось освободить объект.\n" + Describe(), null);
    }

    private void EnsureCanRecycle(LockedFileInspection.Target target)
    {
        var protectedPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            _storage.ProfileBaseDirectory
        };
        if (protectedPaths.Any(path => !string.IsNullOrWhiteSpace(path)
                && (LockedFileInspection.ContainsPath(path, target.Path) || LockedFileInspection.ContainsPath(target.Path, path)))
            || Environment.ProcessPath is { } executable && LockedFileInspection.ContainsPath(target.Path, executable))
            throw new IOException("Нельзя удалить системную папку, профиль RustText или их содержимое.");

        if (_workspace.Windows.SelectMany(window => window.LoadedWorkspaceTabs()).Any(tab => tab.Path is { } path
                && LockedFileInspection.ContainsPath(target.Path, path)))
            throw new IOException("Файл или папка используются открытыми вкладками RustText. Сначала закройте эти вкладки.");
    }

    private async Task<bool> ConfirmRecycleAsync(string title, string path, string explanation, string? action)
    {
        var body = DialogPanel(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap });
        var dialog = Dialog(title, body, 540);
        var cancel = FocusButton(Button(action is null ? "Закрыть" : "Отмена", () => dialog.Close(false)));
        cancel.IsCancel = true;
        cancel.IsDefault = true;
        if (action is not null)
            body.Children.Add(Row(cancel, FocusButton(Button(action, () => dialog.Close(true)))));
        else
            body.Children.Add(Row(cancel));
        return await dialog.ShowDialog<bool>(this);
    }
}
