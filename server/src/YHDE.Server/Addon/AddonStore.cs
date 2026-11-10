using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YHDE.Server.Assets;

namespace YHDE.Server.Addon;

public sealed record AddonManifest(string Version, string Sha256, long Size, string[] Platforms, DateTimeOffset Uploaded);

// What a starter project's join.cfg says. Code: an invite code (view links,
// the admin page). Ticket: a join ticket (edit links, a member's own
// download). Project and From name the game and who invited, for the panel's
// first words; an add-on that doesn't know them ignores them.
public sealed record JoinFile(string? Code = null, string? Ticket = null, string? Project = null, string? From = null);

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

    // Whether the released add-on reads join tickets (the first one that does
    // also has the first-run view, ui/first_run.gd). Until it is released an
    // older add-on would drop a ticket and leave the person outside the
    // project, so edit links are not offered (teams.md §4).
    public bool ReadsTickets
    {
        get
        {
            var current = Current;
            if (current is null) return false;
            lock (_lock)
            {
                if (_ticketsFor != current.Sha256)
                {
                    try
                    {
                        using var package = new ZipArchive(File.OpenRead(PackagePath), ZipArchiveMode.Read);
                        _readsTickets = package.GetEntry(Prefix + TicketMarker) is not null;
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        _readsTickets = false;
                    }
                    _ticketsFor = current.Sha256;
                }
                return _readsTickets;
            }
        }
    }

    public const string TicketMarker = "ui/first_run.gd";

    // The oldest Godot the released add-on runs on (compatibility_minimum in
    // yhde.gdextension), for the join page's words.
    // Add-ons before it was read say 4.7.
    public string OldestGodot
    {
        get
        {
            var current = Current;
            if (current is null) return DefaultOldestGodot;
            lock (_lock)
            {
                if (_godotFor != current.Sha256)
                {
                    _oldestGodot = DefaultOldestGodot;
                    try
                    {
                        using var package = new ZipArchive(File.OpenRead(PackagePath), ZipArchiveMode.Read);
                        if (package.GetEntry(Prefix + "yhde.gdextension") is { } e)
                        {
                            using var r = new StreamReader(e.Open());
                            _oldestGodot = CompatibilityMinimum(r.ReadToEnd()) ?? DefaultOldestGodot;
                        }
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                    }
                    _godotFor = current.Sha256;
                }
                return _oldestGodot;
            }
        }
    }

    public const string DefaultOldestGodot = "4.7";
    private string? _godotFor;
    private string _oldestGodot = DefaultOldestGodot;

    // "4.6" from compatibility_minimum = "4.6" (or "4.6.1"); only the major
    // and minor numbers, which is what Godot's config/features holds.
    public static string? CompatibilityMinimum(string gdextension)
    {
        var m = Regex.Match(gdextension, "^\\s*compatibility_minimum\\s*=\\s*\"?(\\d+)\\.(\\d+)", RegexOptions.Multiline);
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : null;
    }
    private string? _ticketsFor;
    private bool _readsTickets;

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
    // the server address and invite code or ticket on first start (the
    // project's own files and settings then arrive from the server).
    public void WriteStarter(Stream output, string projectName, string serverUrl, string inviteCode) =>
        WriteStarter(output, projectName, serverUrl, new JoinFile(Code: inviteCode));

    public void WriteStarter(Stream output, string projectName, string serverUrl, JoinFile join)
    {
        var folder = FolderName(projectName);
        var files = new List<(string Path, byte[] Bytes)>
        {
            (folder + "/project.godot", Encoding.UTF8.GetBytes(ProjectGodot(projectName))),
            (folder + "/" + Prefix + "join.cfg", Encoding.UTF8.GetBytes(JoinCfg(serverUrl, join))),
        };
        using (var package = new ZipArchive(File.OpenRead(PackagePath), ZipArchiveMode.Read))
        {
            foreach (var entry in package.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                files.Add((folder + "/" + entry.FullName, ms.ToArray()));
            }
        }
        // Every folder gets its own entry, before its files: Godot's Project
        // Manager imports a zip directly, but only makes the folders the zip
        // lists, and skips every file in a folder that isn't listed.
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var made = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
        {
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var dir = string.Join('/', parts[..i]) + "/";
                if (made.Add(dir)) zip.CreateEntry(dir).ExternalAttributes = UnixFolder;
            }
            Add(zip, path, bytes);
        }
    }

    // rwxr-xr-x folders and rw-r--r-- files, so unzip tools that read Unix
    // modes (macOS, Linux) make them readable.
    private const int UnixFolder = unchecked((int)0x41ED0000); // 040755 << 16
    private const int UnixFile = unchecked((int)0x81A40000);   // 100644 << 16

    // Windows cannot store \ / : * ? " < > | in names, nor a trailing dot or space.
    public static string FolderName(string projectName)
    {
        var cleaned = Regex.Replace(projectName, "[\\\\/:*?\"<>|\\x00-\\x1f]", "").Trim().TrimEnd('.').Trim();
        return cleaned.Length == 0 ? "YHDE project" : cleaned;
    }

    // No config/features: any version written here makes every other Godot
    // warn "Opening will change it to Godot 4.x" (checked on 4.6 and 4.7.2).
    // Without it Godot opens the starter quietly and writes in its own
    // version; the game's real settings arrive when it connects.
    public static string ProjectGodot(string projectName) =>
        "; Engine configuration file.\n" +
        "; Made by the YHDE server: the project's own settings arrive when you connect.\n\n" +
        "config_version=5\n\n" +
        "[application]\n\n" +
        $"config/name=\"{GodotString(projectName)}\"\n" +
        "\n" +
        "[editor_plugins]\n\n" +
        "enabled=PackedStringArray(\"res://addons/yhde/plugin.cfg\")\n";

    public static string JoinCfg(string serverUrl, string inviteCode) => JoinCfg(serverUrl, new JoinFile(Code: inviteCode));

    public static string JoinCfg(string serverUrl, JoinFile join)
    {
        var sb = new StringBuilder("; Read and deleted by YHDE the first time this project opens.\n[join]\n\n");
        sb.Append($"url=\"{GodotString(serverUrl)}\"\n");
        if (join.Code is { } code) sb.Append($"code=\"{GodotString(code)}\"\n");
        if (join.Ticket is { } ticket) sb.Append($"ticket=\"{GodotString(ticket)}\"\n");
        if (join.Project is { } project) sb.Append($"project=\"{GodotString(project)}\"\n");
        if (join.From is { } from) sb.Append($"from=\"{GodotString(from)}\"\n");
        return sb.ToString();
    }

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
        entry.ExternalAttributes = UnixFile;
        using var s = entry.Open();
        s.Write(bytes);
    }
}
