using Masuit.Tools.Files;
using Xunit;

namespace ImageSearch.Tests;

/// <summary>
/// config.ini 里预置的播放器配置模板用 "#PlayerPath=..." 形式做"可启用的注释行"。
/// 无论 IniFile 库把 '#' 视为注释还是普通字符，都必须满足：
/// 1) 不产生名为 "PlayerPath" 的键（否则留空探测会被一条带 # 前缀的行错误满足）
/// 2) 不抛异常、不影响同 Section 其他键的读取
/// </summary>
public class IniFileCommentTests : IDisposable
{
    private readonly string _iniPath = Path.Combine(Path.GetTempPath(), "ImageSearchTests_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose()
    {
        try { File.Delete(_iniPath); } catch { }
    }

    [Fact]
    public void 井号前缀行不会产生真实的键()
    {
        File.WriteAllLines(_iniPath,
        [
            "[Global]",
            "#PlayerPath=D:\\tools\\mpv\\mpv.exe",
            "#PlayerArgs=--start={seconds} \"{file}\"",
            "RunServer=false",
        ]);

        var ini = new IniFile(_iniPath);

        // 关键断言：留空（等价于所有模板行被注释）时必须读到默认值，触发自动探测
        Assert.Equal("", ini.GetValue("Global", "PlayerPath", ""));
        Assert.Equal("", ini.GetValue("Global", "PlayerArgs", ""));
        // 同 Section 正常键不受影响
        Assert.Equal("false", ini.GetValue("Global", "RunServer", ""));
    }

    [Fact]
    public void 去掉井号后配置生效()
    {
        File.WriteAllLines(_iniPath,
        [
            "[Global]",
            "PlayerPath=D:\\tools\\mpv\\mpv.exe",
            "PlayerArgs=--start={seconds} \"{file}\"",
        ]);

        var ini = new IniFile(_iniPath);

        Assert.Equal("D:\\tools\\mpv\\mpv.exe", ini.GetValue("Global", "PlayerPath", ""));
        Assert.Equal("--start={seconds} \"{file}\"", ini.GetValue("Global", "PlayerArgs", ""));
    }
}
