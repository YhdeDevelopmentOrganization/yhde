using FluentAssertions;
using YHDE.Server.Accounts;
using YHDE.Server.Presence;

namespace YHDE.Server.Tests.Accounts;

public sealed class NameRulesTests
{
    [Theory]
    [InlineData("  Ada   Lovelace ", "Ada Lovelace")]
    [InlineData("Ada\r\nBcc: x@example.com", "Ada Bcc: x@example.com")]
    [InlineData("Ad​a", "Ada")]                  // zero-width space
    [InlineData("‮gpj.exe", "gpj.exe")]          // right-to-left override
    [InlineData("Юлия", "Юлия")]
    [InlineData("Mäkinen 🎮", "Mäkinen 🎮")]
    public void Names_are_cleaned(string input, string expected) =>
        NameRules.Clean(input, NameRules.Kind.Person).Should().Be(expected);

    [Fact]
    public void Long_names_are_cut_on_whole_characters()
    {
        var name = NameRules.Clean(new string('a', 47) + "🎮🎮", NameRules.Kind.Person);
        name.Should().Be(new string('a', 47) + "🎮");
    }

    [Theory]
    [InlineData("Ada Lovelace")]
    [InlineData("Night Owl Games")]
    [InlineData("Юлия")]
    [InlineData("Team Rocket")]
    [InlineData("Maya from Helsinki")]
    public void Ordinary_names_pass(string name) =>
        NameRules.Problem(NameRules.Clean(name, NameRules.Kind.Person), NameRules.Kind.Person).Should().BeNull();

    [Theory]
    [InlineData("Admin")]
    [InlineData("ADM1N")]
    [InlineData("a.d.m.i.n")]
    [InlineData("Support")]
    [InlineData("Supp0rt")]
    [InlineData("YHDE")]
    [InlineData("YHDE Team")]
    [InlineData("Y.H.D.E support")]
    [InlineData("Official")]
    [InlineData("аdmin")]                              // Cyrillic а
    [InlineData("Visit evil.com")]
    [InlineData("https://example.org")]
    [InlineData("me@example.com")]
    [InlineData("www.free-seats")]
    [InlineData("!!!")]
    public void Official_looking_names_and_links_are_refused(string name) =>
        NameRules.Problem(NameRules.Clean(name, NameRules.Kind.Person), NameRules.Kind.Person).Should().NotBeNull();

    [Fact]
    public void Projects_and_labels_may_use_any_words()
    {
        NameRules.Problem("YHDE test", NameRules.Kind.Project).Should().BeNull();
        NameRules.Problem("For admin@studio", NameRules.Kind.Label).Should().BeNull();
    }

    [Theory]
    [InlineData("Maya", "Maya")]
    [InlineData("", "Anonymous")]
    [InlineData("Admin", "Guest")]
    [InlineData("YHDE Support", "Guest")]
    public void Editor_names_never_look_official(string input, string expected) =>
        PresenceService.SanitizeName(input).Should().Be(expected);

    [Fact]
    public void Chat_text_loses_direction_overrides_but_keeps_emoji()
    {
        PresenceService.SanitizeText("hi ‮olleh", 100).Should().Be("hi olleh");
        PresenceService.SanitizeText("👨‍👩‍👧 family", 100).Should().Be("👨‍👩‍👧 family");
    }
}

public sealed class LimiterTests
{
    [Fact]
    public void Counts_per_key_and_cannot_be_wiped_by_many_other_keys()
    {
        var limiter = new Limiter(3, TimeSpan.FromMinutes(10), maxKeys: 1_000);
        for (var i = 0; i < 3; i++) limiter.Try("victim").Should().BeTrue();
        limiter.Try("victim").Should().BeFalse();

        // An attacker trying lots of other keys used to clear every counter.
        for (var i = 0; i < 5_000; i++) limiter.Try("junk" + i);

        limiter.Try("victim").Should().BeFalse("the victim's count survives");
        limiter.Try("brand-new").Should().BeFalse("a full table refuses new keys until old ones expire");
    }

    [Fact]
    public void A_window_that_has_passed_starts_over()
    {
        // A whole second, so a busy test machine can't stretch the gap
        // between the first two tries past the window.
        var limiter = new Limiter(1, TimeSpan.FromSeconds(1));
        limiter.Try("k").Should().BeTrue();
        limiter.Try("k").Should().BeFalse();
        Thread.Sleep(1100);
        limiter.Try("k").Should().BeTrue();
    }
}

public sealed class SafeReturnTests
{
    [Theory]
    [InlineData("/app#/projects", "/app#/projects")]
    [InlineData("/app#/account", "/app#/account")]
    [InlineData("//evil.com", "/app#/projects")]
    [InlineData("/\\evil.com", "/app#/projects")]
    [InlineData("/\t/evil.com", "/app#/projects")]
    [InlineData("/ /evil.com", "/app#/projects")]
    [InlineData("https://evil.com", "/app#/projects")]
    [InlineData("", "/app#/projects")]
    [InlineData(null, "/app#/projects")]
    public void Only_paths_on_this_site(string? input, string expected) =>
        AuthEndpoints.SafeReturn(input).Should().Be(expected);
}
