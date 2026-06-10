using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// #1 document_ready detect 类型测试（ScriptLoader 校验，经生产反序列化路径 LoadFromJson JSON）。
/// 决策2-A（a-a-4，T4 选X）：document_ready iframe=null 合法（=继承 phase.iframe；现状 phase.iframe 恒 null→顶层）。
/// 删 ScriptLoader document_ready Iframe==null 校验后，省略 iframe 不再拒。DetectEvaluator 评估测试受 Moq 表达式树限制
/// （Playwright ILocator.EvaluateAsync&lt;T&gt; 是 params，表达式树 CS0854 无法 Setup）——评估由 golden E2E（真实 Chromium）+ 代码审查覆盖。
/// 断言验值不只验状态：省略 iframe→通过（继承）；[]/[链]→通过；iframeRef-only→通过（结论14 iframeRef 可独立工作）。
/// </summary>
public class DocumentReadyTests
{
    private const string BaseJson = """
        {{
          "version": 2, "scriptId": "test", "name": "test",
          "phases": [
            {{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "测试", "steps": [
              {{ "kind": "step", "name": "waitReady", "action": "wait", "description": "等加载完成",
                "until": {0}, "timeout": 1000 }}
            ]}}
          ]
        }}
        """;

    [Fact]
    public void Loader_DocumentReady_IframeOmitted_PassesAsInherit()
    {
        // 决策2-A（T4 选X）：iframe 省略 = null → 合法（=继承 phase.iframe，对齐 step iframe 三态 D3）；不再拒。
        // 经生产 LoadFromJson 反序列化路径：null Iframe 加载成功 + 脚本名正确（断言验值不只验状态）。
        var json = string.Format(BaseJson, "{ \"type\": \"document_ready\" }");
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("test", script.Name);
    }

    [Fact]
    public void Loader_DocumentReady_IframeEmptyArray_Passes()
    {
        // [] = 显式主文档，合法
        var json = string.Format(BaseJson, "{ \"type\": \"document_ready\", \"iframe\": [] }");
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("test", script.Name);
    }

    [Fact]
    public void Loader_DocumentReady_IframeChain_Passes()
    {
        // [链] = 指定 iframe，合法
        var json = string.Format(BaseJson, "{ \"type\": \"document_ready\", \"iframe\": [\"iframe[src*='dialog']\"] }");
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("test", script.Name);
    }

    [Fact]
    public void Loader_DocumentReady_NestedInAll_OmittedIframe_PassesAsInherit()
    {
        // 决策2-A（T4 选X）：document_ready 嵌在 all 组合里 + iframe 省略 → 也合法（递归 ValidateDetectParams 不再拒 null）。
        var json = string.Format(BaseJson, "{ \"all\": [ { \"type\": \"document_ready\" } ] }");
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("test", script.Name);
    }

    [Fact]
    public void Loader_DocumentReady_IframeRefOnly_Passes()
    {
        // 结论14（a-a-4）：iframe 省略但传 iframeRef → 合法（iframeRef 维度可独立工作；录制端 ResolveIframeFromRefAsync 提取链，回放不消费 iframeRef）。
        // 经生产 LoadFromJson：iframeRef 字段不被 additionalProperties:false 拒（schema 已加）+ 不影响加载。
        var json = string.Format(BaseJson, "{ \"type\": \"document_ready\", \"iframeRef\": \"e6\" }");
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("test", script.Name);
    }
}
