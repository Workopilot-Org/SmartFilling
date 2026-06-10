using Serilog;
using SmartFilling.App.Configuration;
using SmartFilling.App.Hubs;
using SmartFilling.App.Services;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using SmartFilling.Engine.Services;
using SmartFilling.Engine.Engine;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// ========== 服务注册 ==========

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

// Engine 日志适配
builder.Services.AddSingleton<EngineILogger>(new SerilogLoggerAdapter(Log.Logger));

// 配置
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("WorkerOptions"));
builder.Services.Configure<AiProviderOptions>(builder.Configuration.GetSection("AiProvider"));
builder.Services.Configure<PlatformOptions>(builder.Configuration.GetSection("Platform"));
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("ScriptEngine"));
// 附件根目录对齐：UploadRootPath 为空时注入 App ContentRootPath，确保 App 保存附件（AttachmentService）与前置脚本 upload action 查找（StepExecutor via ScriptEngine）用同一根目录
builder.Services.PostConfigure<EngineOptions>(opt =>
{
    if (string.IsNullOrEmpty(opt.UploadRootPath))
        opt.UploadRootPath = builder.Environment.ContentRootPath;
});
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("AppOptions"));

// 服务
builder.Services.AddSingleton<ScriptService>();
builder.Services.AddSingleton<AttachmentService>();
builder.Services.AddSingleton<CaptchaService>(sp =>
{
    var url = builder.Configuration["CaptchaService:Url"] ?? "http://localhost:8000";
    return new CaptchaService(new CaptchaServiceOptions { Url = url });
});
builder.Services.AddSingleton<WorkerApiClient>();

// HttpClient Singleton（AI API 和平台 API 共享，TCP/TLS 连接复用）
builder.Services.AddSingleton<System.Net.Http.HttpClient>(sp =>
{
    var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
    return new System.Net.Http.HttpClient(handler);
});
builder.Services.AddSingleton<AttachmentClassificationService>();
builder.Services.AddSingleton<TaskOrchestrationService>();

// IAiProvider 注册（供 RecordingEngine 使用）
builder.Services.AddSingleton<IAiProvider>(sp =>
{
    var aiOpts = builder.Configuration.GetSection("AiProvider").Get<AiProviderOptions>();
    var logger = sp.GetRequiredService<EngineILogger>();
    if (aiOpts == null || string.IsNullOrEmpty(aiOpts.ApiKey))
        throw new InvalidOperationException("AiProvider:ApiKey 未配置");
    return new OpenAiProvider(new AiOptions { ApiKey = aiOpts.ApiKey, ModelId = aiOpts.ModelId, Endpoint = aiOpts.Endpoint }, logger);
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "http://localhost:3000",
                "http://localhost:5000",
                "http://localhost:5152",
                "http://localhost:5666"
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

Log.Information("App 服务启动中");

// ========== 构建 ==========

var app = builder.Build();

// schema 降级检查（决策1）：Engine 层 ScriptLoader 无 Serilog 依赖（用 ILogger 抽象解耦），加载失败时仅 Console.Error 即时输出 + 暴露 SchemaLoadFailed 标志；宿主（App）启动时用 Serilog 持久化到文件日志（logs/app-.log），运维可查
if (ScriptLoader.SchemaLoadFailed)
    Log.Logger.Error("脚本 Schema (script-v2.json) 加载失败——schema 校验已降级禁用。含未知字段（旧字段名/拼错如 ifrmae）的脚本将不会被校验（前置不报错/列表不标⚠️/保存不拒绝），请检查 SmartFilling.Engine 嵌入资源配置。");

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();

// 静态文件指向 WebUI 目录
var webUiPath = Path.Combine(builder.Environment.ContentRootPath, "..", "SmartFilling.WebUI");
if (Directory.Exists(webUiPath))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webUiPath) });
else
    app.UseStaticFiles();

// 附件静态文件服务：Worker（HTTP 模式）通过 http://host/uploads/xxx 下载附件，
// 须 serve uploads 目录（与 AttachmentService 落盘同源：UploadRootPath ?? ContentRootPath）。
// 否则 /uploads/xxx.png 走不到静态文件 + MapFallback 的 nonfile 约束因扩展名排除 -> 404。
var uploadRootPath = builder.Configuration["ScriptEngine:UploadRootPath"];
var uploadRoot = string.IsNullOrEmpty(uploadRootPath) ? builder.Environment.ContentRootPath : uploadRootPath;
var uploadPath = Path.Combine(uploadRoot, "uploads");
if (Directory.Exists(uploadPath))
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadPath),
        RequestPath = "/uploads"
    });

app.MapControllers();
app.MapHub<AgentHub>("/hubs/agent");

// SPA fallback：非 API/Hub 请求返回 index.html
app.MapFallback(async (HttpContext ctx) =>
{
    var indexPath = Path.Combine(webUiPath, "index.html");
    if (File.Exists(indexPath))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(indexPath);
    }
    else
    {
        ctx.Response.StatusCode = 404;
    }
});

app.Run();

file record SerilogLoggerAdapter(Serilog.ILogger Log) : EngineILogger
{
    public void LogDebug(string message) => Log.Debug("{Message}", message);
    public void LogInfo(string message) => Log.Information("{Message}", message);
    public void LogInformation(string message) => Log.Information("{Message}", message);
    public void LogWarning(string message) => Log.Warning("{Message}", message);
    public void LogWarning(Exception ex, string message) => Log.Warning(ex, "{Message}", message);
    public void LogError(string message) => Log.Error("{Message}", message);
}
