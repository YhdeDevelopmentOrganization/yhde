namespace YHDE.Server.Gateway;

// What the server tells editors about itself in Welcome (network_protocol.md).
public static class ServerInfo
{
    // Matches the editor add-on release it was built with (plugin.cfg).
    public const string Version = "0.7.4";

    // Older add-ons may sign in, look and be told to update, but not change a
    // project: those before 0.7.4 can delete files for the team that they only
    // failed to write (assets.md).
    public const string MinEditVersion = "0.7.4";

    public static bool MayEdit(string clientVersion) =>
        System.Version.TryParse(clientVersion, out var v) && v >= System.Version.Parse(MinEditVersion);

    // Everything this server can do; editors hide what is missing.
    public static readonly string[] Capabilities = ["ops", "presence", "assets", "social"];
}
