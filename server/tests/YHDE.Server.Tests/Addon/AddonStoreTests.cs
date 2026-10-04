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
    public void Platforms_are_read_from_the_native_core_files()
    {
        Assert.Equal(["windows", "linux", "macos"], AddonStore.PlatformsOf([
            "addons/yhde/bin/libyhde.windows.editor.x86_64.dll",
            "addons/yhde/bin/libyhde.linux.editor.x86_64.so",
            "addons/yhde/bin/libyhde.macos.editor.universal.dylib",
        ]));
    }
}
