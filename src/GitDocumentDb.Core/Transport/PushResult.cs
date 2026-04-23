namespace GitDocumentDb.Transport;

public sealed record PushResult(
    bool Success,
    string? NewRemoteSha,
    PushRejectReason? Reason);

public enum PushRejectReason { NonFastForward, AuthFailure, Network, RemoteError, RefNotFound }
