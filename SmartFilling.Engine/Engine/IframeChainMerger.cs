namespace SmartFilling.Engine.Engine;

/// <summary>
/// 形态 A（2026-07-02）：iframe selector 链继承合并语义（D3 选 b）。
/// 绝对链，非前缀拼接。语义=??（集中入口，便于维护）。
/// - step != null → 用 step（含 [] = 显式主文档，不继承 phase）
/// - step == null → 继承 phase（未配）
/// T9（a-a-4 L4544，2026-07-08 反转原约定）：录制端主文档统一产 []（显式不继承 phase）；null=继承 phase（手写省略/未配）。
/// 回放 Resolve 语义不变（[]!=null→用 []；null→继承 phase）。phase.iframe 恒 null 现状下录制产 [] vs null 回放相同（都主文档）；未来 phase.iframe 修复后差异生效。
/// </summary>
public static class IframeChainMerger
{
    public static string[]? Resolve(string[]? stepIframe, string[]? phaseIframe)
        => stepIframe != null ? stepIframe : phaseIframe;
}
