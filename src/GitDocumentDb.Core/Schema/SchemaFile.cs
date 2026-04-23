using System.Text.Json.Serialization;

namespace GitDocumentDb.Schema;

internal sealed class SchemaFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("tables")] public Dictionary<string, SchemaFileTable> Tables { get; set; } = new();
}

internal sealed class SchemaFileTable
{
    [JsonPropertyName("indexes")] public List<SchemaFileIndex> Indexes { get; set; } = new();
}

internal sealed class SchemaFileIndex
{
    [JsonPropertyName("field")] public string Field { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "equality";
    [JsonPropertyName("unique")] public bool Unique { get; set; }
}
