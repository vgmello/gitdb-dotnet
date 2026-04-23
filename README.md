# GitDocumentDb

A .NET document database backed by a Git repository. See `docs/superpowers/specs/` for the design.

## Phase 1 scope

- `GitDocumentDb.Core` library with:
  - `IDocumentDatabase` / `IDatabase` / `ITable<T>` public API
  - `IGitConnection` transport abstraction
  - In-memory transport for testing (no libgit2 dependency)
  - `LastWriteWins` concurrency with push-rejection retries
  - Lock-free reads via immutable snapshots
  - System.Text.Json serializer (pluggable)
  - Record ID / name validation and size limits
  - Change notifications

Later phases add `OptimisticReject` / `OptimisticMerge`, indexing, queries,
real Git transports (LibGit2Sharp, LocalBare), and the Orleans distribution layer.

## Build and test

```bash
dotnet build
dotnet test
```
