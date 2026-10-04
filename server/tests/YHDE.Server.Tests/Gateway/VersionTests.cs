using System.Text.RegularExpressions;
using YHDE.Server.Gateway;

namespace YHDE.Server.Tests.Gateway;

// The server, the add-on and its native core report one version. Editors
// compare the server's with their own and warn when they differ, so a missed
// bump shows every user a wrong warning.
public sealed class VersionTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "client", "godot", "addons", "yhde", "plugin.cfg")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Server_add_on_and_native_core_have_the_same_version()
    {
        var root = Repo();
        var plugin = File.ReadAllText(Path.Combine(root, "client", "godot", "addons", "yhde", "plugin.cfg"));
        var core = File.ReadAllText(Path.Combine(root, "client", "gdextension", "src", "yhde_session.cpp"));

        Assert.Equal(ServerInfo.Version, Regex.Match(plugin, "version=\"([^\"]+)\"").Groups[1].Value);
        Assert.Equal(ServerInfo.Version, Regex.Match(core, "kAddonVersion = \"([^\"]+)\"").Groups[1].Value);
    }
}
