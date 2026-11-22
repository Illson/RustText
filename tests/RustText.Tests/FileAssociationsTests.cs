using Microsoft.Win32;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class FileAssociationsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupKeepsAWorkingRegistrationAndRepairsItOnlyWhenItDisappears(bool legacy)
    {
        using var temp = new TempDirectory();
        var stable = MakeCopy("Основная копия");
        var preview = MakeCopy("Preview");
        var key = $@"Software\RustText.FileAssociations.Tests.{Guid.NewGuid():N}";
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(key);
            using var choice = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.txt\UserChoice");
            choice.SetValue("ProgId", "RustText.Text");
            using var command = root.CreateSubKey(@"Software\Classes\RustText.Text\shell\open\command");
            if (legacy)
                command.SetValue(null, $"\"{stable}\" \"%1\"");
            else
                Assert.True(FileAssociations.WriteRegistration(root, stable, new HashSet<string> { ".txt" }));

            Assert.False(FileAssociations.WriteRegistration(root, preview, onlyIfChanged: true));
            Assert.Equal($"\"{stable}\" \"%1\"", command.GetValue(null));
            Assert.True(FileAssociations.WriteRegistration(root, preview, selected: new HashSet<string> { ".jsonl" }));
            Assert.Equal($"\"{preview}\" \"%1\"", command.GetValue(null));
            Assert.False(FileAssociations.WriteRegistration(root, stable, onlyIfChanged: true));
            Assert.Equal($"\"{preview}\" \"%1\"", command.GetValue(null));

            File.Delete(preview);
            Assert.True(FileAssociations.WriteRegistration(root, stable, onlyIfChanged: true));
            Assert.Equal($"\"{stable}\" \"%1\"", command.GetValue(null));
            Assert.Equal(".jsonl", Assert.Single(FileAssociations.ReadSelectedExtensions(root)));
            Assert.Equal("RustText.Text", choice.GetValue("ProgId"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }

        string MakeCopy(string name)
        {
            var directory = Directory.CreateDirectory(Path.Combine(temp.Path, name));
            var path = Path.Combine(directory.FullName, "rusttext.exe");
            File.WriteAllText(path, "Невыполняемый тестовый файл");
            return path;
        }
    }

    [Fact]
    public void StartsRegistrationOnlyForTheApplicationOutsideAnIsolatedProfile()
    {
        Assert.True(FileAssociations.CanRegisterOnStartup(@"C:\Apps\RustText.EXE", null));
        Assert.False(FileAssociations.CanRegisterOnStartup(@"C:\Apps\RustText.exe", @"C:\test\profile"));
        Assert.False(FileAssociations.CanRegisterOnStartup(@"C:\Apps\dotnet.exe", null));
        Assert.False(FileAssociations.CanRegisterOnStartup(null, null));
    }

    [Fact]
    public void AdvertisesSupportedTypesWithoutChangingDefaultsAndKeepsSelectionsWhenTheExeMoves()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = $@"Software\RustText.FileAssociations.Tests.{Guid.NewGuid():N}";
        var executable = @"C:\Мои приложения\RustText.exe";
        var movedExecutable = @"D:\Программы\RustText.exe";
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            Assert.Equal(new[] { ".json", ".log", ".txt", ".xml" }, FileAssociations.ReadSelectedExtensions(root).Order());
            using var choice = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.txt\UserChoice");
            choice.SetValue("ProgId", "AnotherApp");
            using var ext = root.CreateSubKey(@"Software\Classes\.txt");
            ext.SetValue(null, "txtfile");
            using var openWith = ext.CreateSubKey("OpenWithProgids");
            openWith.SetValue("AnotherApp", "");

            Assert.True(FileAssociations.WriteRegistration(root, executable, onlyIfChanged: true));
            Assert.False(FileAssociations.WriteRegistration(root, executable, onlyIfChanged: true));
            Assert.True(FileAssociations.WriteRegistration(root, executable, new HashSet<string> { ".jsonl", ".md" }));
            Assert.True(FileAssociations.WriteRegistration(root, movedExecutable, onlyIfChanged: true));

            using var application = root.OpenSubKey(@"Software\Classes\Applications\RustText.exe")!;
            Assert.Equal("RustText", application.GetValue("FriendlyAppName"));
            using var command = application.OpenSubKey(@"shell\open\command")!;
            Assert.Equal($"\"{movedExecutable}\" \"%1\"", command.GetValue(null));
            using var supported = application.OpenSubKey("SupportedTypes")!;
            Assert.Equal(FileAssociations.SupportedExtensions.Order(), supported.GetValueNames().Order());
            Assert.Contains("RustText.Text", openWith.GetValueNames());
            Assert.Contains("AnotherApp", openWith.GetValueNames());
            Assert.Equal("txtfile", ext.GetValue(null));
            Assert.Equal("AnotherApp", choice.GetValue("ProgId"));

            Assert.Equal(new[] { ".jsonl", ".md" }, FileAssociations.ReadSelectedExtensions(root).Order());
            Assert.True(FileAssociations.WriteRegistration(root, movedExecutable, new HashSet<string>()));
            Assert.Empty(FileAssociations.ReadSelectedExtensions(root));
            Assert.Contains("RustText.Text", openWith.GetValueNames());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}
