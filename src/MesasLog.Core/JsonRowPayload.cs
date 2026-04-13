using System.Text.Json.Serialization;

namespace MesasLog.Core;

/// <summary>
/// JSON padronizado: nomes de colunas (ou @N) e valores tipados.
/// </summary>
public sealed class JsonRowPayload
{
    [JsonPropertyName("columns")]
    public List<string> Columns { get; set; } = [];

    [JsonPropertyName("values")]
    public Dictionary<string, object?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
