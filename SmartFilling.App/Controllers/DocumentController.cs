using Microsoft.AspNetCore.Mvc;
using SmartFilling.App.Models;
using SmartFilling.App.Services;

namespace SmartFilling.App.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DocumentController : ControllerBase
{
    private readonly ScriptService _scriptService;

    public DocumentController(ScriptService scriptService) => _scriptService = scriptService;

    [HttpGet]
    public ActionResult<List<DocumentType>> GetAll() => Ok(_scriptService.GetAllDocuments());

    [HttpGet("{id}")]
    public ActionResult<DocumentType> Get(string id)
    {
        var doc = _scriptService.GetDocument(id);
        return doc == null ? NotFound() : Ok(doc);
    }

    [HttpPost]
    public ActionResult<DocumentType> Create([FromBody] CreateDocumentRequest request)
    {
        var doc = _scriptService.CreateDocument(request.Name, request.Description ?? "", request.Icon ?? "📄");
        return Ok(doc);
    }

    [HttpPut("{id}")]
    public ActionResult Update(string id, [FromBody] DocumentType doc)
    {
        if (doc.Id != id) doc.Id = id; // uses init-only, won't actually set
        _scriptService.UpdateDocument(doc);
        return Ok();
    }

    [HttpDelete("{id}")]
    public ActionResult Delete(string id)
    {
        _scriptService.DeleteDocument(id);
        return Ok();
    }

    [HttpGet("{id}/scripts")]
    public ActionResult GetScripts(string id) => Ok(_scriptService.GetScriptsByDocument(id));

    [HttpGet("all-scripts")]
    public ActionResult GetAllScripts() => Ok(_scriptService.GetAllScripts());
}

public class CreateDocumentRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Icon { get; set; }
}
