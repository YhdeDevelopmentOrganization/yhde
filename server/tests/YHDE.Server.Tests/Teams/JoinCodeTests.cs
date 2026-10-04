using FluentAssertions;
using YHDE.Server.Teams;

namespace YHDE.Server.Tests.Teams;

public sealed class JoinCodeTests
{
    [Fact]
    public void New_codes_read_back_as_themselves()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = JoinCode.New();
            code.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$");
            JoinCode.Normalize(code).Should().Be(code);
        }
    }

    [Theory]
    [InlineData("k7qm-2xrd", "K7QM-2XRD")]
    [InlineData(" K7QM 2XRD ", "K7QM-2XRD")]
    [InlineData("K7QM2XRD", "K7QM-2XRD")]
    [InlineData("OIL0-1234", "0110-1234")]
    public void Typing_slips_are_forgiven(string typed, string code) =>
        JoinCode.Normalize(typed).Should().Be(code);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K7QM-2XR")]
    [InlineData("K7QM-2XRDA")]
    [InlineData("K7QM-2XR!")]
    [InlineData("YHDE-AAAA-BBBB-CCCC-DDDD")]
    public void Anything_else_is_not_a_code(string? typed) =>
        JoinCode.Normalize(typed).Should().BeNull();
}
