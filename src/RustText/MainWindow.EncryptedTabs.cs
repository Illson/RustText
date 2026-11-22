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
    private readonly SemaphoreSlim _encryptedOpenGate = new(1, 1);

    private Task EncryptActiveTabAsync() => EncryptActiveTabAsync(PickEncryptedTargetAsync);

    internal async Task EncryptActiveTabAsync(Func<Task<string?>> chooseTargetAsync)
    {
        if (ActiveTab is not { Editor: { } editor } tab)
        {
            SetStatus("Для шифрования откройте обычную текстовую вкладку.");
            return;
        }
        if (tab.Closing) return;
        var document = editor.Document;
        var views = _workspace.Windows.Where(owner => !owner._closed)
            .SelectMany(owner => owner.LoadedWorkspaceTabs()
                .Where(view => ReferenceEquals(view.Editor?.Document, document)).Select(view => (Owner: owner, Tab: view)))
            .ToArray();
        if (views.Any(view => view.Owner._closing || view.Owner._closed || view.Owner._workspaceSwitchPending
            || view.Tab.Closing || view.Tab.Saving || view.Tab.Loading || view.Tab.CheckingSession))
        {
            SetStatus("Дождитесь завершения операции с вкладкой перед шифрованием.");
            return;
        }
        if (document.TextLength > EncryptedTabFile.MaxTextBytes)
        {
            SetStatus("Максимальный размер текста для шифрования — 32 МиБ в UTF-8.");
            return;
        }

        var viewEditors = views.Select(view => (view.Tab, Editor: view.Tab.Editor!, view.Tab.Editor!.IsEnabled)).ToArray();
        var revision = tab.Revision;
        var snapshot = document.CreateSnapshot();
        var sourcePath = tab.Path;
        var token = _lifetime.Token;
        foreach (var (item, viewEditor, _) in viewEditors)
        {
            item.Closing = true;
            viewEditor.IsEnabled = false;
        }
        try
        {
            if (await chooseTargetAsync() is not { } path) return;
            token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                SetStatus("Файл уже существует. Выберите новое имя; шифрование не перезаписывает файлы.");
                return;
            }
            if (!EncryptedTabFile.HasExtension(path))
            {
                SetStatus("Для зашифрованной копии укажите имя с расширением .rte.");
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

            await _workspace.WorkspaceSwitchGate.WaitAsync(token);
            try
            {
                await CloseEncryptedViewsAsync(views, tab, revision, document, sourcePath, token);
            }
            finally { _workspace.WorkspaceSwitchGate.Release(); }
        }
        finally
        {
            foreach (var (item, viewEditor, wasEnabled) in viewEditors)
            {
                item.Closing = false;
                if (ReferenceEquals(item.Editor, viewEditor))
                {
                    viewEditor.IsEnabled = wasEnabled;
                    if (item.AutoRefresh) item.FileWatch?.Schedule(force: true);
                }
            }
        }
    }

    private async Task CloseEncryptedViewsAsync((MainWindow Owner, WorkTab Tab)[] views, WorkTab source, long revision,
        AvaloniaEdit.Document.TextDocument document, string? sourcePath, CancellationToken token)
    {
        var affected = new Dictionary<StorageService, HashSet<long>>();
        var emptyWindows = new HashSet<MainWindow>();
        string? cleanupError = null;

        await _workspace.SaveGate.WaitAsync(token);
        try
        {
            if (source.Revision != revision || views.Any(view => !ReferenceEquals(view.Tab.Editor?.Document, document)))
            {
                SetStatus("Зашифрованная копия сохранена. Вкладка изменена во время операции и оставлена открытой.");
                return;
            }

            foreach (var (owner, tab) in views)
            {
                var hidden = owner._workspaceViews.Values.FirstOrDefault(view =>
                    ReferenceEquals(view.Tabs.GetValueOrDefault(tab.Id), tab));
                var storage = hidden?.Storage ?? owner.SessionStorage;
                AddAffected(storage, tab.Id);
                owner.RemoveTab(tab, hidden?.Tabs);
                if (hidden is null && owner._tabs.Count == 0) emptyWindows.Add(owner);
            }

            foreach (var (storage, ids) in affected.ToArray())
            {
                if (!_workspace.DetachedDrafts.TryGetValue(storage, out var recovery)) continue;
                foreach (var id in ids.ToArray())
                {
                    if (recovery.DestinationIds.Remove(id, out var destination) && destination is { } previousId
                        && !SessionViews(recovery.PrimaryStorage, null).Any(view => view.View.Tabs.ContainsKey(previousId)))
                        AddAffected(recovery.PrimaryStorage, previousId);
                }
            }

            foreach (var (storage, ids) in affected)
            {
                var cacheKey = StorageCacheKey(storage);
                var history = _workspace.GetClosedTabs(storage);
                var removedDrafts = history.Where(item => RemoveClosed(item) && item.DraftSha256 is not null).ToList();
                history.RemoveAll(RemoveClosed);
                foreach (var id in ids) _workspace.GetLiveDrafts(cacheKey).Remove(id);
                _workspace.SetLastCheckpoint(cacheKey, null);
                await TryCleanupAsync(async () =>
                {
                    removedDrafts.AddRange(await storage.RemoveTabHistoryAsync(ids, RemoveClosed, token));
                    // A newly imported session may still have no .NET checkpoint. Commit the remaining
                    // live tabs so the previous application's profile is not restored on the next launch.
                    if (!storage.HasSessionCheckpoint && !_workspace.UnavailableSessions.Contains(cacheKey)
                        && SessionViews(storage, null).ToList() is { Count: > 0 } owners)
                        await SaveSessionViewsAsync(storage, owners, forget: null, force: true);
                });
                await TryCleanupAsync(async () =>
                {
                    if (removedDrafts.Count == 0) return;
                    foreach (var entry in await storage.DraftArchive.GetEntriesAsync(token))
                    {
                        // Tab numbers can be reused after restart. Do not remove an older, unrelated archive by ID alone.
                        if (removedDrafts.Any(item => item.Id == entry.Tab.Id
                            && string.Equals(item.DraftSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                            && (item.ClosedAtMs is null || item.ClosedAtMs == entry.Tab.ClosedAtMs)))
                            await storage.DraftArchive.RemoveAsync(entry, token, requireDeletion: true);
                    }
                });

                // A file bookmark has no text of its own. Independent drafts of the same file are not removed by path.
                bool RemoveClosed(SessionTab entry) => ids.Contains(entry.Id)
                    || (sourcePath is not null && entry.DraftRef is null && SamePath(entry.Path, sourcePath));
            }

            if (sourcePath is not null)
                await TryCleanupAsync(async () => { _recent = await _storage.RemoveRecentAsync(sourcePath, token); });
        }
        finally { _workspace.SaveGate.Release(); }

        foreach (var owner in emptyWindows)
            if (!owner._closed && owner._tabs.Count == 0) await TryCleanupAsync(owner.ShowHomeAsync);
        RefreshHistoryMenus();
        SetStatus(cleanupError is null
            ? "Зашифрованная копия сохранена. Исходная вкладка закрыта без истории."
            : $"Зашифрованная копия сохранена, но не удалось полностью очистить историю вкладки. {cleanupError}");

        void AddAffected(StorageService storage, long id)
        {
            if (!affected.TryGetValue(storage, out var ids)) affected.Add(storage, ids = []);
            ids.Add(id);
        }

        async Task TryCleanupAsync(Func<Task> cleanup)
        {
            try { await cleanup(); }
            catch (Exception error) when (IsUserError(error)) { cleanupError ??= error.Message; }
        }
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

    private async Task DecryptTabPathAsync(string path)
    {
        var token = _lifetime.Token;
        await _encryptedOpenGate.WaitAsync(token);
        try
        {
            if (!_closing && !_closed) await OpenEncryptedContainerAsync(path, token);
        }
        finally { _encryptedOpenGate.Release(); }
    }

    private async Task OpenEncryptedContainerAsync(string path, CancellationToken token)
    {
        var container = await EncryptedTabFile.ReadAsync(path, token);
        var hint = EncryptedTabFile.ReadHint(container);
        if (_recent.Any(entry => SamePath(entry.Path, path)))
        {
            try { await ForgetRecentFileAsync(path); }
            catch (Exception failure) when (IsUserError(failure))
            {
                SetStatus($"Не удалось убрать зашифрованный файл из недавних. {failure.Message}");
            }
        }
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
                ? "После записи новой копии .rte исходная вкладка закроется, а её запись и черновики будут удалены из истории. "
                    + "Исходный файл на диске не изменится. Отдельно сохранённые версии и копии не шифруются и не удаляются.\n\n"
                    + "Рекомендуется длинный уникальный пароль. "
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
            if (value.Length == 0)
            {
                status.Text = creating ? "Введите пароль для шифрования."
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
