using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RustText.Demo.Editor;
using RustText.Demo.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private void BuildMenus()
    {
        _recentMenu = new MenuItem { Header = "Недавние файлы" };
        _closedFilesMenu = new MenuItem { Header = "Закрытые файлы" };
        _closedDraftsMenu = new MenuItem { Header = "Призрачные вкладки" };
        TopMenu.Items.Add(MenuGroup("_Файл",
            Command("Новая вкладка   Ctrl+N", () => NewTab()),
            Command("Новое окно", () => Run(() => OpenWorkspaceWindowAsync(_workspaceId))),
            Command("Открыть…   Ctrl+O", () => Run(() => OpenDialogAsync())),
            Command("Открыть как лог…", () => Run(() => OpenDialogAsync(asLog: true))),
            Command("Подключиться к консоли…", () => Run(OpenConsoleAsync)),
            Command("Открыть в Windows-1251…", () => Run(() => OpenDialogAsync(encoding: TextEncodingKind.Windows1251))),
            Command("Открыть лог в Windows-1251…", () => Run(() => OpenDialogAsync(true, TextEncodingKind.Windows1251))),
            MenuGroup("Перечитать в кодировке",
                Command("Автоопределение", () => ReloadActive(null)),
                Command("UTF-8", () => ReloadActive(TextEncodingKind.Utf8)),
                Command("UTF-16 LE", () => ReloadActive(TextEncodingKind.Utf16LittleEndian)),
                Command("UTF-16 BE", () => ReloadActive(TextEncodingKind.Utf16BigEndian)),
                Command("Windows-1251", () => ReloadActive(TextEncodingKind.Windows1251))),
            new Separator(),
            Command("Сохранить   Ctrl+S", () => Run(() => SaveAsync())),
            Command("Сохранить как…   Ctrl+Shift+S", () => Run(() => SaveAsync(true))),
            Command("Закрыть вкладку   Ctrl+W", CloseActive),
            Command("Восстановить вкладку   Ctrl+Shift+T", () => Run(RestoreLastClosedAsync)),
            _recentMenu,
            _closedFilesMenu,
            new Separator(),
            Command("Настроить открытие файлов в Windows…", () => Run(RegisterAssociationsAsync)),
            Command("Выход", Close)));
        TopMenu.Items.Add(MenuGroup("_Инструменты",
            MenuGroup("Редактор и просмотр",
                Command("Перейти к строке…   Ctrl+G", () => Run(GoToLineAsync)),
                Command("Перенос строк", ToggleWrap),
                MenuGroup("Масштаб текста",
                    Command("Увеличить", () => ResizeText(1)),
                    Command("Уменьшить", () => ResizeText(-1)),
                    Command("Сбросить", () => ResizeText(FocusTheme.EditorFontSize - _fontSize)))),
            MenuGroup("Форматирование и данные",
                Command("Форматировать JSON   Ctrl+Shift+J", () => Run(FormatJsonAsync)),
                Command("Включить проверку JSON", EnableJson),
                new Separator(),
                Command("Генератор строк…", () => Run(ShowGeneratorAsync)),
                Command("Сравнить списки…", () => Run(ShowListComparisonAsync))),
            MenuGroup("История и черновики",
                Command("Версии файла…", () => Run(ShowVersionsAsync)),
                new Separator(),
                _closedDraftsMenu,
                Command("Архив черновиков…", () => Run(ShowDraftArchiveAsync))),
            MenuGroup("Обновления",
                Command("Проверить обновления…", () => Run(() => CheckUpdatesAsync(manual: true))),
                Command("История выпусков…", () => Run(ShowReleaseHistoryAsync))),
            Command("Найти окно процесса…", () => Run(ShowWindowFinderAsync)),
            new Separator(),
            Command("Настройки…", () => Run(ShowSettingsAsync))));
        TopMenu.Items.Add(MenuGroup("_Поиск",
            Command("Найти   Ctrl+F", () => OpenSearchPanel()),
            Command("Заменить   Ctrl+H", () => OpenSearchPanel(true)),
            Command("Следующее совпадение   F3", () => Run(() => NavigateSearchAsync(false))),
            Command("Предыдущее совпадение   Shift+F3", () => Run(() => NavigateSearchAsync(true)))));
        TopMenu.Items.Add(BuildThemeMenu());
    }

    private void OnWorkspaceKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.Alt)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (e.Key == Key.Escape && CancelTabDrag())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            if (ActiveTab?.Log is { } log)
            {
                Run(() => log.NavigateMatchAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
            }
            else
            {
                Run(() => NavigateSearchAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && PanelHost.IsVisible)
        {
            if (ActiveTab?.Editor is { IsKeyboardFocusWithin: true } editor
                && MultiCaretEditing.Count(editor.TextArea) > 1) return;
            CloseSearchPanel();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ActiveTab?.Log?.CloseSearch() == true)
        {
            e.Handled = true;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            return;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.N: NewTab(); break;
            case Key.O: Run(() => OpenDialogAsync()); break;
            case Key.S: Run(() => SaveAsync(shift)); break;
            case Key.W: CloseActive(); break;
            case Key.T when shift: Run(RestoreLastClosedAsync); break;
            case Key.F: OpenSearchPanel(); break;
            case Key.H: OpenSearchPanel(true); break;
            case Key.G: Run(GoToLineAsync); break;
            case Key.J when shift: Run(FormatJsonAsync); break;
            case Key.OemPlus or Key.Add: ResizeText(1); break;
            case Key.OemMinus or Key.Subtract: ResizeText(-1); break;
            case Key.D0: ResizeText(FocusTheme.EditorFontSize - _fontSize); break;
            default: return;
        }

        e.Handled = true;
    }

    private void OnWorkspacePointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.Delta.Y == 0
            || e.Source is not Visual source)
        {
            return;
        }

        var editor = ActiveTab?.EditorLogFilter?.VisibleEditor ?? ActiveTab?.Editor;
        var log = ActiveTab?.Log;
        var isInEditor = editor?.IsVisualAncestorOf(source) == true;
        var isInLog = log?.IsVisualAncestorOf(source) == true;
        if (!isInEditor && !isInLog)
        {
            return;
        }

        ResizeText(Math.Sign(e.Delta.Y));
        e.Handled = true;
    }

    private void ReloadActive(TextEncodingKind? encoding)
    {
        if (ActiveTab is { } tab)
        {
            Run(() => ReloadAsync(tab, encoding));
        }
    }

    private void CloseActive()
    {
        if (ActiveTab is { } tab)
        {
            Run(() => CloseTabAsync(tab));
        }
    }

    private void ToggleWrap()
    {
        var enabled = !_wordWrap;
        foreach (var window in _workspace.Windows)
        {
            window._wordWrap = enabled;
            window.ApplyTextAppearance();
        }
        Run(() => SaveWorkspaceSettingsAsync(settings => settings with { WordWrap = enabled }));
    }

    private void ResizeText(double delta)
    {
        var size = Math.Clamp(_fontSize + delta, 8, 48);
        foreach (var window in _workspace.Windows)
        {
            window._fontSize = size;
            window.ApplyTextAppearance();
        }
        Run(() => SaveWorkspaceSettingsAsync(settings => settings with { FontSize = size }));
    }

    private void ApplyTextAppearance()
    {
        foreach (var tab in _tabs.Values)
        {
            if (tab.Editor is { } editor)
            {
                editor.WordWrap = _wordWrap;
                editor.HorizontalScrollBarVisibility = _wordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
                editor.FontSize = _fontSize;
                if (tab.EditorLogFilter is { IsFiltering: true } filter)
                {
                    filter.VisibleEditor.WordWrap = _wordWrap;
                    filter.VisibleEditor.HorizontalScrollBarVisibility = editor.HorizontalScrollBarVisibility;
                    filter.VisibleEditor.FontSize = _fontSize;
                }
            }

            tab.Log?.SetTextSize(_fontSize);
            if (tab.Log is { } log) log.WordWrap = _wordWrap;
        }
    }

    private void EnableJson()
    {
        if (ActiveTab is { } tab)
        {
            tab.JsonDisabled = false;
            ScheduleJsonValidation(tab);
        }
    }

    private async Task FormatJsonAsync()
    {
        if (ActiveTab is not { Editor: { IsReadOnly: false } editor } tab)
        {
            return;
        }

        MultiCaretEditing.CollapseToPrimary(editor.TextArea);
        var start = editor.SelectionLength > 0 ? editor.SelectionStart : 0;
        var length = editor.SelectionLength > 0 ? editor.SelectionLength : editor.Document.TextLength;
        var revision = tab.Revision;
        var document = editor.Text;
        var input = document.Substring(start, length);
        var path = tab.Path;
        var token = _lifetime.Token;
        var formatted = await Task.Run(() => TextOperations.FormatJson(input, path, document, token), token);
        if (revision != tab.Revision)
        {
            throw new InvalidOperationException("Текст изменился во время форматирования.");
        }

        if (editor.IsReadOnly || !ReferenceEquals(tab.Editor, editor)) return;
        editor.Document.Replace(start, length, formatted);
        SelectText(tab, start, 0);
    }

    private async Task GoToLineAsync()
    {
        if (ActiveTab is not { Editor: { } editor } tab)
        {
            return;
        }

        var limit = editor.Document.LineCount;
        var input = await PromptAsync("Перейти к строке", $"Номер от 1 до {LineNumbers.Format(limit)}", LineNumbers.Format(editor.TextArea.Caret.Line));
        if (input is null)
        {
            return;
        }

        if (!int.TryParse(input, NumberStyles.Integer | NumberStyles.AllowThousands, LineNumbers.FormatInfo, out var line)
            || line < 1 || line > limit)
        {
            throw new ArgumentException($"Укажите строку от 1 до {LineNumbers.Format(limit)}.");
        }

        SelectText(tab, editor.Document.GetLineByNumber(line).Offset, 0);
    }

    private async Task ShowGeneratorAsync()
    {
        var template = new TextBox { Text = "{int_from_1} {guid} {rand_string_8}", AcceptsReturn = true, MinHeight = 90 };
        var count = new TextBox { Text = "1000", PlaceholderText = "Количество строк" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, IsVisible = false };
        var body = DialogPanel(new TextBlock { Text = "Шаблон" }, template, new TextBlock { Text = "Количество строк" }, count, progress, status);
        var dialog = Dialog("Генератор строк", body, 560);
        CancellationTokenSource? source = null;
        var generate = FocusButton(new Button { Content = "В новую вкладку" }, primary: true);
        var file = FocusButton(new Button { Content = "В файл…" });
        generate.Click += (_, _) => Generate(false);
        file.Click += (_, _) => Generate(true);
        body.Children.Add(Row(generate, file, FocusButton(Button("Отменить", () => source?.Cancel()))));
        dialog.Closing += (_, e) =>
        {
            if (source is not null)
            {
                e.Cancel = true;
                source.Cancel();
            }
        };

        async void Generate(bool export)
        {
            if (source is not null)
            {
                return;
            }

            source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            try
            {
                var token = source.Token;
                if (!long.TryParse(count.Text, out var rows))
                {
                    throw new ArgumentException("Укажите целое количество строк.");
                }

                var generator = new LineGenerator(template.Text ?? "", rows);
                export |= generator.EstimateBytes() > LineGenerator.TabByteLimit || rows > LineGenerator.TabLineLimit;
                generate.IsEnabled = file.IsEnabled = false;
                progress.IsVisible = true;
                progress.Value = 0;
                var reporter = new Progress<double>(value => progress.Value = value);
                if (export)
                {
                    using var selected = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = "Сохранить сгенерированные строки",
                        SuggestedFileName = "строки.txt",
                        FileTypeChoices = [TextFiles]
                    });
                    if (selected?.TryGetLocalPath() is { } path)
                    {
                        await Task.Run(() => generator.GenerateFileAsync(path, reporter, token), token);
                        status.Text = $"Сохранено · {path}";
                    }
                }
                else
                {
                    var text = await Task.Run(() => generator.GenerateTextAsync(reporter, token), token);
                    NewTab(text);
                    status.Text = "Результат открыт в новой вкладке.";
                }
            }
            catch (OperationCanceledException)
            {
                status.Text = "Генерация отменена.";
            }
            catch (Exception error) when (IsUserError(error))
            {
                status.Text = error.Message;
            }
            finally
            {
                source.Dispose();
                source = null;
                generate.IsEnabled = file.IsEnabled = true;
            }
        }

        await dialog.ShowDialog(this);
    }

    private async Task ShowListComparisonAsync()
    {
        var left = new TextBox { AcceptsReturn = true, MinHeight = 130, PlaceholderText = "Левый список" };
        var right = new TextBox { AcceptsReturn = true, MinHeight = 130, PlaceholderText = "Правый список" };
        var inputs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        inputs.Children.Add(left);
        Grid.SetColumn(right, 1);
        inputs.Children.Add(right);

        var results = new TabControl { MinHeight = 160 };
        var tabs = new (string Label, TextBox Text)[]
        {
            ("Общие строки", new TextBox { AcceptsReturn = true, IsReadOnly = true }),
            ("Только слева", new TextBox { AcceptsReturn = true, IsReadOnly = true }),
            ("Только справа", new TextBox { AcceptsReturn = true, IsReadOnly = true })
        };
        foreach (var tab in tabs)
        {
            results.Items.Add(new TabItem { Header = tab.Label, Content = tab.Text });
        }

        results.SelectedIndex = 0;
        var body = DialogPanel(inputs, FocusButton(Button("Сравнить", () => Run(async () =>
        {
            var a = left.Text ?? "";
            var b = right.Text ?? "";
            var comparison = await Task.Run(() => TextOperations.CompareLists(a, b), _lifetime.Token);
            tabs[0].Text.Text = comparison.Common;
            tabs[1].Text.Text = comparison.LeftOnly;
            tabs[2].Text.Text = comparison.RightOnly;
        })), primary: true), results);
        body.Children.Add(Row(FocusButton(Button("Скопировать результат", () => Run(() => CopyTextAsync(tabs[Math.Max(0, results.SelectedIndex)].Text.Text)))),
            FocusButton(Button("Открыть во вкладке", () => NewTab(tabs[Math.Max(0, results.SelectedIndex)].Text.Text ?? "")), primary: true)));
        await Dialog("Сравнение списков", body, 740).ShowDialog(this);
    }

    private async Task SaveWorkspaceSettingsAsync(Func<UserSettings, UserSettings> update)
    {
        await _workspace.SettingsGate.WaitAsync(_lifetime.Token);
        try
        {
            var previous = Settings;
            var settings = update(previous);
            if (settings == previous) return;

            await _storage.SaveSettingsAsync(settings, _lifetime.Token);
            _workspace.Settings = settings;
            if (settings.UntitledTabLimit != previous.UntitledTabLimit
                || settings.ShowDraftLineCount != previous.ShowDraftLineCount)
            {
                foreach (var window in _workspace.Windows)
                {
                    foreach (var tab in window.LoadedWorkspaceTabs()) window.UpdateTab(tab);
                    window.RefreshHistoryMenusLocal();
                    if (window._home) window._refreshHome?.Invoke();
                }
            }
            if (settings.Theme != previous.Theme)
                ApplyWorkspaceTheme();
            else if (settings.GlassIntensity != previous.GlassIntensity)
                ApplyWorkspaceGlass();
        }
        finally { _workspace.SettingsGate.Release(); }
    }

    private async Task<string?> PromptAsync(string title, string description, string initial = "")
    {
        var input = new TextBox { Text = initial, MinWidth = 160 };
        var body = DialogPanel(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }, input);
        var dialog = Dialog(title, body);
        body.Children.Add(Row(FocusButton(Button("OK", () => dialog.Close(input.Text ?? "")), primary: true),
            FocusButton(Button("Отмена", () => dialog.Close()))));
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                dialog.Close(input.Text ?? "");
            }
        };
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }

    private static StackPanel DialogPanel(params Control[] children)
    {
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        foreach (var child in children)
        {
            body.Children.Add(child);
        }

        return body;
    }

    private static Button FocusButton(Button button, bool primary = false)
    {
        button.Classes.Add(primary ? "focus-primary" : "focus-quiet");
        return button;
    }

    private static Button FocusIcon(Button button)
    {
        button.Classes.Add("focus-icon");
        return button;
    }

    internal static Window Dialog(string title, Control body, double width = 430, bool scrollContent = true)
    {
        var dialog = new Window
        {
            Title = title,
            Width = Math.Clamp(width, 360, 760),
            MinWidth = 360,
            MaxWidth = 760,
            MinHeight = 190,
            MaxHeight = 720,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = FocusTheme.Panel,
            Foreground = FocusTheme.Text,
            Content = new Border
            {
                Background = FocusTheme.Panel,
                Child = scrollContent ? new ScrollViewer { Content = body } : body
            },
            CanResize = true
        };
        DialogWindowChrome.Apply(dialog);
        return dialog;
    }
}
