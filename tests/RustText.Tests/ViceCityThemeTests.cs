using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class ViceCityThemeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task ExistingWindowRepaintsViceCityAndRestoresFocus() => HeadlessTests.RunAsync(async _ =>
    {
        var window = new MainWindow([]) { Width = 1080, Height = 700 };
        Window? dialog = null;
        window.Show();
        await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
        try
        {
            typeof(MainWindow).GetMethod("NewTab", Private)!.Invoke(window,
                ["Русский текст для выделения\nВторая строка документа"]);
            var tab = typeof(MainWindow).GetProperty("ActiveTab", Private)!.GetValue(window)!;
            var editor = (TextEditor)tab.GetType().GetProperty("Editor")!.GetValue(tab)!;
            var document = editor.Document;
            editor.Select(0, 7);
            var selectedText = editor.SelectedText;
            var dragArea = window.FindControl<Border>("TitleDragArea")!;
            var palm = window.FindControl<Avalonia.Controls.Shapes.Path>("TitlePalm")!;
            var button = new Button { Content = "Отключить проверку", Margin = new Thickness(16) };
            dialog = MainWindow.Dialog("Проверка темы", button);
            dialog.Show(window);

            await HeadlessTests.WaitForAsync(() => dragArea.Bounds.Width > 100
                && editor.TextArea.TextView.VisualLinesValid && button.Bounds.Width > 0);
            var focusPixels = HeaderPixels(window, dragArea);
            var focusCaret = Assert.IsAssignableFrom<ISolidColorBrush>(editor.TextArea.CaretBrush).Color;
            var focusButton = ButtonColors(button);
            Assert.Equal(focusPixels.Left, focusPixels.Right);
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(palm.Fill).Color.A);

            FocusTheme.Apply(AppTheme.ViceCity);
            await HeadlessTests.WaitForAsync(() =>
            {
                var pixels = HeaderPixels(window, dragArea);
                return pixels.Left != pixels.Right && pixels != focusPixels;
            });

            Assert.False(palm.IsHitTestVisible);
            Assert.NotEqual(0, Assert.IsAssignableFrom<ISolidColorBrush>(palm.Fill).Color.A);
            Assert.Same(FocusTheme.Accent, editor.TextArea.CaretBrush);
            Assert.NotEqual(focusCaret, Assert.IsAssignableFrom<ISolidColorBrush>(editor.TextArea.CaretBrush).Color);
            var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(editor.TextArea.SelectionForeground).Color;
            var background = Assert.IsAssignableFrom<ISolidColorBrush>(editor.TextArea.SelectionBrush).Color;
            Assert.True(Contrast(foreground, background) >= 4.5, "Selected text must remain readable in Vice City.");
            Assert.Same(document, editor.Document);
            Assert.Equal(selectedText, editor.SelectedText);
            var viceButton = ButtonColors(button);
            Assert.NotEqual(focusButton.Background, viceButton.Background);
            Assert.NotEqual(focusButton.Foreground, viceButton.Foreground);
            Assert.Equal(focusButton.Border, viceButton.Border);
            Assert.Equal(0, viceButton.Border.A);

            FocusTheme.Apply(AppTheme.Focus);
            await HeadlessTests.WaitForAsync(() => HeaderPixels(window, dragArea) == focusPixels);
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(palm.Fill).Color.A);
            Assert.Equal(focusCaret, Assert.IsAssignableFrom<ISolidColorBrush>(editor.TextArea.CaretBrush).Color);
            Assert.Equal(selectedText, editor.SelectedText);
            Assert.Equal(focusButton, ButtonColors(button));
        }
        finally
        {
            dialog?.Close();
            FocusTheme.Apply(AppTheme.Focus);
            window.Close();
            await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_closed"));
        }
    });

    private static (Color Background, Color Foreground, Color Border) ButtonColors(Button button) =>
        (Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(button.Foreground).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(button.BorderBrush).Color);

    private static (int Left, int Right) HeaderPixels(MainWindow window, Border dragArea)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var origin = dragArea.TranslatePoint(default, window)!.Value;
        var y = (int)((origin.Y + 3) * window.RenderScaling);
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            // Compare raw pixels so the assertion works with RGBA and BGRA framebuffers.
            int Read(double x)
            {
                var position = (int)((origin.X + x) * window.RenderScaling);
                frame.CopyPixels(new PixelRect(position, y, 1, 1), buffer, 4, 4);
                return Marshal.ReadInt32(buffer);
            }
            return (Read(8), Read(dragArea.Bounds.Width - 12));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) =>
            0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
}
