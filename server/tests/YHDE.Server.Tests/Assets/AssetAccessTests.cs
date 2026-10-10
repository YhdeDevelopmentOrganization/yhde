using System.Collections.Concurrent;
using System.IO.Compression;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YHDE.Server.Accounts;
using YHDE.Server.Assets;
using YHDE.Server.Gateway;
using YHDE.Server.Operations;
using YHDE.Server.Persistence.Repositories;
using YHDE.Server.Projects;
using YHDE.Server.Teams;

namespace YHDE.Server.Tests.Assets;

// project_blobs without a database.
public sealed class InMemoryProjectBlobs : IProjectBlobs
{
    public readonly ConcurrentDictionary<(Guid, string), ProjectBlob> Rows = new();

    public Task<ProjectBlob?> GetAsync(Guid projectId, string hash, CancellationToken ct) =>
        Task.FromResult(Rows.TryGetValue((projectId, hash), out var b) ? b : null);

    public Task<bool> AnyAsync(IReadOnlyCollection<Guid> projectIds, string hash, CancellationToken ct) =>
        Task.FromResult(projectIds.Any(p => Rows.ContainsKey((p, hash))));

    public Task AddAsync(Guid projectId, string hash, long size, string uploader, bool referenced, CancellationToken ct)
    {
        Rows.AddOrUpdate((projectId, hash), new ProjectBlob(size, referenced), (_, old) => old with { Referenced = old.Referenced || referenced });
        return Task.CompletedTask;
    }

    public Task MarkReferencedAsync(Guid projectId, string hash, CancellationToken ct)
    {
        if (Rows.TryGetValue((projectId, hash), out var b)) Rows[(projectId, hash)] = b with { Referenced = true };
        return Task.CompletedTask;
    }

    public Task<long> PendingBytesAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct) =>
        Task.FromResult(Rows.Where(r => projectIds.Contains(r.Key.Item1) && !r.Value.Referenced).Sum(r => r.Value.Size));

    public Task<IReadOnlyList<string>> ExpirePendingAsync(TimeSpan age, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

// The file routes serve and accept only the
// asker's project, uploads count toward storage and the disk, and one
// uploader's wrong bytes never cost another their progress.
public sealed class AssetAccessTests : IDisposable
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private readonly TempBlobStore _temp = new();
    private readonly InMemoryProjectBlobs _held = new();

    public void Dispose() => _temp.Dispose();

    private static AssetScope Editor(Guid project, string who = "ada") => new(project, true, who);

    private static DefaultHttpContext Patch(byte[] body, long offset, long total)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[AssetEndpoints.UploadOffsetHeader] = offset.ToString();
        context.Request.Headers[AssetEndpoints.UploadLengthHeader] = total.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        return context;
    }

    private async Task<IResult> UploadAsync(byte[] bytes, AssetScope scope, StorageQuota? quota = null) =>
        await AssetEndpoints.AppendAsync(TempBlobStore.HashOf(bytes), _temp.Store, Patch(bytes, 0, bytes.Length), scope, _held, quota);

    private async Task<string> InProjectAsync(Guid project, byte[] bytes)
    {
        (await UploadAsync(bytes, Editor(project))).Should().BeOfType<Ok<AssetEndpoints.ChunkResponse>>()
            .Which.Value!.Complete.Should().BeTrue();
        return TempBlobStore.HashOf(bytes);
    }

    [Fact]
    public async Task A_view_only_grant_cannot_upload()
    {
        var viewer = new AssetScope(A, CanUpload: false, "viewer");
        (await UploadAsync([1, 2, 3], viewer)).Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(403);
        _temp.Store.Exists(TempBlobStore.HashOf([1, 2, 3])).Should().BeFalse();
        _held.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_cannot_read_or_probe_another_projects_file()
    {
        var hash = await InProjectAsync(B, [9, 9, 9]);

        (await AssetEndpoints.GetAsync(hash, _temp.Store, Editor(A), _held, default)).Should().BeOfType<NotFound>();
        var head = new DefaultHttpContext();
        (await AssetEndpoints.HeadAsync(hash, _temp.Store, head, Editor(A), _held)).Should().BeOfType<NotFound>();
        var missing = await AssetEndpoints.MissingAsync(new([hash]), _temp.Store, Editor(A), _held, default);
        missing.Should().BeOfType<Ok<AssetEndpoints.MissingResponse>>().Which.Value!.Missing.Should().Equal(new[] { hash },
            "a file another project holds looks exactly like one that does not exist");

        (await AssetEndpoints.GetAsync(hash, _temp.Store, Editor(B), _held, default)).Should().BeOfType<FileStreamHttpResult>();
    }

    [Fact]
    public async Task Knowing_a_hash_is_not_enough_the_whole_file_must_be_sent()
    {
        var bytes = new byte[] { 4, 5, 6, 7 };
        var hash = await InProjectAsync(B, bytes);

        // Someone in A claims the file with other bytes: refused, and A still lacks it.
        (await AssetEndpoints.AppendAsync(hash, _temp.Store, Patch([0, 0, 0, 0], 0, 4), Editor(A, "mallory"), _held))
            .Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(422);
        (await _held.GetAsync(A, hash, default)).Should().BeNull();
        _temp.Store.Exists(hash).Should().BeTrue("the stored copy is untouched");

        // The real bytes: A gets it, as a pending upload, and the store keeps one copy.
        (await UploadAsync(bytes, Editor(A))).Should().BeOfType<Ok<AssetEndpoints.ChunkResponse>>().Which.Value!.Complete.Should().BeTrue();
        (await _held.GetAsync(A, hash, default)).Should().Be(new ProjectBlob(4, Referenced: false));
        (await AssetEndpoints.GetAsync(hash, _temp.Store, Editor(A), _held, default)).Should().BeOfType<FileStreamHttpResult>();
    }

    [Fact]
    public async Task An_honest_upload_completes_while_someone_else_sends_wrong_bytes()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var hash = TempBlobStore.HashOf(bytes);

        // Ada sends the first half; Mallory finishes "the same file" with junk.
        (await AssetEndpoints.AppendAsync(hash, _temp.Store, Patch(bytes[..50], 0, 100), Editor(A, "ada"), _held))
            .Should().BeOfType<Ok<AssetEndpoints.ChunkResponse>>().Which.Value!.Offset.Should().Be(50);
        (await AssetEndpoints.AppendAsync(hash, _temp.Store, Patch(new byte[100], 0, 100), Editor(A, "mallory"), _held))
            .Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(422);

        // Ada's progress is still there.
        _temp.Store.PartialLength(hash, "ada").Should().Be(50);
        (await AssetEndpoints.AppendAsync(hash, _temp.Store, Patch(bytes[50..], 50, 100), Editor(A, "ada"), _held))
            .Should().BeOfType<Ok<AssetEndpoints.ChunkResponse>>().Which.Value!.Complete.Should().BeTrue();
        _temp.Store.Exists(hash).Should().BeTrue();
    }

    [Fact]
    public async Task A_file_asked_for_again_is_touched_so_clean_up_keeps_it()
    {
        var hash = await InProjectAsync(A, [3, 1, 4]);
        var old = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(_temp.Store.PathOf(hash), old);

        (await UploadAsync([3, 1, 4], Editor(A))).Should().BeOfType<Ok<AssetEndpoints.ChunkResponse>>().Which.Value!.Complete.Should().BeTrue();

        File.GetLastWriteTimeUtc(_temp.Store.PathOf(hash)).Should().BeAfter(old.AddDays(2));
        _temp.Store.RemoveUnreferenced(new HashSet<string>()).Files.Should().Be(0, "a file touched today is kept");
    }

    [Fact]
    public async Task Uploads_stop_when_the_disk_is_low()
    {
        using var full = new TempBlobStore(minFreeBytes: long.MaxValue / 2);
        var bytes = new byte[] { 1 };
        (await AssetEndpoints.AppendAsync(TempBlobStore.HashOf(bytes), full.Store, Patch(bytes, 0, 1), Editor(A), _held))
            .Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(507);
        full.Store.Exists(TempBlobStore.HashOf(bytes)).Should().BeFalse();
    }

    [Fact]
    public async Task One_uploader_may_have_only_so_many_unfinished_uploads()
    {
        for (var i = 0; i < BlobStore.MaxUnfinishedParts; i++)
        {
            var b = BitConverter.GetBytes(i).Concat(new byte[8]).ToArray();
            (await AssetEndpoints.AppendAsync(TempBlobStore.HashOf(b), _temp.Store, Patch(b[..4], 0, b.Length), Editor(A), _held))
                .Should().BeOfType<Ok<AssetEndpoints.ChunkResponse>>();
        }
        var next = new byte[] { 42, 42 };
        (await AssetEndpoints.AppendAsync(TempBlobStore.HashOf(next), _temp.Store, Patch(next[..1], 0, 2), Editor(A), _held))
            .Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(429);
        (await UploadAsync(next, Editor(A, "someone-else"))).Should().BeOfType<Ok<AssetEndpoints.ChunkResponse>>("the limit is per uploader");
    }

    [Fact]
    public void The_scope_follows_the_grant_and_the_named_project()
    {
        var invite = new AccessGrant(A);
        AssetEndpoints.ScopeFor(invite, null, "Bearer YHDE-X")!.Project.Should().Be(A, "an invite code is for one project");
        AssetEndpoints.ScopeFor(invite, B.ToString(), "Bearer YHDE-X").Should().BeNull("an invite code for A cannot name B");

        var view = new AccessGrant(A) { ReadOnly = new HashSet<Guid> { A } };
        AssetEndpoints.ScopeFor(view, A.ToString(), "Bearer YHDE-X")!.CanUpload.Should().BeFalse();

        var person = new AccessGrant((Guid?)null) { Projects = new HashSet<Guid> { A, B }, ReadOnly = new HashSet<Guid> { B }, UserId = Guid.NewGuid() };
        AssetEndpoints.ScopeFor(person, A.ToString(), "Bearer t")!.CanUpload.Should().BeTrue();
        AssetEndpoints.ScopeFor(person, B.ToString(), "Bearer t")!.CanUpload.Should().BeFalse();
        AssetEndpoints.ScopeFor(person, Guid.NewGuid().ToString(), "Bearer t").Should().BeNull();
        var old = AssetEndpoints.ScopeFor(person, null, "Bearer t")!;
        old.CanUpload.Should().BeFalse("an add-on that does not name its project must update to upload");
        old.ReadableProjects.Should().BeEquivalentTo([A, B]);

        AssetEndpoints.ScopeFor(AccessGrant.AllProjects, null, "Bearer key")!.Global.Should().BeTrue();
        AssetEndpoints.ScopeFor(AccessGrant.AllProjects, "not-a-guid", "Bearer key").Should().BeNull();
    }

    [Fact]
    public async Task An_old_add_on_with_a_sign_in_reads_only_its_projects_files()
    {
        var mine = await InProjectAsync(A, [1, 1]);
        var theirs = await InProjectAsync(B, [2, 2]);
        var old = new AssetScope(null, false, "u") { ReadableProjects = [A] };
        (await AssetEndpoints.GetAsync(mine, _temp.Store, old, _held, default)).Should().BeOfType<FileStreamHttpResult>();
        (await AssetEndpoints.GetAsync(theirs, _temp.Store, old, _held, default)).Should().BeOfType<NotFound>();
    }
}

// The parts that need the database: storage counted at upload,
// pending uploads expiring, and imports that fail half-way.
[Collection("integration")]
public sealed class AssetStorageTests : IDisposable
{
    private readonly TempBlobStore _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DefaultHttpContext Patch(byte[] body, long total)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[AssetEndpoints.UploadOffsetHeader] = "0";
        context.Request.Headers[AssetEndpoints.UploadLengthHeader] = total.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        return context;
    }

    private static async Task<(Team Team, Guid Project)> OwnedProjectAsync(YHDE.Server.Persistence.Database db)
    {
        var accounts = new AccountStore(db);
        var teams = new TeamStore(db);
        var owner = (await accounts.CreateAsync($"t{Guid.NewGuid():N}@example.com", "Owner", null, verified: true, default))!;
        var team = await teams.CreateAsync(owner.Id, owner.Name, Plans.Beta, "month", default);
        var p = await new ProjectStore(db).CreateAsync("Quota", default);
        await teams.AttachProjectAsync(p.ProjectId, team.Id, default);
        return (team, p.ProjectId);
    }

    [Fact]
    public async Task An_upload_over_the_owners_storage_is_refused_without_any_operation()
    {
        if (TestDatabase.Open() is not { } db) return;
        var (team, project) = await OwnedProjectAsync(db);
        var held = new ProjectBlobs(db);
        var quota = new StorageQuota(new TeamStore(db), held);
        var scope = new AssetScope(project, true, "owner");

        var tooBig = team.StorageBytes + 1;
        using var roomy = new TempBlobStore(maxBlobBytes: long.MaxValue);
        (await AssetEndpoints.AppendAsync(new string('a', 64), roomy.Store, Patch([1], tooBig), scope, held, quota))
            .Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(507);

        // Pending uploads count: fill all but 10 bytes, then 11 more is refused.
        await held.AddAsync(project, new string('b', 64), team.StorageBytes - 10, "owner", referenced: false, default);
        var quota2 = new StorageQuota(new TeamStore(db), held);
        (await quota2.RefuseUploadAsync(project, 11, default)).Should().NotBeNull();
        (await quota2.RefuseUploadAsync(project, 10, default)).Should().BeNull();
        (await quota2.RefuseUploadAsync(project, 1, default)).Should().NotBeNull("the 10 bytes were just taken");
        // Using that pending upload in an operation does not count it twice.
        (await quota2.RefuseAsync(project, "RegisterAsset", "{\"n\":10}", default, uploadCounted: true)).Should().BeNull();
    }

    [Fact]
    public async Task Two_uploads_at_the_same_moment_cannot_both_take_the_last_room()
    {
        if (TestDatabase.Open() is not { } db) return;
        var (team, project) = await OwnedProjectAsync(db);
        var held = new ProjectBlobs(db);
        await held.AddAsync(project, new string('c', 64), team.StorageBytes - 100, "owner", referenced: false, default);
        var quota = new StorageQuota(new TeamStore(db), held);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => quota.RefuseUploadAsync(project, 60, default)));
        results.Count(r => r is null).Should().Be(1);
    }

    [Fact]
    public async Task Pending_uploads_expire_and_their_bytes_go_but_used_files_stay()
    {
        if (TestDatabase.Open() is not { } db) return;
        var (_, project) = await OwnedProjectAsync(db);
        var held = new ProjectBlobs(db);
        var unused = TempBlobStore.HashOf(Guid.NewGuid().ToByteArray());
        var used = TempBlobStore.HashOf(Guid.NewGuid().ToByteArray());
        await held.AddAsync(project, unused, 5, "owner", referenced: false, default);
        await held.AddAsync(project, used, 5, "owner", referenced: false, default);
        await held.MarkReferencedAsync(project, used, default);
        await using (var conn = await db.OpenAsync(default))
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE project_blobs SET created_at = now() - interval '3 days' WHERE project_id = @project", new { project });

        var gone = await held.ExpirePendingAsync(BlobJanitor.PendingLifetime, default);
        gone.Should().Contain(unused).And.NotContain(used);
        (await held.GetAsync(project, unused, default)).Should().BeNull();
        (await held.GetAsync(project, used, default)).Should().NotBeNull();
    }

    private static string Zip(params (string Name, byte[] Bytes)[] files)
    {
        var path = Path.Combine(Path.GetTempPath(), $"yhde-import-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in files)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            s.Write(bytes);
        }
        return path;
    }

    private ProjectImporter Importer(YHDE.Server.Persistence.Database db, ProjectStore projects)
    {
        var ops = new OperationRepository(db);
        var committer = new BranchCommitter(ops, Substitute.For<ISessionManager>(), NullLogger<BranchCommitter>.Instance);
        var processor = new OperationProcessor(committer, ops, _temp.Store, NullLogger<OperationProcessor>.Instance);
        return new ProjectImporter(projects, _temp.Store, processor, NullLogger<ProjectImporter>.Instance, new ProjectBlobs(db), db);
    }

    [Fact]
    public async Task A_small_zip_that_unpacks_past_the_room_is_refused_and_leaves_nothing()
    {
        if (TestDatabase.Open() is not { } db) return;
        var projects = new ProjectStore(db);
        var before = (await projects.ListAsync(default)).Count;
        // 4 MB of zeros compresses to a few kB.
        var zip = Zip(("game/project.godot", "config_version=5\n"u8.ToArray()), ("game/big.bin", new byte[4_000_000]));
        try
        {
            new FileInfo(zip).Length.Should().BeLessThan(100_000);
            var act = () => Importer(db, projects).ImportAsync(zip, "Bomb", default, room: 1_000_000);
            await act.Should().ThrowAsync<ImportException>();
            (await projects.ListAsync(default)).Count.Should().Be(before, "no orphan project is left");
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task An_import_that_fails_at_the_end_removes_its_project_and_files()
    {
        if (TestDatabase.Open() is not { } db) return;
        var projects = new ProjectStore(db);
        var unique = Guid.NewGuid().ToByteArray();
        var zip = Zip(("project.godot", "config_version=5\n"u8.ToArray()), ("level.tscn", unique));
        try
        {
            var before = (await projects.ListAsync(default)).Count;
            var act = () => Importer(db, projects).ImportAsync(zip, "Cancelled", default,
                attach: (_, _) => throw new OperationCanceledException());
            await act.Should().ThrowAsync<OperationCanceledException>();
            (await projects.ListAsync(default)).Count.Should().Be(before);
            File.SetLastWriteTimeUtc(_temp.Store.PathOf(TempBlobStore.HashOf(unique)), DateTime.UtcNow.AddDays(-2));
            await Admin.AdminProjectEndpoints.FreeUnusedFiles(db, _temp.Store, NullLogger.Instance);
            _temp.Store.Exists(TempBlobStore.HashOf(unique)).Should().BeFalse("only the failed import used it");
        }
        finally
        {
            File.Delete(zip);
        }
    }
}
