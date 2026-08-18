using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// httpRequest shareBrowserSession=true 真实浏览器集成测试。
/// 用 TcpListener 起本地 HTTP 服务端（不需管理员权限）+ 真实 Playwright Chromium 浏览器。
/// 验证 APIRequestContext 共享浏览器 cookie/storage——浏览器先访问页面设置 cookie，
/// 后续 httpRequest shareBrowserSession=true 自动带该 cookie。
/// </summary>
public class HttpRequestShareBrowserSessionTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private TcpListener? _tcpListener;
    private int _port;
    private CancellationTokenSource _serverCts = new();

    public async Task InitializeAsync()
    {
        // 1. 启动本地 HTTP 服务端（TcpListener，不需管理员权限）
        _tcpListener = new TcpListener(IPAddress.Loopback, 0);
        _tcpListener.Start();
        _port = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
        _ = Task.Run(() => RunHttpServerAsync(_serverCts.Token));

        // 2. 启动真实 Playwright Chromium 浏览器
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        _serverCts.Cancel();
        _tcpListener?.Stop();
        if (_browser != null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    /// <summary>本地 HTTP 服务端：根据 path 返回不同响应（设置 cookie / API 响应）</summary>
    private async Task RunHttpServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcpListener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { continue; }  // per-client catch 不崩服务端

            using var stream = client.GetStream();
            try
            {
                var sr = new StreamReader(stream, Encoding.UTF8);

                // 读 HTTP 请求行
                var requestLine = await sr.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(requestLine)) { client.Dispose(); continue; }
                var parts = requestLine.Split(' ');
                if (parts.Length < 3) { client.Dispose(); continue; }
                var method = parts[0];
                var path = parts[1];

                // 读 headers
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = await sr.ReadLineAsync(ct)))
                {
                    var idx = headerLine.IndexOf(':');
                    if (idx > 0) headers[headerLine[..idx].Trim()] = headerLine[(idx + 1)..].Trim();
                }

                // 读 body（POST 等）
                if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) && cl > 0)
                {
                    var bodyBuf = new char[cl];
                    await sr.ReadBlockAsync(bodyBuf, 0, cl);
                }

                // 根据 path 返回响应
                string response;
            if (path == "/set-cookie")
            {
                // 设置 cookie 的页面
                var body = "<html><body>cookies set</body></html>";
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                response = "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/html\r\n" +
                    "Connection: close\r\n" +
                    "Set-Cookie: sessionToken=test-session-123; Path=/\r\n" +
                    "Set-Cookie: csrfToken=test-csrf-456; Path=/\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    $"\r\n{body}";
            }
            else if (path == "/api/check-cookie")
            {
                // API 端点：返回收到的 cookie，验证浏览器 session 共享
                var cookieHeader = headers.GetValueOrDefault("Cookie", "");
                var hasSession = cookieHeader.Contains("sessionToken=test-session-123");
                var hasCsrf = cookieHeader.Contains("csrfToken=test-csrf-456");
                var json = $"{{\"hasSessionCookie\":{hasSession.ToString().ToLower()},\"hasCsrfCookie\":{hasCsrf.ToString().ToLower()}}}";
                var bodyBytes = Encoding.UTF8.GetBytes(json);
                response = "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json\r\n" +
                    "Connection: close\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    $"\r\n{json}";
            }
            else if (path == "/api/simple")
            {
                var json = """{"data":{"token":"abc123"}}""";
                var bodyBytes = Encoding.UTF8.GetBytes(json);
                response = "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json\r\n" +
                    "Connection: close\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    $"\r\n{json}";
            }
            else
            {
                response = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            }

            var responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes, ct);
            }
            catch (Exception) { }  // per-client 异常不崩服务端
            client.Dispose();
        }
    }

    private static EngineOptions DefaultOptions() => new()
    {
        MaxScriptDuration = 600000,
        DefaultTimeout = 60000,
        StepRetry = new StepRetry { Count = 0 }
    };

    [Fact]
    public async Task ShareBrowserSession_True_AutoSendsBrowserCookies()
    {
        var logger = new NullLogger();
        var engine = new ScriptEngine(logger, DefaultOptions());
        var page = await _browser!.NewPageAsync();

        try
        {
            // 1. 浏览器访问 set-cookie 页面，设置 session cookie
            await page.GotoAsync($"http://localhost:{_port}/set-cookie");

            // 2. 执行 httpRequest shareBrowserSession=true 调用 check-cookie API
            var script = new ScriptBuilder()
                .AddPhase("Main", steps:
                [
                    ScriptBuilder.Step("httpRequest", url: $"http://localhost:{_port}/api/check-cookie", storeAs: "resp")
                        with { ShareBrowserSession = true }
                ])
                .Build();

            var result = await engine.ExecuteAsync(script, new(), page);

            // 3. 断言：响应 200 + body 含 hasSessionCookie=true（验 cookie 共享）
            Assert.True(result.Success, result.ErrorMessage ?? "");
            Assert.True(result.Vars!.ContainsKey("resp"));
            var resp = (Dictionary<string, object>)result.Vars!["resp"];
            Assert.Equal(200, resp["statusCode"]);
            var body = (Dictionary<string, object>)resp["body"];
            Assert.True((bool)body["hasSessionCookie"], "shareBrowserSession=true 应自动带浏览器 session cookie");
            Assert.True((bool)body["hasCsrfCookie"], "shareBrowserSession=true 应自动带浏览器 csrf cookie");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task ShareBrowserSession_Default_Independent_NoCookies()
    {
        var logger = new NullLogger();
        var engine = new ScriptEngine(logger, DefaultOptions());
        var page = await _browser!.NewPageAsync();

        try
        {
            // 浏览器访问 set-cookie 页面设置 cookie
            await page.GotoAsync($"http://localhost:{_port}/set-cookie");

            // 执行 httpRequest 不传 shareBrowserSession（默认 false=HttpClient 独立模式）
            var script = new ScriptBuilder()
                .AddPhase("Main", steps:
                [
                    ScriptBuilder.Step("httpRequest", url: $"http://localhost:{_port}/api/check-cookie", storeAs: "resp")
                ])
                .Build();

            var result = await engine.ExecuteAsync(script, new(), page);

            // 断言：响应 200 + body 含 hasSessionCookie=false（HttpClient 独立不带浏览器 cookie）
            Assert.True(result.Success, result.ErrorMessage ?? "");
            var resp = (Dictionary<string, object>)result.Vars!["resp"];
            Assert.Equal(200, resp["statusCode"]);
            var body = (Dictionary<string, object>)resp["body"];
            Assert.False((bool)body["hasSessionCookie"], "shareBrowserSession 默认 false 应不带浏览器 cookie");
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}
