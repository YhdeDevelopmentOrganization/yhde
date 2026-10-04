using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using YHDE.Server.Accounts;
using YHDE.Server.Gateway;
using YHDE.Server.Projects;
using YHDE.Server.Teams;

namespace YHDE.Server.Tests.Teams;

// People belong to projects (teams.md): an owner's projects, and the people
// invited into each one.
[Collection("integration")]
public sealed class TeamStoreTests
{
    private static (TeamStore Teams, AccountStore Accounts, ProjectStore Projects)? Open() =>
        TestDatabase.Open() is { } db ? (new TeamStore(db), new AccountStore(db), new ProjectStore(db)) : null;

    private static async Task<User> NewUserAsync(AccountStore accounts, string name) =>
        (await accounts.CreateAsync($"t{Guid.NewGuid():N}@example.com", name, null, verified: true, default))!;

    private static async Task<(Team Team, Guid Project)> OwnerWithProjectAsync(TeamStore teams, ProjectStore projects, User owner, string name = "Skyward")
    {
        var team = await teams.OwnedAsync(owner.Id, default) ?? await teams.CreateAsync(owner.Id, owner.Name, Plans.Beta, "month", default);
        var p = await projects.CreateAsync(name, default);
        await teams.AttachProjectAsync(p.ProjectId, team.Id, default);
        return (team, p.ProjectId);
    }

    [Fact]
    public async Task Someone_invited_into_projects_of_two_owners_is_in_both()
    {
        if (Open() is not var (teams, accounts, projects)) return;
        var ada = await NewUserAsync(accounts, "Ada");
        var cy = await NewUserAsync(accounts, "Cy");
        var bo = await NewUserAsync(accounts, "Bo");
        var (adaTeam, sky) = await OwnerWithProjectAsync(teams, projects, ada);
        var (cyTeam, tide) = await OwnerWithProjectAsync(teams, projects, cy, "Tide");

        (await teams.OwnedAsync(bo.Id, default)).Should().BeNull("being invited never needs an access code");
        var (toSky, _) = await teams.InviteAsync(sky, adaTeam.Id, bo.Email, ada.Id, default);
        var (toTide, _) = await teams.InviteAsync(tide, cyTeam.Id, bo.Email, cy.Id, default);
        (await teams.InvitationsForAsync(bo.Email, default)).Select(i => i.Project).Should().BeEquivalentTo(["Skyward", "Tide"]);

        (await teams.AcceptAsync(toSky.Id, bo.Id, "someone-else@example.com", default)).Should().BeNull("only the invited address can accept");
        (await teams.AcceptAsync(toSky.Id, bo.Id, bo.Email, default)).Should().Be(sky);
        (await teams.AcceptAsync(toSky.Id, bo.Id, bo.Email, default)).Should().BeNull("an invitation works once");
        (await teams.AcceptAsync(toTide.Id, bo.Id, bo.Email, default)).Should().Be(tide);

        var mine = await teams.ProjectsForAsync(bo.Id, default);
        mine.Select(m => (m.ProjectId, m.Role, m.Access)).Should().BeEquivalentTo([(sky, "member", "edit"), (tide, "member", "edit")]);
        (await teams.MembersAsync(sky, default)).Select(m => (m.Name, m.Role)).Should().Equal(("Ada", "owner"), ("Bo", "member"));
        (await teams.ProjectsForAsync(ada.Id, default)).Should().ContainSingle().Which.Role.Should().Be("owner");

        (await teams.RemoveMemberAsync(sky, bo.Id, default)).Should().BeTrue();
        (await teams.ProjectsForAsync(bo.Id, default)).Should().ContainSingle().Which.ProjectId.Should().Be(tide, "leaving one project keeps the others");
    }

    [Fact]
    public async Task Members_can_be_made_view_only_and_view_link_codes_only_watch()
    {
        if (Open() is not var (teams, accounts, projects)) return;
        var owner = await NewUserAsync(accounts, "Owner");
        var viewer = await NewUserAsync(accounts, "Viewer");
        var (team, game) = await OwnerWithProjectAsync(teams, projects, owner);
        await teams.AddMemberAsync(game, viewer.Id, "edit", default);
        (await teams.SetAccessAsync(game, viewer.Id, "view", default)).Should().BeTrue();
        (await teams.SetAccessAsync(game, owner.Id, "view", default)).Should().BeFalse("the owner always edits");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Yhde:AccessKey"] = "server-key-0123456789" }).Build();
        var gate = new AccessGate(config, NullLogger<AccessGate>.Instance, projects, new EditorTokens(accounts, teams));
        async Task<AccessGrant> Grant(User u) =>
            (await gate.AdmitAsync("Bearer " + await accounts.CreateSessionAsync(u.Id, "editor", "Godot", "127.0.0.1", TimeSpan.FromHours(1), default), default))!;

        var v = await Grant(viewer);
        v.Allows(game).Should().BeTrue();
        v.CanEdit(game).Should().BeFalse("view only");
        (await Grant(owner)).CanEdit(game).Should().BeTrue();

        // A code made by a view link watches; one made on the admin page edits.
        var (link, _) = await projects.CreateLinkAsync(game, "Playtest", null, 2, default);
        var (_, viewCode) = await projects.CreateInviteAsync(game, "Playtest · download 1", default, link.LinkId);
        var (_, editCode) = await projects.CreateInviteAsync(game, "Staff", default);
        var watching = (await gate.AdmitAsync("Bearer " + viewCode, default))!;
        watching.Allows(game).Should().BeTrue();
        watching.CanEdit(game).Should().BeFalse();
        (await gate.AdmitAsync("Bearer " + editCode, default))!.CanEdit(game).Should().BeTrue();
        (await teams.ViewersAsync([game], default)).Should().Contain(game, 2, "a link holds its downloads until it is turned off");

        await teams.RevokeLinkCodesAsync(link.LinkId, default);
        await projects.RevokeLinkAsync(link.LinkId, default);
        gate.ForgetInvites();
        (await gate.AdmitAsync("Bearer " + viewCode, default)).Should().BeNull("turning a link off cuts off who used it");
        (await teams.ViewersAsync([game], default)).Should().NotContainKey(game);
    }

    [Fact]
    public async Task The_emails_link_opens_the_invitation_and_can_be_resent()
    {
        if (Open() is not var (teams, accounts, projects)) return;
        var ada = await NewUserAsync(accounts, "Ada");
        var bo = await NewUserAsync(accounts, "Bo");
        var (team, sky) = await OwnerWithProjectAsync(teams, projects, ada);
        var (invite, first) = await teams.InviteAsync(sky, team.Id, "not-a-real-inbox@example.com", ada.Id, default);

        (await teams.InvitationByTokenAsync("wrong", default)).Should().BeNull();
        var found = (await teams.InvitationByTokenAsync(first, default))!;
        found.Should().Match<Invitation>(i => i.Id == invite.Id && i.Project == "Skyward" && i.From == "Ada" && i.FromEmail == ada.Email && i.To == "not-a-real-inbox@example.com");

        var (_, second) = (await teams.ResendAsync(invite.Id, sky, default))!.Value;
        (await teams.InvitationByTokenAsync(first, default)).Should().BeNull("the old link stops working");
        (await teams.ResendAsync(invite.Id, Guid.NewGuid(), default)).Should().BeNull("another project can't resend it");

        (await teams.AcceptAsync(invite.Id, bo.Id, null, default)).Should().Be(sky, "the link itself proves the invitation reached them");
        (await teams.InvitationByTokenAsync(second, default)).Should().BeNull("an invitation works once");
    }

    [Fact]
    public async Task Invitations_run_out_after_14_days_and_can_be_sent_again()
    {
        if (Open() is not var (teams, accounts, projects)) return;
        var ada = await NewUserAsync(accounts, "Ada");
        var (team, sky) = await OwnerWithProjectAsync(teams, projects, ada);
        var (invite, token) = await teams.InviteAsync(sky, team.Id, "late@example.com", ada.Id, default);

        await using (var conn = await TestDatabase.Open()!.OpenAsync(default))
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE team_invites SET created_at = NOW() - INTERVAL '15 days' WHERE invite_id = @id", new { id = invite.Id });
        (await teams.InvitesAsync(sky, default)).Should().BeEmpty("it ran out, so it holds no seat");
        (await teams.InvitesAsync(sky, default, withExpired: true)).Should().ContainSingle().Which.Expired.Should().BeTrue();
        (await teams.InvitationByTokenAsync(token, default)).Should().BeNull();
        (await teams.ResendAsync(invite.Id, sky, default)).Should().NotBeNull("one that ran out can be sent again");
        (await teams.InvitesAsync(sky, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Handing_a_project_over_makes_the_old_owner_a_member()
    {
        if (Open() is not var (teams, accounts, projects)) return;
        var ada = await NewUserAsync(accounts, "Ada");
        var bo = await NewUserAsync(accounts, "Bo");
        var (_, sky) = await OwnerWithProjectAsync(teams, projects, ada);
        var boTeam = await teams.CreateAsync(bo.Id, "Bo", Plans.Beta, "month", default);
        await teams.AddMemberAsync(sky, bo.Id, "view", default);

        await teams.TransferAsync(sky, boTeam, ada.Id, default);

        (await teams.MembersAsync(sky, default)).Select(m => (m.Name, m.Role, m.Access)).Should().Equal(("Bo", "owner", "edit"), ("Ada", "member", "edit"));
        (await teams.OfProjectAsync(sky, default))!.Id.Should().Be(boTeam.Id);
    }

    [Fact]
    public async Task An_owner_sees_only_their_projects_with_real_counts()
    {
        if (Open() is not var (teams, accounts, projects)) return;
        var ada = await NewUserAsync(accounts, "Ada");
        var other = await projects.CreateAsync("Not theirs", default);
        var (team, mine) = await OwnerWithProjectAsync(teams, projects, ada);

        var ids = await teams.ProjectIdsAsync(team.Id, default);
        ids.Should().Equal(mine).And.NotContain(other.ProjectId);
        (await teams.StatsAsync(ids, default)).Should().ContainSingle().Which.Should().Be(new TeamProjectStats(mine, 0, 0, 0));
        (await teams.DailyAsync(ids, 30, default)).Should().BeEmpty();
        (await teams.ActivityAsync(ids, 10, default)).Should().BeEmpty();
        (await teams.ProjectActivityAsync(mine, null, 10, default)).Should().BeEmpty();
        (await teams.StatsAsync([], default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_an_account_takes_it_out_of_every_project()
    {
        if (Open() is not var (teams, accounts, projects)) return;
        var ada = await NewUserAsync(accounts, "Ada");
        var bo = await NewUserAsync(accounts, "Bo");
        var (_, sky) = await OwnerWithProjectAsync(teams, projects, ada);
        await teams.AddMemberAsync(sky, bo.Id, "edit", default);
        await accounts.CreateSessionAsync(bo.Id, "web", "test", "127.0.0.1", TimeSpan.FromHours(1), default);

        await teams.DeleteUserAsync(bo.Id, default);

        (await accounts.ByIdAsync(bo.Id, default)).Should().BeNull();
        (await accounts.SessionsAsync(bo.Id, default)).Should().BeEmpty();
        (await teams.MembersAsync(sky, default)).Select(m => m.Name).Should().Equal("Ada");
        (await teams.OwnedAsync(ada.Id, default)).Should().NotBeNull("other people's plans stay");
    }

    [Theory]
    [InlineData("solo", 1, 2, 2)]
    [InlineData("trio", 3, 5, 3)]
    [InlineData("duo", 3, 5, 3)]
    [InlineData("team", 6, 15, 6)]
    [InlineData("studio", 12, 40, 12)]
    public void Plans_match_the_website(string id, int seats, int gb, int maxExtra)
    {
        var p = Plans.Find(id)!;
        (p.Seats, p.StorageGb, p.MaxExtraSeats).Should().Be((seats, gb, maxExtra));
    }
}
