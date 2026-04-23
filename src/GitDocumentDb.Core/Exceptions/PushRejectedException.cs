namespace GitDocumentDb;

public sealed class PushRejectedException : GitDocumentDbException
{
    public PushRejectedException(string message) : base(message) { }
}
