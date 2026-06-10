namespace SmartFilling.BackgroundWorker.Configuration;

public class WorkerOptions
{
    public string TaskMode { get; set; } = "HTTP";
    public int MaxConcurrentTasks { get; set; } = 1;
    public string? BaseUrl { get; set; }
    public string[] CorsOrigins { get; set; } = ["*"];

    /// <summary>加载平台脚本时是否宽松校验（Schema 校验前移除 null 字段）。默认 true：Worker 消费外部脚本，宽容优先。</summary>
    public bool IgnoreNullFieldsOnScriptValidation { get; set; } = true;
}
