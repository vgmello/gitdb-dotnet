# GitDocumentDb Phase 3: Schema + Indexes + Queries — Implementation Plan

**Goal:** Load a per-database `.schema.json`, build equality/range/unique indexes from record content, expose a typed query API, and enforce unique constraints on write.

**Architecture:** Schema is loaded during database open. Indexes become part of the immutable `TableSnapshot`; rebuilds happen on snapshot swap (fetch or write success). Unique index violations are detected during write by `WriteExecutor` before push. Queries compile an `Expression<Func<T,bool>>` predicate into a `QueryPlan` that selects indexed candidates then applies in-memory post-filtering.

**Scope explicitly deferred:** Index snapshot persistence to disk (Phase 4, tied to real transport); disjunctions/complex expression trees (v2 of query engine); aggregates.

---

## File Structure

```
src/GitDocumentDb.Core/
├── Schema/
│   ├── SchemaFile.cs               # JSON DTO
│   ├── DatabaseSchema.cs           # Loaded schema
│   ├── TableSchema.cs
│   ├── IndexDefinition.cs
│   └── SchemaLoader.cs             # Loads .schema.json from a commit
├── Indexing/
│   ├── IIndex.cs                   # Marker interface
│   ├── EqualityIndex.cs            # Generic equality index
│   ├── RangeIndex.cs               # Sorted index for range queries
│   ├── UniqueEqualityIndex.cs      # Equality with unique constraint
│   ├── IndexBuilder.cs             # Builds table indexes from records
│   └── RecordFieldAccessor.cs      # Reflection-based value reader
├── Querying/
│   ├── Query.cs                    # Opaque query object
│   ├── QueryBuilder.cs             # Fluent builder
│   ├── QueryPlan.cs                # Compiled plan
│   ├── QueryCompiler.cs            # Expression -> plan
│   └── QueryException.cs
├── Internal/
│   ├── DatabaseSnapshot.cs         # MODIFIED (schema + indexes)
│   ├── TableSnapshot.cs            # MODIFIED (indexes)
│   ├── SnapshotBuilder.cs          # MODIFIED (builds indexes)
│   └── WriteExecutor.cs            # MODIFIED (unique violation check)
├── Implementation/
│   ├── Database.cs                 # MODIFIED (exposes schema)
│   └── Table.cs                    # MODIFIED (QueryAsync)
└── Abstractions/
    └── ITable.cs                   # MODIFIED (QueryAsync signature)
```

---

## Task 1: Schema model + loader

**Step 1:** Create `src/GitDocumentDb.Core/Schema/IndexDefinition.cs`:
```csharp
namespace GitDocumentDb.Schema;

public enum IndexType { Equality, Range }

public sealed record IndexDefinition(string Field, IndexType Type, bool Unique);
```

**Step 2:** `Schema/TableSchema.cs`:
```csharp
namespace GitDocumentDb.Schema;

public sealed record TableSchema(string Name, IReadOnlyList<IndexDefinition> Indexes);
```

**Step 3:** `Schema/DatabaseSchema.cs`:
```csharp
using System.Collections.Frozen;

namespace GitDocumentDb.Schema;

public sealed record DatabaseSchema(int Version, FrozenDictionary<string, TableSchema> Tables)
{
    public static DatabaseSchema Empty { get; } = new(1, FrozenDictionary<string, TableSchema>.Empty);
}
```

**Step 4:** JSON DTO `Schema/SchemaFile.cs` (internal):
```csharp
using System.Text.Json.Serialization;

namespace GitDocumentDb.Schema;

internal sealed class SchemaFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("tables")] public Dictionary<string, SchemaFileTable> Tables { get; set; } = new();
}

internal sealed class SchemaFileTable
{
    [JsonPropertyName("indexes")] public List<SchemaFileIndex> Indexes { get; set; } = new();
}

internal sealed class SchemaFileIndex
{
    [JsonPropertyName("field")] public string Field { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "equality";
    [JsonPropertyName("unique")] public bool Unique { get; set; }
}
```

**Step 5:** Loader `Schema/SchemaLoader.cs`:
```csharp
using System.Collections.Frozen;
using System.Text.Json;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Schema;

internal static class SchemaLoader
{
    public static async Task<DatabaseSchema> LoadAsync(IGitConnection connection, string commitSha, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(commitSha)) return DatabaseSchema.Empty;

        var tree = await connection.GetTreeAsync(commitSha, ct);
        if (!tree.TryGetBlob(".schema.json", out var blobSha))
            return DatabaseSchema.Empty;

        var bytes = await connection.GetBlobAsync(blobSha, ct);
        var file = JsonSerializer.Deserialize<SchemaFile>(bytes.Span,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var tables = new Dictionary<string, TableSchema>(StringComparer.Ordinal);
        foreach (var (name, table) in file.Tables)
        {
            var defs = new List<IndexDefinition>();
            foreach (var idx in table.Indexes)
            {
                var type = idx.Type.Equals("range", StringComparison.OrdinalIgnoreCase)
                    ? IndexType.Range : IndexType.Equality;
                defs.Add(new IndexDefinition(idx.Field, type, idx.Unique));
            }
            tables[name] = new TableSchema(name, defs);
        }
        return new DatabaseSchema(file.SchemaVersion, tables.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
```

**Step 6:** Test `tests/GitDocumentDb.Core.Tests/SchemaLoaderTests.cs`:
```csharp
using System.Text;
using FluentAssertions;
using GitDocumentDb.Schema;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class SchemaLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_schema_returns_empty()
    {
        var c = new InMemoryGitConnection();
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        var schema = await SchemaLoader.LoadAsync(c, commit, Ct);
        schema.Tables.Should().BeEmpty();
    }

    [Fact]
    public async Task Loads_equality_and_range_indexes_with_unique_flag()
    {
        var json = """
        {
          "schemaVersion": 1,
          "tables": {
            "accounts": {
              "indexes": [
                { "field": "email", "type": "equality", "unique": true },
                { "field": "createdAt", "type": "range" }
              ]
            }
          }
        }
        """;
        var c = new InMemoryGitConnection();
        var blob = await c.WriteBlobAsync(Encoding.UTF8.GetBytes(json), Ct);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, ".schema.json", blob),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);

        var schema = await SchemaLoader.LoadAsync(c, commit, Ct);
        schema.Tables.Should().ContainKey("accounts");
        var defs = schema.Tables["accounts"].Indexes;
        defs.Should().HaveCount(2);
        defs.Should().Contain(d => d.Field == "email" && d.Type == IndexType.Equality && d.Unique);
        defs.Should().Contain(d => d.Field == "createdAt" && d.Type == IndexType.Range && !d.Unique);
    }
}
```

**Step 7:** Build + test. Commit: `feat(core): schema model and loader`.

---

## Task 2: Index data structures + builder

Approach: indexes store raw `object?` keys (from JSON field values) pointing to sets of record IDs. Equality uses a `Dictionary<object, List<string>>`; range uses a `SortedList<object, List<string>>` with a custom comparer. Keep types simple — we work with `object` because fields are dynamic per record type.

**Step 1:** `src/GitDocumentDb.Core/Indexing/IIndex.cs`:
```csharp
namespace GitDocumentDb.Indexing;

internal interface IIndex
{
    string Field { get; }
}
```

**Step 2:** `Indexing/EqualityIndex.cs`:
```csharp
namespace GitDocumentDb.Indexing;

internal sealed class EqualityIndex : IIndex
{
    public string Field { get; }
    public Dictionary<object, List<string>> ByValue { get; }

    public EqualityIndex(string field, Dictionary<object, List<string>> byValue)
    {
        Field = field;
        ByValue = byValue;
    }
}
```

**Step 3:** `Indexing/UniqueEqualityIndex.cs`:
```csharp
namespace GitDocumentDb.Indexing;

internal sealed class UniqueEqualityIndex : IIndex
{
    public string Field { get; }
    public Dictionary<object, string> ByValue { get; }

    public UniqueEqualityIndex(string field, Dictionary<object, string> byValue)
    {
        Field = field;
        ByValue = byValue;
    }
}
```

**Step 4:** `Indexing/RangeIndex.cs`:
```csharp
namespace GitDocumentDb.Indexing;

internal sealed class RangeIndex : IIndex
{
    public string Field { get; }
    public SortedList<object, List<string>> Sorted { get; }

    public RangeIndex(string field, SortedList<object, List<string>> sorted)
    {
        Field = field;
        Sorted = sorted;
    }
}
```

**Step 5:** `Indexing/RecordFieldAccessor.cs` — uses `JsonNode` to extract values (so we work off serialized content, not reflection against T):
```csharp
using System.Text.Json.Nodes;

namespace GitDocumentDb.Indexing;

internal static class RecordFieldAccessor
{
    public static object? Read(JsonNode? root, string fieldPath)
    {
        if (root is null) return null;
        JsonNode? current = root;
        foreach (var segment in fieldPath.Split('.'))
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
                current = next;
            else return null;
        }
        return current switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<long>(out var l) => l,
            JsonValue v when v.TryGetValue<double>(out var d) => d,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => current.ToJsonString(),
        };
    }
}
```

**Step 6:** `Indexing/IndexBuilder.cs`:
```csharp
using System.Collections.Frozen;
using System.Text.Json.Nodes;
using GitDocumentDb.Schema;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Indexing;

internal static class IndexBuilder
{
    public sealed record BuildResult(FrozenDictionary<string, IIndex> Indexes, string? UniqueViolation);

    public static async Task<BuildResult> BuildAsync(
        IGitConnection connection,
        TableSchema schema,
        IReadOnlyDictionary<string, string> records, // id -> blobSha
        CancellationToken ct)
    {
        if (schema.Indexes.Count == 0 || records.Count == 0)
            return new BuildResult(FrozenDictionary<string, IIndex>.Empty, null);

        // Load & parse all records as JsonNode once.
        var parsed = new Dictionary<string, JsonNode>(records.Count, StringComparer.Ordinal);
        foreach (var (id, sha) in records)
        {
            var bytes = await connection.GetBlobAsync(sha, ct);
            var node = JsonNode.Parse(bytes.Span);
            if (node is not null) parsed[id] = node;
        }

        var builders = new Dictionary<string, IIndex>(StringComparer.Ordinal);
        foreach (var def in schema.Indexes)
        {
            if (def.Type == IndexType.Equality && def.Unique)
            {
                var map = new Dictionary<object, string>();
                foreach (var (id, node) in parsed)
                {
                    var value = RecordFieldAccessor.Read(node, def.Field);
                    if (value is null) continue;
                    if (map.TryGetValue(value, out var existing))
                        return new BuildResult(FrozenDictionary<string, IIndex>.Empty,
                            $"unique index '{def.Field}' violation: '{value}' in records '{existing}' and '{id}'");
                    map[value] = id;
                }
                builders[def.Field] = new UniqueEqualityIndex(def.Field, map);
            }
            else if (def.Type == IndexType.Equality)
            {
                var map = new Dictionary<object, List<string>>();
                foreach (var (id, node) in parsed)
                {
                    var value = RecordFieldAccessor.Read(node, def.Field);
                    if (value is null) continue;
                    if (!map.TryGetValue(value, out var list)) map[value] = list = new();
                    list.Add(id);
                }
                builders[def.Field] = new EqualityIndex(def.Field, map);
            }
            else // Range
            {
                var sorted = new SortedList<object, List<string>>(Comparer<object>.Create(CompareValues));
                foreach (var (id, node) in parsed)
                {
                    var value = RecordFieldAccessor.Read(node, def.Field);
                    if (value is null) continue;
                    if (!sorted.TryGetValue(value, out var list)) sorted[value] = list = new();
                    list.Add(id);
                }
                builders[def.Field] = new RangeIndex(def.Field, sorted);
            }
        }
        return new BuildResult(builders.ToFrozenDictionary(StringComparer.Ordinal), null);
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is IComparable ac && a.GetType() == b.GetType()) return ac.CompareTo(b);
        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }
}
```

**Step 7:** Test at `tests/GitDocumentDb.Core.Tests/IndexBuilderTests.cs` — basic equality + unique + range behaviors.

```csharp
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GitDocumentDb.Indexing;
using GitDocumentDb.Schema;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class IndexBuilderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyDictionary<string, string>> Seed(IGitConnection c, Dictionary<string, object> records)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, rec) in records)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rec, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            map[id] = await c.WriteBlobAsync(bytes, Ct);
        }
        return map;
    }

    [Fact]
    public async Task Builds_equality_index()
    {
        var c = new InMemoryGitConnection();
        var records = await Seed(c, new() {
            ["a"] = new { id = "a", status = "active" },
            ["b"] = new { id = "b", status = "active" },
            ["c"] = new { id = "c", status = "inactive" },
        });
        var schema = new TableSchema("t", new[] { new IndexDefinition("status", IndexType.Equality, false) });
        var result = await IndexBuilder.BuildAsync(c, schema, records, Ct);
        result.UniqueViolation.Should().BeNull();
        var idx = result.Indexes["status"].Should().BeOfType<EqualityIndex>().Subject;
        idx.ByValue.Should().HaveCount(2);
    }

    [Fact]
    public async Task Builds_unique_equality_index_and_detects_violation()
    {
        var c = new InMemoryGitConnection();
        var records = await Seed(c, new() {
            ["a"] = new { id = "a", email = "x@y" },
            ["b"] = new { id = "b", email = "x@y" },
        });
        var schema = new TableSchema("t", new[] { new IndexDefinition("email", IndexType.Equality, true) });
        var result = await IndexBuilder.BuildAsync(c, schema, records, Ct);
        result.UniqueViolation.Should().NotBeNull();
    }

    [Fact]
    public async Task Builds_range_index()
    {
        var c = new InMemoryGitConnection();
        var records = await Seed(c, new() {
            ["a"] = new { id = "a", age = 30 },
            ["b"] = new { id = "b", age = 25 },
            ["c"] = new { id = "c", age = 40 },
        });
        var schema = new TableSchema("t", new[] { new IndexDefinition("age", IndexType.Range, false) });
        var result = await IndexBuilder.BuildAsync(c, schema, records, Ct);
        var idx = result.Indexes["age"].Should().BeOfType<RangeIndex>().Subject;
        idx.Sorted.Keys.Should().Equal(new object[] { (long)25, (long)30, (long)40 });
    }
}
```

**Step 8:** Build + test. Commit: `feat(core): index data structures and builder`.

---

## Task 3: Integrate indexes into snapshots

**Step 1:** Modify `src/GitDocumentDb.Core/Internal/TableSnapshot.cs`:
```csharp
using System.Collections.Frozen;
using GitDocumentDb.Indexing;

namespace GitDocumentDb.Internal;

internal sealed record TableSnapshot(
    string Name,
    FrozenDictionary<string, string> Records,
    FrozenDictionary<string, IIndex> Indexes);
```

**Step 2:** Modify `src/GitDocumentDb.Core/Internal/DatabaseSnapshot.cs`:
```csharp
using System.Collections.Frozen;
using GitDocumentDb.Schema;

namespace GitDocumentDb.Internal;

internal sealed record DatabaseSnapshot(
    string CommitSha,
    DateTimeOffset FetchedAt,
    DatabaseSchema Schema,
    FrozenDictionary<string, TableSnapshot> Tables);
```

**Step 3:** Modify `SnapshotBuilder.BuildAsync` — load schema and build indexes:
```csharp
using System.Collections.Frozen;
using GitDocumentDb.Indexing;
using GitDocumentDb.Schema;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Internal;

internal static class SnapshotBuilder
{
    public static async Task<DatabaseSnapshot> BuildAsync(
        IGitConnection connection,
        string commitSha,
        CancellationToken ct)
    {
        var schema = await SchemaLoader.LoadAsync(connection, commitSha, ct);
        var tree = await connection.GetTreeAsync(commitSha, ct);
        var tables = new Dictionary<string, TableSnapshot>(StringComparer.Ordinal);

        foreach (var tableEntry in tree.EnumerateChildren("tables"))
        {
            if (tableEntry.Kind != TreeEntryKind.Tree) continue;
            var records = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var recordEntry in tree.EnumerateChildren($"tables/{tableEntry.Name}"))
            {
                if (recordEntry.Kind != TreeEntryKind.Blob) continue;
                if (!tree.TryGetBlob($"tables/{tableEntry.Name}/{recordEntry.Name}", out var sha)) continue;
                var id = StripExtension(recordEntry.Name);
                records[id] = sha;
            }

            var tableSchema = schema.Tables.TryGetValue(tableEntry.Name, out var ts)
                ? ts
                : new TableSchema(tableEntry.Name, Array.Empty<IndexDefinition>());

            var buildResult = await IndexBuilder.BuildAsync(connection, tableSchema, records, ct);

            tables[tableEntry.Name] = new TableSnapshot(
                tableEntry.Name,
                records.ToFrozenDictionary(StringComparer.Ordinal),
                buildResult.Indexes);
        }

        return new DatabaseSnapshot(
            commitSha,
            DateTimeOffset.UtcNow,
            schema,
            tables.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? fileName : fileName[..dot];
    }
}
```

**Step 4:** Fix any callers. `Database.cs` uses an `EmptySnapshot()` helper — update it:
```csharp
private static DatabaseSnapshot EmptySnapshot() =>
    new("", DateTimeOffset.MinValue, DatabaseSchema.Empty, FrozenDictionary<string, TableSnapshot>.Empty);
```

Add `using GitDocumentDb.Schema;` at the top of `Database.cs`.

`SnapshotTests.cs` previously asserted `snap.Tables["accounts"].Records.Keys` — that still works since `Records` is still a property. But the test constructs `TableSnapshot` with 2 fields, might need updates. Adjust if the constructor signature change causes failures.

**Step 5:** Run `dotnet build` — expect failures in any test/impl that constructs `TableSnapshot` / `DatabaseSnapshot` with the old signatures. Fix them:
- `Database.EmptySnapshot`: update to 4-arg constructor with `DatabaseSchema.Empty`.
- Any tests that directly construct these types (likely none — `SnapshotBuilder` is used instead).

Build + test must succeed with all prior tests passing. Commit: `feat(core): load schema and build indexes in snapshots`.

---

## Task 4: Unique index enforcement on write

**Step 1:** Modify `WriteExecutor.TryCommitAsync` to check unique-index violations against `desiredEntries` after mutations are applied but before tree write.

The check: for each `PreparedOperation` with Kind=Put, after updating `desiredEntries`, parse the record JSON (loading the new blob) and check whether its value for any unique-indexed field would collide with another record's value.

Adding blob-content checks inside WriteExecutor is expensive (re-fetching blobs), so we pass the new record's JsonNode as part of `PreparedOperation`:

```csharp
public sealed record PreparedOperation(
    string TableName,
    string Id,
    string Path,
    WriteOpKind Kind,
    string? BlobSha,
    string? ExpectedVersion,
    JsonNode? RecordJson); // NEW: null for Delete, parsed for Put
```

In `Table.PutAsync` / `CommitAsync`, before dispatch:
```csharp
var node = JsonNode.Parse(bytes.Span);
// pass node in PreparedOperation
```

In `WriteExecutor.TryCommitAsync`, after the loop that builds `desiredEntries`:
```csharp
// Unique index enforcement (works for any concurrency mode).
foreach (var op in operations.Where(o => o.Kind == WriteOpKind.Put && o.RecordJson is not null))
{
    if (!snap.Tables.TryGetValue(op.TableName, out var table)) continue;
    if (!snap.Schema.Tables.TryGetValue(op.TableName, out var tableSchema)) continue;

    foreach (var idx in tableSchema.Indexes.Where(i => i.Type == IndexType.Equality && i.Unique))
    {
        var newValue = RecordFieldAccessor.Read(op.RecordJson, idx.Field);
        if (newValue is null) continue;

        if (table.Indexes.TryGetValue(idx.Field, out var indexObj) && indexObj is UniqueEqualityIndex uidx)
        {
            if (uidx.ByValue.TryGetValue(newValue, out var owner) && owner != op.Id)
            {
                var conflict = new ConflictInfo(
                    op.Path, op.ExpectedVersion ?? "", owner, null,
                    ConflictReason.UniqueViolation);
                return (false, null, op.Id, conflict);
            }
        }
    }
}
```

**Step 2:** Test `tests/GitDocumentDb.Core.Tests/UniqueIndexTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class UniqueIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, string Email);

    private static async Task<ITable<Account>> NewTableWithUniqueEmail()
    {
        var c = new InMemoryGitConnection();
        var schema = """
          {"schemaVersion":1,"tables":{"accounts":{"indexes":[{"field":"email","type":"equality","unique":true}]}}}
          """;
        var blob = await c.WriteBlobAsync(Encoding.UTF8.GetBytes(schema), Ct);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, ".schema.json", blob),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, Ct);

        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<Account>("accounts");
    }

    [Fact]
    public async Task Duplicate_unique_field_is_rejected()
    {
        var t = await NewTableWithUniqueEmail();
        var first = await t.PutAsync("a", new Account("a", "x@y"), null, Ct);
        first.Success.Should().BeTrue();

        var second = await t.PutAsync("b", new Account("b", "x@y"), null, Ct);
        second.Success.Should().BeFalse();
        second.Conflict!.Reason.Should().Be(ConflictReason.UniqueViolation);
    }

    [Fact]
    public async Task Same_record_reupdate_is_not_a_violation()
    {
        var t = await NewTableWithUniqueEmail();
        await t.PutAsync("a", new Account("a", "x@y"), null, Ct);
        var again = await t.PutAsync("a", new Account("a", "x@y"), null, Ct);
        again.Success.Should().BeTrue();
    }
}
```

**Step 3:** Build + test. Commit: `feat(core): enforce unique equality indexes on write`.

---

## Task 5: Query API (builder + Query + QueryAsync)

**Step 1:** `src/GitDocumentDb.Core/Querying/QueryException.cs`:
```csharp
namespace GitDocumentDb;
public sealed class QueryException : GitDocumentDbException
{
    public QueryException(string message) : base(message) { }
}
```

**Step 2:** `src/GitDocumentDb.Core/Querying/Query.cs` — opaque wrapper carrying the expression:
```csharp
using System.Linq.Expressions;

namespace GitDocumentDb;

public sealed class Query
{
    internal LambdaExpression? Predicate { get; }
    internal LambdaExpression? OrderKey { get; }
    internal bool OrderDescending { get; }
    internal int? SkipCount { get; }
    internal int? TakeCount { get; }

    internal Query(LambdaExpression? predicate, LambdaExpression? orderKey, bool orderDescending, int? skip, int? take)
    {
        Predicate = predicate;
        OrderKey = orderKey;
        OrderDescending = orderDescending;
        SkipCount = skip;
        TakeCount = take;
    }

    public static QueryBuilder<T> For<T>() where T : class => new();
}
```

**Step 3:** `Querying/QueryBuilder.cs`:
```csharp
using System.Linq.Expressions;

namespace GitDocumentDb;

public sealed class QueryBuilder<T> where T : class
{
    private Expression<Func<T, bool>>? _predicate;
    private LambdaExpression? _orderKey;
    private bool _orderDesc;
    private int? _skip;
    private int? _take;

    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        _predicate = predicate;
        return this;
    }

    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> key)
    {
        _orderKey = key; _orderDesc = false;
        return this;
    }

    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> key)
    {
        _orderKey = key; _orderDesc = true;
        return this;
    }

    public QueryBuilder<T> Skip(int count) { _skip = count; return this; }
    public QueryBuilder<T> Take(int count) { _take = count; return this; }

    public Query Build() => new(_predicate, _orderKey, _orderDesc, _skip, _take);
}
```

**Step 4:** Add `QueryAsync` to `ITable<T>`:
```csharp
Task<IReadOnlyList<Versioned<T>>> QueryAsync(Query query, ReadOptions? options = null, CancellationToken ct = default);
```

**Step 5:** `Querying/QueryCompiler.cs` — extracts indexable predicates. Supports:
- `x.Field == const` → equality index lookup
- `x.Field <op> const` with `<`, `<=`, `>`, `>=` → range index slice
- `&&` conjunctions — intersect candidates
- Anything else → "not indexable", fall through to full scan

```csharp
using System.Linq.Expressions;

namespace GitDocumentDb;

internal static class QueryCompiler
{
    public sealed record IndexClause(string Field, IndexOp Op, object Value);

    public enum IndexOp { Equal, Greater, GreaterOrEqual, Less, LessOrEqual }

    public static List<IndexClause>? ExtractIndexClauses(LambdaExpression predicate)
    {
        var clauses = new List<IndexClause>();
        if (!Visit(predicate.Body, clauses)) return null;
        return clauses;
    }

    private static bool Visit(Expression expr, List<IndexClause> clauses)
    {
        if (expr is BinaryExpression bin)
        {
            if (bin.NodeType == ExpressionType.AndAlso)
                return Visit(bin.Left, clauses) && Visit(bin.Right, clauses);

            if (TryExtractComparison(bin, out var clause))
            {
                clauses.Add(clause);
                return true;
            }
        }
        return false;
    }

    private static bool TryExtractComparison(BinaryExpression bin, out IndexClause clause)
    {
        clause = default!;
        var op = bin.NodeType switch
        {
            ExpressionType.Equal => IndexOp.Equal,
            ExpressionType.GreaterThan => IndexOp.Greater,
            ExpressionType.GreaterThanOrEqual => IndexOp.GreaterOrEqual,
            ExpressionType.LessThan => IndexOp.Less,
            ExpressionType.LessThanOrEqual => IndexOp.LessOrEqual,
            _ => (IndexOp?)null,
        };
        if (op is null) return false;

        string? field = null;
        object? value = null;
        if (bin.Left is MemberExpression ml && bin.Right is ConstantExpression cr)
        { field = ml.Member.Name; value = cr.Value; }
        else if (bin.Right is MemberExpression mr && bin.Left is ConstantExpression cl)
        {
            field = mr.Member.Name; value = cl.Value;
            op = Flip(op.Value);
        }
        if (field is null || value is null) return false;
        clause = new IndexClause(field, op.Value, value);
        return true;
    }

    private static IndexOp Flip(IndexOp op) => op switch
    {
        IndexOp.Equal => IndexOp.Equal,
        IndexOp.Greater => IndexOp.Less,
        IndexOp.GreaterOrEqual => IndexOp.LessOrEqual,
        IndexOp.Less => IndexOp.Greater,
        IndexOp.LessOrEqual => IndexOp.GreaterOrEqual,
        _ => op,
    };
}
```

**Step 6:** Implement `Table.QueryAsync`:

```csharp
public async Task<IReadOnlyList<Versioned<T>>> QueryAsync(
    Query query, ReadOptions? options = null, CancellationToken ct = default)
{
    await MaybeFetchAsync(options, ct);
    await _db.EnsureOpenedAsync(ct);

    var snap = _db.CurrentSnapshot;
    if (!snap.Tables.TryGetValue(_name, out var table)) return Array.Empty<Versioned<T>>();

    // Determine candidate IDs
    IEnumerable<string> candidateIds;
    bool usedIndex = false;

    if (query.Predicate is not null)
    {
        var clauses = QueryCompiler.ExtractIndexClauses(query.Predicate);
        if (clauses is not null && clauses.Count > 0)
        {
            // Use first clause that matches a known index; intersect as we go.
            HashSet<string>? current = null;
            foreach (var clause in clauses)
            {
                if (!table.Indexes.TryGetValue(clause.Field, out var idx)) continue;
                var ids = ResolveClause(idx, clause);
                if (ids is null) continue;
                usedIndex = true;
                current = current is null ? new HashSet<string>(ids, StringComparer.Ordinal)
                                          : new HashSet<string>(current.Intersect(ids), StringComparer.Ordinal);
            }
            candidateIds = current ?? table.Records.Keys;
        }
        else
        {
            candidateIds = table.Records.Keys;
        }
    }
    else
    {
        candidateIds = table.Records.Keys;
    }

    // Guard full scans
    if (!usedIndex && table.Records.Count > _db.Options.MaxFullScanRecordCount)
        throw new QueryException(
            $"Full-table scan on '{_name}' ({table.Records.Count} records) exceeds MaxFullScanRecordCount={_db.Options.MaxFullScanRecordCount}");

    // Materialize records
    var results = new List<Versioned<T>>();
    Func<T, bool>? compiled = query.Predicate is Expression<Func<T, bool>> typed ? typed.Compile() : null;

    foreach (var id in candidateIds)
    {
        if (!table.Records.TryGetValue(id, out var blobSha)) continue;
        var bytes = await _db.Connection.GetBlobAsync(blobSha, ct);
        var record = _db.Serializer.Deserialize<T>(bytes.Span);
        if (compiled is not null && !compiled(record)) continue;
        results.Add(new Versioned<T>(record, id, blobSha, snap.CommitSha));
    }

    if (query.OrderKey is LambdaExpression ok)
    {
        var keySelector = ok.Compile();
        results = query.OrderDescending
            ? results.OrderByDescending(r => keySelector.DynamicInvoke(r.Record)).ToList()
            : results.OrderBy(r => keySelector.DynamicInvoke(r.Record)).ToList();
    }

    if (query.SkipCount.HasValue) results = results.Skip(query.SkipCount.Value).ToList();
    if (query.TakeCount.HasValue) results = results.Take(query.TakeCount.Value).ToList();

    return results;
}

private static IEnumerable<string>? ResolveClause(
    GitDocumentDb.Indexing.IIndex idx,
    QueryCompiler.IndexClause clause)
{
    if (idx is GitDocumentDb.Indexing.EqualityIndex eq && clause.Op == QueryCompiler.IndexOp.Equal)
    {
        return eq.ByValue.TryGetValue(clause.Value, out var list) ? list : Array.Empty<string>();
    }
    if (idx is GitDocumentDb.Indexing.UniqueEqualityIndex ueq && clause.Op == QueryCompiler.IndexOp.Equal)
    {
        return ueq.ByValue.TryGetValue(clause.Value, out var id) ? new[] { id } : Array.Empty<string>();
    }
    if (idx is GitDocumentDb.Indexing.RangeIndex rng)
    {
        var matching = new List<string>();
        foreach (var key in rng.Sorted.Keys)
        {
            if (Matches(key, clause)) foreach (var id in rng.Sorted[key]) matching.Add(id);
        }
        return matching;
    }
    return null;
}

private static bool Matches(object key, QueryCompiler.IndexClause clause)
{
    var cmp = Comparer<object>.Default.Compare(key, clause.Value);
    return clause.Op switch
    {
        QueryCompiler.IndexOp.Equal => cmp == 0,
        QueryCompiler.IndexOp.Greater => cmp > 0,
        QueryCompiler.IndexOp.GreaterOrEqual => cmp >= 0,
        QueryCompiler.IndexOp.Less => cmp < 0,
        QueryCompiler.IndexOp.LessOrEqual => cmp <= 0,
        _ => false,
    };
}
```

**Step 7:** Add `MaxFullScanRecordCount` to `DatabaseOptions` (default 10000):
```csharp
public int MaxFullScanRecordCount { get; init; } = 10_000;
```

**Step 8:** Tests `tests/GitDocumentDb.Core.Tests/QueryTests.cs` — covers: equality via index, range via index, full-scan with small table, full-scan throttle, OrderBy, Skip/Take.

```csharp
using System.Text;
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class QueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record User(string Id, string Status, int Age);

    private static async Task<ITable<User>> NewTable(string schemaJson)
    {
        var c = new InMemoryGitConnection();
        var blob = await c.WriteBlobAsync(Encoding.UTF8.GetBytes(schemaJson), Ct);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, ".schema.json", blob),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, Ct);

        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<User>("users");
    }

    [Fact]
    public async Task Equality_index_returns_matching_records()
    {
        var t = await NewTable("""
          {"schemaVersion":1,"tables":{"users":{"indexes":[{"field":"status","type":"equality"}]}}}
          """);
        await t.PutAsync("a", new User("a", "active", 30), null, Ct);
        await t.PutAsync("b", new User("b", "active", 25), null, Ct);
        await t.PutAsync("c", new User("c", "inactive", 40), null, Ct);

        var q = Query.For<User>().Where(u => u.Status == "active").Build();
        var results = await t.QueryAsync(q, null, Ct);
        results.Select(r => r.Id).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task Range_index_returns_matching_records_in_order()
    {
        var t = await NewTable("""
          {"schemaVersion":1,"tables":{"users":{"indexes":[{"field":"age","type":"range"}]}}}
          """);
        await t.PutAsync("a", new User("a", "x", 20), null, Ct);
        await t.PutAsync("b", new User("b", "x", 30), null, Ct);
        await t.PutAsync("c", new User("c", "x", 40), null, Ct);

        var q = Query.For<User>().Where(u => u.Age >= 25).OrderBy(u => u.Age).Build();
        var results = await t.QueryAsync(q, null, Ct);
        results.Select(r => r.Id).Should().Equal("b", "c");
    }

    [Fact]
    public async Task Full_scan_allowed_under_threshold()
    {
        var t = await NewTable("""{"schemaVersion":1,"tables":{"users":{"indexes":[]}}}""");
        await t.PutAsync("a", new User("a", "x", 10), null, Ct);
        await t.PutAsync("b", new User("b", "y", 20), null, Ct);
        var q = Query.For<User>().Where(u => u.Age > 15).Build();
        var results = await t.QueryAsync(q, null, Ct);
        results.Select(r => r.Id).Should().Equal("b");
    }

    [Fact]
    public async Task Skip_take_applied()
    {
        var t = await NewTable("""{"schemaVersion":1,"tables":{"users":{"indexes":[]}}}""");
        for (var i = 0; i < 5; i++) await t.PutAsync($"u{i}", new User($"u{i}", "x", i), null, Ct);
        var q = Query.For<User>().OrderBy(u => u.Age).Skip(1).Take(2).Build();
        var results = await t.QueryAsync(q, null, Ct);
        results.Should().HaveCount(2);
        results.Select(r => r.Record.Age).Should().Equal(1, 2);
    }
}
```

**Step 9:** Build + test. Commit: `feat(core): query API with index-aware execution`.

---

## Task 6: README update

Add a new bullet under "Phase 3 (complete)" with: schema loading, equality/range/unique indexes, LINQ-based query API with index-aware execution, unique constraint enforcement on write.

Commit: `docs: update README for phase 3`.

---

## Self-Review

**Coverage:**
- §4.11 indexing — ✅ Tasks 2-4.
- §4.12 query API (v1 scope: equality, range, full-scan threshold, OrderBy/Skip/Take) — ✅ Task 5.
- §4.17 `QueryException` — ✅ Task 5.
- Schema loading — ✅ Task 1.

**Deferred:**
- Index snapshot persistence to disk (Phase 4 with real transport).
- Disjunctions in query predicates (v2).

**Type consistency:** `PreparedOperation` gains `RecordJson` in Task 4; `Table.PutAsync`/`CommitAsync` pass it. `TableSnapshot` gains `Indexes`, all construction sites go through `SnapshotBuilder`. `Query` is opaque; builder is the public construction API.
