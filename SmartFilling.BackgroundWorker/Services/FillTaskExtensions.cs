using System.Text.Json;
using SmartFilling.BackgroundWorker.Models;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// ④-3：FillTask 扩展——returnData 序列化为日志友好的字符串（HTTP/WS 两 completionHandler 共用，规避"HTTP 改了 WS 忘改"漂移）。
/// 修订7：换行清洗（防撕裂日志行）+ 4KB 截断（base64 dataUrl 等大值防日志膨胀 + run_suite 全量读 O(n²)）。
/// </summary>
public static class FillTaskExtensions
{
    private const int MaxReturnDataLogLength = 4096;

    public static string ReturnDataToJson(this FillTask task)
    {
        if (task.ReturnData == null) return "<null>";
        try
        {
            var json = JsonSerializer.Serialize(task.ReturnData);
            json = json.Replace("\r", "").Replace("\n", "");              // 换行清洗（Windows \r\n）
            if (json.Length > MaxReturnDataLogLength)
                json = json[..MaxReturnDataLogLength] + $"...(truncated {json.Length - MaxReturnDataLogLength} chars)";  // 截断保 JSON 头完整
            return json;
        }
        catch (Exception ex) { return $"<serialize-failed: {ex.Message}>"; }
    }
}
