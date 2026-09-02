using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Text.RegularExpressions;
using SearchOption = System.IO.SearchOption;

AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
{
    Console.Error.WriteLine("发生未处理异常：" + eventArgs.ExceptionObject);
};

Console.WriteLine("欢迎使用清除图像exif信息小工具——ExifStraper by 懒得勤快\n\n");
var dirs = new List<string>();
if (args.Length > 0)
{
    if (args[0] == "reg-menu")
    {
        RegContextMenu();
        return;
    }
    dirs.AddRange(args.Where(a => a != "-a"));
}
else
{
    var dir = "";
    while (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
    {
        Console.WriteLine("请将待处理文件夹拖放到此处：");
        dir = Console.ReadLine()?.Trim('"') ?? "";
        if (dir == "reg-menu")
        {
            RegContextMenu();
            return;
        }
    }
    dirs.Add(dir);
}

var stripAll = args.Contains("-a");

Console.WriteLine("正在读取文件目录树......");
var temp = dirs.SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)).Where(s => Regex.IsMatch(s, @"\.(jpe?g|bmp)$", RegexOptions.IgnoreCase)).ToList();
var count = temp.Count;
var index = 0;
var parallelism = Environment.ProcessorCount * 2;
temp.AsParallel().WithDegreeOfParallelism(parallelism).ForAll(file =>
{
    Console.WriteLine($"正在处理[{Interlocked.Increment(ref index)}/{count}]：{file}");
    try
    {
        if (file.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            StripWithImageSharp(file);
        }
        else
        {
            StripJpegMetadata(file, stripAll);
        }
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"图像 {file} 处理失败（I/O 错误）：{ex.Message}");
    }
    catch (OutOfMemoryException)
    {
        Console.Error.WriteLine($"图像 {file} 处理失败（内存不足）");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"图像 {file} 处理失败：{ex.Message}");
    }
});

// 先改最深层的目录名，避免父目录先改名后子目录路径失效
var alldirs = dirs.SelectMany(dir => Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
    .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar))
    .Union(dirs)
    .ToList();
foreach (var dir in alldirs)
{
    var newName = ChineseConverter.Convert(dir, ChineseConversionDirection.TraditionalToSimplified);
    if (dir != newName)
    {
        if (Directory.Exists(newName))
        {
            Console.Error.WriteLine($"跳过：目标目录已存在 {newName}");
            continue;
        }
        FileSystem.MoveDirectory(dir, newName);
    }
}

/// <summary>
/// JPEG 无损剥离元数据：直接按段处理二进制，不重编码像素。
/// 默认剥除 APP1(EXIF/XMP)、APP13(IPTC)、COM(注释)；-a 时额外剥除其余 APPn（保留 APP0 JFIF 与 APP2 ICC 色彩配置）。
/// </summary>
static bool StripJpegMetadata(string file, bool stripAll)
{
    var bytes = File.ReadAllBytes(file);
    if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
    {
        return false; // 非 JPEG（扩展名与内容不符）
    }

    using var ms = new MemoryStream(bytes.Length);
    ms.Write(bytes, 0, 2); // SOI
    var pos = 2;
    var removed = false;
    while (pos + 4 <= bytes.Length)
    {
        if (bytes[pos] != 0xFF)
        {
            break; // 非段结构，剩余原样拷贝
        }

        var marker = bytes[pos + 1];
        if (marker == 0xFF)
        {
            ms.WriteByte(0xFF); // 填充字节
            pos++;
            continue;
        }

        // SOS(0xDA)/EOI(0xD9)：扫描数据起点，剩余内容原样拷贝并结束
        if (marker is 0xDA or 0xD9)
        {
            ms.Write(bytes, pos, bytes.Length - pos);
            pos = bytes.Length;
            break;
        }

        // 无长度字段的独立标记（RSTn/TEM）
        if (marker is >= 0xD0 and <= 0xD7 or 0x01)
        {
            ms.Write(bytes, pos, 2);
            pos += 2;
            continue;
        }

        var segLen = (bytes[pos + 2] << 8) | bytes[pos + 3];
        if (segLen < 2 || pos + 2 + segLen > bytes.Length)
        {
            break; // 段长度损坏，剩余原样拷贝
        }

        var strip = marker switch
        {
            0xE1 or 0xED or 0xFE => true, // APP1(EXIF/XMP)、APP13(IPTC)、COM
            >= 0xE0 and <= 0xEF when stripAll && marker is not 0xE0 and not 0xE2 => true, // -a：其余 APPn（保留 JFIF/ICC）
            _ => false
        };
        if (strip)
        {
            removed = true;
        }
        else
        {
            ms.Write(bytes, pos, 2 + segLen);
        }

        pos += 2 + segLen;
    }

    if (pos < bytes.Length)
    {
        ms.Write(bytes, pos, bytes.Length - pos);
    }

    if (!removed)
    {
        return false;
    }

    // 原子替换，避免写入中断损坏原图
    var tmp = file + ".tmp";
    File.WriteAllBytes(tmp, ms.ToArray());
    File.Move(tmp, file, true);
    return true;
}

/// <summary>BMP 等非 JPEG 格式：走 ImageSharp 清除全部元数据后重存。</summary>
static bool StripWithImageSharp(string file)
{
    using var image = Image.Load<Rgba64>(file);
    if (image.Metadata.ExifProfile == null && image.Metadata.IptcProfile == null && image.Metadata.XmpProfile == null)
    {
        return false;
    }

    image.Metadata.ExifProfile = null;
    image.Metadata.IptcProfile = null;
    image.Metadata.XmpProfile = null;
    image.Save(file);
    return true;
}

void RegContextMenu()
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    var principal = new System.Security.Principal.WindowsPrincipal(identity);
    if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
    {
        Console.Error.WriteLine("注册右键菜单需要管理员权限，请以管理员身份运行此命令");
        return;
    }

    try
    {
        using var dirKey = Registry.ClassesRoot.OpenSubKey("Directory", true)
            ?? throw new InvalidOperationException("无法打开注册表项 HKCR\\Directory");
        using var dirShell = dirKey.OpenSubKey("shell", true)
            ?? throw new InvalidOperationException("无法打开注册表项 HKCR\\Directory\\shell");
        using var key = dirShell.CreateSubKey("ExifStraper", true);
        using var command = key.CreateSubKey("command", true);
        key.SetValue("Icon", "%SystemRoot%\\System32\\shell32.dll,141", RegistryValueKind.ExpandString);
        key.SetValue("MUIVerb", "ExifStraper");
        command.SetValue("", $"\"{System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName}\" \"%1\"", RegistryValueKind.ExpandString);

        using var allKey = Registry.ClassesRoot.OpenSubKey("*", true)
            ?? throw new InvalidOperationException("无法打开注册表项 HKCR\\*");
        using var allShell = allKey.OpenSubKey("shell", true)
            ?? throw new InvalidOperationException("无法打开注册表项 HKCR\\*\\shell");
        using var key2 = allShell.CreateSubKey("ExifStraper", true);
        using var command2 = key2.CreateSubKey("command", true);
        key2.SetValue("Icon", "%SystemRoot%\\System32\\shell32.dll,141", RegistryValueKind.ExpandString);
        key2.SetValue("MUIVerb", "ExifStraper");
        command2.SetValue("", $"\"{System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName}\" \"%1\"", RegistryValueKind.ExpandString);
        Console.WriteLine("右键菜单添加成功");
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("注册右键菜单需要管理员权限，请以管理员身份运行此命令");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"注册右键菜单失败：{ex.Message}");
    }
}
