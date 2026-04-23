namespace GitDocumentDb.Schema;

public sealed record TableSchema(string Name, IReadOnlyList<IndexDefinition> Indexes);
