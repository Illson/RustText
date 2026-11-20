using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace RustText.Demo;

internal static class DialogWindowChrome
{
    public static void Apply(Window window)
    {
        var content = window.Content as Control
            ?? throw new InvalidOperationException("A dialog must have content before applying its chrome.");

        window.WindowDecorations = WindowDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = 0;
        window.Content = null;
        window.CanMinimize = false;
        if (!window.CanResize)
            window.CanMaximize = false;

        var maximizeGlyph = new Path
        {
            Width = 12,
            Height = 12,
            Data = Geometry.Parse("M 1,1 L 11,1 11,11 1,11 Z"),
            Stroke = FocusTheme.Text,
            StrokeThickness = 1.25
        };
        var restoreGlyph = new Path
        {
            Width = 12,
            Height = 12,
            IsVisible = false,
            Data = Geometry.Parse("M 4,1 L 11,1 11,8 M 1,4 L 8,4 8,11 1,11 Z"),
            Stroke = FocusTheme.Text,
            StrokeThickness = 1.25
        };
        var maximizeContent = new Grid
        {
            Width = 12,
            Height = 12,
            Children = { maximizeGlyph, restoreGlyph }
        };
        var maximize = CaptionButton("DialogChromeMaximize", "Развернуть", "Развернуть", () =>
        {
            if (window.CanMaximize)
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
        }, maximizeContent);
        maximize.IsVisible = window.CanMaximize;
        WindowDecorationProperties.SetElementRole(maximize, WindowDecorationsElementRole.MaximizeButton);

        var close = CaptionButton("DialogChromeClose", "Закрыть диалог", "Закрыть", window.Close, new Path
        {
            Width = 12,
            Height = 12,
            Data = Geometry.Parse("M 1,1 L 11,11 M 11,1 L 1,11"),
            Stroke = FocusTheme.Text,
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round
        });
        close.Classes.Add("focus-caption-close");

        window.PropertyChanged += (_, args) =>
        {
            if (args.Property != Window.WindowStateProperty)
                return;

            var maximized = window.WindowState == WindowState.Maximized;
            maximizeGlyph.IsVisible = !maximized;
            restoreGlyph.IsVisible = maximized;
            var name = maximized ? "Восстановить размер окна" : "Развернуть";
            AutomationProperties.SetName(maximize, name);
            ToolTip.SetTip(maximize, name);
        };

        var title = new TextBlock
        {
            Text = window.Title,
            FontWeight = FontWeight.SemiBold,
            Foreground = FocusTheme.Text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        AutomationProperties.SetAutomationId(title, "DialogChromeTitle");
        AutomationProperties.SetName(title, window.Title);

        var titleRegion = new Border
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Child = title
        };
        AutomationProperties.SetAutomationId(titleRegion, "DialogChromeDragRegion");
        titleRegion.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(titleRegion).Properties.IsLeftButtonPressed)
                return;

            args.Handled = true;
            if (args.ClickCount == 2 && window.CanMaximize)
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            else
                window.BeginMoveDrag(args);
        };

        var captions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { maximize, close }
        };
        var titleBar = new Border
        {
            Background = FocusTheme.TitleBar,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            MinHeight = 36,
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(14, 0, 0, 0),
                Children = { titleRegion, captions }
            }
        };
        Grid.SetColumn(captions, 1);
        AutomationProperties.SetAutomationId(titleBar, "DialogChromeTitleBar");

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { titleBar, content }
        };
        Grid.SetRow(content, 1);

        var root = new Border
        {
            Background = FocusTheme.Panel,
            Child = layout
        };
        AutomationProperties.SetAutomationId(root, "DialogChromeRoot");
        window.Content = root;
        window.Classes.Add("focus-dialog");

        void ApplyFrameColor()
        {
            if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
                WindowFrameColor.SetBorderColor(handle.Handle, WindowFrameColor.ToColorRef(FocusTheme.ChromeColor));
        }

        window.Opened += OnOpened;
        window.Closed += OnClosed;

        void OnOpened(object? sender, EventArgs args)
        {
            FocusTheme.ThemeChanged += ApplyFrameColor;
            ApplyFrameColor();
        }

        void OnClosed(object? sender, EventArgs args)
        {
            FocusTheme.ThemeChanged -= ApplyFrameColor;
        }
    }

    private static Button CaptionButton(string automationId, string name, string tooltip, Action action, Control content)
    {
        var button = new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("focus-caption");
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }
}
