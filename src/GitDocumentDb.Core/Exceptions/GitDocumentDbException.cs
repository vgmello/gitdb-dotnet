namespace GitDocumentDb;

public class GitDocumentDbException : Exception
{
    public GitDocumentDbException(string message) : base(message) { }
    public GitDocumentDbException(string message, Exception inner) : base(message, inner) { }
}
