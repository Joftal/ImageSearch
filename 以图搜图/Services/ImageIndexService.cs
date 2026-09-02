using Masuit.Tools;
using Masuit.Tools.Hardware;
using Masuit.Tools.Logging;
using Masuit.Tools.Media;
using Masuit.Tools.Systems;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace 以图搜图.Services;

public sealed class ImageIndexService : Disposable
{
    private readonly ConcurrentHashQueue<int> _writeQueue = new();
    private readonly string _indexPath = DataPath.Get("index.json");
    private readonly string _frameIndexPath = DataPath.Get("frame_index.json");
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly Task? _writeTask;
    private static readonly ConcurrentDictionary<char, (string type, string index)> DriveType = new(new Dictionary<char, (string, string)> { ['\\'] = ("HDD", "Unknown"), ['/'] = ("HDD", "Unknown") });
    private static readonly Task _driveDetectionTask;
    public static ImageIndexService Instance { get; }

    static ImageIndexService()
    {
        // WMI 查询可能耗时，移到后台线程避免阻塞 UI 启动
        _driveDetectionTask = Task.Run(() =>
        {
            foreach (var drive in "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Where(drive => Directory.Exists(drive + ":")))
            {
                DriveType[drive] = GetDriveMediaType(drive);
            }
        });

        Instance = new ImageIndexService();
    }

    private ImageIndexService()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _writeTask = StartWriteTaskAsync(_cancellationTokenSource.Token);
    }

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

    public ConcurrentDictionary<string, IndexItem> Index { get; private set; } = new();
    public ConcurrentDictionary<string, FrameIndexItem> FrameIndex { get; private set; } = new();

    private volatile bool _isIndexing;
    private volatile bool _isWriting;
    public bool IsIndexing { get => _isIndexing; private set => _isIndexing = value; }
    public bool IsWriting { get => _isWriting; private set => _isWriting = value; }

    public event EventHandler<IndexProgressEventArgs>? ProgressChanged;

    public event EventHandler<IndexCompletedEventArgs>? IndexCompleted;

    public event EventHandler? IndexUpdated;

    public async Task LoadIndexAsync()
    {
        try
        {
            if (File.Exists(_indexPath) && new FileInfo(_indexPath).Length > 0)
            {
                await using var fs = File.OpenRead(_indexPath);
                var set = await JsonSerializer.DeserializeAsync<HashSet<IndexItem>>(fs);
                if (set != null)
                {
                    Index = set.ToConcurrentDictionary(x => x.FilePath);
                }
            }

            if (File.Exists(_frameIndexPath) && new FileInfo(_frameIndexPath).Length > 0)
            {
                await using var fs = File.OpenRead(_frameIndexPath);
                var set = await JsonSerializer.DeserializeAsync<HashSet<FrameIndexItem>>(fs);
                if (set != null)
                {
                    FrameIndex = set.ToConcurrentDictionary(x => x.FilePath);
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(ex);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                var errorDialog = new ErrorsDialog(ex.ToString());
                errorDialog.ShowDialog();
            });
        }
    }

    private int _totalCount;
    private long _totalSize;

    /// <summary>
    /// 更新索引。<paramref name="prefetchedFiles"/> 允许调用方传入已枚举的文件列表，
    /// 避免图片索引与视频索引对同一目录重复全树枚举。
    /// </summary>
    public async Task UpdateIndexAsync(string[] directories, bool removeInvalid, string[]? prefetchedFiles = null)
    {
        var files = prefetchedFiles ?? GetFiles(directories);
        _totalCount = 0;
        _totalSize = 0;
        Interlocked.Exchange(ref _lastProgressMs, 0); // 节流时间戳按轮次复位，避免新一轮索引首拍被吞
        IsIndexing = true;
        try
        {
            if (removeInvalid)
            {
                // 同步执行，在索引构建前完成，避免与索引构建并发操作导致误删
                RemoveInvalidIndexes(directories, files);
            }

            var filesToIndex = files.Except(Index.Keys, StringComparer.OrdinalIgnoreCase).Except(FrameIndex.Keys, StringComparer.OrdinalIgnoreCase).Where(s => Regex.IsMatch(s, @"\.(gif|jpe?g|png|bmp|webp)$", RegexOptions.IgnoreCase)).Order().ToArray();
            if (filesToIndex.Length == 0)
            {
                OnIndexCompleted(new IndexCompletedEventArgs());
                return;
            }

            var errors = new List<string>();
            var sw = Stopwatch.StartNew();

            var indexTask = Task.Run(() =>
            {
                Parallel.Invoke(() => UpdateIndexOnSSD(filesToIndex, sw, errors), () => UpdateIndexOnHDD(filesToIndex, sw, errors));
                if (_totalCount > 0)
                {
                    _writeQueue.Enqueue(1);
                }
            });
            indexTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    LogManager.Error(t.Exception);
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
            await indexTask;

            sw.Stop();
            OnIndexCompleted(new IndexCompletedEventArgs
            {
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                FilesProcessed = _totalCount,
                Errors = errors
            });
        }
        finally
        {
            IsIndexing = false;
        }
    }

    private void UpdateIndexOnSSD(string[] filesToIndex, Stopwatch sw, List<string> errors)
    {
        var parallelism = Environment.ProcessorCount * 4;
        filesToIndex.Where(s => GetDriveType(s[0]).type != "HDD").Chunk(parallelism).AsParallel().WithDegreeOfParallelism(parallelism).ForAll(g =>
        {
            foreach (var file in g.Where(File.Exists).TakeWhile(_ => IsIndexing))
            {
                try
                {
                    if (file.EndsWith(".gif", StringComparison.CurrentCultureIgnoreCase))
                    {
                        var indexItem = new FrameIndexItem(file);
                        using var frames = new DisposableCollection<SKBitmap>(SkiaImageHelper.DecodeGrayFrames(file, 160));
                        // 并行计算哈希但按帧序写入，避免并行 Add 导致顺序错乱/集合损坏
                        var hashes = frames.AsParallel().AsOrdered().Select(frame => (frame.DifferenceHash256(), frame.DctHash(), frame.DctHash64())).ToArray();
                        foreach (var (diff, dct, dct64) in hashes)
                        {
                            indexItem.DifferenceHash.Add(diff);
                            indexItem.DctHash.Add(dct);
                            indexItem.DctHash64.Add(dct64);
                        }

                        FrameIndex[file] = indexItem;
                    }
                    else
                    {
                        using var image = SkiaImageHelper.DecodeGrayThumb(file, 160);
                        var indexItem = new IndexItem(file)
                        {
                            DctHash = image.DctHash(),
                            DifferenceHash = image.DifferenceHash256(),
                            DctHash64 = image.DctHash64()
                        };
                        Index[file] = indexItem;
                    }

                    var size = new FileInfo(file).Length;
                    Interlocked.Increment(ref _totalCount);
                    Interlocked.Add(ref _totalSize, size);

                    OnProgressChanged(new IndexProgressEventArgs
                    {
                        Filename = file,
                        Message = $"{_totalCount}/{filesToIndex.Length}",
                        Speed = _totalCount / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                        ThroughputMB = _totalSize / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                        ProcessedFiles = _totalCount,
                        TotalFiles = filesToIndex.Length
                    });
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(file);
                    }

                    // 与 HDD 分支保持一致：单文件失败原因（解码失败等）也要进日志
                    LogManager.Error(ex);
                }
            }
        });
    }

    private void UpdateIndexOnHDD(string[] filesToIndex, Stopwatch sw, List<string> errors)
    {
        var queue = new ConcurrentQueue<(string Path, MemoryStream? Stream, long Length)>();
        long queuedBytes = 0;
        int loading = 1; // 用 int + Interlocked/Volatile 代替 bool，确保跨线程可见性
        Task.Run(() =>
        {
            // 保持块内既有缩进以减少 diff：生产者任何异常逃逸（WMI 失败、索引期间文件被删除等）
            // 都必须复位 loading，否则消费者永久空转——索引不能完成、IsIndexing 无法复位、窗口无法关闭
            try
            {
            var memoryAvailable = Math.Min(RamInfo.Local.MemoryAvailable / 2, 8589934592d);
            var diskCount = DriveType.Values.Where(t => t.type == "HDD").Select(t => t.index).Distinct().Count();
            switch (diskCount)
            {
                case 1:
                    foreach (var file in filesToIndex.Where(s => GetDriveType(s[0]).type == "HDD").Order().TakeWhile(_ => IsIndexing).Where(File.Exists))
                    {
                        if (file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                        {
                            // GIF 按帧解码，预读字节无意义且浪费内存，消费端直接从磁盘解码
                            // 文件可能在 Where(File.Exists) 之后被删除/锁定，长度读取必须容错，否则异常会逃逸出生产者
                            try
                            {
                                queue.Enqueue((file, null, new FileInfo(file).Length));
                            }
                            catch
                            {
                                lock (errors)
                                {
                                    errors.Add(file);
                                }
                            }
                            continue;
                        }

                        MemoryStream? stream = null;
                        try
                        {
                            stream = new MemoryStream(File.ReadAllBytes(file));
                            var length = stream.Length;
                            queue.Enqueue((file, stream, length));
                            stream = null; // 入队成功，所有权转移给消费者，不再由本处释放
                            Interlocked.Add(ref queuedBytes, length);
                            while (Volatile.Read(ref queuedBytes) > memoryAvailable && IsIndexing)
                            {
                                Thread.Sleep(200);
                            }
                        }
                        catch
                        {
                            stream?.Dispose(); // 入队失败或读取异常时释放流
                            lock (errors)
                            {
                                errors.Add(file);
                            }
                        }
                    }

                    break;

                case > 1:
                    filesToIndex.Where(s => GetDriveType(s[0]).type == "HDD").GroupBy(s => GetDriveType(s[0]).index).AsParallel().WithDegreeOfParallelism(diskCount).ForAll(grouping =>
                    {
                        foreach (var file in grouping.Order().TakeWhile(_ => IsIndexing).Where(File.Exists))
                        {
                            if (file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                            {
                                // GIF 按帧解码，预读字节无意义且浪费内存，消费端直接从磁盘解码
                                // 文件可能在 Where(File.Exists) 之后被删除/锁定，长度读取必须容错，否则异常会逃逸出生产者
                                try
                                {
                                    queue.Enqueue((file, null, new FileInfo(file).Length));
                                }
                                catch
                                {
                                    lock (errors)
                                    {
                                        errors.Add(file);
                                    }
                                }
                                continue;
                            }

                            MemoryStream? stream = null;
                            try
                            {
                                stream = new MemoryStream(File.ReadAllBytes(file));
                                var length = stream.Length;
                                queue.Enqueue((file, stream, length));
                                stream = null; // 入队成功，所有权转移给消费者
                                Interlocked.Add(ref queuedBytes, length);
                                while (Volatile.Read(ref queuedBytes) > memoryAvailable && IsIndexing)
                                {
                                    Thread.Sleep(200);
                                }
                            }
                            catch
                            {
                                stream?.Dispose();
                                lock (errors)
                                {
                                    errors.Add(file);
                                }
                            }
                        }
                    });
                    break;
            }

            }
            finally
            {
                Interlocked.Exchange(ref loading, 0);
            }
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                LogManager.Error(t.Exception);
            }
        });
        while (Volatile.Read(ref loading) == 1 || queue.Count > 0)
        {
            if (queue.Count == 0)
            {
                Thread.Sleep(20); // 生产者尚未入队时避免忙等
                continue;
            }

            Parallel.For(0, Math.Min(Environment.ProcessorCount * 4, queue.Count), _ =>
            {
                if (!queue.TryDequeue(out var item))
                {
                    return;
                }

                Interlocked.Add(ref queuedBytes, -item.Length);

                if (!IsIndexing)
                {
                    // 停止请求：队列余量直接丢弃（下次索引时未入库文件会增量补齐），
                    // 不再逐个解码，否则机械盘大预读（可达数 GB）会让"停止"等几分钟才生效
                    item.Stream?.Dispose();
                    return;
                }

                try
                {
                    if (item.Path.EndsWith(".gif", StringComparison.CurrentCultureIgnoreCase))
                    {
                        var indexItem = new FrameIndexItem(item.Path);
                        using var frames = new DisposableCollection<SKBitmap>(SkiaImageHelper.DecodeGrayFrames(item.Path, 160));
                        // 并行计算哈希但按帧序写入，避免并行 Add 导致顺序错乱/集合损坏
                        var hashes = frames.AsParallel().AsOrdered().Select(frame => (frame.DifferenceHash256(), frame.DctHash(), frame.DctHash64())).ToArray();
                        foreach (var (diff, dct, dct64) in hashes)
                        {
                            indexItem.DifferenceHash.Add(diff);
                            indexItem.DctHash.Add(dct);
                            indexItem.DctHash64.Add(dct64);
                        }

                        FrameIndex[item.Path] = indexItem;
                    }
                    else
                    {
                        using var image = SkiaImageHelper.DecodeGrayThumb(item.Stream!, 160);
                        var indexItem = new IndexItem(item.Path)
                        {
                            DctHash = image.DctHash(),
                            DifferenceHash = image.DifferenceHash256(),
                            DctHash64 = image.DctHash64()
                        };
                        Index[item.Path] = indexItem;
                    }

                    Interlocked.Increment(ref _totalCount);
                    Interlocked.Add(ref _totalSize, item.Length);
                    OnProgressChanged(new IndexProgressEventArgs
                    {
                        Filename = item.Path,
                        Message = $"{_totalCount}/{filesToIndex.Length}",
                        Speed = _totalCount / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                        ThroughputMB = _totalSize / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                        ProcessedFiles = _totalCount,
                        TotalFiles = filesToIndex.Length
                    });
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(item.Path);
                    }

                    LogManager.Error(ex);
                }
                finally
                {
                    item.Stream?.Dispose();
                }
            });
        }
    }

    public void StopIndexing()
    {
        IsIndexing = false;
    }

    public void RemoveFromIndex(string path)
    {
        Index.TryRemove(path, out _);
        FrameIndex.TryRemove(path, out _);
        _writeQueue.Enqueue(1);
    }

    /// <summary>清空全部图片/GIF 帧索引（内存 + 落盘）。</summary>
    public void ClearIndex()
    {
        Index.Clear();
        FrameIndex.Clear();
        _writeQueue.Clear();
        _writeQueue.Enqueue(1);
    }

    public IEnumerable<string> GetIndexedPaths()
    {
        return Index.Keys.Union(FrameIndex.Keys);
    }

    /// <summary>Everything 查询的扩展名白名单：图片 + 视频，与调用方（图片哈希索引、视频帧索引）的消费范围对齐。</summary>
    private static readonly string EverythingExtFilter = "jpg;jpeg;bmp;png;gif;webp;" + string.Join(";", VideoFrameExtractor.VideoExtensions);

    public static string[] GetFiles(string[] directories)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Everything64.dll")) && Process.GetProcessesByName("Everything").Length > 0)
        {
            return directories.SelectMany(s =>
            {
                try
                {
                    // 过滤必须包含视频扩展名：GetFiles 的结果同时供视频索引使用，
                    // 只返回图片会导致视频索引失效，且"移除无效索引"会把已有视频索引误判清空
                    var array = EverythingHelper.EnumerateFiles(s, EverythingExtFilter).ToArray();
                    return array.Length == 0 ? EnumerateFilesIgnoreInaccessible(s) : array;
                }
                catch
                {
                    return Array.Empty<string>();
                }
            }).ToArray();
        }

        return directories.SelectMany(static s =>
        {
            try
            {
                return EnumerateFilesIgnoreInaccessible(s);
            }
            catch
            {
                return [];
            }
        }).ToArray();
    }

    /// <summary>递归枚举目录下全部文件，跳过无权限子目录（而非让整个目录树枚举报废）。AttributesToSkip=0 以保持与旧 Directory.GetFiles 一致（包含隐藏/系统文件）。</summary>
    private static string[] EnumerateFilesIgnoreInaccessible(string directory) =>
        Directory.EnumerateFiles(directory, "*", new System.IO.EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        }).ToArray();

    private void RemoveInvalidIndexes(string[] newDirs, string[] allFiles)
    {
        var allPaths = allFiles;

        if (newDirs.Length > 0)
        {
            var allDirs = PathPrefixFinder.FindLongestCommonPathPrefixes(Index.Keys.Union(FrameIndex.Keys), 3).Where(Directory.Exists);
            var combinedFiles = GetFiles(allDirs.Union(newDirs).ToArray());
            allPaths = combinedFiles;
        }

        var removed = false;
        // 仅移除「确认已删除」的索引：盘脱机/网络盘掉线/父目录无权限时宁可保留，
        // 不可因一次性枚举不到而误删（重建需数小时）
        var removes = Index.Keys.Except(allPaths, StringComparer.OrdinalIgnoreCase).Where(PathReachability.FileConfirmedDeleted).ToList();
        foreach (var key in removes)
        {
            Index.TryRemove(key, out _);
            removed = true;
        }

        removes = FrameIndex.Keys.Except(allPaths, StringComparer.OrdinalIgnoreCase).Where(PathReachability.FileConfirmedDeleted).ToList();
        foreach (var key in removes)
        {
            FrameIndex.TryRemove(key, out _);
            removed = true;
        }

        if (removed)
        {
            _writeQueue.Enqueue(1);
        }
    }

    private async Task WriteIndexAsync()
    {
        IsWriting = true;
        try
        {
            // 先写临时文件再原子替换，避免写入中断导致索引损坏
            var indexTmp = _indexPath + ".tmp";
            await using (var fs = new FileStream(indexTmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, Index.Values).ConfigureAwait(false);
                fs.Flush(true);
            }

            File.Move(indexTmp, _indexPath, true);

            var frameTmp = _frameIndexPath + ".tmp";
            await using (var fs = new FileStream(frameTmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, FrameIndex.Values).ConfigureAwait(false);
                fs.Flush(true);
            }

            File.Move(frameTmp, _frameIndexPath, true);
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

    private static (string type, string index) GetDriveMediaType(char driveLetter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_LogicalDisk WHERE DeviceID = '{driveLetter}:'");
            foreach (ManagementObject logicalDisk in searcher.Get())
            {
                // 获取关联的物理磁盘
                foreach (ManagementObject partition in logicalDisk.GetRelated("Win32_DiskPartition"))
                {
                    foreach (ManagementObject diskDrive in partition.GetRelated("Win32_DiskDrive"))
                    {
                        string model = diskDrive["Model"]?.ToString() ?? "Unknown";
                        string mediaType = diskDrive["MediaType"]?.ToString() ?? "Unknown";
                        string interfaceType = diskDrive["InterfaceType"]?.ToString() ?? "Unknown";
                        string diskIndex = diskDrive["Index"]?.ToString() ?? "Unknown";

                        // 判断逻辑
                        if (model.Contains("SSD", StringComparison.CurrentCultureIgnoreCase) || mediaType.Contains("SSD"))
                            return ("SSD", diskIndex);

                        if (mediaType.Contains("Fixed") && !model.Contains("SSD", StringComparison.CurrentCultureIgnoreCase))
                            return ("HDD", diskIndex);

                        if (interfaceType == "USB")
                            return ("USB", diskIndex);

                        return ("Unknown", diskIndex);
                    }
                }
            }
        }
        catch (Exception)
        {
            return ("Unknown", "Unknown");
        }

        return ("Unknown", "Unknown");
    }

    private static (string type, string index) GetDriveType(char driveLetter)
    {
        _driveDetectionTask.Wait(TimeSpan.FromSeconds(10));
        return DriveType.TryGetValue(driveLetter, out var info) ? info : ("SSD", "Unknown");
    }

    private long _lastProgressMs;

    private void OnProgressChanged(IndexProgressEventArgs e)
    {
        if (e.ProcessedFiles % 1000 == 0)
        {
            _writeQueue.Enqueue(1);
        }

        // 进度节流：最多每 100ms 投递一次事件。高吞吐时（数千文件/秒）逐文件投递会让
        // VM 端 Dispatcher.BeginInvoke 洪泛 UI 线程导致卡顿；最后一个文件的事件不丢
        var now = Environment.TickCount64;
        if (e.ProcessedFiles < e.TotalFiles && now - Interlocked.Read(ref _lastProgressMs) < 100)
        {
            return;
        }

        Interlocked.Exchange(ref _lastProgressMs, now);
        ProgressChanged?.Invoke(this, e);
    }

    private void OnIndexCompleted(IndexCompletedEventArgs e)
    {
        IndexCompleted?.Invoke(this, e);
    }

    /// <summary>释放</summary>
    /// <param name="disposing"></param>
    public override void Dispose(bool disposing)
    {
        // 与 VideoIndexService/OrbFeatureService 对齐：重量级清理只允许在显式 Dispose 时执行，
        // 禁止终结器线程（若基类以 Dispose(false) 回调）走到这里——等待任务/同步落盘会拖死进程退出
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

            // 清理事件订阅
            ProgressChanged = null;
            IndexCompleted = null;
            IndexUpdated = null;

            // 清理数据
            FrameIndex?.Clear();
            Index?.Clear();

            // 清理令牌源
            _cancellationTokenSource?.Dispose();
            // 注：Masuit.Tools.Systems.Disposable 的 Dispose(bool) 为抽象方法，无基类实现可调用
        }
    }
}

public record IndexItem(string FilePath)
{
    public ulong[] DifferenceHash { get; set; } = [];
    public ulong DctHash { get; set; }
    public ulong DctHash64 { get; set; }
}

public sealed record FrameIndexItem(string FilePath)
{
    public List<ulong[]> DifferenceHash { get; set; } = new List<ulong[]>();
    public List<ulong> DctHash { get; set; } = new List<ulong>();
    public List<ulong> DctHash64 { get; set; } = new List<ulong>();
}