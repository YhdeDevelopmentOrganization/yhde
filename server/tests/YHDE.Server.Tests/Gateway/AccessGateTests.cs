using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using YHDE.Server.Gateway;

namespace YHDE.Server.Tests.Gateway;

// Unit tests for the shared access key checked on the WebSocket handshake.
public sealed class AccessGateTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static AccessGate Build(string? key, bool? allowAnonymous = null)
    {
        var values = new Dictionary<string, string?>();
        if (key is not null) values["Yhde:AccessKey"] = key;
        if (allowAnonymous is not null) values["Yhde:AllowAnonymous"] = allowAnonymous.Value ? "true" : "false";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AccessGate(config, NullLogger<AccessGate>.Instance);
    }

    [Fact]
    public void Refuses_to_start_without_a_key_unless_anonymous_is_explicit()
    {
        var act = () => Build(key: null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*AccessKey*");

        var blank = () => Build(key: "   ", allowAnonymous: false);
        blank.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Anonymous_mode_admits_everyone()
    {
        var gate = Build(key: "", allowAnonymous: true);

        gate.Required.Should().BeFalse();
        gate.Admits(null).Should().BeTrue();
        gate.Admits("Bearer anything").Should().BeTrue();
    }

    [Fact]
    public void Rejects_keys_that_are_too_short()
    {
        var act = () => Build(key: "short");
        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 16*");
    }

    [Fact]
    public void A_configured_key_wins_over_anonymous_mode()
    {
        var gate = Build(key: Key, allowAnonymous: true);

        gate.Required.Should().BeTrue();
        gate.Admits(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("Bearer " + Key, true)]
    [InlineData("bearer " + Key, true)]
    [InlineData("Bearer  " + Key + " ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(Key, false)]
    [InlineData("Basic " + Key, false)]
    [InlineData("Bearer " + Key + "x", false)]
    [InlineData("Bearer " + "0123456789abcdef", false)]
    [InlineData("Bearer ", false)]
    public void Checks_the_bearer_key(string? header, bool admitted)
    {
        Build(key: Key).Admits(header).Should().Be(admitted);
    }
}
