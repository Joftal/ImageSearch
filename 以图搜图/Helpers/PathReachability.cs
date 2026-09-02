using System.IO;

namespace 以图搜图;

/// <summary>
/// 路径可达性判定：用于"移除无效索引"时区分「文件确实被删除」与「磁盘/网络盘暂时不可达」。
/// 不可达时宁可保留索引，也不能误删——枚举失败与"目录真的空了"在 GetFiles 层面无法区分。
/// </summary>
public static class PathReachability
{
    /// <summary>路径所在驱动器/共享根当前是否可达（盘脱机、映射断开、NAS 掉线均返回 false）。</summary>
    public static bool IsDriveRootReachable(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>确认文件已被删除（而非磁盘掉线/父目录无权限导致枚举不到）。</summary>
    public static bool FileConfirmedDeleted(string path)
    {
        try
        {
            // 盘不可达 或 文件实际仍在 → 不能确认删除
            if (!IsDriveRootReachable(path) || File.Exists(path))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return true; // 父目录链已不存在，文件确认随之一同消失
            }

            // 父目录存在且能枚举 → 文件不在枚举结果里才算确认删除；
            // 父目录存在但无权限枚举（IgnoreInaccessible 会静默跳过该子树）→ 无法判断，保守保留
            Directory.EnumerateFileSystemEntries(parent, "*", new System.IO.EnumerationOptions { AttributesToSkip = 0 })
                .Any();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
