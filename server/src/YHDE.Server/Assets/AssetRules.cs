using System.Text.Json;
using YHDE.Server.Domain;
using YHDE.Server.Operations;

namespace YHDE.Server.Assets;

// What an asset operation must satisfy before it enters the log (assets.md).
//
// Payload fields: "s" project path, "h" content hash, "n" size in bytes,
// "f" previous path (MoveAsset), "o" previous hash (for undo and conflicts),
// "d" optional project files this file depends on (applied before it).
// The bytes must already be in the blob store, so every client that applies
// the operation can fetch them.
public static class AssetRules
{
    public const int MaxPathLength = 1024;
    public const int MaxDependencies = 256;

    // Import results a teammate's editor cannot make itself (a .blend needs
    // Blender) are shared from here; nothing else under .godot/ is.
    public const string ImportedPrefix = "res://.godot/imported/";

    public static bool IsAssetOp(string type) =>
        type is OperationType.RegisterAsset or OperationType.UpdateAsset or OperationType.MoveAsset or OperationType.DeleteAsset;

    public static (RejectionCode code, string reason)? Validate(OperationSubmission submission, BlobStore blobs)
    {
        if (!IsAssetOp(submission.Type)) return null;

        using var doc = JsonDocument.Parse(submission.Payload);
        var root = doc.RootElement;
        var path = StringField(root, "s");
        if (!IsValidPath(path))
            return (RejectionCode.InvalidPayload, $"'{path}' is not a shareable project file path.");

        if (submission.Type == OperationType.MoveAsset)
        {
            var from = StringField(root, "f");
            if (!IsValidPath(from) || from == path)
                return (RejectionCode.InvalidPayload, "A move needs a different, valid source path.");
            if (IsProtected(from))
                return (RejectionCode.InvalidPayload, $"'{from}' cannot be moved away.");
        }

        if (submission.Type == OperationType.DeleteAsset)
            return IsProtected(path) ? (RejectionCode.InvalidPayload, $"'{path}' cannot be deleted for everyone.") : null;

        if (root.TryGetProperty("d", out var deps))
        {
            if (deps.ValueKind != JsonValueKind.Array || deps.GetArrayLength() > MaxDependencies)
                return (RejectionCode.InvalidPayload, "Dependencies must be a short list of project paths.");
            foreach (var dep in deps.EnumerateArray())
            {
                if (dep.ValueKind != JsonValueKind.String || !IsValidPath(dep.GetString()))
                    return (RejectionCode.InvalidPayload, "A dependency is not a valid project path.");
            }
        }

        var hash = StringField(root, "h");
        if (!BlobStore.IsValidHash(hash))
            return (RejectionCode.InvalidPayload, "Asset hash must be a lowercase hex SHA-256.");
        if (!root.TryGetProperty("n", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt64(out var size) || size < 0)
            return (RejectionCode.InvalidPayload, "Asset size is missing.");
        var stored = blobs.SizeOf(hash!);
        if (stored is null)
            return (RejectionCode.AssetMissing, "The file's bytes have not been uploaded.");
        if (stored != size)
            return (RejectionCode.InvalidPayload, "Asset size does not match the uploaded bytes.");
        return null;
    }

    public static bool IsImportedOutput(string? path) =>
        path is not null && path.StartsWith(ImportedPrefix, StringComparison.Ordinal);

    // Removing project.godot would break every editor on the branch.
    public static bool IsProtected(string? path) =>
        path == "res://project.godot";

    // res://relative/path: no traversal, no hidden folders (.godot holds
    // per-machine caches, .git hooks would run code), not the YHDE add-on.
    // The one exception is a file directly in .godot/imported/.
    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength) return false;
        if (!path.StartsWith("res://", StringComparison.Ordinal)) return false;
        // Control characters: Windows cannot store them in a name.
        if (path.Any(char.IsControl)) return false;
        if (path.StartsWith(ImportedPrefix, StringComparison.Ordinal))
        {
            var name = path[ImportedPrefix.Length..];
            return name.Length > 0 && !name.StartsWith('.') && !name.Contains('/') && !name.Contains('\\') && !name.Contains(':')
                && !name.Contains('\0') && name.Trim() == name && !name.EndsWith('.') && !IsMachineFile(name);
        }
        var relative = path[6..];
        if (relative.Length == 0 || relative.Contains('\\') || relative.Contains('\0') || relative.Contains(':')) return false;
        var segments = relative.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0 || segment == "." || segment == "..") return false;
            if (segment.Trim() != segment || segment.EndsWith('.')) return false; // Windows cannot store these
            var isDirectory = i < segments.Length - 1;
            if (isDirectory && segment.StartsWith('.')) return false;
        }
        if (relative.StartsWith("addons/yhde/", StringComparison.OrdinalIgnoreCase)) return false;
        return !IsMachineFile(segments[^1]);
    }

    // Per-machine and temporary files, never shared (the editor skips them
    // too; client/tests/gate/path_rules.json holds the rules both sides check).
    public static bool IsMachineFile(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower is ".ds_store" or "thumbs.db" or "desktop.ini" or "override.cfg"
            || lower.EndsWith(".tmp", StringComparison.Ordinal) || lower.EndsWith('~') || lower.EndsWith(".swp", StringComparison.Ordinal)
            || lower.EndsWith(".yhde-tmp", StringComparison.Ordinal);
    }

    private static string? StringField(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
