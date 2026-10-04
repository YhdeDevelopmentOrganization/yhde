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
public sealed class ProjectImporter(IProjectStore projects, BlobStore blobs, OperationProcessor processor, ILogger<ProjectImporter> logger)
{
    // Fixed namespace for file ids; the same as the editor's (client
    // core/uuid.cpp), so both name a file's operations alike.
    private static readonly byte[] Namespace = Convert.FromHexString("5f0e6c1a9a554f3e8b0b79d2a1c3e4f5");

    // `zipPath`: the uploaded zip on disk. `name`: empty to use the game's own
    // name from project.godot. Throws ImportException for a person-readable problem.
    public async Task<ImportResult> ImportAsync(string zipPath, string name, CancellationToken ct)
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

        var project = await projects.CreateAsync(name, ct);
        var session = Guid.NewGuid(); // the import, as one "connection"
        long bytes = 0;
        var imported = 0;
        // project.godot first, then the files, then their .import / .uid
        // sidecars, the order editors send them in (assets.md).
        foreach (var (entry, path) in files.OrderBy(f => Rank(f.Path)).ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            (string Hash, long Size)? stored;
            await using (var s = entry.Open()) stored = await blobs.PutAsync(s, ct);
            if (stored is not { } blob)
            {
                skipped.Add(path + " (too large)");
                continue;
            }
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
        logger.LogInformation("Imported project {Name}: {Files} files, {Bytes} bytes, {Skipped} skipped",
            name, imported, bytes, skipped.Count);
        return new ImportResult(project, imported, bytes, skipped);
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
