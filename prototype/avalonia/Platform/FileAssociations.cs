using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RustText.Demo.Platform;

public static class FileAssociations
{
    private const string ApplicationName = "RustText";
    private const string CapabilitiesPath = @"Software\RustText\Capabilities";
    private static readonly (string Extension, string ProgId, string Description)[] Associations =
    [
        (".log", "RustText.Log", "RustText log file"),
        (".txt", "RustText.Text", "RustText text file"),
        (".json", "RustText.Json", "RustText JSON file"),
        (".xml", "RustText.Xml", "RustText XML file")
    ];

    /// <summary>Registers the RustText ProgIDs and context-menu verbs in the current user's registry hive.</summary>
    public static void RegisterCurrentUser(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("File association registration is available on Windows.");
        }

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The RustText executable was not found.", fullPath);
        }

        var executableName = Path.GetFileName(fullPath);
        var openCommand = $"{QuoteWindowsArgument(fullPath)} \"%1\"";
        using var userRoot = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var classes = userRoot.CreateSubKey(@"Software\Classes", writable: true)
            ?? throw new IOException("Could not open the current user's Classes registry key.");

        using (var application = classes.CreateSubKey($@"Applications\{executableName}", writable: true)
            ?? throw new IOException("Could not create the application registry key."))
        {
            using var supportedTypes = application.CreateSubKey("SupportedTypes", writable: true)
                ?? throw new IOException("Could not create the application SupportedTypes registry key.");
            foreach (var association in Associations)
            {
                supportedTypes.SetValue(association.Extension, string.Empty, RegistryValueKind.String);
            }

            using var command = application.CreateSubKey(@"shell\open\command", writable: true)
                ?? throw new IOException("Could not create the application open command.");
            command.SetValue(null, openCommand, RegistryValueKind.String);
        }

        foreach (var association in Associations)
        {
            using (var progId = classes.CreateSubKey(association.ProgId, writable: true)
                ?? throw new IOException($"Could not create the {association.ProgId} registry key."))
            {
                progId.SetValue(null, association.Description, RegistryValueKind.String);
                using var command = progId.CreateSubKey(@"shell\open\command", writable: true)
                    ?? throw new IOException($"Could not create the {association.ProgId} open command.");
                command.SetValue(null, openCommand, RegistryValueKind.String);
            }

            using var openWith = classes.CreateSubKey($@"{association.Extension}\OpenWithProgids", writable: true)
                ?? throw new IOException("Could not create the OpenWithProgids registry key.");
            var status = RegSetValueExW(openWith.Handle, association.ProgId, 0, 0, IntPtr.Zero, 0);
            if (status != 0)
            {
                throw new System.ComponentModel.Win32Exception(status);
            }

            using var verb = classes.CreateSubKey($@"SystemFileAssociations\{association.Extension}\shell\RustText", writable: true)
                ?? throw new IOException("Could not create the Explorer context menu entry.");
            verb.SetValue(null, "Открыть в RustText", RegistryValueKind.String);
            using var verbCommand = verb.CreateSubKey("command", writable: true)
                ?? throw new IOException("Could not create the Explorer context menu command.");
            verbCommand.SetValue(null, openCommand, RegistryValueKind.String);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath, writable: true))
        {
            capabilities?.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
            capabilities?.SetValue("ApplicationDescription", "Просмотрщик и редактор логов RustText", RegistryValueKind.String);
            capabilities?.SetValue("ApplicationIcon", $"{QuoteWindowsArgument(fullPath)},0", RegistryValueKind.String);
            using var fileAssociations = capabilities?.CreateSubKey("FileAssociations", writable: true)
                ?? throw new IOException("Could not create the file association capabilities.");
            foreach (var association in Associations)
            {
                fileAssociations.SetValue(association.Extension, association.ProgId, RegistryValueKind.String);
            }
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications", writable: true))
        {
            registered?.SetValue(ApplicationName, CapabilitiesPath, RegistryValueKind.String);
        }

        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Registers RustText and opens Windows default-app settings for the user to choose associations.</summary>
    public static void RegisterAndOpenDefaultAppsSettings(string executablePath)
    {
        RegisterCurrentUser(executablePath);
        var start = new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=RustText")
        {
            UseShellExecute = true
        };
        if (Process.Start(start) is null)
        {
            throw new IOException("Windows did not open the default-app settings page.");
        }
    }

    internal static string QuoteWindowsArgument(string value)
    {
        var output = new System.Text.StringBuilder(value.Length + 2);
        output.Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
            }
            else if (character == '"')
            {
                output.Append('\\', slashes * 2 + 1);
                output.Append('"');
                slashes = 0;
            }
            else
            {
                output.Append('\\', slashes);
                slashes = 0;
                output.Append(character);
            }
        }

        output.Append('\\', slashes * 2);
        output.Append('"');
        return output.ToString();
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(
        Microsoft.Win32.SafeHandles.SafeRegistryHandle key,
        string valueName,
        int reserved,
        uint type,
        IntPtr data,
        int dataLength);
}
