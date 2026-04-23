# GitDocumentDb

A .NET document database backed by a Git repository. See `docs/superpowers/specs/` for the design.

## Status

- **Phase 1** (complete): Core library with `IGitConnection` abstraction, in-memory
  transport, `LastWriteWins` concurrency, lock-free reads via immutable snapshots,
  System.Text.Json serializer, record ID/name/size validation, change notifications.
- **Phase 2** (complete): `OptimisticReject` + `OptimisticMerge` concurrency modes,
  `IRecordMerger<T>` abstraction with `JsonPatchMerger<T>` default, create-if-absent
  via `Versions.Absent`, force-push detection + `HistoryRewritten` notifications.

Later phases add indexing + queries (Phase 3), real Git transports (Phase 4),
and the Orleans distribution layer (Phase 5).

## Build and test

```bash
dotnet build
dotnet test
```
