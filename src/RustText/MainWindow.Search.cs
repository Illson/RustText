using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using AvaloniaEdit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using RustText.Demo.Editor;

namespace RustText.Demo;

public partial class MainWindow
{
    private TextBox _searchInput = null!;
    private TextBox _replacement = null!;
    private ToggleButton _regexOption = null!;
    private ToggleButton _caseOption = null!;
    private ToggleButton _wordOption = null!;
    private CheckBox _allTabs = null!;
    private CheckBox _onlyCurrent = null!;
    private ToggleButton _replaceMode = null!;
    private Button _findAll = null!;
    private Button _cancelSearch = null!;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private WorkTab? _searchTab;
    private TextBlock _searchStatus = null!;
    private ListBox _searchResults = null!;
    private CancellationTokenSource? _searchCancellation;
    private (WorkTab Tab, SearchHit Hit)? _searchFocus;
    private Regex? _highlightPattern;

    private void BuildSearchPanel()
    {
        _searchInput = new TextBox { PlaceholderText = "Найти в тексте" };
        _replacement = new TextBox { PlaceholderText = "Заменить на" };
        SearchBar.StyleInput(_searchInput);
        SearchBar.StyleInput(_replacement);
        _regexOption = SearchBar.Option(".*", "Регулярное выражение");
        _caseOption = SearchBar.Option("Aa", "Учитывать регистр");
        _wordOption = SearchBar.Option("«»", "Целое слово");
        _allTabs = new CheckBox { Content = "Все вкладки", FontSize = 12 };
        _onlyCurrent = new CheckBox { Content = "Только текущее", FontSize = 12 };
        _searchResults = new ListBox { MaxHeight = 112 };
        _searchStatus = SearchBar.Status();

        var rows = SearchBar.MakeRows();
        var find = SearchBar.Command("Найти", () => Run(FindAsync), primary: true);
        _findAll = SearchBar.Command("Найти все", () => Run(() => FindMatchesAsync(selectAll: true)),
            description: "Выделить все совпадения в текущем документе");
        SearchBar.AddRow(rows, 0, SearchBar.Options(_regexOption, _caseOption, _wordOption),
            _searchInput, find, _findAll, SearchBar.Icon("×", "Закрыть поиск (Esc)", CloseSearchPanel));

        var replace = SearchBar.Command("Заменить", () => Run(() => ReplaceAsync(false)));
        var replaceAll = SearchBar.Command("Заменить все", () => Run(() => ReplaceAsync(true)));
        var replaceLabel = new TextBlock
        {
            Text = "Замена",
            Foreground = FocusTheme.Muted,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        SearchBar.AddRow(rows, 1, replaceLabel, _replacement, replace, replaceAll);
        _replaceMode = SearchBar.Option("Замена", "Показать или скрыть поля замены (Ctrl+H)");
        _replaceMode.IsCheckedChanged += (_, _) => SetReplacementVisible();
        SetReplacementVisible();

        _cancelSearch = SearchBar.Icon("■", "Остановить поиск", CancelSearch);
        _cancelSearch.IsVisible = false;
        var footer = SearchBar.Footer(_searchStatus,
            _allTabs, _onlyCurrent, _replaceMode, _cancelSearch,
            SearchBar.Icon("↑", "Предыдущее совпадение (Shift+F3)", () => Run(() => NavigateSearchAsync(true))),
            SearchBar.Icon("↓", "Следующее совпадение (F3)", () => Run(() => NavigateSearchAsync(false))),
            SearchBar.ResultsToggle(_searchResults));
        PanelHost.Content = new Border
        {
            Background = FocusTheme.Panel,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 5),
            Child = new StackPanel { Spacing = 2, Children = { rows, footer, _searchResults } },
        };

        _searchInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Run(FindAsync);
        };
        _searchInput.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) ScheduleSearch();
        };
        _regexOption.IsCheckedChanged += (_, _) => ScheduleSearch();
        _caseOption.IsCheckedChanged += (_, _) => ScheduleSearch();
        _wordOption.IsCheckedChanged += (_, _) => ScheduleSearch();
        _allTabs.IsCheckedChanged += (_, _) => ScheduleSearch();
        _onlyCurrent.IsCheckedChanged += (_, _) => RefreshSearchHighlights();
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            if (PanelHost.IsVisible && ActiveTab?.Editor is not null)
                Run(() => FindMatchesAsync(automatic: true));
        };
        Closed += (_, _) => CancelSearch();
        _searchResults.SelectionChanged += (_, _) =>
        {
            if (_searchResults.SelectedItem is not ListBoxItem { Tag: SearchResult result }
                || !_tabs.ContainsKey(result.Tab.Id)) return;
            if (result.Tab.Revision != result.Revision)
            {
                _searchStatus.Text = "Текст изменился. Повторите поиск.";
                return;
            }

            _searchFocus = (result.Tab, result.Hit);
            SelectText(result.Tab, result.Hit.Offset, result.Hit.Length);
            RefreshSearchHighlights();
        };

        void SetReplacementVisible()
        {
            foreach (var control in new Control[] { replaceLabel, _replacement, replace, replaceAll })
                control.IsVisible = _replaceMode.IsChecked == true;
        }
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
            if (PanelHost.IsVisible) CloseSearchPanel();
            log.FocusSearch();
            return;
        }

        if (ActiveTab?.Editor is { } editor && !_searchInput.IsKeyboardFocusWithin && !_replacement.IsKeyboardFocusWithin)
        {
            var selection = MultiCaretEditing.Primary(editor.TextArea);
            if (selection.Length > 0)
            {
                _regexOption.IsChecked = false;
                _searchInput.Text = editor.Document.GetText(selection.Start, selection.Length);
            }
        }

        PanelHost.IsVisible = true;
        _replaceMode.IsChecked = replace;
        _searchInput.Focus();
        _searchInput.SelectAll();
        ScheduleSearch();
    }

    private void CloseSearchPanel()
    {
        PanelHost.IsVisible = false;
        CancelSearch();
        _highlightPattern = null;
        _searchFocus = null;
        RefreshSearchHighlights();
        ActiveTab?.Editor?.Focus();
    }

    private void ForgetSearchTab(WorkTab tab)
    {
        var isSearchSource = ReferenceEquals(_searchTab, tab);
        if (isSearchSource || _allTabs.IsChecked == true && _searchCancellation is not null)
            CancelSearch();

        if (isSearchSource) _searchTab = null;
        if (_searchFocus is { } focus && ReferenceEquals(focus.Tab, tab)) _searchFocus = null;

        var matchingItems = _searchResults.Items.OfType<ListBoxItem>().Where(item =>
            item.Tag is SearchResult result && ReferenceEquals(result.Tab, tab)).ToArray();
        if (_searchResults.SelectedItem is ListBoxItem selected && matchingItems.Contains(selected))
            _searchResults.SelectedItem = null;
        foreach (var item in matchingItems)
        {
            _searchResults.Items.Remove(item);
        }
    }

    private void CancelSearch()
    {
        _searchDebounce.Stop();
        var search = _searchCancellation;
        _searchCancellation = null;
        search?.Cancel();
        _cancelSearch.IsVisible = false;
        if (search is not null && PanelHost.IsVisible) _searchStatus.Text = "Поиск остановлен";
    }

    private void ScheduleSearch()
    {
        CancelSearch();
        _searchTab = ActiveTab;
        _highlightPattern = null;
        _searchFocus = null;
        _searchResults.Items.Clear();
        _searchStatus.Text = "";
        ToolTip.SetTip(_searchStatus, null);
        var editor = _searchTab?.Editor;
        _findAll.IsEnabled = editor is { IsReadOnly: false };
        RefreshSearchHighlights();
        if (PanelHost.IsVisible && editor is not null && !string.IsNullOrEmpty(_searchInput.Text)
            && IsAutomaticSearchSize(_searchTab!))
            _searchDebounce.Start();
    }

    private void RefreshSearchForActiveTab()
    {
        if (PanelHost.IsVisible && !ReferenceEquals(_searchTab, ActiveTab) && _allTabs.IsChecked != true)
            ScheduleSearch();
        else
        {
            _findAll.IsEnabled = ActiveTab?.Editor is { IsReadOnly: false };
            RefreshSearchHighlights();
        }
    }

    private static bool IsAutomaticSearchSize(WorkTab tab) =>
        (tab.Stamp?.Length ?? (long)(tab.Editor?.Document.TextLength ?? 0) * sizeof(char))
        < TextOperations.AutomaticSearchMaxBytes;

    private Task FindAsync() => FindMatchesAsync();

    private async Task FindMatchesAsync(bool selectAll = false, bool automatic = false)
    {
        CancelSearch();
        var options = CurrentQuery();
        if (options.Pattern.Length == 0)
        {
            if (!automatic) _searchStatus.Text = "Введите текст для поиска";
            return;
        }
        Regex query;
        try { query = TextOperations.BuildRegex(options); }
        catch (ArgumentException ex)
        {
            _searchStatus.Text = ex.Message;
            return;
        }

        var active = ActiveTab;
        var allTabs = _allTabs.IsChecked == true;
        var candidates = allTabs && !selectAll ? _tabs.Values.ToArray() : active is { } selected ? [selected] : [];
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCancellation = source;
        _cancelSearch.IsVisible = true;
        var preserveResults = allTabs && selectAll;
        if (!preserveResults) _searchResults.Items.Clear();
        _highlightPattern = query;
        _searchStatus.Text = "Поиск…";
        var incomplete = false;
        var results = new List<SearchResult>(100);
        var total = 0L;
        SearchSummary? selection = null;
        TextEditor? selectionEditor = null;
        long selectionRevision = 0;
        try
        {
            foreach (var tab in candidates)
            {
                source.Token.ThrowIfCancellationRequested();
                if (automatic && (tab.Editor is null || !IsAutomaticSearchSize(tab)))
                {
                    incomplete |= tab.Deferred is not null || tab.Loading;
                    continue;
                }
                if (tab.Deferred is { DraftRef: not null } draft && _storage.GetDraftLength(draft) > LineGeneratorThreshold)
                {
                    incomplete = true;
                    continue;
                }
                if (tab.Deferred is not null && !tab.IsLog)
                {
                    try { await ActivateDeferredAsync(tab); }
                    catch (Exception ex) when (IsUserError(ex) || ex is OperationCanceledException)
                    {
                        incomplete = true;
                    }
                }
                if (tab.Editor is not { } editor)
                {
                    incomplete |= tab.Deferred is not null || tab.Loading || tab.Recovery is not null;
                    continue;
                }

                var text = editor.Text;
                var revision = tab.Revision;
                var previewLimit = 100 - results.Count;
                var caretLimit = selectAll ? MultiCaretEditing.MaxCarets : 0;
                var result = await Task.Run(
                    () => TextOperations.Find(text, query, previewLimit, source.Token, caretLimit), source.Token);
                if (tab.Revision != revision || !ReferenceEquals(tab.Editor, editor))
                {
                    incomplete = true;
                    continue;
                }
                total += result.Count;
                incomplete |= result.TimedOut;
                foreach (var hit in result.Hits) results.Add(new SearchResult(tab, hit, revision));
                if (selectAll)
                {
                    selection = result;
                    selectionEditor = editor;
                    selectionRevision = revision;
                }
            }

            source.Token.ThrowIfCancellationRequested();
            if (!preserveResults)
            {
                foreach (var result in results)
                {
                    _searchResults.Items.Add(new ListBoxItem
                    {
                        Content = $"{result.Tab.Label.Text} · {result.Hit.Line}: {result.Hit.Preview}",
                        Tag = result,
                    });
                }
            }

            _searchStatus.Text = incomplete ? $"Найдено {total:N0} · результаты неполные" : $"Найдено {total:N0}";
            if (!preserveResults && total > results.Count)
                ToolTip.SetTip(_searchStatus, $"В списке первые {results.Count:N0} совпадений");
            if (!selectAll) return;
            if (!ReferenceEquals(ActiveTab, active) || active?.MultiCaret is not { } multiCaret || active.Revision != selectionRevision
                || !ReferenceEquals(active.Editor, selectionEditor) || selectionEditor is not { IsReadOnly: false }
                || selection is null) return;
            if (selection.TimedOut)
            {
                _searchStatus.Text = "Истекло время поиска. Уточните запрос.";
                return;
            }
            if (selection.Count > MultiCaretEditing.MaxCarets)
            {
                _searchStatus.Text = $"Найдено {selection.Count:N0} · максимум {MultiCaretEditing.MaxCarets:N0} курсоров";
                ToolTip.SetTip(_searchStatus, _searchStatus.Text);
                return;
            }
            if (selection.Carets is { Count: > 0 } ranges)
            {
                multiCaret.SelectMatches(ranges);
                _searchStatus.Text = $"Выделено {MultiCaretEditing.Count(selectionEditor.TextArea):N0}";
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_searchCancellation, source))
            {
                _searchCancellation = null;
                _cancelSearch.IsVisible = false;
                RefreshSearchHighlights();
            }
        }
    }

    private async Task NavigateSearchAsync(bool backward)
    {
        if (ActiveTab is not { Editor: { } editor } tab)
        {
            return;
        }

        var options = CurrentQuery();
        if (options.Pattern.Length == 0)
        {
            OpenSearchPanel();
            return;
        }
        var query = TextOperations.BuildRegex(options);
        _highlightPattern = query;
        var selection = MultiCaretEditing.Primary(editor.TextArea);
        var same = _searchFocus is { } focused && ReferenceEquals(focused.Tab, tab) && focused.Hit.Offset == selection.Start;
        var offset = backward ? selection.Start
            : same ? Math.Min(editor.Document.TextLength, selection.Start + Math.Max(1, selection.Length))
            : editor.CaretOffset;
        var text = editor.Text;
        var revision = tab.Revision;
        var match = await Task.Run(() => TextOperations.FindNext(text, query, offset, backward), _lifetime.Token);
        if (tab.Revision != revision || !ReferenceEquals(tab.Editor, editor) || !ReferenceEquals(ActiveTab, tab)
            || CurrentQuery() != options)
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
            MultiCaretEditing.CollapseToPrimary(editor.TextArea);
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
            MultiCaretEditing.CollapseToPrimary(editor.TextArea);
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
