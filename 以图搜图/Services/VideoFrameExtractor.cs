using Masuit.Tools.Logging;
using Masuit.Tools.Media;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace 以图搜图.Services;

/// <summary>
/// 一帧抽取结果：时间戳（秒）、640px 灰度位图、去重时已算好的差异哈希
/// </summary>
internal sealed class VideoFrame(double timestamp, SKBitmap bitmap, ulong[] diffHash) : IDisposable
{
    public double Timestamp { get; } = timestamp;
    public SKBitmap Bitmap { get; } = bitmap;
    public ulong[] DiffHash { get; } = diffHash;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// 通过外挂 ffmpeg/ffprobe 从视频抽帧：fps=1、640px 灰度 rawvideo 管道输出、
/// showinfo 解析精确时间戳、相邻帧差异哈希 ≥99% 去重。帧不落盘。
/// </summary>
internal static class VideoFrameExtractor
{
    /// <summary>索引帧宽度。640px 保证小区域查询图在帧内有足够 ORB 特征点可命中（320px 时小区域仅占几十像素，特征点稀疏）</summary>
    public const int FrameWidth = 640;

    private static readonly Regex PtsTimeRegex = new(@"pts_time:\s*([0-9.]+)", RegexOptions.Compiled);

    /// <summary>相邻帧差异哈希相似度达到该值则视为重复帧跳过</summary>
    public const float DedupSimilarity = 0.99f;

    public static readonly string[] VideoExtensions = ["mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "ts", "m4v", "mpg", "mpeg", "3gp", "rmvb", "vob"];

    public static string FfmpegPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
    public static string FfprobePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");

    public static bool IsAvailable => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    /// <summary>探测视频分辨率与时长（秒）。失败返回 null。</summary>
    public static async Task<(int width, int height, double duration)?> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FfprobePath))
        {
            return null;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FfprobePath,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -show_entries format=duration -of json \"{videoPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(stdout);
            var stream = doc.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
            if (stream.ValueKind == JsonValueKind.Undefined)
            {
                return null; // 无视频流
            }

            var width = stream.GetProperty("width").GetInt32();
            var height = stream.GetProperty("height").GetInt32();
            var duration = 0d;
            if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
            {
                // ffmpeg 输出恒为点号小数，必须用 InvariantCulture 解析；
                // 否则在逗号小数区域（de/fr 等）'.' 被当作千分位，时长被放大数千倍
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            }

            return width > 0 && height > 0 ? (width, height, duration) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>抽取指定时间点的视频帧为 jpg 文件（供命中帧预览）。成功返回 true。</summary>
    public static async Task<bool> ExtractFrameImageAsync(string videoPath, double timestamp, string outputPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FfmpegPath))
        {
            return false;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                // 数值参数必须 InvariantCulture 格式化：逗号小数区域下 "1,500" 会被 ffmpeg 拒绝
                Arguments = FormattableString.Invariant($"-nostdin -hide_banner -v error -ss {timestamp:F3} -i \"{videoPath}\" -frames:v 1 -q:v 3 -y \"{outputPath}\""),
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 && File.Exists(outputPath);
        }
        catch (Exception)
        {
            KillQuietly(process);
            return false;
        }
    }

    /// <summary>
    /// 枚举视频采样帧（fps=1、去重后）。调用方需 Dispose 每个 VideoFrame。
    /// 枚举被中断（取消/异常）时 ffmpeg 进程会被强制结束。
    /// </summary>
    public static IEnumerable<VideoFrame> ExtractFrames(string videoPath, int srcWidth, int srcHeight, CancellationToken cancellationToken) =>
        ExtractFrames(videoPath, srcWidth, srcHeight, fps: 1, start: null, end: null, cancellationToken);

    /// <summary>
    /// 枚举视频采样帧（可指定采样率与时间区间，供粗命中后高帧率二次精匹配）。
    /// 调用方需 Dispose 每个 VideoFrame。
    /// </summary>
    public static IEnumerable<VideoFrame> ExtractFrames(string videoPath, int srcWidth, int srcHeight, double fps, double? start, double? end, CancellationToken cancellationToken)
    {
        var outHeight = Math.Max(2, (int)Math.Round((double)FrameWidth * srcHeight / srcWidth));
        var frameSize = FrameWidth * outHeight;

        // -ss 放 -i 前（输入定位，快）；-t 限制时长，避免为几秒区间解码整个视频
        // 所有数值参数必须 InvariantCulture 格式化：逗号小数区域下 "1,500" 会被 ffmpeg 拒绝
        var seek = start is > 0 ? FormattableString.Invariant($"-ss {start.Value:F3} ") : "";
        var duration = end.HasValue && start.HasValue && end.Value > start.Value ? FormattableString.Invariant($"-t {end.Value - start.Value:F3} ") : "";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                Arguments = FormattableString.Invariant($"-nostdin -hide_banner -v info -filter_threads 2 {seek}-i \"{videoPath}\" {duration}-an -sn -vf \"fps={fps},showinfo,scale={FrameWidth}:{outHeight},format=gray\" -f rawvideo -"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var frames = new BlockingCollection<VideoFrame?>(32);
        var producer = Task.Run(() => ProduceFrames(process, videoPath, outHeight, frameSize, frames, start), CancellationToken.None);

        try
        {
            foreach (var frame in frames.GetConsumingEnumerable(cancellationToken))
            {
                if (frame != null)
                {
                    yield return frame;
                }
            }
        }
        finally
        {
            KillQuietly(process);
            try
            {
                producer.Wait(3000);
            }
            catch
            {
                // 生产者异常（含 ffmpeg 错误）由索引服务按文件粒度记录，此处不再抛出
            }

            while (frames.TryTake(out var leftover))
            {
                leftover?.Dispose();
            }

            frames.Dispose();
        }
    }

    private static void ProduceFrames(Process process, string videoPath, int outHeight, int frameSize, BlockingCollection<VideoFrame?> output, double? start = null)
    {
        var timestamps = new BlockingCollection<double>(256);
        try
        {
            process.Start();

            // stderr 线程：showinfo 逐帧输出 pts_time，与 stdout 帧顺序一一对应
            var stderrTask = Task.Run(() =>
            {
                try
                {
                    var regex = PtsTimeRegex;
                    while (process.StandardError.ReadLine() is { } line)
                    {
                        var match = regex.Match(line);
                        // ffmpeg 输出恒为点号小数，必须用 InvariantCulture 解析；
                        // 逗号小数区域会把 '.' 当千分位，时间戳被放大数千倍
                        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
                        {
                            timestamps.Add(pts);
                        }
                    }
                }
                catch
                {
                    // 进程被杀导致管道关闭，正常结束
                }
                finally
                {
                    timestamps.CompleteAdding();
                }
            });

            var stdout = process.StandardOutput.BaseStream;
            ulong[]? lastHash = null;
            var frameIndex = 0;
            var buffer = new byte[frameSize]; // 在循环外分配，每帧复用
            while (true)
            {
                if (!ReadExact(stdout, buffer, frameSize))
                {
                    break; // EOF：视频结束
                }

                // 取该帧时间戳；stderr 滞后给 30s 余量（高负载/大分辨率视频时 stderr 可能延迟），
                // 极端情况回退帧号推算
                if (!timestamps.TryTake(out var timestamp, TimeSpan.FromSeconds(30)))
                {
                    timestamp = frameIndex + 0.5;
                }

                // 指定了起始时间时，showinfo 的 pts_time 是相对输入流的绝对时间，
                // 但 -ss 在 -i 前会重置时间戳，需加回起始偏移得到视频内真实时间
                if (start is > 0)
                {
                    timestamp += start.Value;
                }

                frameIndex++;
                var bitmap = CreateGrayBitmap(buffer, FrameWidth, outHeight);
                var hash = bitmap.DifferenceHash256();
                if (lastHash != null && ImageHasher.Compare(lastHash, hash) >= DedupSimilarity)
                {
                    bitmap.Dispose(); // 与上一保留帧几乎相同：跳过
                    continue;
                }

                lastHash = hash;
                output.Add(new VideoFrame(timestamp, bitmap, hash));
            }

            process.WaitForExit(10000);
            stderrTask.Wait(3000);
        }
        catch (Exception ex)
        {
            LogManager.Error(new Exception($"视频抽帧异常 {videoPath}: {ex.Message}", ex));
        }
        finally
        {
            KillQuietly(process);
            output.CompleteAdding();
        }
    }

    /// <summary>恰好读满 size 字节；流提前结束返回 false。</summary>
    private static bool ReadExact(Stream stream, byte[] buffer, int size)
    {
        var offset = 0;
        while (offset < size)
        {
            var read = stream.Read(buffer, offset, size - offset);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static SKBitmap CreateGrayBitmap(byte[] buffer, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        var pixels = bitmap.GetPixels();
        if (bitmap.RowBytes == width)
        {
            Marshal.Copy(buffer, 0, pixels, buffer.Length);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(buffer, y * width, pixels + y * bitmap.RowBytes, width);
            }
        }

        return bitmap;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // 进程已退出
        }
    }
}
