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
using RustText.Demo.Logs;

namespace RustText.Demo;

public partial class MainWindow
{
    private TextBox _searchInput = null!;
    private TextBox _replacement = null!;
    private ToggleButton _regexOption = null!;
    private ToggleButton _caseOption = null!;
    private ToggleButton _wordOption = null!;
    private ToggleButton _allTabs = null!;
    private ToggleButton _onlyCurrent = null!;
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
        _allTabs = SearchBar.Option("Все вкладки", "Искать во всех вкладках");
        _onlyCurrent = SearchBar.Option("Только текущее", "Подсвечивать только текущее совпадение");
        _searchResults = new ListBox { MaxHeight = 112 };
        _searchStatus = SearchBar.Status();

        var rows = SearchBar.MakeRows();
        var find = SearchBar.Command("Найти", () =>
        {
            _searchInput.Focus();
            Run(FindAsync);
        }, primary: true);
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
        PanelHost.Child = new StackPanel { Spacing = 2, Children = { rows, footer, _searchResults } };

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
            if (PanelHost.IsVisible && ActiveTab?.SearchEditor is not null)
                Run(() => FindMatchesAsync(automatic: true));
        };
        Closed += (_, _) => CancelSearch();
        _searchResults.SelectionChanged += (_, _) =>
        {
            if (_searchResults.SelectedItem is not ListBoxItem { Tag: SearchResult result }
                || !_tabs.ContainsKey(result.Tab.Id)) return;
            if (result is LogSearchResult fromLog)
            {
                Run(async () =>
                {
                    if (fromLog.Tab.Log is { } log && log.IsSearchSnapshotCurrent(fromLog.Snapshot))
                    {
                        Select(fromLog.Tab);
                        if (await log.NavigateToSearchHitAsync(fromLog.Snapshot, fromLog.Hit)) return;
                    }
                    _searchStatus.Text = "Лог изменился или недоступен. Повторите поиск.";
                });
                return;
            }

            var fromEditor = (EditorSearchResult)result;
            if (fromEditor.Tab.Revision != fromEditor.Revision)
            {
                _searchStatus.Text = "Текст изменился. Повторите поиск.";
                return;
            }

            _searchFocus = (fromEditor.Tab, fromEditor.Hit);
            SelectText(fromEditor.Tab, fromEditor.Hit.Offset, fromEditor.Hit.Length);
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
        if (ActiveTab?.IsTool == true) return;
        if (ActiveTab is { EditorLogFilter.IsFiltering: true } filteredTab) CloseEditorLevelFilter(filteredTab);
        if (!replace && ActiveTab?.Log is { } log)
        {
            if (PanelHost.IsVisible) CloseSearchPanel();
            log.FocusSearch();
            return;
        }

        if (ActiveTab?.SearchEditor is { } editor && !_searchInput.IsKeyboardFocusWithin && !_replacement.IsKeyboardFocusWithin)
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
        ActiveTab?.SearchEditor?.Focus();
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
        var editor = _searchTab?.SearchEditor;
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
        (tab.Log?.FileLength ?? tab.Stamp?.Length ?? (long)(tab.SearchEditor?.Document.TextLength ?? 0) * sizeof(char))
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
        var jumpToFirst = !selectAll && _searchInput.IsFocused;
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
        EditorSearchResult? firstMatch = null;
        SearchSummary? selection = null;
        TextEditor? selectionEditor = null;
        long selectionRevision = 0;
        try
        {
            foreach (var tab in candidates)
            {
                source.Token.ThrowIfCancellationRequested();
                if (automatic && (tab.Deferred is not null || tab.Loading || !IsAutomaticSearchSize(tab)))
                {
                    incomplete = true;
                    continue;
                }
                if (tab.Deferred is { DraftRef: not null, DraftLogMode: not false } draft
                    && SessionStorage.GetDraftLength(draft) > LineGeneratorThreshold)
                {
                    incomplete = true;
                    continue;
                }
                if (tab.Deferred is not null)
                {
                    try { await ActivateDeferredAsync(tab); }
                    catch (Exception ex) when (IsUserError(ex) || ex is OperationCanceledException)
                    {
                        incomplete = true;
                    }
                }
                if (allTabs && !selectAll && tab.Log is { } log)
                {
                    var remaining = 100 - results.Count;
                    if (remaining == 0 || log.HasPendingEdit)
                    {
                        incomplete = true;
                        continue;
                    }
                    try
                    {
                        var snapshot = log.CaptureSearchSnapshot();
                        var page = automatic
                            ? await log.SearchSnapshotAsync(snapshot, options, null, remaining, source.Token)
                            : await log.SearchSnapshotAsync(snapshot, options, null, remaining, source.Token, long.MaxValue);
                        if (!ReferenceEquals(tab.Log, log) || !log.IsSearchSnapshotCurrent(snapshot))
                        {
                            incomplete = true;
                            continue;
                        }
                        total += page.Hits.Count;
                        incomplete |= page.HasMore || page.Message is not null;
                        foreach (var hit in page.Hits) results.Add(new LogSearchResult(tab, snapshot, hit));
                    }
                    catch (Exception error) when (IsUserError(error) && !source.IsCancellationRequested)
                    {
                        incomplete = true;
                    }
                    continue;
                }
                if (tab.SearchEditor is not { } editor)
                {
                    incomplete |= tab.Deferred is not null || tab.Loading || tab.Recovery is not null;
                    continue;
                }

                incomplete |= tab.ConsoleText?.HasEarlierOutput == true;
                var text = editor.Text;
                var revision = tab.Revision;
                var previewLimit = 100 - results.Count;
                var captureFirst = jumpToFirst && ReferenceEquals(tab, active);
                var resultLimit = captureFirst ? Math.Max(1, previewLimit) : previewLimit;
                var caretLimit = selectAll ? MultiCaretEditing.MaxCarets : 0;
                var minimap = tab.Minimap;
                var lineCount = minimap is null ? 0 : editor.Document.LineCount;
                var result = await Task.Run(
                    () => TextOperations.Find(text, new TextMatcher(options, query), resultLimit, source.Token, caretLimit, lineCount),
                    source.Token);
                source.Token.ThrowIfCancellationRequested();
                if (tab.Revision != revision || !ReferenceEquals(tab.SearchEditor, editor))
                {
                    incomplete = true;
                    continue;
                }
                minimap?.SetSearchOffsets(result.MinimapOffsets);
                total += result.Count;
                incomplete |= result.TimedOut;
                if (previewLimit > 0)
                    foreach (var hit in result.Hits) results.Add(new EditorSearchResult(tab, hit, revision));
                if (captureFirst && result.Hits.Count > 0)
                    firstMatch = new EditorSearchResult(tab, result.Hits[0], revision);
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
                        Content = $"{result.Tab.Label.Text} · {result.Description}",
                        Tag = result,
                    });
                }
            }

            _searchStatus.Text = incomplete ? $"Найдено {total:N0} · результаты неполные" : $"Найдено {total:N0}";
            if (!preserveResults && total > results.Count)
                ToolTip.SetTip(_searchStatus, $"В списке первые {results.Count:N0} совпадений");
            if (!selectAll)
            {
                if (_searchInput.IsFocused && firstMatch is { } first && ReferenceEquals(first.Tab, ActiveTab)
                    && first.Tab.Revision == first.Revision)
                {
                    _searchFocus = (first.Tab, first.Hit);
                    SelectText(first.Tab, first.Hit.Offset, first.Hit.Length, focusEditor: false);
                }
                return;
            }
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

    private Task NavigateSearchAsync(bool backward) => NavigateSearchFromAsync(backward, null);

    private async Task NavigateSearchFromAsync(bool backward, int? startOffset)
    {
        if (ActiveTab is not { SearchEditor: { } editor } tab)
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
        var same = _searchFocus is { } focused && ReferenceEquals(focused.Tab, tab)
            && focused.Hit.Offset == selection.Start && focused.Hit.Length == selection.Length;
        var offset = startOffset ?? (backward ? selection.Start : editor.CaretOffset);
        if (startOffset is null && !backward && same)
        {
            offset = selection.Length > 0 ? selection.End : AfterEmptyMatch(selection.Start, editor.Document.TextLength);
        }
        var text = editor.Text;
        var revision = tab.Revision;
        var result = await Task.Run(() => TextOperations.FindNext(text, new TextMatcher(options, query), offset, backward), _lifetime.Token);
        if (tab.Revision != revision || !ReferenceEquals(tab.SearchEditor, editor) || !ReferenceEquals(ActiveTab, tab)
            || CurrentQuery() != options)
        {
            return;
        }

        if (result is not { } match)
        {
            _searchStatus.Text = "Совпадений нет";
            return;
        }

        _searchFocus = (tab, new SearchHit(match.Index, match.Length, 0, ""));
        SelectText(tab, match.Index, match.Length);
        RefreshSearchHighlights();
    }

    private static int AfterEmptyMatch(int offset, int textLength) => offset < textLength ? offset + 1 : 0;

    private async Task ReplaceAsync(bool all)
    {
        if (ActiveTab is not { Editor: { IsReadOnly: false } editor } tab)
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
            int? nextOffset = null;
            if (match.Success && match.Index == offset && match.Length == selection.Length)
            {
                var inserted = options.IsRegex ? match.Result(replacement) : replacement;
                editor.Document.Replace(offset, selection.Length, inserted);
                var end = offset + inserted.Length;
                editor.Select(end, 0);
                editor.CaretOffset = end;
                nextOffset = match.Length == 0 ? AfterEmptyMatch(end, editor.Document.TextLength) : end;
            }

            await NavigateSearchFromAsync(false, nextOffset);
            return;
        }

        var source = editor.Text;
        var revision = tab.Revision;
        var format = tab.Format;
        var result = await Task.Run(() => TextOperations.ReplaceAll(source, new TextMatcher(options, query), replacement, options.IsRegex,
            _lifetime.Token, format.MixedEndings), _lifetime.Token);
        if (tab.Revision != revision || !_tabs.ContainsKey(tab.Id))
        {
            throw new InvalidOperationException("Текст изменился во время замены.");
        }

        if (editor.IsReadOnly || !ReferenceEquals(tab.Editor, editor)) return;
        if (result.Count > 0)
        {
            MultiCaretEditing.CollapseToPrimary(editor.TextArea);
            if (result.MixedEndings is { } mixedEndings)
            {
                var updatedFormat = format with { MixedEndings = mixedEndings };
                DocumentFormatHistory.For(editor.Document, format).Restore(result.Text, updatedFormat);
            }
            else
            {
                editor.Document.Replace(0, editor.Document.TextLength, result.Text);
            }
        }

        _searchStatus.Text = $"Заменено {result.Count:N0}";
    }

    private void RefreshSearchHighlights()
    {
        foreach (var tab in _tabs.Values)
        {
            if ((tab.Highlights ?? tab.ConsoleText?.Highlights) is not { } highlights || tab.SearchEditor is not { } editor)
            {
                continue;
            }

            highlights.Pattern = PanelHost.IsVisible && (ReferenceEquals(tab, ActiveTab) || _allTabs.IsChecked == true)
                ? _highlightPattern : null;
            highlights.OnlyCurrent = _onlyCurrent.IsChecked == true;
            highlights.Current = PanelHost.IsVisible && _searchFocus is { } active && ReferenceEquals(active.Tab, tab)
                ? (active.Hit.Offset, active.Hit.Length) : null;
            tab.Minimap?.ShowSearch(highlights.Pattern is not null, highlights.OnlyCurrent, highlights.Current);
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }
    }

    private abstract record SearchResult(WorkTab Tab, string Description);
    private sealed record EditorSearchResult(WorkTab Tab, SearchHit Hit, long Revision)
        : SearchResult(Tab, $"{Hit.Line}: {Hit.Preview}");
    private sealed record LogSearchResult(WorkTab Tab, LogView.LogSearchSnapshot Snapshot, LogSearchHit Hit)
        : SearchResult(Tab, Hit.LineNumber is { } line ? $"{LineNumbers.Format(line)}: {Hit.Preview}" : Hit.Preview);
}
