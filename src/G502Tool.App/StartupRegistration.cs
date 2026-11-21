using Microsoft.Win32;

namespace G502Tool.App;

/// <summary>
/// "Start with Windows": a per-user entry under HKCU\...\Run (no admin rights, no service, no scheduled task).
/// Windows then opens the app minimized at sign-in; closing the window still ends the process.
/// </summary>
static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "G502HeroConfig";
    public const string MinimizedArg = "--minimized";

    static string Command => $"\"{Environment.ProcessPath}\" {MinimizedArg}";

    /// <summary>True only when the entry exists and points at this copy of the exe.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string v && string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The exe path registered by another copy of the app, if any (shown so a stale entry is not a surprise).</summary>
    public static string? OtherRegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string v && !string.Equals(v, Command, StringComparison.OrdinalIgnoreCase) ? v : null;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
