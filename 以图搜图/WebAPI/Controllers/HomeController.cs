using Masuit.Tools.Systems;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using Masuit.Tools.Files;
using 以图搜图.Models;
using 以图搜图.Services;
using 以图搜图.ViewModels;

namespace 以图搜图.WebAPI.Controllers;

[ApiController]
public class HomeController : Controller
{
    private readonly ImageIndexService _indexService = ImageIndexService.Instance;
    private readonly VideoIndexService _videoIndexService = VideoIndexService.Instance;
    private readonly ImageSearchService _searchService = new ImageSearchService();
    public static MainViewModel? MainViewModel { get; set; }

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif"
    };

    private static bool IsValidImageMagicBytes(byte[] header, int bytesRead)
    {
        if (bytesRead < 4) return false;
        // JPEG: FF D8 FF
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return true;
        // PNG: 89 50 4E 47
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return true;
        // GIF: 47 49 46 38
        if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38) return true;
        // BMP: 42 4D
        if (header[0] == 0x42 && header[1] == 0x4D) return true;
        // WEBP: RIFF....WEBP (52 49 46 46 .... 57 45 42 50)
        if (bytesRead >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return true;
        // TIFF: 49 49 2A 00 (little-endian) or 4D 4D 00 2A (big-endian)
        if (header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) return true;
        if (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A) return true;
        return false;
    }

    /// <summary>
    /// 创建或更新索引
    /// </summary>
    /// <param name="dir">索引目录</param>
    /// <param name="removeInvalid">是否移除无效索引</param>
    /// <returns></returns>
    [HttpPatch("index")]
    public async Task<ActionResult> UpdateIndex([Required] string dir, bool removeInvalid)
    {
        if (MainViewModel is null) return StatusCode(503, "应用程序尚未初始化完成，请稍后重试");
        if (!Directory.Exists(dir)) return BadRequest("指定的目录不存在");
        // UpdateIndex 命令在索引进行中是"停止"语义，API 调用绝不能静默变成停止或假成功，显式返回冲突
        if (_indexService.IsIndexing || _videoIndexService.IsIndexing)
        {
            return StatusCode(409, "已有索引任务进行中，请先停止或等待完成");
        }

        MainViewModel.DirectoryPath = dir;
        MainViewModel.RemoveInvalidIndex = removeInvalid;
        await MainViewModel.UpdateIndexCommand.ExecuteAsync(this);
        return Ok("已发送指令，请查看主程序窗口");
    }

    /// <summary>
    /// 搜索图像
    /// </summary>
    /// <param name="file">需要搜索的图片</param>
    /// <param name="similar">相似度</param>
    /// <param name="algorithm">匹配算法，1：DifferenceHash，2：DctHash，4：DctHash64，7：所有</param>
    /// <param name="checkRotated">查找旋转</param>
    /// <param name="checkFlip">查找翻转</param>
    /// <returns></returns>
    [HttpPost("search")]
    [RequestSizeLimit(50 * 1024 * 1024)] // 50MB 上限
    public async Task<ActionResult> Search(IFormFile file, [Range(75, 100)] float similar = 75, MatchAlgorithm algorithm = MatchAlgorithm.All, bool checkRotated = true, bool checkFlip = false)
    {
        // 验证 ContentType：仅允许图片类型，防止上传可执行文件等非图片内容
        if (string.IsNullOrEmpty(file.ContentType) || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("仅支持图片文件（Content-Type 必须为 image/*）");
        }

        // 验证文件内容的 magic bytes，防止 Content-Type 伪造
        var ext = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(ext ?? ""))
        {
            ext = ".jpg";
        }

        var filename = DataPath.TempFile(ext);
        try
        {
            using (var stream = file.OpenReadStream())
            {
                // 读取并验证 magic bytes
                var header = new byte[12];
                var bytesRead = await stream.ReadAsync(header, 0, header.Length);
                if (!IsValidImageMagicBytes(header, bytesRead))
                {
                    return BadRequest("文件内容不是有效的图片格式");
                }

                // 回到开头保存完整文件
                stream.Position = 0;
                await stream.SaveFileAsync(filename);
            }

            // 与 UI 搜索行为对齐：传入视频帧索引，API 调用也能命中视频结果
            return Ok(await _searchService.SearchAsync(filename, _indexService.Index, _indexService.FrameIndex, algorithm, similar / 100, checkRotated, checkFlip, _videoIndexService.VideoIndex));
        }
        finally
        {
            try { System.IO.File.Delete(filename); } catch { }
        }
    }
}