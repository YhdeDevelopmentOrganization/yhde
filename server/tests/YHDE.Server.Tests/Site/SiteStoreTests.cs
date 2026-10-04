using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using YHDE.Server.Site;

namespace YHDE.Server.Tests.Site;

// Checks that need no database.
public sealed class SiteRulesTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    public void Pictures_are_recognised_by_their_first_bytes(byte[] head, string type) =>
        SiteStore.SniffImage(head).Should().Be(type);

    [Fact]
    public void Svg_html_and_empty_uploads_are_not_pictures()
    {
        SiteStore.SniffImage("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8).Should().BeNull();
        SiteStore.SniffImage("<html>"u8).Should().BeNull();
        SiteStore.SniffImage([]).Should().BeNull();
    }

    [Theory]
    [InlineData("a@b.fi", "a@b.fi")]
    [InlineData("  Ada.Lovelace@Example.COM ", "ada.lovelace@example.com")]
    [InlineData("not an email", null)]
    [InlineData("a@b", null)]
    [InlineData("<script>@x.io", null)]
    public void Emails_are_normalised_or_refused(string input, string? expected) =>
        SiteStore.NormalizeEmail(input).Should().Be(expected);

    [Fact]
    public void Posts_are_checked()
    {
        SiteStore.PostInput Post(string kind = "news", string title = "Hello", string? version = null, string? cover = null, string status = "draft") =>
            new(kind, title, version, null, "", cover, status, null);
        SiteStore.Check(Post()).Should().BeNull();
        SiteStore.Check(Post(kind: "blog")).Should().NotBeNull();
        SiteStore.Check(Post(title: " ")).Should().NotBeNull();
        SiteStore.Check(Post(kind: "release")).Should().Contain("version");
        SiteStore.Check(Post(kind: "release", version: "0.4.0")).Should().BeNull();
        SiteStore.Check(Post(cover: "https://evil.example/x.png")).Should().NotBeNull();
        SiteStore.Check(Post(cover: "/media/" + new string('a', 64))).Should().BeNull();
        SiteStore.Check(Post(status: "live")).Should().NotBeNull();
    }

    [Fact]
    public void Announcement_links_stay_on_this_site_or_https()
    {
        static SiteSettings With(string link) => new(new Announcement(true, "Hi", link, "info"), new Maintenance(false, ""));
        SiteStore.Check(With("/changelog")).Should().BeNull();
        SiteStore.Check(With("https://discord.gg/x")).Should().BeNull();
        SiteStore.Check(With("javascript:alert(1)")).Should().NotBeNull();
        SiteStore.Check(With("http://plain.example")).Should().NotBeNull();
        SiteStore.Check(new SiteSettings(new Announcement(true, "", "", "info"), new Maintenance(false, ""))).Should().NotBeNull();
    }
}

// The SQL side against a real PostgreSQL (YHDE_TEST_DATABASE).
[Collection("integration")]
public sealed class SiteStoreIntegrationTests
{
    private static SiteStore? Open() => TestDatabase.Open() is { } db ? new SiteStore(db, NullLogger<SiteStore>.Instance) : null;

    [Fact]
    public async Task Only_published_posts_whose_time_has_come_are_live()
    {
        var site = Open();
        if (site is null) return;
        var tag = Guid.NewGuid().ToString("N")[..8];
        SiteStore.PostInput Input(string title, string status, DateTimeOffset? at) => new("news", title + " " + tag, null, "", "text", null, status, at);

        var draft = await site.SaveAsync(null, Input("draft", "draft", null), default);
        var now = await site.SaveAsync(null, Input("now", "published", null), default);
        var later = await site.SaveAsync(null, Input("later", "published", DateTimeOffset.UtcNow.AddDays(1)), default);
        var past = await site.SaveAsync(null, Input("past", "published", DateTimeOffset.UtcNow.AddDays(-1)), default);

        now.PublishAt.Should().NotBeNull("publishing without a time stamps it now");
        draft.PublishAt.Should().BeNull();

        var live = (await site.LiveAsync("news", default)).Where(p => p.Title.EndsWith(tag)).Select(p => p.Title).ToList();
        live.Should().Equal($"now {tag}", $"past {tag}");
        (await site.LiveAsync("release", default)).Should().NotContain(p => p.Title.EndsWith(tag));

        // Editing keeps it, deleting hides it everywhere.
        var edited = await site.SaveAsync(later.Id, Input("later", "published", DateTimeOffset.UtcNow.AddMinutes(-1)), default);
        edited.Id.Should().Be(later.Id);
        (await site.LiveAsync("news", default)).Should().Contain(p => p.Id == later.Id);
        (await site.DeleteAsync(later.Id, default)).Should().BeTrue();
        (await site.DeleteAsync(later.Id, default)).Should().BeFalse();
        (await site.AllAsync(default)).Should().NotContain(p => p.Id == later.Id);
        await FluentActions.Awaiting(() => site.SaveAsync(later.Id, Input("x", "draft", null), default)).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        var site = Open();
        if (site is null) return;
        var s = new SiteSettings(new Announcement(true, "Beta 0.4 is out", "/changelog", "warning"), new Maintenance(false, "Back at 22:30"));
        await site.SaveSettingsAsync(s, default);
        var fresh = new SiteStore(TestDatabase.Open()!, NullLogger<SiteStore>.Instance);
        (await fresh.SettingsAsync(default)).Should().BeEquivalentTo(s);
        await site.SaveSettingsAsync(new SiteSettings(new Announcement(false, "", "", "info"), new Maintenance(false, "")), default);
    }

    [Fact]
    public async Task Early_access_keeps_one_row_per_address_and_can_forget_it()
    {
        var site = Open();
        if (site is null) return;
        var email = $"t{Guid.NewGuid():N}@example.com";
        (await site.AddEarlyAccessAsync(email, "home", default)).Should().BeTrue();
        (await site.AddEarlyAccessAsync(email, "home", default)).Should().BeFalse();
        (await site.EarlyAccessAsync(default)).Count(e => e.Email == email).Should().Be(1);
        (await site.RemoveEarlyAccessAsync(email, default)).Should().BeTrue();
        (await site.EarlyAccessAsync(default)).Should().NotContain(e => e.Email == email);
    }

    [Fact]
    public async Task Media_is_listed_once_per_picture()
    {
        var site = Open();
        if (site is null) return;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()));
        var a = await site.AddMediaAsync(hash, "image/png", 123, "cover.png", default);
        await site.AddMediaAsync(hash, "image/png", 123, "again.png", default);
        a.Url.Should().Be("/media/" + hash);
        (await site.MediaAsync(hash, default))!.Name.Should().Be("cover.png");
        (await site.AllMediaAsync(default)).Count(m => m.Hash == hash).Should().Be(1);
    }
}
