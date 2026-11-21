using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RustText.Demo;

public partial class MainWindow
{
    private const int HistoryPageSize = 8;
    private readonly HashSet<HistoryKind> _collapsedHistory = [];

    private async Task ShowHomeAsync()
    {
        _home = true;
        TitlePalmExtension.IsVisible = false;
        WorkspaceHost.Background = FocusTheme.Editor;
        SetCaption(null);
        _workspace.Catalog ??= await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        _recent = await _storage.GetRecentFilesAsync(_lifetime.Token);
        if (!_home || _closed) return;

        var search = new TextBox { PlaceholderText = "Поиск по имени или пути" };
        var sections = new StackPanel { Spacing = 10 };
        var recent = HistorySection(HistoryKind.Recent, "Недавние файлы");
        var closed = HistorySection(HistoryKind.Closed, "Закрытые файлы");
        var ghosts = HistorySection(HistoryKind.Drafts, "Призрачные вкладки");
        var fileSizes = new Dictionary<string, Task<long?>>(StringComparer.OrdinalIgnoreCase);
        var draftSizes = new Dictionary<string, Task<long?>>(StringComparer.OrdinalIgnoreCase);
        var revision = 0;
        var timedRows = new List<(TextBlock Label, long? Lines, long Worked)>();
        foreach (var panel in new[] { recent, closed, ghosts })
        {
            panel.Back.Click += (_, _) => { panel.Page--; Rebuild(); };
            panel.Next.Click += (_, _) => { panel.Page++; Rebuild(); };
        }
        search.TextChanged += (_, _) =>
        {
            recent.Page = closed.Page = ghosts.Page = 0;
            Rebuild();
        };

        var head = new WrapPanel { ItemSpacing = 10, LineSpacing = 6 };
        head.Children.Add(new TextBlock { Text = "История файлов", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(FocusButton(Button("Открыть…", () => Run(() => OpenDialogAsync())), primary: true));
        head.Children.Add(FocusButton(Button("Новый документ", () => { ShowGroups(); NewTab(); })));
        if (_tabs.Count > 0) head.Children.Add(FocusButton(Button("К вкладкам", ShowGroups)));

        var surface = new Grid
        {
            MaxWidth = 900, Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 10
        };
        var scroll = new ScrollViewer { Content = sections };
        Grid.SetRow(search, 1);
        Grid.SetRow(scroll, 2);
        surface.Children.Add(head);
        surface.Children.Add(search);
        surface.Children.Add(scroll);
        WorkspaceHost.Content = surface;
        _lastHomeRefresh = DateTimeOffset.Now;
        _refreshHome = Rebuild;
        _refreshHomeTimes = () =>
        {
            foreach (var (label, lines, worked) in timedRows)
                label.Text = DescribeWork(lines, worked).TrimStart(' ', '·');
        };
        Rebuild();
        SetStatus(_workspaceLoadWarning ?? "История файлов");
        FormatText.Text = "";

        void Rebuild()
        {
            timedRows.Clear();
            var term = search.Text?.Trim() ?? "";
            var recentFiles = _recent.Where(item => Matches(item.Path)).ToArray();
            var closedFiles = _closedTabs.Where(item => item.Path is { } path && (Matches(path) || Matches(ClosedTabDisplayName(item)))).ToArray();
            var closedDrafts = _closedTabs.Where(item => item.Path is null)
                .Select(item => (Saved: item, Name: ClosedTabDisplayName(item)))
                .Where(item => Matches(item.Name)).ToArray();

            FillHistory(recent, recentFiles, item =>
            {
                var name = Path.GetFileName(item.Path);
                return HistoryRow(name, item.Path, WorkDetails(item.LineCount, item.LastWorkedMs), FileSize(item.Path),
                    () => Run(() => OpenRecentAsync(item)), "Удалить из недавних", () => Run(() => ForgetRecentFileAsync(item.Path)));
            });
            FillHistory(closed, closedFiles, item => ClosedRow(item, ClosedTabDisplayName(item), FileSize(item.Path!)));
            FillHistory(ghosts, closedDrafts, item => ClosedRow(item.Saved, item.Name, DraftSize(item.Saved)));

            var allDraftSizes = _closedTabs.Where(item => item.Path is null && item.DraftRef is not null)
                .DistinctBy(item => item.DraftRef, StringComparer.OrdinalIgnoreCase).Select(DraftSize).ToArray();
            _ = UpdateGhostSizeAsync(++revision, allDraftSizes, closedDrafts.Length);
            bool Matches(string name) => name.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        Control ClosedRow(SessionTab item, string name, Task<long?> size) => HistoryRow(name, item.Path,
            WorkDetails(item.Path is not null || Settings.ShowDraftLineCount ? item.LineCount : null, item.LastWorkedMs), size, () => Run(() => RestoreClosedAsync(item)),
            item.DraftRef is null ? "Удалить из истории" : "Удалить сохранённый черновик",
            () => Run(() => ForgetClosedTabAsync(item)));

        TextBlock WorkDetails(long? lines, long? worked)
        {
            var label = new TextBlock
            {
                Text = DescribeWork(lines, worked).TrimStart(' ', '·'), Foreground = FocusTheme.Muted, FontSize = 12,
                MaxWidth = 175, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            if (worked.HasValue) timedRows.Add((label, lines, worked.Value));
            return label;
        }

        Task<long?> FileSize(string path) => CachedSize(fileSizes, path, () => path);

        Task<long?> DraftSize(SessionTab item) => item.DraftRef is { } reference
            ? CachedSize(draftSizes, reference, () => SessionStorage.GetDraftPath(item)) : Task.FromResult<long?>(0);

        async Task UpdateGhostSizeAsync(int requestedRevision, Task<long?>[] sizes, int count)
        {
            var values = await Task.WhenAll(sizes);
            if (requestedRevision == revision)
            {
                var amount = values.Sum(value => value ?? 0);
                if (values.All(value => value is not null) || amount > 0)
                    ghosts.Summary.Text = $"{LineNumbers.Format(count)}  ·  Всего {FormatHistorySize(amount)} на диске";
            }
        }

        HistoryPanel HistorySection(HistoryKind kind, string title)
        {
            var items = new StackPanel { Spacing = 2 };
            var summary = new TextBlock { Foreground = FocusTheme.Muted, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 });
            Grid.SetColumn(summary, 1);
            header.Children.Add(summary);

            var page = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = FocusTheme.Muted };
            var back = FocusButton(new Button { Content = "Назад" });
            var next = FocusButton(new Button { Content = "Дальше" });
            var navigation = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { back, page, next }
            };
            var expander = new Expander
            {
                Header = header, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = FocusTheme.Panel, BorderBrush = FocusTheme.Border,
                IsExpanded = !_collapsedHistory.Contains(kind),
                Content = new StackPanel { Spacing = 5, Children = { items, navigation } }
            };
            AutomationProperties.SetName(expander, title);
            var panel = new HistoryPanel(items, summary, page, back, next, navigation);
            expander.PropertyChanged += (_, change) =>
            {
                if (change.Property != Expander.IsExpandedProperty) return;
                if (expander.IsExpanded) _collapsedHistory.Remove(kind);
                else _collapsedHistory.Add(kind);
            };
            sections.Children.Add(expander);
            return panel;
        }
    }

    private static void FillHistory<T>(HistoryPanel panel, IReadOnlyList<T> items, Func<T, Control> row)
    {
        var pages = Math.Max(1, (items.Count + HistoryPageSize - 1) / HistoryPageSize);
        panel.Page = Math.Clamp(panel.Page, 0, pages - 1);
        panel.Rows.Children.Clear();
        panel.Summary.Text = LineNumbers.Format(items.Count);
        if (items.Count == 0)
            panel.Rows.Children.Add(new TextBlock { Text = "Список пуст", Foreground = FocusTheme.Muted, Margin = new Thickness(8, 4) });
        foreach (var item in items.Skip(panel.Page * HistoryPageSize).Take(HistoryPageSize)) panel.Rows.Children.Add(row(item));
        panel.Navigation.IsVisible = pages > 1;
        panel.PageLabel.Text = $"{panel.Page + 1} из {pages}";
        panel.Back.IsEnabled = panel.Page > 0;
        panel.Next.IsEnabled = panel.Page + 1 < pages;
    }

    private static Control HistoryRow(string name, string? path, TextBlock meta, Task<long?> size,
        Action open, string removeLabel, Action remove)
    {
        var nameLabel = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
        var fileSize = new TextBlock { Text = "—", Foreground = FocusTheme.Muted, FontSize = 12, TextAlignment = TextAlignment.Right };
        _ = DisplaySizeAsync();
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,78"), ColumnSpacing = 10 };
        columns.Children.Add(nameLabel);
        Grid.SetColumn(meta, 1);
        columns.Children.Add(meta);
        Grid.SetColumn(fileSize, 2);
        columns.Children.Add(fileSize);
        var openButton = FocusButton(Button("", open));
        openButton.Content = columns;
        openButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        openButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(openButton, name);
        ToolTip.SetTip(openButton, path ?? name);

        var close = Button("", remove);
        close.Classes.Add("focus-icon");
        close.Content = new Avalonia.Controls.Shapes.Path
        {
            Width = 12, Height = 12, Data = Geometry.Parse("M 2,2 L 10,10 M 10,2 L 2,10"),
            Stroke = FocusTheme.Muted, StrokeThickness = 1.65, StrokeLineCap = PenLineCap.Round
        };
        AutomationProperties.SetName(close, $"{removeLabel}: {name}");
        ToolTip.SetTip(close, removeLabel);
        var result = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        result.Children.Add(openButton);
        Grid.SetColumn(close, 1);
        result.Children.Add(close);
        return result;

        async Task DisplaySizeAsync()
        {
            if (await size is { } bytes) fileSize.Text = FormatHistorySize(bytes);
        }
    }

    private static Task<long?> CachedSize(Dictionary<string, Task<long?>> cache, string key, Func<string?> path)
    {
        if (cache.TryGetValue(key, out var existing)) return existing;
        var size = Task.Run<long?>(() =>
        {
            try { return path() is { } file ? new FileInfo(file).Length : null; }
            catch (Exception error) when (IsUserError(error)) { return null; }
        });
        cache.Add(key, size);
        return size;
    }

    private static string FormatHistorySize(long bytes)
    {
        string[] units = ["Б", "КиБ", "МиБ", "ГиБ", "ТиБ", "ПиБ"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit + 1 < units.Length)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))} {units[unit]}";
    }

    private async Task ForgetRecentFileAsync(string path)
    {
        _recent = await _storage.RemoveRecentAsync(path, _lifetime.Token);
        RefreshHistoryMenus();
    }

    private async Task ForgetClosedTabAsync(SessionTab saved)
    {
        if (!_closedTabs.Any(item => item.Id == saved.Id) || !_workspace.ClosedTabOperations.Add(saved.Id)) return;
        try
        {
            if (saved.DraftRef is not null && !await ConfirmForgetDraftAsync(ClosedTabDisplayName(saved))) return;
            if (_closed || _closing) return;

            try
            {
                await SaveWorkspaceSessionAsync(excludeWindow: null, forgetClosedTab: saved);
                var stillPresent = saved.DraftRef is { } reference && !reference.StartsWith("legacy/", StringComparison.Ordinal)
                    && File.Exists(SessionStorage.GetDraftPath(saved));
                if (stillPresent) _workspace.SetLastCheckpoint(WorkspaceCacheKey, null);
                SetStatus(stillPresent ? "Запись удалена. Файл черновика пока остаётся в профиле." : "Запись удалена.");
            }
            finally { RefreshHistoryMenus(); }
        }
        finally { _workspace.ClosedTabOperations.Remove(saved.Id); }
    }

    private Task<bool> ConfirmForgetDraftAsync(string name)
    {
        var body = DialogPanel(new TextBlock { Text = $"Сохранённый текст «{name}» будет удалён.", TextWrapping = TextWrapping.Wrap });
        var dialog = Dialog("Удалить черновик?", body);
        var cancel = FocusButton(Button("Отмена", () => dialog.Close(false)));
        cancel.IsCancel = true;
        var delete = Button("Удалить", () => dialog.Close(true));
        delete.Classes.Add("focus-danger");
        body.Children.Add(Row(cancel, delete));
        return dialog.ShowDialog<bool>(this);
    }

    private enum HistoryKind { Recent, Closed, Drafts }

    private sealed record HistoryPanel(StackPanel Rows, TextBlock Summary, TextBlock PageLabel, Button Back, Button Next, StackPanel Navigation)
    {
        public int Page { get; set; }
    }
}
