using Xunit;
using 以图搜图;

namespace ImageSearch.Tests;

/// <summary>
/// 移除无效索引的防误删判定：必须区分「文件真被删」与「盘掉线/父目录无权限/根无法判断」。
/// 用真实临时目录构造场景（不 mock 文件系统）。
/// </summary>
public class PathReachabilityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ImageSearchTests_" + Guid.NewGuid().ToString("N"));

    public PathReachabilityTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void 存在的文件不能判定为已删除()
    {
        var file = Path.Combine(_tempDir, "a.jpg");
        File.WriteAllText(file, "x");
        Assert.False(PathReachability.FileConfirmedDeleted(file));
    }

    [Fact]
    public void 父目录存在且文件不在其中判定为已删除()
    {
        // 目录存在但从未创建该文件（等价于建索引后被用户删除）
        var file = Path.Combine(_tempDir, "ghost.jpg");
        Assert.True(PathReachability.FileConfirmedDeleted(file));
    }

    [Fact]
    public void 父目录链不存在判定为已删除()
    {
        var file = Path.Combine(_tempDir, "gone_dir", "sub", "a.jpg");
        Assert.True(PathReachability.FileConfirmedDeleted(file));
    }

    [Fact]
    public void 根路径无法判断时保守返回false()
    {
        // 相对路径无根：无法判断可达性，必须保守保留
        Assert.False(PathReachability.FileConfirmedDeleted(@"nonexistent_dir_xyz\file.jpg"));
    }

    [Fact]
    public void 可达性判定_真实临时目录可达()
    {
        Assert.True(PathReachability.IsDriveRootReachable(Path.Combine(_tempDir, "a.jpg")));
    }

    [Fact]
    public void 可达性判定_相对路径不可达()
    {
        Assert.False(PathReachability.IsDriveRootReachable("relative-path.jpg"));
    }
}
