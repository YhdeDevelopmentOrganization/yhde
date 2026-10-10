using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YHDE.Server.Assets;
using YHDE.Server.Domain;
using YHDE.Server.Operations;

namespace YHDE.Server.Projects;

public sealed record ImportResult(ProjectInfo Project, int Files, long Bytes, IReadOnlyList<string> Skipped);

// A new project from a zip of a Godot project (admin page, projects.md).
// Each file becomes an ordinary RegisterAsset operation, exactly as if an
// editor had shared it, so editors that join get the game the normal way.
// What editors never share is left out: .godot/ (per-machine caches), the
// YHDE add-on, hidden folders, and names Windows cannot store.
//
// The unpacked size is checked while the zip is read (a small zip can unpack
// to far more than it says), and an import that fails or is cancelled
// half-way removes its project again, with the files only it used.
public sealed class ProjectImporter(IProjectStore projects, BlobStore blobs, OperationProcessor processor, ILogger<ProjectImporter> logger,
    IProjectBlobs? projectBlobs = null, YHDE.Server.Persistence.Database? db = null)
{
    public const int MaxEntries = 50_000;

    // Fixed namespace for file ids; the same as the editor's (client
    // core/uuid.cpp), so both name a file's operations alike.
    private static readonly byte[] Namespace = Convert.FromHexString("5f0e6c1a9a554f3e8b0b79d2a1c3e4f5");

    // `zipPath`: the uploaded zip on disk. `name`: empty to use the game's own
    // name from project.godot. `room`: how many unpacked bytes may be stored.
    // `attach`: runs before the import counts as done (giving the project to a
    // team); if it fails, the project is removed like any failed import.
    // Throws ImportException for a person-readable problem.
    public async Task<ImportResult> ImportAsync(string zipPath, string name, CancellationToken ct,
        long room = long.MaxValue, Func<Guid, CancellationToken, Task>? attach = null)
    {
        using var zip = OpenZip(zipPath);
        var cfg = zip.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => n == "project.godot" || n.EndsWith("/project.godot", StringComparison.Ordinal))
            .Where(n => !n.Contains("/addons/", StringComparison.Ordinal))
            .OrderBy(n => n.Count(c => c == '/'))
            .FirstOrDefault()
            ?? throw new ImportException("This zip has no Godot project in it (no project.godot).");
        var root = cfg[..^"project.godot".Length];

        if (string.IsNullOrWhiteSpace(name)) name = GameName(zip.GetEntry(cfg)!) ?? Path.GetFileNameWithoutExtension(zipPath);
        name = Presence.PresenceService.SanitizeText(name, 80);
        if (name.Length == 0) name = "Imported game";

        var files = new List<(ZipArchiveEntry Entry, string Path)>();
        var skipped = new List<string>();
        foreach (var entry in zip.Entries)
        {
            var full = entry.FullName.Replace('\\', '/');
            if (!full.StartsWith(root, StringComparison.Ordinal) || full.EndsWith('/')) continue;
            var path = "res://" + full[root.Length..];
            if (!AssetRules.IsValidPath(path) || AssetRules.IsImportedOutput(path))
            {
                // .godot/ caches and the YHDE add-on are left out on purpose: not news.
                var rel = full[root.Length..];
                if (!rel.StartsWith(".godot/", StringComparison.Ordinal) && !rel.StartsWith("addons/yhde/", StringComparison.OrdinalIgnoreCase)) skipped.Add(rel);
                continue;
            }
            if (entry.Length > blobs.MaxBlobBytes)
            {
                skipped.Add(full[root.Length..] + " (too large)");
                continue;
            }
            files.Add((entry, path));
        }
        if (files.Count == 0) throw new ImportException("Nothing in this zip can be shared.");
        if (files.Count > MaxEntries) throw new ImportException($"This zip has more than {MaxEntries} files. Share the game from Godot instead.");
        // What the zip says it unpacks to; checked again while reading.
        if (files.Sum(f => f.Entry.Length) > room) throw new ImportException(TooBig);

        var project = await projects.CreateAsync(name, ct);
        var session = Guid.NewGuid(); // the import, as one "connection"
        long bytes = 0;
        long unpacked = 0;
        var imported = 0;
        try
        {
            // project.godot first, then the files, then their .import / .uid
            // sidecars, the order editors send them in (assets.md).
            foreach (var (entry, path) in files.OrderBy(f => Rank(f.Path)).ThenBy(f => f.Path, StringComparer.Ordinal))
            {
                (string Hash, long Size)? stored;
                await using (var s = new CountingStream(entry.Open(), entry.Length, room - unpacked))
                {
                    stored = await blobs.PutAsync(s, ct);
                    unpacked += s.BytesRead;
                }
                if (stored is not { } blob)
                {
                    skipped.Add(path + " (too large)");
                    continue;
                }
                if (projectBlobs is not null) await projectBlobs.AddAsync(project.ProjectId, blob.Hash, blob.Size, "import", referenced: true, ct);
                var payload = JsonSerializer.Serialize(new Dictionary<string, object> { ["s"] = path, ["h"] = blob.Hash, ["n"] = blob.Size });
                var submission = new OperationSubmission(Guid.NewGuid(), OperationType.RegisterAsset, FileId(path), payload, Guid.NewGuid(), 0);
                var result = await processor.ProcessAsync(submission, project.MainBranchId, DevIdentity.ActorId, session, ct);
                if (result is SubmitResult.Rejected r)
                {
                    skipped.Add($"{path} ({r.Reason})");
                    continue;
                }
                bytes += blob.Size;
                imported++;
            }
            if (attach is not null) await attach(project.ProjectId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Import of {Name} stopped after {Files} files; removing the half-made project", name, imported);
            await RemoveAsync(project.ProjectId);
            if (ex is InvalidDataException) throw new ImportException("The zip is damaged: " + ex.Message);
            throw;
        }
        logger.LogInformation("Imported project {Name}: {Files} files, {Bytes} bytes, {Skipped} skipped",
            name, imported, bytes, skipped.Count);
        return new ImportResult(project, imported, bytes, skipped);
    }

    private const string TooBig = "The unpacked game does not fit in your storage. Delete a project first, or leave large files out of the zip.";

    // Not cancellable: it runs because the request was cancelled.
    private async Task RemoveAsync(Guid projectId)
    {
        try
        {
            await projects.SetArchivedAsync(projectId, true, CancellationToken.None);
            await projects.DeleteArchivedAsync(projectId, CancellationToken.None);
            if (db is not null) await Admin.AdminProjectEndpoints.FreeUnusedFiles(db, blobs, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove the half-imported project {ProjectId}", projectId);
        }
    }

    // Reads one zip entry, refusing more bytes than the entry declared or than
    // the room left: a zip cannot unpack to more than it says.
    private sealed class CountingStream(Stream inner, long declared, long room) : Stream
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Count(int n)
        {
            BytesRead += n;
            if (BytesRead > declared) throw new ImportException("The zip is damaged: a file is larger than the zip says.");
            if (BytesRead > room) throw new ImportException(TooBig);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static ZipArchive OpenZip(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new ImportException("That file is not a zip."); }
    }

    private static int Rank(string path) =>
        path == "res://project.godot" ? 0 : path.EndsWith(".import", StringComparison.Ordinal) || path.EndsWith(".uid", StringComparison.Ordinal) ? 2 : 1;

    private static string? GameName(ZipArchiveEntry cfg)
    {
        using var reader = new StreamReader(cfg.Open());
        foreach (var line in reader.ReadToEnd().Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("config/name=", StringComparison.Ordinal)) continue;
            var v = t["config/name=".Length..].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            return v;
        }
        return null;
    }

    // RFC 4122 version 5 (SHA-1) id of "file:<path>" in the YHDE namespace,
    // byte for byte the editor's Uuid::from_name.
    public static Guid FileId(string path)
    {
        var name = Encoding.UTF8.GetBytes("file:" + path);
        var data = new byte[Namespace.Length + name.Length];
        Namespace.CopyTo(data, 0);
        name.CopyTo(data, Namespace.Length);
        var hash = SHA1.HashData(data);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return Guid.ParseExact(Convert.ToHexStringLower(hash, 0, 16), "N");
    }
}

public sealed class ImportException(string message) : Exception(message);
