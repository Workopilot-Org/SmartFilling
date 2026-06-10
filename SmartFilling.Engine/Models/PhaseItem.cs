using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFilling.Engine.Models;

[JsonConverter(typeof(PhaseItemConverter))]
public abstract record PhaseItem
{
    public string Kind { get; init; } = "";
}
