namespace SmartFilling.App.Configuration;

public class AppOptions
{
    /// <summary>录制脚本输出时是否包含所有字段（含 null）。默认 false，只输出有值字段。</summary>
    public bool RecordOutputAllFields { get; set; } = false;

    /// <summary>录制每个交互操作后的等待时间（ms），让页面动画/异步渲染稳定后再继续（原硬编码 500ms）。录制专属配置（选C：从 EngineOptions 迁入，仅 App 录制消费）。</summary>
    public int RecordActionDelay { get; set; } = 500;

    /// <summary>录制 AI 循环的最大轮数上限，防录制死循环。录制专属配置（选C：从 EngineOptions 迁入）。</summary>
    public int MaxRecordTurns { get; set; } = 100;
}
