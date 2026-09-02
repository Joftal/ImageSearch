namespace 以图搜图.Models;

/// <summary>
/// 搜索结果项。使用 class 而非 record，因为属性在构造后仍会被修改（如目录统计、时间戳精炼）。
/// </summary>
public class SearchResult
{
    public string 路径 { get; set; } = string.Empty;
    public float 匹配度 { get; set; }
    public string 匹配算法 { get; set; } = string.Empty;

    public string 大小 { get; set; } = string.Empty;
    public string 所属文件夹大小 { get; set; } = string.Empty;
    public int 所属文件夹文件数 { get; set; }

    /// <summary>最佳命中帧时间戳（格式化，如 12:35），仅视频结果有值</summary>
    public string 时间戳 { get; set; } = string.Empty;

    /// <summary>媒体类型：图片 / GIF / 视频</summary>
    public string 媒体类型 { get; set; } = string.Empty;

    /// <summary>该视频所有命中帧的时间点（秒），仅视频结果有值</summary>
    public List<double>? 命中时间点 { get; set; }

    /// <summary>最佳命中帧时间戳（秒，原始值），仅视频结果有值</summary>
    public double? 命中时间戳秒数 { get; set; }
}