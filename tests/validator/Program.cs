// golden 脚本合法性校验：对每个 JSON 跑 ScriptLoader.LoadFromFile（schema + 业务校验，严格模式 lenientNull=false）
// 用法: dotnet run --project tests/validator [脚本目录，默认 tests/golden-scripts]
using SmartFilling.Engine.Engine;

var dir = args.Length > 0 ? args[0] : "tests/golden-scripts";
if (!Path.IsPathRooted(dir))
    dir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, dir));

if (!Directory.Exists(dir))
{
    Console.Error.WriteLine($"目录不存在: {dir}");
    return 2;
}

var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToArray();
if (files.Length == 0)
{
    Console.WriteLine($"(目录无 json: {dir})");
    return 0;
}

int fail = 0;
foreach (var f in files)
{
    var name = Path.GetFileName(f);
    try
    {
        ScriptLoader.LoadFromFile(f, lenientNull: false);
        Console.WriteLine($"OK   {name}");
    }
    catch (Exception ex)
    {
        fail++;
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine($"     {ex.Message.Replace("\n", "\n     ")}");
    }
}

Console.WriteLine($"\n校验完成: {files.Length - fail}/{files.Length} 通过");
return fail > 0 ? 1 : 0;
