using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace RustText.Demo;

public partial class MainWindow
{
    private async Task ShowSettingsAsync()
    {
        _workspace.Catalog ??= await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        var initial = Settings with { WordWrap = _wordWrap, FontSize = _fontSize };
        var limit = SettingsInput(initial.UntitledTabLimit, "SettingsDraftTitleLimit");
        var perFile = SettingsInput(initial.VersionFileLimitBytes / (1024 * 1024), "SettingsVersionFileLimit");
        var total = SettingsInput(initial.VersionTotalLimitBytes / (1024 * 1024), "SettingsVersionTotalLimit");
        var draftLines = SettingsControl(new CheckBox
        {
            Content = "Показывать число строк у вкладок без имени",
            IsChecked = initial.ShowDraftLineCount
        }, "SettingsShowDraftLines");
        var autoUpdate = SettingsControl(new CheckBox
        {
            Content = "Проверять обновления при запуске и каждый час",
            IsChecked = initial.AutoCheckUpdates
        }, "SettingsAutoUpdates");
        var wordWrap = SettingsControl(new CheckBox
        {
            Content = "Переносить длинные строки",
            IsChecked = _wordWrap
        }, "SettingsWordWrap");
        var fontSize = SettingsInput(_fontSize, "SettingsFontSize");
        var startupMode = SettingsControl(new ComboBox
        {
            ItemsSource = new[] { "Последнее пространство", "Выбранное пространство", "Пустое окно" },
            SelectedIndex = initial.WorkspaceStartupMode switch
            {
                WorkspaceStartupMode.Specific => 1,
                WorkspaceStartupMode.Empty => 2,
                _ => 0
            }
        }, "SettingsWorkspaceStartupMode");
        var workspaces = _workspace.Catalog.Workspaces;
        var startupWorkspace = SettingsControl(new ComboBox
        {
            ItemsSource = workspaces,
            ItemTemplate = new FuncDataTemplate<WorkspaceEntry>((entry, _) => new TextBlock { Text = entry?.Name }),
            SelectedItem = workspaces.FirstOrDefault(item => item.Id == initial.StartupWorkspaceId)
                ?? workspaces.FirstOrDefault(item => item.Id == _workspaceId)
                ?? workspaces.FirstOrDefault(),
            IsEnabled = startupMode.SelectedIndex == 1
        }, "SettingsStartupWorkspace");
        var initialStartupWorkspace = startupWorkspace.SelectedItem as WorkspaceEntry;
        startupMode.SelectionChanged += (_, _) => startupWorkspace.IsEnabled = startupMode.SelectedIndex == 1;
        var workspaceHistory = SettingsControl(new TextBox
        {
            Text = initial.WorkspaceHistoryLimit < 0 ? "Безлимит" : initial.WorkspaceHistoryLimit.ToString(CultureInfo.CurrentCulture)
        }, "SettingsWorkspaceHistoryLimit");
        var largeFiles = SettingsControl(new ComboBox
        {
            ItemsSource = new[] { "Спрашивать", "Открывать как лог", "Открывать в редакторе" },
            SelectedIndex = initial.OpenLargeFilesAsLog switch { true => 1, false => 2, null => 0 }
        }, "SettingsLargeFiles");

        var archiveAfter = new DurationSetting("Архивировать закрытые черновики через", "DraftArchiveAfter",
            initial.ArchiveClosedDraftAfterMinutes,
            [("1 час", 60, 1, 1), ("5 часов", 300, 5, 1), ("1 день", 1440, 1, 2),
                ("7 дней", 10_080, 7, 2), ("Никогда", 0, 0, 0)],
            ["минут", "часов", "дней"], [1, 60, 1440], 525_600);
        var archiveRetention = new DurationSetting("Хранить архивные черновики", "DraftArchiveRetention",
            initial.ArchivedDraftRetentionDays,
            [("1 день", 1, 1, 0), ("7 дней", 7, 7, 0), ("30 дней", 30, 30, 0),
                ("1 год", 365, 1, 3), ("Безлимит", 0, 0, 0)],
            ["дней", "недель", "месяцев", "лет"], [1, 7, 30, 365], 3650);

        var editor = SettingsPage("Редактор", wordWrap, SettingsRow("Размер текста (8–48 px)", fontSize));
        var files = SettingsPage("Файлы и версии", SettingsRow("Файлы больше 100 МиБ", largeFiles),
            SettingsRow("Лимит снимка файла, МиБ", perFile), SettingsRow("Общий лимит снимков, МиБ", total));
        var workspace = SettingsPage("Рабочие пространства", SettingsRow("Открывать при запуске", startupMode),
            SettingsRow("Стартовое пространство", startupWorkspace),
            SettingsRow("Хранить удалённые пространства (0–200 или «Безлимит»)", workspaceHistory));
        var drafts = SettingsPage("Черновики и архив", draftLines,
            SettingsRow("Символов в заголовке пустой вкладки (1–200)", limit),
            archiveAfter.Fields, archiveRetention.Fields,
            new TextBlock
            {
                Text = "Открытые вкладки не архивируются. Для старых записей срок начинается после первого сохранения новой версии.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = FocusTheme.Muted
            });
        var updates = SettingsPage("Обновления", autoUpdate);

        var sections = new ListBox { SelectionMode = SelectionMode.AlwaysSelected, Background = Brushes.Transparent };
        AutomationProperties.SetName(sections, "Разделы настроек");
        var sectionPages = new (string Title, string Id, Control Page)[]
        {
            ("Редактор", "SettingsSectionEditor", editor),
            ("Файлы и версии", "SettingsSectionFilesVersions", files),
            ("Рабочие пространства", "SettingsSectionWorkspaces", workspace),
            ("Черновики и архив", "SettingsSectionDraftArchive", drafts),
            ("Обновления", "SettingsSectionUpdates", updates)
        };
        foreach (var (title, id, _) in sectionPages)
        {
            var item = SettingsControl(new ListBoxItem { Content = title }, id);
            item.Classes.Add("settings-section");
            sections.Items.Add(item);
        }

        var pageScroller = new ScrollViewer
        {
            Content = editor,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        sections.SelectionChanged += (_, _) =>
        {
            if (sections.SelectedIndex < 0) return;
            pageScroller.Content = sectionPages[sections.SelectedIndex].Page;
            pageScroller.Offset = default;
        };
        sections.SelectedIndex = 0;

        var sidebar = new Border
        {
            Background = FocusTheme.Chrome,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(8, 12),
            Child = sections
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("192,*") };
        body.Children.Add(sidebar);
        Grid.SetColumn(pageScroller, 1);
        body.Children.Add(pageScroller);

        var warning = new TextBlock
        {
            Foreground = FocusTheme.Danger,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        footer.Children.Add(warning);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(body);
        var footerBar = new Border
        {
            Background = FocusTheme.Chrome,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10),
            Child = footer
        };
        Grid.SetRow(footerBar, 1);
        layout.Children.Add(footerBar);

        var dialog = SettingsControl(Dialog("Настройки", layout, 690, scrollContent: false), "SettingsDialog");
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.MinWidth = 570;
        dialog.MinHeight = 340;
        dialog.Height = 454;
        var save = SettingsControl(FocusButton(Button("Сохранить", Save), primary: true), "SettingsSave");
        save.IsDefault = true;
        var cancel = SettingsControl(Button("Отмена", () => dialog.Close()), "SettingsCancel");
        cancel.Classes.Add("focus-outline");
        cancel.IsCancel = true;
        var actions = Row(cancel, save);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        dialog.Opened += (_, _) => sections.Focus();

        if (await dialog.ShowDialog<UserSettings?>(this) is not { } edited) return;
        var previous = Settings;
        await SaveWorkspaceSettingsAsync(current =>
        {
            previous = current;
            return current with
            {
                UntitledTabLimit = Changed(initial.UntitledTabLimit, edited.UntitledTabLimit, current.UntitledTabLimit),
                VersionFileLimitBytes = Changed(initial.VersionFileLimitBytes, edited.VersionFileLimitBytes, current.VersionFileLimitBytes),
                VersionTotalLimitBytes = Changed(initial.VersionTotalLimitBytes, edited.VersionTotalLimitBytes, current.VersionTotalLimitBytes),
                ShowDraftLineCount = Changed(initial.ShowDraftLineCount, edited.ShowDraftLineCount, current.ShowDraftLineCount),
                AutoCheckUpdates = Changed(initial.AutoCheckUpdates, edited.AutoCheckUpdates, current.AutoCheckUpdates),
                AutoCheckUpdatesChoiceMade = true,
                OpenLargeFilesAsLog = Changed(initial.OpenLargeFilesAsLog, edited.OpenLargeFilesAsLog, current.OpenLargeFilesAsLog),
                WordWrap = Changed(initial.WordWrap, edited.WordWrap, current.WordWrap),
                FontSize = Changed(initial.FontSize, edited.FontSize, current.FontSize),
                WorkspaceStartupMode = Changed(initial.WorkspaceStartupMode, edited.WorkspaceStartupMode, current.WorkspaceStartupMode),
                StartupWorkspaceId = Changed(initial.StartupWorkspaceId, edited.StartupWorkspaceId, current.StartupWorkspaceId),
                WorkspaceHistoryLimit = Changed(initial.WorkspaceHistoryLimit, edited.WorkspaceHistoryLimit, current.WorkspaceHistoryLimit),
                ArchiveClosedDraftAfterMinutes = Changed(initial.ArchiveClosedDraftAfterMinutes, edited.ArchiveClosedDraftAfterMinutes,
                    current.ArchiveClosedDraftAfterMinutes),
                ArchivedDraftRetentionDays = Changed(initial.ArchivedDraftRetentionDays, edited.ArchivedDraftRetentionDays,
                    current.ArchivedDraftRetentionDays)
            };
        });

        var turnedOn = !previous.AutoCheckUpdates && Settings.AutoCheckUpdates;
        SynchronizeAutomaticUpdateTimer(runImmediately: turnedOn, forceImmediateCheck: turnedOn);
        foreach (var window in _workspace.Windows)
        {
            window._wordWrap = Settings.WordWrap ?? window._wordWrap;
            window._fontSize = Settings.FontSize ?? window._fontSize;
            window.ApplyTextAppearance();
        }
        if (Settings.ArchiveClosedDraftAfterMinutes != previous.ArchiveClosedDraftAfterMinutes
            || Settings.ArchivedDraftRetentionDays != previous.ArchivedDraftRetentionDays)
            await ArchiveClosedDraftsAsync();

        void Save()
        {
            if (!double.TryParse(fontSize.Text, CultureInfo.CurrentCulture, out var parsedFontSize) || parsedFontSize is not (>= 8 and <= 48))
            {
                ShowInvalid(editor, fontSize, "Введите размер текста от 8 до 48 px.");
                return;
            }
            if (!int.TryParse(perFile.Text, out var fileLimit) || fileLimit is not (>= 0 and <= 64))
            {
                ShowInvalid(files, perFile, "Введите целый лимит снимка от 0 до 64 МиБ.");
                return;
            }
            if (!long.TryParse(total.Text, out var totalLimit) || totalLimit is not (>= 0 and <= 1024))
            {
                ShowInvalid(files, total, "Введите целый общий лимит от 0 до 1024 МиБ.");
                return;
            }

            var historyLimit = -1;
            if (!string.Equals(workspaceHistory.Text?.Trim(), "Безлимит", StringComparison.OrdinalIgnoreCase)
                && (!int.TryParse(workspaceHistory.Text, out historyLimit) || historyLimit is not (>= 0 and <= 200)))
            {
                ShowInvalid(workspace, workspaceHistory, "Введите число от 0 до 200 или «Безлимит».");
                return;
            }
            if (!int.TryParse(limit.Text, out var length) || length is not (>= 1 and <= 200))
            {
                ShowInvalid(drafts, limit, "Введите число символов от 1 до 200.");
                return;
            }
            if (!archiveAfter.TryRead(out var archiveMinutes))
            {
                ShowInvalid(drafts, archiveAfter.Count, "Укажите срок до архивации от 0 до 365 дней.");
                return;
            }
            if (!archiveRetention.TryRead(out var retentionDays))
            {
                ShowInvalid(drafts, archiveRetention.Count, "Укажите срок хранения от 0 до 3650 дней.");
                return;
            }

            var startup = startupMode.SelectedIndex switch
            {
                1 => WorkspaceStartupMode.Specific,
                2 => WorkspaceStartupMode.Empty,
                _ => WorkspaceStartupMode.LastUsed
            };
            var selectedWorkspace = startupWorkspace.SelectedItem as WorkspaceEntry;
            var startupWorkspaceId = startup == WorkspaceStartupMode.Specific || selectedWorkspace != initialStartupWorkspace
                ? selectedWorkspace?.Id : initial.StartupWorkspaceId;
            dialog.Close(initial with
            {
                UntitledTabLimit = length,
                VersionFileLimitBytes = fileLimit * 1024 * 1024,
                VersionTotalLimitBytes = totalLimit * 1024 * 1024,
                ShowDraftLineCount = draftLines.IsChecked == true,
                AutoCheckUpdates = autoUpdate.IsChecked == true,
                OpenLargeFilesAsLog = largeFiles.SelectedIndex switch { 1 => true, 2 => false, _ => null },
                WordWrap = wordWrap.IsChecked == true,
                FontSize = parsedFontSize,
                WorkspaceStartupMode = startup,
                StartupWorkspaceId = startupWorkspaceId,
                WorkspaceHistoryLimit = historyLimit,
                ArchiveClosedDraftAfterMinutes = archiveMinutes,
                ArchivedDraftRetentionDays = retentionDays
            });
        }

        void ShowInvalid(Control page, Control field, string message)
        {
            sections.SelectedIndex = Array.FindIndex(sectionPages, section => section.Page == page);
            warning.Text = message;
            warning.IsVisible = true;
            field.Focus();
            field.BringIntoView();
        }

        static T Changed<T>(T before, T after, T current) =>
            EqualityComparer<T>.Default.Equals(before, after) ? current : after;
    }

    private static T SettingsControl<T>(T control, string id) where T : Control
    {
        AutomationProperties.SetAutomationId(control, id);
        return control;
    }

    private static TextBox SettingsInput<T>(T value, string id) where T : IFormattable =>
        SettingsControl(new TextBox { Text = value.ToString(null, CultureInfo.CurrentCulture) }, id);

    private static StackPanel SettingsPage(string title, params Control[] fields)
    {
        var heading = new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        return DialogPanel([heading, .. fields]);
    }

    private static Grid SettingsRow(string label, Control field)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,178"), ColumnSpacing = 12 };
        row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(field, label);
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        return row;
    }

    private sealed class DurationSetting
    {
        private readonly ComboBox _preset;
        private readonly ComboBox _unit;
        private readonly (string Label, int Value, int Count, int Unit)[] _presets;
        private readonly int[] _multipliers;
        private readonly int _maximum;

        public DurationSetting(string label, string id, int value,
            (string Label, int Value, int Count, int Unit)[] presets, string[] units, int[] multipliers, int maximum)
        {
            _presets = presets;
            _multipliers = multipliers;
            _maximum = maximum;
            var selected = Array.FindIndex(presets, preset => preset.Value == value);
            _preset = SettingsControl(new ComboBox
            {
                ItemsSource = presets.Select(preset => preset.Label).Append("Вручную").ToArray(),
                SelectedIndex = selected >= 0 ? selected : presets.Length
            }, id + "Preset");
            Count = SettingsInput(selected >= 0 ? presets[selected].Count : value, id + "Count");
            Count.Width = 71;
            _unit = SettingsControl(new ComboBox
            {
                ItemsSource = units,
                SelectedIndex = selected >= 0 ? presets[selected].Unit : 0,
                Width = 100
            }, id + "Unit");
            AutomationProperties.SetName(Count, label + " — число");
            AutomationProperties.SetName(_unit, label + " — единица времени");

            var manual = Row(Count, _unit);
            manual.HorizontalAlignment = HorizontalAlignment.Right;
            manual.IsVisible = selected < 0;
            _preset.SelectionChanged += (_, _) => manual.IsVisible = _preset.SelectedIndex == presets.Length;
            Fields = new StackPanel { Spacing = 8, Children = { SettingsRow(label, _preset), manual } };
        }

        public TextBox Count { get; }
        public Control Fields { get; }

        public bool TryRead(out int value)
        {
            var selected = _preset.SelectedIndex;
            if (selected >= 0 && selected < _presets.Length)
            {
                value = _presets[selected].Value;
                return true;
            }

            value = 0;
            if (!int.TryParse(Count.Text, NumberStyles.None, CultureInfo.CurrentCulture, out var count)
                || _unit.SelectedIndex < 0 || count > _maximum / _multipliers[_unit.SelectedIndex])
                return false;

            value = count * _multipliers[_unit.SelectedIndex];
            return true;
        }
    }
}
