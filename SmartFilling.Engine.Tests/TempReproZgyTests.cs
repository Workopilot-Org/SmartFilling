using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Tests.Unit;

public class TempReproZgyTests
{
    [Fact]
    public void LoadZgy_AfterSavePhaseFix()
    {
        var root = AppContext.BaseDirectory;
        for (int i = 0; i < 5; i++) root = Directory.GetParent(root)!.FullName;
        var json = File.ReadAllText(Path.Combine(root, "SmartFilling.App", "data", "scripts", "zgy-cg-registration-add.json"));
        // 生产路径：LoadFromJson 内部跑 schema + ValidateAndGetErrors，失败抛 InvalidOperationException
        var script = ScriptLoader.LoadFromJson(json, lenientNull: true);
        var save = script.Phases.OfType<PhaseNode>().First(p => p.Name == "save");
        var stepNames = save.Steps.Select(s => ((StepNode)s).Name).ToList();
        Console.WriteLine($"save steps: {string.Join(", ", stepNames)}");
        // save phase 4 steps：clickSave / waitSaved / waitSavedMs（固定等待） / extractBillCode
        // （zgy 脚本演进加了 waitSavedMs 固定等待，原期望 3 未算；非本次改动引起——git diff zgy 脚本空）
        Assert.Equal(4, stepNames.Count);
        Assert.Contains("clickSave", stepNames);
        Assert.Contains("waitSaved", stepNames);
        Assert.Contains("waitSavedMs", stepNames);
        Assert.Contains("extractBillCode", stepNames);
        Assert.DoesNotContain("checkSuccess", stepNames);
        Assert.DoesNotContain("checkFail", stepNames);
    }
}
