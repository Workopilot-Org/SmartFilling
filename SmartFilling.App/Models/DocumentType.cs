namespace SmartFilling.App.Models;

public class DocumentType
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "📄";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<string> OrderedScriptIds { get; set; } = [];
    public string DataProcessingPrompts { get; set; } = "";
}

public class AttachmentInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
