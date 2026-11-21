using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using RustText.Demo.Platform;

namespace RustText.Demo;

internal enum ShellKind { Cmd, PowerShell }

internal sealed class ShellTab : Grid, IDisposable
{
    private const int OutputLimit = 250_000;
    private readonly ShellKind _shell;
    private readonly Process _process;
    private readonly Button _runElevated = new() { Content = "От администратора", IsEnabled = false };
    private readonly TextEditor _output = new() { IsReadOnly = true, Document = new() };
    private readonly TextBox _input = new() { PlaceholderText = "Введите команду и нажмите Enter" };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _sending;
    private bool _elevating;
    private bool _sessionEnded;
    private bool _disposed;

    public ShellTab(ShellKind shell, string workingDirectory, double fontSize)
    {
        _shell = shell;
        RowDefinitions = new RowDefinitions("*,Auto");
        Margin = new Thickness(8);
        RowSpacing = 6;
        _output.FontFamily = _input.FontFamily = new FontFamily("Consolas");
        _output.FontSize = _input.FontSize = fontSize;
        _output.Document.UndoStack.SizeLimit = 0;
        Children.Add(_output);
        var commandRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        commandRow.Children.Add(_input);
        commandRow.Children.Add(_runElevated);
        SetColumn(_runElevated, 1);
        SetRow(commandRow, 1);
        Children.Add(commandRow);
        ToolTip.SetTip(_runElevated, "Выполнить одну команду в отдельном процессе с подтверждением UAC");

        _process = Start(shell, workingDirectory);
        _input.KeyDown += SendOnEnter;
        _input.TextChanged += (_, _) => UpdateElevatedButton();
        _runElevated.Click += RunElevated;
        _ = ObserveAsync();
    }

    internal static string GetExecutablePath(ShellKind shell) => shell == ShellKind.Cmd
        ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
        : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static Process Start(ShellKind shell, string workingDirectory)
    {
        var codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        var encoding = shell == ShellKind.Cmd && codePage != Encoding.UTF8.CodePage
            ? CodePagesEncodingProvider.Instance.GetEncoding(codePage)!
            : new UTF8Encoding(false);
        var info = new ProcessStartInfo
        {
            FileName = GetExecutablePath(shell),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };
        if (shell == ShellKind.Cmd)
        {
            // A portable copy can be renamed. An external host (e.g. headless tests) uses the apphost.
            info.FileName = Assembly.GetEntryAssembly() == typeof(Program).Assembly
                ? Environment.ProcessPath ?? throw new IOException("Путь RustText недоступен.")
                : Path.Combine(AppContext.BaseDirectory, "rusttext.exe");
            if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.ArgumentList.Add(CmdInputHost.Argument);
            info.ArgumentList.Add(encoding.CodePage.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            const string script = """
                [Console]::InputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                $OutputEncoding = [Console]::OutputEncoding
                $ProgressPreference = "SilentlyContinue"
                while ($true) {
                    [Console]::Write("PS $($ExecutionContext.SessionState.Path.CurrentLocation)> ")
                    $rustTextCommand = [Console]::ReadLine()
                    if ($null -eq $rustTextCommand) { break }
                    if ($rustTextCommand.Trim().Length -eq 0) { continue }
                    try { Invoke-Expression $rustTextCommand | Out-Default }
                    catch { [Console]::Error.WriteLine($_.ToString()) }
                }
                """;
            string[] args = ["-NoLogo", "-NoProfile", "-OutputFormat", "Text", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];
            foreach (var arg in args) info.ArgumentList.Add(arg);
        }
        return Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить оболочку.");
    }

    public void FocusInput() => _input.Focus();

    private async void SendOnEnter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_sending || _elevating || _disposed || !_input.IsEnabled) return;

        var command = _input.Text ?? "";
        _sending = true;
        try
        {
            _input.Text = "";
            Append(command + Environment.NewLine);
            await _process.StandardInput.WriteLineAsync(command.AsMemory(), _lifetime.Token);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (!_disposed) AppendNotice($"Не удалось отправить команду. {error.Message}");
        }
        finally { _sending = false; }
    }

    private async void RunElevated(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_disposed || _sending || _elevating || _sessionEnded
            || _input.Text is not { } command || string.IsNullOrWhiteSpace(command)) return;

        _elevating = true;
        _input.IsEnabled = false;
        UpdateElevatedButton();
        var token = _lifetime.Token;
        try
        {
            Append($"{Environment.NewLine}[Администратор] {command}{Environment.NewLine}");
            var exitCode = await ElevatedShellCommand.RunAsync(_shell, command, text => AppendOnUiThreadAsync(text, token), token);
            if (_disposed) return;

            if (exitCode is { } code)
            {
                _input.Text = "";
                AppendNotice($"Команда от администратора завершена · код {code}");
            }
            else
            {
                AppendNotice("Запуск от имени администратора отменён.");
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException
            or Win32Exception or ArgumentException or OperationCanceledException or TimeoutException)
        {
            if (!_disposed) AppendNotice($"Ошибка запуска от имени администратора. {error.Message}");
        }
        finally
        {
            _elevating = false;
            if (!_disposed)
            {
                _input.IsEnabled = !_sessionEnded;
                UpdateElevatedButton();
            }
        }
    }

    private void UpdateElevatedButton() =>
        _runElevated.IsEnabled = !_sessionEnded && !_elevating && !string.IsNullOrWhiteSpace(_input.Text);

    private async Task ObserveAsync()
    {
        var token = _lifetime.Token;
        try
        {
            await Task.WhenAll(ReadAsync(_process.StandardOutput, token), ReadAsync(_process.StandardError, token),
                _process.WaitForExitAsync(token));
            if (!_disposed) AppendNotice($"Сеанс завершён · код {_process.ExitCode}");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (!_disposed) AppendNotice($"Ошибка сеанса. {error.Message}");
        }
        finally
        {
            _sessionEnded = true;
            if (!_disposed)
            {
                _input.IsEnabled = false;
                UpdateElevatedButton();
            }
        }
    }

    private async Task ReadAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            await AppendOnUiThreadAsync(new string(buffer, 0, count), token);
        }
    }

    private async Task AppendOnUiThreadAsync(string text, CancellationToken token)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_disposed) Append(text);
        }, DispatcherPriority.Background, token);
    }

    private void AppendNotice(string message)
    {
        var document = _output.Document;
        var needsLineBreak = document.TextLength > 0 && document.GetCharAt(document.TextLength - 1) is not ('\r' or '\n');
        var prefix = needsLineBreak ? Environment.NewLine : "";
        Append($"{prefix}[Терминал] {message}{Environment.NewLine}");
    }

    private void Append(string text)
    {
        var document = _output.Document;
        document.Insert(document.TextLength, text);
        var excess = document.TextLength - OutputLimit;
        if (excess > 0)
        {
            var remove = document.GetLineByOffset(excess).Offset;
            if (remove == 0) remove = excess;
            document.Remove(0, remove);
        }
        _output.ScrollToLine(document.LineCount);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or AggregateException)
        {
            Debug.WriteLine(error);
        }
        finally
        {
            _process.Dispose();
            _lifetime.Dispose();
        }
    }
}
