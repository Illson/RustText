using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using RustText.Demo.Editor;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class WorkspaceTests
{
    [Fact]
    public Task ReloadKeepsNewTypingAndUndo() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "reload.txt");
        await File.WriteAllTextAsync(path, "исходный файл");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            var loaded = await DocumentFiles.ReadAsync(path);
            var pending = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reload = CallAsync(window, "ApplyEditorReloadAsync", tab, editor, pending.Task);

            editor.TextArea.Focus();
            editor.CaretOffset = editor.Document.TextLength;
            window.KeyTextInput(" + правка");
            Assert.EndsWith(" + правка", editor.Text);
            pending.SetResult(loaded);
            await reload;

            Assert.Same(editor, Read<TextEditor>(ActiveTab(window), "Editor"));
            Assert.Equal("исходный файл + правка", editor.Text);
            Assert.True(Read<bool>(tab, "Dirty"));
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.Equal("исходный файл", editor.Text);
            Assert.False(Read<bool>(tab, "Dirty"));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task InactiveTabHoverChangesOnlyHoveredSurface() => HeadlessTests.RunAsync(async profile =>
    {
        var firstPath = Path.Combine(profile, "first.txt");
        var secondPath = Path.Combine(profile, "second.txt");
        await File.WriteAllTextAsync(firstPath, "first document");
        await File.WriteAllTextAsync(secondPath, "second document");
        var window = await OpenWorkspaceAsync(firstPath, secondPath);
        try
        {
            var group = Read<object>(ActiveTab(window), "Group");
            var tabs = Read<TabControl>(group, "Tabs");
            var items = tabs.Items.OfType<TabItem>().ToArray();
            Assert.Equal(2, items.Length);
            var selected = items[0];
            var inactive = items[1];
            tabs.SelectedItem = selected;
            window.MouseMove(new Point(-10, -10));
            await Task.Delay(30);

            var selectedSurface = TabSurface(selected);
            var inactiveSurface = TabSurface(inactive);
            var selectedBackground = SolidColor(selectedSurface.Background);
            var selectedAccent = SolidColor(selectedSurface.BorderBrush);
            var inactiveBackground = SolidColor(inactiveSurface.Background);
            var hoverPoint = inactiveSurface.TranslatePoint(
                new Point(inactiveSurface.Bounds.Width / 2, inactiveSurface.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("Inactive tab surface is not attached.");

            window.MouseMove(hoverPoint);
            await Task.Delay(30);

            Assert.NotEqual(inactiveBackground, SolidColor(inactiveSurface.Background));
            Assert.Equal(selectedBackground, SolidColor(selectedSurface.Background));
            Assert.Equal(selectedAccent, SolidColor(selectedSurface.BorderBrush));
            Assert.Same(selected, tabs.SelectedItem);
        }
        finally
        {
            await CloseAsync(window);
        }
    });
    [Fact]
    public Task ReloadCompletionDoesNotReopenClosedTab() => HeadlessTests.RunAsync(async profile =>
    {
        var first = Path.Combine(profile, "active.txt");
        var second = Path.Combine(profile, "closing.txt");
        await File.WriteAllTextAsync(first, "остаётся в окне");
        await File.WriteAllTextAsync(second, "закрывается при чтении");
        var window = await OpenWorkspaceAsync(first, second);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            var loaded = await DocumentFiles.ReadAsync(second);
            var pending = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reload = CallAsync(window, "ApplyEditorReloadAsync", tab, editor, pending.Task);
            await CallAsync(window, "CloseTabAsync", tab);
            pending.SetResult(loaded);
            await reload;

            Assert.Single(Tabs(window));
            Assert.Equal("остаётся в окне", Read<TextEditor>(ActiveTab(window), "Editor").Text);
            Assert.Null(Property(tab, "Editor"));
            Assert.Equal(0, editor.Document.TextLength);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task DetachedTabKeepsDocumentUndoAndReturnsWhenSecondWindowCloses() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "move.txt");
            await File.WriteAllTextAsync(path, "исходный документ");
            var original = await OpenWorkspaceAsync(path);
            MainWindow? detached = null;
            try
            {
                var tab = ActiveTab(original);
                var editor = Read<TextEditor>(tab, "Editor");
                var document = editor.Document;
                document.Insert(document.TextLength, " + правка");
                Assert.True(document.UndoStack.CanUndo);

                var item = Read<TabItem>(tab, "Item");
                var move = item.ContextMenu!.Items.OfType<MenuItem>()
                    .Single(menu => Equals(menu.Header, "Переместить в новое окно"));
                move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                detached = Assert.IsType<MainWindow>(original.FindOtherWindow());
                Assert.Empty(Tabs(original));
                var movedTab = ActiveTab(detached);
                Assert.Same(document, Read<TextEditor>(movedTab, "Editor").Document);
                Assert.Equal("исходный документ + правка", document.Text);
                Assert.True(Read<bool>(movedTab, "Dirty"));
                document.UndoStack.Undo();
                Assert.Equal("исходный документ", document.Text);
                Assert.False(Read<bool>(movedTab, "Dirty"));

                await CloseAsync(detached);
                Assert.Single(Tabs(original));
                Assert.Same(document, Read<TextEditor>(ActiveTab(original), "Editor").Document);
            }
            finally
            {
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                await CloseAsync(original);
            }
        });

    [Fact]
    public Task SplitButtonsAndClosingNestedGroupsPreserveDraftOrderAndActiveTabAfterRestart() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "original.txt");
            await File.WriteAllTextAsync(path, "оригинал");
            var window = await OpenWorkspaceAsync(path);
            var left = Field(window, "_active");
            Invoke(window, "NewTab", "первый черновик");
            var across = GroupButton(left, "│");
            Assert.Equal("Разделить слева и справа", ToolTip.GetTip(across));
            Click(across);
            Assert.Equal<Orientation?>(Orientation.Horizontal, Read<Orientation?>(Field(window, "_root"), "Axis"));
            var right = Field(window, "_active");
            Invoke(window, "NewTab", "второй черновик");
            Invoke(window, "NewTab", "третий черновик");
            var moved = ActiveTab(window);

            var below = GroupButton(right, "─");
            Assert.Equal("Разделить сверху и снизу", ToolTip.GetTip(below));
            Click(below);
            Assert.Equal<Orientation?>(Orientation.Vertical,
                Read<Orientation?>(Read<object>(Field(window, "_root"), "Second"), "Axis"));
            var bottom = Field(window, "_active");
            Assert.Same(moved, ActiveTab(window));
            Invoke(window, "MoveTab", moved, right, null);
            Assert.Empty(Read<TabControl>(bottom, "Tabs").Items);
            Click(GroupButton(bottom, "Закрыть группу"));
            Assert.Equal(2, Groups(window).Length);
            Click(GroupButton(right, "Закрыть группу"));
            Assert.Single(Groups(window));
            Assert.Same(moved, ActiveTab(window));
            Assert.False(GroupButton(left, "Закрыть группу").IsEnabled);
            Assert.Equal(["оригинал", "первый черновик", "второй черновик", "третий черновик"], TabTexts(window, left));
            await CloseAsync(window);

            var reopened = await OpenWorkspaceAsync();
            try
            {
                var group = Assert.Single(Groups(reopened));
                await HeadlessTests.WaitForAsync(() => Property(ActiveTab(reopened), "Editor") is TextEditor);
                Assert.Equal("третий черновик", Read<TextEditor>(ActiveTab(reopened), "Editor").Text);
                foreach (var item in Read<TabControl>(group, "Tabs").Items.OfType<TabItem>())
                {
                    var tab = Tabs(reopened).Single(tab => ReferenceEquals(Read<TabItem>(tab, "Item"), item));
                    await CallAsync(reopened, "ActivateDeferredAsync", tab);
                }
                Assert.Equal(["оригинал", "первый черновик", "второй черновик", "третий черновик"], TabTexts(reopened, group));
            }
            finally
            {
                await CloseAsync(reopened);
            }
        });

    [Fact]
    public Task ClosingLeftGroupKeepsTabsInVisualOrder() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "left.txt");
        await File.WriteAllTextAsync(path, "первый");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var left = Field(window, "_active");
            Invoke(window, "NewTab", "второй");
            Click(GroupButton(left, "│"));
            var right = Field(window, "_active");
            var activeDraft = ActiveTab(window);
            Invoke(window, "NewTab", "третий");
            var lastDraft = ActiveTab(window);

            Click(GroupButton(left, "Закрыть группу"));
            Assert.Same(right, Assert.Single(Groups(window)));
            Assert.Same(lastDraft, ActiveTab(window));
            Assert.True(Read<bool>(activeDraft, "Dirty"));
            Assert.Equal(["первый", "второй", "третий"], TabTexts(window, right));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task ClosedHistoryAndDeferredDraftSurviveFourChangedSessions() => HeadlessTests.RunAsync(async _ =>
    {
        var earlierText = "ранний\n" + string.Join('\n', Enumerable.Range(1, 220).Select(index => $"строка {index}"));
        const string recentText = "последний\nпоследний закрытый черновик";
        const string backgroundText = "отложенный\nтекст фоновой вкладки";
        var initial = await OpenWorkspaceAsync();
        Invoke(initial, "ShowGroups");
        Invoke(initial, "NewTab", earlierText);
        var earlier = Read<TextEditor>(ActiveTab(initial), "Editor");
        await HeadlessTests.WaitForAsync(() => earlier.GetVisualDescendants().OfType<ScrollViewer>()
            .Any(view => view.Extent.Height > view.Viewport.Height));
        var scroll = earlier.GetVisualDescendants().OfType<ScrollViewer>().First();
        earlier.Select(42, 9);
        earlier.CaretOffset = 42;
        scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, 320));
        await CallAsync(initial, "CloseTabAsync", ActiveTab(initial));

        Invoke(initial, "ShowGroups");
        Invoke(initial, "NewTab", recentText);
        await CallAsync(initial, "CloseTabAsync", ActiveTab(initial));
        Invoke(initial, "ShowGroups");
        Invoke(initial, "NewTab", backgroundText);
        Invoke(initial, "NewTab", "активный");
        await CloseAsync(initial);

        var working = await OpenWorkspaceAsync();
        await HeadlessTests.WaitForAsync(() => Property(ActiveTab(working), "Editor") is TextEditor);
        var deferred = Tabs(working).Single(tab => Property(tab, "Deferred") is SessionTab);
        var active = Read<TextEditor>(ActiveTab(working), "Editor");
        for (var generation = 1; generation <= 4; generation++)
        {
            active.Document.Insert(active.Document.TextLength, $" · {generation}");
            await CallAsync(working, "SaveSessionAsync");
            Assert.Null(Property(deferred, "Editor"));
        }
        await CloseAsync(working);

        var final = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => Property(ActiveTab(final), "Editor") is TextEditor);
            var background = Tabs(final).Single(tab => Property(tab, "Deferred") is SessionTab);
            await CallAsync(final, "ActivateDeferredAsync", background);
            Assert.Equal(backgroundText, Read<TextEditor>(background, "Editor").Text);

            const RawInputModifiers shortcut = RawInputModifiers.Control | RawInputModifiers.Shift;
            final.KeyPress(Key.T, shortcut, PhysicalKey.T, "t");
            final.KeyRelease(Key.T, shortcut, PhysicalKey.T, "t");
            await HeadlessTests.WaitForAsync(() => Property(ActiveTab(final), "Editor") is TextEditor editor && editor.Text == recentText);

            var closedMenu = (MenuItem)Field(final, "_closedMenu");
            var older = Assert.Single(closedMenu.Items.OfType<MenuItem>(), item =>
                item.Header is string title && title.StartsWith("ранний", StringComparison.Ordinal));
            older.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessTests.WaitForAsync(() => Property(ActiveTab(final), "Editor") is TextEditor editor && editor.Text == earlierText);
            earlier = Read<TextEditor>(ActiveTab(final), "Editor");
            await HeadlessTests.WaitForAsync(() => Math.Abs(earlier.VerticalOffset - 320) < 1);
            Assert.Equal(42, earlier.SelectionStart);
            Assert.Equal(9, earlier.SelectionLength);
            Assert.Equal(42, earlier.CaretOffset);
        }
        finally
        {
            await CloseAsync(final);
        }
    });

    [Fact]
    public Task NulInTextStartupDoesNotPreventOpeningAnotherFile() => HeadlessTests.RunAsync(async profile =>
    {
        var bad = Path.Combine(profile, "nul.txt");
        var good = Path.Combine(profile, "following.txt");
        await File.WriteAllBytesAsync(bad, [0x61, 0, 0x62]);
        await File.WriteAllTextAsync(good, "окно работает");
        var window = await OpenWorkspaceAsync(bad, good);
        try
        {
            Assert.Single(Tabs(window));
            Assert.Equal("окно работает", Read<TextEditor>(ActiveTab(window), "Editor").Text);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task TextMinimapClickDragAndWheelKeepCaretAndSelection() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "scroll.txt");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 1500).Select(index => $"Строка {index} · пример текста"));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var minimap = Assert.Single(editor.GetVisualParent()!.GetVisualChildren().OfType<DocumentMinimap>());
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            await HeadlessTests.WaitForAsync(() => scroll.Extent.Height > scroll.Viewport.Height && minimap.Bounds.Height > 10);
            editor.Select(3, 5);
            editor.CaretOffset = 8;
            var top = minimap.TranslatePoint(new Point(20, 1), window)!.Value;
            var middle = minimap.TranslatePoint(new Point(20, minimap.Bounds.Height / 2), window)!.Value;
            var bottom = minimap.TranslatePoint(new Point(20, minimap.Bounds.Height + 2), window)!.Value;
            var maximum = scroll.Extent.Height - scroll.Viewport.Height;

            window.MouseDown(middle, MouseButton.Left);
            Assert.InRange(scroll.Offset.Y, maximum * .45, maximum * .55);
            window.MouseMove(bottom, RawInputModifiers.LeftMouseButton);
            Assert.InRange(scroll.Offset.Y, maximum - 1, maximum + 1);
            window.MouseUp(bottom, MouseButton.Left);
            window.MouseWheel(middle, new Vector(0, 1));
            Assert.True(scroll.Offset.Y < maximum - 1);
            window.MouseDown(top, MouseButton.Left);
            window.MouseUp(top, MouseButton.Left);
            Assert.InRange(scroll.Offset.Y, 0, maximum * .01);
            Assert.Equal(3, editor.SelectionStart);
            Assert.Equal(5, editor.SelectionLength);
            Assert.Equal(8, editor.CaretOffset);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public void JsonTokensDistinguishKeysValuesAndEscapedQuotes()
    {
        const string line = """{"meta": {"say": "a\"b", "n": -1.25e+2, "flags": [true, false, null]}}""";
        var tokens = JsonColorizer.Tokenize(line)
            .Where(token => token.Kind != JsonTokenKind.Punctuation)
            .Select(token => (line.Substring(token.Start, token.Length), token.Kind)).ToArray();
        Assert.Equal(
            [("meta", JsonTokenKind.Key), ("say", JsonTokenKind.Key), ("""a\"b""", JsonTokenKind.String),
             ("n", JsonTokenKind.Key), ("-1.25e+2", JsonTokenKind.Number), ("flags", JsonTokenKind.Key),
             ("true", JsonTokenKind.Literal), ("false", JsonTokenKind.Literal), ("null", JsonTokenKind.Literal)],
            tokens);
        Assert.Contains(JsonColorizer.Tokenize(""" "key"  """, () => true), token => token.Kind == JsonTokenKind.Key);
        Assert.Contains(JsonColorizer.Tokenize(""" {"value": "unterminated"""), token => token.Kind == JsonTokenKind.String);
    }

    [Fact]
    public Task AnonymousJsonKeepsHighlightingWithValidationSelectionAndSearch() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", """{"enabled": true, "labels": ["имя", "🚀"]}""");
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            Assert.True(Assert.Single(editor.TextArea.TextView.LineTransformers.OfType<JsonColorizer>()).Enabled);
            editor.Select(2, 3);
            editor.CaretOffset = 5;
            editor.Document.Insert(editor.Document.TextLength - 1, ",");
            await HeadlessTests.WaitForAsync(() => Property(tab, "JsonError") is not null);
            ((TextBox)Field(window, "_searchInput")).Text = "labels";
            await CallAsync(window, "FindAsync");
            Assert.Single(((ListBox)Field(window, "_searchResults")).Items);
            Assert.True(Assert.Single(editor.TextArea.TextView.LineTransformers.OfType<JsonColorizer>()).Enabled);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task DialogUsesTheWorkspacePalette() => HeadlessTests.RunAsync(async _ =>
    {
        var main = await OpenWorkspaceAsync();
        try
        {
            var dialogTask = CallAsync(main, "ShowSettingsAsync");
            var dialog = Assert.Single(main.OwnedWindows);
            Assert.Equal(SolidColor(FocusTheme.Editor), SolidColor(main.Background));
            Assert.Equal(SolidColor(FocusTheme.Panel), SolidColor(dialog.Background));
            Assert.Equal(((ISolidColorBrush)main.Foreground!).Color, ((ISolidColorBrush)dialog.Foreground!).Color);
            Assert.NotEqual(Colors.Black, ((ISolidColorBrush)dialog.Background!).Color);
            dialog.Close();
            await dialogTask;
        }
        finally
        {
            await CloseAsync(main);
        }
    });

    private static Border TabSurface(TabItem item) => Assert.Single(item.GetVisualDescendants().OfType<Border>(), border => border.Name == "TabSurface");
    private static Color SolidColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static async Task<MainWindow> OpenWorkspaceAsync(params string[] paths)
    {
        var window = new MainWindow(paths);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible ||
            window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Не удалось записать сессию.", StringComparison.Ordinal) == true);
        Assert.False(window.IsVisible, window.FindControl<TextBlock>("StatusText")?.Text);
    }

    private static object ActiveTab(MainWindow window) => Property(window, "ActiveTab", Private)!;
    private static object[] Groups(MainWindow window) => ((System.Collections.IEnumerable)Invoke(window, "Groups", [null])!).Cast<object>().ToArray();
    private static IEnumerable<object> Tabs(MainWindow window) => ((System.Collections.IDictionary)Field(window, "_tabs")).Values.Cast<object>();

    private static string[] TabTexts(MainWindow window, object group) => Read<TabControl>(group, "Tabs")
        .Items.OfType<TabItem>().Select(item => Read<TextEditor>(
            Tabs(window).Single(tab => ReferenceEquals(Read<TabItem>(tab, "Item"), item)), "Editor").Text).ToArray();

    private static Button GroupButton(object group, string name)
    {
        var tools = Assert.IsType<StackPanel>(Read<WorkspaceTabs>(group, "Tabs").HeaderActions);
        return Assert.Single(tools.Children.OfType<Button>(), button => Equals(button.Content, name)
            || string.Equals(Avalonia.Automation.AutomationProperties.GetName(button), name, StringComparison.Ordinal));
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);

    private static Task CallAsync(MainWindow window, string name, params object?[] args) => (Task)Invoke(window, name, args)!;
    private static object? Property(object target, string name, BindingFlags binding = BindingFlags.Instance | BindingFlags.Public) =>
        target.GetType().GetProperty(name, binding)!.GetValue(target);

    private static T Read<T>(object target, string name) => (T)Property(target, name)!;
}
