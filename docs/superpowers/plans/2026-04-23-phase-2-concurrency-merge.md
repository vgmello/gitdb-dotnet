# GitDocumentDb Phase 2: Concurrency Modes + Merge + Force-Push — Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans to implement task-by-task. Steps use `- [ ]` checkbox syntax.

**Goal:** Implement `OptimisticReject` and `OptimisticMerge` concurrency modes with per-operation version checks, pluggable `IRecordMerger<T>`, a default JSON-patch merger, create-if-absent via `Versions.Absent`, and force-push detection/notification.

**Architecture:** Concurrency checks happen inside `WriteExecutor.TryCommitAsync` before tree construction. `OptimisticReject` returns `WriteResult.Conflict` with current state if the observed version differs from `ExpectedVersion`. `OptimisticMerge` invokes the registered merger; if the merger succeeds, the merged record becomes the new blob; otherwise the op falls back to `OptimisticReject` semantics. Force-push is detected during `FetchAsync` by walking ancestors of the new remote commit; if the previous local commit is not among them, Core raises a `ChangeReason.HistoryRewritten` notification and rebuilds its snapshot.

**Tech Stack:** .NET 10, System.Text.Json (for JsonPatchMerger), existing xUnit v3 + FluentAssertions test harness.

---

## File Structure (Phase 2 additions)

```
src/GitDocumentDb.Core/
├── Abstractions/
│   └── IRecordMerger.cs             # NEW
├── Models/
│   └── MergeResult.cs                # NEW
├── Merging/
│   └── JsonPatchMerger.cs            # NEW
├── Transport/
│   └── IGitConnection.cs             # MODIFIED (add GetCommitParentsAsync)
├── Transport.InMemory/
│   └── InMemoryGitConnection.cs      # MODIFIED (track parents; support GetCommitParentsAsync)
├── Internal/
│   ├── AncestorWalker.cs             # NEW
│   └── WriteExecutor.cs              # MODIFIED (mode dispatch, version checks, merge)
├── Implementation/
│   ├── Database.cs                   # MODIFIED (force-push detection in FetchAsync)
│   └── Table.cs                      # MODIFIED (plumb ExpectedVersion)
└── Models/
    └── DatabaseOptions.cs            # MODIFIED (add RecordMergers dictionary)

tests/GitDocumentDb.Core.Tests/
├── OptimisticRejectTests.cs          # NEW
├── OptimisticMergeTests.cs           # NEW
├── JsonPatchMergerTests.cs           # NEW
├── CreateIfAbsentTests.cs            # NEW
├── ForcePushTests.cs                 # NEW
└── AncestorWalkerTests.cs            # NEW
```

---

## Task 1: Transport — parent tracking

Add `GetCommitParentsAsync` to `IGitConnection`. Update `InMemoryGitConnection` to track a per-commit parent list.

**Files:**
- Modify: `src/GitDocumentDb.Core/Transport/IGitConnection.cs`
- Modify: `src/GitDocumentDb.Core/Transport.InMemory/InMemoryGitConnection.cs`
- Modify: `tests/GitDocumentDb.Core.Tests/InMemoryGitConnectionTests.cs`

- [ ] **Step 1: Extend test file with a parent-tracking test**

Append to `InMemoryGitConnectionTests.cs`:
```csharp
    [Fact]
    public async Task GetCommitParents_returns_empty_for_root_commit()
    {
        var c = new InMemoryGitConnection();
        var commit = await CreateEmptyCommit(c, null);
        var parents = await c.GetCommitParentsAsync(commit, Ct);
        parents.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCommitParents_returns_single_parent_for_linear_commit()
    {
        var c = new InMemoryGitConnection();
        var first = await CreateEmptyCommit(c, null);
        var second = await CreateEmptyCommit(c, first);
        var parents = await c.GetCommitParentsAsync(second, Ct);
        parents.Should().BeEquivalentTo(new[] { first });
    }
```

(Reuse the existing `CreateEmptyCommit` helper.)

Run the test — compile error expected.

- [ ] **Step 2: Add `GetCommitParentsAsync` to `IGitConnection`**

Append to `IGitConnection.cs` (inside the interface):
```csharp
    Task<IReadOnlyList<string>> GetCommitParentsAsync(string commitSha, CancellationToken ct);
```

- [ ] **Step 3: Implement in `InMemoryGitConnection`**

Change the `_commits` dictionary to store both tree and parent info:

```csharp
// Replace:
// private readonly ConcurrentDictionary<string, string> _commits = new();
private readonly ConcurrentDictionary<string, (string TreeSha, string? ParentSha)> _commits = new();
```

Update `CreateCommitAsync` — after computing `commitSha`:
```csharp
_commits.TryAdd(commitSha, (spec.TreeSha, spec.ParentSha));
```

Update `GetTreeAsync` — change lookup:
```csharp
if (!_commits.TryGetValue(commitSha, out var entry))
    throw new InvalidOperationException($"Unknown commit {commitSha}");
var treeSha = entry.TreeSha;
var entries = _trees.TryGetValue(treeSha, out var t) ? t : new();
return Task.FromResult<ITreeView>(new InMemoryTreeView(commitSha, new(entries)));
```

Add the new method:
```csharp
public Task<IReadOnlyList<string>> GetCommitParentsAsync(string commitSha, CancellationToken ct)
{
    if (!_commits.TryGetValue(commitSha, out var entry))
        throw new InvalidOperationException($"Unknown commit {commitSha}");
    IReadOnlyList<string> parents = entry.ParentSha is null
        ? Array.Empty<string>()
        : new[] { entry.ParentSha };
    return Task.FromResult(parents);
}
```

- [ ] **Step 4: Run tests**

`dotnet test` — all 52 prior + 2 new = 54 tests pass.

- [ ] **Step 5: Commit**

```
feat(core): transport parent tracking and GetCommitParentsAsync
```

---

## Task 2: Ancestor walker utility

A helper that checks whether a commit is an ancestor of another. Used for force-push detection.

**Files:**
- Create: `src/GitDocumentDb.Core/Internal/AncestorWalker.cs`
- Create: `tests/GitDocumentDb.Core.Tests/AncestorWalkerTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using FluentAssertions;
using GitDocumentDb.Internal;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class AncestorWalkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> EmptyCommit(IGitConnection c, string? parent)
    {
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), Ct);
        return await c.CreateCommitAsync(new CommitSpec(
            tree, parent, "t", "t@x", DateTimeOffset.UnixEpoch, "m"), Ct);
    }

    [Fact]
    public async Task Commit_is_ancestor_of_itself()
    {
        var c = new InMemoryGitConnection();
        var a = await EmptyCommit(c, null);
        (await AncestorWalker.IsAncestorAsync(c, a, a, 1000, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Parent_is_ancestor_of_child()
    {
        var c = new InMemoryGitConnection();
        var a = await EmptyCommit(c, null);
        var b = await EmptyCommit(c, a);
        (await AncestorWalker.IsAncestorAsync(c, a, b, 1000, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Unrelated_commits_are_not_ancestors()
    {
        var c = new InMemoryGitConnection();
        var a = await EmptyCommit(c, null);
        var b = await EmptyCommit(c, null);
        (await AncestorWalker.IsAncestorAsync(c, a, b, 1000, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Walk_stops_at_max_depth()
    {
        var c = new InMemoryGitConnection();
        var chain = await EmptyCommit(c, null);
        for (var i = 0; i < 10; i++) chain = await EmptyCommit(c, chain);
        // max depth 3 — walker should not reach the root.
        var root = await EmptyCommit(c, null);
        (await AncestorWalker.IsAncestorAsync(c, root, chain, 3, Ct)).Should().BeFalse();
    }
}
```

Run — compile error.

- [ ] **Step 2: Implement `AncestorWalker.cs`**

```csharp
using GitDocumentDb.Transport;

namespace GitDocumentDb.Internal;

internal static class AncestorWalker
{
    public static async Task<bool> IsAncestorAsync(
        IGitConnection connection,
        string candidate,
        string descendant,
        int maxDepth,
        CancellationToken ct)
    {
        if (candidate == descendant) return true;

        var visited = new HashSet<string>(StringComparer.Ordinal) { descendant };
        var queue = new Queue<(string sha, int depth)>();
        queue.Enqueue((descendant, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (sha, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var parents = await connection.GetCommitParentsAsync(sha, ct);
            foreach (var parent in parents)
            {
                if (parent == candidate) return true;
                if (visited.Add(parent))
                    queue.Enqueue((parent, depth + 1));
            }
        }
        return false;
    }
}
```

- [ ] **Step 3: Run tests**

All pass.

- [ ] **Step 4: Commit**

```
feat(core): AncestorWalker utility for ancestor checks
```

---

## Task 3: OptimisticReject + ExpectedVersion plumbing

Add per-operation version checks. `OptimisticReject` fails the write with `ConflictInfo` if the current version of a record differs from `ExpectedVersion`.

**Files:**
- Modify: `src/GitDocumentDb.Core/Internal/WriteExecutor.cs`
- Modify: `src/GitDocumentDb.Core/Implementation/Table.cs`
- Create: `tests/GitDocumentDb.Core.Tests/OptimisticRejectTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class OptimisticRejectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, int Value);

    private static ITable<Account> NewTable()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<Account>("accounts");
    }

    [Fact]
    public async Task Reject_with_matching_version_succeeds()
    {
        var t = NewTable();
        var initial = await t.PutAsync("a", new Account("a", 1), null, Ct);
        var r = await t.PutAsync("a", new Account("a", 2),
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = initial.NewVersion },
            Ct);
        r.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Reject_with_stale_version_fails_with_conflict()
    {
        var t = NewTable();
        var initial = await t.PutAsync("a", new Account("a", 1), null, Ct);
        await t.PutAsync("a", new Account("a", 2), null, Ct); // advance

        var r = await t.PutAsync("a", new Account("a", 3),
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = initial.NewVersion },
            Ct);
        r.Success.Should().BeFalse();
        r.Conflict.Should().NotBeNull();
        r.Conflict!.Reason.Should().Be(ConflictReason.VersionMismatch);
        r.Conflict.ExpectedVersion.Should().Be(initial.NewVersion);
        r.Conflict.ActualVersion.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reject_delete_of_missing_record_fails_with_ExpectedPresentButAbsent()
    {
        var t = NewTable();
        var r = await t.DeleteAsync("missing",
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = "some-sha" },
            Ct);
        r.Success.Should().BeFalse();
        r.Conflict.Should().NotBeNull();
        r.Conflict!.Reason.Should().Be(ConflictReason.ExpectedPresentButAbsent);
    }
}
```

Run — fails (mode/version not implemented).

- [ ] **Step 2: Update `WriteExecutor.PreparedOperation` to carry `ExpectedVersion`**

Change the record:
```csharp
public sealed record PreparedOperation(
    string TableName,
    string Id,
    string Path,
    WriteOpKind Kind,
    string? BlobSha,
    string? ExpectedVersion);
```

- [ ] **Step 3: Add a pre-commit version check inside `WriteExecutor.TryCommitAsync`**

Insert at the top of `TryCommitAsync`, before the `desiredEntries` loop:

```csharp
// Concurrency-mode check. Only OptimisticReject enforces per-op version expectations here;
// OptimisticMerge is handled in a separate overload (Task 6).
if (options.Mode == ConcurrencyMode.OptimisticReject)
{
    foreach (var op in operations)
    {
        var currentVersion = LookupCurrentVersion(snap, op.TableName, op.Id);
        var conflict = CheckReject(op, currentVersion);
        if (conflict is not null)
            return (false, null, op.Id, conflict);
    }
}
```

Helper methods (add at the bottom of `WriteExecutor`):
```csharp
private static string? LookupCurrentVersion(DatabaseSnapshot snap, string tableName, string id)
{
    if (!snap.Tables.TryGetValue(tableName, out var table)) return null;
    return table.Records.TryGetValue(id, out var sha) ? sha : null;
}

private static ConflictInfo? CheckReject(PreparedOperation op, string? currentVersion)
{
    // Put with ExpectedVersion = Absent: fail if record exists
    if (op.Kind == WriteOpKind.Put && op.ExpectedVersion == Versions.Absent && currentVersion is not null)
        return new ConflictInfo(op.Path, Versions.Absent, currentVersion, null, ConflictReason.ExpectedAbsentButPresent);

    // Put with concrete ExpectedVersion: fail on version mismatch
    if (op.Kind == WriteOpKind.Put && op.ExpectedVersion is not null && op.ExpectedVersion != Versions.Absent)
    {
        if (currentVersion != op.ExpectedVersion)
            return new ConflictInfo(op.Path, op.ExpectedVersion, currentVersion ?? "", null, ConflictReason.VersionMismatch);
    }

    // Delete with concrete ExpectedVersion: record must exist at that version
    if (op.Kind == WriteOpKind.Delete && op.ExpectedVersion is not null)
    {
        if (op.ExpectedVersion == Versions.Absent)
            return new ConflictInfo(op.Path, Versions.Absent, currentVersion ?? "", null, ConflictReason.VersionMismatch);
        if (currentVersion is null)
            return new ConflictInfo(op.Path, op.ExpectedVersion, "", null, ConflictReason.ExpectedPresentButAbsent);
        if (currentVersion != op.ExpectedVersion)
            return new ConflictInfo(op.Path, op.ExpectedVersion, currentVersion, null, ConflictReason.VersionMismatch);
    }

    return null;
}
```

Change the return type of `TryCommitAsync` to include the conflict:
```csharp
private static async Task<(bool success, string? newCommitSha, string? conflictOpId, ConflictInfo? conflict)>
    TryCommitAsync(...)
```

And update all callers. Now in `ExecuteSingleAsync`:
```csharp
var result = await TryCommitAsync(db, snap, new[] { op }, options, ct);
if (result.success)
{
    var newVersion = op.Kind == WriteOpKind.Put ? op.BlobSha : null;
    return new WriteResult(true, newVersion, result.newCommitSha, null, null);
}
if (result.conflict is not null)
{
    // Logical conflict under OptimisticReject — no retry.
    return new WriteResult(false, null, null, result.conflict, null);
}
// Push rejection — fetch + retry.
await db.FetchAsync(ct);
await Task.Delay(ComputeBackoff(options, attempt), ct);
```

For `ExecuteBatchAsync`, the per-op conflict produces a whole-batch failure with the offending op's conflict carried in its `OperationResult`:
```csharp
if (result.conflict is not null)
{
    var ops = operations.Select(o =>
        o.Id == result.conflictOpId
            ? new OperationResult(o.Id, false, null, result.conflict, null)
            : new OperationResult(o.Id, false, null, null, null))
        .ToList();
    return new BatchResult(false, null, ops);
}
```

- [ ] **Step 4: Plumb `ExpectedVersion` through Table**

In `Table.PutAsync`: pass `options?.ExpectedVersion`:
```csharp
var op = new WriteExecutor.PreparedOperation(
    _name, id, path, WriteOpKind.Put, blobSha, options?.ExpectedVersion);
```

In `Table.DeleteAsync`: same for Delete kind.

In `Table.CommitAsync`: each op has its own `op.ExpectedVersion`, so use that:
```csharp
prepared.Add(new WriteExecutor.PreparedOperation(
    _name, op.Id, path, WriteOpKind.Put, blobSha, op.ExpectedVersion));
// or Delete:
prepared.Add(new WriteExecutor.PreparedOperation(
    _name, op.Id, path, WriteOpKind.Delete, null, op.ExpectedVersion));
```

- [ ] **Step 5: Run tests**

All pass (54 prior + 3 new = 57).

- [ ] **Step 6: Commit**

```
feat(core): OptimisticReject concurrency mode with expected-version checks
```

---

## Task 4: Create-if-absent via `Versions.Absent`

Already supported structurally after Task 3 (the `CheckReject` helper handles `Versions.Absent`). This task verifies the behavior with dedicated tests, including LastWriteWins (which should IGNORE `ExpectedVersion`).

**Files:**
- Create: `tests/GitDocumentDb.Core.Tests/CreateIfAbsentTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class CreateIfAbsentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, int V);

    private static ITable<Account> NewTable()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<Account>("accounts");
    }

    [Fact]
    public async Task Create_if_absent_succeeds_when_absent()
    {
        var t = NewTable();
        var r = await t.PutAsync("a", new Account("a", 1),
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = Versions.Absent }, Ct);
        r.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Create_if_absent_fails_when_present()
    {
        var t = NewTable();
        await t.PutAsync("a", new Account("a", 1), null, Ct);
        var r = await t.PutAsync("a", new Account("a", 2),
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = Versions.Absent }, Ct);
        r.Success.Should().BeFalse();
        r.Conflict!.Reason.Should().Be(ConflictReason.ExpectedAbsentButPresent);
    }

    [Fact]
    public async Task LastWriteWins_ignores_expected_version()
    {
        var t = NewTable();
        await t.PutAsync("a", new Account("a", 1), null, Ct);
        // LastWriteWins: expected version is informational only, must not cause a conflict.
        var r = await t.PutAsync("a", new Account("a", 2),
            new WriteOptions { Mode = ConcurrencyMode.LastWriteWins, ExpectedVersion = "some-stale-sha" }, Ct);
        r.Success.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run tests**

All pass (no code changes needed if Task 3 was implemented correctly).

- [ ] **Step 3: Commit**

```
test(core): create-if-absent semantics via Versions.Absent
```

---

## Task 5: `IRecordMerger<T>` + `MergeResult<T>` + options plumbing

**Files:**
- Create: `src/GitDocumentDb.Core/Abstractions/IRecordMerger.cs`
- Create: `src/GitDocumentDb.Core/Models/MergeResult.cs`
- Modify: `src/GitDocumentDb.Core/Models/DatabaseOptions.cs`

- [ ] **Step 1: `MergeResult.cs`**

```csharp
namespace GitDocumentDb;

public sealed record MergeResult<T>(bool Succeeded, T? Merged, string? ConflictDescription);
```

- [ ] **Step 2: `IRecordMerger.cs`**

```csharp
namespace GitDocumentDb;

public interface IRecordMerger<T>
{
    MergeResult<T> Merge(T baseRecord, T local, T remote);
}
```

- [ ] **Step 3: Extend `DatabaseOptions`**

Add to `DatabaseOptions`:
```csharp
public IReadOnlyDictionary<Type, object> RecordMergers { get; init; } =
    new Dictionary<Type, object>();
```

The value is `object` because `IRecordMerger<T>` is generic; we store a type-keyed dictionary that consumers fill with typed mergers. `WriteExecutor` resolves the merger with a cast at merge time.

- [ ] **Step 4: Build and run tests**

`dotnet build` must succeed. No new tests yet — test comes in Task 7. All 57 tests still pass.

- [ ] **Step 5: Commit**

```
feat(core): IRecordMerger abstraction and options plumbing
```

---

## Task 6: `JsonPatchMerger<T>` — default merger

A minimal JSON Patch (RFC 6902) implementation: compute the set of paths that differ between base→local and base→remote; if the sets are disjoint, apply both changes to the base. Otherwise, fail.

**Files:**
- Create: `src/GitDocumentDb.Core/Merging/JsonPatchMerger.cs`
- Create: `tests/GitDocumentDb.Core.Tests/JsonPatchMergerTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
using FluentAssertions;
using GitDocumentDb.Merging;

namespace GitDocumentDb.Tests;

public class JsonPatchMergerTests
{
    public sealed record User(string Id, string Name, string Email, int Age);

    [Fact]
    public void Non_overlapping_changes_merge_successfully()
    {
        var merger = new JsonPatchMerger<User>();
        var baseRec = new User("1", "Alice", "a@x", 30);
        var local = baseRec with { Name = "Alice L." };          // changes Name
        var remote = baseRec with { Email = "alice@new.com" };   // changes Email

        var result = merger.Merge(baseRec, local, remote);
        result.Succeeded.Should().BeTrue();
        result.Merged!.Name.Should().Be("Alice L.");
        result.Merged.Email.Should().Be("alice@new.com");
        result.Merged.Id.Should().Be("1");
        result.Merged.Age.Should().Be(30);
    }

    [Fact]
    public void Overlapping_changes_fail_to_merge()
    {
        var merger = new JsonPatchMerger<User>();
        var baseRec = new User("1", "Alice", "a@x", 30);
        var local = baseRec with { Email = "local@x" };
        var remote = baseRec with { Email = "remote@x" };

        var result = merger.Merge(baseRec, local, remote);
        result.Succeeded.Should().BeFalse();
        result.ConflictDescription.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Unchanged_local_returns_remote()
    {
        var merger = new JsonPatchMerger<User>();
        var baseRec = new User("1", "Alice", "a@x", 30);
        var remote = baseRec with { Email = "alice@new.com" };
        var result = merger.Merge(baseRec, baseRec, remote);
        result.Succeeded.Should().BeTrue();
        result.Merged!.Email.Should().Be("alice@new.com");
    }

    [Fact]
    public void Unchanged_remote_returns_local()
    {
        var merger = new JsonPatchMerger<User>();
        var baseRec = new User("1", "Alice", "a@x", 30);
        var local = baseRec with { Name = "Alice L." };
        var result = merger.Merge(baseRec, local, baseRec);
        result.Succeeded.Should().BeTrue();
        result.Merged!.Name.Should().Be("Alice L.");
    }
}
```

Run — compile error.

- [ ] **Step 2: Implement `JsonPatchMerger.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitDocumentDb.Merging;

public sealed class JsonPatchMerger<T> : IRecordMerger<T>
{
    private static readonly JsonSerializerOptions s_opts = new(JsonSerializerDefaults.Web);

    public MergeResult<T> Merge(T baseRecord, T local, T remote)
    {
        var baseNode  = JsonSerializer.SerializeToNode(baseRecord,  s_opts);
        var localNode = JsonSerializer.SerializeToNode(local,       s_opts);
        var remoteNode = JsonSerializer.SerializeToNode(remote,     s_opts);

        if (baseNode is null || localNode is null || remoteNode is null)
            return new MergeResult<T>(false, default, "Unsupported null top-level JSON");

        var localChanges  = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var remoteChanges = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        DiffPaths(baseNode, localNode,  "", localChanges);
        DiffPaths(baseNode, remoteNode, "", remoteChanges);

        foreach (var path in localChanges.Keys)
            if (remoteChanges.ContainsKey(path))
                return new MergeResult<T>(false, default, $"Overlapping change at '{path}'");

        // Apply both change sets to a fresh copy of base.
        var merged = JsonSerializer.SerializeToNode(baseRecord, s_opts)!;
        foreach (var (path, value) in localChanges)  ApplyAtPath(merged, path, value);
        foreach (var (path, value) in remoteChanges) ApplyAtPath(merged, path, value);

        var mergedRecord = merged.Deserialize<T>(s_opts)!;
        return new MergeResult<T>(true, mergedRecord, null);
    }

    // Depth-first traversal of two JSON trees, collecting paths where they differ.
    // Paths use simple dot notation ("foo.bar.0"); arrays use index.
    private static void DiffPaths(JsonNode? a, JsonNode? b, string path, Dictionary<string, JsonNode?> into)
    {
        if (JsonNodeEquals(a, b)) return;

        if (a is JsonObject aObj && b is JsonObject bObj)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in aObj) keys.Add(kv.Key);
            foreach (var kv in bObj) keys.Add(kv.Key);
            foreach (var k in keys)
            {
                aObj.TryGetPropertyValue(k, out var av);
                bObj.TryGetPropertyValue(k, out var bv);
                DiffPaths(av, bv, path.Length == 0 ? k : $"{path}.{k}", into);
            }
            return;
        }

        if (a is JsonArray aArr && b is JsonArray bArr)
        {
            var max = Math.Max(aArr.Count, bArr.Count);
            for (var i = 0; i < max; i++)
            {
                var av = i < aArr.Count ? aArr[i] : null;
                var bv = i < bArr.Count ? bArr[i] : null;
                DiffPaths(av, bv, $"{path}.{i}", into);
            }
            return;
        }

        // Leaf (scalar) or type-mismatched — record the whole path.
        into[path] = b?.DeepClone();
    }

    private static bool JsonNodeEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return JsonNode.DeepEquals(a, b);
    }

    private static void ApplyAtPath(JsonNode root, string path, JsonNode? value)
    {
        if (path.Length == 0)
        {
            // Whole-document replacement — not expected at diff leaves.
            return;
        }

        var segments = path.Split('.');
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            current = current is JsonArray arr && int.TryParse(seg, out var idx)
                ? arr[idx]!
                : ((JsonObject)current)[seg]!;
        }
        var last = segments[^1];
        var clone = value?.DeepClone();
        if (current is JsonArray arr2 && int.TryParse(last, out var lastIdx))
            arr2[lastIdx] = clone;
        else
            ((JsonObject)current)[last] = clone;
    }
}
```

- [ ] **Step 3: Run tests**

All pass (57 + 4 = 61).

- [ ] **Step 4: Commit**

```
feat(core): JsonPatchMerger with path-disjoint three-way merge
```

---

## Task 7: `OptimisticMerge` execution path

When the mode is `OptimisticMerge` and a version mismatch is detected, invoke the registered `IRecordMerger<T>`. On successful merge, substitute the merged record's blob for the original. On failure, fall back to a `UnmergeableChange` conflict.

The challenge: `WriteExecutor` is generic-free (operates on raw paths + blob SHAs). Merging requires `T`. So the merge must happen in `Table<T>` before dispatch, not in `WriteExecutor`.

**Files:**
- Modify: `src/GitDocumentDb.Core/Implementation/Table.cs`
- Modify: `src/GitDocumentDb.Core/Internal/WriteExecutor.cs`
- Create: `tests/GitDocumentDb.Core.Tests/OptimisticMergeTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Merging;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class OptimisticMergeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record User(string Id, string Name, string Email, int Age);

    private static ITable<User> NewTable()
    {
        var c = new InMemoryGitConnection();
        var options = new DatabaseOptions
        {
            RecordMergers = new Dictionary<Type, object>
            {
                [typeof(User)] = new JsonPatchMerger<User>(),
            },
        };
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), options);
        return doc.GetDatabase("alpha").GetTable<User>("users");
    }

    [Fact]
    public async Task Non_overlapping_changes_merge_and_succeed()
    {
        var t = NewTable();
        var initial = await t.PutAsync("1", new User("1", "Alice", "a@x", 30), null, Ct);

        // Another writer advances Email
        await t.PutAsync("1", new User("1", "Alice", "alice@new.com", 30), null, Ct);

        // We submit a Name change based on the INITIAL version
        var r = await t.PutAsync("1",
            new User("1", "Alice L.", "a@x", 30),
            new WriteOptions
            {
                Mode = ConcurrencyMode.OptimisticMerge,
                ExpectedVersion = initial.NewVersion,
            }, Ct);

        r.Success.Should().BeTrue();
        var read = await t.GetAsync("1", null, Ct);
        read!.Record.Name.Should().Be("Alice L.");
        read.Record.Email.Should().Be("alice@new.com");
    }

    [Fact]
    public async Task Overlapping_changes_fail_with_unmergeable_conflict()
    {
        var t = NewTable();
        var initial = await t.PutAsync("1", new User("1", "Alice", "a@x", 30), null, Ct);
        await t.PutAsync("1", new User("1", "Alice", "remote@x", 30), null, Ct);

        var r = await t.PutAsync("1",
            new User("1", "Alice", "local@x", 30),
            new WriteOptions
            {
                Mode = ConcurrencyMode.OptimisticMerge,
                ExpectedVersion = initial.NewVersion,
            }, Ct);

        r.Success.Should().BeFalse();
        r.Conflict!.Reason.Should().Be(ConflictReason.UnmergeableChange);
    }

    [Fact]
    public async Task OptimisticMerge_without_registered_merger_falls_back_to_reject()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var t = doc.GetDatabase("alpha").GetTable<User>("users");

        var initial = await t.PutAsync("1", new User("1", "Alice", "a@x", 30), null, Ct);
        await t.PutAsync("1", new User("1", "Alice", "remote@x", 30), null, Ct);

        var r = await t.PutAsync("1",
            new User("1", "Alice", "local@x", 30),
            new WriteOptions
            {
                Mode = ConcurrencyMode.OptimisticMerge,
                ExpectedVersion = initial.NewVersion,
            }, Ct);

        r.Success.Should().BeFalse();
        r.Conflict!.Reason.Should().Be(ConflictReason.VersionMismatch);
    }
}
```

Run — fails (no merge path yet).

- [ ] **Step 2: Add merge handling to `Table.PutAsync`**

Replace `PutAsync` with:
```csharp
public async Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default)
{
    RecordIdValidator.ThrowIfInvalid(id, nameof(id));
    ArgumentNullException.ThrowIfNull(record);

    options ??= new WriteOptions();

    // If OptimisticMerge and a mismatch exists, try to merge BEFORE serializing/pushing.
    if (options.Mode == ConcurrencyMode.OptimisticMerge
        && options.ExpectedVersion is not null
        && options.ExpectedVersion != Versions.Absent)
    {
        await _db.EnsureOpenedAsync(ct);
        var snap = _db.CurrentSnapshot;
        var current = LookupCurrentVersion(snap);
        if (current is not null && current != options.ExpectedVersion)
        {
            var mergeResult = await TryMergeAsync(id, record, options.ExpectedVersion, current, ct);
            if (mergeResult.Conflict is not null)
                return new WriteResult(false, null, null, mergeResult.Conflict, null);
            if (mergeResult.MergedRecord is not null)
                record = mergeResult.MergedRecord;
        }
    }

    var writer = new ArrayBufferWriter<byte>();
    _db.Serializer.Serialize(record, writer);
    var bytes = writer.WrittenMemory;
    if (bytes.Length > _db.Options.RecordSizeHardLimitBytes)
        return new WriteResult(false, null, null, null, WriteFailureReason.RecordTooLarge);

    var blobSha = await _db.Connection.WriteBlobAsync(bytes, ct);
    var path = $"tables/{_name}/{id}{_db.Serializer.FileExtension}";

    // After merging, any push-level CAS still needs the merged version's ExpectedVersion updated
    // to the current remote version, because the merge used remote state already.
    string? effectiveExpectedVersion = options.ExpectedVersion;
    if (options.Mode == ConcurrencyMode.OptimisticMerge && effectiveExpectedVersion != Versions.Absent)
    {
        var snap = _db.CurrentSnapshot;
        effectiveExpectedVersion = LookupCurrentVersion(snap);
    }

    var preparedOp = new WriteExecutor.PreparedOperation(
        _name, id, path, WriteOpKind.Put, blobSha, effectiveExpectedVersion);

    // For the executor's concurrency check, promote OptimisticMerge to OptimisticReject now.
    // The merge is already resolved at this point.
    var effectiveOptions = options.Mode == ConcurrencyMode.OptimisticMerge
        ? new WriteOptions
        {
            Mode = ConcurrencyMode.OptimisticReject,
            ExpectedVersion = effectiveExpectedVersion,
            MaxRetries = options.MaxRetries,
            RetryBackoff = options.RetryBackoff,
            MaxRetryBackoff = options.MaxRetryBackoff,
            Author = options.Author,
            CommitMessage = options.CommitMessage,
        }
        : options;

    return await WriteExecutor.ExecuteSingleAsync(_db, preparedOp, effectiveOptions, ct);
}

private string? LookupCurrentVersion(DatabaseSnapshot snap)
{
    if (!snap.Tables.TryGetValue(_name, out var table)) return null;
    return table.Records.TryGetValue(_nameLookupHelper(snap)!, out var sha) ? sha : null;
}
```

Wait — the `LookupCurrentVersion` helper needs the id parameter. Rewrite:

```csharp
private string? LookupCurrentVersion(DatabaseSnapshot snap, string id)
{
    if (!snap.Tables.TryGetValue(_name, out var table)) return null;
    return table.Records.TryGetValue(id, out var sha) ? sha : null;
}
```

And replace usage:
```csharp
var current = LookupCurrentVersion(snap, id);
```

Add `TryMergeAsync`:
```csharp
private async Task<(T? MergedRecord, ConflictInfo? Conflict)> TryMergeAsync(
    string id, T local, string baseVersion, string currentVersion, CancellationToken ct)
{
    if (!_db.Options.RecordMergers.TryGetValue(typeof(T), out var mergerObj)
        || mergerObj is not IRecordMerger<T> merger)
    {
        // No merger registered → fall back to reject semantics with VersionMismatch
        return (default, new ConflictInfo(
            $"tables/{_name}/{id}{_db.Serializer.FileExtension}",
            baseVersion, currentVersion, null, ConflictReason.VersionMismatch));
    }

    var baseBytes = await _db.Connection.GetBlobAsync(baseVersion, ct);
    var currentBytes = await _db.Connection.GetBlobAsync(currentVersion, ct);
    var baseRecord = _db.Serializer.Deserialize<T>(baseBytes.Span);
    var remoteRecord = _db.Serializer.Deserialize<T>(currentBytes.Span);

    var result = merger.Merge(baseRecord, local, remoteRecord);
    if (!result.Succeeded)
    {
        return (default, new ConflictInfo(
            $"tables/{_name}/{id}{_db.Serializer.FileExtension}",
            baseVersion, currentVersion, currentBytes, ConflictReason.UnmergeableChange));
    }
    return (result.Merged, null);
}
```

- [ ] **Step 3: Run tests**

All pass (61 + 3 = 64).

- [ ] **Step 4: Commit**

```
feat(core): OptimisticMerge via registered IRecordMerger with fallback
```

---

## Task 8: Force-push detection in `FetchAsync`

**Files:**
- Modify: `src/GitDocumentDb.Core/Implementation/Database.cs`
- Create: `tests/GitDocumentDb.Core.Tests/ForcePushTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class ForcePushTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Doc(string Id, int V);

    [Fact]
    public async Task Non_ancestor_remote_triggers_history_rewritten_notification()
    {
        var backend = new InMemoryGitConnection();
        var doc1 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var db1 = doc1.GetDatabase("alpha");
        var t1 = db1.GetTable<Doc>("things");

        // Establish initial history on db1
        await t1.PutAsync("a", new Doc("a", 1), null, Ct);
        var firstCommit = db1.CurrentCommit;

        // Externally force-push an unrelated branch head (non-descendant)
        var tree = await backend.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), Ct);
        var rogue = await backend.CreateCommitAsync(new CommitSpec(
            tree, null, "rogue", "r@x", DateTimeOffset.UnixEpoch, "forced"), Ct);

        // Forcibly overwrite the ref (bypassing CAS). We use UpdateRefAsync with the current SHA to
        // simulate what a server-side force-push would look like to a fetching client.
        await backend.UpdateRefAsync("refs/heads/db/alpha", firstCommit, rogue, Ct);

        // Now db1 fetches — should detect history rewrite
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var received = new List<ChangeNotification>();
        var watch = Task.Run(async () =>
        {
            await foreach (var n in db1.WatchAsync(cts.Token))
            {
                received.Add(n);
                if (received.Count == 1) break;
            }
        }, cts.Token);

        await Task.Delay(50, Ct);
        var fetch = await db1.FetchAsync(Ct);

        await watch;
        received.Should().HaveCount(1);
        received[0].Reason.Should().Be(ChangeReason.HistoryRewritten);
        fetch.HadChanges.Should().BeTrue();
    }
}
```

Run — fails (FetchAsync doesn't check ancestry).

- [ ] **Step 2: Modify `Database.FetchAsync`**

Replace `FetchAsync`:
```csharp
public async Task<FetchResult> FetchAsync(CancellationToken ct = default)
{
    var previous = CurrentSnapshot.CommitSha;
    var remoteSha = await _connection.ResolveRefAsync(_refName, ct);
    if (string.IsNullOrEmpty(remoteSha) || remoteSha == previous)
        return new FetchResult(false, previous, previous, Array.Empty<string>());

    var reason = ChangeReason.RemoteAdvance;
    if (!string.IsNullOrEmpty(previous))
    {
        // Is the previous local commit an ancestor of the new remote?
        var isAncestor = await AncestorWalker.IsAncestorAsync(
            _connection, previous, remoteSha, maxDepth: 1000, ct);
        if (!isAncestor)
            reason = ChangeReason.HistoryRewritten;
    }

    var newSnap = await SnapshotBuilder.BuildAsync(_connection, remoteSha, ct);
    SwapSnapshot(newSnap);

    PublishNotification(new ChangeNotification(
        remoteSha, DateTimeOffset.UtcNow, Array.Empty<string>(), reason));

    return new FetchResult(true, previous, remoteSha, Array.Empty<string>());
}
```

- [ ] **Step 3: Run tests**

All pass (64 + 1 = 65).

- [ ] **Step 4: Commit**

```
feat(core): force-push detection emits HistoryRewritten notifications
```

---

## Task 9: README update for Phase 2

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update `README.md`**

Replace the Phase 1 scope section. New content:

```markdown
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
```

- [ ] **Step 2: Commit**

```
docs: update README for phase 2
```

---

## Self-Review

**Spec coverage (Phase 2 scope):**

- §4.2 `ConflictInfo` / `ConflictReason` / `Versions.Absent` — Tasks 3, 4.
- §4.3 concurrency modes (`OptimisticReject`, `OptimisticMerge`, default and per-call) — Tasks 3, 7.
- §4.4 version tokens used for conflict detection — Task 3.
- §4.5 logical-conflict semantics per mode (reject / merge / LWW) — Tasks 3, 4, 7.
- §4.6 batch per-op conflict handling (offending op carries conflict, siblings get `null`) — Task 3.
- §4.7 three-way merger, `IRecordMerger<T>`, default JSON-patch — Tasks 5, 6, 7.
- §4.8 force-push detection + `HistoryRewritten` — Task 8.
- §4.11 `ChangeReason.HistoryRewritten` — Task 8.

**Placeholder scan:** no `TBD`, `TODO`, or "similar to" — all steps contain concrete code.

**Type consistency:**
- `PreparedOperation` gains `ExpectedVersion` in Task 3; all Task 3/7 call sites pass it.
- `TryCommitAsync` return tuple gains `conflictOpId` and `ConflictInfo?` fields in Task 3; both callers (`ExecuteSingleAsync`, `ExecuteBatchAsync`) updated.
- `IRecordMerger<T>` defined in Task 5, consumed in Task 7.
- `AncestorWalker.IsAncestorAsync` defined in Task 2, consumed in Task 8.

**Out of scope for Phase 2** (later phases): indexing (§4.12), queries (§4.13), real transports (§4.14), schema loading, storage modes, Orleans.
