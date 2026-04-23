# GitDocumentDb Phase 4: Real Git Transports — Implementation Plan

**Goal:** Implement `IGitConnection` on top of LibGit2Sharp so databases can live on real Git repositories — either a local bare repo (for dev/integration tests) or a remote HTTPS/SSH server.

**Architecture:** A single `LibGit2SharpGitConnection` implementation manages a working clone directory. The "remote" can be any URI that libgit2 understands: a local bare repo (`file:///path/to/repo.git`), HTTPS, or SSH. Storage mode (on-disk only for v1; tmpfs is an OS concern and requires the clone path to point at a tmpfs mount) and clone strategy (full / shallow / blobless) are configuration. Credentials are supplied via an `IGitCredentialsProvider` injection.

**Tech Stack:** LibGit2Sharp 0.30+ (native libgit2). Tests use a temporary directory with an init-bare'd repo as the "remote."

---

## File Structure

```
src/GitDocumentDb.Transport.LibGit2Sharp/
├── GitDocumentDb.Transport.LibGit2Sharp.csproj
├── LibGit2SharpGitConnection.cs          # IGitConnection impl
├── LibGit2SharpTreeView.cs               # ITreeView impl
├── LibGit2SharpOptions.cs                # Remote URL, clone path, strategy, credentials
└── IGitCredentialsProvider.cs            # Interface for HTTPS/SSH auth

tests/GitDocumentDb.Transport.LibGit2Sharp.Tests/
├── GitDocumentDb.Transport.LibGit2Sharp.Tests.csproj
├── LocalBareTransportTests.cs            # Round-trips against a temp bare repo
└── IntegrationTests.cs                   # Full CRUD via DocumentDatabase
```

---

## Task 1: Transport project scaffolding

**Step 1:** Create `src/GitDocumentDb.Transport.LibGit2Sharp/GitDocumentDb.Transport.LibGit2Sharp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>GitDocumentDb.Transport.LibGit2Sharp</RootNamespace>
    <AssemblyName>GitDocumentDb.Transport.LibGit2Sharp</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591;NU1510</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="LibGit2Sharp" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\GitDocumentDb.Core\GitDocumentDb.Core.csproj" />
  </ItemGroup>
</Project>
```

**Step 2:** Add `LibGit2Sharp` to `Directory.Packages.props`:
```xml
<PackageVersion Include="LibGit2Sharp" Version="0.31.0" />
```

(If 0.31.0 isn't latest, use whatever is current stable.)

**Step 3:** Create the test project `tests/GitDocumentDb.Transport.LibGit2Sharp.Tests/GitDocumentDb.Transport.LibGit2Sharp.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <RootNamespace>GitDocumentDb.Transport.LibGit2Sharp.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\GitDocumentDb.Core\GitDocumentDb.Core.csproj" />
    <ProjectReference Include="..\..\src\GitDocumentDb.Transport.LibGit2Sharp\GitDocumentDb.Transport.LibGit2Sharp.csproj" />
  </ItemGroup>
</Project>
```

**Step 4:** Add both projects to `GitDocumentDb.slnx`:
```bash
dotnet sln add src/GitDocumentDb.Transport.LibGit2Sharp/GitDocumentDb.Transport.LibGit2Sharp.csproj
dotnet sln add tests/GitDocumentDb.Transport.LibGit2Sharp.Tests/GitDocumentDb.Transport.LibGit2Sharp.Tests.csproj
```

**Step 5:** Add a smoke test `tests/GitDocumentDb.Transport.LibGit2Sharp.Tests/SmokeTest.cs`:
```csharp
namespace GitDocumentDb.Transport.LibGit2Sharp.Tests;

public class SmokeTest
{
    [Fact]
    public void ProjectCompiles() => Assert.True(true);
}
```

**Step 6:** Build + test. Commit: `chore: scaffold LibGit2Sharp transport projects`.

## Task 2: Options + credentials abstraction

**Step 1:** `src/GitDocumentDb.Transport.LibGit2Sharp/IGitCredentialsProvider.cs`:
```csharp
namespace GitDocumentDb.Transport.LibGit2Sharp;

public interface IGitCredentialsProvider
{
    GitCredentials GetCredentials(string url);
}

public abstract record GitCredentials;

public sealed record UsernamePasswordCredentials(string Username, string Password) : GitCredentials;

public sealed record SshKeyCredentials(string Username, string PublicKey, string PrivateKey, string Passphrase) : GitCredentials;

public sealed record AnonymousCredentials : GitCredentials
{
    public static AnonymousCredentials Instance { get; } = new();
}
```

**Step 2:** `src/GitDocumentDb.Transport.LibGit2Sharp/LibGit2SharpOptions.cs`:
```csharp
namespace GitDocumentDb.Transport.LibGit2Sharp;

public enum CloneStrategy { Full, Shallow, PartialBlobless }

public sealed class LibGit2SharpOptions
{
    public required string RemoteUrl { get; init; }
    public required string LocalClonePath { get; init; }
    public CloneStrategy CloneStrategy { get; init; } = CloneStrategy.Full;
    public IGitCredentialsProvider? Credentials { get; init; }
}
```

**Step 3:** Build + commit: `feat(transport-lg2s): options and credentials abstraction`.

## Task 3: LibGit2SharpTreeView

**Step 1:** `src/GitDocumentDb.Transport.LibGit2Sharp/LibGit2SharpTreeView.cs`:
```csharp
using GitDocumentDb.Transport;
using Lg2 = global::LibGit2Sharp;

namespace GitDocumentDb.Transport.LibGit2Sharp;

internal sealed class LibGit2SharpTreeView : ITreeView
{
    private readonly Lg2.IRepository _repo;
    private readonly Lg2.Tree _root;

    public LibGit2SharpTreeView(Lg2.IRepository repo, string commitSha, Lg2.Tree root)
    {
        _repo = repo;
        CommitSha = commitSha;
        _root = root;
    }

    public string CommitSha { get; }

    public bool TryGetBlob(string path, out string blobSha)
    {
        var entry = _root[path];
        if (entry is null || entry.TargetType != Lg2.TreeEntryTargetType.Blob)
        {
            blobSha = "";
            return false;
        }
        blobSha = entry.Target.Sha;
        return true;
    }

    public bool TryGetTree(string path, out ITreeView subtree)
    {
        var entry = _root[path];
        if (entry is null || entry.TargetType != Lg2.TreeEntryTargetType.Tree)
        {
            subtree = null!;
            return false;
        }
        subtree = new LibGit2SharpTreeView(_repo, CommitSha, (Lg2.Tree)entry.Target);
        return true;
    }

    public IEnumerable<TreeEntry> EnumerateChildren(string path)
    {
        Lg2.Tree tree;
        if (string.IsNullOrEmpty(path))
        {
            tree = _root;
        }
        else
        {
            var entry = _root[path];
            if (entry is null || entry.TargetType != Lg2.TreeEntryTargetType.Tree)
                yield break;
            tree = (Lg2.Tree)entry.Target;
        }

        foreach (var child in tree)
        {
            var kind = child.TargetType switch
            {
                Lg2.TreeEntryTargetType.Tree => TreeEntryKind.Tree,
                Lg2.TreeEntryTargetType.Blob => TreeEntryKind.Blob,
                _ => TreeEntryKind.Blob,
            };
            yield return new TreeEntry(child.Name, child.Target.Sha, kind);
        }
    }
}
```

## Task 4: LibGit2SharpGitConnection

**Step 1:** `src/GitDocumentDb.Transport.LibGit2Sharp/LibGit2SharpGitConnection.cs`:

```csharp
using GitDocumentDb.Transport;
using Lg2 = global::LibGit2Sharp;

namespace GitDocumentDb.Transport.LibGit2Sharp;

public sealed class LibGit2SharpGitConnection : IGitConnection, IDisposable
{
    private readonly LibGit2SharpOptions _options;
    private readonly Lg2.Repository _repo;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LibGit2SharpGitConnection(LibGit2SharpOptions options)
    {
        _options = options;

        Directory.CreateDirectory(_options.LocalClonePath);
        var gitDir = Path.Combine(_options.LocalClonePath, ".git");
        if (!Directory.Exists(gitDir) && !IsBareRepo(_options.LocalClonePath))
        {
            CloneOrInit();
        }
        _repo = new Lg2.Repository(_options.LocalClonePath);
    }

    private static bool IsBareRepo(string path) =>
        File.Exists(Path.Combine(path, "HEAD")) && File.Exists(Path.Combine(path, "config"));

    private void CloneOrInit()
    {
        var cloneOptions = new Lg2.CloneOptions
        {
            IsBare = false,
        };
        if (_options.CloneStrategy == CloneStrategy.Shallow)
        {
            // LibGit2Sharp doesn't expose depth directly in all versions; if unavailable,
            // document this as a Full-clone fallback for Phase 4.
        }

        if (_options.Credentials is not null)
            cloneOptions.FetchOptions.CredentialsProvider = (url, user, types) =>
                ToLg2Credentials(_options.Credentials.GetCredentials(url));

        try
        {
            Lg2.Repository.Clone(_options.RemoteUrl, _options.LocalClonePath, cloneOptions);
        }
        catch (Lg2.LibGit2SharpException)
        {
            // If clone fails (e.g., empty remote), init the local repo and add the remote.
            Lg2.Repository.Init(_options.LocalClonePath);
            using var repo = new Lg2.Repository(_options.LocalClonePath);
            repo.Network.Remotes.Add("origin", _options.RemoteUrl);
        }
    }

    private static Lg2.Credentials ToLg2Credentials(GitCredentials cred) => cred switch
    {
        UsernamePasswordCredentials up => new Lg2.UsernamePasswordCredentials
        {
            Username = up.Username,
            Password = up.Password,
        },
        SshKeyCredentials ssh => new Lg2.SshUserKeyCredentials
        {
            Username = ssh.Username,
            PublicKey = ssh.PublicKey,
            PrivateKey = ssh.PrivateKey,
            Passphrase = ssh.Passphrase,
        },
        _ => new Lg2.DefaultCredentials(),
    };

    public Task<string?> ResolveRefAsync(string refName, CancellationToken ct)
    {
        // Resolve from the remote tracking ref after a fetch.
        var remoteRef = refName.Replace("refs/heads/", "refs/remotes/origin/");
        var local = _repo.Refs[refName];
        if (local is not null) return Task.FromResult<string?>(local.TargetIdentifier);
        var remote = _repo.Refs[remoteRef];
        return Task.FromResult<string?>(remote?.TargetIdentifier);
    }

    public Task<IReadOnlyList<string>> ListRefsAsync(string prefix, CancellationToken ct)
    {
        IReadOnlyList<string> refs = _repo.Refs
            .Where(r => r.CanonicalName.StartsWith(prefix, StringComparison.Ordinal)
                     || r.CanonicalName.StartsWith(prefix.Replace("refs/heads/", "refs/remotes/origin/"), StringComparison.Ordinal))
            .Select(r => r.CanonicalName.StartsWith("refs/remotes/origin/", StringComparison.Ordinal)
                ? "refs/heads/" + r.CanonicalName["refs/remotes/origin/".Length..]
                : r.CanonicalName)
            .Distinct()
            .ToList();
        return Task.FromResult(refs);
    }

    public Task<ITreeView> GetTreeAsync(string commitSha, CancellationToken ct)
    {
        var commit = _repo.Lookup<Lg2.Commit>(commitSha)
            ?? throw new InvalidOperationException($"Unknown commit {commitSha}");
        ITreeView view = new LibGit2SharpTreeView(_repo, commitSha, commit.Tree);
        return Task.FromResult(view);
    }

    public Task<ReadOnlyMemory<byte>> GetBlobAsync(string blobSha, CancellationToken ct)
    {
        var blob = _repo.Lookup<Lg2.Blob>(blobSha)
            ?? throw new InvalidOperationException($"Unknown blob {blobSha}");
        using var stream = blob.GetContentStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Task.FromResult<ReadOnlyMemory<byte>>(ms.ToArray());
    }

    public Task<string> WriteBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        using var ms = new MemoryStream(content.ToArray());
        var blob = _repo.ObjectDatabase.CreateBlob(ms);
        return Task.FromResult(blob.Sha);
    }

    public Task<string> WriteTreeAsync(TreeBuildSpec spec, CancellationToken ct)
    {
        var def = spec.BaseTreeSha is not null
            ? Lg2.TreeDefinition.From(_repo.Lookup<Lg2.Tree>(spec.BaseTreeSha))
            : new Lg2.TreeDefinition();

        foreach (var m in spec.Mutations)
        {
            if (m.Kind == TreeMutationKind.Upsert)
            {
                var blob = _repo.Lookup<Lg2.Blob>(m.BlobSha!)
                    ?? throw new InvalidOperationException($"Unknown blob {m.BlobSha}");
                def.Add(m.Path, blob, Lg2.Mode.NonExecutableFile);
            }
            else
            {
                def.Remove(m.Path);
            }
        }

        var tree = _repo.ObjectDatabase.CreateTree(def);
        return Task.FromResult(tree.Sha);
    }

    public Task<string> CreateCommitAsync(CommitSpec spec, CancellationToken ct)
    {
        var tree = _repo.Lookup<Lg2.Tree>(spec.TreeSha)
            ?? throw new InvalidOperationException($"Unknown tree {spec.TreeSha}");

        var author = new Lg2.Signature(spec.AuthorName, spec.AuthorEmail, spec.AuthorDate);
        var parents = spec.ParentSha is null
            ? Array.Empty<Lg2.Commit>()
            : new[] { _repo.Lookup<Lg2.Commit>(spec.ParentSha)! };

        var commit = _repo.ObjectDatabase.CreateCommit(
            author, author, spec.Message, tree, parents, prettifyMessage: false);
        return Task.FromResult(commit.Sha);
    }

    public async Task<PushResult> UpdateRefAsync(
        string refName, string? expectedOldSha, string newSha, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Ensure our view of the remote is current before CAS.
            await FetchAsyncInternal(ct);

            var existing = _repo.Refs[refName];
            var current = existing?.TargetIdentifier;
            if (current != expectedOldSha)
                return new PushResult(false, null, PushRejectReason.NonFastForward);

            if (existing is null)
                _repo.Refs.Add(refName, newSha);
            else
                _repo.Refs.UpdateTarget(existing, newSha);

            // Push to remote.
            try
            {
                var origin = _repo.Network.Remotes["origin"];
                if (origin is not null)
                {
                    var pushOptions = new Lg2.PushOptions();
                    if (_options.Credentials is not null)
                        pushOptions.CredentialsProvider = (url, user, types) =>
                            ToLg2Credentials(_options.Credentials.GetCredentials(url));
                    _repo.Network.Push(origin, $"{refName}:{refName}", pushOptions);
                }
                return new PushResult(true, newSha, null);
            }
            catch (Lg2.NonFastForwardException)
            {
                // Roll back local ref; remote advanced.
                if (existing is null) _repo.Refs.Remove(refName);
                else _repo.Refs.UpdateTarget(existing, current!);
                return new PushResult(false, null, PushRejectReason.NonFastForward);
            }
        }
        finally { _lock.Release(); }
    }

    public async Task<FetchResult> FetchAsync(string refName, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var before = _repo.Refs[refName]?.TargetIdentifier ?? "";
            await FetchAsyncInternal(ct);
            var after = _repo.Refs[refName]?.TargetIdentifier ?? "";
            return new FetchResult(
                before != after, before, after, Array.Empty<string>());
        }
        finally { _lock.Release(); }
    }

    private Task FetchAsyncInternal(CancellationToken ct)
    {
        var origin = _repo.Network.Remotes["origin"];
        if (origin is null) return Task.CompletedTask;

        var fetchOptions = new Lg2.FetchOptions();
        if (_options.Credentials is not null)
            fetchOptions.CredentialsProvider = (url, user, types) =>
                ToLg2Credentials(_options.Credentials.GetCredentials(url));

        var refspecs = origin.FetchRefSpecs.Select(r => r.Specification);
        Lg2.Commands.Fetch(_repo, "origin", refspecs, fetchOptions, null);

        // Mirror remote-tracking refs into local refs (so ResolveRefAsync / UpdateRefAsync see them).
        foreach (var remote in _repo.Refs.Where(r => r.CanonicalName.StartsWith("refs/remotes/origin/", StringComparison.Ordinal)))
        {
            var localName = "refs/heads/" + remote.CanonicalName["refs/remotes/origin/".Length..];
            if (localName.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            var existing = _repo.Refs[localName];
            if (existing is null) _repo.Refs.Add(localName, remote.TargetIdentifier);
            else _repo.Refs.UpdateTarget(existing, remote.TargetIdentifier);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetCommitParentsAsync(string commitSha, CancellationToken ct)
    {
        var commit = _repo.Lookup<Lg2.Commit>(commitSha)
            ?? throw new InvalidOperationException($"Unknown commit {commitSha}");
        IReadOnlyList<string> parents = commit.Parents.Select(p => p.Sha).ToList();
        return Task.FromResult(parents);
    }

    public void Dispose()
    {
        _lock.Dispose();
        _repo.Dispose();
    }
}
```

Commit: `feat(transport-lg2s): LibGit2Sharp IGitConnection implementation`.

## Task 5: Integration tests

**Step 1:** Create `tests/GitDocumentDb.Transport.LibGit2Sharp.Tests/LocalBareTransportTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.LibGit2Sharp;
using Lg2 = global::LibGit2Sharp;

namespace GitDocumentDb.Transport.LibGit2Sharp.Tests;

public class LocalBareTransportTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _bareRepoPath;
    private readonly string _clonePath;

    public LocalBareTransportTests()
    {
        _bareRepoPath = Path.Combine(Path.GetTempPath(), $"gitdb-bare-{Guid.NewGuid():N}.git");
        _clonePath = Path.Combine(Path.GetTempPath(), $"gitdb-clone-{Guid.NewGuid():N}");
        Lg2.Repository.Init(_bareRepoPath, isBare: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_bareRepoPath)) TryDelete(_bareRepoPath);
        if (Directory.Exists(_clonePath)) TryDelete(_clonePath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private LibGit2SharpGitConnection NewConnection() =>
        new(new LibGit2SharpOptions
        {
            RemoteUrl = "file://" + _bareRepoPath,
            LocalClonePath = _clonePath,
        });

    [Fact]
    public async Task Blob_roundtrip()
    {
        using var c = NewConnection();
        var sha = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("hello"), Ct);
        sha.Should().Be("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0");
        var bytes = await c.GetBlobAsync(sha, Ct);
        bytes.ToArray().Should().Equal(Encoding.UTF8.GetBytes("hello"));
    }

    [Fact]
    public async Task Tree_commit_and_push()
    {
        using var c = NewConnection();
        var blob = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("{}"), Ct);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, "tables/accounts/a.json", blob),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, "init"), Ct);

        var push = await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, Ct);
        push.Success.Should().BeTrue();

        var resolved = await c.ResolveRefAsync("refs/heads/db/alpha", Ct);
        resolved.Should().Be(commit);

        var treeView = await c.GetTreeAsync(commit, Ct);
        treeView.TryGetBlob("tables/accounts/a.json", out var gotBlob).Should().BeTrue();
        gotBlob.Should().Be(blob);
    }

    [Fact]
    public async Task Second_connection_sees_pushed_state()
    {
        using (var c1 = NewConnection())
        {
            var blob = await c1.WriteBlobAsync(Encoding.UTF8.GetBytes("x"), Ct);
            var tree = await c1.WriteTreeAsync(new TreeBuildSpec(null, new[]
            {
                new TreeMutation(TreeMutationKind.Upsert, "tables/t/a.json", blob),
            }), Ct);
            var commit = await c1.CreateCommitAsync(new CommitSpec(
                tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, "init"), Ct);
            var push = await c1.UpdateRefAsync("refs/heads/db/alpha", null, commit, Ct);
            push.Success.Should().BeTrue();
        }

        var clone2 = Path.Combine(Path.GetTempPath(), $"gitdb-clone2-{Guid.NewGuid():N}");
        try
        {
            using var c2 = new LibGit2SharpGitConnection(new LibGit2SharpOptions
            {
                RemoteUrl = "file://" + _bareRepoPath,
                LocalClonePath = clone2,
            });
            var sha = await c2.ResolveRefAsync("refs/heads/db/alpha", Ct);
            sha.Should().NotBeNullOrEmpty();
        }
        finally { TryDelete(clone2); }
    }
}
```

**Step 2:** `tests/GitDocumentDb.Transport.LibGit2Sharp.Tests/IntegrationTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using GitDocumentDb;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using Lg2 = global::LibGit2Sharp;

namespace GitDocumentDb.Transport.LibGit2Sharp.Tests;

public class IntegrationTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, string Email);

    private readonly string _bare;
    private readonly string _clone;

    public IntegrationTests()
    {
        _bare = Path.Combine(Path.GetTempPath(), $"gitdb-int-bare-{Guid.NewGuid():N}.git");
        _clone = Path.Combine(Path.GetTempPath(), $"gitdb-int-clone-{Guid.NewGuid():N}");
        Lg2.Repository.Init(_bare, isBare: true);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_bare)) Directory.Delete(_bare, true); } catch { }
        try { if (Directory.Exists(_clone)) Directory.Delete(_clone, true); } catch { }
    }

    [Fact]
    public async Task End_to_end_put_and_get_via_real_transport()
    {
        var connection = new LibGit2SharpGitConnection(new LibGit2SharpOptions
        {
            RemoteUrl = "file://" + _bare,
            LocalClonePath = _clone,
        });
        var doc = new DocumentDatabase(connection, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var db = doc.GetDatabase("alpha");
        var table = db.GetTable<Account>("accounts");

        var put = await table.PutAsync("a", new Account("a", "x@y"), null, Ct);
        put.Success.Should().BeTrue();

        var read = await table.GetAsync("a", null, Ct);
        read.Should().NotBeNull();
        read!.Record.Should().Be(new Account("a", "x@y"));
    }
}
```

**Step 3:** Run `dotnet build` and `dotnet test`. All tests should pass (existing 83 Core tests + new transport tests).

**Step 4:** Commit: `test(transport-lg2s): integration tests against local bare repo`.

## Task 6: README update

Add Phase 4 bullet and mention LibGit2Sharp transport. Commit: `docs: update README for phase 4`.

---

## Self-review

**Coverage:**
- §4.14 transport abstraction satisfied by `LibGit2SharpGitConnection`.
- §4.16 storage configuration: `LocalClonePath` honored; `CloneStrategy.Full` implemented; Shallow/PartialBlobless behaviors noted but v1 falls back to Full if libgit2 doesn't expose depth.

**Deferred:**
- Full shallow/partial-clone implementation — requires libgit2 API calls not always exposed via LibGit2Sharp; document as Phase 4.5.
- Credential caching, retry on transport failure — Phase 5 responsibility.

**Integration expectations:**
- Phase 4 tests run against a temporary bare repo; they verify blob/tree/commit/ref operations match Git's on-disk format (SHA-1 of blobs must match `GitBlobHasher`).
