using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Masuit.Tools.Files;
using Masuit.Tools.Files.FileDetector;
using Masuit.Tools.Logging;
using Masuit.Tools.Media;
using Masuit.Tools.Systems;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using 以图搜图.Models;
using 以图搜图.Services;
using 以图搜图.WebAPI;
using 以图搜图.WebAPI.Controllers;
using ModelsMatchAlgorithm = 以图搜图.Models.MatchAlgorithm;
using Timer = System.Timers.Timer;

namespace 以图搜图.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ImageIndexService _indexService;
    private readonly ImageSearchService _searchService;
    private readonly VideoIndexService _videoIndexService;

    [ObservableProperty]
    private string destImageInfo = string.Empty;

    [ObservableProperty]
    private string destImagePath = string.Empty;

    [ObservableProperty]
    private string directoryPath = string.Empty;

    [ObservableProperty]
    private string elapsedTime = string.Empty;

    [ObservableProperty]
    private bool findFlipped;

    [ObservableProperty]
    private bool findRotated = true;

    [ObservableProperty]
    private string imagePath = string.Empty;

    [ObservableProperty]
    private string indexCount = "正在加载索引...";

    [ObservableProperty]
    private string indexSpeed = string.Empty;

    [ObservableProperty]
    private string processStatus = string.Empty;

    [ObservableProperty]
    private bool removeInvalidIndex;

    [ObservableProperty]
    private ObservableCollection<SearchResult> searchResults = new();

    [ObservableProperty]
    private SearchResult? selectedResult;

    [ObservableProperty]
    private Visibility showRemoveInvalidIndex = Visibility.Collapsed;

    [ObservableProperty]
    private int similarity = 80;

    public ModelsMatchAlgorithm MatchAlgorithm
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                if (Similarity < SimilarityMinimum)
                {
                    Similarity = SimilarityMinimum;
                }

                if (value == MatchAlgorithm.DctHash32)
                {
                    Similarity = 90;
                }

                OnPropertyChanged(nameof(SimilarityMinimum));
            }
        }
    } = ModelsMatchAlgorithm.All;

    public IReadOnlyList<ModelsMatchAlgorithm> MatchAlgorithms { get; } = Enum.GetValues<ModelsMatchAlgorithm>();

    public int SimilarityMinimum => MatchAlgorithm.HasFlag(ModelsMatchAlgorithm.DifferenceHash) ? 70 : 85;

    [ObservableProperty]
    private string sourceImageInfo = string.Empty;

    [ObservableProperty]
    private string sourceImagePath = string.Empty;

    [ObservableProperty]
    private string updateIndexButtonText = "🔄 更新索引";

    [ObservableProperty]
    private bool updateIndexButtonEnabled = true;

    [ObservableProperty]
    private bool isSearching;

    [ObservableProperty]
    private string searchStatusText = string.Empty;

    [ObservableProperty]
    private Visibility searchLoadingVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private double indexProgress;

    [ObservableProperty]
    private string indexProgressText = string.Empty;

    [ObservableProperty]
    private Visibility indexProgressVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string indexSpeedText = string.Empty;

    [ObservableProperty]
    private string indexThroughputText = string.Empty;

    [ObservableProperty]
    private string maxThroughputText = string.Empty;

    [ObservableProperty]
    private string estimatedRemainingTimeText = string.Empty;

    [ObservableProperty]
    private string processingFilename = string.Empty;

    [ObservableProperty]
    private ObservableCollection<double> speedHistory = new();

    [ObservableProperty]
    private bool isSearchEnabled;

    [ObservableProperty]
    private double cpuUsage;

    [ObservableProperty]
    private double memoryUsage;

    [ObservableProperty]
    private string webApiServer;

    [ObservableProperty]
    private bool webApiServerRunning;

    private Process? _currentProcess;
    private TimeSpan _lastTotalProcessorTime;
    private DateTime _lastCpuCheckTime;
    private Timer? _performanceTimer;
    private Timer? _updateIndexTimer;
    private readonly IniFile _config = new IniFile(DataPath.Get("config.ini"));
    private string? _previewTempFile;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public MainViewModel()
    {
        _indexService = ImageIndexService.Instance;
        _searchService = new ImageSearchService();
        _videoIndexService = VideoIndexService.Instance;

        _indexService.ProgressChanged += OnIndexProgressChanged;
        _indexService.IndexCompleted += OnIndexCompleted;
        _indexService.IndexUpdated += OnIndexUpdated;
        _videoIndexService.ProgressChanged += OnIndexProgressChanged;
        _videoIndexService.IndexCompleted += OnIndexCompleted;
        _videoIndexService.IndexUpdated += OnIndexUpdated;

        // 异步初始化性能监测，避免阻塞 UI 线程
        _ = Task.Run(InitializePerformanceMonitoring);
        WebApiServerRunning = WebApiStartup.ServerRunning;
        LoadIndexAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                LogManager.Error(t.Exception);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IndexCount = "索引加载失败";
                    ProcessStatus = $"索引加载失败：{t.Exception?.InnerException?.Message}";
                });
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
        HomeController.MainViewModel = this;
        WebApiServer = _config.GetValue("Global", "RunServer", false) ? $"http://127.0.0.1:{_config.GetValue("Global", "HttpPort", 5000)}/api" : "";
        if (_config.GetValue("Global", "IndexAutoUpdate", false))
        {
            _updateIndexTimer = new Timer(TimeSpan.FromHours(1));
            _updateIndexTimer.Elapsed += (sender, args) =>
            {
                if (UpdateIndexCommand.CanExecute(sender) && IndexProgressVisibility != Visibility.Visible)
                {
                    UpdateIndexCommand.Execute(sender);
                }
            };
            _updateIndexTimer.Start();
        }
    }

    partial void OnImagePathChanged(string value)
    {
        if (File.Exists(value))
        {
            SourceImagePath = value;
            UpdateSourceImageInfo(value);
        }
    }

    partial void OnSelectedResultChanged(SearchResult? value)
    {
        if (value != null && File.Exists(value.路径))
        {
            if (value.媒体类型 == "视频" && value.命中时间戳秒数 != null)
            {
                // 视频结果：用 ffmpeg 抽命中帧到临时文件供预览
                DestImagePath = string.Empty;
                DestImageInfo = $"视频：{Path.GetFileName(value.路径)}，命中 {value.时间戳}";
                _ = Task.Run(async () =>
                {
                    var tempFile = DataPath.TempFile(".jpg");
                    if (await VideoFrameExtractor.ExtractFrameImageAsync(value.路径, value.命中时间戳秒数.Value, tempFile))
                    {
                        // 原子替换临时文件引用，删除旧文件
                        var old = Interlocked.Exchange(ref _previewTempFile, tempFile);
                        if (old != null)
                        {
                            try { File.Delete(old); } catch { }
                        }

                        // 仅当 _previewTempFile 仍指向本次提取的文件时才更新 UI，
                        // 避免快速切换选中项时旧任务覆盖新任务的结果
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (_previewTempFile == tempFile)
                                DestImagePath = tempFile;
                        });
                    }
                    else
                    {
                        try { File.Delete(tempFile); } catch { }
                    }
                });
                return;
            }

            if (_previewTempFile != null)
            {
                try { File.Delete(_previewTempFile); } catch { }
                _previewTempFile = null;
            }

            DestImagePath = value.路径;
            UpdateDestImageInfo(value.路径);
        }
    }

    partial void OnSimilarityChanged(int value)
    {
        var minimum = SimilarityMinimum;
        if (value < minimum)
        {
            Similarity = minimum;
        }
    }

    private async Task LoadIndexAsync()
    {
        await _indexService.LoadIndexAsync();
        await _videoIndexService.LoadIndexAsync();
        await OrbFeatureService.Instance.LoadIndexAsync();
        UpdateIndexCount();
    }

    [RelayCommand]
    private void SelectDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "选择文件夹",
            ValidateNames = false
        };

        // Workaround for folder selection
        if (dialog.ShowDialog() == true)
        {
            DirectoryPath = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        }
    }

    [RelayCommand]
    private void SelectImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|所有文件|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            ImagePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void ClearIndex()
    {
        if (_indexService.IsIndexing || _videoIndexService.IsIndexing)
        {
            MessageBox.Show(Application.Current.MainWindow!, "正在索引中，请先停止索引", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(Application.Current.MainWindow!, "确认清除所有索引数据吗？清除后需重新更新索引才能搜索。", "清除索引", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _indexService.ClearIndex();
        _videoIndexService.ClearIndex();
        OrbFeatureService.Instance.ClearIndex();
        UpdateIndexCount();
        ProcessStatus = "索引已清除";
    }

    [RelayCommand]
    private async Task UpdateIndex()
    {
        if (_indexService.IsIndexing || _videoIndexService.IsIndexing)
        {
            _indexService.StopIndexing();
            _videoIndexService.StopIndexing();
            UpdateIndexButtonText = "🔄 更新索引";
            UpdateIndexButtonEnabled = false;
            //MessageBox.Show(Application.Current.MainWindow!, "已发送停止请求，请等待完成...", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 立即更新 UI 显示
        OnIndexProgressChanged(this, new IndexProgressEventArgs
        {
            Message = "准备开始"
        });

        await Task.Run(async () =>
        {
            try
            {
                var paths = _indexService.GetIndexedPaths().ToList();
                if (string.IsNullOrWhiteSpace(DirectoryPath) && paths.Count == 0)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(Application.Current.MainWindow!, "请先选择文件夹", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                        IndexProgressVisibility = Visibility.Collapsed;
                        UpdateIndexButtonText = "🔄 更新索引";
                    });
                    return;
                }

                var dirs = string.IsNullOrWhiteSpace(DirectoryPath) ? PathPrefixFinder.FindLongestCommonPathPrefixes(paths.Union(_videoIndexService.GetIndexedPaths()).ToList(), 3).Where(Directory.Exists).ToArray() : [DirectoryPath];

                // 目录树枚举只做一次：同时供图片索引与视频索引使用（原实现枚举两遍，IO 翻倍）
                var allFiles = ImageIndexService.GetFiles(dirs);

                // 在 UI 线程上执行图片索引（内部会 Task.Run 到后台线程）
                try
                {
                    await _indexService.UpdateIndexAsync(dirs, RemoveInvalidIndex, allFiles);
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() => MessageBox.Show(ex.Message));
                }

                // 图片索引完成后顺序执行视频索引（共享进度条与停止状态）
                var videoFiles = allFiles.Where(IsVideoFile).ToArray();
                if (RemoveInvalidIndex)
                {
                    _videoIndexService.RemoveInvalidIndexes(videoFiles);
                }

                await _videoIndexService.UpdateIndexAsync(videoFiles);
            }
            finally
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    UpdateIndexButtonText = "🔄 更新索引";
                    UpdateIndexButtonEnabled = true;
                    RemoveInvalidIndex = false;
                });
            }
        });
    }

    [RelayCommand]
    private async Task Search()
    {
        if (string.IsNullOrEmpty(ImagePath))
        {
            MessageBox.Show(Application.Current.MainWindow!, "请先选择图片", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!IsSearchEnabled)
        {
            MessageBox.Show(Application.Current.MainWindow!, "当前没有任何索引，请先添加文件夹创建索引后再搜索", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (new FileInfo(ImagePath).DetectFiletype()?.MimeType?.StartsWith("image") != true)
        {
            MessageBox.Show(Application.Current.MainWindow!, "不是图像文件，无法检索", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await SearchCore(ImagePath);
    }

    [RelayCommand]
    private async Task SearchFromClipboard()
    {
        if (!IsSearchEnabled)
        {
            MessageBox.Show(Application.Current.MainWindow!, "当前没有任何索引，请先添加文件夹创建索引后再搜索", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    ImagePath = files[0]!;
                    await Search();
                }

                return;
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (File.Exists(text))
                {
                    ImagePath = text;
                    await Search();
                }

                return;
            }

            if (Clipboard.ContainsImage())
            {
                // 在 UI 线程（STA 模式）获取剪贴板图片，然后在后台线程处理编码
                try
                {
                    var image = Clipboard.GetImage();
                    if (image != null)
                    {
                        // 立即冻结图片对象，使其可以跨线程访问
                        image.Freeze();

                        // 后台处理图片编码和搜索，避免 UI 线程阻塞
                        await Task.Run(async () =>
                        {
                            try
                            {
                                var filename = DataPath.TempFile(".jpg");

                                // 编码图片在后台线程执行
                                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));

                                await using (var fileStream = new FileStream(filename, FileMode.Create))
                                {
                                    encoder.Save(fileStream);
                                }

                                // 切回 UI 线程更新 UI
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    OnImagePathChanged(filename);
                                });

                                // 执行搜索（在后台线程中等待）
                                await SearchCore(filename);

                                // 搜索完成后立即删除临时文件（解码在 SearchCore 内完成，无需延时）
                                try { File.Delete(filename); } catch { }
                            }
                            catch (Exception ex)
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    MessageBox.Show(Application.Current.MainWindow!, $"处理剪贴板图片失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                                    IsSearching = false;
                                    SearchLoadingVisibility = Visibility.Collapsed;
                                    SearchStatusText = string.Empty;
                                });
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Application.Current.MainWindow!, $"读取剪贴板失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    IsSearching = false;
                    SearchLoadingVisibility = Visibility.Collapsed;
                    SearchStatusText = string.Empty;
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 剪贴板被其他进程锁定时可能抛出 COM 异常，静默处理
            IsSearching = false;
            SearchLoadingVisibility = Visibility.Collapsed;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            IsSearching = false;
            SearchLoadingVisibility = Visibility.Collapsed;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedResult != null)
        {
            FileExplorerHelper.ExplorerFile(SelectedResult.路径);
        }
    }

    [RelayCommand]
    private void PlayResult()
    {
        if (SelectedResult == null || !File.Exists(SelectedResult.路径)) return;

        if (SelectedResult.媒体类型 == "视频")
        {
            // 视频：带命中时间戳定位播放（探测不到播放器时内部回退系统默认打开）
            PlayerLauncher.OpenAtPosition(SelectedResult.路径, SelectedResult.命中时间戳秒数);
        }
        else
        {
            Process.Start(new ProcessStartInfo { FileName = SelectedResult.路径, UseShellExecute = true })?.Dispose();
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedResult == null) return;

        var result = MessageBox.Show(Application.Current.MainWindow!, "确认删除选中项吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.OK)
        {
            if (File.Exists(SelectedResult.路径))
            {
                // 删除前释放 Image 控件占用的文件
                if (DestImagePath == SelectedResult.路径)
                {
                    DestImagePath = string.Empty;
                    DestImageInfo = string.Empty;
                }

                try
                {
                    File.Delete(SelectedResult.路径);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 失败时索引与列表均未改动，保持状态一致，仅提示原因
                    MessageBox.Show(Application.Current.MainWindow!, $"删除失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            _indexService.RemoveFromIndex(SelectedResult.路径);
            _videoIndexService.RemoveFromIndex(SelectedResult.路径);
            SearchResults.Remove(SelectedResult);
            UpdateIndexCount();
        }
    }

    [RelayCommand]
    private void DeleteToRecycleBin()
    {
        if (SelectedResult == null) return;

        var result = MessageBox.Show(Application.Current.MainWindow!, "确认删除到回收站吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.OK)
        {
            // 删除前释放 Image 控件占用的文件
            if (DestImagePath == SelectedResult.路径)
            {
                DestImagePath = string.Empty;
                DestImageInfo = string.Empty;
            }

            try
            {
                RecycleBinHelper.Delete(SelectedResult.路径);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                MessageBox.Show(Application.Current.MainWindow!, $"删除到回收站失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            _indexService.RemoveFromIndex(SelectedResult.路径);
            _videoIndexService.RemoveFromIndex(SelectedResult.路径);
            SearchResults.Remove(SelectedResult);
            UpdateIndexCount();
        }
    }

    public async Task HandleDrop(IDataObject dataObject)
    {
        var searchScheduled = false; // 标记是否已调度后台搜索任务
        try
        {
            // 1. 检查文件拖放
            if (dataObject.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])dataObject.GetData(DataFormats.FileDrop)!;
                if (files.Length > 0)
                {
                    ImagePath = files[0];
                    await Search();
                    return;
                }
            }

            // 2. 直接获取位图数据（优先处理，避免格式转换问题）
            if (dataObject.GetDataPresent(DataFormats.Bitmap))
            {
                try
                {
                    var image = (System.Windows.Media.Imaging.BitmapSource)dataObject.GetData(DataFormats.Bitmap)!;
                    // 立即冻结图片对象，使其可以跨线程访问
                    image.Freeze();

                    // 在后台线程处理图片编码，避免 UI 线程阻塞
                    searchScheduled = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var filename = DataPath.TempFile(".jpg");

                            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));

                            await using (var fileStream = new FileStream(filename, FileMode.Create))
                            {
                                encoder.Save(fileStream);
                            }

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                OnImagePathChanged(filename);
                            });

                            await SearchCore(filename);

                            // 搜索完成后立即删除临时文件（解码在 SearchCore 内完成，无需延时）
                            try { File.Delete(filename); } catch { }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"位图处理异常: {ex.Message}");
                        }
                    });
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"位图数据处理失败: {ex.Message}");
                    // 继续尝试其他格式
                }
            }

            // 3. 处理 DIB (Device Independent Bitmap) 格式
            if (dataObject.GetDataPresent(DataFormats.Dib))
            {
                try
                {
                    var image = (System.Windows.Media.Imaging.BitmapSource)dataObject.GetData(DataFormats.Dib)!;
                    // 立即冻结图片对象，使其可以跨线程访问
                    image.Freeze();

                    // 在后台线程处理图片编码，避免 UI 线程阻塞
                    searchScheduled = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var filename = DataPath.TempFile(".jpg");

                            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));

                            await using (var fileStream = new FileStream(filename, FileMode.Create))
                            {
                                encoder.Save(fileStream);
                            }

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                OnImagePathChanged(filename);
                            });

                            await SearchCore(filename);

                            // 搜索完成后立即删除临时文件（解码在 SearchCore 内完成，无需延时）
                            try { File.Delete(filename); } catch { }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DIB 处理异常: {ex.Message}");
                        }
                    });
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DIB 格式处理失败: {ex.Message}");
                    // 继续尝试其他格式
                }
            }

            // 4. 处理浏览器拖放的图片（FileContents）
            if (dataObject.GetDataPresent("FileContents"))
            {
                try
                {
                    var data = dataObject.GetData("FileContents");
                    if (data is Stream stream)
                    {
                        // 先在 UI 线程将 Stream 内容复制到字节数组，避免 IDataObject 释放后 Stream 失效
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        var bytes = ms.ToArray();

                        // 在后台线程保存文件，避免 UI 线程阻塞
                        searchScheduled = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var filename = DataPath.TempFile(".jpg");
                                await File.WriteAllBytesAsync(filename, bytes);

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    OnImagePathChanged(filename);
                                });

                                await SearchCore(filename);

                                // 搜索完成后立即删除临时文件（解码在 SearchCore 内完成，无需延时）
                                try { File.Delete(filename); } catch { }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"FileContents 处理异常: {ex.Message}");
                            }
                        });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FileContents 处理失败: {ex.Message}");
                    // 继续尝试其他格式
                }
            }

            // 5. 处理URL或Base64文本
            if (dataObject.GetDataPresent(DataFormats.Text))
            {
                try
                {
                    string text = dataObject.GetData(DataFormats.Text)!.ToString()!;

                    // 检查是否为URL
                    if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                    {
                        // 在后台线程下载和处理文件，避免 UI 线程阻塞
                        searchScheduled = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // 先读取响应头检查文件大小，防止恶意 URL 返回超大文件导致 OOM
                                using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                                response.EnsureSuccessStatusCode();
                                const long MaxDownloadBytes = 50 * 1024 * 1024; // 50MB 上限
                                if (response.Content.Headers.ContentLength is null or > MaxDownloadBytes)
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        var sizeMB = response.Content.Headers.ContentLength.HasValue ? $"{response.Content.Headers.ContentLength.Value / 1024 / 1024}MB" : "未知大小";
                                        MessageBox.Show(Application.Current.MainWindow!, $"下载文件过大（{sizeMB}），超过 50MB 限制", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                                        IsSearching = false;
                                        SearchLoadingVisibility = Visibility.Collapsed;
                                    });
                                    return;
                                }
                                var ext = Path.GetExtension(uri.AbsolutePath);
                                if (string.IsNullOrEmpty(ext))
                                {
                                    ext = ".jpg";
                                }
                                var filename = DataPath.TempFile(ext);
                                // 流式写入，避免将整个文件读入内存；且在写入侧强制限长——
                                // 不能仅信 Content-Length，服务器可谎报头后超量发送
                                var tooLarge = false;
                                await using (var fs = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))
                                await using (var net = await response.Content.ReadAsStreamAsync())
                                {
                                    var buffer = new byte[81920];
                                    long downloaded = 0;
                                    int read;
                                    while ((read = await net.ReadAsync(buffer)) > 0)
                                    {
                                        downloaded += read;
                                        if (downloaded > MaxDownloadBytes)
                                        {
                                            tooLarge = true;
                                            break;
                                        }

                                        await fs.WriteAsync(buffer.AsMemory(0, read));
                                    }
                                }

                                if (tooLarge)
                                {
                                    try { File.Delete(filename); } catch { }
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        MessageBox.Show(Application.Current.MainWindow!, "下载文件实际大小超过 50MB 限制，已中止", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                                        IsSearching = false;
                                        SearchLoadingVisibility = Visibility.Collapsed;
                                    });
                                    return;
                                }

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    OnImagePathChanged(filename);
                                });

                                await SearchCore(filename);

                                // 搜索完成后立即删除临时文件（解码在 SearchCore 内完成，无需延时）
                                try { File.Delete(filename); } catch { }
                            }
                            catch (Exception ex)
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    MessageBox.Show(Application.Current.MainWindow!, $"下载或处理URL图片失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    IsSearching = false;
                                    SearchLoadingVisibility = Visibility.Collapsed;
                                    SearchStatusText = string.Empty;
                                });
                            }
                        });
                        return;
                    }

                    // 检查是否为Base64图像数据
                    if (text.StartsWith("data:image/"))
                    {
                        int commaIndex = text.IndexOf(',');
                        if (commaIndex != -1)
                        {
                            string base64Data = text.Substring(commaIndex + 1);
                            // Base64 解码移入后台线程，避免大图片时冻结 UI
                            searchScheduled = true;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    byte[] bytes = Convert.FromBase64String(base64Data);
                                    var filename = DataPath.TempFile(".jpg");
                                    await File.WriteAllBytesAsync(filename, bytes);

                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        OnImagePathChanged(filename);
                                    });

                                    await SearchCore(filename);

                                    // 搜索完成后立即删除临时文件（解码在 SearchCore 内完成，无需延时）
                                    try { File.Delete(filename); } catch { }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Base64 处理异常: {ex.Message}");
                                }
                            });
                            return;
                        }
                    }

                    // 检查是否为本地文件路径
                    if (File.Exists(text))
                    {
                        ImagePath = text;
                        await Search();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"文本数据处理失败: {ex.Message}");
                    // 继续尝试其他格式
                }
            }

            // 如果所有格式都失败，显示提示
            MessageBox.Show(Application.Current.MainWindow!, "无法识别拖放的数据格式，请尝试从剪切板搜索或选择本地文件拖放", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow!, $"处理拖放数据时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Debug.WriteLine($"HandleDrop 异常: {ex}");
        }
        finally
        {
            // 仅在没有后台搜索任务运行时清除状态，避免覆盖正在进行的搜索
            if (!searchScheduled && !IsSearching)
            {
                IsSearching = false;
                SearchLoadingVisibility = Visibility.Collapsed;
                SearchStatusText = string.Empty;
            }
        }
    }

    public void HandleDataGridKeyUp(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Delete && SelectedResult != null)
        {
            // 与右键菜单保持一致，添加确认对话框
            var message = modifiers == ModifierKeys.Shift
                ? "确认删除到回收站吗？"
                : "确认删除选中项吗？（将永久删除）";
            var result = MessageBox.Show(Application.Current.MainWindow!, message, "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;

            if (File.Exists(SelectedResult.路径))
            {
                // 删除前释放 Image 控件占用的文件
                if (DestImagePath == SelectedResult.路径)
                {
                    DestImagePath = string.Empty;
                    DestImageInfo = string.Empty;
                }

                try
                {
                    if (modifiers == ModifierKeys.Shift)
                    {
                        RecycleBinHelper.Delete(SelectedResult.路径);
                    }
                    else
                    {
                        File.Delete(SelectedResult.路径);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    MessageBox.Show(Application.Current.MainWindow!, $"删除失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            _indexService.RemoveFromIndex(SelectedResult.路径);
            _videoIndexService.RemoveFromIndex(SelectedResult.路径);
            SearchResults.Remove(SelectedResult);
            UpdateIndexCount();
        }

        if (modifiers == ModifierKeys.Control && key == Key.O && SelectedResult != null)
        {
            FileExplorerHelper.ExplorerFile(SelectedResult.路径);
        }
    }

    public bool CanClose()
    {
        // 先停止自动更新定时器，防止关闭窗口期间定时器触发新索引
        _updateIndexTimer?.Stop();
        return _indexService is { IsIndexing: false, IsWriting: false } && _videoIndexService is { IsIndexing: false, IsWriting: false };
    }

    private static bool IsVideoFile(string path) => VideoFrameExtractor.VideoExtensions.Any(ext => path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase));

    private async Task SearchCore(string filename)
    {
        // 如果已有搜索在运行，等待其完成后再执行（避免并发搜索导致结果覆盖和 CTS 竞争）
        await _searchLock.WaitAsync();
        // 版本号必须在 try 之前声明：finally 块需要读取它来判断等待期间是否有新搜索启动
        var currentVersion = Interlocked.Increment(ref _searchVersion);
        try
        {
            IsSearching = true;
            SearchLoadingVisibility = Visibility.Visible;
            SearchStatusText = "🔍 正在搜索相似图片...";
            ElapsedTime = string.Empty;

            // 在后台线程执行搜索,避免 UI 线程阻塞
            var (results, elapsed) = await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var sim = Similarity / 100f;

                var resultList = await _searchService.SearchAsync(
                    filename,
                    _indexService.Index,
                    _indexService.FrameIndex,
                    MatchAlgorithm,
                    sim,
                    FindRotated,
                    FindFlipped,
                    _videoIndexService.VideoIndex);

                // 粗筛无高置信视频命中（无命中或仅低分误命中）→ 自动回退 ORB 深度搜索（支持局部裁切图查询）
                if (!filename.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                    && resultList.Where(r => r.媒体类型 == "视频").All(r => r.匹配度 < 0.9f)
                    && OrbFeatureService.Instance.OrbIndex.Count > 0)
                {
                    // 原子替换 CTS：先创建新的，再交换旧的，最后取消并释放旧的
                    var newCts = new CancellationTokenSource();
                    var oldCts = Interlocked.Exchange(ref _deepSearchCts, newCts);
                    oldCts?.Cancel();
                    oldCts?.Dispose();
                    var token = newCts.Token;
                    try
                    {
                        Application.Current.Dispatcher.Invoke(() => SearchStatusText = "🔬 粗筛未命中视频，正在进行 ORB 深度搜索...");
                        using var queryGray = SkiaImageHelper.DecodeGrayThumb(filename, VideoFrameExtractor.FrameWidth);
                        var deepResults = await OrbFeatureService.Instance.SearchDeepAsync(queryGray, _videoIndexService.VideoIndex, (p, total) =>
                        {
                            Application.Current.Dispatcher.Invoke(() => SearchStatusText = $"🔬 ORB 深度搜索中：{p:#,0}/{total:#,0} 帧...");
                        }, token);
                        // 深度搜索结果必须补全文件信息：行样式会将"大小为空"的行禁用，不补全则无法选中/预览
                        ImageSearchService.EnrichResults(deepResults);
                        resultList.AddRange(deepResults);
                        resultList = resultList.OrderByDescending(r => r.匹配度).DistinctBy(r => r.路径).ToList();
                    }
                    catch (OperationCanceledException)
                    {
                        // 被新一次搜索打断
                    }
                    finally
                    {
                        // 释放本次搜索的 CTS（仅当仍指向自己时才释放，避免误释放后续搜索的 CTS）
                        if (Interlocked.CompareExchange(ref _deepSearchCts, null, newCts) == newCts)
                        {
                            newCts.Dispose();
                        }
                    }
                }

                sw.Stop();
                return (resultList, sw.ElapsedMilliseconds);
            });

            // 切回 UI 线程更新 UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                ElapsedTime = $"{elapsed}ms";

                // 批量替换：创建新集合避免逐项触发布局重算
                var newList = new ObservableCollection<SearchResult>(results);
                SearchResults = newList;

                if (SearchResults.Count > 0)
                {
                    SelectedResult = SearchResults[0];
                    SearchStatusText = $"✅ 搜索完成，找到 {SearchResults.Count} 个相似图片";
                }
                else
                {
                    SearchStatusText = "ℹ️ 未找到相似图片";
                }
            });
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                SearchStatusText = $"❌ 搜索失败: {ex.Message}";
                MessageBox.Show(Application.Current.MainWindow!, $"搜索时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
        finally
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                IsSearching = false;
            });
            // 先释放锁，让排队的搜索可以立即开始
            _searchLock.Release();
            // 延迟隐藏 loading，让用户看到完成状态
            await Task.Delay(800);
            // 仅在没有新搜索已启动时才隐藏，使用版本号避免竞态条件
            if (Volatile.Read(ref _searchVersion) == currentVersion)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    SearchLoadingVisibility = Visibility.Collapsed;
                    SearchStatusText = string.Empty;
                });
            }
        }
    }

    private double _maxThroughput;
    private CancellationTokenSource? _deepSearchCts;
    private long _searchVersion; // 用于检测 SearchCore finally 块执行时是否有新搜索启动
    private readonly SemaphoreSlim _searchLock = new(1, 1);

    private void OnIndexProgressChanged(object? sender, IndexProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            UpdateIndexButtonText = "⏸️ 停止索引";
            ProcessStatus = e.Message;
            IndexProgress = e.ProgressPercentage;
            IndexProgressText = $"{e.ProcessedFiles:#,0} / {e.TotalFiles:#,0}";
            IndexProgressVisibility = Visibility.Visible;

            if (e.ProcessedFiles > 0)
            {
                ProcessingFilename = "正在处理：" + e.Filename;
                IndexSpeed = $"索引速度: {e.Speed:F0} items/s ({e.ThroughputMB:F2}MB/s)";
                IndexSpeedText = $"{e.Speed:F0} items/s";
                IndexThroughputText = $"{e.ThroughputMB:F2} MB/s";

                // 计算最大吞吐量
                _maxThroughput = Math.Max(e.ThroughputMB, _maxThroughput);
                MaxThroughputText = $"{_maxThroughput:F2} MB/s";

                // 计算预估剩余时间
                var remainingFiles = e.TotalFiles - e.ProcessedFiles;
                if (remainingFiles > 0 && e.Speed > 0)
                {
                    // 0.9 是经验修正系数：实际速度通常低于当前瞬时速度（因后期文件可能更大/更慢）
                    var estimatedSeconds = remainingFiles / e.Speed / 0.9;
                    EstimatedRemainingTimeText = FormatTimespan(TimeSpan.FromSeconds(estimatedSeconds));
                }
                else
                {
                    EstimatedRemainingTimeText = "--";
                }

                // 添加速度数据点到历史记录 - 显示整个索引过程（限制最多2000个数据点）
                if (SpeedHistory.Count >= 2000)
                {
                    SpeedHistory.RemoveAt(0);
                }
                switch (e.TotalFiles)
                {
                    case <= 1000:
                    case <= 10000 when e.ProcessedFiles % 10 == 0:
                    case <= 100000 when e.ProcessedFiles % 100 == 0:
                    case > 100000 when e.ProcessedFiles % 200 == 0:
                        SpeedHistory.Add(e.ThroughputMB);
                        break;
                }
            }
        });
    }

    private string FormatTimespan(TimeSpan timespan)
    {
        if (timespan.TotalHours >= 1)
        {
            return $"{(int)timespan.TotalHours}h {timespan.Minutes}m {timespan.Seconds}s";
        }
        else if (timespan.TotalMinutes >= 1)
        {
            return $"{(int)timespan.TotalMinutes}m {timespan.Seconds}s";
        }
        else
        {
            return $"{timespan.Seconds}s";
        }
    }

    private void OnIndexUpdated(object? sender, EventArgs args) =>
        Application.Current.Dispatcher.Invoke(UpdateIndexCount);

    private void OnIndexCompleted(object? sender, IndexCompletedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            //UpdateIndexCount();

            if (e.Errors.Count > 0)
            {
                var errorDialog = new ErrorsDialog($"索引创建完成，耗时：{e.ElapsedSeconds:F2}s，以下文件格式不正确无法创建索引，请检查：\r\n{string.Join("\r\n", e.Errors)}");
                errorDialog.ShowDialog();
            }
            else if (e.FilesProcessed > 0)
            {
                MessageBox.Show(Application.Current.MainWindow!, $"索引创建完成，耗时：{e.ElapsedSeconds:F2}s", "消息", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            IndexProgressVisibility = Visibility.Collapsed;
            IndexProgress = 0;
            IndexProgressText = string.Empty;
            IndexSpeedText = string.Empty;
            IndexThroughputText = string.Empty;
            MaxThroughputText = string.Empty;
            EstimatedRemainingTimeText = string.Empty;
            UpdateIndexButtonText = "🔄 更新索引";
            UpdateIndexButtonEnabled = true;
            ProcessingFilename = string.Empty;
            _maxThroughput = 0;
            SpeedHistory.Clear();
        });
    }

    private void UpdateIndexCount()
    {
        var count = _indexService.Index.Count + _indexService.FrameIndex.Count + _videoIndexService.VideoIndex.Count;
        IndexCount = count > 0 ? $"{count}文件" : "请先创建索引";

        // 根据索引总数决定是否显示移除无效索引选项
        ShowRemoveInvalidIndex = count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 根据索引总数决定是否启用搜索配置区域
        IsSearchEnabled = count > 0;
    }

    static bool TryGetImageInfo(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        using var codec = SKCodec.Create(path);
        if (codec == null)
        {
            return false;
        }

        width = codec.Info.Width;
        height = codec.Info.Height;
        return true;
    }

    private void UpdateSourceImageInfo(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                if (TryGetImageInfo(path, out var width, out var height))
                {
                    var fileInfo = new FileInfo(path);
                    SourceImageInfo = $"分辨率：{width}x{height}，大小：{fileInfo.Length / 1024}KB";
                }
                else
                {
                    SourceImageInfo = "无法加载图片信息";
                }
            }
            catch
            {
                SourceImageInfo = "无法加载图片信息";
            }
        }
    }

    private void UpdateDestImageInfo(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                if (TryGetImageInfo(path, out var width, out var height))
                {
                    var fileInfo = new FileInfo(path);
                    DestImageInfo = $"分辨率：{width}x{height}，大小：{fileInfo.Length / 1024}KB";
                }
                else
                {
                    DestImageInfo = "无法加载图片信息";
                }
            }
            catch
            {
                DestImageInfo = "无法加载图片信息";
            }
        }
    }

    private void InitializePerformanceMonitoring()
    {
        try
        {
            _currentProcess = Process.GetCurrentProcess();
            _lastTotalProcessorTime = _currentProcess.TotalProcessorTime;
            _lastCpuCheckTime = DateTime.UtcNow;

            // 创建定时器，每秒更新一次
            _performanceTimer = new System.Timers.Timer(1000);
            _performanceTimer.Elapsed += UpdatePerformanceMetrics;
            _performanceTimer.AutoReset = true;
            _performanceTimer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"性能监测初始化失败: {ex.Message}");
        }
    }

    private void UpdatePerformanceMetrics(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            if (_currentProcess != null)
            {
                // 刷新进程信息
                _currentProcess.Refresh();

                // 使用 TotalProcessorTime 手动计算 CPU 使用率（避免 PerformanceCounter 多实例歧义）
                var currentTotalProcessorTime = _currentProcess.TotalProcessorTime;
                var currentTime = DateTime.UtcNow;
                var cpuTimeDelta = (currentTotalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds;
                var realTimeDelta = (currentTime - _lastCpuCheckTime).TotalMilliseconds;
                var cpuUsageValue = realTimeDelta > 0 ? cpuTimeDelta / realTimeDelta * 100.0 / Environment.ProcessorCount : 0;
                _lastTotalProcessorTime = currentTotalProcessorTime;
                _lastCpuCheckTime = currentTime;

                // 获取内存使用量（转换为 MB）
                var memoryUsage = _currentProcess.WorkingSet64 / (1024.0 * 1024.0);

                // 切回 UI 线程更新 UI（使用 BeginInvoke 避免阻塞计时器线程）
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    CpuUsage = cpuUsageValue;
                    MemoryUsage = memoryUsage;
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"更新性能指标失败: {ex.Message}");
        }
    }

    // 不需要终结器：_performanceTimer、_updateIndexTimer 是 System.Timers.Timer，
    // _currentProcess 是 Process——均为托管资源包装器，各自有终结器兜底。
    // MainViewModel 与应用同生命周期，在进程退出时由 GC 自然回收，无需显式终结。
    // 此处保留 Dispose 供 App.OnExit 显式调用，确保确定性释放。
    public void Dispose()
    {
        // 取消事件订阅，避免单例服务持有 ViewModel 引用
        _indexService.ProgressChanged -= OnIndexProgressChanged;
        _indexService.IndexCompleted -= OnIndexCompleted;
        _indexService.IndexUpdated -= OnIndexUpdated;
        _videoIndexService.ProgressChanged -= OnIndexProgressChanged;
        _videoIndexService.IndexCompleted -= OnIndexCompleted;
        _videoIndexService.IndexUpdated -= OnIndexUpdated;

        _performanceTimer?.Dispose();
        _updateIndexTimer?.Dispose();
        // 清理临时预览文件
        if (_previewTempFile != null)
        {
            try { File.Delete(_previewTempFile); } catch { }
            _previewTempFile = null;
        }
        // 取消正在进行的 ORB 深度搜索
        var cts = Interlocked.Exchange(ref _deepSearchCts, null);
        cts?.Cancel();
        cts?.Dispose();
        // 不 Dispose _currentProcess：我们不拥有该进程句柄，
        // Dispose 会关闭句柄导致 Process 属性访问失败。
    }
}