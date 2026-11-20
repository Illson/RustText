using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class ExternalEncodingRefreshTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task AutoDetectedEditorRefreshesToUtf16AndSavesTheNewBom() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "external-auto-encoding.txt");
        await File.WriteAllTextAsync(path, "initial text", new UTF8Encoding(false));
        var window = await OpenEditorWindowAsync(path, null);
        try
        {
            var tab = Property(window, "ActiveTab")!;
            await File.WriteAllTextAsync(path, "новый текст", Encoding.Unicode);

            await HeadlessTests.WaitForAsync(() => Property(tab, "Editor") is TextEditor editor
                && editor.Text == "новый текст"
                && Equals(Property(tab, "Encoding"), TextEncodingKind.Utf16LittleEndian));

            var editor = (TextEditor)Property(tab, "Editor")!;
            editor.Document.Insert(editor.Document.TextLength, "!");
            await InvokeTask(window, "SaveAsync", false);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal([0xFF, 0xFE], bytes[..2]);
            Assert.Equal("новый текст!", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    });

    [Fact]
    public Task RestoredAutoDetectedEditorTracksANewBomEncoding() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "auto-encoding.txt");
        await File.WriteAllTextAsync(path, "initial text", new UTF8Encoding(false));
        var firstWindow = await OpenEditorWindowAsync(path, null);
        await CloseWindowAsync(firstWindow);

        await File.WriteAllTextAsync(path, "новый текст", Encoding.Unicode);
        var restoredWindow = await OpenEditorWindowAsync(null, null);
        try
        {
            var tab = Property(restoredWindow, "ActiveTab")!;
            await HeadlessTests.WaitForAsync(() => Property(tab, "Editor") is TextEditor editor
                && editor.Text == "новый текст"
                && Equals(Property(tab, "Encoding"), TextEncodingKind.Utf16LittleEndian));

            Assert.Equal("новый текст", ((TextEditor)Property(tab, "Editor")!).Text);
        }
        finally
        {
            await CloseWindowAsync(restoredWindow);
        }
    });

    [Fact]
    public Task RestoredWindows1251ChoiceRemainsExplicitForExternalChanges() => HeadlessTests.RunAsync(async profile =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var windows1251 = Encoding.GetEncoding(1251);
        var path = Path.Combine(profile, "explicit-encoding.txt");
        await File.WriteAllBytesAsync(path, windows1251.GetBytes("исходный текст"));
        var firstWindow = await OpenEditorWindowAsync(path, TextEncodingKind.Windows1251);
        await CloseWindowAsync(firstWindow);

        var restoredWindow = await OpenEditorWindowAsync(null, null);
        try
        {
            var tab = Property(restoredWindow, "ActiveTab")!;
            await HeadlessTests.WaitForAsync(() => Property(tab, "Editor") is TextEditor editor
                && editor.Text == "исходный текст");
            await File.WriteAllBytesAsync(path, windows1251.GetBytes("новый текст"));
            await HeadlessTests.WaitForAsync(() => ((TextEditor)Property(tab, "Editor")!).Text == "новый текст");

            var editor = (TextEditor)Property(tab, "Editor")!;
            editor.Document.Insert(editor.Document.TextLength, "!");
            await InvokeTask(restoredWindow, "SaveAsync", false);
            Assert.Equal(windows1251.GetBytes("новый текст!"), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            await CloseWindowAsync(restoredWindow);
        }
    });

    private static async Task<MainWindow> OpenEditorWindowAsync(string? path, TextEncodingKind? encoding)
    {
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        if (path is not null)
        {
            await InvokeTask(window, "OpenPathAsync", path, false, encoding);
        }
        else
        {
            await HeadlessTests.WaitForAsync(() => Property(window, "ActiveTab") is { } tab
                && Property(tab, "Editor") is TextEditor);
        }
        return window;
    }

    private static async Task CloseWindowAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closed"));
    }

    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static object? Property(object owner, string name) =>
        owner.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(owner);

    private static async Task InvokeTask(object owner, string name, params object?[] arguments)
    {
        var task = (Task)(owner.GetType().GetMethod(name, Private | BindingFlags.Public)!
            .Invoke(owner, arguments) ?? throw new InvalidOperationException($"{name} returned null."));
        await task;
    }
}
