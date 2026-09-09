using System.IO;
using Microsoft.Win32;

namespace VirtualDesktopTaskbar;

/// <summary>极简配置：开机启动（HKCU Run 键）。</summary>
internal static class Settings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VirtualDesktopTaskbar";

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null) return;
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>轻量日志：%LOCALAPPDATA%\VirtualDesktopTaskbar\log.txt</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path;

    static Log()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VirtualDesktopTaskbar");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "log.txt");
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\r\n");
        }
        catch
        {
            // 日志失败不影响主功能
        }
    }
}
