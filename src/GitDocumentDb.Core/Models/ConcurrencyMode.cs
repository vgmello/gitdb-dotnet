namespace GitDocumentDb;

public enum ConcurrencyMode
{
    LastWriteWins,
    OptimisticReject,
    OptimisticMerge,
}

public enum ConflictReason
{
    VersionMismatch,
    UniqueViolation,
    HistoryRewritten,
    UnmergeableChange,
    ExpectedAbsentButPresent,
    ExpectedPresentButAbsent,
}

public enum WriteFailureReason
{
    SchemaViolation,
    InvalidId,
    RecordTooLarge,
    PushRejected,
    TransportError,
}

public enum ChangeReason { RemoteAdvance, HistoryRewritten }

public enum WriteOpKind { Put, Delete }
