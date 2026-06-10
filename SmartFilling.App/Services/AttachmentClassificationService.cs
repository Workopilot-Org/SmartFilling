using Microsoft.Extensions.Options;
using SmartFilling.App.Configuration;
using SmartFilling.App.Models;

namespace SmartFilling.App.Services;

public class AttachmentClassificationService
{
    private readonly HttpClient _httpClient;
    private readonly PlatformOptions _platform;
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    public AttachmentClassificationService(HttpClient httpClient, IOptions<PlatformOptions> platformOptions)
    {
        _httpClient = httpClient;
        _platform = platformOptions.Value;
        _baseUrl = _platform.NetApiUrl.TrimEnd('/');
        _apiKey = string.IsNullOrEmpty(_platform.ApiKey) ? null : _platform.ApiKey;
    }

    public async Task<ClassificationPageResult> ClassifyOnlyAsync(
        string filePath, string fileUrl,
        string[]? categoryCodes = null, int[]? categoryIds = null,
        int? groupId = null, string? groupCode = null)
    {
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "File", Path.GetFileName(filePath));
        form.Add(new StringContent("false"), "ExtractFields");
        AddCategoryParams(form, categoryCodes, categoryIds, groupId, groupCode);

        var result = await SendClassificationRequestAsync(form);
        return result.ClassificationResults[0];
    }

    public async Task<ClassificationPageResult> ClassifyAndExtractAsync(
        string filePath, string fileUrl,
        string[]? categoryCodes = null, int[]? categoryIds = null,
        int? groupId = null, string? groupCode = null)
    {
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "File", Path.GetFileName(filePath));
        form.Add(new StringContent("true"), "ExtractFields");
        AddCategoryParams(form, categoryCodes, categoryIds, groupId, groupCode);

        var result = await SendClassificationRequestAsync(form);
        return result.ClassificationResults[0];
    }

    public async Task<ClassificationPageResult> ExtractOnlyAsync(
        string filePath, string fileUrl, string categoryCode, string extractMode = "DOCUMENT")
    {
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "File", Path.GetFileName(filePath));
        form.Add(new StringContent(categoryCode), "CategoryCode");
        form.Add(new StringContent(extractMode), "ExtractMode");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/Classfication/ExtractFile");
        if (!string.IsNullOrEmpty(_apiKey)) request.Headers.Add("API-KEY", _apiKey);
        request.Content = form;

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var apiResponse = await response.Content.ReadFromJsonAsync<ClassificationApiResponse<string>>(SmartFilling.Engine.HttpJsonOptions.CaseInsensitive);  // #20
        var batchNo = apiResponse?.Data ?? throw new InvalidOperationException("未获取到批次号");
        var result = await GetResultAsync(batchNo);
        if (result.Result.Length == 0 || result.Result[0].ClassificationResults.Length == 0)
            throw new InvalidOperationException("提取结果为空");
        return result.Result[0].ClassificationResults[0];
    }

    private async Task<ClassificationFileResult> SendClassificationRequestAsync(MultipartFormDataContent form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/Classfication/ClassifyFile");
        if (!string.IsNullOrEmpty(_apiKey)) request.Headers.Add("API-KEY", _apiKey);
        request.Content = form;

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var apiResponse = await response.Content.ReadFromJsonAsync<ClassificationApiResponse<string>>(SmartFilling.Engine.HttpJsonOptions.CaseInsensitive);  // #20
        var batchNo = apiResponse?.Data ?? throw new InvalidOperationException("未获取到批次号");
        var result = await GetResultAsync(batchNo);

        if (result.Result.Length == 0 || result.Result[0].ClassificationResults.Length == 0)
            throw new InvalidOperationException("分类结果为空");

        return result.Result[0];
    }

    private async Task<ClassificationResultData> GetResultAsync(string batchNo)
    {
        int timeoutSeconds = _platform.DefaultTimeoutSeconds > 0 ? _platform.DefaultTimeoutSeconds : 60;
        int pollIntervalMs = _platform.ClassificationPollIntervalMs > 0 ? _platform.ClassificationPollIntervalMs : 5000;
        await Task.Delay(pollIntervalMs);
        var startTime = DateTime.Now;

        while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
        {
            var url = $"{_baseUrl}/api/Classfication/GetClassificationResult?batchNo={Uri.EscapeDataString(batchNo)}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_apiKey)) request.Headers.Add("API-KEY", _apiKey);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<ClassificationApiResponse<ClassificationResultData>>(SmartFilling.Engine.HttpJsonOptions.CaseInsensitive);  // #20
            var result = apiResponse?.Data ?? throw new InvalidOperationException("查询结果失败");

            if (result.Code == 1) return result;
            await Task.Delay(pollIntervalMs);
        }

        throw new TimeoutException($"分类任务在 {timeoutSeconds} 秒内未完成");
    }

    private static void AddCategoryParams(MultipartFormDataContent form,
        string[]? categoryCodes, int[]? categoryIds, int? groupId, string? groupCode)
    {
        if (categoryIds != null && categoryIds.Length > 0)
            foreach (var id in categoryIds) form.Add(new StringContent(id.ToString()), "CategoryIds");
        else if (categoryCodes != null && categoryCodes.Length > 0)
            foreach (var code in categoryCodes) form.Add(new StringContent(code), "CategoryCodes");
        else if (groupId.HasValue)
            form.Add(new StringContent(groupId.Value.ToString()), "GroupId");
        else if (!string.IsNullOrEmpty(groupCode))
            form.Add(new StringContent(groupCode), "GroupCode");
    }
}
