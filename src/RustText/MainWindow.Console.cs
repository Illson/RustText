using System.Diagnostics;
using System.Globalization;
using RustText.Demo.Editor;
using RustText.Demo.Logs;
using RustText.Demo.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private Task OpenShellAsync(ShellKind shell)
    {
        if (_closing || _closed || _workspaceSwitchPending) return Task.CompletedTask;
        if (!_restoreFinished)
        {
            SetStatus("Дождитесь открытия рабочего пространства.");
            return Task.CompletedTask;
        }
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Терминал доступен в Windows.");

        var folder = ActiveTab?.Path is { } path ? Path.GetDirectoryName(path) : null;
        if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var terminal = new ShellTab(shell, folder!, _fontSize);
        var tab = AddTab(select: false);
        tab.Shell = terminal;
        tab.CustomTitle = shell == ShellKind.Cmd ? "CMD" : "PowerShell";
        tab.Item.Content = terminal;
        UpdateTab(tab);
        Select(tab);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(ActiveTab, tab)) terminal.FocusInput();
        });
        return Task.CompletedTask;
    }

    private async Task OpenConsoleAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            SetStatus("Подключение к консоли доступно в Windows.");
            return;
        }
        var input = await PromptAsync("Подключиться к консоли", "Имя процесса или PID в классической консоли Windows");
        if (string.IsNullOrWhiteSpace(input)) return;
        var query = input.Trim();
        int processId;
        if (!int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out processId))
        {
            var name = query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? query[..^4] : query;
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length == 0) throw new InvalidOperationException("Процесс с этим именем не найден.");
                if (processes.Length == 1) processId = processes[0].Id;
                else
                {
                    var ids = processes.Select(process => process.Id).Order().ToArray();
                    var shown = string.Join(", ", ids.Take(12)) + (ids.Length > 12 ? "…" : "");
                    var pid = await PromptAsync("Выбрать процесс", $"Укажите PID процесса {name}. Найдены {shown}.");
                    if (pid is null) return;
                    if (!int.TryParse(pid.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out processId)
                        || !ids.Contains(processId))
                        throw new InvalidOperationException("Введите PID одного из найденных процессов.");
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        if (processId <= 0 || processId == Environment.ProcessId)
            throw new InvalidOperationException("Укажите PID консольной программы.");
        using var target = Process.GetProcessById(processId);
        var title = $"{target.ProcessName} ({processId})";
        var storage = SessionStorage;
        SetStatus($"Подключение к консоли {title}…");
        var capture = await ConsoleCapture.ConnectAsync(processId, title, _lifetime.Token);
        if (_closing || _closed || _workspaceSwitchPending || !ReferenceEquals(storage, SessionStorage))
        {
            capture.Dispose();
            return;
        }

        var tab = AddTab(select: false);
        tab.CustomTitle = title;
        try
        {
            AttachLog(tab, capture.Path, TextEncodingKind.Utf8, new LogTabState(FollowEnd: true), console: capture);
            Select(tab);
        }
        catch
        {
            RemoveTab(tab);
            capture.Dispose();
            throw;
        }

        var log = tab.Log;
        if (log is null) return;
        var error = await capture.Completion;
        if (error is null && capture.IsDisposed) return;
        var status = error is null
            ? "Подключённый процесс завершил работу."
            : $"Подключение к консоли завершено с ошибкой. {error.Message}";
        log.ReportConsoleDisconnected(status);
    }
}
