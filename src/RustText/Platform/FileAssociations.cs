using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RustText.Demo.Platform;

public static class FileAssociations
{
    private const string ApplicationName = "RustText";
    private const string CapabilitiesPath = @"Software\RustText\Capabilities";
    private const string FileAssociationsPath = CapabilitiesPath + @"\FileAssociations";
    private const string RegistrationStamp = "OpenWithRegistration";
    private const string DefaultAppsUri = "ms-settings:defaultapps";
    private static readonly (string Extension, string ProgId, string Description)[] Associations =
    [
        (".log", "RustText.Log", "RustText log file"),
        (".txt", "RustText.Text", "RustText text file"),
        (".json", "RustText.Json", "RustText JSON file"),
        (".xml", "RustText.Xml", "RustText XML file"),
        (".jsonl", "RustText.JsonLines", "RustText JSON Lines file"),
        (".ndjson", "RustText.NdJson", "RustText NDJSON file"),
        (".cfg", "RustText.Config", "RustText configuration file"),
        (".ini", "RustText.Ini", "RustText INI file"),
        (".csv", "RustText.Csv", "RustText CSV file"),
        (".md", "RustText.Markdown", "RustText Markdown file")
    ];

    internal static IReadOnlyList<string> SupportedExtensions { get; } =
        Array.AsReadOnly(Associations.Select(association => association.Extension).ToArray());

    /// <summary>Registers the RustText ProgIDs and context-menu verbs in the current user's registry hive.</summary>
    public static void RegisterCurrentUser(string executablePath) => RegisterCurrentUser(executablePath, null, false);

    private static void RegisterCurrentUser(string executablePath, IReadOnlySet<string>? selected, bool onlyIfChanged)
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

        using var userRoot = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        if (!WriteRegistration(userRoot, fullPath, selected, onlyIfChanged)) return;
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    internal static bool CanRegisterOnStartup(string? executablePath, string? profileOverride) =>
        string.IsNullOrWhiteSpace(profileOverride)
        && string.Equals(Path.GetFileName(executablePath), "rusttext.exe", StringComparison.OrdinalIgnoreCase);

    internal static void TryRegisterOnStartup(string? executablePath, string? profileOverride)
    {
        if (!OperatingSystem.IsWindows() || !CanRegisterOnStartup(executablePath, profileOverride)) return;

        try
        {
            RegisterCurrentUser(executablePath!, null, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or ArgumentException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Не удалось зарегистрировать RustText в Проводнике: {error.Message}");
        }
    }

    internal static IReadOnlySet<string> ReadSelectedExtensions()
    {
        using var userRoot = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        return ReadSelectedExtensions(userRoot);
    }

    internal static IReadOnlySet<string> ReadSelectedExtensions(RegistryKey userRoot)
    {
        using var registered = userRoot.OpenSubKey(FileAssociationsPath);
        var extensions = registered?.GetValueNames() ?? [".log", ".txt", ".json", ".xml"];
        return new HashSet<string>(extensions.Where(extension => SupportedExtensions.Contains(extension,
            StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
    }

    internal static bool WriteRegistration(RegistryKey userRoot, string fullPath,
        IReadOnlySet<string>? selected = null, bool onlyIfChanged = false)
    {
        using var capabilities = userRoot.CreateSubKey(CapabilitiesPath, writable: true)
            ?? throw new IOException("Could not create the application capabilities.");
        var stamp = $"1|{fullPath}|{string.Join(',', SupportedExtensions)}";
        if (onlyIfChanged && string.Equals(capabilities.GetValue(RegistrationStamp) as string, stamp, StringComparison.Ordinal))
            return false;

        selected ??= ReadSelectedExtensions(userRoot);
        // An interrupted registration must be retried on the next ordinary startup.
        capabilities.DeleteValue(RegistrationStamp, throwOnMissingValue: false);
        var executableName = Path.GetFileName(fullPath);
        var openCommand = $"{QuoteWindowsArgument(fullPath)} \"%1\"";
        using var classes = userRoot.CreateSubKey(@"Software\Classes", writable: true)
            ?? throw new IOException("Could not open the current user's Classes registry key.");

        using (var application = classes.CreateSubKey($@"Applications\{executableName}", writable: true)
            ?? throw new IOException("Could not create the application registry key."))
        {
            application.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
            using var icon = application.CreateSubKey("DefaultIcon", writable: true)
                ?? throw new IOException("Could not create the application icon key.");
            icon.SetValue(null, $"{QuoteWindowsArgument(fullPath)},0", RegistryValueKind.String);
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

        capabilities.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
        capabilities.SetValue("ApplicationDescription", "Просмотрщик и редактор логов RustText", RegistryValueKind.String);
        capabilities.SetValue("ApplicationIcon", $"{QuoteWindowsArgument(fullPath)},0", RegistryValueKind.String);
        using (var fileAssociations = capabilities.CreateSubKey("FileAssociations", writable: true)
            ?? throw new IOException("Could not create the file association capabilities."))
        {
            foreach (var association in Associations)
            {
                if (selected.Contains(association.Extension))
                    fileAssociations.SetValue(association.Extension, association.ProgId, RegistryValueKind.String);
                else
                    fileAssociations.DeleteValue(association.Extension, throwOnMissingValue: false);
            }
        }

        using var registered = userRoot.CreateSubKey(@"Software\RegisteredApplications", writable: true)
            ?? throw new IOException("Could not create the registered applications key.");
        registered.SetValue(ApplicationName, CapabilitiesPath, RegistryValueKind.String);
        capabilities.SetValue(RegistrationStamp, stamp, RegistryValueKind.String);
        return true;
    }

    /// <summary>Registers RustText and opens Windows default-app settings for the user to choose associations.</summary>
    internal static void RegisterAndOpenDefaultAppsSettings(string executablePath, IReadOnlySet<string> selected)
    {
        RegisterCurrentUser(executablePath, selected, false);
        if (TryOpenSettings(selected.Count == 0 ? DefaultAppsUri : $"{DefaultAppsUri}?registeredAppUser={ApplicationName}"))
            return;
        if (selected.Count != 0 && TryOpenSettings(DefaultAppsUri)) return;
        throw new IOException("Не удалось открыть параметры приложений Windows.");
    }

    private static bool TryOpenSettings(string uri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
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
