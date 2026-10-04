using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YHDE.Server.Assets;

namespace YHDE.Server.Addon;

public sealed record AddonManifest(string Version, string Sha256, long Size, string[] Platforms, DateTimeOffset Uploaded);

// The YHDE editor add-on this server hands out (onboarding.md): the operator
// uploads a package on the admin page; the join page gives it to new people
// (alone, or inside a starter project for an invite code) and editors update
// themselves from it.
//
// A package is stored normalized: only files under addons/yhde/, whatever
// folder the uploaded zip had them in. Nothing from a package is ever run on
// the server; it is only checked, stored and handed out.
//
// An upload is only staged ("pending"): editors, download links and the join
// page keep the released package until the admin releases the new one.
public sealed class AddonStore
{
    public const long MaxPackageBytes = 128L * 1024 * 1024;
    public const string Prefix = "addons/yhde/";
    private const string PackageFile = "yhde-addon.zip";
    private const string ManifestFile = "manifest.json";
    private const string PendingFile = "pending.zip";
    private const string PendingManifestFile = "pending.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir;
    private readonly object _lock = new();
    private AddonManifest? _current;
    private AddonManifest? _pending;
    private readonly string? _unavailable; // why add-ons cannot be stored here

    public AddonStore(string dir)
    {
        _dir = dir;
        try
        {
            Directory.CreateDirectory(_dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never keep the server from starting over this (a read-only disk,
            // a server kit from before 0.3 without the add-on volume).
            _unavailable = $"The server cannot store the add-on in {_dir} ({e.Message}). With the server kit, run ./yhde update.";
            return;
        }
        _current = Load(ManifestFile, PackageFile);
        _pending = Load(PendingManifestFile, PendingFile);
    }

    private AddonManifest? Load(string manifest, string package)
    {
        if (!File.Exists(Path.Combine(_dir, manifest)) || !File.Exists(Path.Combine(_dir, package))) return null;
        try { return JsonSerializer.Deserialize<AddonManifest>(File.ReadAllText(Path.Combine(_dir, manifest)), Json); }
        catch (JsonException) { return null; }
    }

    // Next to the blob store: /data/addon on the server kit.
    public static AddonStore From(IConfiguration configuration, BlobStoreOptions blobs)
    {
        var dir = configuration["Yhde:AddonPath"];
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Path.GetDirectoryName(blobs.Root.TrimEnd('/', '\\')) ?? blobs.Root, "addon");
        return new AddonStore(Path.GetFullPath(dir));
    }

    // Released: what editors, download links and the join page get.
    public AddonManifest? Current { get { lock (_lock) return _current; } }
    // Uploaded, waiting for the admin to release it.
    public AddonManifest? Pending { get { lock (_lock) return _pending; } }
    public string PackagePath => Path.Combine(_dir, PackageFile);

    // Checks an uploaded zip and stages it (see Release). Returns an error for
    // the person uploading, or null.
    public string? Install(byte[] zip, DateTimeOffset now)
    {
        if (_unavailable is not null) return _unavailable;
        List<(string Path, byte[] Bytes)> files;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var cfg = archive.Entries
                .Where(e => e.FullName.Replace('\\', '/').EndsWith("plugin.cfg", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName.Replace('\\', '/'))
                .Where(n => n.Equals("plugin.cfg", StringComparison.OrdinalIgnoreCase) || n.EndsWith("/plugin.cfg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Length)
                .FirstOrDefault(n => archive.GetEntry(n[..^"plugin.cfg".Length] + "yhde.gdextension") is not null);
            if (cfg is null)
                return "This is not the YHDE add-on: the zip needs the addons/yhde folder (plugin.cfg and yhde.gdextension).";
            var root = cfg[..^"plugin.cfg".Length];

            files = [];
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(root, StringComparison.Ordinal) || name.EndsWith('/')) continue;
                var relative = name[root.Length..];
                if (!IsSafeRelative(relative)) return $"The zip has a file with an unsafe name: {name}";
                total += entry.Length;
                if (total > MaxPackageBytes) return "The add-on is too large once unpacked.";
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                files.Add((Prefix + relative, ms.ToArray()));
            }
        }
        catch (InvalidDataException)
        {
            return "That file is not a zip.";
        }

        var pluginCfg = files.First(f => f.Path == Prefix + "plugin.cfg");
        var version = VersionOf(Encoding.UTF8.GetString(pluginCfg.Bytes));
        if (version is null) return "plugin.cfg has no version=\"…\" line.";
        var platforms = PlatformsOf(files.Select(f => f.Path));
        if (platforms.Length == 0)
            return "The add-on has no native core in addons/yhde/bin/ (build it first, e.g. libyhde.windows.editor.x86_64.dll).";

        var package = Pack(files);
        var manifest = new AddonManifest(version, Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
            package.LongLength, platforms, now);
        lock (_lock)
        {
            var tmp = Path.Combine(_dir, PendingFile + ".tmp");
            try
            {
                File.WriteAllBytes(tmp, package);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"The server could not save the add-on ({e.Message}).";
            }
            File.Move(tmp, Path.Combine(_dir, PendingFile), overwrite: true);
            var mtmp = Path.Combine(_dir, PendingManifestFile + ".tmp");
            File.WriteAllText(mtmp, JsonSerializer.Serialize(manifest, Json));
            File.Move(mtmp, Path.Combine(_dir, PendingManifestFile), overwrite: true);
            _pending = manifest;
        }
        return null;
    }

    // Makes the staged package the one everyone gets. False: nothing staged.
    public bool Release(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_pending is null) return false;
            var released = _pending with { Uploaded = now };
            File.Move(Path.Combine(_dir, PendingFile), PackagePath, overwrite: true);
            var mtmp = Path.Combine(_dir, ManifestFile + ".tmp");
            File.WriteAllText(mtmp, JsonSerializer.Serialize(released, Json));
            File.Move(mtmp, Path.Combine(_dir, ManifestFile), overwrite: true);
            File.Delete(Path.Combine(_dir, PendingManifestFile));
            _current = released;
            _pending = null;
            return true;
        }
    }

    public void Discard()
    {
        lock (_lock)
        {
            File.Delete(Path.Combine(_dir, PendingFile));
            File.Delete(Path.Combine(_dir, PendingManifestFile));
            _pending = null;
        }
    }

    // A new person's starting point for one project: an empty Godot project
    // named after the game, with YHDE turned on and a join file that fills in
    // the server address and invite code on first start (the project's own
    // files and settings then arrive from the server).
    public void WriteStarter(Stream output, string projectName, string serverUrl, string inviteCode)
    {
        var folder = FolderName(projectName);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, folder + "/project.godot", Encoding.UTF8.GetBytes(ProjectGodot(projectName)));
        Add(zip, folder + "/" + Prefix + "join.cfg", Encoding.UTF8.GetBytes(JoinCfg(serverUrl, inviteCode)));
        using var package = new ZipArchive(File.OpenRead(PackagePath), ZipArchiveMode.Read);
        foreach (var entry in package.Entries)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            Add(zip, folder + "/" + entry.FullName, ms.ToArray());
        }
    }

    // Windows cannot store \ / : * ? " < > | in names, nor a trailing dot or space.
    public static string FolderName(string projectName)
    {
        var cleaned = Regex.Replace(projectName, "[\\\\/:*?\"<>|\\x00-\\x1f]", "").Trim().TrimEnd('.').Trim();
        return cleaned.Length == 0 ? "YHDE project" : cleaned;
    }

    public static string ProjectGodot(string projectName) =>
        "; Engine configuration file.\n" +
        "; Made by the YHDE server: the project's own settings arrive when you connect.\n\n" +
        "config_version=5\n\n" +
        "[application]\n\n" +
        $"config/name=\"{GodotString(projectName)}\"\n" +
        "config/features=PackedStringArray(\"4.7\")\n\n" +
        "[editor_plugins]\n\n" +
        "enabled=PackedStringArray(\"res://addons/yhde/plugin.cfg\")\n";

    public static string JoinCfg(string serverUrl, string inviteCode) =>
        "; Read and deleted by YHDE the first time this project opens.\n" +
        "[join]\n\n" +
        $"url=\"{GodotString(serverUrl)}\"\n" +
        $"code=\"{GodotString(inviteCode)}\"\n";

    public static string? VersionOf(string pluginCfg)
    {
        var m = Regex.Match(pluginCfg, "^\\s*version\\s*=\\s*\"([^\"]{1,32})\"", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string[] PlatformsOf(IEnumerable<string> paths)
    {
        var bins = paths.Where(p => p.StartsWith(Prefix + "bin/", StringComparison.Ordinal)).Select(p => p.ToLowerInvariant()).ToList();
        var found = new List<string>();
        if (bins.Any(p => p.Contains(".windows.") && p.EndsWith(".dll"))) found.Add("windows");
        if (bins.Any(p => p.Contains(".linux.") && p.EndsWith(".so"))) found.Add("linux");
        if (bins.Any(p => p.Contains(".macos.") && (p.EndsWith(".dylib") || p.Contains(".framework/")))) found.Add("macos");
        return [.. found];
    }

    private static bool IsSafeRelative(string relative)
    {
        if (relative.Length == 0 || relative.StartsWith('/') || relative.Contains(':')) return false;
        return relative.Split('/').All(s => s.Length > 0 && s != "." && s != "..");
    }

    private static string GodotString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");

    private static byte[] Pack(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files.OrderBy(f => f.Path, StringComparer.Ordinal)) Add(zip, path, bytes);
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(bytes);
    }
}
