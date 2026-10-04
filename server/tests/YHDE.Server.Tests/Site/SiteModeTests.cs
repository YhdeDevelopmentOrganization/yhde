using FluentAssertions;
using Microsoft.Extensions.Configuration;
using YHDE.Server.Site;

namespace YHDE.Server.Tests.Site;

// Official site or self-hosted server, and the operator's details (SiteMode).
public sealed class SiteModeTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void A_server_is_self_hosted_unless_it_says_it_is_the_official_site()
    {
        SiteMode.Parse(Config()).Official.Should().BeFalse();
        SiteMode.Parse(Config(("Yhde:Site:Official", "true"))).Official.Should().BeTrue();
    }

    [Fact]
    public void The_operators_details_are_read_and_cleaned()
    {
        var (_, op) = SiteMode.Parse(Config(
            ("Yhde:Operator:Name", "  Pixel ‮Studio\n  "),
            ("Yhde:Operator:Email", "admin@pixel.example"),
            ("Yhde:Operator:PrivacyUrl", "https://pixel.example/privacy"),
            ("Yhde:Operator:TermsUrl", "http://pixel.example/terms")));
        op.Name.Should().Be("Pixel Studio");
        op.Email.Should().Be("admin@pixel.example");
        op.PrivacyUrl.Should().Be("https://pixel.example/privacy");
        op.TermsUrl.Should().Be("http://pixel.example/terms");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("/privacy")]
    [InlineData("ftp://pixel.example/terms")]
    public void Only_web_links_are_accepted_for_legal_pages(string url) =>
        SiteMode.Parse(Config(("Yhde:Operator:PrivacyUrl", url))).Operator.PrivacyUrl.Should().BeEmpty();

    [Theory]
    [InlineData("not an email")]
    [InlineData("<script>@x")]
    [InlineData("a\"b@c")]
    public void Odd_contact_addresses_are_dropped(string email) =>
        SiteMode.Parse(Config(("Yhde:Operator:Email", email))).Operator.Email.Should().BeEmpty();
}
