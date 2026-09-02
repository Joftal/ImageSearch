using Masuit.Tools.Files;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace 以图搜图;

/// <summary>
/// 用外部播放器打开视频并定位到指定秒数：自动探测 mpv → PotPlayer → VLC → MPC-HC/BE，
/// config.ini 的 PlayerPath/PlayerArgs 可完全覆盖（含任意其他播放器）。
/// 探测不到播放器时回退系统默认关联打开（从头播放）。
/// </summary>
public static class PlayerLauncher
{
    private sealed record PlayerConfig(string Path, string Type, string? ArgsTemplate);

    private static PlayerConfig? _cached;
    private static bool _resolved;

    /// <summary>打开视频并跳到 seconds 秒处；为 null/0 或探测不到播放器时按系统默认方式打开。</summary>
    public static void OpenAtPosition(string file, double? seconds)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            return;
        }

        var player = ResolvePlayer();
        if (player == null || seconds is null or <= 0)
        {
            ShellOpen(file);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = player.Path,
                Arguments = BuildArgs(player, file, seconds.Value),
                UseShellExecute = false
            })?.Dispose();
        }
        catch
        {
            ShellOpen(file);
        }
    }

    private static void ShellOpen(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // 无可执行操作
        }
    }

    /// <summary>解析播放器（结果缓存整个进程生命周期）：config.ini 覆盖优先，其次注册表 App Paths，最后常见安装目录。</summary>
    private static PlayerConfig? ResolvePlayer()
    {
        if (_resolved)
        {
            return _cached;
        }
        _resolved = true;

        var config = new IniFile(DataPath.Get("config.ini"));
        var customPath = config.GetValue("Global", "PlayerPath", "");
        var customArgs = config.GetValue("Global", "PlayerArgs", "");
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            _cached = new PlayerConfig(customPath, GuessType(customPath) ?? "custom", string.IsNullOrWhiteSpace(customArgs) ? null : customArgs);
            return _cached;
        }

        // (exe 名, 类型, 常见安装目录回退)
        (string exe, string type, string?[] fallbacks)[] candidates =
        [
            ("mpv.exe", "mpv", [@"C:\Program Files\mpv\mpv.exe"]),
            ("PotPlayerMini64.exe", "potplayer", [@"C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe", @"C:\Program Files (x86)\DAUM\PotPlayer\PotPlayerMini64.exe"]),
            ("vlc.exe", "vlc", [@"C:\Program Files\VideoLAN\VLC\vlc.exe", @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe"]),
            ("mpc-hc64.exe", "mpc-hc", [@"C:\Program Files\MPC-HC\mpc-hc64.exe"]),
            ("mpc-be64.exe", "mpc-be", [@"C:\Program Files\MPC-BE x64\mpc-be64.exe"]),
        ];

        foreach (var (exe, type, fallbacks) in candidates)
        {
            var path = FindAppPath(exe) ?? fallbacks.FirstOrDefault(File.Exists);
            if (path != null)
            {
                _cached = new PlayerConfig(path, type, string.IsNullOrWhiteSpace(customArgs) ? null : customArgs);
                return _cached;
            }
        }

        return null;
    }

    /// <summary>注册表 HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths 查询 exe 安装位置。</summary>
    private static string? FindAppPath(string exeName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            var path = key?.GetValue(null) as string;
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GuessType(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("mpv")) return "mpv";
        if (name.Contains("potplayer")) return "potplayer";
        if (name.Contains("vlc")) return "vlc";
        if (name.Contains("mpc-hc")) return "mpc-hc";
        if (name.Contains("mpc-be")) return "mpc-be";
        return null;
    }

    private static string BuildArgs(PlayerConfig player, string file, double seconds)
    {
        // 用户自定义参数模板优先：{file}=视频路径 {seconds}=秒（不变文化，避免逗号小数区域出错）
        if (!string.IsNullOrEmpty(player.ArgsTemplate))
        {
            return player.ArgsTemplate
                .Replace("{file}", file)
                .Replace("{seconds}", seconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        return player.Type switch
        {
            "mpv" => FormattableString.Invariant($"--start={seconds:F3} \"{file}\""),
            "vlc" => FormattableString.Invariant($"--start-time={seconds:F0} \"{file}\""),
            "potplayer" => $"\"{file}\" /seek={ToHms(seconds)}",
            "mpc-hc" or "mpc-be" => $"\"{file}\" /start {ToHms(seconds)}",
            _ => $"\"{file}\"",
        };
    }

    private static string ToHms(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(ts.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
    }
}
