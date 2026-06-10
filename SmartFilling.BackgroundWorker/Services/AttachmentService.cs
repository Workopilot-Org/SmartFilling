using System.Text.Json;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.BackgroundWorker.Services;

public class AttachmentService
{
    /// <summary>
    /// 递归遍历 fillData，找到所有附件对象（含 {name, url, path} 的对象或数组），
    /// 从 url 下载到本地，填充 path 字段。
    /// </summary>
    public static async Task DownloadAttachmentsInFillDataAsync(
        Dictionary<string, object> fillData,
        string contentRootPath,
        EngineILogger logger,
        string? attachmentBaseUrl = null,
        int timeoutSeconds = 60)
    {
        if (fillData == null) return;

        var uploadDir = Path.Combine(contentRootPath, "uploads");
        Directory.CreateDirectory(uploadDir);

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 60)
        };
        int downloadCount = await TraverseAndDownloadAsync(fillData, uploadDir, httpClient, attachmentBaseUrl, logger);

        if (downloadCount > 0)
            logger.LogInformation($"附件下载完成，共 {downloadCount} 个文件");
    }

    private static async Task<int> TraverseAndDownloadAsync(
        object? node,
        string uploadDir,
        HttpClient httpClient,
        string? baseUrl,
        EngineILogger logger)
    {
        int count = 0;

        switch (node)
        {
            case Dictionary<string, object> dict:
                foreach (var key in dict.Keys.ToList())
                {
                    var value = dict[key];
                    if (value is Dictionary<string, object> att && att.ContainsKey("url"))
                    {
                        var path = att.TryGetValue("path", out var p) ? p?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            var url = att["url"]?.ToString() ?? "";
                            var name = att.TryGetValue("name", out var n) ? n?.ToString() : null;
                            if (!string.IsNullOrEmpty(url))
                            {
                                await DownloadSingleAsync(url, name, uploadDir, httpClient, baseUrl, logger,
                                    localPath => att["path"] = localPath);
                                count++;
                            }
                        }
                    }
                    else
                    {
                        count += await TraverseAndDownloadAsync(value, uploadDir, httpClient, baseUrl, logger);
                    }
                }
                break;

            case List<object> list:
                foreach (var item in list)
                {
                    if (item is Dictionary<string, object> listItem && listItem.ContainsKey("url"))
                    {
                        var path = listItem.TryGetValue("path", out var p) ? p?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            var url = listItem["url"]?.ToString() ?? "";
                            var name = listItem.TryGetValue("name", out var n) ? n?.ToString() : null;
                            if (!string.IsNullOrEmpty(url))
                            {
                                await DownloadSingleAsync(url, name, uploadDir, httpClient, baseUrl, logger,
                                    localPath => listItem["path"] = localPath);
                                count++;
                            }
                        }
                    }
                    else
                    {
                        count += await TraverseAndDownloadAsync(item, uploadDir, httpClient, baseUrl, logger);
                    }
                }
                break;

            case JsonElement je:
                // JsonElement 只读，递归检查但不修改（fillData 应已 deserialize 为 Dictionary）
                if (je.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in je.EnumerateObject())
                        count += await TraverseAndDownloadAsync(prop.Value, uploadDir, httpClient, baseUrl, logger);  // #13：传 prop.Value（原 null 误传→嵌套附件遍历不到）
                }
                else if (je.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in je.EnumerateArray())
                        count += await TraverseAndDownloadAsync(item, uploadDir, httpClient, baseUrl, logger);  // #13：传 item（原 null 误传）
                }
                break;
        }

        return count;
    }

    private static async Task DownloadSingleAsync(
        string rawUrl,
        string? originalName,
        string uploadDir,
        HttpClient httpClient,
        string? baseUrl,
        EngineILogger logger,
        Action<string> onDownloaded)
    {
        if (string.IsNullOrEmpty(rawUrl)) return;

        var downloadUrl = rawUrl;
        if (!string.IsNullOrEmpty(baseUrl)
            && !rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            downloadUrl = $"{baseUrl.TrimEnd('/')}/{rawUrl.TrimStart('/')}";
        }

        var guid = Guid.NewGuid().ToString("N")[..8];
        // F9：净化 originalName 路径段（防 ../ 逃逸 uploadDir）。跨平台 Linux 不识别 \ 为分隔符，
        // 故 Path.GetFileName + GetFullPath StartsWith 双层兜底（与 App F7 EnsureWithinUploadDir 对齐）。
        var safeName = string.IsNullOrEmpty(originalName) ? guid : Path.GetFileName(originalName);
        var fileName = $"{guid}_{safeName}";
        var localPath = Path.Combine(uploadDir, fileName);
        var fullUpload = Path.GetFullPath(uploadDir);
        if (!Path.GetFullPath(localPath).StartsWith(fullUpload + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"附件下载路径逃逸 uploads 目录: {originalName}");

        try
        {
            var fileBytes = await httpClient.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(localPath, fileBytes);

            onDownloaded($"uploads/{fileName}");
        }
        catch (Exception ex)
        {
            // R3-8：附件是任务必需输入，下载失败应 fail-fast（任务立即失败上报），不再容错吞异常——
            // 原容错会让缺失附件 path 为空，若 upload step 走 skipIfDataEmpty/ElementMissing 被跳过，
            // 任务会误报"成功"但附件未传（不完整结果）。调用处 FillController(HTTP)/AutomationWsClient(WS)
            // 外层 try-catch 会捕获并上报任务失败。
            logger.LogError($"附件下载失败: 名称={originalName}, Url={downloadUrl} - {ex.Message}");
            throw new InvalidOperationException($"附件下载失败: 名称={originalName}, Url={downloadUrl} - {ex.Message}", ex);
        }
    }
}
