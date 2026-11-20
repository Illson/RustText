using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RustText.Demo.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private async Task<bool> ShowUpdateDialogAsync(UpdateRelease? release,
        Func<CancellationToken, Task<IReadOnlyList<ReleaseNotes>>>? fetchHistory = null)
    {
        using var historyLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        fetchHistory ??= UpdateService.GetReleaseHistoryAsync;
        var notesPanel = new StackPanel { Spacing = 9 };
        var historyPanel = new StackPanel { Spacing = 4 };
        var notesScroll = new ScrollViewer
        {
            Content = notesPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var historyScroll = new ScrollViewer
        {
            Content = historyPanel,
            IsVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var content = new Grid();
        content.Children.Add(notesScroll);
        content.Children.Add(historyScroll);

        var dialog = new Window
        {
            Title = release is null ? "История версий RustText" : "Обновление RustText",
            Width = 680,
            Height = 660,
            MinWidth = 420,
            MinHeight = 420,
            MaxWidth = 900,
            MaxHeight = 820,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = FocusTheme.Panel,
            Foreground = FocusTheme.Text,
            CanResize = true
        };
        var latestButton = new Button { Content = "Что нового", IsVisible = release is not null };
        var historyButton = new Button { Content = "История версий", IsVisible = release is not null };
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Margin = new Thickness(18, 5, 18, 10)
        };
        tabs.Children.Add(latestButton);
        tabs.Children.Add(historyButton);
        var hint = new TextBlock
        {
            Text = release is null ? null : "Сессия открытых окон сохранится перед перезапуском.",
            Foreground = FocusTheme.Muted,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(18, 9, 18, 15)
        };
        footer.Children.Add(hint);
        var laterButton = new Button { Content = release is null ? "Закрыть" : "Позже", MinWidth = 90 };
        laterButton.Classes.Add("focus-quiet");
        laterButton.Click += (_, _) => dialog.Close(false);
        Grid.SetColumn(laterButton, 1);
        footer.Children.Add(laterButton);
        if (release is not null)
        {
            var updateButton = new Button { Content = "Скачать и перезапустить", MinWidth = 190 };
            updateButton.Classes.Add("focus-primary");
            updateButton.Click += (_, _) =>
            {
                dialog.Close(true);
            };
            Grid.SetColumn(updateButton, 2);
            footer.Children.Add(updateButton);
        }

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var heading = BuildUpdateHeading(release);
        layout.Children.Add(heading);
        Grid.SetRow(tabs, 1);
        layout.Children.Add(tabs);
        Grid.SetRow(content, 2);
        layout.Children.Add(content);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);
        dialog.Content = new Border { Background = FocusTheme.Panel, Child = layout };
        DialogWindowChrome.Apply(dialog);

        var historyLoaded = false;
        var historyLoading = false;
        async Task LoadHistoryAsync()
        {
            if (historyLoaded || historyLoading)
                return;

            historyLoading = true;
            historyButton.IsEnabled = false;
            historyPanel.Children.Clear();
            historyPanel.Children.Add(MutedText("Загружаются опубликованные версии."));
            try
            {
                var history = await fetchHistory(historyLifetime.Token);
                historyLifetime.Token.ThrowIfCancellationRequested();
                historyPanel.Children.Clear();
                for (var index = 0; index < history.Count; index++)
                    historyPanel.Children.Add(BuildReleaseHistoryItem(history[index], index == 0));
                if (history.Count == 0)
                    historyPanel.Children.Add(MutedText("Опубликованных выпусков пока нет."));
                historyLoaded = true;
            }
            catch (OperationCanceledException) when (historyLifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (IsUserError(exception) || exception is OperationCanceledException)
            {
                historyPanel.Children.Clear();
                historyPanel.Children.Add(new TextBlock
                {
                    Text = "Не удалось загрузить историю выпусков. " + exception.Message,
                    Foreground = FocusTheme.Warning,
                    TextWrapping = TextWrapping.Wrap
                });
                var retryButton = new Button { Content = "Повторить" };
                retryButton.Classes.Add("focus-quiet");
                retryButton.Click += async (_, _) => await LoadHistoryAsync();
                historyPanel.Children.Add(retryButton);
            }
            finally
            {
                historyLoading = false;
                historyButton.IsEnabled = true;
            }
        }

        void SelectHistory(bool show)
        {
            historyScroll.IsVisible = show;
            notesScroll.IsVisible = !show;
            SelectTab(latestButton, !show);
            SelectTab(historyButton, show);
        }

        latestButton.Click += (_, _) => SelectHistory(show: false);
        historyButton.Click += async (_, _) =>
        {
            SelectHistory(show: true);
            await LoadHistoryAsync();
        };

        if (release is null)
        {
            SelectHistory(show: true);
            dialog.Opened += async (_, _) => await LoadHistoryAsync();
        }
        else
        {
            notesPanel.Children.Add(BuildReleaseNotes(release.Version, release.Notes));
            notesPanel.Children.Add(ReleasePageButton(release.ReleasePageUri));
        }

        dialog.Opened += (_, _) => (release is null ? laterButton : latestButton).Focus();
        dialog.Closed += (_, _) => historyLifetime.Cancel();
        return await dialog.ShowDialog<bool>(this);
    }

    private static Control BuildUpdateHeading(UpdateRelease? release)
    {
        var title = new TextBlock
        {
            Text = release is null ? "История выпусков RustText" : "Доступна новая версия",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            Foreground = FocusTheme.Text
        };
        var subtitle = new TextBlock
        {
            Text = release is null ? "Опубликованные версии и их изменения" : "RustText · Windows x64",
            Foreground = FocusTheme.Muted
        };
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(title);
        stack.Children.Add(subtitle);
        if (release is not null)
        {
            var route = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
            route.Children.Add(VersionCell("Установлена", CurrentUpdateVersion));
            var arrow = new TextBlock { Text = "→", Foreground = FocusTheme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
            Grid.SetColumn(arrow, 1);
            route.Children.Add(arrow);
            var available = VersionCell("Доступна", release.Version, true);
            Grid.SetColumn(available, 2);
            route.Children.Add(available);
            stack.Children.Add(route);
        }

        return new Border
        {
            Background = FocusTheme.Chrome,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 14),
            Child = stack
        };
    }

    private static Control VersionCell(string label, string version, bool accent = false)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(10, 7) };
        stack.Children.Add(new TextBlock { Text = label, Foreground = FocusTheme.Muted, FontSize = 12 });
        stack.Children.Add(new TextBlock
        {
            Text = version,
            Foreground = accent ? FocusTheme.Warning : FocusTheme.Text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 18
        });
        return new Border
        {
            Background = FocusTheme.Field,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = stack
        };
    }

    private static Control BuildReleaseNotes(string version, string? markdown)
    {
        var panel = new StackPanel { Spacing = 9, Margin = new Thickness(18, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = "Что нового в " + version,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = FocusTheme.Text
        });
        var rendered = RenderMarkdown(markdown);
        if (rendered.Children.Count == 0)
            rendered.Children.Add(MutedText("Описание изменений пока не опубликовано."));
        panel.Children.Add(rendered);
        return panel;
    }

    private static Control BuildReleaseHistoryItem(ReleaseNotes release, bool expanded)
    {
        var expander = new Expander
        {
            Header = new TextBlock
            {
                Text = release.Version + "    " + ReleaseSummary(release.Body),
                Foreground = FocusTheme.Text,
                TextTrimming = TextTrimming.CharacterEllipsis
            },
            IsExpanded = expanded
        };
        if (expanded)
            Populate();
        else
            expander.Expanded += (_, _) => Populate();

        void Populate()
        {
            if (expander.Content is not null)
                return;
            var content = new StackPanel { Spacing = 8, Margin = new Thickness(12, 2, 12, 10) };
            var formatted = RenderMarkdown(release.Body);
            if (formatted.Children.Count == 0)
                formatted.Children.Add(MutedText("Описание изменений пока не опубликовано."));
            content.Children.Add(formatted);
            content.Children.Add(ReleasePageButton(release.PageUri));
            expander.Content = content;
        }
        expander.Classes.Add("focus-history-expander");
        return new Border
        {
            Background = FocusTheme.Chrome,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 2),
            Child = expander
        };
    }

    private static string ReleaseSummary(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "Выпуск";
        var line = markdown.Split('\n')
            .Select(value => value.Trim().TrimStart('#', '-', '*', ' '))
            .FirstOrDefault(value => value.Length > 0) ?? "Выпуск";
        return line.Length > 70 ? line[..67] + "…" : line;
    }

    private static StackPanel RenderMarkdown(string? markdown)
    {
        var panel = new StackPanel { Spacing = 5 };
        if (string.IsNullOrWhiteSpace(markdown))
            return panel;

        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            panel.Children.Add(MarkdownLine(string.Join(" ", paragraph), 13));
            paragraph.Clear();
        }

        foreach (var source in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = source.Trim();
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }
            var hashes = line.TakeWhile(character => character == '#').Count();
            if (hashes is > 0 and <= 4 && line.Length > hashes && line[hashes] == ' ')
            {
                FlushParagraph();
                var heading = MarkdownLine(line[(hashes + 1)..].Trim(), hashes == 1 ? 17 : 15);
                heading.Foreground = FocusTheme.Text;
                heading.FontWeight = FontWeight.SemiBold;
                heading.Margin = new Thickness(0, 6, 0, 0);
                panel.Children.Add(heading);
                continue;
            }
            var marker = line.IndexOf(". ", StringComparison.Ordinal);
            var ordered = marker is > 0 and < 6 && line[..marker].All(char.IsDigit);
            var unordered = line.StartsWith("- ", StringComparison.Ordinal)
                || line.StartsWith("* ", StringComparison.Ordinal)
                || line.StartsWith("+ ", StringComparison.Ordinal);
            if (ordered || unordered)
            {
                FlushParagraph();
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 7, Margin = new Thickness(5, 0, 0, 0) };
                row.Children.Add(new TextBlock { Text = ordered ? line[..(marker + 1)] : "•", Foreground = FocusTheme.Accent });
                var item = MarkdownLine(line[(ordered ? marker + 2 : 2)..].Trim(), 13);
                Grid.SetColumn(item, 1);
                row.Children.Add(item);
                panel.Children.Add(row);
                continue;
            }
            if (line.StartsWith(new string('\u0060', 3), StringComparison.Ordinal))
            {
                FlushParagraph();
                continue;
            }
            paragraph.Add(line);
        }
        FlushParagraph();
        return panel;
    }

    private static TextBlock MarkdownLine(string text, double fontSize)
    {
        var block = new TextBlock { FontSize = fontSize, Foreground = FocusTheme.Muted, TextWrapping = TextWrapping.Wrap };
        while (text.Length > 0)
        {
            var code = text.IndexOf('\u0060');
            var bold = text.IndexOf("**", StringComparison.Ordinal);
            var link = text.IndexOf('[', StringComparison.Ordinal);
            var next = new[] { code, bold, link }.Where(index => index >= 0).DefaultIfEmpty(-1).Min();
            if (next < 0)
            {
                block.Inlines?.Add(new Run(text));
                break;
            }
            if (next > 0)
            {
                block.Inlines?.Add(new Run(text[..next]));
                text = text[next..];
                continue;
            }
            if (text.StartsWith("**", StringComparison.Ordinal))
            {
                var end = text.IndexOf("**", 2, StringComparison.Ordinal);
                if (end > 2)
                {
                    block.Inlines?.Add(new Run(text[2..end]) { FontWeight = FontWeight.SemiBold, Foreground = FocusTheme.Text });
                    text = text[(end + 2)..];
                    continue;
                }
            }
            else if (text[0] == '\u0060')
            {
                var end = text.IndexOf('\u0060', 1);
                if (end > 1)
                {
                    block.Inlines?.Add(new Run(text[1..end]) { FontFamily = new FontFamily("Consolas"), Foreground = FocusTheme.Text });
                    text = text[(end + 1)..];
                    continue;
                }
            }
            else if (text[0] == '[')
            {
                var labelEnd = text.IndexOf("](", StringComparison.Ordinal);
                var urlEnd = labelEnd < 0 ? -1 : text.IndexOf(')', labelEnd + 2);
                if (labelEnd > 0 && urlEnd > labelEnd)
                {
                    block.Inlines?.Add(new Run(text[1..labelEnd]) { Foreground = FocusTheme.Accent });
                    text = text[(urlEnd + 1)..];
                    continue;
                }
            }
            block.Inlines?.Add(new Run(text[..1]));
            text = text[1..];
        }
        return block;
    }

    private static Button ReleasePageButton(Uri? uri)
    {
        var button = new Button { Content = "Открыть выпуск на GitHub", HorizontalAlignment = HorizontalAlignment.Left };
        button.Classes.Add("focus-quiet");
        button.Click += (_, _) =>
        {
            if (uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/Illson/RustText/releases/", StringComparison.Ordinal))
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };
        return button;
    }

    private static TextBlock MutedText(string text) =>
        new() { Text = text, Foreground = FocusTheme.Muted, TextWrapping = TextWrapping.Wrap };

    private static void SelectTab(Button button, bool selected)
    {
        button.Classes.Clear();
        button.Classes.Add(selected ? "focus-primary" : "focus-quiet");
    }
}
