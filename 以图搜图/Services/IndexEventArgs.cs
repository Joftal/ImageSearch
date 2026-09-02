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
}

public class IndexCompletedEventArgs : EventArgs
{
    public double ElapsedSeconds { get; init; }
    public int FilesProcessed { get; init; }
    public List<string> Errors { get; init; } = new();
}