namespace GitDocumentDb;

public sealed class QueryException : GitDocumentDbException
{
    public QueryException(string message) : base(message) { }
}
