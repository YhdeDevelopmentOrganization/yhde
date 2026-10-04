using System.Text.Json;
using System.Text.RegularExpressions;

namespace YHDE.Server.Admin;

// Server updates approved on the admin page (deployment.md). The server
// kit's timer, on the host, looks for new commits on GitHub and writes
// status.json into Yhde:UpdatesDir; the page shows it. Approving a commit
// writes a request file there, which the timer applies (backup, build,
// health check, and a rollback if the new version does not come up). The
// server itself never runs git or Docker.
public sealed partial class ServerUpdates(IConfiguration config)
{
    private string? Dir => config["Yhde:UpdatesDir"] is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    public object Get()
    {
        var dir = Dir;
        if (dir is null) return new { configured = false };
        JsonElement? status = null;
        var file = Path.Combine(dir, "status.json");
        try
        {
            if (File.Exists(file)) status = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        var requested = ReadRequest(dir);
        return new { configured = true, status, requested, checkRequested = File.Exists(Path.Combine(dir, "check")) };
    }

    // Approve one of the newer commits the timer listed. Null: accepted.
    public string? Request(string? commit)
    {
        var dir = Dir;
        if (dir is null) return "Updates from the admin page need the server kit (./yhde update installs the update checker).";
        if (commit is null || !Sha().IsMatch(commit)) return "Choose one of the listed versions.";
        var listed = false;
        try
        {
            using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "status.json")));
            if (status.RootElement.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.Array)
                listed = available.EnumerateArray().Any(c => c.TryGetProperty("sha", out var s) && s.GetString() == commit);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return "The update checker has not reported yet. Wait a minute and refresh.";
        }
        if (!listed) return "That version is not in the list of updates.";
        Write(dir, "request", commit);
        return null;
    }

    public string? RequestCheck()
    {
        var dir = Dir;
        if (dir is null) return "Updates from the admin page need the server kit.";
        Write(dir, "check", DateTimeOffset.UtcNow.ToString("O"));
        return null;
    }

    private static string? ReadRequest(string dir)
    {
        try
        {
            var file = Path.Combine(dir, "request");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (IOException) { return null; }
    }

    private static void Write(string dir, string name, string text)
    {
        var tmp = Path.Combine(dir, name + ".tmp");
        File.WriteAllText(tmp, text);
        File.Move(tmp, Path.Combine(dir, name), overwrite: true);
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha();
}
