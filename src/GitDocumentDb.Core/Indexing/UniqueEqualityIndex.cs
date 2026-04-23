namespace GitDocumentDb.Indexing;

internal sealed class UniqueEqualityIndex : IIndex
{
    public string Field { get; }
    public Dictionary<object, string> ByValue { get; }

    public UniqueEqualityIndex(string field, Dictionary<object, string> byValue)
    {
        Field = field;
        ByValue = byValue;
    }
}
