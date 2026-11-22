using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

public sealed class EncryptedTabFileTests
{
    private const string Password = "Ключ от шкафа 92 🌿";

    [Theory]
    [InlineData("")]
    [InlineData("Секретная строка 🌲\r\nВторая строка\n")]
    public void RoundTripsTextAndExposesOnlyTheHint(string text)
    {
        const string hint = "Домашнее растение";
        var data = EncryptedTabFile.Encrypt(text, Password, hint);
        Assert.Equal(hint, EncryptedTabFile.ReadHint(data));
        Assert.Equal(text, EncryptedTabFile.Decrypt(data, Password));
        if (text.Length != 0) Assert.Equal(-1, data.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(-1, data.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Password)));
        Assert.False(data.AsSpan().SequenceEqual(EncryptedTabFile.Encrypt(text, Password, hint)));
    }

    [Fact]
    public void RejectsWrongPasswordAndTamperedHintOrContent()
    {
        var data = EncryptedTabFile.Encrypt("private", Password, "public hint");
        Assert.Throws<InvalidDataException>(() => EncryptedTabFile.Decrypt(data, "неправильный пароль"));
        var content = data.ToArray();
        content[^17] ^= 1;
        Assert.Throws<InvalidDataException>(() => EncryptedTabFile.Decrypt(content, Password));
        var hint = "public hint"u8;
        var index = data.AsSpan().IndexOf(hint);
        Assert.True(index >= 0);
        data[index] = (byte)'P';
        Assert.Equal("Public hint", EncryptedTabFile.ReadHint(data));
        Assert.Throws<InvalidDataException>(() => EncryptedTabFile.Decrypt(data, Password));
    }

    [Fact]
    public void RejectsTruncatedUnknownAndInvalidlySizedFiles()
    {
        var data = EncryptedTabFile.Encrypt("", Password, "");
        Assert.Throws<InvalidDataException>(() => EncryptedTabFile.ReadHint(data[..^1]));
        var unknownVersion = data.ToArray();
        unknownVersion[7] = (byte)'9';
        Assert.Throws<InvalidDataException>(() => EncryptedTabFile.ReadHint(unknownVersion));
        data[8] = 1;
        Assert.Throws<InvalidDataException>(() => EncryptedTabFile.ReadHint(data));
        Assert.Throws<ArgumentException>(() => EncryptedTabFile.Encrypt("text", Password, new string('я', 513)));
        Assert.Throws<ArgumentException>(() => EncryptedTabFile.Encrypt("text", "short", ""));
    }

    [Fact]
    public async Task WritesOnlyNewFilesAndCleansUpWhenTheDestinationAlreadyExists()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "копия.rte");
        var existing = Path.Combine(directory.Path, "исходный.txt");
        await File.WriteAllTextAsync(existing, "original text");
        var data = EncryptedTabFile.Encrypt("unsaved revision", Password, "подсказка");
        await Assert.ThrowsAnyAsync<IOException>(() => EncryptedTabFile.WriteNewAsync(existing, data, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EncryptedTabFile.WriteNewAsync(path, data, cancelled.Token));
        Assert.False(File.Exists(path));
        await EncryptedTabFile.WriteNewAsync(path, data, CancellationToken.None);
        var read = await EncryptedTabFile.ReadAsync(path, CancellationToken.None);
        Assert.Equal("unsaved revision", EncryptedTabFile.Decrypt(read, Password));
        Assert.Equal("original text", await File.ReadAllTextAsync(existing));
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }
}

[Collection(HeadlessCollection.Name)]
public sealed class EncryptedTabDialogTests
{
    private const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    private static bool ReadFlag(MainWindow window, string name) => (bool)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;

    [Fact]
    public Task EncryptsUnsavedTabAndRetriesAfterTheWrongPassword() => HeadlessTests.RunAsync(async profile =>
    {
        var source = Path.Combine(profile, "source.txt");
        var target = Path.Combine(profile, "copy.rte");
        const string original = "Первая версия";
        const string changed = "Несохранённые изменения 🌿";
        const string password = "Случайный тестовый пароль";
        const string hint = "Подсказка про растение";
        await File.WriteAllTextAsync(source, original);
        var window = new MainWindow([source]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished") && window.GetVisualDescendants()
                .OfType<TextEditor>().Any(editor => editor.Text == original));
            var document = Assert.Single(window.GetVisualDescendants().OfType<TextEditor>(), editor => editor.Text == original);
            document.Text = changed;

            var saving = window.EncryptActiveTabAsync(() => Task.FromResult<string?>(target));
            var create = await FindDialogAsync("Зашифровать копию вкладки");
            Assert.Contains(create.GetVisualDescendants().OfType<TextBlock>(), label =>
                label.Text is { } warning && warning.Contains("только новая копия .rte", StringComparison.Ordinal)
                && warning.Contains("черновиках без шифрования", StringComparison.Ordinal)
                && warning.Contains("ещё нет файла", StringComparison.Ordinal));
            Field(create, "Пароль").Text = password;
            Field(create, "Повторите пароль").Text = password;
            Field(create, "Подсказка (необязательно)").Text = hint;
            Click(create, "Зашифровать");
            await saving;
            Assert.Equal(original, await File.ReadAllTextAsync(source));
            Assert.Equal(changed, document.Text);

            var opening = window.DecryptTabPathAsync(target);
            var unlock = await FindDialogAsync("Расшифровать файл");
            Assert.Contains(unlock.GetVisualDescendants().OfType<TextBlock>(), label => label.Text?.Contains(hint) == true);
            Field(unlock, "Пароль").Text = "wrong password";
            Click(unlock, "Расшифровать");
            var retry = await FindDialogAsync("Расшифровать файл");
            Assert.NotSame(unlock, retry);
            Assert.Contains(retry.GetVisualDescendants().OfType<TextBlock>(), label => label.Text?.Contains("Неверный пароль") == true);
            Field(retry, "Пароль").Text = password;
            Click(retry, "Расшифровать");
            var result = await FindDialogAsync("Расшифрованный текст");
            Assert.Equal(changed, Assert.Single(result.GetVisualDescendants().OfType<TextEditor>()).Text);
            result.Close();
            await opening;

            async Task<Window> FindDialogAsync(string title)
            {
                await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog => dialog.Title == title));
                var dialog = Assert.Single(window.OwnedWindows, dialog => dialog.Title == title);
                dialog.UpdateLayout();
                return dialog;
            }
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed"));
        }

        static TextBox Field(Window dialog, string placeholder) =>
            Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(), box => Equals(box.PlaceholderText, placeholder));
        static void Click(Window dialog, string label) =>
            Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, label))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    });

    [Fact]
    public Task DecryptedTextOnlyAppearsInAReadOnlyDialog() => HeadlessTests.RunAsync(async profile =>
    {
        const string secret = "not-stored-by-rusttext-941c";
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
            var pending = MainWindow.ShowDecryptedTextAsync(window, secret);
            var dialog = Assert.Single(window.OwnedWindows);
            dialog.UpdateLayout();
            var viewer = Assert.Single(dialog.GetVisualDescendants().OfType<TextEditor>());
            Assert.True(viewer.IsReadOnly);
            Assert.Equal(secret, viewer.Text);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextEditor>(), editor => editor.Text == secret);
            dialog.Close();
            await pending;
            Assert.Empty(viewer.Text);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed"));
        }
        foreach (var path in Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)), StringComparison.Ordinal);
    });
}
