namespace 以图搜图.Services;

public class IndexProgressEventArgs : EventArgs
{
    public string Filename { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
    public double Speed { get; init; }
    public double ThroughputMB { get; init; }
    public int ProcessedFiles { get; init; }
    public int TotalFiles { get; init; }
    public double ProgressPercentage => TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles * 100 : 0;

    /// <summary>该事件对应文件自身的进度（0~1），仅视频索引的逐帧事件填充，供并行任务各自的进度卡片使用</summary>
    public double? FileProgressFraction { get; init; }
}

public class IndexCompletedEventArgs : EventArgs
{
    public double ElapsedSeconds { get; init; }
    public int FilesProcessed { get; init; }
    public List<string> Errors { get; init; } = new();
}