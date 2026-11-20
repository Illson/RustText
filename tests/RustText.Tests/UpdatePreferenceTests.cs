using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class UpdatePreferenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StartupPromptsForLegacyPreferenceAndAppliesChoice(bool enable) => HeadlessTests.RunAsync(async profile =>
    {
        if (!OperatingSystem.IsWindows()) return;

        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSettingsAsync(new UserSettings { AutoCheckUpdates = false });
        var window = new MainWindow();
        window.Show();

        try
        {
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any());
            var dialog = Assert.Single(window.OwnedWindows);
            var choice = dialog.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, enable ? "Включить" : "Оставить выключенной"));
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            await HeadlessTests.WaitForAsync(() =>
            {
                var settings = (UserSettings)Property(window, "Settings")!;
                return settings.AutoCheckUpdates == enable && settings.AutoCheckUpdatesChoiceMade;
            });

            var persisted = await storage.LoadSettingsAsync();
            Assert.Equal(enable, persisted.AutoCheckUpdates);
            Assert.True(persisted.AutoCheckUpdatesChoiceMade);
            Assert.Equal(enable, MainWindow.ShouldRunAutomaticUpdateCheck(persisted.AutoCheckUpdates,
                isWindows: true, profileOverride: null));

            const string profileOverride = "RUSTTEXT_PROFILE_DIR";
            var previous = Environment.GetEnvironmentVariable(profileOverride);
            Environment.SetEnvironmentVariable(profileOverride, null);
            try
            {
                typeof(MainWindow).GetMethod("SynchronizeAutomaticUpdateTimer", Private)!
                    .Invoke(window, [false, false]);
                var workspace = Field(window, "_workspace")!;
                Assert.Equal(enable, Field(workspace, "AutomaticUpdateTimer") is not null);
                Assert.False((bool)Field(workspace, "AutomaticUpdateStartupCheckAttempted")!);
            }
            finally
            {
                Environment.SetEnvironmentVariable(profileOverride, previous);
            }
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, Private | BindingFlags.Public)!.GetValue(target);

    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(target);
}
