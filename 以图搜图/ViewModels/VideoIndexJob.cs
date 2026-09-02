using CommunityToolkit.Mvvm.ComponentModel;

namespace 以图搜图.ViewModels;

/// <summary>单个视频索引任务的进度卡片：并行索引时一视频一卡，完成后由 FileCompleted 事件移除。</summary>
public partial class VideoIndexJob : ObservableObject
{
    [ObservableProperty]
    private string fileName = string.Empty;

    /// <summary>该视频自身进度 0~100</summary>
    [ObservableProperty]
    private double percent;

    [ObservableProperty]
    private string percentText = string.Empty;
}
