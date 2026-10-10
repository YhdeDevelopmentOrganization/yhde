namespace YHDE.Server.Gateway;

// What the server tells editors about itself in Welcome (network_protocol.md).
public static class ServerInfo
{
    // Matches the editor add-on release it was built with (plugin.cfg).
    public const string Version = "0.6.5";

    // Everything this server can do; editors hide what is missing.
    public static readonly string[] Capabilities = ["ops", "presence", "assets", "social"];
}
