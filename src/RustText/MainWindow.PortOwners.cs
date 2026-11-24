using Avalonia.Controls;
using Avalonia.Threading;

namespace RustText.Demo;

public partial class MainWindow
{
    private void ShowPortOwners()
    {
        if (_closing || _closed || _workspaceSwitchPending || !OperatingSystem.IsWindows()) return;
        if (!_restoreFinished)
        {
            SetStatus("Дождитесь открытия рабочего пространства.");
            return;
        }

        var tab = _tabs.Values.FirstOrDefault(tab => tab.PortOwners is not null);
        if (tab is null)
        {
            tab = AddTab(select: false);
            tab.CustomTitle = "Процесс по порту";
            tab.Item.Content = new PortOwnersView();
            tab.Item.ContextMenu = new ContextMenu
            {
                Items = { Command("Закрыть", () => Run(() => CloseTabAsync(tab))) }
            };
            UpdateTab(tab);
        }

        Select(tab);
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(ActiveTab, tab)) tab.PortOwners?.FocusInput();
        });
    }
}
