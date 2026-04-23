namespace GitDocumentDb;

public sealed class TransportException : GitDocumentDbException
{
    public TransportException(string message) : base(message) { }
    public TransportException(string message, Exception inner) : base(message, inner) { }
}
