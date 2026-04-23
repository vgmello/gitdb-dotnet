# GitDocumentDb — Finalized Design Specification

**Date:** 2026-04-23
**Status:** Approved for implementation planning

## 1. Overview

GitDocumentDb is a .NET document database that uses a Git repository as its durable storage backend. Records are files, tables are folders, databases are branches in a single Git repository. The system is inherently distributed because Git itself is distributed — any number of independent processes can use the same repository concurrently, with conflicts resolved via configurable concurrency policies.

The stack is delivered as two independent .NET libraries:

- **`GitDocumentDb.Core`** — the database itself. Embedded, single-process usage. Multi-writer-safe by construction.
- **`GitDocumentDb.Orleans`** — an Orleans-based distribution layer that adds batching, single-activation serialization, and notification fan-out on top of Core.

Core is fully functional on its own. The Orleans library is a scaling optimization, not a correctness requirement.

## 2. Guiding principles

- **Git is the only external dependency.** A reachable Git server over HTTPS or SSH is the sole infrastructure requirement. No databases, message brokers, caches, or distributed lock services.
- **Git's distributed nature is embraced, not hidden.** The library assumes multiple independent writers at all times. Single-writer scenarios are a degenerate case, not the default.
- **Separation of concerns between libraries is strict.** Core has zero Orleans dependencies. Orleans does not reimplement any Core semantics; it wraps Core for distribution.
- **Correctness first, performance second.** Every operation is correct under any concurrent access pattern. Performance optimizations (batching, caching, notifications) do not weaken correctness guarantees.
- **Reads never block behind writes.** The read path is fully lock-free, both in Core (immutable snapshots) and in the Orleans wrapper (reentrant writer grain, stateless worker query grain).
- **Memory is a first-class budget.** Allocation on hot paths is minimized via pooled buffers, immutable snapshots with structural sharing, and `ValueTask`/`Span`-based APIs.
- **Operational concerns are separate from data concerns.** Storage mode, clone strategy, and cache sizes are application configuration. Schema and index definitions are data and live in the repository.

## 3. Data model and repository layout

A single Git repository is the storage substrate. Each database lives on its own branch; there is no `databases/` folder prefix. Branch name is `db/{database-name}`.

Layout of an individual database branch:

```
/                               # branch: db/{database-name}
├── .schema.json                # schema + index definitions
└── tables/
    ├── {table-name}/
    │   ├── {record-id}.json
    │   └── ...
    └── ...
```

Rationale for per-branch databases:

- Different databases become fully independent write domains. Writer processes for different databases never contend at the transport layer.
- Push failure on database A does not affect database B.
- Branch history per database is clean and inspectable.

The `main` branch is reserved for repository-level metadata (README, governance files) and is never written to by the library.

**Record ID character set.** `^[A-Za-z0-9_\-\.]{1,200}$`, must not start with `.`. Enforced on write; invalid IDs raise `ArgumentException`.

**Database name character set.** `^[A-Za-z0-9_\-]{1,100}$`. Enforced at `GetDatabase`.

**Table name character set.** Same as database name.

Database discovery is branch-based. `ListDatabasesAsync` enumerates remote refs matching `refs/heads/db/*`. Creating a new database is a commit on a newly created `db/{name}` branch; no deployment-level action is required.

## 4. Library 1: GitDocumentDb.Core

### 4.1. Responsibilities

Core implements the document database semantics:

- Opening, cloning, and maintaining local copies of the repository.
- Reading records with version tokens.
- Writing records with configurable concurrency policies.
- Detecting conflicts at both the Git push level (transport) and the record level (logical).
- Building and maintaining in-memory indexes based on `.schema.json`.
- Executing queries against local state.
- Providing change notifications when the local clone advances.

Core does not:

- Assume it is the only process using the repository.
- Depend on Orleans, any distributed system, or any external coordination.
- Cache across process restarts beyond what Git itself persists (and an optional local-disk index snapshot, §4.11).

### 4.2. Public API surface

```csharp
public interface IDocumentDatabase
{
    IDatabase GetDatabase(string name);
    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default);
}

public interface IDatabase
{
    string Name { get; }
    string CurrentCommit { get; }
    DateTimeOffset LastFetchedAt { get; }

    ITable<T> GetTable<T>(string name) where T : class;
    Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken ct = default);

    Task<FetchResult> FetchAsync(CancellationToken ct = default);
    IAsyncEnumerable<ChangeNotification> WatchAsync(CancellationToken ct = default);
}

public interface ITable<T> where T : class
{
    ValueTask<Versioned<T>?> GetAsync(string id, ReadOptions? options = null, CancellationToken ct = default);
    Task<IReadOnlyList<Versioned<T>>> QueryAsync(Query query, ReadOptions? options = null, CancellationToken ct = default);

    Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default);
    Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default);

    Task<BatchResult> CommitAsync(IEnumerable<WriteOperation<T>> operations, WriteOptions? options = null, CancellationToken ct = default);
}
```

Supporting types:

```csharp
public record Versioned<T>(T Record, string Id, string Version, string CommitSha);

public record WriteResult(
    bool Success,
    string? NewVersion,
    string? NewCommitSha,
    ConflictInfo? Conflict,
    WriteFailureReason? FailureReason);

public record BatchResult(
    bool Success,
    string? NewCommitSha,
    IReadOnlyList<OperationResult> Operations);

public record OperationResult(
    string Id,
    bool Success,
    string? NewVersion,
    ConflictInfo? Conflict,
    WriteFailureReason? FailureReason);

public record ConflictInfo(
    string Path,
    string ExpectedVersion,
    string ActualVersion,
    ReadOnlyMemory<byte>? CurrentContent,
    ConflictReason Reason);

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

public record FetchResult(
    bool HadChanges,
    string PreviousCommit,
    string CurrentCommit,
    IReadOnlyList<string> ChangedPaths);

public record ChangeNotification(
    string CommitSha,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> ChangedPaths,
    ChangeReason Reason);

public enum ChangeReason { RemoteAdvance, HistoryRewritten }

public record WriteOperation<T>(
    WriteOpKind Kind,
    string Id,
    T? Record,
    string? ExpectedVersion);

public enum WriteOpKind { Put, Delete }

public static class Versions
{
    public const string Absent = "__absent__";
}

public record PushResult(
    bool Success,
    string? NewRemoteSha,
    PushRejectReason? Reason);

public enum PushRejectReason { NonFastForward, AuthFailure, Network, RemoteError }
```

### 4.3. Concurrency model

Every write operation specifies a `ConcurrencyMode`:

```csharp
public enum ConcurrencyMode
{
    LastWriteWins,
    OptimisticReject,
    OptimisticMerge,
}

public class WriteOptions
{
    public ConcurrencyMode Mode { get; init; } = ConcurrencyMode.LastWriteWins;
    public string? ExpectedVersion { get; init; }
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan MaxRetryBackoff { get; init; } = TimeSpan.FromSeconds(5);
}
```

Semantics:

- **LastWriteWins**: the write unconditionally overwrites the current content. `ExpectedVersion` is ignored for conflict detection; it may still be used for observability.
- **OptimisticReject**: if the current version of the record differs from `ExpectedVersion`, the write fails with `WriteResult.Conflict`. The caller is responsible for re-reading and retrying.
- **OptimisticMerge**: if the current version differs, a three-way merge is attempted. See §4.7.

**Create-if-absent.** Setting `ExpectedVersion = Versions.Absent` asserts the record does not exist. If it does, the write fails with `ConflictReason.ExpectedAbsentButPresent`.

**Delete-if-present.** For `Delete` operations, an `ExpectedVersion` of a concrete version asserts the record is at that version. `Versions.Absent` is not valid for deletes. If the record is already gone, delete with `OptimisticReject` fails with `ExpectedPresentButAbsent`.

The database carries a default mode that applies when `WriteOptions.Mode` is not specified:

```csharp
public class DatabaseOptions
{
    public ConcurrencyMode DefaultConcurrencyMode { get; init; } = ConcurrencyMode.LastWriteWins;
}
```

**Retry backoff.** When a push is rejected and retries are attempted, delay for attempt N is:

```
delay(N) = min(RetryBackoff * 2^N, MaxRetryBackoff) * jitter
jitter  = 0.5 + random() * 0.5      // in [0.5, 1.0]
```

### 4.4. Version tokens

Read operations return a version token alongside the record. The version is the blob SHA of the record in the repository — stable, content-addressed, Git-native.

When the caller submits a write with `ExpectedVersion`, Core compares the token against the current state. This is a pure content comparison; the commit the record was observed in does not affect the check.

For explicit audit purposes, `Versioned<T>` also carries the `CommitSha` in which the record was observed. This is informational; the version token alone is sufficient for conflict detection.

**Absence sentinel.** `Versions.Absent` (see §4.2) is reserved for "I expect this record not to exist." It is not a valid blob SHA and cannot collide with a real version.

### 4.5. Conflict detection layers

Two distinct layers of conflict exist and are handled separately:

**Transport-level conflict (Git push rejection).** When Core attempts to push and the remote ref has advanced since Core's last fetch, the push is rejected. This is handled internally:

1. Fetch the latest state.
2. Re-validate every operation in the batch against the new state using the operation's concurrency mode, including re-checking unique-index constraints against the refreshed state.
3. If all operations remain valid, rebuild the commit on the new base and retry the push.
4. If any operation is now in conflict, surface per the logical conflict rules below.
5. Bounded by `WriteOptions.MaxRetries`; when exhausted, the operation fails with `PushRejectedException` for single-record writes or `BatchResult.Success = false` with per-operation reasons for batches.

**Logical conflict (record-level).** A record has been modified since the version the caller observed. Handled per the concurrency mode:

- `LastWriteWins`: no conflict; proceed.
- `OptimisticReject`: operation fails with `WriteResult.Conflict` containing the current state.
- `OptimisticMerge`: attempt merge; succeed or fail with `WriteResult.Conflict`.

Transport conflicts are invisible to the caller unless they escalate into logical conflicts after fetch. Logical conflicts are always surfaced — Core never silently picks a winner (except under `LastWriteWins` by explicit caller choice).

### 4.6. Batch semantics

Batches are atomic at the Git level. A `CommitAsync` call produces zero or one Git commits:

- If all operations in the batch pass their concurrency checks and the push succeeds, one commit containing all changes is produced. `BatchResult.Success` is `true` and every `OperationResult.Success` is `true`.
- If any operation fails its concurrency check (under `OptimisticReject` or unmergeable `OptimisticMerge` or a unique-index violation), the entire batch fails. No commit is produced. `BatchResult.Operations` contains per-operation results indicating which operation(s) caused the failure; non-offending operations have `Success = false` with `FailureReason = null` to indicate "skipped due to sibling failure."
- Callers wanting partial success submit smaller batches. Partial-success batches are deferred to v2.

The atomicity guarantee matches Git's native model: a commit is a commit; there is no "partial commit" concept.

### 4.7. Three-way merge

For `OptimisticMerge`, Core performs a three-way merge:

- **Base**: the blob content corresponding to `ExpectedVersion`.
- **Local**: the new content being written.
- **Remote**: the current blob content in the repository.

The merger is pluggable:

```csharp
public interface IRecordMerger<T>
{
    MergeResult<T> Merge(T baseRecord, T local, T remote);
}

public record MergeResult<T>(bool Succeeded, T? Merged, string? ConflictDescription);
```

Default implementations:

- `JsonPatchMerger<T>` — computes JSON Patch diffs between base→local and base→remote; merges if no overlapping paths; fails otherwise.
- `LineBasedMerger<T>` — falls back to Git's line-based merge for text formats. Not recommended for structured records.

Consumers register custom mergers (e.g., CRDT-based, domain-specific) via the database options:

```csharp
public class DatabaseOptions
{
    public Dictionary<Type, object> RecordMergers { get; init; } = new();
}
```

When a merge fails, Core returns `WriteResult.Conflict` with the base, local, and remote versions all attached so the caller can resolve manually.

**Base-blob unavailability.** If the base blob corresponding to `ExpectedVersion` is no longer retrievable (aggressive server-side GC), Core degrades to `OptimisticReject` semantics for that operation and returns `ConflictReason.VersionMismatch`. The library requires `gc.auto=0` on local clones to prevent local GC from causing this; server-side GC is the operator's responsibility.

### 4.8. Force-push handling

A force-push by another writer rewrites branch history in a non-fast-forward way. Core detects this on fetch when the common ancestor check fails.

Required behavior:

1. Log the event at error level with both the old and new ref SHAs.
2. Invalidate all in-flight writes; they are surfaced to callers as `WriteResult.Conflict` with `ConflictReason.HistoryRewritten`.
3. Drain the writer grain's pending batch buffer (Orleans layer) with the same conflict reason before reopening Core.
4. Discard the local clone's in-memory state and reload from the new remote state.
5. Rebuild indexes from scratch (or reload from a valid snapshot whose commit SHA exists in the new history).
6. Fire a `ChangeNotification` with `Reason = HistoryRewritten` so subscribers can react.

Force-pushes should not occur in normal operation but the library must not corrupt state when they do.

### 4.9. Read consistency controls

`ReadOptions` lets callers control freshness:

```csharp
public class ReadOptions
{
    public bool FetchFirst { get; init; } = false;
    public TimeSpan? MaxStaleness { get; init; }
}
```

- Default: reads are served from the local clone as-is. Maximum performance, bounded freshness.
- `FetchFirst = true`: forces a fetch before the read. Strongest freshness; adds round-trip latency to the Git server.
- `MaxStaleness`: if `LastFetchedAt` indicates the local clone is older than the specified duration, fetch first; otherwise serve locally.

The library exposes the clone's current state via `IDatabase.CurrentCommit` and `LastFetchedAt` for observability.

### 4.10. Snapshot architecture and lock-free reads

Core maintains an immutable **snapshot** object per database:

```csharp
internal sealed record DatabaseSnapshot(
    string CommitSha,
    DateTimeOffset FetchedAt,
    ITreeView Tree,
    IReadOnlyDictionary<string, ITableSnapshot> Tables);

internal sealed record TableSnapshot(
    string Name,
    ITreeView TableTree,
    IReadOnlyDictionary<string, IIndex> Indexes);
```

A single `AtomicReference<DatabaseSnapshot>` represents "current state." Reads take the reference once and query it; they never acquire a lock.

Writes build a new snapshot off the current one (with structural sharing for unchanged tables), push, and on success atomically swap the reference. On failure the new snapshot is discarded.

`DatabaseOptions.MaxSnapshotCacheCount` (default 2) bounds how many recent snapshots are retained for concurrent readers. Older snapshots are released to the GC once no reader holds them.

### 4.11. Change notifications

`IDatabase.WatchAsync` yields `ChangeNotification` events whenever a fetch (manual or triggered by a background loop) observes new commits. Notifications include the new commit SHA, the list of paths changed since the previous state, and the `ChangeReason` (normal remote advance or history rewrite).

A background fetch loop is configured via database options:

```csharp
public class DatabaseOptions
{
    public bool EnableBackgroundFetch { get; init; } = false;
    public TimeSpan BackgroundFetchInterval { get; init; } = TimeSpan.FromSeconds(60);
}
```

When enabled, the library fetches on the configured interval and raises notifications as needed. Disabled by default because consumers often want explicit control; the Orleans wrapper enables it on reader grain services by default.

### 4.12. Indexing

Indexes are declared in the repository's `.schema.json`:

```json
{
  "schemaVersion": 1,
  "tables": {
    "accounts": {
      "indexes": [
        { "field": "email", "type": "equality", "unique": true },
        { "field": "status", "type": "equality" },
        { "field": "createdAt", "type": "range" }
      ]
    }
  }
}
```

Record shape is determined by the .NET type bound via `GetTable<T>(name)`. The schema file governs only table existence and indexes.

Index types:

- **equality** — hash map from value to record IDs. Supports exact-match queries.
- **range** — sorted structure (immutable sorted dictionary or B-tree snapshot) supporting range and ordering queries.
- **unique equality** — as above, but writes fail with `ConflictReason.UniqueViolation` if a duplicate value is inserted. Uniqueness is re-verified after every fetch-and-rebase during push retry (§4.5).

Index implementation:

- Equality: `FrozenDictionary<TValue, ImmutableArray<string>>` per snapshot; mutations create a new dictionary with structural reuse where possible.
- Range: `ImmutableSortedDictionary<TValue, ImmutableArray<string>>`; supports O(log n) range iteration.
- Unique equality: `FrozenDictionary<TValue, string>` with a uniqueness check before replace.

Indexes are built in memory on database open, by walking the table's tree. On fetch, indexes are updated incrementally using Git tree diffing (`git_diff_tree_to_tree`) rather than rebuilt from scratch.

**Index snapshot persistence (v1).** After a successful open or fetch, indexes for each table are serialized to a local file keyed by commit SHA:

```
{LocalClonePath}/{databaseName}/.indexes/{commitSha}/{tableName}.bin
```

On open, if a snapshot exists for the current commit SHA, it is loaded instead of rebuilding. Snapshots older than the current commit (and its retained ancestors) are garbage-collected after a successful load. Format is a compact binary layout (length-prefixed strings + packed values) — not a public format.

This replaces the "rebuild on every open" behavior originally deferred to v2. Cold-start latency for large databases drops from O(records) to O(snapshot read).

### 4.13. Query API

Queries are built via a fluent builder:

```csharp
public class Query
{
    public static QueryBuilder<T> For<T>() where T : class;
}

public class QueryBuilder<T>
{
    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate);
    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> key);
    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> key);
    public QueryBuilder<T> Skip(int count);
    public QueryBuilder<T> Take(int count);
    public Query Build();
}
```

Supported predicate forms (v1):

- `x.Field == constant` — equality (uses equality index if available).
- `x.Field > constant` / `>=` / `<` / `<=` — range (uses range index if available).
- `x.Field >= a && x.Field <= b` — combined range.
- Conjunctions (`&&`) across multiple indexed fields — intersection.
- Disjunctions (`||`) across indexed fields — union.
- Anything else falls back to in-memory evaluation on candidate IDs, or a full scan if no indexed clause is present.

Execution strategy:

1. Analyze predicate; extract indexed sub-clauses.
2. If any indexed clause matches, use the indexes to produce a candidate set of record IDs.
3. Load candidate records from the current snapshot.
4. Apply any remaining predicate evaluation in memory.
5. Apply ordering and pagination.

Full-table scans (no usable index) are permitted but log a warning. An option rejects full scans on large tables:

```csharp
public class DatabaseOptions
{
    public int MaxFullScanRecordCount { get; init; } = 10_000;
}
```

Queries beyond this threshold against unindexed fields fail with `QueryException`.

### 4.14. Transport abstraction

Core abstracts the Git transport to support multiple connection types and testability:

```csharp
public interface IGitConnection
{
    Task<FetchResult> FetchAsync(string refName, CancellationToken ct);
    Task<PushResult> PushAsync(string refName, string expectedOldSha, string newSha, CancellationToken ct);
    Task<ReadOnlyMemory<byte>> GetBlobAsync(string sha, CancellationToken ct);
    Task<ITreeView> GetTreeAsync(string commitSha, CancellationToken ct);
    Task<string> CreateCommitAsync(CommitSpec spec, CancellationToken ct);
    Task<IReadOnlyList<string>> ListRemoteRefsAsync(string prefix, CancellationToken ct);
}
```

Implementations shipped as sibling packages:

- `GitDocumentDb.Transport.LibGit2Sharp` — uses LibGit2Sharp against a local clone and a remote. Default for HTTPS/SSH connections. Credentials provided via `IGitCredentialsProvider`.
- `GitDocumentDb.Transport.InMemory` — uses libgit2 mempack backend with no remote. For unit tests.
- `GitDocumentDb.Transport.LocalBare` — uses a local bare repository as the "remote." For integration tests and single-machine dev.

Consumers of Core depend only on the abstraction; transports are chosen via DI at composition time.

### 4.15. Serialization

Record serialization is pluggable:

```csharp
public interface IRecordSerializer
{
    void Serialize<T>(T record, IBufferWriter<byte> output);
    T Deserialize<T>(ReadOnlySpan<byte> input);
    string FileExtension { get; }
}
```

Default: `SystemTextJsonRecordSerializer` using `Utf8JsonReader`/`Utf8JsonWriter` on `ArrayPool<byte>`-backed buffers.

The serializer's `FileExtension` determines the record file suffix (e.g., `.json`).

### 4.16. Storage configuration

The database's local clone can live on disk or in a memory-backed filesystem (tmpfs). This is application configuration:

```csharp
public class DatabaseOptions
{
    public string LocalClonePath { get; init; } = "/var/lib/gitdb/clones";
    public StorageMode StorageMode { get; init; } = StorageMode.Disk;
    public CloneStrategy CloneStrategy { get; init; } = CloneStrategy.Full;
    public long TmpfsMaxBytes { get; init; } = 2L * 1024 * 1024 * 1024; // 2 GB
    public long RecordSizeSoftLimitBytes { get; init; } = 1L * 1024 * 1024;       // 1 MB warn
    public long RecordSizeHardLimitBytes { get; init; } = 10L * 1024 * 1024;      // 10 MB reject
    public int MaxSnapshotCacheCount { get; init; } = 2;
}

public enum StorageMode { Disk, Tmpfs }
public enum CloneStrategy { Full, Shallow, PartialBlobless }
```

Guidelines:

- **Tmpfs**: repository fully resident in RAM. Appropriate for databases under ~500 MB. Use with caution for larger databases as RAM cost scales with repository size, not working set.
- **Disk (default)**: clone on container-local or persistent disk. OS page cache handles hot data. Appropriate for any size.
- **Full clone**: complete history. Supports historical queries.
- **Shallow clone** (`--depth 1`): current state only. Fast activation; no history queries.
- **Partial blobless clone** (`--filter=blob:none`): trees and commits only; blobs fetched on-demand. Appropriate for very large databases with skewed access.

Core validates `StorageMode.Tmpfs` against `TmpfsMaxBytes` at open time; if the repository exceeds the threshold, it falls back to disk with a warning.

**Record size enforcement.** Records over `RecordSizeSoftLimitBytes` log a warning. Records over `RecordSizeHardLimitBytes` fail the write with `WriteFailureReason.RecordTooLarge`.

### 4.17. Error model

Core defines a hierarchy of exceptions:

- `GitDocumentDbException` — base class.
- `PushRejectedException` — push rejected after retries exhausted. Wraps `BatchResult` where applicable.
- `HistoryRewrittenException` — force-push detected.
- `QueryException` — query failed (e.g., unindexed scan above threshold, malformed expression).
- `TransportException` — underlying Git transport error (network, auth, etc.).

`WriteResult` and `BatchResult` carry `ConflictInfo` and `WriteFailureReason` rather than throwing for expected outcomes — concurrency conflicts, schema violations, invalid IDs, and oversized records are results, not exceptions. Exceptions are reserved for programmer errors (unknown table, malformed schema file) and infrastructure failures (transport, force-push).

### 4.18. Thread safety

- `IDocumentDatabase` and `IDatabase` instances are thread-safe. Multiple threads may call methods concurrently.
- Reads are lock-free (§4.10) and never block writes or other reads.
- Writes to the same database within one process are serialized through a per-database async lock. Concurrent writes queue and are handled sequentially. The lock is held only for the duration of the push (network round-trip) plus the snapshot swap.
- The write lock does not coordinate across processes — that is what the Git server is for.

### 4.19. Memory efficiency

Hot paths target minimal allocation:

- **Serialization**: `Utf8JsonReader` / `Utf8JsonWriter` backed by `ArrayPool<byte>`. Records are serialized into pooled buffers, not intermediate strings.
- **Blob content**: `ReadOnlyMemory<byte>` / `ReadOnlySpan<byte>` throughout the read path.
- **Snapshots**: immutable and shared between readers. Structural sharing on fetch — unchanged tables reuse their `TableSnapshot` reference.
- **Indexes**: compact representations (`FrozenDictionary`, `ImmutableSortedDictionary`) with arrays (not lists) for record-ID buckets.
- **String interning**: schema-side field names and table names interned at database open (bounded, known-size set).
- **Task allocations**: `ValueTask<T>` on hot read APIs (`GetAsync`, snapshot lookups) to avoid `Task` allocation on the sync fast path.
- **Pools**: `MemoryPool<byte>` and `ArrayPool<byte>` for serialization; per-database `ObjectPool<WriteOperation<T>>` for batch buffers.
- **LINQ-free hot paths**: index lookup and snapshot traversal use explicit loops and struct enumerators.

Targets (§8 metrics will verify):

- Steady-state read: ≤2 allocations per `GetAsync` (the result `Versioned<T>` and the deserialized record).
- Steady-state fetch with no changes: zero snapshot allocations.
- Batch commit: ≤1 allocation per operation beyond the record itself.

## 5. Library 2: GitDocumentDb.Orleans

### 5.1. Responsibilities

Orleans library responsibilities:

- Host Core library instances inside Orleans grains.
- Serialize in-cluster concurrent writes via single-activation grains, reducing conflict rate.
- Batch pending writes to reduce push frequency.
- Distribute change notifications across silos via Orleans streams.
- Manage grain lifecycle (activation, deactivation, failover) in coordination with Core.

Orleans library does not:

- Reimplement concurrency modes, conflict handling, or merge logic.
- Assume grain single-activation is sufficient for correctness. Core's multi-writer safety is still active and essential.
- Introduce infrastructure dependencies beyond Orleans itself (cluster membership via Kubernetes DNS or static clustering).

### 5.2. Grain topology

**`IDatabaseWriterGrain`** — one activation per database cluster-wide (under normal operation; transient double-activation during failover is possible and safely handled by Core).
- Owns a Core `IDatabase` instance.
- Receives write requests, batches them, calls `ITable.CommitAsync`.
- Serves `GetStrongAsync` reads directly from its local snapshot (no cross-silo hop).
- Publishes commit notifications on successful push.
- Marked `[Reentrant]` so read calls do not block behind the async batch flush.

**`IDatabaseReaderGrainService`** — one instance per silo (Orleans `IGrainService`).
- Owns a Core `IDatabase` instance with `EnableBackgroundFetch = true`.
- Maintains indexes via Core.
- Subscribes to commit notifications (implicit stream subscription; see §5.7).
- Fetches and updates on notification; falls back on background fetch interval.

**`ITableQueryGrain`** — stateless worker, local-only placement.
- Fronts the reader grain service for query traffic.
- Reads land on the local silo; zero cross-silo hops for reads.

Writer single-activation is a throughput optimization, not a mutex. Core's push-time conflict detection is what guarantees correctness under transient double-activation or against external writers.

### 5.3. Write path via Orleans

1. Client calls `ITableQueryGrain.PutAsync(...)` or similar entry point.
2. The query grain routes the write to the `IDatabaseWriterGrain` for the relevant database.
3. Writer grain appends the operation to its in-memory pending buffer with a `TaskCompletionSource` for the caller.
4. On the next batch flush (timer or size threshold), the writer grain:
   a. Fetches the latest remote state (cheap — just a ref update).
   b. Calls `ITable.CommitAsync` on its Core instance with the batched operations.
   c. On success, completes all pending TCSes with the result.
   d. On failure (e.g., persistent push rejection or per-op conflicts), completes TCSes with the per-operation result or conflict info.
5. After a successful push, the writer grain publishes a `CommitNotification` on an Orleans stream keyed to the database.

The batch flush runs on a long-lived background task inside the grain. Grain calls that enqueue writes return quickly (the TCS is the coordination point). Read calls (`GetStrongAsync`) return directly from the writer's snapshot without waiting for a flush.

### 5.4. Batching configuration

```csharp
public class OrleansWriterOptions
{
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(20);
    public int MaxBatchSize { get; init; } = 50;
    public long MaxBatchBytes { get; init; } = 1024 * 1024; // 1 MB
    public int MaxPendingOperations { get; init; } = 10_000;
}
```

Flush triggers when any of: flush interval elapses with pending operations, batch size reaches `MaxBatchSize`, batch bytes reach `MaxBatchBytes`.

`MaxPendingOperations` provides backpressure. Beyond this, writes are rejected with a transient error until the buffer drains.

### 5.5. Immediate flush

For latency-sensitive writes, clients may request immediate flush:

```csharp
Task<WriteResult> PutImmediate<T>(string table, string id, T record, WriteOptions? options);
```

This triggers a flush on the writer grain without waiting for the timer. To be used sparingly — routine use defeats batching.

### 5.6. Read path via Orleans

1. Client calls `ITableQueryGrain` on any silo.
2. As a stateless worker, the call lands on the local silo.
3. The query grain obtains a reference to the silo-local `IDatabaseReaderGrainService` via DI.
4. The reader grain service serves the read from its in-memory snapshot (lock-free) and indexes.
5. Result returns to client. No cross-silo hops.

For strong-consistency reads, the client routes to the writer grain instead:

```csharp
Task<Versioned<T>?> GetStrongAsync<T>(string table, string id);
```

This call lands on the writer grain's silo and reads from its snapshot (which includes the effects of just-pushed commits visible to the writer before readers receive the notification). The reentrant writer grain serves this call without waiting for an in-flight flush.

### 5.7. Notification fan-out

After a successful push, the writer grain publishes to an Orleans stream:

```
StreamNamespace = "GitDocumentDb.Commits"
StreamKey       = databaseName
```

Reader grain services subscribe via `[ImplicitStreamSubscription("GitDocumentDb.Commits")]`. Implicit subscriptions do not require a PubSubStore and work cluster-wide via the Orleans grain directory, so no external storage or broker is needed.

On notification:

1. Fetch the latest commits from the remote.
2. Update indexes incrementally.
3. Swap the snapshot reference.

Orleans streams are configured with the in-memory stream provider. Notifications are best-effort; the 60s fallback fetch loop in Core catches any missed events.

### 5.8. Placement strategies

- `IDatabaseWriterGrain`: default random placement. Custom placement preferring silos with warm clones is deferred to v2.
- `IDatabaseReaderGrainService`: one per silo (grain services are silo-local by definition).
- `ITableQueryGrain`: `[StatelessWorker]` ensures local-only placement.

### 5.9. Clustering

Orleans clustering is configured via:

- **Kubernetes DNS clustering**: StatefulSet with stable pod DNS names. Preferred for Kubernetes deployments.
- **Static clustering**: fixed silo list. For dev or fixed topologies.

No external cluster membership store (ADO.NET, Azure Table, etc.) is introduced. Consumers may choose to use one if they wish, but it is not required.

### 5.10. Failure and recovery

**Writer grain crash**: Orleans reactivates on another silo. The new activation opens its Core instance, which fetches the latest state. Any pending writes that had not been pushed are lost; their clients did not receive acks and must retry. `MaxPendingOperations` provides an upper bound on the number of lost in-flight writes.

**Transient double-activation during failover**: both activations may push concurrently; Core's push-time conflict detection handles this. Worst case: some writes see `PushRejected` and clients retry.

**Reader grain service crash**: transparent to clients connecting to other silos. On the affected silo, the grain service restarts on next call; Core opens and fetches (or loads from index snapshot, §4.12).

**Git server outage**: writes buffer up to `MaxPendingOperations`. Beyond that, new writes are rejected with a transient error. Reads continue from local snapshots.

**Orleans stream delivery failure**: caught by Core's background fetch loop (enabled by the Orleans wrapper for all reader grain services).

### 5.11. Activation warm-up

```csharp
public class OrleansReaderOptions
{
    public bool EnableBackgroundFetch { get; init; } = true;
    public TimeSpan BackgroundFetchInterval { get; init; } = TimeSpan.FromSeconds(60);
    public IReadOnlyList<string> EagerlyActivateDatabases { get; init; } = Array.Empty<string>();
}
```

For databases listed in `EagerlyActivateDatabases`, the reader grain service opens Core eagerly on silo start (during `IGrainService.Start`). Other databases open lazily on first query.

### 5.12. Configuration surface

```csharp
public class OrleansDatabaseOptions
{
    public DatabaseOptions Core { get; init; }
    public OrleansWriterOptions Writer { get; init; }
    public OrleansReaderOptions Reader { get; init; }
}

public interface IDatabaseConfigurationProvider
{
    OrleansDatabaseOptions GetOptionsFor(string databaseName);
}
```

Default implementation reads from `IOptionsMonitor<OrleansDatabaseOptionsMap>` backed by configuration (appsettings, Kubernetes ConfigMap, etc.). Per-environment configs select storage mode, clone strategy, batch timing, and cache sizes without touching the repository.

### 5.13. Registration

```csharp
services.AddGitDocumentDb(options =>
{
    options.RemoteUrl = "https://git.internal/db-storage.git";
    options.CredentialsProvider = sp => new TokenCredentialsProvider(...);
})
.AddOrleansHosting(options =>
{
    options.DefaultConcurrencyMode = ConcurrencyMode.OptimisticReject;
    options.DefaultStorageMode = StorageMode.Disk;
    // per-DB overrides loaded from IConfiguration
});
```

## 6. Package structure

```
GitDocumentDb.Core
├── Public interfaces (IDocumentDatabase, IDatabase, ITable<T>, etc.)
├── Concurrency modes and conflict detection
├── Index management (equality, range, unique) and snapshot persistence
├── Query execution engine
├── Schema loading and validation
├── IGitConnection abstraction
├── IRecordMerger<T> and default JSON patch merger
└── IRecordSerializer and default System.Text.Json serializer

GitDocumentDb.Transport.LibGit2Sharp
└── IGitConnection implementation for real Git servers via LibGit2Sharp

GitDocumentDb.Transport.InMemory
└── IGitConnection implementation using libgit2 mempack for tests

GitDocumentDb.Transport.LocalBare
└── IGitConnection implementation using local bare repo for dev/tests

GitDocumentDb.Orleans
├── Writer grain, reader grain service, stateless query grain
├── Batching and pending buffer
├── Implicit stream subscription for notifications
└── DI registration extensions

GitDocumentDb.Orleans.Abstractions
└── Grain interfaces only (for client-only packages)
```

## 7. Testing requirements

### 7.1. Core library tests

All Core tests use `Transport.InMemory` or `Transport.LocalBare`. No Orleans. No network.

Required scenarios:

- CRUD basics under each `ConcurrencyMode`.
- Create-if-absent (`ExpectedVersion = Versions.Absent`): succeeds when absent, conflicts when present.
- Delete-if-present with `OptimisticReject` on missing record.
- Multi-writer: two `IDatabase` instances against the same bare repo. Each mode.
- Three-way merge: non-overlapping changes succeed; overlapping changes fail with `Conflict`.
- Transport-level conflict: simulate push rejection; verify retry-then-rebase.
- Logical conflict escalated from transport conflict: verify per-mode behavior.
- Unique index under concurrent writers: two writers insert the same value; one succeeds, one gets `UniqueViolation`.
- Force-push detection: mutate bare repo out-of-band; verify `HistoryRewritten` handling and in-flight write drain.
- Read consistency: `FetchFirst`, `MaxStaleness`, no-fetch behaviors.
- Snapshot immutability: concurrent readers during a write complete with consistent results.
- Index correctness: equality, range, unique. After writes, deletes, mixed operations.
- Index snapshot persistence: round-trip serialize/deserialize; load-on-open for matching commit SHA.
- Index rebuild on open (when snapshot absent): performance bounded for tables with 100k, 1M records.
- Query execution: indexed and full-scan paths; full-scan threshold enforcement; predicate forms listed in §4.13.
- Schema loading and validation on write (unknown table fails).
- Record ID validation: valid IDs accepted, invalid rejected.
- Record size limits: warn at soft, reject at hard.
- Change notification delivery on fetch.
- Retry backoff: exponential with jitter, bounded by `MaxRetryBackoff`.
- Allocation benchmarks: hot-path targets in §4.19 verified via `BenchmarkDotNet` + `MemoryDiagnoser`.

### 7.2. Orleans library tests

Orleans tests use `Orleans.TestingHost.TestCluster` with Core's `Transport.LocalBare`.

Required scenarios:

- Batch coalescing: multiple concurrent writes produce single commits.
- Writer grain `[Reentrant]` behavior: concurrent `GetStrongAsync` during batch flush does not block.
- Writer grain failover: crash and reactivate; no data corruption.
- Transient double-activation: forced via test hooks; Core conflict detection preserves correctness.
- Reader notification via implicit stream subscription: writer push observed by readers on all silos within bounded time.
- Stream delivery failure: readers still converge via fallback fetch.
- Cross-silo query: stateless worker placement keeps reads local.
- Strong-consistency read through writer grain.
- Backpressure: `MaxPendingOperations` enforced.
- Eager activation: databases in `EagerlyActivateDatabases` are ready before first query.

### 7.3. Integration tests

Integration tests run against a real Git server (Gitea in a container) with both libraries composed.

Required smoke tests:

- End-to-end write, read, query.
- Multiple Orleans cluster instances against the same repo — cross-cluster conflict detection.
- Orleans cluster plus a CLI tool using Core directly against the same repo — multi-consumer correctness.
- Large-database activation timing with each clone strategy.
- Server-side GC interaction (base-blob unavailability path).

## 8. Observability

Metrics (via `System.Diagnostics.Metrics`):

- `gitdb.writes.total` (counter, per database, per mode, per outcome).
- `gitdb.writes.latency` (histogram, per database).
- `gitdb.batches.size` (histogram, per database).
- `gitdb.batches.bytes` (histogram, per database).
- `gitdb.push.attempts.total` (counter, per database).
- `gitdb.push.rejections.total` (counter, per database, per reason).
- `gitdb.push.latency` (histogram, per database).
- `gitdb.fetch.total` (counter, per database, per trigger).
- `gitdb.fetch.latency` (histogram, per database).
- `gitdb.reads.total` (counter, per database, per table, per index/full-scan).
- `gitdb.reads.latency` (histogram, per database, per table).
- `gitdb.conflicts.total` (counter, per database, per mode, per reason).
- `gitdb.clone.bytes` (gauge, per database).
- `gitdb.clone.cold_start.latency` (histogram, per database — from open to ready-to-serve).
- `gitdb.index.rebuild.latency` (histogram, per database, per table).
- `gitdb.index.snapshot.loaded` (counter, per database, per table).
- `gitdb.indexes.entries` (gauge, per database, per table, per index).
- `gitdb.snapshot.allocated.bytes` (gauge, per database).
- `gitdb.activation.latency` (histogram, Orleans-only, per grain type).

Logs:

- Force-push detection: error level.
- Push rejection after retries exhausted: error level.
- Schema violations (write rejected): warning level.
- Full-table scans: warning level with table name and estimated cost.
- Record size soft-limit exceeded: warning level.
- Transport failures: warning level.
- Successful pushes, fetches, activations: debug level.

## 9. Deferred to v2

- **Partial-success batches**: `BatchResult` with per-operation outcomes where some succeed. v1 is all-or-nothing.
- **Repository maintenance coordination**: `git gc`, repack, branch cleanup. v1 assumes server-side scheduled maintenance.
- **Schema migrations**: tooling to migrate record shapes across large tables. v1 relies on application-level migration.
- **Custom placement for writer grains**: prefer silos with warm clones. v1 uses default random placement.
- **Cross-database transactions**: saga-style coordination. Out of scope; not planned.
- **Large binary records**: externalize to blob storage with references in Git. Out of scope for v1.
- **Historical queries (time-travel)**: query against a past commit SHA. Out of scope for v1.

## 10. Non-goals

- **Full SQL query language.** Document store with indexed queries only. No joins, no aggregates beyond basic count/exists, no query planner.
- **Strong consistency across databases.** Each database is its own atomicity domain.
- **Replacement for OLTP databases at high-transaction-rate workloads.** Target is ~500–2000 writes/sec per database.
- **Hiding Git.** The repository is human-readable and human-editable. Manual commits are handled as external writes by the library. This is a feature.

## 11. Summary of key design decisions

1. **Single Git repository, one branch per database.** Branch name `db/{name}`. `main` is reserved for repo-level metadata.
2. **Two libraries**: Core (embedded, multi-writer-safe) and Orleans (distribution wrapper).
3. **Concurrency modes per write**: `LastWriteWins`, `OptimisticReject`, `OptimisticMerge`, with per-database default and per-call override.
4. **Version tokens = blob SHAs**; content-addressed, Git-native. `Versions.Absent` sentinel for create-if-absent.
5. **Batches are atomic**: all-or-nothing at the Git commit level.
6. **Pluggable record mergers** via `IRecordMerger<T>`.
7. **Pluggable serializers** via `IRecordSerializer`; default System.Text.Json with pooled buffers.
8. **Force-push detected and handled**, never silently absorbed.
9. **Pluggable transports** (HTTPS/SSH via LibGit2Sharp, in-memory for tests, local bare for dev).
10. **Storage mode per database** (disk or tmpfs) as app config, not repo config.
11. **Index definitions in repository**, index data in memory with on-disk snapshot per commit SHA.
12. **Lock-free reads** via immutable snapshots with structural sharing.
13. **Orleans writer grain is `[Reentrant]`**; reads never block behind a flushing batch.
14. **Orleans adds batching and fan-out** but does not alter Core correctness semantics.
15. **Stateless worker query grains** for zero-hop reads.
16. **Implicit stream subscriptions** for cluster-wide notification fan-out — no PubSubStore, no external broker.
17. **Kubernetes DNS or static clustering** — no external cluster membership store.
18. **Retry backoff is exponential with jitter**, bounded by `MaxRetryBackoff`.
19. **Record ID/name validation** enforced at the API boundary.
20. **Record size soft/hard limits** enforced per database.
21. **Memory-conscious APIs**: `ValueTask`, `ReadOnlyMemory<byte>`, pooled buffers, LINQ-free hot paths.
