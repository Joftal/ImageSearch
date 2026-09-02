using System.Diagnostics;
using System.Windows;
using Masuit.Tools.Files;
using Masuit.Tools.Logging;
using 以图搜图.WebAPI;

namespace 以图搜图;

public partial class App : Application
{
#pragma warning disable CS0649 // DEBUG 配置下单实例检查被 #if !DEBUG 跳过，字段有意不赋值
    private static Mutex? _mutex;
#pragma warning restore CS0649
    private const string MutexName = "ImageSearch_SingleInstance_Mutex";

    protected override void OnStartup(StartupEventArgs e)
    {
        // 清理上次运行残留的临时文件（崩溃/异常退出时未删除的查询图/预览帧）；便携模式下位于程序目录 temp\
        DataPath.CleanupTempFiles();
#if DEBUG
        base.OnStartup(e); // 仍需触发 StartupUri 创建 MainWindow
        return; // 开发期跳过管理员提权/单实例检查/异常处理注册
#endif
#pragma warning disable CS0162 // DEBUG 下上方 return 使后续代码不可达，属有意为之
        var isAdmin = new IniFile(DataPath.Get("config.ini")).GetValue("Global", "RunAsAdmin", false);
        if (isAdmin && !IsRunAsAdmin())
        {
            // 以管理员权限重新启动应用程序
            var exeName = Process.GetCurrentProcess().MainModule?.FileName;
            if (exeName != null)
            {
                var startInfo = new ProcessStartInfo(exeName)
                {
                    UseShellExecute = true,
                    Verb = "runas" // 提升权限
                };
                try
                {
                    Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex);
                    MessageBox.Show("需要管理员权限才能运行此应用程序。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                Current.Shutdown();
                return;
            }
            else
            {
                LogManager.Error(new Exception("无法获取程序路径，无法以管理员权限重启"));
                MessageBox.Show("配置要求以管理员身份运行，但无法获取程序路径。\n请手动右键以管理员身份运行。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

#if !DEBUG
        // 检查单实例（在 Web 服务器启动之前，避免第二实例端口冲突）
        _mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            // 应用已在运行，激活现有实例并退出
            ActivateExistingWindow();
            Current.Shutdown();
            return;
        }
#endif

        WebApiStartup.Run(e.Args);

        base.OnStartup(e);

        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // 某些系统上设置进程优先级可能失败（非管理员、Group Policy 限制等）
        }

        // 处理未捕获的异常
        DispatcherUnhandledException += (sender, args) =>
        {
            LogManager.Error(args.Exception);
            var owner = Current.MainWindow;
            if (owner != null)
            {
                MessageBox.Show(owner, args.Exception.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(args.Exception.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            args.Handled = true;
        };

        // 处理非UI线程异常
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            LogManager.Error((Exception)args.ExceptionObject);
        };
    }
#pragma warning restore CS0162

    protected override void OnExit(ExitEventArgs e)
    {
        // 释放 MainViewModel 中的定时器和性能计数器（确定性释放，避免终结器线程风险）
        if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
        {
            try
            {
                vm.Dispose();
            }
            catch (Exception ex)
            {
                LogManager.Error(ex);
            }
        }

        // 退出时同步落盘并释放三个索引服务（Dispose 内含退出前 flush）
        foreach (var svc in new Masuit.Tools.Systems.Disposable[] { Services.ImageIndexService.Instance, Services.VideoIndexService.Instance, Services.OrbFeatureService.Instance })
        {
            try
            {
                svc.Dispose();
            }
            catch (Exception ex)
            {
                LogManager.Error(ex);
            }
        }

        base.OnExit(e);
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Task.Run(() => WebApiStartup.Stop()).Wait(TimeSpan.FromSeconds(5));
    }

    public static bool IsRunAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void ActivateExistingWindow()
    {
        try
        {
            // 查找现有的应用程序进程
            using var currentProcess = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(currentProcess.ProcessName);

            if (processes.Length > 1)
            {
                // 找到其他实例，激活其主窗口
                var existingProcess = processes.FirstOrDefault(p => p.Id != currentProcess.Id);
                if (existingProcess != null)
                {
                    try
                    {
                        var mainWindowHandle = existingProcess.MainWindowHandle;
                        if (mainWindowHandle != IntPtr.Zero)
                        {
                            // 显示窗口
                            if (NativeMethods.IsIconic(mainWindowHandle))
                            {
                                NativeMethods.ShowWindow(mainWindowHandle, 9); // 恢复窗口
                            }

                            // 激活窗口
                            NativeMethods.SetForegroundWindow(mainWindowHandle);
                            NativeMethods.BringWindowToTop(mainWindowHandle);
                        }
                    }
                    finally
                    {
                        // 释放非当前进程的 Process 对象
                        foreach (var p in processes)
                        {
                            if (p.Id != currentProcess.Id) p.Dispose();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(ex);
        }
    }
}

// Windows API 互操作
public static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);
}