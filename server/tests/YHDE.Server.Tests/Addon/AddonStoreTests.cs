using System.IO.Compression;
using System.Text;
using YHDE.Server.Addon;

namespace YHDE.Server.Tests.Addon;

public sealed class AddonStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yhde-addon-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return ms.ToArray();
    }

    private static (string, string)[] Addon(string root, string version = "0.3.0") =>
    [
        (root + "plugin.cfg", $"[plugin]\nname=\"YHDE\"\nversion=\"{version}\"\nscript=\"plugin.gd\"\n"),
        (root + "yhde.gdextension", "[configuration]\n"),
        (root + "plugin.gd", "@tool\nextends EditorPlugin\n"),
        (root + "bin/libyhde.windows.editor.x86_64.dll", "MZ"),
    ];

    private static Dictionary<string, string> Read(string zipPath) => Read(File.ReadAllBytes(zipPath));

    private static Dictionary<string, string> Read(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        });
    }

    [Theory]
    [InlineData("addons/yhde/")]
    [InlineData("yhde/")]
    [InlineData("")]
    [InlineData("YHDE-0.3.0/addons/yhde/")]
    public void Accepts_the_add_on_in_any_folder_and_stores_it_under_addons_yhde(string root)
    {
        var store = new AddonStore(_dir);
        var error = store.Install(Zip([.. Addon(root), ("README.txt", "not part of the add-on")]), DateTimeOffset.UnixEpoch);

        Assert.Null(error);
        Assert.Null(store.Current); // staged until released
        Assert.Equal("0.3.0", store.Pending!.Version);
        Assert.True(store.Release(DateTimeOffset.UnixEpoch));
        Assert.Null(store.Pending);
        Assert.Equal("0.3.0", store.Current!.Version);
        Assert.Equal(["windows"], store.Current.Platforms);
        var files = Read(store.PackagePath);
        Assert.Contains("addons/yhde/plugin.cfg", files.Keys);
        Assert.Contains("addons/yhde/bin/libyhde.windows.editor.x86_64.dll", files.Keys);
        Assert.All(files.Keys, k => Assert.StartsWith("addons/yhde/", k));
        // Files outside the add-on's folder are left out (at the root, everything is the add-on).
        if (root != "") Assert.DoesNotContain("addons/yhde/README.txt", files.Keys);
    }

    [Fact]
    public void Survives_a_restart()
    {
        var store = new AddonStore(_dir);
        store.Install(Zip(Addon("addons/yhde/", "1.2.3")), DateTimeOffset.UnixEpoch);
        Assert.Equal("1.2.3", new AddonStore(_dir).Pending!.Version); // a staged upload survives too
        store.Release(DateTimeOffset.UnixEpoch);
        Assert.Equal("1.2.3", new AddonStore(_dir).Current!.Version);
    }

    [Fact]
    public void A_new_upload_waits_while_the_released_one_stays()
    {
        var store = new AddonStore(_dir);
        store.Install(Zip(Addon("addons/yhde/", "1.0.0")), DateTimeOffset.UnixEpoch);
        store.Release(DateTimeOffset.UnixEpoch);
        store.Install(Zip(Addon("addons/yhde/", "1.1.0")), DateTimeOffset.UnixEpoch);
        Assert.Equal("1.0.0", store.Current!.Version);
        Assert.Contains("version=\"1.0.0\"", Read(store.PackagePath)["addons/yhde/plugin.cfg"]);
        store.Discard();
        Assert.Null(store.Pending);
        Assert.False(store.Release(DateTimeOffset.UnixEpoch));
        Assert.Equal("1.0.0", store.Current!.Version);
    }

    [Fact]
    public void Refuses_a_zip_that_is_not_the_add_on()
    {
        var store = new AddonStore(_dir);
        Assert.NotNull(store.Install(Zip(("project.godot", "config_version=5")), DateTimeOffset.UnixEpoch));
        Assert.NotNull(store.Install(Encoding.UTF8.GetBytes("not a zip"), DateTimeOffset.UnixEpoch));
        Assert.Null(store.Current);
    }

    [Fact]
    public void Refuses_an_add_on_without_its_native_core()
    {
        var store = new AddonStore(_dir);
        var error = store.Install(Zip(Addon("addons/yhde/").Where(f => !f.Item1.Contains("/bin/")).ToArray()), DateTimeOffset.UnixEpoch);
        Assert.Contains("native core", error);
    }

    [Fact]
    public void Refuses_unsafe_file_names()
    {
        var store = new AddonStore(_dir);
        var error = store.Install(Zip([.. Addon("addons/yhde/"), ("addons/yhde/../../evil.gd", "x")]), DateTimeOffset.UnixEpoch);
        Assert.NotNull(error);
        Assert.Null(store.Current);
    }

    [Fact]
    public void A_starter_project_is_named_after_the_game_and_joins_it_on_first_start()
    {
        var store = new AddonStore(_dir);
        store.Install(Zip(Addon("addons/yhde/")), DateTimeOffset.UnixEpoch);
        store.Release(DateTimeOffset.UnixEpoch);
        using var ms = new MemoryStream();
        store.WriteStarter(ms, "What?! \"Game\"", "wss://example.com/ws", "YHDE-AAAA-BBBB-CCCC-DDDD");

        var files = Read(ms.ToArray());
        const string folder = "What! Game/"; // no characters Windows refuses
        Assert.Contains(folder + "addons/yhde/plugin.cfg", files.Keys);
        Assert.Contains(folder + "addons/yhde/bin/libyhde.windows.editor.x86_64.dll", files.Keys);
        var project = files[folder + "project.godot"];
        Assert.Contains("config/name=\"What?! \\\"Game\\\"\"", project);
        Assert.Contains("res://addons/yhde/plugin.cfg", project);
        var join = files[folder + "addons/yhde/join.cfg"];
        Assert.Contains("url=\"wss://example.com/ws\"", join);
        Assert.Contains("code=\"YHDE-AAAA-BBBB-CCCC-DDDD\"", join);
    }

    [Fact]
    public void A_starter_lists_every_folder_before_its_files()
    {
        // Godot's Project Manager imports a zip directly, but skips every file
        // in a folder the zip doesn't list (checked on Godot 4.7.2).
        var store = new AddonStore(_dir);
        store.Install(Zip(Addon("addons/yhde/")), DateTimeOffset.UnixEpoch);
        store.Release(DateTimeOffset.UnixEpoch);
        using var ms = new MemoryStream();
        store.WriteStarter(ms, "Skyhop", "wss://example.com/ws", "YHDE-AAAA-BBBB-CCCC-DDDD");

        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read);
        var seen = new HashSet<string>();
        foreach (var e in zip.Entries)
        {
            var parent = e.FullName.TrimEnd('/');
            parent = parent.Contains('/') ? parent[..(parent.LastIndexOf('/') + 1)] : "";
            if (parent.Length > 0) Assert.Contains(parent, seen);
            if (e.FullName.EndsWith('/')) seen.Add(e.FullName);
        }
        Assert.Equal(["Skyhop/", "Skyhop/addons/", "Skyhop/addons/yhde/", "Skyhop/addons/yhde/bin/"], seen.Order());
        Assert.All(zip.Entries, e => Assert.NotEqual(0, (e.ExternalAttributes >> 16) & 0x1FF));
    }

    [Fact]
    public void A_starter_from_an_edit_link_carries_a_ticket_instead_of_a_code()
    {
        var store = new AddonStore(_dir);
        store.Install(Zip(Addon("addons/yhde/")), DateTimeOffset.UnixEpoch);
        store.Release(DateTimeOffset.UnixEpoch);
        using var ms = new MemoryStream();
        store.WriteStarter(ms, "Skyhop", "wss://example.com/ws", new JoinFile(Ticket: "t1", Project: "Skyhop", From: "Alex \"A\""));

        var join = Read(ms.ToArray())["Skyhop/addons/yhde/join.cfg"];
        Assert.Contains("ticket=\"t1\"", join);
        Assert.Contains("project=\"Skyhop\"", join);
        Assert.Contains("from=\"Alex \\\"A\\\"\"", join);
        Assert.DoesNotContain("code=", join);
    }

    [Fact]
    public void Edit_links_wait_for_an_addon_that_reads_tickets()
    {
        var store = new AddonStore(_dir);
        Assert.False(store.ReadsTickets);
        store.Install(Zip(Addon("addons/yhde/")), DateTimeOffset.UnixEpoch);
        store.Release(DateTimeOffset.UnixEpoch);
        Assert.False(store.ReadsTickets);
        store.Install(Zip([.. Addon("addons/yhde/", "0.7.0"), ("addons/yhde/" + AddonStore.TicketMarker, "@tool\nextends VBoxContainer\n")]), DateTimeOffset.UnixEpoch);
        Assert.False(store.ReadsTickets, "only the released add-on counts");
        store.Release(DateTimeOffset.UnixEpoch);
        Assert.True(store.ReadsTickets);
    }

    [Fact]
    public void A_starter_names_no_godot_version()
    {
        // Any version there makes every other Godot ask to "change it to 4.x".
        Assert.DoesNotContain("config/features", AddonStore.ProjectGodot("Skyhop"));
    }

    [Theory]
    [InlineData("[configuration]\nentry_symbol = \"yhde_init\"\ncompatibility_minimum = \"4.6\"\n", "4.6")]
    [InlineData("compatibility_minimum = 4.7.2\n", "4.7")]
    [InlineData("[configuration]\n", null)]
    public void The_oldest_godot_is_read_from_the_gdextension(string text, string? oldest) =>
        Assert.Equal(oldest, AddonStore.CompatibilityMinimum(text));

    [Fact]
    public void The_join_page_says_which_godot_the_released_addon_needs()
    {
        var store = new AddonStore(_dir);
        Assert.Equal("4.7", store.OldestGodot);
        store.Install(Zip([.. Addon("addons/yhde/").Where(f => f.Item1 != "addons/yhde/yhde.gdextension"),
            ("addons/yhde/yhde.gdextension", "[configuration]\ncompatibility_minimum = \"4.6\"\n")]), DateTimeOffset.UnixEpoch);
        Assert.Equal("4.7", store.OldestGodot);
        store.Release(DateTimeOffset.UnixEpoch);
        Assert.Equal("4.6", store.OldestGodot);
    }

    [Fact]
    public void Platforms_are_read_from_the_native_core_files()
    {
        Assert.Equal(["windows", "linux", "macos"], AddonStore.PlatformsOf([
            "addons/yhde/bin/libyhde.windows.editor.x86_64.dll",
            "addons/yhde/bin/libyhde.linux.editor.x86_64.so",
            "addons/yhde/bin/libyhde.macos.editor.universal.dylib",
        ]));
    }
}
