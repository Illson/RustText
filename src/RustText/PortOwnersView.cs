using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RustText.Demo.Platform;

namespace RustText.Demo;

internal sealed class PortOwnersView : UserControl, IDisposable
{
    private const string PortHint = "Укажите локальный порт от 1 до 65535.";
    private readonly TextBox _port = new() { PlaceholderText = "Например, 8080", Width = 140 };
    private readonly Button _find = new() { Content = "Найти", Classes = { "focus-primary" } };
    private readonly Button _stop = new() { Content = "Завершить процесс…", IsEnabled = false };
    private readonly TextBlock _status = new() { Text = PortHint, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _confirmation = new() { IsVisible = false, Spacing = 6 };
    private readonly ListBox _results = new()
    {
        ItemTemplate = new FuncDataTemplate<PortOwners.Match>((match, _) => match is null ? null : new TextBlock
        {
            Text = $"{match.Owner.Label}   ·   {match.Binding.Protocol} {match.Binding.Local}"
                + (match.Binding.State.Length == 0 ? "" : $"   ·   {match.Binding.State}")
                + (match.Owner.CanClose ? "" : "   ·   Завершение недоступно"),
            TextWrapping = TextWrapping.Wrap
        })
    };
    private readonly CancellationTokenSource _lifetime = new();
    private PortOwners.Match? _pending;
    private bool _busy;

    public PortOwnersView()
    {
        AutomationProperties.SetName(_port, "Номер локального порта");
        AutomationProperties.SetName(_results, "Процессы, использующие порт");
        var cancel = new Button { Content = "Отмена" };
        var confirm = new Button { Content = "Завершить" };
        _confirmation.Children.Add(_warning);
        _confirmation.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, confirm }
        });
        var header = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { _port, _find, _stop } },
                _status, _confirmation
            }
        };
        DockPanel.SetDock(header, Dock.Top);
        Content = new DockPanel { Margin = new Thickness(12), Children = { header, _results } };

        _port.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            _results.ItemsSource = null;
            _status.Text = PortHint;
        };
        _port.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SearchAsync();
        };
        _find.Click += async (_, _) => await SearchAsync();
        _results.SelectionChanged += (_, _) => UpdateActions();
        _stop.Click += (_, _) =>
        {
            if (!_stop.IsEnabled || _results.SelectedItem is not PortOwners.Match match) return;
            _pending = match;
            _warning.Text = $"Принудительно завершить {match.Owner.Label}? "
                + "Закроется весь процесс, а не только порт. Несохранённые данные могут быть потеряны.";
            UpdateActions();
            cancel.Focus();
        };
        cancel.Click += (_, _) => CancelConfirmation();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _pending is null) return;
            e.Handled = true;
            CancelConfirmation();
        };
        confirm.Click += async (_, _) =>
        {
            if (!OperatingSystem.IsWindows() || _pending is not { } match) return;
            _pending = null;
            await RunAsync("Проверка и завершение процесса…", async token =>
            {
                await Task.Run(() => PortOwners.StopAsync(match, token), token);
                await RefreshAsync(match.Binding.Local.Port, token, $"Процесс с PID {match.Owner.Id} завершён. ");
            });
        };
    }

    public void FocusInput() => _port.Focus();

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void CancelConfirmation()
    {
        _pending = null;
        UpdateActions();
        _stop.Focus();
    }

    private void UpdateActions()
    {
        _confirmation.IsVisible = _pending is not null;
        _port.IsEnabled = _find.IsEnabled = _results.IsEnabled = !_busy && _pending is null;
        _stop.IsEnabled = _find.IsEnabled && _results.SelectedItem is PortOwners.Match { Owner.CanClose: true };
    }

    private Task SearchAsync()
    {
        if (!OperatingSystem.IsWindows() || !_find.IsEnabled) return Task.CompletedTask;
        if (!int.TryParse(_port.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            _status.Text = "Неверный номер порта. " + PortHint;
            return Task.CompletedTask;
        }
        return RunAsync($"Поиск по порту {port}…", token => RefreshAsync(port, token));
    }

    [SupportedOSPlatform("windows")]
    private async Task RefreshAsync(int port, CancellationToken token, string prefix = "")
    {
        var matches = await Task.Run(() => PortOwners.FindAsync(port, token), token);
        token.ThrowIfCancellationRequested();
        _results.ItemsSource = matches;
        _status.Text = prefix + (matches.Length == 0 ? $"Порт {port} не найден в списке подключений." : $"Записей для порта {port}: {matches.Length}.");
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> action)
    {
        var token = _lifetime.Token;
        _busy = true;
        _results.ItemsSource = null;
        _status.Text = status;
        UpdateActions();
        try { await action(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or Win32Exception)
        {
            _status.Text = error.Message;
        }
        finally
        {
            _busy = false;
            if (!token.IsCancellationRequested) UpdateActions();
        }
    }
}
