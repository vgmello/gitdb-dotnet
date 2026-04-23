namespace GitDocumentDb.Indexing;

internal sealed class RangeIndex : IIndex
{
    public string Field { get; }
    public SortedList<object, List<string>> Sorted { get; }

    public RangeIndex(string field, SortedList<object, List<string>> sorted)
    {
        Field = field;
        Sorted = sorted;
    }
}
