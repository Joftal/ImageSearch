using Masuit.Tools.Files;
using Masuit.Tools.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using System.IO;
using System.Reflection;

namespace 以图搜图.WebAPI;

public class WebApiStartup
{
    private static WebApplication? _application;
    private static Task? _serverTask;
    public static bool ServerRunning { get; set; }

    public static Task Run(params string[] args)
    {
        var config = new IniFile(DataPath.Get("config.ini"));
        var runServer = config.GetValue("Global", "RunServer", false);
        if (!runServer)
        {
            return Task.CompletedTask;
        }

        var apiKey = config.GetValue("Global", "ApiKey", "");
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "以图搜图 - 本地图像检索工具WPF版 by 懒得勤快 (评估版本)",
                Version = "v1"
            });
            // 设置 XML 注释文件路径；发布/裁剪后文件可能缺失，IncludeXmlComments 会急切加载并抛异常，必须守卫
            var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFilename);
            if (File.Exists(xmlPath))
            {
                c.IncludeXmlComments(xmlPath);
            }
        });
        // 不配置 CORS：本工具仅监听回环地址，无需跨域访问。
        // 移除 AllowAnyOrigin 防止恶意网页通过 JavaScript 跨域调用本地 API。
        var app = builder.Build();
        _application = app;

        // API Key 鉴权中间件：config.ini 中配置了 ApiKey 时启用
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            app.Use(async (context, next) =>
            {
                // Swagger/OpenAPI 文档端点免鉴权
                var path = context.Request.Path.Value ?? "";
                if (path.StartsWith("/openapi") || path == "/api" || path == "/api/")
                {
                    await next();
                    return;
                }

                if (!context.Request.Headers.TryGetValue("X-API-Key", out var provided) || !TimingSafeEquals(provided.ToString(), apiKey))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("Unauthorized: missing or invalid API key. Set X-API-Key header.");
                    return;
                }

                await next();
            });
        }

        app.UseSwagger(options => options.RouteTemplate = "/openapi/{documentName}.json");
        app.MapScalarApiReference("/api");
        app.MapControllers();
        // 本地工具默认只监听回环地址，避免无鉴权的索引/搜索接口暴露到局域网
        var port = config.GetValue("Global", "HttpPort", 5000);
        try
        {
            // 与实际监听行为保持一致：启动前显式指定回环地址
            app.Urls.Clear();
            app.Urls.Add($"http://127.0.0.1:{port}");
            // StartAsync 会等 Kestrel 完成绑定，端口占用等启动错误在此同步抛出。
            // （原实现直接 RunAsync 绑错时异常落在无人观察的 Task 内，状态灯常绿误报运行中）
            app.StartAsync().GetAwaiter().GetResult();
            _serverTask = app.WaitForShutdownAsync();
            ServerRunning = true;
            // 运行期宿主异常也不能静默：记录并复位状态
            _serverTask.ContinueWith(t =>
            {
                ServerRunning = false;
                LogManager.Error(new Exception("WebAPI 服务运行期异常退出", t.Exception));
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception e) when (e is System.IO.IOException or System.Net.Sockets.SocketException)
        {
            LogManager.Error(new Exception($"WebAPI 端口 {port} 被占用，WebAPI 服务未启动: {e.Message}", e));
            ServerRunning = false;
            app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            _application = null; // host 已随失败路径释放，避免 Stop() 里二次 DisposeAsync
        }
        return _serverTask ?? Task.CompletedTask;
    }

    public static async Task Stop()
    {
        if (_application is not null)
        {
            try
            {
                await _application.StopAsync();
            }
            catch (Exception) { }
            await _application.DisposeAsync();
        }
    }

    private static bool TimingSafeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}