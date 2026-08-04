using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// 步骤间固定等待配置解析测试（纯函数，直接调 ScriptEngine.ResolveStepInterval）。
/// 取值优先级：script.Settings.StepIntervalMs > EngineOptions.StepIntervalMs。
/// 解析层只做"取值"，不做守卫（负数原样透传）；守卫在 ApplyStepIntervalAsync 的 interval&lt;=0（见 StepIntervalIntegrationTests）。
/// </summary>
public class StepIntervalConfigTests
{
    private static ScriptV2 BuildScript(ScriptSettings? settings = null) => new()
    {
        ScriptId = "t",
        Name = "t",
        Phases = [],
        Settings = settings
    };

    [Fact]
    public void ConfigOnly()
    {
        // 仅配置级 1000，脚本级 null → 1000
        var script = BuildScript();
        var options = new EngineOptions { StepIntervalMs = 1000 };
        Assert.Equal(1000, ScriptEngine.ResolveStepInterval(script, options));
    }

    [Fact]
    public void ScriptOverrideConfig()
    {
        // 脚本级 2000 覆盖配置级 500 → 2000
        var script = BuildScript(new ScriptSettings { StepIntervalMs = 2000 });
        var options = new EngineOptions { StepIntervalMs = 500 };
        Assert.Equal(2000, ScriptEngine.ResolveStepInterval(script, options));
    }

    [Fact]
    public void ConfigZero_Disabled()
    {
        // 配置级 0（默认关闭），脚本级 null → 0
        var script = BuildScript();
        var options = new EngineOptions { StepIntervalMs = 0 };
        Assert.Equal(0, ScriptEngine.ResolveStepInterval(script, options));
    }

    [Fact]
    public void ScriptExplicitZero()
    {
        // 脚本级显式 0 覆盖配置级 500 → 0（明确关闭，不被配置级 500 覆盖）
        var script = BuildScript(new ScriptSettings { StepIntervalMs = 0 });
        var options = new EngineOptions { StepIntervalMs = 500 };
        Assert.Equal(0, ScriptEngine.ResolveStepInterval(script, options));
    }

    [Fact]
    public void Negative_PassesThrough()
    {
        // 解析层原样透传负数；下游 ApplyStepIntervalAsync 的 interval<=0 守卫拦截（见集成测试 Negative_IntervalsSkipped）
        var script = BuildScript();
        var options = new EngineOptions { StepIntervalMs = -100 };
        Assert.Equal(-100, ScriptEngine.ResolveStepInterval(script, options));
    }
}
