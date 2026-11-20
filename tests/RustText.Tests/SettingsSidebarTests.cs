using System.Globalization;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class SettingsSidebarTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task SettingsSidebarKeepsDraftEditsAcrossSectionsAndSavesOnce() => HeadlessTests.RunAsync(async profile =>
    {
        var namedWorkspace = await new WorkspaceCatalogStore(profile).CreateAsync("Project logs");
        var window = await OpenWorkspaceAsync();
        try
        {
            AssertToolsMenuEntries(window);

            var showSettings = CallAsync(window, "ShowSettingsAsync");
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.OfType<Window>()
                .Any(dialog => AutomationProperties.GetAutomationId(dialog) == "SettingsDialog"));
            var dialog = window.OwnedWindows.OfType<Window>()
                .Single(item => AutomationProperties.GetAutomationId(item) == "SettingsDialog");

            Assert.True(FindControl<ListBoxItem>(dialog, "SettingsSectionEditor").IsSelected);

            SelectSection(dialog, "SettingsSectionDraftArchive");
            var archivePreset = FindControl<ComboBox>(dialog, "DraftArchiveAfterPreset");
            Assert.Equal(1, archivePreset.SelectedIndex);
            archivePreset.SelectedIndex = 5;
            var retentionPreset = FindControl<ComboBox>(dialog, "DraftArchiveRetentionPreset");
            Assert.Equal(2, retentionPreset.SelectedIndex);
            retentionPreset.SelectedIndex = 5;
            var archiveCount = FindControl<TextBox>(dialog, "DraftArchiveAfterCount");
            var archiveUnit = FindControl<ComboBox>(dialog, "DraftArchiveAfterUnit");
            var retentionCount = FindControl<TextBox>(dialog, "DraftArchiveRetentionCount");
            var retentionUnit = FindControl<ComboBox>(dialog, "DraftArchiveRetentionUnit");
            Assert.True(archiveCount.IsVisible);
            Assert.True(archiveUnit.IsVisible);
            Assert.True(retentionCount.IsVisible);
            Assert.True(retentionUnit.IsVisible);
            archiveCount.Text = "12";
            archiveUnit.SelectedIndex = 0;
            retentionCount.Text = "3";
            retentionUnit.SelectedIndex = 1;

            SelectSection(dialog, "SettingsSectionWorkspaces");
            var startupMode = FindControl<ComboBox>(dialog, "SettingsWorkspaceStartupMode");
            startupMode.SelectedIndex = 1;
            var startupWorkspace = FindControl<ComboBox>(dialog, "SettingsStartupWorkspace");
            startupWorkspace.SelectedItem = startupWorkspace.Items.OfType<WorkspaceEntry>()
                .Single(entry => entry.Name == namedWorkspace.Name);

            SelectSection(dialog, "SettingsSectionDraftArchive");
            Assert.Equal("12", FindControl<TextBox>(dialog, "DraftArchiveAfterCount").Text);
            Assert.Equal(0, FindControl<ComboBox>(dialog, "DraftArchiveAfterUnit").SelectedIndex);
            Assert.Equal("3", FindControl<TextBox>(dialog, "DraftArchiveRetentionCount").Text);
            Assert.Equal(1, FindControl<ComboBox>(dialog, "DraftArchiveRetentionUnit").SelectedIndex);

            SelectSection(dialog, "SettingsSectionEditor");
            var fontSize = FindControl<TextBox>(dialog, "SettingsFontSize");
            fontSize.Text = "19";

            SelectSection(dialog, "SettingsSectionUpdates");
            var autoUpdates = FindControl<CheckBox>(dialog, "SettingsAutoUpdates");
            var expectedAutoUpdates = autoUpdates.IsChecked != true;
            autoUpdates.IsChecked = expectedAutoUpdates;

            SelectSection(dialog, "SettingsSectionFilesVersions");
            var largeFiles = FindControl<ComboBox>(dialog, "SettingsLargeFiles");
            largeFiles.SelectedIndex = 1;

            FindControl<Button>(dialog, "SettingsSave")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await showSettings;

            var saved = await StorageService.OpenForTesting(profile).LoadSettingsAsync();
            Assert.Equal(19, saved.FontSize);
            Assert.Equal(expectedAutoUpdates, saved.AutoCheckUpdates);
            Assert.True(saved.OpenLargeFilesAsLog);
            Assert.Equal(WorkspaceStartupMode.Specific, saved.WorkspaceStartupMode);
            Assert.Equal(namedWorkspace.Id, saved.StartupWorkspaceId);
            Assert.Equal(12, saved.ArchiveClosedDraftAfterMinutes);
            Assert.Equal(21, saved.ArchivedDraftRetentionDays);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task SettingsSidebarCancelDiscardsPendingChanges() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            var storage = StorageService.OpenForTesting(profile);
            var original = await storage.LoadSettingsAsync();
            var showSettings = CallAsync(window, "ShowSettingsAsync");
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.OfType<Window>()
                .Any(dialog => AutomationProperties.GetAutomationId(dialog) == "SettingsDialog"));
            var dialog = window.OwnedWindows.OfType<Window>()
                .Single(item => AutomationProperties.GetAutomationId(item) == "SettingsDialog");

            SelectSection(dialog, "SettingsSectionEditor");
            FindControl<TextBox>(dialog, "SettingsFontSize").Text = "21";
            FindControl<Button>(dialog, "SettingsCancel")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await showSettings;

            var saved = await storage.LoadSettingsAsync();
            Assert.Equal(original.FontSize, saved.FontSize);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    private static void AssertToolsMenuEntries(MainWindow window)
    {
        var menu = window.FindControl<Menu>("TopMenu")!;
        var tools = menu.Items.OfType<MenuItem>().Single(item => Header(item) == "_Инструменты");
        var rootEntries = tools.Items.OfType<MenuItem>().Select(Header).ToArray();
        var history = tools.Items.OfType<MenuItem>().Single(item => Header(item) == "История и черновики");
        var historyEntries = history.Items.OfType<MenuItem>().Select(Header).ToArray();

        Assert.Equal(1, rootEntries.Count(label => label == "Настройки…"));
        Assert.Equal(1, historyEntries.Count(label => label == "Архив черновиков…"));
        Assert.DoesNotContain("Архив черновиков…", rootEntries);
        Assert.DoesNotContain("Черновики и архив…", historyEntries);
    }

    private static void SelectSection(Window dialog, string automationId)
    {
        var section = FindControl<ListBoxItem>(dialog, automationId);
        section.IsSelected = true;
        Assert.True(section.IsSelected);
    }

    private static T FindControl<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetAutomationId(control) == automationId);

    private static string Header(MenuItem item) => item.Header switch
    {
        TextBlock text => text.Text ?? string.Empty,
        _ => Convert.ToString(item.Header, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static async Task<MainWindow> OpenWorkspaceAsync()
    {
        var window = new MainWindow([]);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible);
    }

    private static Task CallAsync(MainWindow window, string name) =>
        (Task)typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, null)!;

    private static object Field(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
}
