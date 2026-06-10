using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Models;
using SmartFilling.BackgroundWorker.Services;
using SmartFilling.Engine.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using Xunit;

namespace SmartFilling.BackgroundWorker.Tests;

/// <summary>
/// code-msg 计划 DEC-3 层2 helper 接线测试（用户决策 3-B，2026-07-13）。
/// 测 AutomationWsClient.BuildFillDataAndDownloadAttachmentsAsync（internal static）的 WS 数据流接线：
/// payload.Params -> NormalizeJsonElement 转 CLR fillData + username/password 硬编码注入 + 调 DownloadAttachmentsInFillDataAsync 接线。
/// 技术要点：DownloadAttachmentsInFillDataAsync 内部 new HttpClient 不可 stub，但**用不含 file 字段的 payload** ->
/// TraverseAndDownloadAsync 遍历 fillData 无附件对象 -> downloadCount=0 不触发 httpClient.GetByteArrayAsync 真实下载 ->
/// 可验 fillData 构建 + username/password 注入 + 调用接线，不触发真实 HTTP（下载部分仍靠联调，与 ISSUE-C 声明一致）。
/// </summary>
public class AutomationWsClientHelperTests
{
    /// <summary>构造 mock IServiceScope，ServiceProvider.GetRequiredService 返回各依赖。</summary>
    private static IServiceScope CreateMockScope(string contentRootPath, EngineOptions engineOptions, PlatformOptions platformOptions)
    {
        var envMock = Substitute.For<IWebHostEnvironment>();
        envMock.ContentRootPath.Returns(contentRootPath);

        var loggerMock = Substitute.For<EngineILogger>();

        var spMock = Substitute.For<IServiceProvider>();
        spMock.GetService(typeof(IWebHostEnvironment)).Returns(envMock);
        spMock.GetService(typeof(EngineILogger)).Returns(loggerMock);
        spMock.GetService(typeof(IOptions<EngineOptions>)).Returns(Options.Create(engineOptions));
        spMock.GetService(typeof(IOptions<PlatformOptions>)).Returns(Options.Create(platformOptions));

        var scopeMock = Substitute.For<IServiceScope>();
        scopeMock.ServiceProvider.Returns(spMock);
        return scopeMock;
    }

    [Fact]
    public async Task BuildFillData_NoFileField_FillDataBuilt_WithCredentials_NoDownload()
    {
        // payload.Params={"foo":"bar"}（无 file 字段）+ username + decryptedPassword
        // -> fillData 含 foo（NormalizeJsonElement 转换）+ username/password 硬编码注入，不触发真实下载
        var tmpDir = Path.Combine(Path.GetTempPath(), "sf-helper-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var fooElement = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("\"bar\"");
            var payload = new ExecuteTaskPayload
            {
                TaskId = "t1",
                ScriptId = "s1",
                Username = "testuser",
                Password = "ignored",  // password 经解密后单独传 decryptedPassword，payload.Password 不读
                Params = new Dictionary<string, System.Text.Json.JsonElement> { ["foo"] = fooElement }
            };

            var scope = CreateMockScope(tmpDir,
                new EngineOptions { UploadRootPath = tmpDir },
                new PlatformOptions { AttachmentBaseUrl = null, DefaultTimeoutSeconds = 30 });

            var fillData = await AutomationWsClient.BuildFillDataAndDownloadAttachmentsAsync(payload, "decryptedPwd", scope);

            // 验值：foo 经 NormalizeJsonElement 转 CLR string（非 JsonElement 残留）
            Assert.Equal("bar", fillData["foo"]);
            Assert.IsType<string>(fillData["foo"]);
            // 验值：username/password 硬编码注入（DEC-7 前提，Worker fillData 硬编码这两个 key）
            Assert.Equal("testuser", fillData["username"]);
            Assert.Equal("decryptedPwd", fillData["password"]);
            // 不含 file 字段 -> downloadCount=0 未触发下载：uploads 目录虽被无条件创建，但内无下载文件
            var uploadsDir = Path.Combine(tmpDir, "uploads");
            Assert.True(Directory.Exists(uploadsDir), "DownloadAttachmentsInFillDataAsync 无条件创建 uploads 目录");
            Assert.Empty(Directory.GetFiles(uploadsDir));  // 无附件时不下载任何文件
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task BuildFillData_NullParams_OnlyCredentialsInjected()
    {
        // payload.Params=null（极端边界）-> fillData 仅含 username/password，不崩
        var tmpDir = Path.Combine(Path.GetTempPath(), "sf-helper-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var payload = new ExecuteTaskPayload
            {
                TaskId = "t1",
                ScriptId = "s1",
                Username = "u1",
                Password = "",
                Params = null
            };

            var scope = CreateMockScope(tmpDir,
                new EngineOptions { UploadRootPath = tmpDir },
                new PlatformOptions { AttachmentBaseUrl = null, DefaultTimeoutSeconds = 30 });

            var fillData = await AutomationWsClient.BuildFillDataAndDownloadAttachmentsAsync(payload, "pwd1", scope);

            Assert.Equal("u1", fillData["username"]);
            Assert.Equal("pwd1", fillData["password"]);
            Assert.Equal(2, fillData.Count);  // 仅 username/password，无其他键
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }
}
