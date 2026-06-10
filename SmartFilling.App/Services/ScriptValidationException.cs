namespace SmartFilling.App.Services;

/// <summary>
/// 脚本校验异常：SaveScript(forceSave=false) 业务校验失败时抛出，承载错误列表供 Controller 返回前端。
/// RecordController.Save catch→BadRequest{validationFailed=true, errors}→app.js confirm 是否强制保存→forceSave=true 重发跳校验。
/// 对齐 RecordingCancelledException 风格（专用异常外抛，避免被吞成 HTTP 500 / silent-success；前端只在日志记「保存失败」用户不知为何）。
/// </summary>
public class ScriptValidationException : Exception
{
    /// <summary>业务校验错误列表（ScriptLoader.ValidateAndGetErrors 产出，供 Controller 透传前端）</summary>
    public IReadOnlyList<string> Errors { get; }

    public ScriptValidationException(IReadOnlyList<string> errors)
        : base($"脚本校验失败: {errors.Count} 项错误")
        => Errors = errors;
}
