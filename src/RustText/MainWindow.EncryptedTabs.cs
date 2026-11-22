using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit;

namespace RustText.Demo;

public partial class MainWindow
{
    private static readonly FilePickerFileType EncryptedTabFiles = new("Зашифрованный файл RustText")
    {
        Patterns = ["*" + EncryptedTabFile.Extension]
    };

    private Task EncryptActiveTabAsync() => EncryptActiveTabAsync(PickEncryptedTargetAsync);

    internal async Task EncryptActiveTabAsync(Func<Task<string?>> chooseTargetAsync)
    {
        if (ActiveTab?.Editor?.Document is not { } document)
        {
            SetStatus("Для шифрования откройте обычную текстовую вкладку.");
            return;
        }
        if (document.TextLength > EncryptedTabFile.MaxTextBytes)
        {
            SetStatus("Максимальный размер текста для шифрования — 32 МиБ в UTF-8.");
            return;
        }

        var snapshot = document.CreateSnapshot();
        var token = _lifetime.Token;
        if (await chooseTargetAsync() is not { } path) return;
        token.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            SetStatus("Файл уже существует. Выберите новое имя; шифрование не перезаписывает файлы.");
            return;
        }

        var input = await AskEncryptionPasswordAsync(creating: true, string.Empty, null);
        if (input is not { } secret) return;
        var container = await Task.Run(() =>
        {
            using var reader = snapshot.CreateReader();
            return EncryptedTabFile.Encrypt(reader.ReadToEnd(), secret.Password, secret.Hint);
        }, token);
        await EncryptedTabFile.WriteNewAsync(path, container, token);
        if (!_closed) SetStatus("Зашифрованная копия сохранена. Исходная вкладка не изменена.");
    }

    private async Task<string?> PickEncryptedTargetAsync()
    {
        using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить зашифрованную копию",
            SuggestedFileName = "Вкладка" + EncryptedTabFile.Extension,
            DefaultExtension = EncryptedTabFile.Extension[1..],
            FileTypeChoices = [EncryptedTabFiles]
        });
        return file?.TryGetLocalPath();
    }

    private async Task DecryptTabFileAsync()
    {
        var items = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Расшифровать файл RustText",
            FileTypeFilter = [EncryptedTabFiles, FilePickerFileTypes.All]
        });
        if (items.Count == 0) return;
        using var file = items[0];
        if (file.TryGetLocalPath() is { } path) await DecryptTabPathAsync(path);
    }

    internal async Task DecryptTabPathAsync(string path)
    {
        var token = _lifetime.Token;
        var container = await EncryptedTabFile.ReadAsync(path, token);
        var hint = EncryptedTabFile.ReadHint(container);
        string? error = null;
        while (!token.IsCancellationRequested && await AskEncryptionPasswordAsync(creating: false, hint, error) is { } secret)
        {
            string text;
            try
            {
                text = await Task.Run(() => EncryptedTabFile.Decrypt(container, secret.Password), token);
            }
            catch (InvalidDataException failure)
            {
                error = failure.Message;
                continue;
            }
            token.ThrowIfCancellationRequested();
            await ShowDecryptedTextAsync(this, text);
            return;
        }
    }

    private async Task<(string Password, string Hint)?> AskEncryptionPasswordAsync(bool creating, string hint, string? error)
    {
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "Пароль", IsUndoEnabled = false };
        var confirmation = new TextBox { PasswordChar = '●', PlaceholderText = "Повторите пароль", IsUndoEnabled = false };
        var hintInput = new TextBox { PlaceholderText = "Подсказка (необязательно)", MaxLength = EncryptedTabFile.MaxHintBytes };
        var status = new TextBlock { Text = error, Foreground = FocusTheme.Danger, TextWrapping = TextWrapping.Wrap };
        var body = DialogPanel(new TextBlock
        {
            Text = creating
                ? "Шифруется только новая копия .rte. Исходная вкладка остаётся открытой: RustText может и дальше "
                    + "сохранять её текст в черновиках без шифрования, даже если у вкладки ещё нет файла. "
                    + "Исходный файл и прежние черновики не меняются.\n\n"
                    + $"Используйте длинный уникальный пароль (не менее {EncryptedTabFile.PasswordMinLength} символов). "
                    + "Подсказку видно без пароля: не помещайте в неё секреты."
                : string.IsNullOrEmpty(hint) ? "Подсказка не сохранена." : $"Подсказка из файла (ещё не проверена):\n{hint}",
            TextWrapping = TextWrapping.Wrap
        }, password);
        if (creating)
        {
            body.Children.Add(confirmation);
            body.Children.Add(hintInput);
        }
        body.Children.Add(status);
        var dialog = Dialog(creating ? "Зашифровать копию вкладки" : "Расшифровать файл", body, 490);
        var submit = FocusButton(Button(creating ? "Зашифровать" : "Расшифровать", () =>
        {
            var value = password.Text ?? string.Empty;
            if (value.Length > EncryptedTabFile.PasswordMaxLength
                || creating && (value.Length < EncryptedTabFile.PasswordMinLength || string.IsNullOrWhiteSpace(value))
                || !creating && value.Length == 0)
            {
                status.Text = creating ? $"Введите пароль от {EncryptedTabFile.PasswordMinLength} до {EncryptedTabFile.PasswordMaxLength} символов."
                    : "Введите пароль для расшифровки.";
                return;
            }
            if (creating && !string.Equals(value, confirmation.Text, StringComparison.Ordinal))
            {
                status.Text = "Пароли не совпадают.";
                return;
            }
            if (creating && System.Text.Encoding.UTF8.GetByteCount(hintInput.Text ?? "") > EncryptedTabFile.MaxHintBytes)
            {
                status.Text = "Подсказка не должна превышать 1024 байта в UTF-8.";
                return;
            }
            dialog.Close(true);
        }), primary: true);
        submit.IsDefault = true;
        var cancel = FocusButton(Button("Отмена", () => dialog.Close(false)));
        cancel.IsCancel = true;
        body.Children.Add(Row(cancel, submit));
        dialog.Opened += (_, _) => password.Focus();
        try
        {
            return await dialog.ShowDialog<bool>(this)
                ? (password.Text ?? string.Empty, creating ? hintInput.Text ?? string.Empty : string.Empty)
                : null;
        }
        finally
        {
            password.Text = null;
            confirmation.Text = null;
        }
    }

    internal static async Task ShowDecryptedTextAsync(Window owner, string text)
    {
        var editor = new TextEditor
        {
            Name = "DecryptedTabText",
            Text = text,
            IsReadOnly = true,
            Background = FocusTheme.Editor,
            Foreground = FocusTheme.Text,
            FontSize = FocusTheme.EditorFontSize,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        var body = new DockPanel { Margin = new Thickness(10) };
        var dialog = Dialog("Расшифрованный текст", body, 720, scrollContent: false);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.Height = 480;
        var notice = new TextBlock
        {
            Text = "Только чтение. RustText не сохраняет этот текст в сессии или черновиках.",
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        var close = FocusButton(Button("Закрыть", () => dialog.Close()));
        close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(notice, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        body.Children.Add(notice);
        body.Children.Add(close);
        body.Children.Add(editor);
        try { await dialog.ShowDialog(owner); }
        finally { editor.Document = new AvaloniaEdit.Document.TextDocument(); }
    }
}
