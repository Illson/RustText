using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    }

    [Fact]
    public void PasswordMustNotBeEmptyButSpacesAreKeptAsEntered()
    {
        Assert.Throws<ArgumentException>(() => EncryptedTabFile.Encrypt("text", "", ""));
        var data = EncryptedTabFile.Encrypt("text", " ", "");
        Assert.Throws<ArgumentException>(() => EncryptedTabFile.Decrypt(data, ""));
        Assert.Equal("text", EncryptedTabFile.Decrypt(data, " "));
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

    [Theory]
    [InlineData(1)]
    [InlineData(1500)]
    public Task EncryptsUnsavedTabAndRetriesAfterTheWrongPassword(int passwordLength) => HeadlessTests.RunAsync(async profile =>
    {
        var source = Path.Combine(profile, "source.txt");
        var target = Path.Combine(profile, "copy.rte");
        const string original = "Первая версия";
        const string changed = "Несохранённые изменения 🌿";
        var password = new string('я', passwordLength);
        const string hint = "Подсказка про растение";
        await File.WriteAllTextAsync(source, original);
        var storage = StorageService.OpenDefault();
        var oldBookmark = new SessionTab(12, source, TextEncodingKind.Utf8);
        var other = new SessionTab(13, Path.Combine(profile, "other.txt"), TextEncodingKind.Utf8);
        await storage.AddRecentAsync(new(other.Path!, TextEncodingKind.Utf8));
        await storage.SaveSessionAsync(new([], [oldBookmark, other]));
        var window = new MainWindow([source]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished") && window.GetVisualDescendants()
                .OfType<TextEditor>().Any(editor => editor.Text == original));
            var document = Assert.Single(window.GetVisualDescendants().OfType<TextEditor>(), editor => editor.Text == original);
            document.Text = changed;
            await SaveSessionAsync(window);
            var saved = Assert.Single((await storage.LoadSessionAsync())!.Tabs);
            var draft = storage.GetDraftPath(saved);
            Assert.True(File.Exists(draft));

            var saving = window.EncryptActiveTabAsync(() => Task.FromResult<string?>(target));
            var create = await WaitForDialogAsync(window, "Зашифровать копию вкладки");
            Assert.Contains(create.GetVisualDescendants().OfType<TextBlock>(), label =>
                label.Text is { } warning && warning.Contains("вкладка закроется", StringComparison.Ordinal)
                && warning.Contains("Исходный файл на диске не изменится", StringComparison.Ordinal));
            Click(create, "Зашифровать");
            Assert.Contains(create.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Введите пароль для шифрования.");
            Assert.False(File.Exists(target));
            Field(create, "Пароль").Text = password;
            Field(create, "Повторите пароль").Text = password;
            Field(create, "Подсказка (необязательно)").Text = hint;
            Click(create, "Зашифровать");
            await saving;
            Assert.Equal(original, await File.ReadAllTextAsync(source));
            Assert.Empty(document.Text);
            Assert.Empty(window.GetVisualDescendants().OfType<TextEditor>());
            Assert.False(File.Exists(draft));
            Assert.Equal(other.Path, Assert.Single(await storage.GetRecentFilesAsync()).Path);
            AssertNoEncryptedHistory(await storage.LoadSessionAsync(), source, target, saved.Id);
            AssertNoEncryptedHistory(await ReadCheckpointAsync(storage, "previous"), source, target, saved.Id);

            var opening = window.ReceivePathsAsync([target], activate: false);
            var unlock = await WaitForDialogAsync(window, "Расшифровать файл");
            Assert.Contains(unlock.GetVisualDescendants().OfType<TextBlock>(), label => label.Text?.Contains(hint) == true);
            Field(unlock, "Пароль").Text = "wrong password";
            Click(unlock, "Расшифровать");
            var retry = await WaitForDialogAsync(window, "Расшифровать файл");
            Assert.NotSame(unlock, retry);
            Assert.Contains(retry.GetVisualDescendants().OfType<TextBlock>(), label => label.Text?.Contains("Неверный пароль") == true);
            Field(retry, "Пароль").Text = password;
            Click(retry, "Расшифровать");
            var result = await WaitForDialogAsync(window, "Расшифрованный текст");
            Assert.Equal(changed, Assert.Single(result.GetVisualDescendants().OfType<TextEditor>()).Text);
            result.Close();
            await opening;
            Assert.Equal(other.Path, Assert.Single(await storage.GetRecentFilesAsync()).Path);
            AssertNoEncryptedHistory(await storage.LoadSessionAsync(), source, target, saved.Id);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task StartupWithAnEncryptedPathPromptsAndCancellationDoesNotCreateHistory() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "startup.RTE");
        await EncryptedTabFile.WriteNewAsync(path, EncryptedTabFile.Encrypt("не открывать без пароля", "1", "подсказка запуска"), CancellationToken.None);
        var storage = StorageService.OpenDefault();
        await storage.AddRecentAsync(new(path, TextEncodingKind.Utf8));
        var window = new MainWindow([path]);
        window.Show();
        try
        {
            var prompt = await WaitForDialogAsync(window, "Расшифровать файл");
            Assert.Contains(prompt.GetVisualDescendants().OfType<TextBlock>(), label => label.Text?.Contains("подсказка запуска") == true);
            Click(prompt, "Отмена");
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
            Assert.Empty(window.GetVisualDescendants().OfType<TextEditor>());
            Assert.Empty(await storage.GetRecentFilesAsync());
        }
        finally
        {
            await CloseAsync(window);
        }
        var state = await storage.LoadSessionAsync();
        Assert.Empty(state?.Tabs ?? []);
        Assert.Empty(state?.ClosedTabs ?? []);
    });

    [Fact]
    public Task ALockedPlaintextDraftDoesNotHideTheCleanupFailureOrUndoTheEncryptedCopy() => HeadlessTests.RunAsync(async profile =>
    {
        const string secret = "недоступный для удаления черновик";
        var storage = StorageService.OpenDefault();
        var target = Path.Combine(profile, "locked-draft.rte");
        var draft = await storage.SaveClosedDraftAsync(new(3, null, TextEncodingKind.Utf8), secret);
        await storage.SaveSessionAsync(new([draft]));
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished") && window.GetVisualDescendants()
                .OfType<TextEditor>().Any(editor => editor.Text == secret));
            await using var held = new FileStream(storage.GetDraftPath(draft)!, FileMode.Open, FileAccess.Read, FileShare.Read);
            var saving = window.EncryptActiveTabAsync(() => Task.FromResult<string?>(target));
            SubmitTestPassword(GetCreateDialog(window));
            await saving;
            Assert.Empty(window.GetVisualDescendants().OfType<TextEditor>());
            Assert.Empty((await storage.LoadSessionAsync())!.Tabs);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                label => label.Text?.Contains("не удалось полностью очистить историю", StringComparison.Ordinal) == true);
            Assert.Equal(secret, EncryptedTabFile.Decrypt(await EncryptedTabFile.ReadAsync(target, CancellationToken.None), "1"));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task DroppingAnEncryptedPathStillOpensTheOtherFilesAndNeverAddsTheContainerToHistory() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "dropped.rte");
        var plain = Path.Combine(profile, "note.txt");
        const string text = "обычный документ";
        await EncryptedTabFile.WriteNewAsync(path, EncryptedTabFile.Encrypt("private", "1", ""), CancellationToken.None);
        await File.WriteAllTextAsync(plain, text);
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
            var encryptedItem = await window.StorageProvider.TryGetFileFromPathAsync(path);
            var plainItem = await window.StorageProvider.TryGetFileFromPathAsync(plain);
            Assert.NotNull(encryptedItem);
            Assert.NotNull(plainItem);
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(encryptedItem));
            transfer.Add(DataTransferItem.CreateFile(plainItem));
            var drop = new DragEventArgs(DragDrop.DropEvent, transfer, window, default, KeyModifiers.None);
            window.RaiseEvent(drop);
            Assert.True(drop.Handled);
            Click(await WaitForDialogAsync(window, "Расшифровать файл"), "Отмена");
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<TextEditor>().Any(editor => editor.Text == text));

            await File.WriteAllTextAsync(path, "damaged .rte");
            await window.ReceivePathsAsync([path], activate: false);
            Assert.Empty(window.OwnedWindows);
            Assert.Single(window.GetVisualDescendants().OfType<TextEditor>(), editor => editor.Text == text);
            await SaveSessionAsync(window);
            var storage = StorageService.OpenDefault();
            Assert.Equal(plain, Assert.Single(await storage.GetRecentFilesAsync()).Path);
            Assert.Equal(plain, Assert.Single((await storage.LoadSessionAsync())!.Tabs).Path);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task UntitledTabSurvivesCancellationAndFailedWriteButIsForgottenAfterSuccess() => HeadlessTests.RunAsync(async profile =>
    {
        const string secret = "текущий черновик ~417c4e";
        const string earlier = "прежняя правка того же черновика ~8c5343";
        const string other = "соседний черновик ~da1662";
        const string unrelatedArchive = "независимый архив с повторно использованным номером вкладки";
        var target = Path.Combine(profile, "untitled.rte");
        var storage = StorageService.OpenDefault();
        var closed = await storage.SaveClosedDraftAsync(new SessionTab(3, null, TextEncodingKind.Utf8)
            { ClosedAtMs = DateTimeOffset.UtcNow.AddHours(-6).ToUnixTimeMilliseconds() }, earlier);
        var neighbour = await storage.SaveClosedDraftAsync(new(4, null, TextEncodingKind.Utf8), other);
        var unrelated = await storage.SaveClosedDraftAsync(new SessionTab(3, null, TextEncodingKind.Utf8)
            { ClosedAtMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() }, unrelatedArchive);
        var unrelatedEntry = await storage.DraftArchive.ArchiveAsync(unrelated, storage.GetDraftPath(unrelated)!);
        await storage.DraftArchive.ArchiveAsync(closed, storage.GetDraftPath(closed)!);
        var window = new MainWindow([]);
        await storage.SaveSessionAsync(new([closed], [neighbour]));
        window.Show();

        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished") && window.GetVisualDescendants()
                .OfType<TextEditor>().Any(editor => editor.Text == earlier));
            var document = Assert.Single(window.GetVisualDescendants().OfType<TextEditor>());
            document.Text = secret;
            await SaveSessionAsync(window);

            var cancelled = window.EncryptActiveTabAsync(() => Task.FromResult<string?>(target));
            var create = GetCreateDialog(window);
            Click(create, "Отмена");
            await cancelled;
            Assert.Equal(secret, document.Text);
            Assert.False(File.Exists(target));

            var collision = window.EncryptActiveTabAsync(() => Task.FromResult<string?>(target));
            create = GetCreateDialog(window);
            await File.WriteAllTextAsync(target, "already taken");
            SubmitTestPassword(create);
            await Assert.ThrowsAnyAsync<IOException>(() => collision);
            Assert.Equal("already taken", await File.ReadAllTextAsync(target));
            Assert.Equal(secret, document.Text);
            Assert.Equal(secret, await storage.LoadDraftAsync(Assert.Single((await storage.LoadSessionAsync())!.Tabs)));
            Assert.Equal(2, (await storage.DraftArchive.GetEntriesAsync()).Count);
            File.Delete(target);

            var saved = window.EncryptActiveTabAsync(() => Task.FromResult<string?>(target));
            SubmitTestPassword(GetCreateDialog(window));
            await saved;
            Assert.Empty(document.Text);
            Assert.Empty(window.GetVisualDescendants().OfType<TextEditor>());
            Assert.Equal(unrelatedEntry.Id, Assert.Single(await storage.DraftArchive.GetEntriesAsync()).Id);
            var restoredArchive = await storage.DraftArchive.RestoreAsync(unrelatedEntry);
            Assert.Equal(unrelatedArchive, await storage.LoadDraftAsync(restoredArchive));
            foreach (var state in new[] { await storage.LoadSessionAsync(), await ReadCheckpointAsync(storage, "previous") })
            {
                Assert.Empty(state!.Tabs);
                Assert.Equal(other, await storage.LoadDraftAsync(Assert.Single(state.ClosedTabs!)));
            }
            foreach (var path in Directory.EnumerateFiles(storage.RootDirectory, "*", SearchOption.AllDirectories))
            {
                var data = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
                Assert.DoesNotContain(secret, data, StringComparison.Ordinal);
                Assert.DoesNotContain(earlier, data, StringComparison.Ordinal);
            }
            Assert.Equal(secret, EncryptedTabFile.Decrypt(await EncryptedTabFile.ReadAsync(target, CancellationToken.None), "1"));
        }
        finally
        {
            await CloseAsync(window);
        }
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

    [Fact]
    public Task EncryptingANewNotePreservesAnIndependentArchivedCopyOfItsTemplate() => HeadlessTests.RunAsync(async profile =>
    {
        const string template = "План на день";
        var storage = StorageService.OpenDefault();
        var old = await storage.SaveClosedDraftAsync(new SessionTab(8, null, TextEncodingKind.Utf8)
            { CustomTitle = "Вчера", ClosedAtMs = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds() }, template);
        var archive = await storage.DraftArchive.ArchiveAsync(old, storage.GetDraftPath(old)!);
        await storage.SaveSessionAsync(new([new SessionTab(8, null, TextEncodingKind.Utf8) { CustomTitle = "Сегодня" }]),
            new Dictionary<long, string> { [8] = template });
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished") && window.GetVisualDescendants()
                .OfType<TextEditor>().Any(editor => editor.Text == template));
            const string updated = "План на сегодня: готово";
            window.GetVisualDescendants().OfType<TextEditor>().Single().Text = updated;
            await SaveSessionAsync(window);
            var target = Path.Combine(profile, "today.rte");
            var saving = window.EncryptActiveTabAsync(() => Task.FromResult<string?>(target));
            SubmitTestPassword(GetCreateDialog(window));
            await saving;
            Assert.Equal(updated, EncryptedTabFile.Decrypt(await EncryptedTabFile.ReadAsync(target, default), "1"));
            Assert.Equal(archive.Id, Assert.Single(await storage.DraftArchive.GetEntriesAsync()).Id);
            var restored = await storage.DraftArchive.RestoreAsync(archive);
            Assert.Equal("Вчера", restored.CustomTitle);
            Assert.Equal(template, await storage.LoadDraftAsync(restored));
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OrdinarySaveAsRefusesNewAndExistingEncryptedPaths(bool existing) => HeadlessTests.RunAsync(async profile =>
    {
        var source = Path.Combine(profile, "note.txt");
        var target = Path.Combine(profile, "copy.RTE");
        await File.WriteAllTextAsync(source, "original");
        var window = new MainWindow([source]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished") && window.GetVisualDescendants()
                .OfType<TextEditor>().Any(editor => editor.Text == "original"));
            var editor = window.GetVisualDescendants().OfType<TextEditor>().Single();
            editor.Text = "unsaved";
            var encrypted = EncryptedTabFile.Encrypt("previous", "test", "");
            await File.WriteAllBytesAsync(target, encrypted);
            var chosen = await window.StorageProvider.TryGetFileFromPathAsync(target);
            Assert.NotNull(chosen);
            if (!existing) File.Delete(target);
            var picker = System.Reflection.DispatchProxy.Create<IStorageProvider, SaveTargetPicker>();
            ((SaveTargetPicker)(object)picker).Target = chosen;
            typeof(TopLevel).GetField("_storageProvider", Private)!.SetValue(window, picker);
            var save = (Task)typeof(MainWindow).GetMethod("SaveAsync", Private)!.Invoke(window, [true])!;
            var error = await Assert.ThrowsAsync<IOException>(() => save);
            Assert.Contains(".rte", error.Message);
            Assert.Equal("unsaved", editor.Text);
            Assert.Equal("original", await File.ReadAllTextAsync(source));
            if (existing) Assert.Equal(encrypted, await File.ReadAllBytesAsync(target));
            else Assert.False(File.Exists(target));
        }
        finally { await CloseAsync(window); }
    });

    public class SaveTargetPicker : System.Reflection.DispatchProxy
    {
        public IStorageFile? Target { get; set; }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IStorageProvider.SaveFilePickerAsync) ? Task.FromResult(Target)
                : throw new NotSupportedException(method?.Name);
    }

    private static Task SaveSessionAsync(MainWindow window) =>
        (Task)typeof(MainWindow).GetMethod("SaveSessionAsync", Private)!.Invoke(window, null)!;

    private static async Task<SessionState?> ReadCheckpointAsync(StorageService storage, string pointer)
    {
        var checkpoints = Path.Combine(storage.RootDirectory, "session");
        var name = (await File.ReadAllTextAsync(Path.Combine(checkpoints, pointer + ".json"))).Trim();
        return JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(checkpoints, name, "manifest.json")),
            StorageJsonContext.Default.SessionState);
    }

    private static void AssertNoEncryptedHistory(SessionState? checkpoint, string source, string target, long id)
    {
        Assert.NotNull(checkpoint);
        Assert.DoesNotContain(checkpoint.Tabs.Concat(checkpoint.ClosedTabs ?? []),
            tab => tab.Id == id || tab.Path == source || tab.Path == target);
    }

    private static async Task<Window> WaitForDialogAsync(MainWindow window, string title)
    {
        await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog => dialog.Title == title));
        return GetDialog(window, title);
    }

    private static Window GetCreateDialog(MainWindow window) => GetDialog(window, "Зашифровать копию вкладки");

    private static void SubmitTestPassword(Window create)
    {
        Field(create, "Пароль").Text = "1";
        Field(create, "Повторите пароль").Text = "1";
        Click(create, "Зашифровать");
    }

    private static Window GetDialog(MainWindow window, string title)
    {
        var dialog = Assert.Single(window.OwnedWindows, item => item.Title == title);
        dialog.UpdateLayout();
        return dialog;
    }

    private static TextBox Field(Window dialog, string placeholder) =>
        Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(), box => Equals(box.PlaceholderText, placeholder));

    private static void Click(Window dialog, string label) =>
        Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, label))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task CloseAsync(MainWindow window)
    {
        foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
        window.Close();
        await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed"));
    }
}
