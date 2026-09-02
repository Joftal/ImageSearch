namespace 以图搜图.Models;

/// <summary>
/// 视频帧索引项：fps=1 采样 + 相邻帧去重后的逐帧哈希与时间戳（按位对齐）。
/// 使用 class 而非 record，因为 List 属性在索引期间持续被修改。
/// </summary>
public sealed class VideoIndexItem(string filePath)
{
    /// <summary>视频文件路径</summary>
    public string FilePath { get; } = filePath;

    public List<ulong[]> DifferenceHash { get; set; } = new List<ulong[]>();

    public List<ulong> DctHash { get; set; } = new List<ulong>();

    public List<ulong> DctHash64 { get; set; } = new List<ulong>();

    /// <summary>每个保留帧的时间戳（秒），与哈希列表按位对齐</summary>
    public List<double> Timestamps { get; set; } = new List<double>();

    /// <summary>视频总时长（秒），用于索引进度与结果展示</summary>
    public double Duration { get; set; }
}
