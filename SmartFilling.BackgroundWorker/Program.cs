using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using SmartFilling.BackgroundWorker;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Hubs;
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

// ========== 模式判断 ==========
var workerConfig = builder.Configuration.GetSection("WorkerOptions").Get<WorkerOptions>() ?? new WorkerOptions();
var taskMode = workerConfig.TaskMode;
if (taskMode != "WS" && taskMode != "HTTP")
    throw new InvalidOperationException($"TaskMode 仅支持 \"WS\" 或 \"HTTP\"，当前值: \"{taskMode}\"");

// ========== Engine 日志适配 ==========
builder.Services.AddSingleton<EngineILogger>(new SerilogLoggerAdapter(Log.Logger));

// ========== 公共服务 ==========
// #13/O.3.1：注册 ObjectToClrConverter，把 FillData 的 object 还原为 CLR 托管对象（原 MVC 默认绑定为 JsonElement→AttachmentService 嵌套附件遍历全 miss）
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new ObjectToClrConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CaptchaService（使用 Engine 共享类）
builder.Services.AddSingleton<CaptchaService>(sp =>
{
    var url = builder.Configuration["CaptchaService:Url"] ?? "http://localhost:8000";
    return new CaptchaService(new CaptchaServiceOptions { Url = url });
});

// HttpClient Singleton（AI API 和平台 API 共享，TCP/TLS 连接复用）
builder.Services.AddSingleton<System.Net.Http.HttpClient>(sp =>
{
    var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
    return new System.Net.Http.HttpClient(handler);
});

// 共享服务
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("ScriptEngine"));  // J.1.2：EngineOptions 自动绑定（和 App 一致），修双轨技术债 + 漏读字段
// 附件根目录对齐：UploadRootPath 为空时注入 Worker ContentRootPath，确保附件下载保存（AttachmentService）与 upload action 查找（StepExecutor）用同一根目录（默认部署/--contentroot 覆盖均对齐）
builder.Services.PostConfigure<EngineOptions>(opt =>
{
    if (string.IsNullOrEmpty(opt.UploadRootPath))
        opt.UploadRootPath = builder.Environment.ContentRootPath;
});
builder.Services.AddSingleton(workerConfig);
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("WorkerOptions"));
builder.Services.AddSingleton<TaskConcurrencyManager>();
builder.Services.AddSingleton<TaskRunnerFactory>();
builder.Services.AddSingleton<TaskOrchestrationService>();  // TaskExecutionService 的依赖（多脚本编排 + 任务级 CTS 管理）
builder.Services.AddSingleton<TaskExecutionService>();

if (taskMode == "WS")
{
    // WS 模式
    builder.Services.Configure<WsClientOptions>(builder.Configuration.GetSection("WsClient"));
    builder.Services.Configure<PlatformOptions>(builder.Configuration.GetSection("Platform"));
    builder.Services.AddSingleton<AutomationWsClient>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomationWsClient>());
    builder.Services.AddSingleton<ScriptService>();
    builder.Services.AddSingleton<IProgressReporterFactory, WsProgressReporterFactory>();
    builder.Services.AddSingleton<ITaskCompletionHandler, WsTaskCompletionHandler>();
}
else
{
    // HTTP 模式
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<IProgressReporterFactory, SignalRProgressReporterFactory>();
    builder.Services.AddSingleton<ITaskCompletionHandler, HttpTaskCompletionHandler>();
}

// CORS
// ["*"] 或含 "*" 表示不校验（AllowAnyOrigin）；否则按具体 origin 白名单 + AllowCredentials。
// 注意：不能用 `origins is ["*"]` 判断——WithOrigins 会把 "*" 当字面 origin，真实浏览器 Origin 永不匹配→不加 CORS 头。
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

Log.Information("服务启动中，模式: {Mode}", taskMode);

// ========== 构建 ==========
var app = builder.Build();

// schema 降级检查（决策1）：Engine 层 ScriptLoader 无 Serilog 依赖，加载失败时仅 Console.Error 即时输出 + 暴露 SchemaLoadFailed 标志；宿主（Worker）启动时用 Serilog 持久化到文件日志，运维可查（Worker 填报 LoadFromJson 含 schema 校验，降级会致抓不到未知字段）
if (ScriptLoader.SchemaLoadFailed)
    Log.Logger.Error("脚本 Schema (script-v2.json) 加载失败——schema 校验已降级禁用。Worker 填报 LoadFromJson 将抓不到未知字段（可能 silent 放过病态脚本），请检查 SmartFilling.Engine 嵌入资源配置。");

// R2-8：启用 ForwardedHeaders——反代（Nginx 等）下从 X-Forwarded-Host/Proto 还原浏览器原始地址，
// 使 Request.Host/Scheme 反映浏览器视角（FillController workerHubUrl 用 Request.Host 构造，反代下否则是内部地址前端连不上）。
// 必须在 UseRouting/UseCors 之前。默认仅信任 localhost 代理；跨机反代需配置 KnownProxies/KnownNetworks（见 ForwardedHeadersOptions）。
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseRouting(); // CORS 默认策略需在端点路由之后评估；缺此行则跨域响应头不被添加（浏览器拦截 SignalR negotiate，前端报 Failed to complete negotiation）
app.UseCors();
app.MapControllers();

if (taskMode != "WS")
    app.MapHub<FillHub>("/hubs/fill");

app.MapGet("/", (HttpContext ctx) =>
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Ok(new
    {
        service = "SmartFilling Background Worker",
        mode = taskMode == "WS" ? "WS Client" : "HTTP Server",
        status = "running",
        health = $"{baseUrl}/api/fill/health",
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
