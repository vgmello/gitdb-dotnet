namespace GitDocumentDb.Schema;

public enum IndexType { Equality, Range }

public sealed record IndexDefinition(string Field, IndexType Type, bool Unique);
