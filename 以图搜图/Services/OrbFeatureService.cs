using Masuit.Tools;
using Masuit.Tools.Logging;
using Masuit.Tools.Systems;
using OpenCvSharp;
using SkiaSharp;
using System.Collections.Concurrent;
using System.IO;
using 以图搜图.Models;

namespace 以图搜图.Services;

/// <summary>一帧的 ORB 特征：时间戳（秒）+ 关键点坐标 + 描述子（n×32 字节）</summary>
public sealed record OrbFrame(double Timestamp, Point2f[] Keypoints, byte[] Descriptors);

/// <summary>单帧匹配结果：good 匹配数、RANSAC 内点数、内点率</summary>
public readonly record struct FrameMatch(int Good, int Inliers, float Precision);

/// <summary>
/// ORB 特征索引与深度匹配：索引时从 640px 灰度帧提取 nfeatures=500 的 ORB 描述子，
/// 持久化到 video_orb_index.bin（二进制，[path][frameCount]×[ts][kpCount][kp xy][desc]）。
/// 深度搜索：BFMatcher.Hamming + ratio test + findHomography(RANSAC) 内点判定，
/// 查询图多尺度提取以覆盖裁剪区域与整帧的尺度差，粗命中后高帧率二次精确定位。
/// </summary>
public sealed class OrbFeatureService : Disposable
{
    private const int OrbFeatures = 500;
    private const float RatioTest = 0.75f;
    private const int MinGoodMatches = 10;
    private const int MinHomographyInliers = 8;

    /// <summary>查询图多尺度提取的缩放系数：覆盖裁剪区域（小图）到整帧（大图）的尺度差</summary>
    private static readonly float[] QueryScales = [0.5f, 0.75f, 1f];

    /// <summary>粗命中后二次精确定位的区间半径（秒）与采样率</summary>
    private const double RefineWindowSeconds = 2.5;
    private const double RefineFps = 5;

    private static readonly byte[] Magic = "ORB2"u8.ToArray();

    private readonly ConcurrentHashQueue<int> _writeQueue = new();
    private readonly string _orbPath = DataPath.Get("video_orb_index.bin");
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly Task? _writeTask;

    public static OrbFeatureService Instance { get; } = new OrbFeatureService();

    private OrbFeatureService()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _writeTask = StartWriteTaskAsync(_cancellationTokenSource.Token);
    }

    public ConcurrentDictionary<string, List<OrbFrame>> OrbIndex { get; private set; } = new();
    private readonly ConcurrentDictionary<string, object> _orbLocks = new();

    public event EventHandler? IndexUpdated;

    private async Task StartWriteTaskAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_writeQueue.TryDequeue(out _))
                {
                    _writeQueue.Clear();
                    await WriteIndexAsync();
                    IndexUpdated?.Invoke(this, EventArgs.Empty);
                }

                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常的取消操作
        }
    }

    /// <summary>从灰度帧提取 ORB 特征，关键点为空时返回 null。</summary>
    public static OrbFrame? Extract(double timestamp, SKBitmap grayFrame)
    {
        using var mat = Mat.FromPixelData(grayFrame.Height, grayFrame.Width, MatType.CV_8UC1, grayFrame.GetPixels(), grayFrame.RowBytes).Clone();
        using var orb = ORB.Create(OrbFeatures, edgeThreshold: 8);
        using var descriptors = new Mat();
        orb.DetectAndCompute(mat, null, out var keypoints, descriptors);
        if (descriptors.Empty() || keypoints.Length < MinGoodMatches)
        {
            return null;
        }

        // 手动拷贝 raw bytes（Mat.ToBytes() 是 ImEncode 带 header，不是 raw data）
        var raw = new byte[descriptors.Rows * descriptors.Cols];
        System.Runtime.InteropServices.Marshal.Copy(descriptors.Data, raw, 0, raw.Length);
        return new OrbFrame(timestamp, keypoints.Select(k => k.Pt).ToArray(), raw);
    }

    /// <summary>索引时逐帧追加（描述子为空的帧跳过，与哈希索引按时间戳对账即可）。</summary>
    public void AddFrame(string videoPath, double timestamp, SKBitmap grayFrame)
    {
        var frame = Extract(timestamp, grayFrame);
        if (frame == null)
        {
            return;
        }

        OrbIndex.GetOrAdd(videoPath, _ => new List<OrbFrame>());
        lock (_orbLocks.GetOrAdd(videoPath, _ => new object()))
        {
            OrbIndex[videoPath].Add(frame);
        }
    }

    /// <summary>
    /// 批量并行提取 ORB 特征并追加到索引。调用方需保证帧位图在方法返回前不被 Dispose。
    /// 并行提取后一次性 AddRange，避免并发 Add 导致的线程安全问题。
    /// </summary>
    public void AddFramesParallel(string videoPath, IReadOnlyList<(double Timestamp, SKBitmap Bitmap)> frames)
    {
        if (frames.Count == 0)
        {
            return;
        }

        // 并行提取，AsOrdered 保持帧序
        var extracted = frames.AsParallel().AsOrdered()
            .Select(f => Extract(f.Timestamp, f.Bitmap))
            .Where(f => f != null)
            .Cast<OrbFrame>()
            .ToList();

        if (extracted.Count > 0)
        {
            OrbIndex.GetOrAdd(videoPath, _ => new List<OrbFrame>());
            lock (_orbLocks.GetOrAdd(videoPath, _ => new object()))
            {
                OrbIndex[videoPath].AddRange(extracted);
            }
        }
    }

    public void RemoveFromIndex(string path)
    {
        if (OrbIndex.TryRemove(path, out _))
        {
            _writeQueue.Enqueue(1);
        }
    }

    /// <summary>清空全部 ORB 特征索引（内存 + 落盘）。</summary>
    public void ClearIndex()
    {
        OrbIndex.Clear();
        _writeQueue.Clear();
        _writeQueue.Enqueue(1);
    }

    public void RemoveInvalidIndexes(IEnumerable<string> existingVideoFiles)
    {
        var existing = existingVideoFiles as ICollection<string> ?? existingVideoFiles.ToArray();
        if (existing.Count == 0 && OrbIndex.Count > 0)
        {
            LogManager.Error(new Exception("移除无效 ORB 索引的输入列表为空，疑似目录枚举失败，已跳过清理以避免误删整个 ORB 索引库"));
            return;
        }

        var removed = false;
        // 仅移除「确认已删除」的索引；盘脱机/网络盘掉线/父目录无权限时保留
        foreach (var key in OrbIndex.Keys.Except(existing, StringComparer.OrdinalIgnoreCase).Where(PathReachability.FileConfirmedDeleted).ToList())
        {
            OrbIndex.TryRemove(key, out _);
            removed = true;
        }

        if (removed)
        {
            _writeQueue.Enqueue(1);
        }
    }

    /// <summary>视频索引完成（或中断）后统一落盘。</summary>
    public void Flush() => _writeQueue.Enqueue(1);

    /// <summary>
    /// 深度搜索：查询图（任意尺寸灰度图）多尺度提取 ORB 后与全部视频帧匹配。
    /// 评分 = 内点数 × 内点率（兼顾命中强度与几何一致性，避免少量高内点率误命中排在真正强命中前面）。
    /// 粗命中后对命中点附近 ±窗口做高帧率二次抽帧精确定位，修正时间戳。
    /// 返回按最佳评分降序的视频结果（含全部命中时间点）。
    /// </summary>
    public async Task<List<SearchResult>> SearchDeepAsync(SKBitmap queryGray, ConcurrentDictionary<string, VideoIndexItem> videoIndex, Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        // 多尺度提取：查询图可能是视频内小区域的裁剪（小图）或整帧截图（大图），
        // 与索引帧存在数倍尺度差。先归一化到索引帧宽度，再做 0.5/0.75/1.0 三档，
        // 覆盖「查询区域在帧内被放大/缩小」的尺度组合
        var queryFrames = new List<OrbFrame>();
        using (var normalized = NormalizeWidth(queryGray))
        {
            foreach (var scale in QueryScales)
            {
                using var scaled = ScaleTo(normalized, scale);
                if (Extract(0, scaled) is { } frame)
                {
                    queryFrames.Add(frame);
                }
            }
        }

        if (queryFrames.Count == 0)
        {
            return new List<SearchResult>();
        }

        // 快照 OrbIndex，避免搜索期间索引构建线程并发修改 List<OrbFrame>
        // 对每个视频的帧列表在 per-key 锁保护下做快照，防止并发 AddRange 导致异常
        var orbSnapshot = OrbIndex.ToArray()
            .Select(e =>
            {
                var lockObj = _orbLocks.GetOrAdd(e.Key, _ => new object());
                OrbFrame[] frames;
                lock (lockObj) { frames = e.Value.ToArray(); }
                return (e.Key, Frames: frames);
            })
            .ToArray();
        var total = orbSnapshot.Sum(e => e.Frames.Length);
        var processed = 0;
        var bag = new ConcurrentBag<(string path, double ts, float score)>();

        await Task.Run(() =>
        {
            orbSnapshot.AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount).WithCancellation(cancellationToken).ForAll(entry =>
            {
                foreach (var frame in entry.Frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var best = MatchFrameMulti(queryFrames, frame);
                    if (best is { Good: >= MinGoodMatches, Inliers: >= MinHomographyInliers })
                    {
                        // 评分 = 内点数 × 内点率（原始分，用于排序和精确定位比较）
                        bag.Add((entry.Key, frame.Timestamp, best.Value.Inliers * best.Value.Precision));
                    }

                    var p = Interlocked.Increment(ref processed);
                    if (p % 200 == 0)
                    {
                        progress?.Invoke(p, total);
                    }
                }
            });
        }, cancellationToken);

        // 按视频聚合：最佳命中定匹配度与时间戳，全部命中帧收进 命中时间点
        var results = bag.GroupBy(x => x.path).Select(g =>
        {
            var best = g.OrderByDescending(x => x.score).First();
            var timeSpan = TimeSpan.FromSeconds(best.ts);
            return new SearchResult
            {
                路径 = g.Key,
                // 归一化到 0~1：使用 S 形映射 score/(50+score) 保留高分段区分度。
                // score=10→0.17, 50→0.50, 100→0.67, 200→0.80, 300→0.86
                匹配度 = best.score / (50f + best.score),
                匹配算法 = "ORB 深度匹配",
                媒体类型 = "视频",
                时间戳 = timeSpan.ToString(timeSpan.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss"),
                命中时间戳秒数 = best.ts,
                命中时间点 = g.Select(x => x.ts).Order().ToList()
            };
        }).OrderByDescending(r => r.匹配度).ToList();

        // 粗命中后二次精确定位：fps=1 采样可能错过目标画面出现的精确时刻，
        // 在命中点附近高帧率重抽帧匹配，修正时间戳到真实出现时刻
        foreach (var result in results.Take(10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefineTimestampAsync(result, queryFrames, cancellationToken);
        }

        return results.OrderByDescending(r => r.匹配度).ToList();
    }

    /// <summary>多尺度查询帧与单帧匹配，返回各尺度中内点数最多的一次。</summary>
    private static FrameMatch? MatchFrameMulti(List<OrbFrame> queryFrames, OrbFrame frame)
    {
        FrameMatch? best = null;
        foreach (var query in queryFrames)
        {
            var match = MatchFrame(query, frame);
            if (best == null || match.Inliers > best.Value.Inliers)
            {
                best = match;
            }
        }

        return best;
    }

    /// <summary>
    /// 高帧率二次精确定位：在粗命中时间点 ±窗口内重抽帧，逐帧匹配取最佳，
    /// 修正结果的时间戳与命中时间点。失败（抽帧失败/无更优命中）时保持粗命中结果。
    /// </summary>
    private static async Task RefineTimestampAsync(SearchResult result, List<OrbFrame> queryFrames, CancellationToken cancellationToken)
    {
        try
        {
            if (result.命中时间戳秒数 is not { } coarseTs)
            {
                return;
            }

            var probed = await VideoFrameExtractor.ProbeAsync(result.路径, cancellationToken);
            if (probed is not { } info)
            {
                return;
            }

            var start = Math.Max(0, coarseTs - RefineWindowSeconds);
            var end = coarseTs + RefineWindowSeconds;
            if (info.duration > 0)
            {
                end = Math.Min(end, info.duration);
            }
            if (end - start < 0.5)
            {
                return;
            }

            var bestScore = 0f;
            var bestTs = coarseTs;
            var scored = new List<(double ts, float score)>();
            foreach (var frame in VideoFrameExtractor.ExtractFrames(result.路径, info.width, info.height, RefineFps, start, end, cancellationToken))
            {
                using (frame)
                {
                    if (Extract(frame.Timestamp, frame.Bitmap) is not { } orbFrame)
                    {
                        continue;
                    }

                    if (MatchFrameMulti(queryFrames, orbFrame) is { Good: >= MinGoodMatches, Inliers: >= MinHomographyInliers } m)
                    {
                        var score = m.Inliers * m.Precision;
                        scored.Add((frame.Timestamp, score));
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestTs = frame.Timestamp;
                        }
                    }
                }
            }

            if (bestScore > 0)
            {
                result.命中时间戳秒数 = bestTs;
                var timeSpan = TimeSpan.FromSeconds(bestTs);
                result.时间戳 = timeSpan.ToString(timeSpan.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");

                // 只保留得分 ≥ 最佳 80% 的帧作为命中点：低分帧是画面自相似/静态背景的弱误命中，
                // 全部收进命中时间点会让用户误以为目标画面横跨整个区间
                var hits = scored.Where(s => s.score >= bestScore * 0.8f).Select(s => s.ts).Order().ToList();
                if (hits.Count > 0)
                {
                    result.命中时间点 = hits;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogManager.Error(new Exception($"ORB 精确定位失败 {result.路径}: {ex.Message}", ex));
        }
    }

    /// <summary>ratio test + RANSAC 单应性校验，返回 (good 匹配数, 内点数, 内点率)。</summary>
    private static FrameMatch MatchFrame(OrbFrame query, OrbFrame frame)
    {
        using var queryDesc = Mat.FromPixelData(query.Keypoints.Length, 32, MatType.CV_8UC1, query.Descriptors);
        using var frameDesc = Mat.FromPixelData(frame.Keypoints.Length, 32, MatType.CV_8UC1, frame.Descriptors);
        using var matcher = new BFMatcher(NormTypes.Hamming);
        var knn = matcher.KnnMatch(queryDesc, frameDesc, 2);

        var queryPts = new List<Point2f>();
        var framePts = new List<Point2f>();
        foreach (var pair in knn)
        {
            if (pair.Length == 2 && pair[0].Distance < RatioTest * pair[1].Distance)
            {
                queryPts.Add(query.Keypoints[pair[0].QueryIdx]);
                framePts.Add(frame.Keypoints[pair[0].TrainIdx]);
            }
        }

        if (queryPts.Count < MinGoodMatches)
        {
            return new FrameMatch(queryPts.Count, 0, 0);
        }

        using var p1 = InputArray.Create(queryPts);
        using var p2 = InputArray.Create(framePts);
        using var mask = new Mat();
        Cv2.FindHomography(p1, p2, HomographyMethods.Ransac, 3, mask);
        if (mask.Empty())
        {
            return new FrameMatch(queryPts.Count, 0, 0);
        }

        var inliers = Cv2.CountNonZero(mask);
        return new FrameMatch(queryPts.Count, inliers, inliers / (float)queryPts.Count);
    }

    /// <summary>把查询图宽度归一化到索引帧宽度（更宽则缩小，更窄则放大），保持与索引同基准。</summary>
    private static SKBitmap NormalizeWidth(SKBitmap gray)
    {
        if (gray.Width == VideoFrameExtractor.FrameWidth)
        {
            return gray.Copy();
        }

        var h = Math.Max(2, (int)Math.Round((double)VideoFrameExtractor.FrameWidth * gray.Height / gray.Width));
        return gray.Resize(new SKImageInfo(VideoFrameExtractor.FrameWidth, h, SKColorType.Gray8, SKAlphaType.Opaque), SKSamplingOptions.Default);
    }

    /// <summary>按比例缩放灰度图（scale=1 时返回副本，确保调用方拥有独立所有权）。</summary>
    private static SKBitmap ScaleTo(SKBitmap gray, float scale)
    {
        if (Math.Abs(scale - 1f) < 0.01f)
        {
            return gray.Copy();
        }

        var w = Math.Max(16, (int)Math.Round(gray.Width * scale));
        var h = Math.Max(16, (int)Math.Round(gray.Height * scale));
        return gray.Resize(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque), SKSamplingOptions.Default);
    }

    public async Task LoadIndexAsync()
    {
        try
        {
            if (!File.Exists(_orbPath) || new FileInfo(_orbPath).Length == 0)
            {
                return;
            }

            var buffer = await File.ReadAllBytesAsync(_orbPath);
            using var reader = new BinaryReader(new MemoryStream(buffer));
            if (!reader.ReadBytes(4).SequenceEqual(Magic))
            {
                return; // 旧格式或损坏：放弃 ORB 缓存，随下次索引重建
            }

            var index = new ConcurrentDictionary<string, List<OrbFrame>>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                var path = reader.ReadString();
                var frameCount = reader.ReadInt32();
                if (frameCount < 0 || frameCount > 1_000_000)
                {
                    throw new InvalidDataException($"ORB 索引数据损坏：帧数异常 ({frameCount})");
                }
                var frames = new List<OrbFrame>(frameCount);
                for (var i = 0; i < frameCount; i++)
                {
                    var ts = reader.ReadDouble();
                    var kpCount = reader.ReadInt32();
                    if (kpCount <= 0 || kpCount > 10000)
                    {
                        throw new InvalidDataException($"ORB 索引数据损坏：关键点数异常 ({kpCount})");
                    }

                    var kps = new Point2f[kpCount];
                    for (var k = 0; k < kpCount; k++)
                    {
                        kps[k] = new Point2f(reader.ReadSingle(), reader.ReadSingle());
                    }

                    frames.Add(new OrbFrame(ts, kps, reader.ReadBytes(kpCount * 32)));
                }

                index[path] = frames;
            }

            OrbIndex = index;
        }
        catch (Exception ex)
        {
            LogManager.Error(ex);
        }
    }

    private async Task WriteIndexAsync()
    {
        try
        {
            // 写盘期间视频索引流水线可能仍在并发 AddRange：与 SearchDeepAsync 同理，
            // 先在 per-key 锁内做快照再序列化，避免枚举 List<OrbFrame> 时抛 InvalidOperationException 导致本轮落盘整体失败
            var snapshot = OrbIndex.ToArray()
                .Select(e =>
                {
                    var lockObj = _orbLocks.GetOrAdd(e.Key, _ => new object());
                    OrbFrame[] frames;
                    lock (lockObj) { frames = e.Value.ToArray(); }
                    return (e.Key, Frames: frames);
                })
                .ToArray();

            // 先写临时文件再原子替换，避免写入中断导致索引损坏；同时消除 MemoryStream 整段拷贝
            var tmp = _orbPath + ".tmp";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await using (var writer = new BinaryWriter(fs, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(Magic);
                    foreach (var (path, frames) in snapshot)
                    {
                        writer.Write(path);
                        writer.Write(frames.Length);
                        foreach (var frame in frames)
                        {
                            writer.Write(frame.Timestamp);
                            writer.Write(frame.Keypoints.Length);
                            foreach (var kp in frame.Keypoints)
                            {
                                writer.Write(kp.X);
                                writer.Write(kp.Y);
                            }

                            writer.Write(frame.Descriptors);
                        }
                    }

                    writer.Flush();
                }

                fs.Flush(true);
            }

            File.Move(tmp, _orbPath, true);
        }
        catch (Exception ex)
        {
            LogManager.Error(ex);
        }
    }

    public override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 先停止后台写入任务，避免与手动刷新竞争
            _cancellationTokenSource?.Cancel();
            try
            {
                _writeTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) { }
            catch (AggregateException) { }

            // 后台任务停止后，把待写入的索引同步落盘
            try
            {
                if (_writeQueue.TryDequeue(out _))
                {
                    _writeQueue.Clear();
                    WriteIndexAsync().Wait();
                }
            }
            catch (Exception e)
            {
                LogManager.Error(e);
            }

            _cancellationTokenSource?.Dispose();

            // 清理事件订阅，避免 disposed 后仍被引用
            IndexUpdated = null;

            // 清理数据
            OrbIndex.Clear();

            // 注：Masuit.Tools.Systems.Disposable 的 Dispose(bool) 为抽象方法，无基类实现可调用
        }
    }
}
