using Microsoft.Win32;

namespace SampAccountManager.Services;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SampAccountManager";

    public static void Set(bool enabled, string exePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, "\"" + exePath + "\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
