using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace 以图搜图.Converters;

public class ServerStatusColorConverter : IValueConverter
{
    // 使用静态冻结画刷，避免每次绑定转换都创建新实例导致内存泄漏
    // Freeze() 后画刷不可变，可安全跨线程共享，WPF 不会为其创建 Dispatcher 代理
    private static readonly SolidColorBrush GreenBrush;
    private static readonly SolidColorBrush RedBrush;

    static ServerStatusColorConverter()
    {
        GreenBrush = new SolidColorBrush(Color.FromArgb(255, 40, 167, 69));
        GreenBrush.Freeze();
        RedBrush = new SolidColorBrush(Color.FromArgb(255, 220, 53, 69));
        RedBrush.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo cultureInfo)
    {
        return value is bool isRunning && isRunning ? GreenBrush : RedBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo cultureInfo)
    {
        throw new NotImplementedException();
    }
}