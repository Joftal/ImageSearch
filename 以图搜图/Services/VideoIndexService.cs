using Masuit.Tools;
using Masuit.Tools.Logging;
using Masuit.Tools.Media;
using Masuit.Tools.Systems;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using 以图搜图.Models;

namespace 以图搜图.Services;

/// <summary>
/// 视频帧索引服务：对外挂 ffmpeg 抽取的采样帧计算三种全局哈希，
/// 持久化到 video_index.json（模式与 ImageIndexService 一致：常驻流 + 写队列合并落盘）。
/// </summary>
public sealed class VideoIndexService : Disposable
{
    private readonly ConcurrentHashQueue<int> _writeQueue = new();
    private readonly string _videoIndexPath = DataPath.Get("video_index.json");
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly Task? _writeTask;

    public static VideoIndexService Instance { get; } = new VideoIndexService();

    private VideoIndexService()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _writeTask = StartWriteTaskAsync(_cancellationTokenSource.Token);
    }

    public ConcurrentDictionary<string, VideoIndexItem> VideoIndex { get; private set; } = new();

    private volatile bool _isIndexing;
    private volatile bool _isWriting;
    public bool IsIndexing { get => _isIndexing; private set => _isIndexing = value; }
    public bool IsWriting { get => _isWriting; private set => _isWriting = value; }

    public event EventHandler<IndexProgressEventArgs>? ProgressChanged;

    public event EventHandler<IndexCompletedEventArgs>? IndexCompleted;

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

    public async Task LoadIndexAsync()
    {
        try
        {
            if (File.Exists(_videoIndexPath) && new FileInfo(_videoIndexPath).Length > 0)
            {
                await using var fs = File.OpenRead(_videoIndexPath);
                var set = await JsonSerializer.DeserializeAsync<HashSet<VideoIndexItem>>(fs);
                if (set != null)
                {
                    VideoIndex = set.ToConcurrentDictionary(x => x.FilePath);
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(ex);
        }
    }

    /// <summary>
    /// 对视频文件列表建/增量建索引（已索引的自动跳过）。
    /// 逐个视频处理：ffmpeg 内部已多线程解码，逐视频串行即可吃满 CPU。
    /// </summary>
    public async Task UpdateIndexAsync(string[] videoFiles, CancellationToken cancellationToken = default)
    {
        if (!VideoFrameExtractor.IsAvailable)
        {
            LogManager.Error(new Exception("未找到 tools/ffmpeg.exe 或 tools/ffprobe.exe，视频索引不可用"));
            IndexCompleted?.Invoke(this, new IndexCompletedEventArgs { Errors = new List<string> { "缺少 ffmpeg/ffprobe" } });
            return;
        }

        var filesToIndex = videoFiles.Where(File.Exists).Except(VideoIndex.Keys).Order().ToArray();

        // 哈希索引存在但 ORB 特征缺失的视频：可能因旧版本索引、索引损坏、或视频文件被修改。
        // 为安全起见，移除旧哈希索引，将这些视频视为全新文件重新索引（哈希+ORB 一起重建），
        // 避免哈希与 ORB 基于不同内容导致搜索结果矛盾。
        var orbMissing = videoFiles.Where(File.Exists).Where(f => VideoIndex.ContainsKey(f) && !OrbFeatureService.Instance.OrbIndex.ContainsKey(f)).Order().ToArray();
        foreach (var f in orbMissing)
        {
            VideoIndex.TryRemove(f, out _);
            OrbFeatureService.Instance.RemoveFromIndex(f);
        }
        // 重新加入待索引列表
        filesToIndex = filesToIndex.Concat(orbMissing).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (orbMissing.Length > 0)
        {
            OnProgressChanged(new IndexProgressEventArgs { Message = $"检测到 {orbMissing.Length} 个视频缺少 ORB 特征索引，正在重建完整索引..." });
        }

        if (filesToIndex.Length == 0)
        {
            IndexCompleted?.Invoke(this, new IndexCompletedEventArgs());
            return;
        }

        IsIndexing = true;
        try
        {
        var errors = new List<string>();
        var sw = Stopwatch.StartNew();
        var totalFrames = 0;

        OnProgressChanged(new IndexProgressEventArgs { Message = "正在分析视频文件..." });

        // 先并行探测全部视频的分辨率与时长。进度单位统一为”视频秒”（fps=1 抽帧，帧时间戳≈已处理秒数），
        // 分子分母同单位，进度条才与视频处理实际进度对应
        var probed = new ConcurrentDictionary<string, (int width, int height, double duration)>();

        // 必须限制并发：Task.WhenAll 直接展开会为每个视频同步启动一个 ffprobe.exe，
        // 上千新视频即进程风暴（内存/句柄爆炸）。探测很轻，ProcessorCount/2 足够跑满
        var probeGate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
        await Task.WhenAll(filesToIndex.Select(async file =>
        {
            await probeGate.WaitAsync(cancellationToken);
            try
            {
                // 已请求停止时不再发起新探测，让停止尽快生效
                if (!IsIndexing || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var info = await VideoFrameExtractor.ProbeAsync(file, cancellationToken);
                if (info != null)
                {
                    probed[file] = info.Value;
                }
            }
            catch (OperationCanceledException)
            {
                // 预留防御：当前没有任何调用方传真实 CancellationToken，此路径实际不可达。
                // 若未来传入可取消 token，取消必须向上抛而不能记为"探测失败"——
                // 此时整条索引随取消中止，OnIndexCompleted 不发送属预期行为
                throw;
            }
            catch
            {
                // 探测失败的文件按索引失败处理
            }
            finally
            {
                probeGate.Release();
            }
        }));

        // 用户主动停止时未探测的文件是"被取消"而非"出错"，不应进错误列表
        if (IsIndexing && !cancellationToken.IsCancellationRequested)
        {
            errors.AddRange(filesToIndex.Except(probed.Keys));
        }
        var filesToProcess = filesToIndex.Where(probed.ContainsKey).ToArray();
        var totalSeconds = filesToProcess.Sum(f => probed[f].duration);
        var totalProgress = Math.Max((int)Math.Ceiling(totalSeconds), 1);
        long processedSecondsMs = 0; // 毫秒，Interlocked.Add 不支持 double
        var processedBytes = 0L;

        // 多视频并行索引：ffmpeg 解码吃 1~2 核，ORB 提取吃多核，2~3 路并行能吃满 CPU
        var parallelism = Math.Clamp(Environment.ProcessorCount / 4, 2, 3);

        await Task.Run(() =>
        {
            Parallel.ForEach(filesToProcess, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, file =>
            {
                if (!IsIndexing || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                VideoIndexItem? item = null;
                List<(double Timestamp, SKBitmap Bitmap)>? pendingBatch = null;
                try
                {
                    var (width, height, duration) = probed[file];
                    var fileSize = new FileInfo(file).Length;
                    item = new VideoIndexItem(file) { Duration = duration };
                    // 进度采用增量累计：本工人自己记住已上报到的时间戳，每帧只加增量。
                    // 多视频并行时全局计数器单调递增，进度条不会因各路交替上报自己位置而回跳
                    var lastTs = 0.0;

                    // 批次流水线：枚举线程算哈希并分批，ORB 消费 Task 并行提取，抽帧与 ORB 重叠执行
                    const int batchSize = 64;
                    var batches = new BlockingCollection<List<(double Timestamp, SKBitmap Bitmap)>>(2);
                    pendingBatch = new List<(double Timestamp, SKBitmap Bitmap)>(batchSize);

                    var consumer = Task.Run(() =>
                    {
                        try
                        {
                            foreach (var batch in batches.GetConsumingEnumerable(cancellationToken))
                            {
                                OrbFeatureService.Instance.AddFramesParallel(file, batch);
                                foreach (var (_, bitmap) in batch)
                                {
                                    bitmap.Dispose();
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // 取消：剩余批由 finally 兜底释放
                        }
                    }, CancellationToken.None);

                    try
                    {
                        foreach (var frame in VideoFrameExtractor.ExtractFrames(file, width, height, cancellationToken))
                        {
                            item.DifferenceHash.Add(frame.DiffHash);
                            item.DctHash.Add(frame.Bitmap.DctHash());
                            item.DctHash64.Add(frame.Bitmap.DctHash64());
                            item.Timestamps.Add(frame.Timestamp);

                            pendingBatch.Add((frame.Timestamp, frame.Bitmap));
                            Interlocked.Increment(ref totalFrames);

                            var clampedTs = duration > 0 ? Math.Min(frame.Timestamp, duration) : frame.Timestamp;
                            var deltaMs = (long)Math.Max(0, (clampedTs - lastTs) * 1000);
                            lastTs = clampedTs;
                            Interlocked.Add(ref processedSecondsMs, deltaMs);
                            if (duration > 0)
                            {
                                Interlocked.Add(ref processedBytes, (long)(fileSize * (deltaMs / 1000.0) / duration));
                            }

                            var currentSeconds = Volatile.Read(ref processedSecondsMs) / 1000.0;
                            var currentBytes = Volatile.Read(ref processedBytes);
                            OnProgressChanged(new IndexProgressEventArgs
                            {
                                Filename = file,
                                Message = $"视频: {Path.GetFileName(file)} ({(duration > 0 ? frame.Timestamp / duration : 0):P0})",
                                Speed = currentSeconds / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                                ThroughputMB = currentBytes / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                                ProcessedFiles = Math.Min((int)currentSeconds, totalProgress),
                                TotalFiles = totalProgress
                            });

                            if (pendingBatch.Count >= batchSize)
                            {
                                batches.Add(pendingBatch, cancellationToken);
                                pendingBatch = new List<(double Timestamp, SKBitmap Bitmap)>(batchSize);
                            }

                            if (!IsIndexing || cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        // 提交尾批并通知结束；取消时不再提交，剩余帧由 finally 兜底释放
                        if (pendingBatch.Count > 0 && !cancellationToken.IsCancellationRequested)
                        {
                            batches.Add(pendingBatch, cancellationToken);
                            pendingBatch = null;
                        }

                        batches.CompleteAdding();
                        try
                        {
                            // CompleteAdding 后消费者只剩已入队批次的有界工作量，不限时等待其退干净。
                            // 不能用超时兜底：超时后 Dispose 会与未退出的消费者竞争（ObjectDisposedException
                            // 逃逸其 catch），且已取出的帧位图失去确定性释放
                            consumer.Wait();
                        }
                        catch
                        {
                            // 消费者异常不影响主流程，半成品索引由外层丢弃
                        }
                        batches.Dispose();
                    }

                    // 被中断时不入库，下次从头重索引该视频，避免半成品索引
                    if (IsIndexing && !cancellationToken.IsCancellationRequested && item.Timestamps.Count > 0)
                    {
                        VideoIndex[file] = item;
                        _writeQueue.Enqueue(1);
                        OrbFeatureService.Instance.Flush();
                        // 只补记尾部差额：帧增量已逐帧累计，最后保留帧到视频结尾（去重尾段）的部分在这里补齐
                        if (duration > lastTs)
                        {
                            Interlocked.Add(ref processedSecondsMs, (long)((duration - lastTs) * 1000));
                            if (duration > 0)
                            {
                                Interlocked.Add(ref processedBytes, (long)(fileSize * (duration - lastTs) / duration));
                            }
                        }
                    }
                    else if (item.Timestamps.Count > 0)
                    {
                        // 中断：丢弃该视频的 ORB 半成品，保持与哈希索引一致
                        OrbFeatureService.Instance.RemoveFromIndex(file);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 中断：丢弃该视频的 ORB 半成品，保持与哈希索引一致
                    if (item is { Timestamps.Count: > 0 })
                    {
                        OrbFeatureService.Instance.RemoveFromIndex(file);
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Error(new Exception($"视频索引失败 {file}: {ex.Message}", ex));
                    lock (errors)
                    {
                        errors.Add(file);
                    }
                }
                finally
                {
                    // 异常安全：确保未提交批的帧位图被释放（正常路径已 Dispose，这里是兜底）
                    if (pendingBatch != null)
                    {
                        foreach (var (_, bitmap) in pendingBatch)
                        {
                            try { bitmap.Dispose(); } catch { }
                        }
                    }
                }
            });
        }, CancellationToken.None).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                LogManager.Error(t.Exception);
            }
        });

        sw.Stop();
        OnIndexCompleted(new IndexCompletedEventArgs
        {
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
            FilesProcessed = totalFrames,
            Errors = errors
        });
        }
        finally
        {
            IsIndexing = false;
        }
    }

    public void StopIndexing()
    {
        IsIndexing = false;
    }

    public void RemoveFromIndex(string path)
    {
        if (VideoIndex.TryRemove(path, out _))
        {
            _writeQueue.Enqueue(1);
        }

        OrbFeatureService.Instance.RemoveFromIndex(path);
    }

    /// <summary>清空全部视频帧索引（内存 + 落盘），同时清除 ORB 特征索引以保持一致性。</summary>
    public void ClearIndex()
    {
        VideoIndex.Clear();
        OrbFeatureService.Instance.ClearIndex();
        _writeQueue.Clear();
        _writeQueue.Enqueue(1);
    }

    /// <summary>清理文件已不存在的视频索引，返回是否有变更。</summary>
    public void RemoveInvalidIndexes(IEnumerable<string> existingVideoFiles)
    {
        var existing = existingVideoFiles as ICollection<string> ?? existingVideoFiles.ToArray();
        // 防御：输入列表为空但索引非空时，说明上游目录枚举失败（而非用户删光了视频），拒绝清理以免整库误删
        if (existing.Count == 0 && VideoIndex.Count > 0)
        {
            LogManager.Error(new Exception("移除无效视频索引的输入列表为空，疑似目录枚举失败，已跳过清理以避免误删整个视频索引库"));
            return;
        }

        var removed = false;
        // 仅移除「确认已删除」的索引；盘脱机/网络盘掉线/父目录无权限时保留
        foreach (var key in VideoIndex.Keys.Except(existing, StringComparer.OrdinalIgnoreCase).Where(PathReachability.FileConfirmedDeleted).ToList())
        {
            VideoIndex.TryRemove(key, out _);
            removed = true;
        }

        OrbFeatureService.Instance.RemoveInvalidIndexes(existing);
        if (removed)
        {
            _writeQueue.Enqueue(1);
        }
    }

    public IEnumerable<string> GetIndexedPaths() => VideoIndex.Keys;

    private async Task WriteIndexAsync()
    {
        IsWriting = true;
        try
        {
            // 先写临时文件再原子替换，避免写入中断导致索引损坏
            var tmp = _videoIndexPath + ".tmp";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, VideoIndex.Values).ConfigureAwait(false);
                fs.Flush(true);
            }

            File.Move(tmp, _videoIndexPath, true);
        }
        catch (Exception ex)
        {
            LogManager.Error(ex);
        }
        finally
        {
            IsWriting = false;
        }
    }

    private void OnProgressChanged(IndexProgressEventArgs e) => ProgressChanged?.Invoke(this, e);

    private void OnIndexCompleted(IndexCompletedEventArgs e) => IndexCompleted?.Invoke(this, e);

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
            ProgressChanged = null;
            IndexCompleted = null;
            IndexUpdated = null;

            // 清理数据
            VideoIndex.Clear();

            // 注：Masuit.Tools.Systems.Disposable 的 Dispose(bool) 为抽象方法，无基类实现可调用
        }
    }
}
