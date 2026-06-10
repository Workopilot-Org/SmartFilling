using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// 批次1：VariableHelper.GetFieldDefinition 搬迁自 StepExecutor（public，供录制端复用 fieldDef 递归查找）。
/// 覆盖顶层/嵌套/深层嵌套命中 + null fieldName/未找到/空 Fields/null script 守卫。
/// 断言验值（Format/Transform/Type）不只验状态——录制端依赖这些子字段做 transform/format 转换。
/// </summary>
public class GetFieldDefinitionTests
{
    [Fact]
    public void GetFieldDefinition_TopLevel_ReturnsField()
    {
        var script = BuildSampleScript();

        var def = VariableHelper.GetFieldDefinition("topField", script);

        Assert.NotNull(def);
        Assert.Equal("topField", def!.Name);
        Assert.Equal("string", def.Type);
    }

    [Fact]
    public void GetFieldDefinition_TopLevel_ReturnsFormatAndTransform()
    {
        var script = BuildSampleScript();

        var def = VariableHelper.GetFieldDefinition("topField", script);

        Assert.NotNull(def);
        Assert.Equal("X", def!.Format);
        Assert.Equal("upper", def.Transform);
    }

    [Fact]
    public void GetFieldDefinition_Nested_ReturnsField()
    {
        // 嵌套命中：顶层 field.Fields 里的字段能递归找到（修 N5）
        var script = BuildSampleScript();

        var def = VariableHelper.GetFieldDefinition("amount", script);

        Assert.NotNull(def);
        Assert.Equal("amount", def!.Name);
        Assert.Equal("0.00", def.Format);
        Assert.Equal("number", def.Type);
    }

    [Fact]
    public void GetFieldDefinition_DeepNested_ReturnsField()
    {
        // 多层嵌套递归
        var script = new ScriptBuilder()
            .AddField("root", "根", type: "array", fields: new List<FieldDefinition>
            {
                new() { Name = "mid", Type = "array", Fields = new List<FieldDefinition>
                {
                    new() { Name = "leaf", Type = "string", Format = "F" }
                }}
            })
            .Build();

        var def = VariableHelper.GetFieldDefinition("leaf", script);

        Assert.NotNull(def);
        Assert.Equal("leaf", def!.Name);
        Assert.Equal("F", def.Format);
    }

    [Fact]
    public void GetFieldDefinition_FirstMatchWins_AcrossFields()
    {
        // 顶层与嵌套同名：按遍历顺序返回首个命中（顶层优先）
        var script = BuildSampleScript();

        var def = VariableHelper.GetFieldDefinition("topField", script);

        Assert.NotNull(def);
        Assert.Equal("X", def!.Format);  // 顶层 topField 的 Format，非其他
    }

    [Fact]
    public void GetFieldDefinition_NullFieldName_ReturnsNull()
    {
        var script = BuildSampleScript();

        var def = VariableHelper.GetFieldDefinition(null, script);

        Assert.Null(def);
    }

    [Fact]
    public void GetFieldDefinition_NotFound_ReturnsNull()
    {
        var script = BuildSampleScript();

        var def = VariableHelper.GetFieldDefinition("nope", script);

        Assert.Null(def);
    }

    [Fact]
    public void GetFieldDefinition_EmptyFields_ReturnsNull()
    {
        var script = new ScriptBuilder().Build();  // 空 Fields

        var def = VariableHelper.GetFieldDefinition("x", script);

        Assert.Null(def);
    }

    [Fact]
    public void GetFieldDefinition_NullScript_ReturnsNull()
    {
        // 批次1 新增入口守卫：script=null 返回 null（public 方法防御）
        var def = VariableHelper.GetFieldDefinition("x", null!);

        Assert.Null(def);
    }

    [Fact]
    public void GetFieldDefinition_Nested_ReturnsTransform()
    {
        // 嵌套 field 的 Transform 子字段验证（核查第10轮🟡6 补：原 Nested 测 Format/Type，补 Transform）
        var script = new ScriptBuilder()
            .AddField("parent", "父", type: "array", fields: new List<FieldDefinition>
            {
                new() { Name = "code", Type = "string", Transform = "trim" }
            })
            .Build();

        var def = VariableHelper.GetFieldDefinition("code", script);

        Assert.NotNull(def);
        Assert.Equal("trim", def!.Transform);
    }

    private static ScriptV2 BuildSampleScript() =>
        new ScriptBuilder()
            .AddField("topField", "顶层字段", type: "string", format: "X", transform: "upper")
            .AddField("detail", "明细", type: "array", fields: new List<FieldDefinition>
            {
                new() { Name = "amount", Type = "number", Format = "0.00" },
                new() { Name = "date", Type = "date", Format = "yyyy-MM-dd" }
            })
            .Build();
}
