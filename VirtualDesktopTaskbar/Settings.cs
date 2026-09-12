using System.IO;
using Microsoft.Win32;

namespace VirtualDesktopTaskbar;

/// <summary>极简配置：开机启动（HKCU Run 键）。</summary>
internal static class Settings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VirtualDesktopTaskbar";

    /// <summary>仅当注册表值存在且路径仍指向当前 exe 时才算已启用
    /// （exe 移动/删除后显示未启用，用户重新勾选即覆盖为新路径）。</summary>
    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (key?.GetValue(ValueName) is not string value) return false;
        var exePath = Environment.ProcessPath;
        return exePath != null &&
               value.Contains(exePath, StringComparison.OrdinalIgnoreCase);
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
