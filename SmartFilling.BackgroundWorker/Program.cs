using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using SmartFilling.BackgroundWorker;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Services;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Services;
using SmartFilling.Engine.Engine;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// ========== Engine 日志适配 ==========
builder.Services.AddSingleton<EngineILogger>(new SerilogLoggerAdapter(Log.Logger));

// ========== 公共服务 ==========
// #13/O.3.1：注册 ObjectToClrConverter，把 FillData 的 object 还原为 CLR 托管对象（原 MVC 默认绑定为 JsonElement→AttachmentService 嵌套附件遍历全 miss）
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new ObjectToClrConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CaptchaService（使用 Engine 共享类；CaptchaController HTTP 端点已随协议剥离砍删，仅保留 Service 注册——回放 captcha action 走 Engine 内部 CaptchaService 直连 Python 8000）
builder.Services.AddSingleton<CaptchaService>(sp =>
{
    var url = builder.Configuration["CaptchaService:Url"] ?? "http://localhost:8000";
    return new CaptchaService(new CaptchaServiceOptions { Url = url });
});

// HttpClient Singleton（AI API / 平台 API / 回调推送共享，TCP/TLS 连接复用）
builder.Services.AddSingleton<System.Net.Http.HttpClient>(sp =>
{
    var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
    return new System.Net.Http.HttpClient(handler);
});

// 共享服务
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("ScriptEngine"));  // J.1.2：EngineOptions 自动绑定（和 App 一致）
// 附件根目录对齐：UploadRootPath 为空时注入 Worker ContentRootPath，确保附件下载保存（AttachmentService）与 upload action 查找（StepExecutor）用同一根目录
builder.Services.PostConfigure<EngineOptions>(opt =>
{
    if (string.IsNullOrEmpty(opt.UploadRootPath))
        opt.UploadRootPath = builder.Environment.ContentRootPath;
});
var workerConfig = builder.Configuration.GetSection("WorkerOptions").Get<WorkerOptions>() ?? new WorkerOptions();
builder.Services.AddSingleton(workerConfig);
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("WorkerOptions"));
builder.Services.AddSingleton<TaskConcurrencyManager>();
builder.Services.AddSingleton<TaskRunnerFactory>();
builder.Services.AddSingleton<TaskOrchestrationService>();  // TaskExecutionService 的依赖（多脚本编排 + 任务级 CTS 管理）
builder.Services.AddSingleton<TaskExecutionService>();

// ========== 协议剥离后单一 HTTP 模式（删双轨 if + TaskMode + AddSignalR/MapHub FillHub） ==========
builder.Services.AddSingleton<TaskResultStore>();           // 终态 JSONL 持久化（GET 地基，决策7）
builder.Services.AddSingleton<InMemoryProgressStore>();     // 进度内存字典（pull 模式 GET 查，第3轮-C7）
builder.Services.AddSingleton<TaskActivityTracker>();       // 任务活动跟踪（/health?taskId + App/PlatformAdapter 健康检测，G1-11）
builder.Services.AddSingleton<IProgressReporterFactory, HttpProgressReporterFactory>();  // 替代 SignalRProgressReporterFactory（FillHub 砍后不再用 SignalR 推送）
builder.Services.AddSingleton<ITaskCompletionHandler, HttpTaskCompletionHandler>();

// CORS
// ["*"] 或含 "*" 表示不校验（AllowAnyOrigin）；否则按具体 origin 白名单 + AllowCredentials。
var corsOrigins = workerConfig.CorsOrigins ?? [];
var allowAnyOrigin = Array.IndexOf(corsOrigins, "*") >= 0;
Log.Information("CORS 策略: AllowAnyOrigin={AllowAny}, Origins=[{Origins}], 长度={Len}", allowAnyOrigin, string.Join(",", corsOrigins), corsOrigins.Length);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowAnyOrigin)
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        else
            policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
    });
});

Log.Information("服务启动中，模式: HTTP Server");

// ========== 构建 ==========
var app = builder.Build();

// schema 降级检查（决策1）：Engine 层 ScriptLoader 无 Serilog 依赖，加载失败时仅 Console.Error 即时输出 + 暴露 SchemaLoadFailed 标志；宿主（Worker）启动时用 Serilog 持久化到文件日志，运维可查
if (ScriptLoader.SchemaLoadFailed)
    Log.Logger.Error("脚本 Schema (script-v2.json) 加载失败——schema 校验已降级禁用。Worker 填报 LoadFromJson 将抓不到未知字段（可能 silent 放过病态脚本），请检查 SmartFilling.Engine 嵌入资源配置。");

// R2-8：启用 ForwardedHeaders——反代（Nginx 等）下从 X-Forwarded-Host/Proto 还原浏览器原始地址，
// 使 Request.Host/Scheme 反映浏览器视角（health/swagger 等 URL 用 Request.Host 构造，反代下否则是内部地址网关连不上）。
// 必须在 UseRouting/UseCors 之前。默认仅信任 localhost 代理；跨机反代需配置 KnownProxies/KnownNetworks（见 ForwardedHeadersOptions）。
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseRouting(); // CORS 默认策略需在端点路由之后评估；缺此行则跨域响应头不被添加
app.UseCors();
app.MapControllers();

app.MapGet("/", (HttpContext ctx) =>
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Ok(new
    {
        service = "SmartFilling Background Worker",
        mode = "HTTP Server",  // WS 模式已移除（协议剥离），单一 HTTP
        status = "running",
        health = $"{baseUrl}/health",  // FillController health 路由改为 ~/health
        swagger = $"{baseUrl}/swagger"
    });
});

app.Run();

// Serilog → Engine ILogger 适配
file record SerilogLoggerAdapter(Serilog.ILogger Log) : EngineILogger
{
    public void LogDebug(string message) => Log.Debug("{Message}", message);
    public void LogInfo(string message) => Log.Information("{Message}", message);
    public void LogInformation(string message) => Log.Information("{Message}", message);
    public void LogWarning(string message) => Log.Warning("{Message}", message);
    public void LogWarning(Exception ex, string message) => Log.Warning(ex, "{Message}", message);
    public void LogError(string message) => Log.Error("{Message}", message);
}
