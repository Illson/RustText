using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using RustText.Demo.Editor;

namespace RustText.Demo;

public partial class MainWindow
{
    private TextBox _searchInput = null!;
    private TextBox _replacement = null!;
    private CheckBox _regexOption = null!;
    private CheckBox _caseOption = null!;
    private CheckBox _wordOption = null!;
    private CheckBox _allTabs = null!;
    private CheckBox _onlyCurrent = null!;
    private StackPanel _replacementRow = null!;
    private TextBlock _searchStatus = null!;
    private ListBox _searchResults = null!;
    private CancellationTokenSource? _searchCancellation;
    private (WorkTab Tab, SearchHit Hit)? _searchFocus;
    private Regex? _highlightPattern;

    private void BuildSearchPanel()
    {
        _searchInput = new TextBox { PlaceholderText = "Найти в тексте", MinWidth = 180 };
        _replacement = new TextBox { PlaceholderText = "Заменить на", MinWidth = 180 };
        _regexOption = new CheckBox { Content = "Регулярное выражение" };
        _caseOption = new CheckBox { Content = "Учитывать регистр" };
        _wordOption = new CheckBox { Content = "Целое слово" };
        _allTabs = new CheckBox { Content = "Все вкладки" };
        _onlyCurrent = new CheckBox { Content = "Только текущее" };
        _searchResults = new ListBox { MaxHeight = 130, MinHeight = 55 };
        _searchStatus = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#C0CEDA")), VerticalAlignment = VerticalAlignment.Center };
        var searchRow = new DockPanel { LastChildFill = true };
        var commands = Row(Button("Найти", () => Run(FindAsync)), Button("↑", () => Run(() => NavigateSearchAsync(true))),
            Button("↓", () => Run(() => NavigateSearchAsync(false))),
            Button("Замена", () => { _replacementRow.IsVisible = !_replacementRow.IsVisible; }),
            Button("Отменить", () => _searchCancellation?.Cancel()), Button("×", CloseSearchPanel));
        DockPanel.SetDock(commands, Dock.Right);
        searchRow.Children.Add(commands);
        searchRow.Children.Add(_searchInput);

        _replacementRow = Row(_replacement, Button("Заменить", () => Run(() => ReplaceAsync(false))),
            Button("Заменить все", () => Run(() => ReplaceAsync(true))));
        _replacementRow.IsVisible = false;

        var layout = new StackPanel { Margin = new Thickness(12, 8), Spacing = 6 };
        layout.Children.Add(searchRow);
        layout.Children.Add(_replacementRow);
        var options = new WrapPanel { ItemSpacing = 9 };
        foreach (var option in new Control[] { _regexOption, _caseOption, _wordOption, _allTabs, _onlyCurrent, _searchStatus })
        {
            options.Children.Add(option);
        }

        layout.Children.Add(options);
        layout.Children.Add(_searchResults);
        PanelHost.Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1B2734")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3C4D5C")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = layout
        };

        _searchInput.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                Run(FindAsync);
            }
        };
        _searchInput.TextChanged += (_, _) =>
        {
            _highlightPattern = null;
            _searchFocus = null;
            RefreshSearchHighlights();
        };
        _onlyCurrent.IsCheckedChanged += (_, _) => RefreshSearchHighlights();
        _searchResults.SelectionChanged += (_, _) =>
        {
            if (_searchResults.SelectedItem is ListBoxItem { Tag: SearchResult result } && _tabs.ContainsKey(result.Tab.Id))
            {
                if (result.Tab.Revision != result.Revision)
                {
                    _searchStatus.Text = "Текст изменился. Повторите поиск.";
                    return;
                }

                _searchFocus = (result.Tab, result.Hit);
                SelectText(result.Tab, result.Hit.Offset, result.Hit.Length);
                RefreshSearchHighlights();
            }
        };
    }

    private static StackPanel Row(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }

        return panel;
    }

    private SearchOptions CurrentQuery() => new(_searchInput.Text ?? "", _regexOption.IsChecked == true,
        _caseOption.IsChecked == true, _wordOption.IsChecked == true);

    private void OpenSearchPanel(bool replace = false)
    {
        if (!replace && ActiveTab?.Log is { } log)
        {
            log.FocusSearch();
            return;
        }

        PanelHost.IsVisible = true;
        _replacementRow.IsVisible = replace;
        _searchInput.Focus();
        _searchInput.SelectAll();
    }

    private void CloseSearchPanel()
    {
        PanelHost.IsVisible = false;
        _searchCancellation?.Cancel();
        _highlightPattern = null;
        _searchFocus = null;
        RefreshSearchHighlights();
        ActiveTab?.Editor?.Focus();
    }

    private async Task FindAsync()
    {
        var query = TextOperations.BuildRegex(CurrentQuery());
        _highlightPattern = query;
        _searchCancellation?.Cancel();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCancellation = source;
        _searchResults.Items.Clear();
        var candidates = _allTabs.IsChecked == true ? _tabs.Values.ToArray() : ActiveTab is { } selected ? [selected] : [];
        var incomplete = false;
        var results = new List<SearchResult>(100);
        var total = 0L;
        _searchStatus.Text = "Поиск…";
        try
        {
            foreach (var tab in candidates)
            {
                source.Token.ThrowIfCancellationRequested();
                if (tab.Deferred?.DraftRef is not null)
                {
                    var draftPath = _storage.GetDraftPath(tab.Deferred);
                    if (draftPath is not null && new FileInfo(draftPath).Length > LineGeneratorThreshold)
                    {
                        incomplete = true;
                        continue;
                    }
                }

                if (tab.Deferred is not null && !tab.IsLog)
                {
                    try
                    {
                        await ActivateDeferredAsync(tab);
                    }
                    catch (Exception error) when (IsUserError(error) || error is OperationCanceledException)
                    {
                        incomplete = true;
                    }
                }

                if (tab.Editor is not { } editor)
                {
                    incomplete |= tab.Loading || tab.Recovery is not null;
                    continue;
                }

                var text = editor.Text;
                var revision = tab.Revision;
                var result = await Task.Run(() => TextOperations.Find(text, query, 100 - results.Count, source.Token), source.Token);
                total += result.Count;
                incomplete |= result.TimedOut;
                foreach (var hit in result.Hits)
                {
                    results.Add(new SearchResult(tab, hit, revision));
                }
            }

            source.Token.ThrowIfCancellationRequested();
            foreach (var result in results)
            {
                _searchResults.Items.Add(new ListBoxItem
                {
                    Content = $"{result.Tab.Label.Text} · {result.Hit.Line}: {result.Hit.Preview}",
                    Tag = result
                });
            }

            _searchStatus.Text = incomplete ? $"Найдено {total:N0} · результаты неполные" : $"Найдено {total:N0}";
            RefreshSearchHighlights();
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, source))
            {
                _searchCancellation = null;
            }
        }
    }

    private async Task NavigateSearchAsync(bool backward)
    {
        if (ActiveTab is not { Editor: { } editor } tab)
        {
            return;
        }

        var query = TextOperations.BuildRegex(CurrentQuery());
        _highlightPattern = query;
        var same = _searchFocus is { } focused && ReferenceEquals(focused.Tab, tab) && focused.Hit.Offset == editor.SelectionStart;
        var offset = backward ? editor.SelectionStart
            : same ? Math.Min(editor.Document.TextLength, editor.SelectionStart + Math.Max(1, editor.SelectionLength))
            : editor.CaretOffset;
        var text = editor.Text;
        var revision = tab.Revision;
        var match = await Task.Run(() => TextOperations.FindNext(text, query, offset, backward), _lifetime.Token);
        if (tab.Revision != revision || !_tabs.ContainsKey(tab.Id))
        {
            return;
        }

        if (match is null)
        {
            _searchStatus.Text = "Совпадений нет";
            return;
        }

        _searchFocus = (tab, new SearchHit(match.Index, match.Length, 0, ""));
        SelectText(tab, match.Index, match.Length);
        RefreshSearchHighlights();
    }

    private async Task ReplaceAsync(bool all)
    {
        if (ActiveTab is not { Editor: { } editor } tab)
        {
            return;
        }

        var options = CurrentQuery();
        var query = TextOperations.BuildRegex(options);
        var replacement = _replacement.Text ?? "";
        if (!all)
        {
            var offset = editor.SelectionStart;
            var selection = editor.SelectedText;
            var match = query.Match(editor.Text, offset);
            if (match.Success && match.Index == offset && match.Length == selection.Length)
            {
                editor.Document.Replace(offset, selection.Length, options.IsRegex ? match.Result(replacement) : replacement);
                editor.CaretOffset = Math.Min(editor.Document.TextLength, offset + replacement.Length);
            }

            await NavigateSearchAsync(false);
            return;
        }

        var source = editor.Text;
        var revision = tab.Revision;
        var result = await Task.Run(() => TextOperations.ReplaceAll(source, query, replacement, options.IsRegex), _lifetime.Token);
        if (tab.Revision != revision || !_tabs.ContainsKey(tab.Id))
        {
            throw new InvalidOperationException("Текст изменился во время замены.");
        }

        if (result.Count > 0)
        {
            editor.Document.Replace(0, editor.Document.TextLength, result.Text);
        }

        _searchStatus.Text = $"Заменено {result.Count:N0}";
    }

    private void RefreshSearchHighlights()
    {
        foreach (var tab in _tabs.Values)
        {
            if (tab.Highlights is not { } highlights || tab.Editor is not { } editor)
            {
                continue;
            }

            highlights.Pattern = PanelHost.IsVisible && (ReferenceEquals(tab, ActiveTab) || _allTabs.IsChecked == true)
                ? _highlightPattern : null;
            highlights.OnlyCurrent = _onlyCurrent.IsChecked == true;
            highlights.Current = PanelHost.IsVisible && _searchFocus is { } active && ReferenceEquals(active.Tab, tab)
                ? (active.Hit.Offset, active.Hit.Length) : null;
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }
    }

    private sealed record SearchResult(WorkTab Tab, SearchHit Hit, long Revision);
}
