using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RustText.Demo;

public partial class MainWindow
{
    private const string DefaultWorkspaceSymbol = "›";
    private const string EmptyWorkspaceSymbol = "○";
    private static readonly (string Name, string Hex)[] WorkspaceColors =
    [
        ("Синий", "#75A9FF"), ("Бирюзовый", "#60D4CD"), ("Зелёный", "#8AD196"),
        ("Золотой", "#F1BF69"), ("Розовый", "#E88EB9"), ("Сиреневый", "#BAA0F3"),
        ("Красный", "#F18A83"), ("Серый", "#A5ABB6")
    ];
    private static readonly string[] WorkspaceSymbols = ["◆", "●", "▲", "★", "✦", "⚙", "▣"];

    private WorkspaceEntry? CurrentWorkspaceEntry() => _startEmpty ? null
        : _workspace.Catalog?.Workspaces.FirstOrDefault(workspace => workspace.Id == _workspaceId);

    private void InitializeWorkspaceMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => PopulateWorkspaceMenu(flyout);
        WorkspaceMenuButton.Flyout = flyout;
        RefreshWorkspaceButton();
    }

    private void RefreshWorkspaceButton()
    {
        var workspace = CurrentWorkspaceEntry();
        WorkspaceBadgeSymbol.Text = _startEmpty ? EmptyWorkspaceSymbol : workspace?.Symbol ?? DefaultWorkspaceSymbol;
        WorkspaceBadgeSymbol.Foreground = workspace?.Symbol is null ? FocusTheme.AppIconForeground : FocusTheme.Text;
        WorkspaceBadge.BorderBrush = WorkspaceOutline(workspace?.Color);
        WorkspaceMenuButton.IsEnabled = _restoreFinished;
        var name = _startEmpty ? "Пустое окно" : workspace?.Name ?? "По умолчанию";
        var label = $"Рабочие пространства · {name}";
        AutomationProperties.SetName(WorkspaceMenuButton, label);
        ToolTip.SetTip(WorkspaceMenuButton, label);
    }

    private void PopulateWorkspaceMenu(MenuFlyout flyout)
    {
        RefreshWorkspaceButton();
        flyout.Items.Clear();
        foreach (var workspace in _workspace.Catalog?.Workspaces ?? [])
        {
            var current = !_startEmpty && workspace.Id == _workspaceId;
            var item = Command(workspace.Name, () => Run(() => SwitchWorkspaceAsync(workspace.Id)));
            item.Header = new TextBlock
            {
                Text = current ? workspace.Name + " · открыто" : workspace.Name,
                MaxWidth = 290, TextTrimming = TextTrimming.CharacterEllipsis
            };
            item.Icon = CreateWorkspaceBadge(workspace.Symbol ?? DefaultWorkspaceSymbol, workspace.Color);
            item.IsEnabled = !current;
            flyout.Items.Add(item);
        }
        if (_windowId == WorkspaceCatalogStore.PrimaryWindowId)
        {
            var empty = Command(_startEmpty ? "Пустое окно · открыто" : "Пустое окно", () => Run(EnterEmptyStartupAsync));
            empty.Icon = CreateWorkspaceBadge(EmptyWorkspaceSymbol, null);
            empty.IsEnabled = !_startEmpty;
            flyout.Items.Add(empty);
        }
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Command("Новая рабочая область…", () => Run(ShowCreateWorkspaceAsync)));
        flyout.Items.Add(Command("Управление…", () => Run(ShowWorkspacesAsync)));
    }

    private async Task ShowCreateWorkspaceAsync()
    {
        var name = new TextBox { MaxLength = 80, PlaceholderText = "Например, Разбор логов" };
        var color = new ComboBox
        {
            ItemsSource = WorkspaceColors.Select(choice => choice.Name).ToArray(), SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var symbol = new TextBox { Text = WorkspaceSymbols[0], Width = 52, MaxLength = 32, HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(name, "WorkspaceNameInput");
        AutomationProperties.SetAutomationId(color, "WorkspaceColorInput");
        AutomationProperties.SetAutomationId(symbol, "WorkspaceSymbolInput");
        AutomationProperties.SetName(name, "Имя рабочей области");
        AutomationProperties.SetName(color, "Цвет рамки");
        AutomationProperties.SetName(symbol, "Символ рабочего пространства");

        var symbols = new WrapPanel { ItemSpacing = 3, LineSpacing = 4 };
        symbols.Children.Add(symbol);
        foreach (var choice in WorkspaceSymbols)
        {
            var button = FocusButton(Button(choice, () => symbol.Text = choice));
            button.Padding = new Thickness(5, 2);
            symbols.Children.Add(button);
        }

        var fields = new StackPanel
        {
            Spacing = 5,
            Children = { new TextBlock { Text = "Цвет рамки" }, color, new TextBlock { Text = "Символ" }, symbols }
        };
        var badge = CreateWorkspaceBadge(WorkspaceSymbols[0], WorkspaceColors[0].Hex, size: 48);
        var previewSymbol = (TextBlock)badge.Child!;
        var presentation = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        presentation.Children.Add(fields);
        Grid.SetColumn(badge, 1);
        presentation.Children.Add(badge);
        color.SelectionChanged += (_, _) => badge.BorderBrush = WorkspaceOutline(SelectedColor());
        symbol.TextChanged += (_, _) => previewSymbol.Text = symbol.Text;

        var error = new TextBlock { Foreground = FocusTheme.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var body = DialogPanel(new TextBlock { Text = "Имя" }, name, presentation, error);
        var dialog = Dialog("Новая рабочая область", body, 420);
        var create = FocusButton(new Button { Content = "Создать", IsDefault = true }, primary: true);
        var cancel = FocusButton(new Button { Content = "Отмена", IsCancel = true });
        create.Click += async (_, _) =>
        {
            create.IsEnabled = cancel.IsEnabled = false;
            error.IsVisible = false;
            try
            {
                var glyph = WorkspaceCatalogStore.ValidateSymbol(symbol.Text ?? "");
                var created = await _workspace.CatalogStore.CreateAsync(name.Text ?? "", WorkspaceThemeKey, _lifetime.Token,
                    symbol: glyph, color: SelectedColor());
                if (_workspace.Catalog is { } catalog)
                    _workspace.Catalog = catalog with { Workspaces = [.. catalog.Workspaces, created] };
                dialog.Close(created);
            }
            catch (Exception exception) when (IsUserError(exception))
            {
                error.Text = exception.Message;
                error.IsVisible = true;
            }
            finally { create.IsEnabled = cancel.IsEnabled = true; }
        };
        cancel.Click += (_, _) => dialog.Close();
        body.Children.Add(Row(create, cancel));
        dialog.Opened += (_, _) => name.Focus();
        if (await dialog.ShowDialog<WorkspaceEntry?>(this) is { } result)
            await SwitchWorkspaceAsync(result.Id);

        string SelectedColor() => WorkspaceColors[Math.Max(0, color.SelectedIndex)].Hex;
    }

    private static Border CreateWorkspaceBadge(string symbol, string? color, int size = 23) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(1.5), BorderBrush = WorkspaceOutline(color), Background = FocusTheme.AppIconBackground,
        Child = new TextBlock
        {
            Text = symbol, Foreground = color is null ? FocusTheme.AppIconForeground : FocusTheme.Text,
            FontSize = size * .57, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static IBrush WorkspaceOutline(string? color) => color is not null && Color.TryParse(color, out var parsed)
        ? new SolidColorBrush(parsed) : FocusTheme.AppIconBorder;
}
