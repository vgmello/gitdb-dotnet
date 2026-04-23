namespace GitDocumentDb;

public interface IRecordMerger<T>
{
    MergeResult<T> Merge(T baseRecord, T local, T remote);
}
