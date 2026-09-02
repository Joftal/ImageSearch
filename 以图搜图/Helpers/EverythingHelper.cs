using System.Runtime.InteropServices;
using System.Text;

namespace 以图搜图;

public static class EverythingHelper
{
    // 导入Everything DLL的方法
    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_SetSearch(string lpSearchString);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_GetResultFullPathName(uint index, StringBuilder path, uint length);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern bool Everything_Query(bool wait);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_SetMax(uint dwMaxResults);

    private static readonly object _lock = new();

    static EverythingHelper()
    {
        Everything_SetMax(1_000_000); // 限制最多100万结果，避免内存溢出
    }

    public static IEnumerable<string> EnumerateFiles(string directoryPath, string extFilter = "jpg;jpeg;bmp;png;gif;webp")
    {
        string search = $"file:\"{directoryPath}\" ext:{extFilter}"; // 仅文件，并限制路径

        // Everything SDK 使用进程全局状态，必须加锁防止并发调用导致结果交错
        lock (_lock)
        {
            Everything_SetSearch(search);
            Everything_Query(true); // 执行搜索
            uint numResults = Everything_GetNumResults();
            var results = new List<string>((int)numResults);
            var path = new StringBuilder(32768); // Windows 长路径最大32767字符

            for (uint i = 0; i < numResults; i++)
            {
                path.Clear();
                Everything_GetResultFullPathName(i, path, 32768);
                results.Add(path.ToString());
            }
            return results;
        }
    }
}
