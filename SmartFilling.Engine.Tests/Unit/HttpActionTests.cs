using System.Net;
using System.Text.Json;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// httpRequest action 单元测试。
/// 用 stub HttpMessageHandler 测试 HttpClient 独立模式（shareBrowserSession 默认 false）。
/// shareBrowserSession=true 需真实浏览器 + HTTP 服务端，不在本测试覆盖（参照 IframeNestedBrowserTests 先例，需手动联调）。
/// </summary>
public class HttpActionTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions DefaultOptions() => new()
    {
        MaxScriptDuration = 600000,
        StepRetry = new StepRetry { Count = 0 }
    };

    /// <summary>创建 stub HttpMessageHandler，返回固定响应。</summary>
    private static StubHttpHandler CreateHandler(HttpStatusCode status, string body, string contentType = "application/json",
        Func<HttpRequestMessage, Task>? capture = null)
    {
        return new StubHttpHandler(status, body, contentType, capture);
    }

    /// <summary>用 stub handler 创建 HttpClient → 注入 StepExecutor → 注入 ScriptEngine。</summary>
    private ScriptEngine CreateEngine(StubHttpHandler handler)
    {
        var httpClient = new HttpClient(handler);
        // ScriptEngine 构造函数 1：logger + options + captchaService + httpClient
        return new ScriptEngine(_logger, DefaultOptions(), null, httpClient);
    }

    #region 正向 GET

    [Fact]
    public async Task Get_JsonBody_StoredInVars()
    {
        var handler = CreateHandler(HttpStatusCode.OK, """{"data":{"token":"abc"}}""", "application/json");
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/token", storeAs: "resp")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
        // 断言 vars 含 resp，且 resp.body.data.token == "abc"（验值不只验状态）
        Assert.True(result.Vars!.ContainsKey("resp"), "resp 变量未存入 vars");
        var resp = result.Vars!["resp"];
        Assert.IsType<Dictionary<string, object>>(resp);
        var respDict = (Dictionary<string, object>)resp;
        Assert.Equal(200, respDict["statusCode"]);
        var body = Assert.IsType<Dictionary<string, object>>(respDict["body"]);
        var data = Assert.IsType<Dictionary<string, object>>(body["data"]);
        Assert.Equal("abc", data["token"].ToString());
    }

    #endregion

    #region POST + object body

    [Fact]
    public async Task Post_ObjectBody_JsonSerialized_ContentTypeSet()
    {
        string? capturedBody = null;
        string? capturedMediaType = null;
        string? capturedMethod = null;
        var handler = CreateHandler(HttpStatusCode.OK, "{}", "application/json", async req =>
        {
            capturedMethod = req.Method.Method;
            if (req.Content != null)
            {
                capturedBody = await req.Content.ReadAsStringAsync();
                capturedMediaType = req.Content.Headers.ContentType?.MediaType;
            }
        });
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/submit", storeAs: "resp")
                    with { Method = "POST", Body = (object)new Dictionary<string, object> { ["name"] = "test" } }
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.Equal("POST", capturedMethod);
        Assert.Contains("\"name\":\"test\"", capturedBody);
        Assert.Equal("application/json", capturedMediaType);
    }

    #endregion

    #region string body

    [Fact]
    public async Task Post_StringBody_RawSent_NoAutoContentType()
    {
        string? capturedBody = null;
        var handler = CreateHandler(HttpStatusCode.OK, "{}", "application/json", async req =>
        {
            if (req.Content != null)
                capturedBody = await req.Content.ReadAsStringAsync();
        });
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/raw", storeAs: "resp")
                    with { Method = "POST", Body = (object)"hello world" }
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.Equal("hello world", capturedBody);
    }

    #endregion

    #region headers/params 变量替换

    [Fact]
    public async Task HeadersAndParams_VarsReplaced()
    {
        HttpRequestMessage? captured = null;
        var handler = CreateHandler(HttpStatusCode.OK, "{}", "application/json", req => { captured = req; return Task.CompletedTask; });
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/path", storeAs: "resp")
                    with
                    {
                        Headers = (object)new Dictionary<string, object> { ["Authorization"] = "Bearer {{token}}" },
                        Params = (object)new Dictionary<string, object> { ["page"] = "{{pageNum}}" }
                    }
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var vars = new Dictionary<string, object> { ["token"] = "secret123", ["pageNum"] = "42" };
        var result = await engine.ExecuteAsync(script, vars, page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.NotNull(captured);
        Assert.Equal("Bearer secret123", captured!.Headers.GetValues("Authorization").First());
        // params 拼到 URL query string
        Assert.Contains("page=42", captured.RequestUri!.ToString());
    }

    #endregion

    #region 非 2xx 抛错

    [Fact]
    public async Task Non2xx_DefaultFailOnHttpError_Throws()
    {
        var handler = CreateHandler(HttpStatusCode.InternalServerError, "error", "text/plain");
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/fail", storeAs: "resp")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.False(result.Success);
        Assert.Contains("HTTP 500", result.ErrorMessage ?? "");
    }

    #endregion

    #region failOnHttpError=false 不抛

    [Fact]
    public async Task FailOnHttpErrorFalse_StoresResponse_DoesNotThrow()
    {
        var handler = CreateHandler(HttpStatusCode.NotFound, """{"error":"not found"}""", "application/json");
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/404", storeAs: "resp")
                    with { FailOnHttpError = false }
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.True(result.Vars!.ContainsKey("resp"));
        var respDict = (Dictionary<string, object>)result.Vars!["resp"];
        Assert.Equal(404, respDict["statusCode"]);
    }

    #endregion

    #region body 非 JSON

    [Fact]
    public async Task NonJsonBody_StoredAsString()
    {
        var handler = CreateHandler(HttpStatusCode.OK, "plain text", "text/plain");
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/text", storeAs: "resp")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
        var respDict = (Dictionary<string, object>)result.Vars!["resp"];
        Assert.Equal("plain text", respDict["body"].ToString());
    }

    #endregion

    #region method 非法

    [Fact]
    public async Task IllegalMethod_ThrowsArgumentException()
    {
        // 绕过 schema，直接构造非法 method 的 StepNode
        var handler = CreateHandler(HttpStatusCode.OK, "{}");
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/x", storeAs: "resp")
                    with { Method = "FOO" }
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.False(result.Success);
        Assert.Contains("非法 HTTP method", result.ErrorMessage ?? "");
    }

    #endregion

    #region check(js) 嵌套取值

    [Fact]
    public async Task CheckJs_AccessNestedResponseBody()
    {
        var handler = CreateHandler(HttpStatusCode.OK, """{"data":{"token":"abc"}}""", "application/json");
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/token", storeAs: "resp"),
                ScriptBuilder.CheckStep(
                    ScriptBuilder.Detect("js", check: "vars.resp.body.data.token === 'abc'"),
                    "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
    }

    #endregion

    #region 多值头 Set-Cookie

    [Fact]
    public async Task MultiValueHeader_StoredAsList()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{}", "application/json");
        handler.SetHeaders.Add("Set-Cookie", ["session=abc; Path=/", "csrfToken=xyz; Path=/", "refresh=def; Path=/"]);
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/cookies", storeAs: "resp")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
        var respDict = (Dictionary<string, object>)result.Vars!["resp"];
        var headers = (Dictionary<string, object>)respDict["headers"];
        // 多值头存为 List<string>（验类型 + 元素数 + 元素值）
        var setCookie = Assert.IsType<List<string>>(headers["Set-Cookie"]);
        Assert.Equal(3, setCookie.Count);
        Assert.Contains("session=abc", setCookie[0]);
        // 单值头存为 string
        Assert.IsType<string>(headers["Content-Type"]);
    }

    #endregion

    #region 超时

    [Fact]
    public async Task Timeout_StepTimeout_WrapsOceAsStepError()
    {
        // stub 延迟 > step timeout → linked CTS CancelAfter 触发 → SendAsync 抛 OCE
        // → ExecuteHttpAsync 包装为 InvalidOperationException（非 OCE）→ 被 step 级 catch 捕获 → step 失败（非脚本级超时）
        var handler = new DelayingHandler(TimeSpan.FromSeconds(5));
        var engine = CreateEngine(new StubHttpHandler(HttpStatusCode.OK, "{}"));
        // 用 DelayingHandler 替换 stub
        var delayingEngine = new ScriptEngine(_logger, DefaultOptions(), null, new HttpClient(handler));

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/slow", storeAs: "resp")
                    with { Timeout = 200 }  // 200ms 超时，stub 延迟 5s
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await delayingEngine.ExecuteAsync(script, new(), page);

        // step 失败（非脚本级超时）——OCE 被包装为 InvalidOperationException 进入 step 级 onError=stop
        Assert.False(result.Success);
        Assert.Contains("超时", result.ErrorMessage ?? "");
    }

    #endregion

    #region form-data 文件上传

    [Fact]
    public async Task FormData_FileUpload_MultipartContentType_WithFields()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        await System.IO.File.WriteAllTextAsync(tempFile, "test file content");

        try
        {
            string? capturedBody = null;
            string? capturedContentType = null;
            var handler = CreateHandler(HttpStatusCode.OK, "{}", "application/json", async req =>
            {
                if (req.Content != null)
                {
                    capturedBody = await req.Content.ReadAsStringAsync();
                    capturedContentType = req.Content.Headers.ContentType?.MediaType;
                }
            });
            var engine = CreateEngine(handler);

            var script = new ScriptBuilder()
                .AddPhase("Main", steps:
                [
                    ScriptBuilder.Step("httpRequest", url: "https://api.test.com/upload", storeAs: "resp")
                        with
                        {
                            Method = "POST",
                            Body = (object)new Dictionary<string, object> { ["category"] = "合同" },
                            Files = new List<object> { new Dictionary<string, object> { ["paramName"] = "file", ["path"] = tempFile } }
                        }
                ])
                .Build();

            MockPageFactory.Create(out var page);
            var result = await engine.ExecuteAsync(script, new(), page);

            Assert.True(result.Success, result.ErrorMessage ?? "");
            Assert.NotNull(capturedContentType);
            Assert.Contains("multipart/form-data", capturedContentType!);
            // 文件字段内容在请求体中
            Assert.Contains("test file content", capturedBody!);
            // body 普通字段在请求体中
            Assert.Contains("合同", capturedBody!);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task FormData_FilenameAndContentType_InContentDisposition()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        await System.IO.File.WriteAllTextAsync(tempFile, "pdf content");

        try
        {
            string? capturedBody = null;
            var handler = CreateHandler(HttpStatusCode.OK, "{}", "application/json", async req =>
            {
                if (req.Content != null) capturedBody = await req.Content.ReadAsStringAsync();
            });
            var engine = CreateEngine(handler);

            var script = new ScriptBuilder()
                .AddPhase("Main", steps:
                [
                    ScriptBuilder.Step("httpRequest", url: "https://api.test.com/upload", storeAs: "resp")
                        with
                        {
                            Method = "POST",
                            Files = new List<object> { new Dictionary<string, object> {
                                ["paramName"] = "file", ["path"] = tempFile,
                                ["filename"] = "custom.pdf", ["contentType"] = "application/pdf"
                            } }
                        }
                ])
                .Build();

            MockPageFactory.Create(out var page);
            var result = await engine.ExecuteAsync(script, new(), page);

            Assert.True(result.Success, result.ErrorMessage ?? "");
            Assert.NotNull(capturedBody);
            // filename 在 Content-Disposition 中（.NET 可能用 filename= 或 filename*= 编码）
            Assert.Contains("custom.pdf", capturedBody!);
            // contentType 在文件 part 的 Content-Type 中
            Assert.Contains("application/pdf", capturedBody!);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task FilesEmpty_ObjectBody_GoesJson()
    {
        // files 显式空数组 → hasFiles=false → body 走 JSON（隐式编码判断回归）
        string? capturedContentType = null;
        var handler = CreateHandler(HttpStatusCode.OK, "{}", "application/json", req =>
        {
            capturedContentType = req.Content?.Headers.ContentType?.MediaType;
            return Task.CompletedTask;
        });
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/submit", storeAs: "resp")
                    with { Method = "POST", Body = (object)new Dictionary<string, object> { ["a"] = 1 }, Files = [] }
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.Equal("application/json", capturedContentType);
    }

    #endregion

    #region evaluate + args 提取多值头

    [Fact]
    public void EvaluateArgs_MultiValueHeader_ResolvedAsList()
    {
        // 验证 ReplaceArgsVars → ResolveRawValue → PluckPath 返回 List<string> 原始对象
        // （evaluate args 拿到 JS 数组的前提：args 占位符解析返回 List 而非 ToString 后的 string）
        var vars = new Dictionary<string, object>
        {
            ["resp"] = new Dictionary<string, object>
            {
                ["headers"] = new Dictionary<string, object>
                {
                    ["Set-Cookie"] = new List<string> { "session=abc; Path=/", "csrfToken=xyz; Path=/", "refresh=def; Path=/" }
                }
            }
        };
        var resolved = VariableHelper.ResolveRawValue("{{resp.headers.Set-Cookie}}", [], vars);
        Assert.NotNull(resolved);
        var list = Assert.IsType<List<string>>(resolved);
        Assert.Equal(3, list.Count);
        Assert.Equal("csrfToken=xyz; Path=/", list[1]);
    }

    #endregion

    #region screenshot base64 → httpRequest body 引用

    [Fact]
    public async Task ScreenshotBase64_ReferencedInHttpRequestBody()
    {
        string? capturedBody = null;
        var handler = CreateHandler(HttpStatusCode.OK, "{}", "application/json", async req =>
        {
            if (req.Content != null) capturedBody = await req.Content.ReadAsStringAsync();
        });
        var engine = CreateEngine(handler);

        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("httpRequest", url: "https://api.test.com/upload", storeAs: "resp")
                    with
                    {
                        Method = "POST",
                        Body = (object)new Dictionary<string, object> { ["fileName"] = "form.png", ["fileData"] = "{{screenshotBase64}}" }
                    }
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var vars = new Dictionary<string, object> { ["screenshotBase64"] = "iVBORw0KGgoAAAANS" };
        var result = await engine.ExecuteAsync(script, vars, page);

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.NotNull(capturedBody);
        Assert.Contains("iVBORw0KGgoAAAANS", capturedBody!);
    }

    #endregion

    #region storeContent=base64 单值

    [Fact]
    public async Task Screenshot_StoreContentBase64_PureBase64String()
    {
        // screenshot 需真实浏览器截图，本测试只验 storeContent 归一化逻辑（NormalizeStoreContent）
        var contents = VariableHelper.NormalizeStoreContent("base64");
        Assert.Single(contents);
        Assert.Equal("base64", contents[0]);
    }

    [Fact]
    public async Task Screenshot_StoreContentArray_ThreeItems()
    {
        var contents = VariableHelper.NormalizeStoreContent(
            (object)new List<object> { "dataUrl", "base64", "path" });
        Assert.Equal(3, contents.Count);
        Assert.Contains("dataUrl", contents);
        Assert.Contains("base64", contents);
        Assert.Contains("path", contents);
    }

    [Fact]
    public async Task Screenshot_StoreContentNull_DefaultsToPath()
    {
        var contents = VariableHelper.NormalizeStoreContent(null);
        Assert.Single(contents);
        Assert.Equal("path", contents[0]);
    }

    [Fact]
    public async Task Screenshot_StoreContentBoth_SchemaRejects()
    {
        // schema 层独立验：storeContent="both" 被 oneOf enum 拒绝（enum 无 both）
        var json = """
        {"version":2,"scriptId":"test","name":"test","phases":[
          {"kind":"phase","name":"p","aiGoal":"t","steps":[
            {"kind":"step","name":"s","action":"screenshot","storeAs":"shot","storeContent":"both"}
          ]}
        ]}
        """;
        var errors = ScriptLoader.ValidateSchemaAndGetErrors(json, false);
        // schema 应拒绝 "both"（enum 只有 path/dataUrl/base64）
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("storeContent"));
    }

    #endregion

    #region storeContent 含 path + saveToFile=false 校验

    [Fact]
    public async Task Screenshot_SaveToFileFalse_StoreContentPath_ScriptLoaderRejects()
    {
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("screenshot", storeAs: "shot")
                    with { SaveToFile = false, StoreContent = (object)new List<string> { "path", "dataUrl" } }
            ])
            .Build();

        var errors = ScriptLoader.ValidateAndGetErrors(script);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("path") && e.Contains("saveToFile"));
    }

    #endregion

    #region 内嵌 stub handler

    private class StubHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _contentType;
        private readonly Func<HttpRequestMessage, Task>? _capture;
        public Dictionary<string, List<string>> SetHeaders { get; } = new();

        public StubHttpHandler(HttpStatusCode status, string body, string contentType = "application/json",
            Func<HttpRequestMessage, Task>? capture = null)
        {
            _status = status;
            _body = body;
            _contentType = contentType;
            _capture = capture;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_capture != null) await _capture(request);
            var response = new HttpResponseMessage(_status)
            {
                Content = new System.Net.Http.StringContent(_body, System.Text.Encoding.UTF8, _contentType)
            };
            // 添加额外响应头（如多值 Set-Cookie）
            foreach (var kv in SetHeaders)
                foreach (var v in kv.Value)
                    response.Headers.Add(kv.Key, v);
            return response;
        }
    }

    private class DelayingHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public DelayingHandler(TimeSpan delay) { _delay = delay; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    #endregion
}
