namespace GitDocumentDb.Indexing;

internal sealed class EqualityIndex : IIndex
{
    public string Field { get; }
    public Dictionary<object, List<string>> ByValue { get; }

    public EqualityIndex(string field, Dictionary<object, List<string>> byValue)
    {
        Field = field;
        ByValue = byValue;
    }
}
