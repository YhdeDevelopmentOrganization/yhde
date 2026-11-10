using Dapper;
using FluentAssertions;
using YHDE.Server.Accounts;
using YHDE.Server.Persistence;
using YHDE.Server.Projects;
using YHDE.Server.Teams;

namespace YHDE.Server.Tests.Teams;

// Invite links that let people edit (teams.md §4): each download carries a
// join ticket; a signed-in account redeems it and joins as a member who edits.
[Collection("integration")]
public sealed class JoinTicketTests
{
    private sealed record Setup(Database Db, TeamStore Teams, AccountStore Accounts, ProjectStore Projects, JoinTicketStore Tickets);

    private static Setup? Open() =>
        TestDatabase.Open() is { } db ? new(db, new TeamStore(db), new AccountStore(db), new ProjectStore(db), new JoinTicketStore(db)) : null;

    private static async Task<User> NewUserAsync(AccountStore accounts, string name, bool verified = true) =>
        (await accounts.CreateAsync($"t{Guid.NewGuid():N}@example.com", name, null, verified, default))!;

    private static async Task<(User Owner, Team Team, Guid Project)> OwnerWithProjectAsync(Setup s, string name = "Skyhop")
    {
        var owner = await NewUserAsync(s.Accounts, "Alex");
        var team = await s.Teams.CreateAsync(owner.Id, owner.Name, Plans.Beta, "month", default);
        var p = await s.Projects.CreateAsync(name, default);
        await s.Teams.AttachProjectAsync(p.ProjectId, team.Id, default);
        return (owner, team, p.ProjectId);
    }

    // What /join/<token>/download does for an edit link.
    private static async Task<string> DownloadAsync(Setup s, string linkToken)
    {
        var use = (await s.Projects.UseLinkAsync(linkToken, default))!;
        use.Access.Should().Be("edit");
        return await s.Tickets.CreateAsync(use.ProjectId, use.LinkId, null, default);
    }

    private static Task<Redeemed> Redeem(Setup s, User u, string? ticket) =>
        JoinTicketRedeemer.RedeemAsync(s.Teams, s.Tickets, s.Projects, u, ticket, default);

    private static async Task<AccountService.Refused> Refused(Func<Task> act) =>
        (await act.Should().ThrowAsync<AccountService.Refused>()).Which;

    [Fact]
    public async Task A_ticket_makes_the_account_a_member_who_edits_once()
    {
        if (Open() is not { } s) return;
        var (owner, _, game) = await OwnerWithProjectAsync(s);
        var lena = await NewUserAsync(s.Accounts, "Lena", verified: false);
        var sam = await NewUserAsync(s.Accounts, "Sam");
        var (link, token) = await s.Projects.CreateLinkAsync(game, "", null, 2, default, "edit");
        var ticket = await DownloadAsync(s, token);

        var joined = await Redeem(s, lena, ticket);
        joined.Should().Be(new Redeemed(game, "Skyhop", "edit", false, owner.Email), "an unconfirmed email does not stop joining");
        (await s.Teams.MembersAsync(game, default)).Should().Contain(m => m.UserId == lena.Id && m.Access == "edit" && m.Role == "member");

        (await Redeem(s, lena, ticket)).Already.Should().BeTrue("the editor may send it again");
        (await Refused(() => Redeem(s, sam, ticket))).Status.Should().Be(409, "a ticket works for one account");
        (await s.Teams.MembersAsync(game, default)).Should().NotContain(m => m.UserId == sam.Id);
        (await Redeem(s, owner, ticket)).Already.Should().BeTrue("the owner is in already");
        _ = link;
    }

    [Fact]
    public async Task Edit_links_hold_people_places_and_view_links_do_not()
    {
        if (Open() is not { } s) return;
        var (_, team, game) = await OwnerWithProjectAsync(s);
        team.PeoplePerProject.Should().Be(3);
        var (_, token) = await s.Projects.CreateLinkAsync(game, "", null, 2, default, "edit");
        await s.Projects.CreateLinkAsync(game, "", null, 4, default);

        async Task<int> Held() => (await s.Teams.EditHoldsAsync([game], default)).GetValueOrDefault(game);
        (await Held()).Should().Be(2, "two downloads left");
        (await s.Teams.ViewersAsync([game], default)).Should().Contain(game, 4, "viewer places are separate");

        var ticket = await DownloadAsync(s, token);
        (await Held()).Should().Be(2, "one download left and one ticket not used yet");
        await Redeem(s, await NewUserAsync(s.Accounts, "Lena"), ticket);
        (await Held()).Should().Be(1, "a used ticket is a member, counted as one");
    }

    [Fact]
    public async Task A_full_project_refuses_with_the_join_code_words()
    {
        if (Open() is not { } s) return;
        var (_, _, game) = await OwnerWithProjectAsync(s);
        var (_, token) = await s.Projects.CreateLinkAsync(game, "", null, 1, default, "edit");
        var ticket = await DownloadAsync(s, token);
        // Its own place counts for it: with two others in, it still fits.
        for (var i = 0; i < 2; i++) await s.Teams.AddMemberAsync(game, (await NewUserAsync(s.Accounts, "M")).Id, "edit", default);
        var lena = await NewUserAsync(s.Accounts, "Lena");
        (await Redeem(s, lena, ticket)).Already.Should().BeFalse();

        var (_, token2) = await s.Projects.CreateLinkAsync(game, "", null, 1, default, "edit");
        var late = await DownloadAsync(s, token2);
        await s.Teams.AddMemberAsync(game, (await NewUserAsync(s.Accounts, "M")).Id, "edit", default);
        var e = await Refused(async () => await Redeem(s, await NewUserAsync(s.Accounts, "Kim"), late));
        e.Status.Should().Be(409);
        e.Message.Should().Be("Skyhop is full: it has room for 3 developers besides its owner. Ask the owner to make room.");
    }

    [Fact]
    public async Task Turning_the_link_off_stops_unused_tickets_and_frees_their_places()
    {
        if (Open() is not { } s) return;
        var (_, _, game) = await OwnerWithProjectAsync(s);
        var (link, token) = await s.Projects.CreateLinkAsync(game, "", null, 2, default, "edit");
        var used = await DownloadAsync(s, token);
        var unused = await DownloadAsync(s, token);
        var lena = await NewUserAsync(s.Accounts, "Lena");
        await Redeem(s, lena, used);

        await s.Projects.RevokeLinkAsync(link.LinkId, default);
        (await s.Teams.EditHoldsAsync([game], default)).Should().NotContainKey(game);
        var e = await Refused(async () => await Redeem(s, await NewUserAsync(s.Accounts, "Sam"), unused));
        e.Status.Should().Be(410);
        e.Message.Should().Be("This link was turned off. Ask Alex for a new one.");
        (await s.Teams.MembersAsync(game, default)).Should().Contain(m => m.UserId == lena.Id, "who joined stays; the owner removes people one by one");

        await s.Projects.DeleteLinkAsync(link.LinkId, default);
        (await Refused(() => Redeem(s, lena, unused))).Status.Should().Be(404, "a deleted link takes its tickets with it");
    }

    [Fact]
    public async Task Archived_projects_and_old_tickets_take_nobody_in()
    {
        if (Open() is not { } s) return;
        var (_, _, game) = await OwnerWithProjectAsync(s);
        var (_, token) = await s.Projects.CreateLinkAsync(game, "", null, 3, default, "edit");
        var a = await DownloadAsync(s, token);
        var b = await DownloadAsync(s, token);

        await using (var conn = await s.Db.OpenAsync(default))
            await conn.ExecuteAsync("UPDATE join_tickets SET expires_at = NOW() - INTERVAL '1 minute' WHERE token_hash = @h", new { h = JoinTicket.Hash(b) });
        (await Refused(async () => await Redeem(s, await NewUserAsync(s.Accounts, "Sam"), b))).Message.Should().Be("This invite ran out. Ask Alex for a new link.");

        await s.Projects.SetArchivedAsync(game, true, default);
        var e = await Refused(async () => await Redeem(s, await NewUserAsync(s.Accounts, "Lena"), a));
        e.Message.Should().Be("Skyhop is archived, so it can't take anyone in.");
        (await Refused(async () => await Redeem(s, await NewUserAsync(s.Accounts, "Kim"), "not a ticket"))).Status.Should().Be(404);
        (await Refused(async () => await Redeem(s, await NewUserAsync(s.Accounts, "Kim"), JoinTicket.New()))).Status.Should().Be(404);
    }

    [Fact]
    public async Task A_members_own_download_works_only_for_their_account()
    {
        if (Open() is not { } s) return;
        var (owner, _, game) = await OwnerWithProjectAsync(s);
        var ticket = await s.Tickets.CreateAsync(game, null, owner.Id, default);
        (await s.Teams.EditHoldsAsync([game], default)).Should().NotContainKey(game, "it holds no place: the owner is in already");

        var joined = await Redeem(s, owner, ticket);
        joined.Should().Be(new Redeemed(game, "Skyhop", "edit", true, null));
        var e = await Refused(async () => await Redeem(s, await NewUserAsync(s.Accounts, "Sam"), ticket));
        e.Status.Should().Be(403);
        e.Message.Should().Be("This download was made for another account. Sign in with that account, or ask Alex for a link.");
    }

    [Fact]
    public async Task Tickets_are_long_random_and_only_their_hash_is_kept()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var t = JoinTicket.New();
            JoinTicket.LooksLikeTicket(t).Should().BeTrue();
            seen.Add(t).Should().BeTrue();
        }
        JoinTicket.LooksLikeTicket("YHDE-AAAA-BBBB-CCCC-DDDD").Should().BeFalse();
        JoinTicket.Hash("x").Should().NotEqual(InviteCode.Hash("x"));

        if (Open() is not { } s) return;
        var (_, _, game) = await OwnerWithProjectAsync(s);
        var ticket = await s.Tickets.CreateAsync(game, null, null, default);
        await using var conn = await s.Db.OpenAsync(default);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM join_tickets WHERE project_id = @game AND token_hash = @h",
            new { game, h = JoinTicket.Hash(ticket) })).Should().Be(1);
    }
}
