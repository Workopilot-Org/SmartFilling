using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Services;
using Xunit;

namespace SmartFilling.BackgroundWorker.Tests;

/// <summary>
/// code-msg 计划第一部分：ScriptService.FetchScriptsAsync 解包链单测（stub HttpMessageHandler 注入）。
/// 覆盖：{code,msg,data} 信封解包 + DEC-2 code==200 校验 + DEC-1 scirptTotalContent 数组 EnumerateArray + DEC-4 LoadFromJson 全校验 + API-KEY header + 注释 Skip。
/// 走生产路径（FetchScriptsAsync 内部 JsonSerializer.Deserialize 信封 + ScriptLoader.LoadFromJson 校验），断言验值不只验状态。
/// </summary>
public class ScriptServiceFetchTests
{
    /// <summary>最小合法 ScriptV2 JSON（DEC-1 数组形态：scirptTotalContent 内是 [ScriptV2] 数组）。</summary>
    private const string MinimalScriptJson = """
        {
          "version": 2, "scriptId": "s1", "name": "测试脚本",
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "测试主流程", "steps": [
            { "kind": "step", "name": "checkAlways", "action": "check", "description": "校验", "detect": { "type": "always" }, "then": "nothing" }
          ]}]
        }
        """;

    /// <summary>构造 envelope JSON：data[*].scirptTotalContent 内嵌 ScriptV2 数组（string 经 JsonSerializer.Serialize 转义）。</summary>
    private static string BuildEnvelope(string scriptArrayJson, int code = 200, string msg = "操作成功", string scriptId = "s1")
    {
        var contentEscaped = System.Text.Json.JsonSerializer.Serialize(scriptArrayJson);  // scirptTotalContent 是 JSON 字符串，转义嵌入
        return $"{{\"code\":{code},\"msg\":\"{msg}\",\"data\":[{{\"scriptId\":\"{scriptId}\",\"scriptName\":\"测试\",\"scirptTotalContent\":{contentEscaped}}}]}}";
    }

    private static ScriptService CreateService(StubHttpMessageHandler stub, PlatformOptions? platform = null, WorkerOptions? worker = null)
        => new(
            new HttpClient(stub),
            Options.Create(platform ?? new PlatformOptions { JavaApiUrl = "http://test", ApiKey = "" }),
            Options.Create(worker ?? new WorkerOptions { IgnoreNullFieldsOnScriptValidation = true }),
            NullLogger<ScriptService>.Instance);

    [Fact]
    public async Task Fetch_ArrayForm_ReturnsScript()
    {
        // DEC-1 主形态：scirptTotalContent 内是 [ScriptV2] 数组 → 解析出 1 个 ScriptV2，Name 正确
        var body = BuildEnvelope($"[{MinimalScriptJson}]");
        var stub = new StubHttpMessageHandler(body);
        var svc = CreateService(stub);

        var scripts = await svc.FetchScriptsAsync("s1", CancellationToken.None);

        Assert.Single(scripts);
        Assert.Equal("测试脚本", scripts[0].Name);  // 验值
    }

    [Fact]
    public async Task Fetch_Non200Code_ThrowsWithMsg()
    {
        // DEC-2：code!=200 抛 InvalidOperationException 含 msg（比 v1 健壮，v1 只查 Data==null）
        var body = BuildEnvelope("[]", code: 500, msg: "服务内部错误");
        var stub = new StubHttpMessageHandler(body);
        var svc = CreateService(stub);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.FetchScriptsAsync("s1", CancellationToken.None));
        Assert.Contains("500", ex.Message);
        Assert.Contains("服务内部错误", ex.Message);  // 验值含 msg
    }

    [Fact]
    public async Task Fetch_EmptyData_Throws()
    {
        // data 空 → 抛"脚本不存在"
        var body = "{\"code\":200,\"msg\":\"ok\",\"data\":[]}";
        var stub = new StubHttpMessageHandler(body);
        var svc = CreateService(stub);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.FetchScriptsAsync("s1", CancellationToken.None));
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public async Task Fetch_ApiKeyHeader_AddedWhenNonEmpty()
    {
        // PlatformOptions.ApiKey 非空 → 请求带 API-KEY 头
        var body = BuildEnvelope($"[{MinimalScriptJson}]");
        var stub = new StubHttpMessageHandler(body);
        var svc = CreateService(stub, platform: new PlatformOptions { JavaApiUrl = "http://test", ApiKey = "sk-test-key" });

        await svc.FetchScriptsAsync("s1", CancellationToken.None);

        Assert.NotNull(stub.LastRequest);
        Assert.True(stub.LastRequest!.Headers.Contains("API-KEY"));
        Assert.Equal("sk-test-key", stub.LastRequest.Headers.GetValues("API-KEY").First());  // 验值
    }

    [Fact]
    public async Task Fetch_ApiKeyHeader_AbsentWhenEmpty()
    {
        // ApiKey 空 → 请求不带 API-KEY 头（避免空 header）
        var body = BuildEnvelope($"[{MinimalScriptJson}]");
        var stub = new StubHttpMessageHandler(body);
        var svc = CreateService(stub, platform: new PlatformOptions { JavaApiUrl = "http://test", ApiKey = "" });

        await svc.FetchScriptsAsync("s1", CancellationToken.None);

        Assert.NotNull(stub.LastRequest);
        Assert.False(stub.LastRequest!.Headers.Contains("API-KEY"));
    }

    [Fact]
    public async Task Fetch_EmptyScirptTotalContent_Throws()
    {
        // scirptTotalContent 为空字符串 → 抛"scirptTotalContent 为空"
        var body = "{\"code\":200,\"msg\":\"ok\",\"data\":[{\"scriptId\":\"s1\",\"scirptTotalContent\":\"\"}]}";
        var stub = new StubHttpMessageHandler(body);
        var svc = CreateService(stub);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.FetchScriptsAsync("s1", CancellationToken.None));
        Assert.Contains("scirptTotalContent", ex.Message);
        Assert.Contains("空", ex.Message);
    }

    [Fact]
    public async Task Fetch_ScirptTotalContent_WithLineComment_Parses()
    {
        // 观察3：scirptTotalContent 内含 // 行注释（与 ScriptLoader CommentHandling.Skip 一致，防 Skip 回退 silent 解析失败）。
        // 两层都须 Skip 注释：FetchScriptsAsync 先 Parse(CommentHandling.Skip) 判 ValueKind，再 LoadFromJson（内部 Skip）——任一层回退 Disallow 即抛 → 测试红。
        var scriptJson = MinimalScriptJson.Replace("\"name\": \"测试脚本\",", "\"name\": \"测试脚本\", // 顶层行注释\n");
        var body = BuildEnvelope($"[{scriptJson}]");
        var stub = new StubHttpMessageHandler(body);
        var svc = CreateService(stub);

        var scripts = await svc.FetchScriptsAsync("s1", CancellationToken.None);

        Assert.Single(scripts);
        Assert.Equal("测试脚本", scripts[0].Name);  // 验值：注释不影响解析，Name 正确（不只验成功）
    }

    [Fact]
    public async Task Fetch_ScirptTotalContent_NotArray_Throws()
    {
        // ISSUE-F：DEC-1 数组-only——scirptTotalContent 是单个 ScriptV2 对象（非数组）→ 抛"不是数组"（fail-fast）
        var body = BuildEnvelope(MinimalScriptJson);  // 单对象，非 [ScriptV2]
        var stub = new StubHttpMessageHandler(body);
        var svc = CreateService(stub);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.FetchScriptsAsync("s1", CancellationToken.None));
        Assert.Contains("不是数组", ex.Message);
    }

    /// <summary>Stub HttpMessageHandler：返回固定 body，捕获最后请求（验 API-KEY header）。</summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHttpMessageHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
