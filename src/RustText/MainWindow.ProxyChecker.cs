using Avalonia.Controls;

namespace RustText.Demo;

public partial class MainWindow
{
    private void ShowProxyChecker()
    {
        if (_closing || _closed || _workspaceSwitchPending) return;
        if (!_restoreFinished)
        {
            SetStatus("Дождитесь открытия рабочего пространства.");
            return;
        }

        if (_tabs.Values.FirstOrDefault(tab => tab.ProxyChecker is not null) is { } existing)
        {
            Select(existing);
            return;
        }

        var view = new ProxyCheckerView();
        var tab = AddTab(select: false);
        tab.CustomTitle = "Прокси-чекер";
        tab.Item.Content = view;
        tab.Item.ContextMenu = new ContextMenu
        {
            Items = { Command("Закрыть", () => Run(() => CloseTabAsync(tab))) }
        };
        UpdateTab(tab);
        Select(tab);
    }
}
