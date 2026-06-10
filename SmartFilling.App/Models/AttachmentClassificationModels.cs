namespace SmartFilling.App.Models;

public class ClassificationApiResponse<T>
{
    public int Code { get; set; }
    public string? Msg { get; set; }
    public T Data { get; set; } = default!;
    public int Total { get; set; }
}

public class ClassificationResultData
{
    public int Code { get; set; }
    public int Progress { get; set; }
    public ClassificationFileResult[] Result { get; set; } = Array.Empty<ClassificationFileResult>();
}

public class ClassificationFileResult
{
    public int TotalPages { get; set; }
    public string ElapsedMs { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public ClassificationPageResult[] ClassificationResults { get; set; } = Array.Empty<ClassificationPageResult>();
}

public class ClassificationPageResult
{
    public string FileUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public int? CategoryId { get; set; }
    public string? CategoryCode { get; set; }
    public string CategoryName { get; set; } = "";
    public bool? IsCover { get; set; }
    public string OcrText { get; set; } = "";
    public object? ExtractDataJson { get; set; }
}
