namespace SmartFilling.App.Recording;

/// <summary>
/// P3-9：前置脚本执行失败异常。RecordingEngine 原把前置脚本失败包装成 InvalidOperationException 被 catch 吞走、
/// 返回部分脚本伪装录制成功。改为抛此专用异常外抛→RecordController Failed→前端"录制失败"如实上报。
/// </summary>
public class PrerequisiteScriptFailedException : Exception
{
    public PrerequisiteScriptFailedException(string message) : base(message) { }
}
