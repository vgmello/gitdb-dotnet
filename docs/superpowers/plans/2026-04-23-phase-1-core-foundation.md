# GitDocumentDb Phase 1: Core Foundation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a minimum-viable `GitDocumentDb.Core` library supporting CRUD against an in-memory Git backend with `LastWriteWins` concurrency, lock-free reads from immutable snapshots, and change notifications. Indexes, queries, other concurrency modes, and real Git transports come in later phases.

**Architecture:** Core is a pure .NET library with an `IGitConnection` abstraction that hides Git internals. Phase 1 ships a managed `InMemoryGitConnection` (no native `libgit2` dependency) that implements the abstraction with dictionaries — enough to exercise every Core code path in fast unit tests. The read path is lock-free via an atomic reference to an immutable `DatabaseSnapshot`; writes serialize through a per-database async lock, build a new snapshot off-thread, and atomically swap on push success.

**Tech Stack:** .NET 10, C# (latest), xUnit v3, FluentAssertions, BenchmarkDotNet (for allocation benchmarks in Task 17), `System.Text.Json`, `System.IO.Hashing` (for Git-compatible SHA-1 blob hashing), `System.Collections.Immutable`, `System.Collections.Frozen`.

---

## File Structure

```
gitdb-dotnet/
├── GitDocumentDb.sln
├── src/
│   └── GitDocumentDb.Core/
│       ├── GitDocumentDb.Core.csproj
│       ├── Abstractions/
│       │   ├── IDocumentDatabase.cs           # Top-level factory
│       │   ├── IDatabase.cs                   # Per-database API
│       │   ├── ITable.cs                      # Per-table API
│       │   └── IRecordSerializer.cs           # Pluggable serialization
│       ├── Transport/
│       │   ├── IGitConnection.cs              # Git transport abstraction
│       │   ├── ITreeView.cs                   # Tree reading
│       │   ├── TreeEntry.cs                   # Tree entry record
│       │   ├── CommitSpec.cs                  # Commit build spec
│       │   ├── TreeBuildSpec.cs               # Tree build spec
│       │   └── PushResult.cs                  # Push outcome
│       ├── Transport.InMemory/
│       │   ├── InMemoryGitConnection.cs       # Managed fake
│       │   └── InMemoryTreeView.cs
│       ├── Models/
│       │   ├── Versioned.cs
│       │   ├── WriteOptions.cs
│       │   ├── WriteResult.cs
│       │   ├── BatchResult.cs
│       │   ├── OperationResult.cs
│       │   ├── WriteOperation.cs
│       │   ├── ConflictInfo.cs
│       │   ├── FetchResult.cs
│       │   ├── ChangeNotification.cs
│       │   ├── ConcurrencyMode.cs
│       │   ├── ReadOptions.cs
│       │   ├── DatabaseOptions.cs
│       │   └── Versions.cs                    # Absent sentinel
│       ├── Internal/
│       │   ├── DatabaseSnapshot.cs            # Immutable state
│       │   ├── TableSnapshot.cs
│       │   ├── SnapshotBuilder.cs             # Builds snapshots from trees
│       │   ├── RecordIdValidator.cs
│       │   ├── NameValidator.cs               # DB/table names
│       │   └── GitBlobHasher.cs               # Git-format SHA-1 of blobs
│       ├── Serialization/
│       │   └── SystemTextJsonRecordSerializer.cs
│       ├── Exceptions/
│       │   ├── GitDocumentDbException.cs
│       │   ├── PushRejectedException.cs
│       │   └── TransportException.cs
│       └── Implementation/
│           ├── DocumentDatabase.cs            # IDocumentDatabase impl
│           ├── Database.cs                    # IDatabase impl
│           └── Table.cs                       # ITable<T> impl
└── tests/
    └── GitDocumentDb.Core.Tests/
        ├── GitDocumentDb.Core.Tests.csproj
        ├── InMemoryGitConnectionTests.cs
        ├── RecordIdValidatorTests.cs
        ├── GitBlobHasherTests.cs
        ├── SerializerTests.cs
        ├── SnapshotTests.cs
        ├── TableCrudTests.cs
        ├── MultiWriterTests.cs
        ├── ChangeNotificationTests.cs
        └── AllocationBenchmarks.cs             # Manual-run benchmarks
```

Every file has one clear responsibility. Abstractions are in `Abstractions/`. The in-memory transport is a sibling namespace under `Transport.InMemory` so it can later be extracted to its own package without touching Core.

---

## Task 1: Solution scaffolding

**Files:**
- Create: `GitDocumentDb.sln`
- Create: `src/GitDocumentDb.Core/GitDocumentDb.Core.csproj`
- Create: `src/GitDocumentDb.Core/Class1.cs` (temporary, deleted in next task)
- Create: `tests/GitDocumentDb.Core.Tests/GitDocumentDb.Core.Tests.csproj`
- Create: `tests/GitDocumentDb.Core.Tests/SmokeTest.cs`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `global.json`

- [ ] **Step 1: Create `global.json`**

```json
{
  "sdk": {
    "version": "10.0.0",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 2: Create `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest</AnalysisLevel>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="System.Collections.Immutable" Version="10.0.0" />
    <PackageVersion Include="xunit.v3" Version="1.0.0" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageVersion Include="FluentAssertions" Version="6.12.1" />
    <PackageVersion Include="BenchmarkDotNet" Version="0.14.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create the Core project**

Run:
```bash
cd /Users/vgmello-dev/repos/projects/gitdb-dotnet
dotnet new classlib -n GitDocumentDb.Core -o src/GitDocumentDb.Core --framework net10.0
```

Overwrite `src/GitDocumentDb.Core/GitDocumentDb.Core.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>GitDocumentDb</RootNamespace>
    <AssemblyName>GitDocumentDb.Core</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Collections.Immutable" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="GitDocumentDb.Core.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create the test project**

Run:
```bash
dotnet new xunit3 -n GitDocumentDb.Core.Tests -o tests/GitDocumentDb.Core.Tests --framework net10.0
```

Overwrite `tests/GitDocumentDb.Core.Tests/GitDocumentDb.Core.Tests.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <RootNamespace>GitDocumentDb.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\GitDocumentDb.Core\GitDocumentDb.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Create the solution**

Run:
```bash
cd /Users/vgmello-dev/repos/projects/gitdb-dotnet
dotnet new sln -n GitDocumentDb
dotnet sln add src/GitDocumentDb.Core/GitDocumentDb.Core.csproj
dotnet sln add tests/GitDocumentDb.Core.Tests/GitDocumentDb.Core.Tests.csproj
rm src/GitDocumentDb.Core/Class1.cs
```

- [ ] **Step 7: Write smoke test**

Create `tests/GitDocumentDb.Core.Tests/SmokeTest.cs`:

```csharp
namespace GitDocumentDb.Tests;

public class SmokeTest
{
    [Fact]
    public void BuildsAndRuns() => Assert.True(true);
}
```

- [ ] **Step 8: Build and run tests**

Run:
```bash
dotnet build
dotnet test
```

Expected: `Passed: 1`.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "chore: solution scaffolding for GitDocumentDb.Core"
```

---

## Task 2: Core models (records, enums, options)

**Files:**
- Create: `src/GitDocumentDb.Core/Models/ConcurrencyMode.cs`
- Create: `src/GitDocumentDb.Core/Models/Versions.cs`
- Create: `src/GitDocumentDb.Core/Models/Versioned.cs`
- Create: `src/GitDocumentDb.Core/Models/ConflictInfo.cs`
- Create: `src/GitDocumentDb.Core/Models/WriteOptions.cs`
- Create: `src/GitDocumentDb.Core/Models/WriteResult.cs`
- Create: `src/GitDocumentDb.Core/Models/WriteOperation.cs`
- Create: `src/GitDocumentDb.Core/Models/OperationResult.cs`
- Create: `src/GitDocumentDb.Core/Models/BatchResult.cs`
- Create: `src/GitDocumentDb.Core/Models/FetchResult.cs`
- Create: `src/GitDocumentDb.Core/Models/ChangeNotification.cs`
- Create: `src/GitDocumentDb.Core/Models/ReadOptions.cs`
- Create: `src/GitDocumentDb.Core/Models/DatabaseOptions.cs`

These are pure data types — no behavior. No tests in this task; later tasks exercise them.

- [ ] **Step 1: `ConcurrencyMode.cs`**

```csharp
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
```

- [ ] **Step 2: `Versions.cs`**

```csharp
namespace GitDocumentDb;

public static class Versions
{
    public const string Absent = "__absent__";
}
```

- [ ] **Step 3: `Versioned.cs`**

```csharp
namespace GitDocumentDb;

public sealed record Versioned<T>(T Record, string Id, string Version, string CommitSha) where T : class;
```

- [ ] **Step 4: `ConflictInfo.cs`**

```csharp
namespace GitDocumentDb;

public sealed record ConflictInfo(
    string Path,
    string ExpectedVersion,
    string ActualVersion,
    ReadOnlyMemory<byte>? CurrentContent,
    ConflictReason Reason);
```

- [ ] **Step 5: `WriteOptions.cs`**

```csharp
namespace GitDocumentDb;

public sealed class WriteOptions
{
    public ConcurrencyMode Mode { get; init; } = ConcurrencyMode.LastWriteWins;
    public string? ExpectedVersion { get; init; }
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan MaxRetryBackoff { get; init; } = TimeSpan.FromSeconds(5);
    public string? Author { get; init; }
    public string? CommitMessage { get; init; }
}
```

- [ ] **Step 6: `WriteResult.cs`**

```csharp
namespace GitDocumentDb;

public sealed record WriteResult(
    bool Success,
    string? NewVersion,
    string? NewCommitSha,
    ConflictInfo? Conflict,
    WriteFailureReason? FailureReason);
```

- [ ] **Step 7: `WriteOperation.cs`**

```csharp
namespace GitDocumentDb;

public sealed record WriteOperation<T>(
    WriteOpKind Kind,
    string Id,
    T? Record,
    string? ExpectedVersion) where T : class;
```

- [ ] **Step 8: `OperationResult.cs`**

```csharp
namespace GitDocumentDb;

public sealed record OperationResult(
    string Id,
    bool Success,
    string? NewVersion,
    ConflictInfo? Conflict,
    WriteFailureReason? FailureReason);
```

- [ ] **Step 9: `BatchResult.cs`**

```csharp
namespace GitDocumentDb;

public sealed record BatchResult(
    bool Success,
    string? NewCommitSha,
    IReadOnlyList<OperationResult> Operations);
```

- [ ] **Step 10: `FetchResult.cs`**

```csharp
namespace GitDocumentDb;

public sealed record FetchResult(
    bool HadChanges,
    string PreviousCommit,
    string CurrentCommit,
    IReadOnlyList<string> ChangedPaths);
```

- [ ] **Step 11: `ChangeNotification.cs`**

```csharp
namespace GitDocumentDb;

public sealed record ChangeNotification(
    string CommitSha,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> ChangedPaths,
    ChangeReason Reason);
```

- [ ] **Step 12: `ReadOptions.cs`**

```csharp
namespace GitDocumentDb;

public sealed class ReadOptions
{
    public bool FetchFirst { get; init; }
    public TimeSpan? MaxStaleness { get; init; }
}
```

- [ ] **Step 13: `DatabaseOptions.cs`**

```csharp
namespace GitDocumentDb;

public sealed class DatabaseOptions
{
    public ConcurrencyMode DefaultConcurrencyMode { get; init; } = ConcurrencyMode.LastWriteWins;
    public bool EnableBackgroundFetch { get; init; }
    public TimeSpan BackgroundFetchInterval { get; init; } = TimeSpan.FromSeconds(60);
    public long RecordSizeSoftLimitBytes { get; init; } = 1L * 1024 * 1024;
    public long RecordSizeHardLimitBytes { get; init; } = 10L * 1024 * 1024;
    public int MaxSnapshotCacheCount { get; init; } = 2;
    public string DefaultAuthorName { get; init; } = "GitDocumentDb";
    public string DefaultAuthorEmail { get; init; } = "gitdb@localhost";
}
```

- [ ] **Step 14: Build**

Run: `dotnet build`. Expected: `Build succeeded`.

- [ ] **Step 15: Commit**

```bash
git add -A
git commit -m "feat(core): define public model types and enums"
```

---

## Task 3: Name and record ID validators

**Files:**
- Create: `src/GitDocumentDb.Core/Internal/RecordIdValidator.cs`
- Create: `src/GitDocumentDb.Core/Internal/NameValidator.cs`
- Create: `tests/GitDocumentDb.Core.Tests/RecordIdValidatorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/GitDocumentDb.Core.Tests/RecordIdValidatorTests.cs`:

```csharp
using FluentAssertions;
using GitDocumentDb.Internal;

namespace GitDocumentDb.Tests;

public class RecordIdValidatorTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("abc_123")]
    [InlineData("A-B-C")]
    [InlineData("file.json")]
    [InlineData("a.b.c-d_e")]
    public void Valid_ids_are_accepted(string id)
    {
        RecordIdValidator.IsValid(id).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("with space")]
    [InlineData("with/slash")]
    [InlineData("with\\backslash")]
    [InlineData("semi;colon")]
    public void Invalid_ids_are_rejected(string id)
    {
        RecordIdValidator.IsValid(id).Should().BeFalse();
    }

    [Fact]
    public void Too_long_ids_are_rejected()
    {
        var id = new string('a', 201);
        RecordIdValidator.IsValid(id).Should().BeFalse();
    }

    [Fact]
    public void ThrowIfInvalid_throws_ArgumentException_with_paramName()
    {
        var act = () => RecordIdValidator.ThrowIfInvalid("bad/id", "id");
        act.Should().Throw<ArgumentException>().WithParameterName("id");
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("Accounts")]
    [InlineData("my-db_1")]
    public void Valid_names_accepted(string name)
    {
        NameValidator.IsValid(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("with.dot")]
    [InlineData("with space")]
    public void Invalid_names_rejected(string name)
    {
        NameValidator.IsValid(name).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RecordIdValidatorTests"`
Expected: compilation error `RecordIdValidator does not exist`.

- [ ] **Step 3: Implement `RecordIdValidator.cs`**

```csharp
namespace GitDocumentDb.Internal;

internal static class RecordIdValidator
{
    private const int MaxLength = 200;

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength || id[0] == '.')
            return false;
        foreach (var c in id)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                     or '_' or '-' or '.';
            if (!ok) return false;
        }
        return true;
    }

    public static void ThrowIfInvalid(string? id, string paramName)
    {
        if (!IsValid(id))
            throw new ArgumentException(
                $"Record id must match [A-Za-z0-9_\\-.]{{1,{MaxLength}}} and not start with '.'.",
                paramName);
    }
}
```

- [ ] **Step 4: Implement `NameValidator.cs`**

```csharp
namespace GitDocumentDb.Internal;

internal static class NameValidator
{
    private const int MaxLength = 100;

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength) return false;
        foreach (var c in name)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                     or '_' or '-';
            if (!ok) return false;
        }
        return true;
    }

    public static void ThrowIfInvalid(string? name, string paramName)
    {
        if (!IsValid(name))
            throw new ArgumentException(
                $"Name must match [A-Za-z0-9_\\-]{{1,{MaxLength}}}.", paramName);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~RecordIdValidatorTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): record id and name validators"
```

---

## Task 4: Git blob hasher (SHA-1 of Git's blob header + content)

Git's blob SHA is `SHA1("blob " + len + "\0" + content)`. We need this to generate deterministic version tokens that will match whatever real Git computes in later phases.

**Files:**
- Create: `src/GitDocumentDb.Core/Internal/GitBlobHasher.cs`
- Create: `tests/GitDocumentDb.Core.Tests/GitBlobHasherTests.cs`

- [ ] **Step 1: Write failing test with known Git SHA**

Create `tests/GitDocumentDb.Core.Tests/GitBlobHasherTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using GitDocumentDb.Internal;

namespace GitDocumentDb.Tests;

public class GitBlobHasherTests
{
    // Git's SHA-1 for the empty blob is well-known:
    //   git hash-object --stdin < /dev/null
    //   -> e69de29bb2d1d6434b8b29ae775ad8c2e48c5391
    [Fact]
    public void Empty_blob_sha_matches_git()
    {
        GitBlobHasher.Hash(ReadOnlySpan<byte>.Empty)
            .Should().Be("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391");
    }

    // echo -n "hello" | git hash-object --stdin
    // -> b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0
    [Fact]
    public void Hello_blob_sha_matches_git()
    {
        GitBlobHasher.Hash(Encoding.UTF8.GetBytes("hello"))
            .Should().Be("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0");
    }
}
```

- [ ] **Step 2: Run test, verify compile failure**

Run: `dotnet test --filter "GitBlobHasherTests"`
Expected: compile error.

- [ ] **Step 3: Implement `GitBlobHasher.cs`**

```csharp
using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace GitDocumentDb.Internal;

internal static class GitBlobHasher
{
    public static string Hash(ReadOnlySpan<byte> content)
    {
        // Header: "blob <length>\0"
        Span<byte> headerBuf = stackalloc byte[32];
        var headerLen = Encoding.ASCII.GetBytes($"blob {content.Length}\0", headerBuf);

        using var sha1 = SHA1.Create();
        sha1.TransformBlock(headerBuf[..headerLen].ToArray(), 0, headerLen, null, 0);
        var tail = content.ToArray();
        sha1.TransformFinalBlock(tail, 0, tail.Length);
        return Convert.ToHexStringLower(sha1.Hash!);
    }
}
```

- [ ] **Step 4: Run test**

Run: `dotnet test --filter "GitBlobHasherTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): Git-compatible blob SHA-1 hasher"
```

---

## Task 5: IGitConnection abstraction and supporting types

**Files:**
- Create: `src/GitDocumentDb.Core/Transport/TreeEntry.cs`
- Create: `src/GitDocumentDb.Core/Transport/ITreeView.cs`
- Create: `src/GitDocumentDb.Core/Transport/TreeBuildSpec.cs`
- Create: `src/GitDocumentDb.Core/Transport/CommitSpec.cs`
- Create: `src/GitDocumentDb.Core/Transport/PushResult.cs`
- Create: `src/GitDocumentDb.Core/Transport/IGitConnection.cs`

No tests in this task — it's pure interface definition. Task 6 implements and tests.

- [ ] **Step 1: `TreeEntry.cs`**

```csharp
namespace GitDocumentDb.Transport;

public enum TreeEntryKind { Blob, Tree }

public sealed record TreeEntry(string Name, string Sha, TreeEntryKind Kind);
```

- [ ] **Step 2: `ITreeView.cs`**

```csharp
namespace GitDocumentDb.Transport;

public interface ITreeView
{
    string CommitSha { get; }
    bool TryGetBlob(string path, out string blobSha);
    bool TryGetTree(string path, out ITreeView subtree);
    IEnumerable<TreeEntry> EnumerateChildren(string path);
}
```

- [ ] **Step 3: `TreeBuildSpec.cs`**

```csharp
namespace GitDocumentDb.Transport;

public sealed record TreeBuildSpec(
    string? BaseTreeSha,
    IReadOnlyList<TreeMutation> Mutations);

public sealed record TreeMutation(
    TreeMutationKind Kind,
    string Path,
    string? BlobSha);

public enum TreeMutationKind { Upsert, Delete }
```

- [ ] **Step 4: `CommitSpec.cs`**

```csharp
namespace GitDocumentDb.Transport;

public sealed record CommitSpec(
    string TreeSha,
    string? ParentSha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string Message);
```

- [ ] **Step 5: `PushResult.cs`**

```csharp
namespace GitDocumentDb.Transport;

public sealed record PushResult(
    bool Success,
    string? NewRemoteSha,
    PushRejectReason? Reason);

public enum PushRejectReason { NonFastForward, AuthFailure, Network, RemoteError, RefNotFound }
```

- [ ] **Step 6: `IGitConnection.cs`**

```csharp
namespace GitDocumentDb.Transport;

public interface IGitConnection
{
    Task<string?> ResolveRefAsync(string refName, CancellationToken ct);
    Task<IReadOnlyList<string>> ListRefsAsync(string prefix, CancellationToken ct);

    Task<ITreeView> GetTreeAsync(string commitSha, CancellationToken ct);
    Task<ReadOnlyMemory<byte>> GetBlobAsync(string blobSha, CancellationToken ct);

    Task<string> WriteBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct);
    Task<string> WriteTreeAsync(TreeBuildSpec spec, CancellationToken ct);
    Task<string> CreateCommitAsync(CommitSpec spec, CancellationToken ct);

    Task<PushResult> UpdateRefAsync(
        string refName, string? expectedOldSha, string newSha, CancellationToken ct);

    Task<FetchResult> FetchAsync(string refName, CancellationToken ct);
}
```

- [ ] **Step 7: Build**

Run: `dotnet build`. Expected: success.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): IGitConnection transport abstraction"
```

---

## Task 6: InMemoryGitConnection (managed fake)

The fake stores blobs, trees, and commits in dictionaries keyed by SHA. Refs are a single mutable dictionary with optimistic CAS via `expectedOldSha`. No network, no native libs.

**Files:**
- Create: `src/GitDocumentDb.Core/Transport.InMemory/InMemoryTreeView.cs`
- Create: `src/GitDocumentDb.Core/Transport.InMemory/InMemoryGitConnection.cs`
- Create: `tests/GitDocumentDb.Core.Tests/InMemoryGitConnectionTests.cs`

- [ ] **Step 1: Write failing test suite**

Create `tests/GitDocumentDb.Core.Tests/InMemoryGitConnectionTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class InMemoryGitConnectionTests
{
    [Fact]
    public async Task WriteBlob_returns_deterministic_sha()
    {
        var c = new InMemoryGitConnection();
        var sha1 = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("hello"), default);
        var sha2 = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("hello"), default);
        sha1.Should().Be(sha2);
        sha1.Should().Be("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0");
    }

    [Fact]
    public async Task GetBlob_returns_content_written()
    {
        var c = new InMemoryGitConnection();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var sha = await c.WriteBlobAsync(bytes, default);
        var got = await c.GetBlobAsync(sha, default);
        got.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task Tree_roundtrip_enumerates_children()
    {
        var c = new InMemoryGitConnection();
        var b1 = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("one"), default);
        var b2 = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("two"), default);

        var treeSha = await c.WriteTreeAsync(new TreeBuildSpec(
            BaseTreeSha: null,
            Mutations: new[]
            {
                new TreeMutation(TreeMutationKind.Upsert, "tables/a/1.json", b1),
                new TreeMutation(TreeMutationKind.Upsert, "tables/a/2.json", b2),
            }), default);

        var commitSha = await c.CreateCommitAsync(new CommitSpec(
            treeSha, null, "t", "t@x", DateTimeOffset.UnixEpoch, "init"), default);

        var tree = await c.GetTreeAsync(commitSha, default);
        tree.TryGetBlob("tables/a/1.json", out var s1).Should().BeTrue();
        s1.Should().Be(b1);
        tree.EnumerateChildren("tables/a").Select(e => e.Name)
            .Should().BeEquivalentTo("1.json", "2.json");
    }

    [Fact]
    public async Task UpdateRef_succeeds_on_matching_expected()
    {
        var c = new InMemoryGitConnection();
        var commit = await CreateEmptyCommit(c, null);
        var result = await c.UpdateRefAsync("refs/heads/db/x", null, commit, default);
        result.Success.Should().BeTrue();
        (await c.ResolveRefAsync("refs/heads/db/x", default)).Should().Be(commit);
    }

    [Fact]
    public async Task UpdateRef_fails_on_non_fast_forward()
    {
        var c = new InMemoryGitConnection();
        var c1 = await CreateEmptyCommit(c, null);
        await c.UpdateRefAsync("refs/heads/db/x", null, c1, default);

        var c2 = await CreateEmptyCommit(c, c1);
        // Caller thinks the ref is still pointing at null.
        var bad = await c.UpdateRefAsync("refs/heads/db/x", null, c2, default);
        bad.Success.Should().BeFalse();
        bad.Reason.Should().Be(PushRejectReason.NonFastForward);
    }

    [Fact]
    public async Task Delete_mutation_removes_entry_from_new_tree()
    {
        var c = new InMemoryGitConnection();
        var b = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("x"), default);
        var t1 = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, "a/1.json", b),
            new TreeMutation(TreeMutationKind.Upsert, "a/2.json", b),
        }), default);
        var t2 = await c.WriteTreeAsync(new TreeBuildSpec(t1, new[]
        {
            new TreeMutation(TreeMutationKind.Delete, "a/1.json", null),
        }), default);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            t2, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), default);
        var tree = await c.GetTreeAsync(commit, default);
        tree.TryGetBlob("a/1.json", out _).Should().BeFalse();
        tree.TryGetBlob("a/2.json", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ListRefs_filters_by_prefix()
    {
        var c = new InMemoryGitConnection();
        var commit = await CreateEmptyCommit(c, null);
        await c.UpdateRefAsync("refs/heads/db/one", null, commit, default);
        await c.UpdateRefAsync("refs/heads/db/two", null, commit, default);
        await c.UpdateRefAsync("refs/heads/main", null, commit, default);
        var dbs = await c.ListRefsAsync("refs/heads/db/", default);
        dbs.Should().BeEquivalentTo("refs/heads/db/one", "refs/heads/db/two");
    }

    private static async Task<string> CreateEmptyCommit(IGitConnection c, string? parent)
    {
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), default);
        return await c.CreateCommitAsync(new CommitSpec(
            tree, parent, "t", "t@x", DateTimeOffset.UnixEpoch, "m"), default);
    }
}
```

- [ ] **Step 2: Run tests, verify compile failure**

Run: `dotnet test --filter "InMemoryGitConnectionTests"`
Expected: compile error.

- [ ] **Step 3: Implement `InMemoryTreeView.cs`**

```csharp
using GitDocumentDb.Transport;

namespace GitDocumentDb.Transport.InMemory;

internal sealed class InMemoryTreeView : ITreeView
{
    private readonly Dictionary<string, (string sha, TreeEntryKind kind)> _entries;

    public InMemoryTreeView(string commitSha, Dictionary<string, (string, TreeEntryKind)> entries)
    {
        CommitSha = commitSha;
        _entries = entries;
    }

    public string CommitSha { get; }

    public bool TryGetBlob(string path, out string blobSha)
    {
        if (_entries.TryGetValue(path, out var e) && e.kind == TreeEntryKind.Blob)
        {
            blobSha = e.sha;
            return true;
        }
        blobSha = "";
        return false;
    }

    public bool TryGetTree(string path, out ITreeView subtree)
    {
        // In this fake, a "subtree" is any prefix with children. We synthesize a view.
        var prefix = path.Length == 0 ? "" : path + "/";
        var filtered = new Dictionary<string, (string, TreeEntryKind)>();
        foreach (var (k, v) in _entries)
            if (k.StartsWith(prefix, StringComparison.Ordinal))
                filtered[k[prefix.Length..]] = v;
        if (filtered.Count == 0)
        {
            subtree = null!;
            return false;
        }
        subtree = new InMemoryTreeView(CommitSha, filtered);
        return true;
    }

    public IEnumerable<TreeEntry> EnumerateChildren(string path)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        var direct = new Dictionary<string, (string sha, TreeEntryKind kind)>();
        foreach (var (key, value) in _entries)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = key[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                direct[rest] = value;
            }
            else
            {
                var dirName = rest[..slash];
                direct.TryAdd(dirName, ("", TreeEntryKind.Tree));
            }
        }
        return direct.Select(kv => new TreeEntry(kv.Key, kv.Value.sha, kv.Value.kind));
    }
}
```

- [ ] **Step 4: Implement `InMemoryGitConnection.cs`**

```csharp
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GitDocumentDb.Internal;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Transport.InMemory;

public sealed class InMemoryGitConnection : IGitConnection
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
    // treeSha -> (path -> (sha, kind))
    private readonly ConcurrentDictionary<string, Dictionary<string, (string sha, TreeEntryKind kind)>> _trees = new();
    // commitSha -> treeSha
    private readonly ConcurrentDictionary<string, string> _commits = new();
    // refName -> commitSha
    private readonly ConcurrentDictionary<string, string> _refs = new();

    public Task<string?> ResolveRefAsync(string refName, CancellationToken ct)
        => Task.FromResult(_refs.TryGetValue(refName, out var sha) ? sha : null);

    public Task<IReadOnlyList<string>> ListRefsAsync(string prefix, CancellationToken ct)
    {
        IReadOnlyList<string> list = _refs.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<ITreeView> GetTreeAsync(string commitSha, CancellationToken ct)
    {
        if (!_commits.TryGetValue(commitSha, out var treeSha))
            throw new InvalidOperationException($"Unknown commit {commitSha}");
        var entries = _trees.TryGetValue(treeSha, out var t) ? t : new();
        return Task.FromResult<ITreeView>(new InMemoryTreeView(commitSha, new(entries)));
    }

    public Task<ReadOnlyMemory<byte>> GetBlobAsync(string blobSha, CancellationToken ct)
    {
        if (!_blobs.TryGetValue(blobSha, out var bytes))
            throw new InvalidOperationException($"Unknown blob {blobSha}");
        return Task.FromResult<ReadOnlyMemory<byte>>(bytes);
    }

    public Task<string> WriteBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var bytes = content.ToArray();
        var sha = GitBlobHasher.Hash(bytes);
        _blobs.TryAdd(sha, bytes);
        return Task.FromResult(sha);
    }

    public Task<string> WriteTreeAsync(TreeBuildSpec spec, CancellationToken ct)
    {
        Dictionary<string, (string sha, TreeEntryKind kind)> entries =
            spec.BaseTreeSha is not null && _trees.TryGetValue(spec.BaseTreeSha, out var baseEntries)
                ? new(baseEntries)
                : new();

        foreach (var m in spec.Mutations)
        {
            if (m.Kind == TreeMutationKind.Upsert)
                entries[m.Path] = (m.BlobSha!, TreeEntryKind.Blob);
            else
                entries.Remove(m.Path);
        }

        var sha = HashTree(entries);
        _trees.TryAdd(sha, entries);
        return Task.FromResult(sha);
    }

    public Task<string> CreateCommitAsync(CommitSpec spec, CancellationToken ct)
    {
        // Commit SHA is a deterministic hash of its fields.
        var sb = new StringBuilder();
        sb.Append("tree ").Append(spec.TreeSha).Append('\n');
        if (spec.ParentSha is not null) sb.Append("parent ").Append(spec.ParentSha).Append('\n');
        sb.Append("author ").Append(spec.AuthorName).Append(' ')
          .Append('<').Append(spec.AuthorEmail).Append("> ")
          .Append(spec.AuthorDate.ToUnixTimeSeconds()).Append('\n');
        sb.Append('\n').Append(spec.Message);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(bytes);
        var commitSha = Convert.ToHexStringLower(hash);
        _commits.TryAdd(commitSha, spec.TreeSha);
        return Task.FromResult(commitSha);
    }

    public Task<PushResult> UpdateRefAsync(
        string refName, string? expectedOldSha, string newSha, CancellationToken ct)
    {
        while (true)
        {
            var currentExists = _refs.TryGetValue(refName, out var current);
            var matches = (expectedOldSha, currentExists) switch
            {
                (null, false) => true,
                (not null, true) => expectedOldSha == current,
                _ => false,
            };
            if (!matches)
                return Task.FromResult(new PushResult(false, null, PushRejectReason.NonFastForward));

            bool ok;
            if (currentExists)
                ok = _refs.TryUpdate(refName, newSha, current!);
            else
                ok = _refs.TryAdd(refName, newSha);
            if (ok)
                return Task.FromResult(new PushResult(true, newSha, null));
        }
    }

    public async Task<FetchResult> FetchAsync(string refName, CancellationToken ct)
    {
        // In memory: remote == local. "Fetch" is a no-op returning current state.
        var sha = await ResolveRefAsync(refName, ct);
        return new FetchResult(false, sha ?? "", sha ?? "", Array.Empty<string>());
    }

    private static string HashTree(Dictionary<string, (string sha, TreeEntryKind kind)> entries)
    {
        var sb = new StringBuilder();
        foreach (var kv in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(' ').Append(kv.Value.sha)
              .Append(' ').Append(kv.Value.kind).Append('\n');
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "InMemoryGitConnectionTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): in-memory IGitConnection implementation"
```

---

## Task 7: Record serializer

**Files:**
- Create: `src/GitDocumentDb.Core/Abstractions/IRecordSerializer.cs`
- Create: `src/GitDocumentDb.Core/Serialization/SystemTextJsonRecordSerializer.cs`
- Create: `tests/GitDocumentDb.Core.Tests/SerializerTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/GitDocumentDb.Core.Tests/SerializerTests.cs`:

```csharp
using System.Buffers;
using FluentAssertions;
using GitDocumentDb.Serialization;

namespace GitDocumentDb.Tests;

public class SerializerTests
{
    public sealed record TestRecord(string Id, int Count, string? Note);

    [Fact]
    public void Roundtrip_preserves_record()
    {
        var s = new SystemTextJsonRecordSerializer();
        var writer = new ArrayBufferWriter<byte>();
        s.Serialize(new TestRecord("x", 5, "n"), writer);
        var result = s.Deserialize<TestRecord>(writer.WrittenSpan);
        result.Should().Be(new TestRecord("x", 5, "n"));
    }

    [Fact]
    public void Extension_is_json()
    {
        new SystemTextJsonRecordSerializer().FileExtension.Should().Be(".json");
    }
}
```

- [ ] **Step 2: Run test, verify compile failure**

Run: `dotnet test --filter "SerializerTests"` — compile error expected.

- [ ] **Step 3: Implement `IRecordSerializer.cs`**

```csharp
using System.Buffers;

namespace GitDocumentDb;

public interface IRecordSerializer
{
    void Serialize<T>(T record, IBufferWriter<byte> output);
    T Deserialize<T>(ReadOnlySpan<byte> input);
    string FileExtension { get; }
}
```

- [ ] **Step 4: Implement `SystemTextJsonRecordSerializer.cs`**

```csharp
using System.Buffers;
using System.Text.Json;

namespace GitDocumentDb.Serialization;

public sealed class SystemTextJsonRecordSerializer : IRecordSerializer
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public string FileExtension => ".json";

    public void Serialize<T>(T record, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true });
        JsonSerializer.Serialize(writer, record, s_options);
    }

    public T Deserialize<T>(ReadOnlySpan<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        return JsonSerializer.Deserialize<T>(ref reader, s_options)
            ?? throw new InvalidOperationException("Record deserialized to null");
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "SerializerTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): pluggable record serializer with System.Text.Json default"
```

---

## Task 8: DatabaseSnapshot and TableSnapshot

Immutable representation of a database's state at a commit. No indexes yet (Phase 3); Phase 1 snapshots just hold the current commit SHA and the tree view.

**Files:**
- Create: `src/GitDocumentDb.Core/Internal/DatabaseSnapshot.cs`
- Create: `src/GitDocumentDb.Core/Internal/TableSnapshot.cs`
- Create: `src/GitDocumentDb.Core/Internal/SnapshotBuilder.cs`
- Create: `tests/GitDocumentDb.Core.Tests/SnapshotTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/GitDocumentDb.Core.Tests/SnapshotTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using GitDocumentDb.Internal;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class SnapshotTests
{
    [Fact]
    public async Task Snapshot_built_from_tree_enumerates_tables_and_records()
    {
        var c = new InMemoryGitConnection();
        var b = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("{}"), default);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, "tables/accounts/a.json", b),
            new TreeMutation(TreeMutationKind.Upsert, "tables/accounts/b.json", b),
            new TreeMutation(TreeMutationKind.Upsert, "tables/orders/c.json",  b),
        }), default);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), default);

        var snap = await SnapshotBuilder.BuildAsync(c, commit, default);

        snap.CommitSha.Should().Be(commit);
        snap.Tables.Keys.Should().BeEquivalentTo("accounts", "orders");
        snap.Tables["accounts"].Records.Keys.Should().BeEquivalentTo("a", "b");
        snap.Tables["orders"].Records.Keys.Should().BeEquivalentTo("c");
        snap.Tables["accounts"].Records["a"].Should().Be(b);
    }

    [Fact]
    public async Task Empty_tree_produces_empty_snapshot()
    {
        var c = new InMemoryGitConnection();
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), default);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), default);
        var snap = await SnapshotBuilder.BuildAsync(c, commit, default);
        snap.Tables.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run, verify compile failure**

Run: `dotnet test --filter "SnapshotTests"` — compile error expected.

- [ ] **Step 3: `TableSnapshot.cs`**

```csharp
using System.Collections.Frozen;

namespace GitDocumentDb.Internal;

internal sealed record TableSnapshot(
    string Name,
    FrozenDictionary<string, string> Records);   // record id (no extension) -> blob sha
```

- [ ] **Step 4: `DatabaseSnapshot.cs`**

```csharp
using System.Collections.Frozen;

namespace GitDocumentDb.Internal;

internal sealed record DatabaseSnapshot(
    string CommitSha,
    DateTimeOffset FetchedAt,
    FrozenDictionary<string, TableSnapshot> Tables);
```

- [ ] **Step 5: `SnapshotBuilder.cs`**

```csharp
using System.Collections.Frozen;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Internal;

internal static class SnapshotBuilder
{
    public static async Task<DatabaseSnapshot> BuildAsync(
        IGitConnection connection,
        string commitSha,
        CancellationToken ct)
    {
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
            tables[tableEntry.Name] = new TableSnapshot(
                tableEntry.Name,
                records.ToFrozenDictionary(StringComparer.Ordinal));
        }

        return new DatabaseSnapshot(
            commitSha,
            DateTimeOffset.UtcNow,
            tables.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? fileName : fileName[..dot];
    }
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test --filter "SnapshotTests"`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): immutable database and table snapshots"
```

---

## Task 9: IDocumentDatabase / IDatabase / ITable interfaces

**Files:**
- Create: `src/GitDocumentDb.Core/Abstractions/IDocumentDatabase.cs`
- Create: `src/GitDocumentDb.Core/Abstractions/IDatabase.cs`
- Create: `src/GitDocumentDb.Core/Abstractions/ITable.cs`

No tests; these are interface definitions consumed by later tasks.

- [ ] **Step 1: `IDocumentDatabase.cs`**

```csharp
namespace GitDocumentDb;

public interface IDocumentDatabase
{
    IDatabase GetDatabase(string name);
    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: `IDatabase.cs`**

```csharp
namespace GitDocumentDb;

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
```

- [ ] **Step 3: `ITable.cs`**

```csharp
namespace GitDocumentDb;

public interface ITable<T> where T : class
{
    ValueTask<Versioned<T>?> GetAsync(string id, ReadOptions? options = null, CancellationToken ct = default);

    Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default);
    Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default);

    Task<BatchResult> CommitAsync(IEnumerable<WriteOperation<T>> operations, WriteOptions? options = null, CancellationToken ct = default);
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`. Expected: success.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): public database and table interfaces"
```

---

## Task 10: DocumentDatabase implementation (root factory) and branch naming

**Files:**
- Create: `src/GitDocumentDb.Core/Internal/BranchNaming.cs`
- Create: `src/GitDocumentDb.Core/Implementation/DocumentDatabase.cs`
- Create: `tests/GitDocumentDb.Core.Tests/DocumentDatabaseTests.cs`

- [ ] **Step 1: Write failing test**

Create `tests/GitDocumentDb.Core.Tests/DocumentDatabaseTests.cs`:

```csharp
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class DocumentDatabaseTests
{
    private static DocumentDatabase Create(IGitConnection c) =>
        new(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());

    [Fact]
    public async Task ListDatabases_returns_empty_when_no_db_branches()
    {
        var c = new InMemoryGitConnection();
        var db = Create(c);
        (await db.ListDatabasesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ListDatabases_returns_branch_names_stripped_of_prefix()
    {
        var c = new InMemoryGitConnection();
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), default);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), default);
        await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, default);
        await c.UpdateRefAsync("refs/heads/db/beta", null, commit, default);

        var db = Create(c);
        (await db.ListDatabasesAsync()).Should().BeEquivalentTo("alpha", "beta");
    }

    [Fact]
    public void GetDatabase_rejects_invalid_name()
    {
        var c = new InMemoryGitConnection();
        var db = Create(c);
        var act = () => db.GetDatabase("bad.name");
        act.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run, verify compile failure**

Run: `dotnet test --filter "DocumentDatabaseTests"` — compile error expected.

- [ ] **Step 3: Implement `BranchNaming.cs`**

```csharp
namespace GitDocumentDb.Internal;

internal static class BranchNaming
{
    public const string DatabaseRefPrefix = "refs/heads/db/";
    public static string RefFor(string databaseName) => DatabaseRefPrefix + databaseName;
    public static string NameFrom(string refName) =>
        refName.StartsWith(DatabaseRefPrefix, StringComparison.Ordinal)
            ? refName[DatabaseRefPrefix.Length..]
            : refName;
}
```

- [ ] **Step 4: Implement `DocumentDatabase.cs` (partial — `GetDatabase` throws `NotImplementedException` until Task 11)**

```csharp
using GitDocumentDb.Internal;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Implementation;

public sealed class DocumentDatabase : IDocumentDatabase
{
    private readonly IGitConnection _connection;
    private readonly IRecordSerializer _serializer;
    private readonly DatabaseOptions _options;
    private readonly Dictionary<string, Database> _databases = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public DocumentDatabase(
        IGitConnection connection,
        IRecordSerializer serializer,
        DatabaseOptions options)
    {
        _connection = connection;
        _serializer = serializer;
        _options = options;
    }

    public IDatabase GetDatabase(string name)
    {
        NameValidator.ThrowIfInvalid(name, nameof(name));
        lock (_sync)
        {
            if (!_databases.TryGetValue(name, out var db))
            {
                db = new Database(name, _connection, _serializer, _options);
                _databases[name] = db;
            }
            return db;
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var refs = await _connection.ListRefsAsync(BranchNaming.DatabaseRefPrefix, ct);
        return refs.Select(BranchNaming.NameFrom).ToList();
    }
}
```

- [ ] **Step 5: Create `Database.cs` stub so it compiles**

Create `src/GitDocumentDb.Core/Implementation/Database.cs` with a minimal stub:

```csharp
using GitDocumentDb.Transport;

namespace GitDocumentDb.Implementation;

public sealed class Database : IDatabase
{
    internal Database(string name, IGitConnection connection, IRecordSerializer serializer, DatabaseOptions options)
    {
        Name = name;
        _ = connection; _ = serializer; _ = options;
    }

    public string Name { get; }
    public string CurrentCommit => throw new NotImplementedException();
    public DateTimeOffset LastFetchedAt => throw new NotImplementedException();
    public ITable<T> GetTable<T>(string name) where T : class => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FetchResult> FetchAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<ChangeNotification> WatchAsync(CancellationToken ct = default) => throw new NotImplementedException();
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test --filter "DocumentDatabaseTests"`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): DocumentDatabase root factory and branch naming"
```

---

## Task 11: Database open flow, snapshot loading, lock-free reads

The `Database` class holds an `AtomicReference<DatabaseSnapshot>` (we use `Volatile.Read` / `Interlocked.Exchange` on a field). On first use it loads the snapshot from the remote branch; if the branch doesn't exist, it starts with an empty snapshot.

**Files:**
- Modify: `src/GitDocumentDb.Core/Implementation/Database.cs`
- Create: `tests/GitDocumentDb.Core.Tests/DatabaseOpenTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/GitDocumentDb.Core.Tests/DatabaseOpenTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class DatabaseOpenTests
{
    private static DocumentDatabase Create(IGitConnection c) =>
        new(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());

    [Fact]
    public async Task Database_for_nonexistent_branch_opens_with_empty_state()
    {
        var c = new InMemoryGitConnection();
        var doc = Create(c);
        var db = doc.GetDatabase("alpha");
        (await db.ListTablesAsync()).Should().BeEmpty();
        db.CurrentCommit.Should().Be("");
    }

    [Fact]
    public async Task Database_loads_state_from_existing_branch()
    {
        var c = new InMemoryGitConnection();
        var b = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("{}"), default);
        var t = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, "tables/accounts/a.json", b),
        }), default);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            t, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), default);
        await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, default);

        var doc = Create(c);
        var db = doc.GetDatabase("alpha");
        (await db.ListTablesAsync()).Should().BeEquivalentTo("accounts");
        db.CurrentCommit.Should().Be(commit);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test --filter "DatabaseOpenTests"` — expected to fail (stub throws `NotImplementedException`).

- [ ] **Step 3: Replace `Database.cs` with the real implementation**

```csharp
using System.Collections.Frozen;
using GitDocumentDb.Internal;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Implementation;

public sealed class Database : IDatabase
{
    private readonly IGitConnection _connection;
    private readonly IRecordSerializer _serializer;
    private readonly DatabaseOptions _options;
    private readonly string _refName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private DatabaseSnapshot _snapshot;

    internal Database(string name, IGitConnection connection, IRecordSerializer serializer, DatabaseOptions options)
    {
        Name = name;
        _connection = connection;
        _serializer = serializer;
        _options = options;
        _refName = BranchNaming.RefFor(name);
        _snapshot = EmptySnapshot();
    }

    public string Name { get; }
    public string CurrentCommit => Volatile.Read(ref _snapshot).CommitSha;
    public DateTimeOffset LastFetchedAt => Volatile.Read(ref _snapshot).FetchedAt;

    internal IGitConnection Connection => _connection;
    internal IRecordSerializer Serializer => _serializer;
    internal DatabaseOptions Options => _options;
    internal string RefName => _refName;
    internal SemaphoreSlim WriteLock => _writeLock;

    internal DatabaseSnapshot CurrentSnapshot => Volatile.Read(ref _snapshot);

    internal void SwapSnapshot(DatabaseSnapshot snapshot) =>
        Interlocked.Exchange(ref _snapshot, snapshot);

    public ITable<T> GetTable<T>(string name) where T : class
    {
        NameValidator.ThrowIfInvalid(name, nameof(name));
        return new Table<T>(this, name);
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken ct = default)
    {
        await EnsureOpenedAsync(ct);
        var snap = CurrentSnapshot;
        return snap.Tables.Keys.ToList();
    }

    public async Task<FetchResult> FetchAsync(CancellationToken ct = default)
    {
        var previous = CurrentSnapshot.CommitSha;
        var remoteSha = await _connection.ResolveRefAsync(_refName, ct);
        if (string.IsNullOrEmpty(remoteSha) || remoteSha == previous)
            return new FetchResult(false, previous, previous, Array.Empty<string>());

        var newSnap = await SnapshotBuilder.BuildAsync(_connection, remoteSha, ct);
        SwapSnapshot(newSnap);
        return new FetchResult(true, previous, remoteSha, Array.Empty<string>());
    }

    public IAsyncEnumerable<ChangeNotification> WatchAsync(CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 16");

    internal async Task EnsureOpenedAsync(CancellationToken ct)
    {
        if (CurrentSnapshot.CommitSha.Length != 0) return;
        var remoteSha = await _connection.ResolveRefAsync(_refName, ct);
        if (string.IsNullOrEmpty(remoteSha)) return;
        var snap = await SnapshotBuilder.BuildAsync(_connection, remoteSha, ct);
        SwapSnapshot(snap);
    }

    private static DatabaseSnapshot EmptySnapshot() =>
        new("", DateTimeOffset.MinValue, FrozenDictionary<string, TableSnapshot>.Empty);
}
```

- [ ] **Step 4: Stub `Table<T>` so compile succeeds**

Create `src/GitDocumentDb.Core/Implementation/Table.cs`:

```csharp
namespace GitDocumentDb.Implementation;

internal sealed class Table<T> : ITable<T> where T : class
{
    internal Table(Database database, string name) { _ = database; _ = name; }

    public ValueTask<Versioned<T>?> GetAsync(string id, ReadOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<BatchResult> CommitAsync(IEnumerable<WriteOperation<T>> operations, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException();
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "DatabaseOpenTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): Database open flow with lock-free snapshot reads"
```

---

## Task 12: Table reads (GetAsync from snapshot)

**Files:**
- Modify: `src/GitDocumentDb.Core/Implementation/Table.cs`
- Create: `tests/GitDocumentDb.Core.Tests/TableReadTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/GitDocumentDb.Core.Tests/TableReadTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class TableReadTests
{
    public sealed record Account(string Id, string Email, int Version);

    [Fact]
    public async Task Get_returns_null_when_record_absent()
    {
        var c = new InMemoryGitConnection();
        var db = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions())
            .GetDatabase("alpha");
        var table = db.GetTable<Account>("accounts");
        var result = await table.GetAsync("missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_deserialized_record_and_version()
    {
        var c = new InMemoryGitConnection();
        // seed one record via the transport directly
        var json = "{\"id\":\"a\",\"email\":\"x@y\",\"version\":1}"u8;
        var blob = await c.WriteBlobAsync(json.ToArray(), default);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, "tables/accounts/a.json", blob),
        }), default);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), default);
        await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, default);

        var db = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions())
            .GetDatabase("alpha");
        var table = db.GetTable<Account>("accounts");

        var result = await table.GetAsync("a");
        result.Should().NotBeNull();
        result!.Record.Should().Be(new Account("a", "x@y", 1));
        result.Id.Should().Be("a");
        result.Version.Should().Be(blob);
        result.CommitSha.Should().Be(commit);
    }

    [Fact]
    public async Task Get_rejects_invalid_ids()
    {
        var c = new InMemoryGitConnection();
        var db = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions())
            .GetDatabase("alpha");
        var table = db.GetTable<Account>("accounts");
        var act = async () => await table.GetAsync("bad/id");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test --filter "TableReadTests"` — expected to fail.

- [ ] **Step 3: Replace `Table.cs` with reads implemented**

```csharp
using GitDocumentDb.Internal;

namespace GitDocumentDb.Implementation;

internal sealed class Table<T> : ITable<T> where T : class
{
    private readonly Database _db;
    private readonly string _name;

    internal Table(Database db, string name)
    {
        _db = db;
        _name = name;
    }

    public async ValueTask<Versioned<T>?> GetAsync(string id, ReadOptions? options = null, CancellationToken ct = default)
    {
        RecordIdValidator.ThrowIfInvalid(id, nameof(id));

        await MaybeFetchAsync(options, ct);
        await _db.EnsureOpenedAsync(ct);

        var snap = _db.CurrentSnapshot;
        if (!snap.Tables.TryGetValue(_name, out var table)) return null;
        if (!table.Records.TryGetValue(id, out var blobSha)) return null;

        var bytes = await _db.Connection.GetBlobAsync(blobSha, ct);
        var record = _db.Serializer.Deserialize<T>(bytes.Span);
        return new Versioned<T>(record, id, blobSha, snap.CommitSha);
    }

    public Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 13");
    public Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 13");
    public Task<BatchResult> CommitAsync(IEnumerable<WriteOperation<T>> operations, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 14");

    private async ValueTask MaybeFetchAsync(ReadOptions? options, CancellationToken ct)
    {
        if (options is null) return;
        if (options.FetchFirst ||
            (options.MaxStaleness.HasValue &&
             DateTimeOffset.UtcNow - _db.LastFetchedAt > options.MaxStaleness.Value))
        {
            await _db.FetchAsync(ct);
        }
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "TableReadTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): Table.GetAsync lock-free reads from snapshot"
```

---

## Task 13: Single-record writes (PutAsync + DeleteAsync, LastWriteWins)

Writes serialize through `Database.WriteLock`. The write flow:
1. Wait on the write lock.
2. Snapshot current state.
3. Serialize the record; enforce size limits.
4. Write the new blob.
5. Build a new tree with the mutation.
6. Create a commit.
7. `UpdateRefAsync` with the current snapshot's commit as `expectedOldSha`.
8. On success, swap the snapshot to reflect the new state; return.
9. On `NonFastForward`, fetch, re-acquire the current snapshot, and retry within `MaxRetries`.
10. On exhaustion, return `WriteResult { Success = false, FailureReason = PushRejected }`.

**Files:**
- Create: `src/GitDocumentDb.Core/Exceptions/GitDocumentDbException.cs`
- Create: `src/GitDocumentDb.Core/Exceptions/PushRejectedException.cs`
- Create: `src/GitDocumentDb.Core/Exceptions/TransportException.cs`
- Create: `src/GitDocumentDb.Core/Internal/WriteExecutor.cs`
- Modify: `src/GitDocumentDb.Core/Implementation/Table.cs`
- Create: `tests/GitDocumentDb.Core.Tests/TableWriteTests.cs`

- [ ] **Step 1: Exception classes**

`GitDocumentDbException.cs`:
```csharp
namespace GitDocumentDb;
public class GitDocumentDbException : Exception
{
    public GitDocumentDbException(string message) : base(message) { }
    public GitDocumentDbException(string message, Exception inner) : base(message, inner) { }
}
```

`PushRejectedException.cs`:
```csharp
namespace GitDocumentDb;
public sealed class PushRejectedException : GitDocumentDbException
{
    public PushRejectedException(string message) : base(message) { }
}
```

`TransportException.cs`:
```csharp
namespace GitDocumentDb;
public sealed class TransportException : GitDocumentDbException
{
    public TransportException(string message) : base(message) { }
    public TransportException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] **Step 2: Write failing write tests**

Create `tests/GitDocumentDb.Core.Tests/TableWriteTests.cs`:

```csharp
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class TableWriteTests
{
    public sealed record Account(string Id, string Email, int Version);

    private static (DocumentDatabase doc, IDatabase db, ITable<Account> t) NewTable()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var db = doc.GetDatabase("alpha");
        return (doc, db, db.GetTable<Account>("accounts"));
    }

    [Fact]
    public async Task Put_into_empty_database_creates_record_and_advances_commit()
    {
        var (_, db, t) = NewTable();
        var result = await t.PutAsync("a", new Account("a", "x@y", 1));
        result.Success.Should().BeTrue();
        result.NewVersion.Should().NotBeNullOrEmpty();
        result.NewCommitSha.Should().NotBeNullOrEmpty();

        db.CurrentCommit.Should().Be(result.NewCommitSha);

        var read = await t.GetAsync("a");
        read.Should().NotBeNull();
        read!.Record.Should().Be(new Account("a", "x@y", 1));
        read.Version.Should().Be(result.NewVersion);
    }

    [Fact]
    public async Task Put_twice_updates_the_record()
    {
        var (_, _, t) = NewTable();
        await t.PutAsync("a", new Account("a", "x@y", 1));
        var r = await t.PutAsync("a", new Account("a", "x@y", 2));
        r.Success.Should().BeTrue();
        var read = await t.GetAsync("a");
        read!.Record.Version.Should().Be(2);
    }

    [Fact]
    public async Task Delete_removes_the_record()
    {
        var (_, _, t) = NewTable();
        await t.PutAsync("a", new Account("a", "x@y", 1));
        var r = await t.DeleteAsync("a");
        r.Success.Should().BeTrue();
        (await t.GetAsync("a")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_of_missing_record_under_last_write_wins_succeeds_noop()
    {
        var (_, _, t) = NewTable();
        var r = await t.DeleteAsync("missing");
        r.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Record_over_hard_size_limit_fails()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(),
            new DatabaseOptions { RecordSizeHardLimitBytes = 50 });
        var table = doc.GetDatabase("alpha").GetTable<Account>("accounts");
        var big = new Account("a", new string('x', 1000), 1);
        var r = await table.PutAsync("a", big);
        r.Success.Should().BeFalse();
        r.FailureReason.Should().Be(WriteFailureReason.RecordTooLarge);
    }
}
```

- [ ] **Step 3: Run, verify failure**

Run: `dotnet test --filter "TableWriteTests"` — expected failure (not implemented).

- [ ] **Step 4: Implement `WriteExecutor.cs`**

```csharp
using System.Buffers;
using System.Collections.Frozen;
using GitDocumentDb.Implementation;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Internal;

internal static class WriteExecutor
{
    public sealed record PreparedOperation(
        string TableName,
        string Id,
        string Path,
        WriteOpKind Kind,
        byte[]? Content,
        string? BlobSha);

    public static async Task<WriteResult> ExecuteSingleAsync(
        Database db,
        string tableName,
        PreparedOperation op,
        WriteOptions? options,
        CancellationToken ct)
    {
        options ??= new WriteOptions();
        await db.WriteLock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
            {
                await db.EnsureOpenedAsync(ct);
                var snap = db.CurrentSnapshot;
                var result = await TryCommitAsync(db, snap, new[] { op }, options, ct);
                if (result.success)
                {
                    var newVersion = op.Kind == WriteOpKind.Put ? op.BlobSha : null;
                    return new WriteResult(true, newVersion, result.newCommitSha, null, null);
                }
                // Non-fast-forward: fetch and retry.
                await db.FetchAsync(ct);
                await Task.Delay(ComputeBackoff(options, attempt), ct);
            }
            return new WriteResult(false, null, null, null, WriteFailureReason.PushRejected);
        }
        finally
        {
            db.WriteLock.Release();
        }
    }

    public static async Task<BatchResult> ExecuteBatchAsync(
        Database db,
        IReadOnlyList<PreparedOperation> operations,
        WriteOptions? options,
        CancellationToken ct)
    {
        options ??= new WriteOptions();
        await db.WriteLock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
            {
                await db.EnsureOpenedAsync(ct);
                var snap = db.CurrentSnapshot;
                var result = await TryCommitAsync(db, snap, operations, options, ct);
                if (result.success)
                {
                    var ops = operations.Select(o =>
                        new OperationResult(o.Id, true,
                            o.Kind == WriteOpKind.Put ? o.BlobSha : null,
                            null, null)).ToList();
                    return new BatchResult(true, result.newCommitSha, ops);
                }
                await db.FetchAsync(ct);
                await Task.Delay(ComputeBackoff(options, attempt), ct);
            }
            var failed = operations.Select(o =>
                new OperationResult(o.Id, false, null, null, WriteFailureReason.PushRejected)).ToList();
            return new BatchResult(false, null, failed);
        }
        finally
        {
            db.WriteLock.Release();
        }
    }

    private static async Task<(bool success, string? newCommitSha)> TryCommitAsync(
        Database db,
        DatabaseSnapshot snap,
        IReadOnlyList<PreparedOperation> operations,
        WriteOptions options,
        CancellationToken ct)
    {
        var treeMutations = new List<TreeMutation>(operations.Count);
        foreach (var op in operations)
        {
            if (op.Kind == WriteOpKind.Put)
            {
                treeMutations.Add(new TreeMutation(TreeMutationKind.Upsert, op.Path, op.BlobSha!));
            }
            else
            {
                // Delete is a no-op if the file isn't present.
                var tablePath = $"tables/{op.TableName}";
                if (snap.Tables.TryGetValue(op.TableName, out var table) &&
                    table.Records.ContainsKey(op.Id))
                {
                    treeMutations.Add(new TreeMutation(TreeMutationKind.Delete, op.Path, null));
                }
            }
        }

        if (treeMutations.Count == 0)
        {
            // No-op (e.g., delete of absent). Treat as success without a commit.
            return (true, snap.CommitSha);
        }

        var baseTree = await GetBaseTreeShaAsync(db, snap, ct);
        var newTree = await db.Connection.WriteTreeAsync(
            new TreeBuildSpec(baseTree, treeMutations), ct);

        var newCommit = await db.Connection.CreateCommitAsync(
            new CommitSpec(
                newTree,
                snap.CommitSha.Length == 0 ? null : snap.CommitSha,
                options.Author ?? db.Options.DefaultAuthorName,
                db.Options.DefaultAuthorEmail,
                DateTimeOffset.UtcNow,
                options.CommitMessage ?? "gitdb write"),
            ct);

        var expectedOld = snap.CommitSha.Length == 0 ? null : snap.CommitSha;
        var push = await db.Connection.UpdateRefAsync(db.RefName, expectedOld, newCommit, ct);

        if (!push.Success)
            return (false, null);

        // Swap snapshot with the new commit's state.
        var newSnap = await SnapshotBuilder.BuildAsync(db.Connection, newCommit, ct);
        db.SwapSnapshot(newSnap);
        return (true, newCommit);
    }

    private static async Task<string?> GetBaseTreeShaAsync(Database db, DatabaseSnapshot snap, CancellationToken ct)
    {
        if (snap.CommitSha.Length == 0) return null;
        // The in-memory transport only exposes commit -> tree via GetTreeAsync; we don't
        // need the raw tree sha for WriteTreeAsync in the fake (it accepts null and
        // rebuilds from scratch). Real transports will need the tree sha; we'll add a
        // GetCommitTreeShaAsync method in Phase 4. For now, pass null and rely on the
        // write containing all relevant entries.
        _ = db; _ = ct;
        return null;
    }

    private static TimeSpan ComputeBackoff(WriteOptions options, int attempt)
    {
        var baseMs = options.RetryBackoff.TotalMilliseconds * Math.Pow(2, attempt);
        var cappedMs = Math.Min(baseMs, options.MaxRetryBackoff.TotalMilliseconds);
        var jitter = 0.5 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromMilliseconds(cappedMs * jitter);
    }
}
```

Note: the base-tree handling above is intentionally simplified for Phase 1. `InMemoryGitConnection.WriteTreeAsync` handles `BaseTreeSha: null` by starting from an empty tree, so we must pass all entries on each write. Fix this for Phase 1 by including all existing records as `Upsert` mutations — see step 5.

- [ ] **Step 5: Fix base-tree handling — include existing records on every write**

The simplified handling above produces incorrect results (a write would erase other records). Replace `TryCommitAsync` in `WriteExecutor.cs` so it builds the full new tree from the snapshot:

```csharp
private static async Task<(bool success, string? newCommitSha)> TryCommitAsync(
    Database db,
    DatabaseSnapshot snap,
    IReadOnlyList<PreparedOperation> operations,
    WriteOptions options,
    CancellationToken ct)
{
    // Start with every existing record in the snapshot, then apply the mutations on top.
    var desiredEntries = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (tableName, table) in snap.Tables)
        foreach (var (id, blobSha) in table.Records)
            desiredEntries[$"tables/{tableName}/{id}.json"] = blobSha;

    var anyChange = false;
    foreach (var op in operations)
    {
        if (op.Kind == WriteOpKind.Put)
        {
            if (!desiredEntries.TryGetValue(op.Path, out var existing) || existing != op.BlobSha)
            {
                desiredEntries[op.Path] = op.BlobSha!;
                anyChange = true;
            }
        }
        else
        {
            if (desiredEntries.Remove(op.Path)) anyChange = true;
        }
    }

    if (!anyChange)
        return (true, snap.CommitSha);

    var mutations = desiredEntries
        .Select(kv => new TreeMutation(TreeMutationKind.Upsert, kv.Key, kv.Value))
        .ToList();

    var newTree = await db.Connection.WriteTreeAsync(new TreeBuildSpec(null, mutations), ct);
    var newCommit = await db.Connection.CreateCommitAsync(
        new CommitSpec(
            newTree,
            snap.CommitSha.Length == 0 ? null : snap.CommitSha,
            options.Author ?? db.Options.DefaultAuthorName,
            db.Options.DefaultAuthorEmail,
            DateTimeOffset.UtcNow,
            options.CommitMessage ?? "gitdb write"),
        ct);

    var expectedOld = snap.CommitSha.Length == 0 ? null : snap.CommitSha;
    var push = await db.Connection.UpdateRefAsync(db.RefName, expectedOld, newCommit, ct);
    if (!push.Success) return (false, null);

    var newSnap = await SnapshotBuilder.BuildAsync(db.Connection, newCommit, ct);
    db.SwapSnapshot(newSnap);
    return (true, newCommit);
}
```

Remove the now-unused `GetBaseTreeShaAsync`. Phase 4's real LibGit2Sharp transport will use proper base-tree diffing.

- [ ] **Step 6: Wire `PutAsync` / `DeleteAsync` in `Table.cs`**

Replace the `NotImplementedException` stubs in `Table.cs` with:

```csharp
public async Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default)
{
    RecordIdValidator.ThrowIfInvalid(id, nameof(id));
    ArgumentNullException.ThrowIfNull(record);

    var writer = new ArrayBufferWriter<byte>();
    _db.Serializer.Serialize(record, writer);
    var bytes = writer.WrittenMemory;

    if (bytes.Length > _db.Options.RecordSizeHardLimitBytes)
        return new WriteResult(false, null, null, null, WriteFailureReason.RecordTooLarge);

    var blobSha = await _db.Connection.WriteBlobAsync(bytes, ct);
    var path = $"tables/{_name}/{id}{_db.Serializer.FileExtension}";
    var op = new WriteExecutor.PreparedOperation(_name, id, path, WriteOpKind.Put, null, blobSha);
    return await WriteExecutor.ExecuteSingleAsync(_db, _name, op, options, ct);
}

public async Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default)
{
    RecordIdValidator.ThrowIfInvalid(id, nameof(id));
    var path = $"tables/{_name}/{id}{_db.Serializer.FileExtension}";
    var op = new WriteExecutor.PreparedOperation(_name, id, path, WriteOpKind.Delete, null, null);
    return await WriteExecutor.ExecuteSingleAsync(_db, _name, op, options, ct);
}
```

Add `using System.Buffers;` at the top of the file.

- [ ] **Step 7: Run tests**

Run: `dotnet test --filter "TableWriteTests"`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): PutAsync and DeleteAsync under LastWriteWins"
```

---

## Task 14: Batch writes (CommitAsync)

**Files:**
- Modify: `src/GitDocumentDb.Core/Implementation/Table.cs`
- Create: `tests/GitDocumentDb.Core.Tests/TableBatchTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/GitDocumentDb.Core.Tests/TableBatchTests.cs`:

```csharp
using System.Buffers;
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class TableBatchTests
{
    public sealed record Account(string Id, string Email);

    private static ITable<Account> NewTable()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<Account>("accounts");
    }

    [Fact]
    public async Task Batch_of_puts_produces_one_commit()
    {
        var t = NewTable();
        var ops = new[]
        {
            new WriteOperation<Account>(WriteOpKind.Put, "a", new Account("a", "x@y"), null),
            new WriteOperation<Account>(WriteOpKind.Put, "b", new Account("b", "y@z"), null),
            new WriteOperation<Account>(WriteOpKind.Put, "c", new Account("c", "z@w"), null),
        };
        var r = await t.CommitAsync(ops);
        r.Success.Should().BeTrue();
        r.Operations.Should().HaveCount(3).And.OnlyContain(o => o.Success);

        (await t.GetAsync("a")).Should().NotBeNull();
        (await t.GetAsync("b")).Should().NotBeNull();
        (await t.GetAsync("c")).Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_with_put_and_delete()
    {
        var t = NewTable();
        await t.PutAsync("a", new Account("a", "x@y"));

        var ops = new[]
        {
            new WriteOperation<Account>(WriteOpKind.Delete, "a", null, null),
            new WriteOperation<Account>(WriteOpKind.Put, "b", new Account("b", "y@z"), null),
        };
        var r = await t.CommitAsync(ops);
        r.Success.Should().BeTrue();
        (await t.GetAsync("a")).Should().BeNull();
        (await t.GetAsync("b")).Should().NotBeNull();
    }

    [Fact]
    public async Task Oversized_record_fails_batch_pre_push()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(),
            new DatabaseOptions { RecordSizeHardLimitBytes = 50 });
        var t = doc.GetDatabase("alpha").GetTable<Account>("accounts");
        var ops = new[]
        {
            new WriteOperation<Account>(WriteOpKind.Put, "a", new Account("a", "x@y"), null),
            new WriteOperation<Account>(WriteOpKind.Put, "b", new Account("b", new string('x', 1000)), null),
        };
        var r = await t.CommitAsync(ops);
        r.Success.Should().BeFalse();
        r.Operations.Should().Contain(o => o.Id == "b" && o.FailureReason == WriteFailureReason.RecordTooLarge);
        (await t.GetAsync("a")).Should().BeNull("atomic batch must not partially commit");
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test --filter "TableBatchTests"` — expected failure.

- [ ] **Step 3: Implement `CommitAsync` in `Table.cs`**

Replace the `NotImplementedException` stub for `CommitAsync` with:

```csharp
public async Task<BatchResult> CommitAsync(
    IEnumerable<WriteOperation<T>> operations,
    WriteOptions? options = null,
    CancellationToken ct = default)
{
    var opList = operations.ToList();
    var prepared = new List<WriteExecutor.PreparedOperation>(opList.Count);
    var failures = new List<OperationResult>();

    foreach (var op in opList)
    {
        RecordIdValidator.ThrowIfInvalid(op.Id, "operation.Id");
        var path = $"tables/{_name}/{op.Id}{_db.Serializer.FileExtension}";
        if (op.Kind == WriteOpKind.Put)
        {
            if (op.Record is null)
            {
                failures.Add(new OperationResult(op.Id, false, null, null, WriteFailureReason.InvalidId));
                continue;
            }
            var writer = new ArrayBufferWriter<byte>();
            _db.Serializer.Serialize(op.Record, writer);
            var bytes = writer.WrittenMemory;
            if (bytes.Length > _db.Options.RecordSizeHardLimitBytes)
            {
                failures.Add(new OperationResult(op.Id, false, null, null, WriteFailureReason.RecordTooLarge));
                continue;
            }
            var blobSha = await _db.Connection.WriteBlobAsync(bytes, ct);
            prepared.Add(new WriteExecutor.PreparedOperation(_name, op.Id, path, WriteOpKind.Put, null, blobSha));
        }
        else
        {
            prepared.Add(new WriteExecutor.PreparedOperation(_name, op.Id, path, WriteOpKind.Delete, null, null));
        }
    }

    if (failures.Count > 0)
    {
        // Atomic: if any op fails pre-push, the whole batch fails and nothing is committed.
        var all = opList.Select(o =>
            failures.FirstOrDefault(f => f.Id == o.Id)
            ?? new OperationResult(o.Id, false, null, null, null)).ToList();
        return new BatchResult(false, null, all);
    }

    return await WriteExecutor.ExecuteBatchAsync(_db, prepared, options, ct);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "TableBatchTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): atomic batch writes via CommitAsync"
```

---

## Task 15: Multi-writer test (two Database instances, same backend)

Verifies the push-rejection retry loop works end-to-end: two Core instances racing against each other with `LastWriteWins` both succeed with retries.

**Files:**
- Create: `tests/GitDocumentDb.Core.Tests/MultiWriterTests.cs`

- [ ] **Step 1: Write the test**

Create `tests/GitDocumentDb.Core.Tests/MultiWriterTests.cs`:

```csharp
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class MultiWriterTests
{
    public sealed record Account(string Id, int Value);

    [Fact]
    public async Task Two_writers_interleaving_all_land_under_last_write_wins()
    {
        var backend = new InMemoryGitConnection();
        var doc1 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var doc2 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var t1 = doc1.GetDatabase("alpha").GetTable<Account>("accounts");
        var t2 = doc2.GetDatabase("alpha").GetTable<Account>("accounts");

        var writes1 = Enumerable.Range(0, 25)
            .Select(i => t1.PutAsync($"w1-{i}", new Account($"w1-{i}", i)))
            .ToArray();
        var writes2 = Enumerable.Range(0, 25)
            .Select(i => t2.PutAsync($"w2-{i}", new Account($"w2-{i}", i)))
            .ToArray();

        var allResults = await Task.WhenAll(writes1.Concat(writes2));
        allResults.Should().OnlyContain(r => r.Success);

        // Both writers can read all records after fetching.
        await doc1.GetDatabase("alpha").FetchAsync();
        await doc2.GetDatabase("alpha").FetchAsync();
        for (var i = 0; i < 25; i++)
        {
            (await t1.GetAsync($"w1-{i}")).Should().NotBeNull();
            (await t1.GetAsync($"w2-{i}")).Should().NotBeNull();
            (await t2.GetAsync($"w1-{i}")).Should().NotBeNull();
            (await t2.GetAsync($"w2-{i}")).Should().NotBeNull();
        }
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test --filter "MultiWriterTests"`
Expected: all pass. (The retry loop in `WriteExecutor` handles the non-fast-forward rejections that will occur when writers race.)

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test(core): multi-writer LastWriteWins race converges"
```

---

## Task 16: Change notifications (WatchAsync + background fetch loop)

`WatchAsync` yields a `ChangeNotification` whenever a fetch observes a new commit. The database runs a lazy background fetch loop (started on first `WatchAsync` or when `DatabaseOptions.EnableBackgroundFetch == true`). Uses `System.Threading.Channels` for per-subscriber backpressure.

**Files:**
- Modify: `src/GitDocumentDb.Core/Implementation/Database.cs`
- Create: `tests/GitDocumentDb.Core.Tests/ChangeNotificationTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/GitDocumentDb.Core.Tests/ChangeNotificationTests.cs`:

```csharp
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class ChangeNotificationTests
{
    public sealed record Account(string Id, int V);

    [Fact]
    public async Task Watch_yields_notification_when_fetch_observes_new_commit()
    {
        var backend = new InMemoryGitConnection();
        var doc1 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var doc2 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());

        var db1 = doc1.GetDatabase("alpha");
        var db2 = doc2.GetDatabase("alpha");
        var t2 = db2.GetTable<Account>("accounts");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<ChangeNotification>();
        var task = Task.Run(async () =>
        {
            await foreach (var n in db1.WatchAsync(cts.Token))
            {
                received.Add(n);
                if (received.Count == 1) break;
            }
        }, cts.Token);

        // Another writer makes a change.
        await t2.PutAsync("a", new Account("a", 1));

        // Explicit fetch on db1 to trigger the notification.
        await db1.FetchAsync();

        await task;
        received.Should().HaveCount(1);
        received[0].Reason.Should().Be(ChangeReason.RemoteAdvance);
        received[0].CommitSha.Should().Be(db1.CurrentCommit);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test --filter "ChangeNotificationTests"` — expected failure (WatchAsync throws).

- [ ] **Step 3: Modify `Database.cs` to implement `WatchAsync` and emit on fetch**

Add fields and replace `WatchAsync` / `FetchAsync` / `SwapSnapshot`:

```csharp
using System.Runtime.CompilerServices;
using System.Threading.Channels;

// ... inside Database:

private readonly List<Channel<ChangeNotification>> _subscribers = new();
private readonly object _subscriberLock = new();

public async IAsyncEnumerable<ChangeNotification> WatchAsync(
    [EnumeratorCancellation] CancellationToken ct = default)
{
    var channel = Channel.CreateUnbounded<ChangeNotification>(
        new UnboundedChannelOptions { SingleReader = true });
    lock (_subscriberLock) _subscribers.Add(channel);
    try
    {
        while (await channel.Reader.WaitToReadAsync(ct))
            while (channel.Reader.TryRead(out var n))
                yield return n;
    }
    finally
    {
        lock (_subscriberLock) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }
}

public async Task<FetchResult> FetchAsync(CancellationToken ct = default)
{
    var previous = CurrentSnapshot.CommitSha;
    var remoteSha = await _connection.ResolveRefAsync(_refName, ct);
    if (string.IsNullOrEmpty(remoteSha) || remoteSha == previous)
        return new FetchResult(false, previous, previous, Array.Empty<string>());

    var newSnap = await SnapshotBuilder.BuildAsync(_connection, remoteSha, ct);
    SwapSnapshot(newSnap);
    PublishNotification(new ChangeNotification(
        remoteSha, DateTimeOffset.UtcNow, Array.Empty<string>(), ChangeReason.RemoteAdvance));
    return new FetchResult(true, previous, remoteSha, Array.Empty<string>());
}

internal new void SwapSnapshot(DatabaseSnapshot snapshot) =>
    Interlocked.Exchange(ref _snapshot, snapshot);

private void PublishNotification(ChangeNotification notification)
{
    Channel<ChangeNotification>[] subs;
    lock (_subscriberLock) subs = _subscribers.ToArray();
    foreach (var s in subs) s.Writer.TryWrite(notification);
}
```

(Keep `SwapSnapshot` non-new; move the notification to a separate `PublishRemoteAdvance` helper called from `FetchAsync` only. Local writes swap snapshots without publishing — subscribers are for remote advances.)

Final version of the relevant `Database.cs` section:

```csharp
public async Task<FetchResult> FetchAsync(CancellationToken ct = default)
{
    var previous = CurrentSnapshot.CommitSha;
    var remoteSha = await _connection.ResolveRefAsync(_refName, ct);
    if (string.IsNullOrEmpty(remoteSha) || remoteSha == previous)
        return new FetchResult(false, previous, previous, Array.Empty<string>());

    var newSnap = await SnapshotBuilder.BuildAsync(_connection, remoteSha, ct);
    SwapSnapshot(newSnap);

    Channel<ChangeNotification>[] subs;
    lock (_subscriberLock) subs = _subscribers.ToArray();
    var notification = new ChangeNotification(
        remoteSha, DateTimeOffset.UtcNow, Array.Empty<string>(), ChangeReason.RemoteAdvance);
    foreach (var s in subs) s.Writer.TryWrite(notification);

    return new FetchResult(true, previous, remoteSha, Array.Empty<string>());
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "ChangeNotificationTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): WatchAsync change notifications via fetch events"
```

---

## Task 17: Full-suite test run and allocation budget benchmark

**Files:**
- Create: `tests/GitDocumentDb.Core.Tests/AllocationBenchmarks.cs`

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: all tests pass.

- [ ] **Step 2: Create allocation benchmark (manual run; not part of CI)**

Create `tests/GitDocumentDb.Core.Tests/AllocationBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

[MemoryDiagnoser]
public class AllocationBenchmarks
{
    public sealed record Account(string Id, string Email);

    private ITable<Account>? _table;

    [GlobalSetup]
    public async Task Setup()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var db = doc.GetDatabase("alpha");
        _table = db.GetTable<Account>("accounts");
        await _table.PutAsync("a", new Account("a", "x@y"));
    }

    [Benchmark]
    public async Task<Versioned<Account>?> GetAsync_hot() => await _table!.GetAsync("a");
}
```

This benchmark is not a pass/fail gate in Phase 1 — it's a baseline measurement. Add it to the package reference:

Ensure `tests/GitDocumentDb.Core.Tests/GitDocumentDb.Core.Tests.csproj` references BenchmarkDotNet:

```xml
<PackageReference Include="BenchmarkDotNet" />
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "test(core): allocation benchmarks for Get hot path"
```

---

## Task 18: README and summary

**Files:**
- Create: `README.md`

- [ ] **Step 1: Write the README**

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add -A
git commit -m "docs: phase 1 README"
```

---

## Self-Review

**Spec coverage for Phase 1 scope:**

- §3 branch layout / naming → Task 10 (`BranchNaming`)
- §4.2 public API surface (subset for Phase 1) → Tasks 2, 9, 11, 12, 13, 14
- §4.3 `LastWriteWins` mode → Tasks 13, 14
- §4.4 version tokens = blob SHAs → Task 12 (`Version = blobSha`)
- §4.5 transport-level conflict retries → Task 13 (`WriteExecutor`)
- §4.6 batches atomic → Task 14
- §4.9 read consistency (FetchFirst / MaxStaleness) → Task 12 (`MaybeFetchAsync`)
- §4.10 snapshot architecture → Tasks 8, 11
- §4.11 change notifications (WatchAsync) → Task 16
- §4.14 transport abstraction → Tasks 5, 6
- §4.15 serializer pluggability → Task 7
- §4.16 record size limits → Tasks 13, 14
- §4.18 thread safety → Task 11, 13 (write lock; lock-free reads)
- §4.19 memory-efficient hot paths → Tasks 7 (pooled JSON), 12 (`ValueTask`), 17 (baseline)

**Out of scope for Phase 1** (picked up in later phases):

- OptimisticReject / OptimisticMerge, three-way merger, force-push detection → Phase 2
- Indexing, queries, schema loading, snapshot persistence → Phase 3
- LibGit2Sharp / LocalBare transports, storage modes, clone strategies → Phase 4
- Orleans writer/reader grains, batching, streams → Phase 5

**Placeholder scan:** no "TBD", "TODO", "similar to", or "add appropriate X". Every step that changes code shows the code.

**Type consistency:** `Versioned<T>.Version` is the blob SHA across Task 12 and Task 13. `WriteOperation<T>.Kind` and `WriteOpKind` match between Task 2 and Task 14. `WriteResult.FailureReason` nullable matches across Tasks 2, 13, 14. `Database.SwapSnapshot` / `Database.CurrentSnapshot` / `Database.EnsureOpenedAsync` match their callers in `Table<T>` and `WriteExecutor`.

**Fixups in review:** Task 13's initial `GetBaseTreeShaAsync` draft was broken (would have erased other records); Step 5 corrects it by rebuilding the tree from the snapshot on each write. Real transports in Phase 4 will use proper base-tree diffing.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-23-phase-1-core-foundation.md`.

**Two execution options:**

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task with review between tasks. Fast iteration, clean context per task.
2. **Inline Execution** — Execute tasks in this session with checkpoints every few tasks for review.

Which approach?
