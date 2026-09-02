using System;
using System.IO;

namespace 以图搜图;

/// <summary>
/// 数据文件路径解析：统一锚定到 exe 所在目录（便携模式），程序产生的数据不离开程序所在盘。
/// 迁移链：程序目录缺失时，依次尝试从 %LocalAppData%\ImageSearch\ 与当前工作目录的旧位置搬回。
/// 注意：若程序放在 Program Files 等受保护目录，需以管理员身份运行（config.ini 的 RunAsAdmin）；建议放普通数据盘目录。
/// </summary>
public static class DataPath
{
    private static readonly string DataDirectory = AppContext.BaseDirectory;
    private static readonly string TempDirectory = Path.Combine(AppContext.BaseDirectory, "temp");

    public static string Get(string fileName)
    {
        var path = Path.Combine(DataDirectory, fileName);
        if (File.Exists(path))
        {
            return path;
        }

        foreach (var legacy in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageSearch", fileName),
            Path.GetFullPath(fileName)
        })
        {
            if (string.Equals(legacy, path, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacy))
            {
                continue;
            }

            try
            {
                // 从旧位置（%LocalAppData% 或工作目录）搬回程序目录
                File.Move(legacy, path);
                return path;
            }
            catch (IOException)
            {
                // 迁移失败（文件占用等）时继续使用旧位置，不丢既有数据
                return legacy;
            }
            catch (UnauthorizedAccessException)
            {
                // 权限不足时继续使用旧位置，不丢既有数据
                return legacy;
            }
        }

        return path;
    }

    /// <summary>生成程序目录 temp\ 下的唯一临时文件路径（查询图/预览帧等短期文件，用完即删）。</summary>
    public static string TempFile(string extension)
    {
        Directory.CreateDirectory(TempDirectory);
        return Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + extension);
    }

    /// <summary>清理上次运行残留的临时文件（崩溃/异常退出时未能删除的）。启动时调用。</summary>
    public static void CleanupTempFiles()
    {
        try
        {
            if (!Directory.Exists(TempDirectory))
            {
                return;
            }

            // 只删超过 1 小时的陈旧文件：启动时先于此执行的实例可能正在使用刚创建的临时文件，
            // 全量删除会误伤（单实例检查在 Release 下晚于本清理执行）
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var file in Directory.EnumerateFiles(TempDirectory))
            {
                try
                {
                    if (File.GetCreationTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch { }
            }
        }
        catch
        {
            // 清理失败不影响启动
        }
    }
}
