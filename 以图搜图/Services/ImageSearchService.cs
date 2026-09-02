using System.Collections;
using Masuit.Tools;
using Masuit.Tools.Media;
using SkiaSharp;
using System.Collections.Concurrent;
using System.IO;
using 以图搜图.Models;

namespace 以图搜图.Services;

public class ImageSearchService
{
    public async Task<List<SearchResult>> SearchAsync(string filename, ConcurrentDictionary<string, IndexItem> index, ConcurrentDictionary<string, FrameIndexItem> frameIndex, MatchAlgorithm algorithm, float similarity, bool checkRotated, bool checkFlipped, ConcurrentDictionary<string, VideoIndexItem>? videoIndex = null)
    {
        var parallelism = Environment.ProcessorCount * 4;
        return await Task.Run(() =>
        {
            var defHashs = new ConcurrentBag<ulong[]>();
            var dctHashs = new ConcurrentBag<ulong>();
            var pHashs = new ConcurrentBag<ulong>();
            var actions = new List<Action>();

            if (filename.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                using var frames = new DisposableCollection<SKBitmap>(SkiaImageHelper.DecodeGrayFrames(filename,160));
                foreach (var frame in frames.Items)
                {
                    actions.Add(() =>
                    {
                            if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                            {
                                defHashs.Add(frame.DifferenceHash256());
                            }

                            if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                            {
                                dctHashs.Add(frame.DctHash());
                            }
                            if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                            {
                                pHashs.Add(frame.DctHash64());
                            }
                    });
                }

                Parallel.Invoke(actions.ToArray());
            }
            else
            {
                using (var image = SkiaImageHelper.DecodeGrayThumb(filename,160))
                {
                    if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                    {
                        actions.Add(() => defHashs.Add(image.DifferenceHash256()));
                    }

                    if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                    {
                        actions.Add(() => dctHashs.Add(image.DctHash()));
                    }
                    if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                    {
                        actions.Add(() => pHashs.Add(image.DctHash64()));
                    }
                    if (checkRotated)
                    {
                        actions.Add(() =>
                        {
                            using var clone = image.Rotate(90);
                            if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                            {
                                defHashs.Add(clone.DifferenceHash256());
                            }

                            if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                            {
                                dctHashs.Add(clone.DctHash());
                            }
                            if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                            {
                                pHashs.Add(clone.DctHash64());
                            }
                        });
                        actions.Add(() =>
                        {
                            using var clone = image.Rotate(180);
                            if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                            {
                                defHashs.Add(clone.DifferenceHash256());
                            }

                            if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                            {
                                dctHashs.Add(clone.DctHash());
                            }
                            if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                            {
                                pHashs.Add(clone.DctHash64());
                            }
                        });
                        actions.Add(() =>
                        {
                            using var clone = image.Rotate(270);
                            if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                            {
                                defHashs.Add(clone.DifferenceHash256());
                            }

                            if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                            {
                                dctHashs.Add(clone.DctHash());
                            }
                            if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                            {
                                pHashs.Add(clone.DctHash64());
                            }
                        });
                    }

                    if (checkFlipped)
                    {
                        actions.Add(() =>
                        {
                            using var clone = image.FlipHorizontal();
                            if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                            {
                                defHashs.Add(clone.DifferenceHash256());
                            }

                            if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                            {
                                dctHashs.Add(clone.DctHash());
                            }
                            if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                            {
                                pHashs.Add(clone.DctHash64());
                            }
                        });
                        actions.Add(() =>
                        {
                            using var clone = image.FlipVertical();
                            if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                            {
                                defHashs.Add(clone.DifferenceHash256());
                            }

                            if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                            {
                                dctHashs.Add(clone.DctHash());
                            }
                            if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                            {
                                pHashs.Add(clone.DctHash64());
                            }
                        });
                    }

                    Parallel.Invoke(actions.ToArray());
                }
            }

            var list = new List<SearchResult>();

            if (filename.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                list.AddRange(frameIndex.AsParallel().WithDegreeOfParallelism(parallelism).SelectMany(x =>
                {
                    var items = new List<SearchResult>(4);
                    if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                    {
                        items.Add(new SearchResult
                        {
                            路径 = x.Key,
                            匹配度 = x.Value.DifferenceHash.SelectMany(h => defHashs.Select(hh => ImageHasher.Compare(h, hh)).Where(f => f >= similarity)).OrderDescending().Take(10).DefaultIfEmpty().Average(),
                            匹配算法 = "Difference Hash"
                        });
                    }
                    var sim = Math.Max(0.85, similarity);
                    if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                    {
                        items.Add(new SearchResult
                        {
                            路径 = x.Key,
                            匹配度 = x.Value.DctHash64.SelectMany(h => pHashs.Select(hh => ImageHasher.Compare(h, hh)).Where(f => f >= sim)).OrderDescending().Take(10).DefaultIfEmpty().Average(),
                            匹配算法 = "DCT Hash 64"
                        });
                    }
                    if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                    {
                        items.Add(new SearchResult
                        {
                            路径 = x.Key,
                            匹配度 = x.Value.DctHash.SelectMany(h => dctHashs.Select(hh => ImageHasher.Compare(h, hh)).Where(f => f >= sim)).OrderDescending().Take(10).DefaultIfEmpty().Average(),
                            匹配算法 = "DCT Hash 32"
                        });
                    }
                    return items;
                }).Where(x => x.匹配度 >= similarity));
            }
            else
            {
                var sim = Math.Max(0.85, similarity);
                list.AddRange(frameIndex.AsParallel().WithDegreeOfParallelism(parallelism).SelectMany(x =>
                {
                    var items = new List<SearchResult>(4);
                    if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                    {
                        items.Add(new SearchResult
                        {
                            路径 = x.Key,
                            匹配度 = x.Value.DctHash64.SelectMany(h => pHashs.Select(hh => ImageHasher.Compare(h, hh)).Where(f => f >= sim)).MaxOrDefault(),
                            匹配算法 = "DCT Hash 64"
                        });
                    }
                    if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                    {
                        items.Add(new SearchResult
                        {
                            路径 = x.Key,
                            匹配度 = x.Value.DifferenceHash.SelectMany(h => defHashs.Select(hh => ImageHasher.Compare(h, hh)).Where(f => f >= similarity)).MaxOrDefault(),
                            匹配算法 = "Difference Hash"
                        });
                    }
                    if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                    {
                        items.Add(new SearchResult
                        {
                            路径 = x.Key,
                            匹配度 = x.Value.DctHash.SelectMany(h => dctHashs.Select(hh => ImageHasher.Compare(h, hh)).Where(f => f >= sim)).MaxOrDefault(),
                            匹配算法 = "DCT Hash 32"
                        });
                    }
                    return items;
                }).Where(x => x.匹配度 >= similarity));

                list.AddRange(index.AsParallel().WithDegreeOfParallelism(parallelism).SelectMany(pair =>
                {
                    var (key, value) = pair;
                    var items = new List<SearchResult>();
                    {
                        if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                        {
                            var match = pHashs.Max(h => ImageHasher.Compare(value.DctHash64, h));
                            if (match >= sim)
                            {
                                items.Add(new SearchResult
                                {
                                    路径 = key,
                                    匹配度 = match,
                                    匹配算法 = "DCT Hash 64"
                                });
                            }
                        }
                        if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                        {
                            var match = defHashs.Max(h => ImageHasher.Compare(value.DifferenceHash, h));
                            if (match >= similarity)
                            {
                                items.Add(new SearchResult
                                {
                                    路径 = key,
                                    匹配度 = match,
                                    匹配算法 = "Difference Hash"
                                });
                            }
                        }
                        if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                        {
                            var match = dctHashs.Max(h => ImageHasher.Compare(value.DctHash, h));
                            if (match >= sim)
                            {
                                items.Add(new SearchResult
                                {
                                    路径 = key,
                                    匹配度 = match,
                                    匹配算法 = "DCT Hash 32"
                                });
                            }
                        }
                    }
                    return items;
                }));
            }

            // 视频帧检索：与 GIF 逐帧同构，但需记录命中帧序号以换算时间戳；GIF 查询图不参与视频检索
            if (!filename.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) && videoIndex is { Count: > 0 })
            {
                var videoSim = (float)Math.Max(0.85, similarity);
                list.AddRange(videoIndex.AsParallel().WithDegreeOfParallelism(parallelism).SelectMany(x =>
                {
                    var items = new List<SearchResult>(4);
                    if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                    {
                        var (best, bestIdx, hits) = MatchFrames(x.Value.DctHash64, h => pHashs.Max(hh => ImageHasher.Compare(h, hh)), videoSim);
                        if (bestIdx >= 0)
                        {
                            items.Add(ToVideoResult(x.Key, x.Value.Timestamps, best, bestIdx, hits, "DCT Hash 64"));
                        }
                    }
                    if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                    {
                        var (best, bestIdx, hits) = MatchFrames(x.Value.DifferenceHash, h => defHashs.Max(hh => ImageHasher.Compare(h, hh)), similarity);
                        if (bestIdx >= 0)
                        {
                            items.Add(ToVideoResult(x.Key, x.Value.Timestamps, best, bestIdx, hits, "Difference Hash"));
                        }
                    }
                    if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                    {
                        var (best, bestIdx, hits) = MatchFrames(x.Value.DctHash, h => dctHashs.Max(hh => ImageHasher.Compare(h, hh)), videoSim);
                        if (bestIdx >= 0)
                        {
                            items.Add(ToVideoResult(x.Key, x.Value.Timestamps, best, bestIdx, hits, "DCT Hash 32"));
                        }
                    }
                    return items;
                }));
            }

            list = list.OrderByDescending(a => a.匹配度).DistinctBy(e => e.路径).ToList();
            // 一次遍历过滤有效文件，避免双重 File.Exists 系统调用
            var validResults = list.Where(e => File.Exists(e.路径)).ToList();
            EnrichResults(validResults, parallelism);
            return validResults;
        });
    }

    /// <summary>
    /// 为搜索结果补全文件大小与所属文件夹统计（目录级并行统计 + 单遍赋值）。
    /// 必须补全：UI 行样式会把「大小为空」的结果行禁用为不可选中/预览
    /// （ORB 深度搜索结果若绕过本方法直接合并进列表，将无法点击查看）。
    /// </summary>
    public static void EnrichResults(List<SearchResult> results, int? parallelism = null)
    {
        parallelism ??= Environment.ProcessorCount * 4;
        var dic = results.GroupBy(r => new FileInfo(r.路径).DirectoryName).Where(g => g.Key != null).AsParallel().WithDegreeOfParallelism(parallelism.Value).Select(g =>
        {
            var count = 0;
            long size = 0;
            try
            {
                // 只统计当前目录一层，避免对结果所在目录做全树递归遍历
                foreach (var f in new DirectoryInfo(g.Key!).EnumerateFiles("*.*", SearchOption.TopDirectoryOnly))
                {
                    count++;
                    size += f.Length;
                }
            }
            catch
            {
                // 目录无权限等异常时按 0 统计
            }

            return new
            {
                Key = g.Key!,
                Length = count,
                Size = size / 1048576f
            };
        }).ToDictionary(a => a.Key);

        results.OrderBy(e => e.路径).ForEach(result =>
        {
            try
            {
                var file = new FileInfo(result.路径);
                result.大小 = $"{file.Length / 1024}KB";
                if (string.IsNullOrEmpty(result.媒体类型))
                {
                    result.媒体类型 = result.路径.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "GIF" : "图片";
                }
                var dirName = file.DirectoryName!;
                if (dic.ContainsKey(dirName))
                {
                    result.所属文件夹文件数 = dic[dirName].Length;
                    result.所属文件夹大小 = $"{dic[dirName].Size:F2}MB";
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }

    /// <summary>
    /// 逐帧与查询哈希比对：返回最佳相似度、最佳帧号与全部过阈值的命中帧号。
    /// </summary>
    private static (float best, int bestIdx, List<int> hits) MatchFrames<THash>(List<THash> frameHashes, Func<THash, float> scorer, float threshold)
    {
        var best = 0f;
        var bestIdx = -1;
        var hits = new List<int>();
        for (var i = 0; i < frameHashes.Count; i++)
        {
            var sim = scorer(frameHashes[i]);
            if (sim >= threshold)
            {
                hits.Add(i);
                if (sim > best)
                {
                    best = sim;
                    bestIdx = i;
                }
            }
        }

        return (best, bestIdx, hits);
    }

    private static SearchResult ToVideoResult(string path, List<double> timestamps, float best, int bestIdx, List<int> hits, string algorithmName)
    {
        var bestTs = bestIdx < timestamps.Count ? timestamps[bestIdx] : bestIdx + 0.5;
        var timeSpan = TimeSpan.FromSeconds(bestTs);
        return new SearchResult
        {
            路径 = path,
            匹配度 = best,
            匹配算法 = algorithmName,
            媒体类型 = "视频",
            时间戳 = timeSpan.ToString(timeSpan.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss"),
            命中时间戳秒数 = bestTs,
            命中时间点 = hits.Select(i => i < timestamps.Count ? timestamps[i] : i + 0.5).Order().ToList()
        };
    }
}