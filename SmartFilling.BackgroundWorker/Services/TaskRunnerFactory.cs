using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using Microsoft.Extensions.Options;
using SmartFilling.Engine.Models;

namespace SmartFilling.BackgroundWorker.Services;

public class TaskRunnerFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly EngineILogger _logger;
    private readonly IOptions<EngineOptions> _engineOptions;

    public TaskRunnerFactory(IServiceProvider serviceProvider, IConfiguration configuration, EngineILogger logger, IOptions<EngineOptions> engineOptions)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _engineOptions = engineOptions;
    }

    public ScriptEngineRunner Create()
    {
        var reporterFactory = _serviceProvider.GetRequiredService<IProgressReporterFactory>();

        // J.1.2：EngineOptions 自动绑定（和 App 一致），修"每次加配置改两处"双轨技术债（字段以 EngineOptions.cs 为准；MaxRecordTurns 已迁 AppOptions、CaptchaServiceUrl 已删）
        var engineOptions = _engineOptions.Value;

        // AI Options（AiProvider 独立段，不在 EngineOptions）
        AiOptions? aiOptions = null;
        var apiKey = _configuration["AiProvider:ApiKey"];
        if (!string.IsNullOrEmpty(apiKey))
        {
            aiOptions = new AiOptions
            {
                ApiKey = apiKey,
                ModelId = _configuration["AiProvider:ModelId"] ?? "deepseek-v3.2",
                Endpoint = _configuration["AiProvider:Endpoint"],
                CircuitBreakerThreshold = _configuration.GetValue("AiProvider:CircuitBreakerThreshold", 5),
            };
        }

        var captchaService = _serviceProvider.GetService<SmartFilling.Engine.Services.CaptchaService>();
        var orchestrationService = _serviceProvider.GetService<TaskOrchestrationService>();

        return new ScriptEngineRunner(_logger, reporterFactory, _configuration, engineOptions, aiOptions, captchaService, orchestrationService);
    }
}
