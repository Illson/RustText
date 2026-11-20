using System.Reflection;
using Avalonia.Controls;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class UpdatePreferenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task StartupAutomaticallyEnablesLegacyUpdateChecks() => HeadlessTests.RunAsync(async profile =>
    {
        if (!OperatingSystem.IsWindows()) return;

        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSettingsAsync(new UserSettings { AutoCheckUpdates = false });
        var window = new MainWindow();
        window.Show();

        try
        {
            await HeadlessTests.WaitForAsync(() =>
            {
                var settings = (UserSettings)Property(window, "Settings")!;
                return settings.AutoCheckUpdates && settings.AutoCheckUpdatesChoiceMade;
            });
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished")!);

            Assert.Empty(window.OwnedWindows);
            var persisted = await storage.LoadSettingsAsync();
            Assert.True(persisted.AutoCheckUpdates);
            Assert.True(persisted.AutoCheckUpdatesChoiceMade);
            Assert.True(MainWindow.ShouldRunAutomaticUpdateCheck(persisted.AutoCheckUpdates,
                isWindows: true, profileOverride: null));

            AssertAutomaticTimerCanBeScheduled(window, expected: true);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    [Fact]
    public Task StartupPreservesConfirmedOptOut() => HeadlessTests.RunAsync(async profile =>
    {
        if (!OperatingSystem.IsWindows()) return;

        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSettingsAsync(new UserSettings
        {
            AutoCheckUpdates = false,
            AutoCheckUpdatesChoiceMade = true
        });
        var window = new MainWindow();
        window.Show();

        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished")!);

            Assert.Empty(window.OwnedWindows);
            var settings = (UserSettings)Property(window, "Settings")!;
            Assert.False(settings.AutoCheckUpdates);
            Assert.True(settings.AutoCheckUpdatesChoiceMade);

            var persisted = await storage.LoadSettingsAsync();
            Assert.False(persisted.AutoCheckUpdates);
            Assert.True(persisted.AutoCheckUpdatesChoiceMade);
            AssertAutomaticTimerCanBeScheduled(window, expected: false);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    private static void AssertAutomaticTimerCanBeScheduled(MainWindow window, bool expected)
    {
        const string profileOverride = "RUSTTEXT_PROFILE_DIR";
        var previous = Environment.GetEnvironmentVariable(profileOverride);
        Environment.SetEnvironmentVariable(profileOverride, null);
        try
        {
            typeof(MainWindow).GetMethod("SynchronizeAutomaticUpdateTimer", Private)!
                .Invoke(window, [false, false]);
            var workspace = Field(window, "_workspace")!;
            Assert.Equal(expected, Field(workspace, "AutomaticUpdateTimer") is not null);
            Assert.False((bool)Field(workspace, "AutomaticUpdateStartupCheckAttempted")!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(profileOverride, previous);
        }
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, Private | BindingFlags.Public)!.GetValue(target);

    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(target);
}
