using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using YHDE.Server.Accounts;
using YHDE.Server.Gateway;
using YHDE.Server.Teams;

namespace YHDE.Server.Tests.Accounts;

public sealed class EditorSignInTests
{
    [Fact]
    public void A_team_grant_opens_only_the_teams_projects()
    {
        var mine = Guid.NewGuid();
        var grant = new AccessGrant((Guid?)null) { Projects = new HashSet<Guid> { mine }, UserId = Guid.NewGuid(), UserName = "Ada" };
        grant.Allows(mine).Should().BeTrue();
        grant.Allows(Guid.NewGuid()).Should().BeFalse();
        new AccessGrant((Guid?)null) { Projects = new HashSet<Guid>() }.Allows(mine).Should().BeFalse("no team, no projects");
        AccessGrant.AllProjects.Allows(mine).Should().BeTrue();
        new AccessGrant(mine).Allows(Guid.NewGuid()).Should().BeFalse();
    }

    [Theory]
    [InlineData("abcd-efgh", "ABCD-EFGH")]
    [InlineData(" abcdefgh ", "ABCD-EFGH")]
    [InlineData("ABCD-EFG", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void User_codes_are_forgiving_to_type(string? input, string expected) =>
        DeviceEndpoints.NormalizeUserCode(input).Should().Be(expected);

    [Fact]
    public void Only_sign_in_tokens_are_looked_up_as_accounts()
    {
        EditorTokens.LooksLikeToken(Secrets.NewToken()).Should().BeTrue();
        EditorTokens.LooksLikeToken("YHDE-ABCD-EFGH-JKMN-PQRS").Should().BeFalse();
        EditorTokens.LooksLikeToken("short").Should().BeFalse();
        EditorTokens.LooksLikeToken(new string('a', 42) + "!").Should().BeFalse();
    }

    // Against a real PostgreSQL when YHDE_TEST_DATABASE is set.
    [Fact]
    public async Task An_editor_token_admits_its_teams_projects_as_its_account()
    {
        if (TestDatabase.Open() is not { } db) return;
        var accounts = new AccountStore(db);
        var teams = new TeamStore(db);
        var projects = new YHDE.Server.Projects.ProjectStore(db);
        var ada = (await accounts.CreateAsync($"t{Guid.NewGuid():N}@example.com", "Ada", null, verified: true, default))!;
        var team = await teams.CreateAsync(ada.Id, "Team " + Guid.NewGuid().ToString("N")[..6], Plans.Find("team")!, "month", default);
        var project = await projects.CreateAsync("Game", default);
        await teams.AttachProjectAsync(project.ProjectId, team.Id, default);
        var other = await projects.CreateAsync("Someone else's", default);

        var token = await accounts.CreateSessionAsync(ada.Id, "editor", "Godot", "127.0.0.1", TimeSpan.FromHours(1), default);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Yhde:AccessKey"] = "server-key-0123456789" }).Build();
        var gate = new AccessGate(config, NullLogger<AccessGate>.Instance, projects, new EditorTokens(accounts, teams));

        var grant = await gate.AdmitAsync("Bearer " + token, default);
        grant.Should().NotBeNull();
        grant!.UserId.Should().Be(ada.Id);
        grant.UserName.Should().Be("Ada");
        grant.Allows(project.ProjectId).Should().BeTrue();
        grant.Allows(other.ProjectId).Should().BeFalse();

        // A web sign-in is not an editor sign-in; an ended one admits nothing.
        var web = await accounts.CreateSessionAsync(ada.Id, "web", "Browser", "127.0.0.1", TimeSpan.FromHours(1), default);
        (await gate.AdmitAsync("Bearer " + web, default)).Should().BeNull();
        await accounts.RevokeTokenAsync(token, default);
        gate.ForgetInvites();
        (await gate.AdmitAsync("Bearer " + token, default)).Should().BeNull();
    }
}
