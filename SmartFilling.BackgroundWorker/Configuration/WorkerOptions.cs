namespace SmartFilling.BackgroundWorker.Configuration;

public class WorkerOptions
{
    public int MaxConcurrentTasks { get; set; } = 1;
    public string[] CorsOrigins { get; set; } = ["*"];

    /// <summary>加载平台脚本时是否宽松校验（Schema 校验前移除 null 字段）。默认 true：Worker 消费外部脚本，宽容优先。</summary>
    public bool IgnoreNullFieldsOnScriptValidation { get; set; } = true;

    /// <summary>附件下载超时秒（B3：从 PlatformOptions.DefaultTimeoutSeconds 迁移；Worker 侧 PlatformOptions 删除后统一用此）。</summary>
    public int AttachmentDownloadTimeoutSeconds { get; set; } = 60;

    /// <summary>终态持久化（决策7：单文件 JSONL，定时 compact）。</summary>
    public TaskResultStoreOptions TaskResultStore { get; set; } = new();

    /// <summary>进度推送配置（第3轮-C2：Enabled 包装 {Push, Pull}，Push 与 PushOptions 不同层级无冲突）。</summary>
    public ProgressReportOptions ProgressReport { get; set; } = new();

    /// <summary>终态推送配置（决策8：Retry 子对象对齐 StepRetry 字段，但新建 WebhookRetry 语义独立）。</summary>
    public CompleteReportOptions CompleteReport { get; set; } = new();
}

public class TaskResultStoreOptions
{
    /// <summary>JSONL 终态文件路径（相对 ContentRoot）。</summary>
    public string FilePath { get; set; } = "data/task-results.jsonl";
    /// <summary>终态保留天数（决策9）。</summary>
    public int RetentionDays { get; set; } = 7;
    /// <summary>清理扫描间隔（小时，决策9）。</summary>
    public int CleanupIntervalHours { get; set; } = 6;
}

public class ProgressReportOptions
{
    /// <summary>Enabled 包装：Push 推模式总开关（产生进度时 POST 到 progressCallbackUrl，callbackUrl 非空且此开关 true 才推）；Pull 拉模式总开关（写 InMemoryProgressStore 供 GET 查）。</summary>
    public ProgressEnabledOptions Enabled { get; set; } = new();
    /// <summary>采集/推送类型开关。</summary>
    public TypesOptions Types { get; set; } = new();
    /// <summary>push HTTP 超时配置。</summary>
    public ProgressPushOptions PushOptions { get; set; } = new();
}

public class ProgressEnabledOptions
{
    /// <summary>推模式总开关：产生进度时 POST 到 progressCallbackUrl（callbackUrl 非空且此开关 true 才推，第3轮-C2）。</summary>
    public bool Push { get; set; }
    /// <summary>拉模式总开关：写 InMemoryProgressStore 供 GET 查（B7）。</summary>
    public bool Pull { get; set; } = true;
}

public class TypesOptions
{
    public bool Log { get; set; }
    public bool Screenshot { get; set; }
    public bool Completed { get; set; }
}

public class ProgressPushOptions
{
    public int TimeoutSeconds { get; set; } = 5;
}

public class CompleteReportOptions
{
    /// <summary>Enabled 包装：Push 推模式总开关（终态 POST 到 completeCallbackUrl，callbackUrl 非空且此开关 true 才推）；Pull 拉模式总开关（写 JSONL 供 GET 查）。</summary>
    public CompleteEnabledOptions Enabled { get; set; } = new();
    /// <summary>push HTTP 超时 + 重试配置。</summary>
    public CompletePushOptions PushOptions { get; set; } = new();
}

public class CompleteEnabledOptions
{
    /// <summary>推模式总开关：终态 POST 到 completeCallbackUrl（callbackUrl 非空且此开关 true 才推，第3轮-C2）。</summary>
    public bool Push { get; set; }
    /// <summary>拉模式总开关：写 JSONL 供 GET 查（B7）。</summary>
    public bool Pull { get; set; } = true;
}

public class CompletePushOptions
{
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>终态推送失败重试（决策8/B10：新建 WebhookRetry 语义独立，字段对齐 StepRetry 但不复用其类型）。</summary>
    public WebhookRetry Retry { get; set; } = new();
}

/// <summary>HTTP 出站重试配置（Webhook/回调）。语义独立于引擎步骤重试（StepRetry），字段同形（Count/Interval）。</summary>
public class WebhookRetry
{
    public int Count { get; set; } = 3;
    public int Interval { get; set; } = 1000;
}