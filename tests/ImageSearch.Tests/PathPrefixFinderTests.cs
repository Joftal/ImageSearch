using Xunit;
using 以图搜图;

namespace ImageSearch.Tests;

/// <summary>
/// PathPrefixFinder 把索引过的文件路径聚成"待重扫的根目录集合"，
/// 更新索引/移除无效索引都依赖它——分组错误会导致扫描面扩大或缩小，必须有回归保护。
/// </summary>
public class PathPrefixFinderTests
{
    [Fact]
    public void 空输入返回空集合()
    {
        var result = PathPrefixFinder.FindLongestCommonPathPrefixes(Array.Empty<string>(), 3);
        Assert.Empty(result);
    }

    [Fact]
    public void 单个路径返回其父目录()
    {
        var result = PathPrefixFinder.FindLongestCommonPathPrefixes([@"D:\pics\cat.jpg"], 3);
        Assert.Single(result);
        Assert.Contains(@"D:\pics", result);
    }

    [Fact]
    public void 同目录多个文件返回该目录()
    {
        var result = PathPrefixFinder.FindLongestCommonPathPrefixes([@"D:\pics\a.jpg", @"D:\pics\b.jpg", @"D:\pics\c.jpg"], 3);
        Assert.Single(result);
        Assert.Contains(@"D:\pics", result);
    }

    [Fact]
    public void 同前缀深层目录求出公共祖先()
    {
        // 组键为前 3 段，组内取最长公共前缀
        var result = PathPrefixFinder.FindLongestCommonPathPrefixes([@"D:\root\sub1\x.jpg", @"D:\root\sub1\y.jpg"], 3);
        Assert.Single(result);
        Assert.Contains(@"D:\root\sub1", result);
    }

    [Fact]
    public void 不同第一层目录分为不同组()
    {
        var result = PathPrefixFinder.FindLongestCommonPathPrefixes([@"D:\a\x\1.jpg", @"D:\b\y\2.jpg"], 3);
        Assert.Equal(2, result.Count);
        Assert.Contains(@"D:\a\x", result);
        Assert.Contains(@"D:\b\y", result);
    }

    [Fact]
    public void 不同盘符必然分为不同组()
    {
        var result = PathPrefixFinder.FindLongestCommonPathPrefixes([@"C:\u\p\1.jpg", @"D:\v\q\2.jpg"], 3);
        Assert.Equal(2, result.Count);
        Assert.Contains(@"C:\u\p", result);
        Assert.Contains(@"D:\v\q", result);
    }

    [Fact]
    public void 组内公共前缀不会越过文件名段()
    {
        // 文件直接位于被索引目录根部时，公共前缀应止于目录本身而不是文件名
        var result = PathPrefixFinder.FindLongestCommonPathPrefixes([@"D:\pics\a.jpg", @"D:\pics\b.jpg"], 3);
        Assert.Single(result);
        Assert.Contains(@"D:\pics", result);
    }

    [Fact]
    public void 空路径与空白项被忽略()
    {
        var result = PathPrefixFinder.FindLongestCommonPathPrefixes(["", @"D:\pics\a.jpg"], 3);
        Assert.Single(result);
        Assert.Contains(@"D:\pics", result);
    }

    [Fact]
    public void UNC路径正确处理()
    {
        var result = PathPrefixFinder.FindLongestCommonPathPrefixes([@"\\server\share\dir\1.jpg", @"\\server\share\dir\2.jpg"], 3);
        Assert.Single(result);
        Assert.Contains(@"\\server\share\dir", result);
    }
}
