namespace SmartFilling.Engine.Engine;

/// <summary>
/// R2-D（a-a-4 批次 E，2026-07-10 用户拍板选 A）：iframe 链断裂专用异常。
/// DetectEvaluator selector 类 detect 评估前调 FrameResolver.ValidateChainAsync 逐层校验链完整性，
/// 某层 count==0（链断裂）抛本异常 -> 穿透 DetectEvaluator catch（when 过滤）-> check/condition/wait onError 触发（根治 detect③ dead write）。
/// 非 selector 类 detect（iframe_exists/page_contains/document_ready 等）不校验（链断 false 合理/勉强/不依赖 frame）。
/// IsBrowserCrashException 不含本异常关键词（closed/disconnected/Target closed/Browser closed），不 rethrow，正常进 retry->onError。
/// </summary>
public class IframeChainBrokenException : Exception
{
    public IframeChainBrokenException(string message) : base(message) { }
}
