using FluentAssertions;
using YHDE.Server.Accounts;
using YHDE.Server.Teams;

namespace YHDE.Server.Tests.Teams;

[Collection("integration")]
public sealed class PromoStoreTests
{
    private static (PromoStore Promos, TeamStore Teams, AccountStore Accounts)? Open() =>
        TestDatabase.Open() is { } db ? (new PromoStore(db), new TeamStore(db), new AccountStore(db)) : null;

    private static async Task<(Team Team, Guid Owner)> NewTeamAsync(TeamStore teams, AccountStore accounts, string plan)
    {
        var u = (await accounts.CreateAsync($"t{Guid.NewGuid():N}@example.com", "Owner", null, verified: true, default))!;
        return (await teams.CreateAsync(u.Id, "T " + Guid.NewGuid().ToString("N")[..6], Plans.Find(plan)!, "month", default), u.Id);
    }

    private static PromoCode Code(string kind = "extra_seats", decimal amount = 2, int? months = 3, string[]? plans = null, int? max = null,
        bool active = true, DateTimeOffset? starts = null, DateTimeOffset? ends = null, bool newOnly = false) =>
        new(Guid.Empty, "T" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), "", active, starts, ends, plans, newOnly, kind, amount, months, max, DateTimeOffset.UtcNow, 0);

    [Fact]
    public async Task A_code_gives_its_seats_once_per_team_and_only_on_its_plans()
    {
        if (Open() is not var (promos, teams, accounts)) return;
        var code = Code(plans: ["team"]);
        await promos.SaveAsync(null, code, null, default);

        var (trio, trioOwner) = await NewTeamAsync(teams, accounts, "trio");
        (await promos.RedeemAsync(code.Code, trio, trioOwner, default)).Should().Be("other plan");

        var (team, owner) = await NewTeamAsync(teams, accounts, "team");
        (await promos.RedeemAsync(" " + code.Code.ToLowerInvariant() + " ", team, owner, default)).Should().BeNull("codes are forgiving to type");
        (await promos.RedeemAsync(code.Code, team, owner, default)).Should().Be("already used by this team");

        var after = (await teams.GetAsync(team.Id, default))!;
        after.BonusSeats.Should().Be(2);
        after.Seats.Should().Be(8, "Team's 6 plus the code's 2");
        (await promos.ForTeamAsync(team.Id, default)).Should().ContainSingle()
            .Which.Text.Should().Be("2 extra seats for 3 months on Team");
    }

    [Fact]
    public async Task Making_a_team_needs_a_code_that_unlocks_teams_and_the_team_exists_only_if_it_worked()
    {
        if (Open() is not var (promos, teams, accounts)) return;
        async Task<Guid> Person() => (await accounts.CreateAsync($"t{Guid.NewGuid():N}@example.com", "Owner", null, verified: true, default))!.Id;
        var team = Plans.Find("team")!;

        var plain = Code();
        await promos.SaveAsync(null, plain, null, default);
        var p1 = await Person();
        (await promos.CreateTeamAsync(plain.Code, p1, "Plain", team, "month", default)).Why.Should().Be("doesn't unlock teams");
        (await teams.OwnedAsync(p1, default)).Should().BeNull("a refused code leaves no team behind");
        (await promos.CreateTeamAsync("NOPE-" + Guid.NewGuid().ToString("N")[..6], p1, "Plain", team, "month", default)).Why.Should().Be("unknown");

        var access = Code(kind: "nothing", amount: 0, months: null, max: 2) with { UnlocksTeams = true };
        await promos.SaveAsync(null, access, null, default);
        foreach (var name in new[] { "First", "Second" })
        {
            var (made, why) = await promos.CreateTeamAsync(access.Code.ToLowerInvariant(), await Person(), name, team, "month", default);
            why.Should().BeNull();
            (await promos.ForTeamAsync(made!.Id, default)).Should().ContainSingle().Which.Text.Should().Be("Early access");
        }
        var p3 = await Person();
        (await promos.CreateTeamAsync(access.Code, p3, "Third", team, "month", default)).Why.Should().Be("used up");
        (await teams.OwnedAsync(p3, default)).Should().BeNull();

        // An access code can also give something, on chosen plans only.
        var seats = Code(plans: ["studio"]) with { UnlocksTeams = true };
        await promos.SaveAsync(null, seats, null, default);
        (await promos.CreateTeamAsync(seats.Code, p3, "Third", team, "month", default)).Why.Should().Be("other plan");
        var (studio, _) = await promos.CreateTeamAsync(seats.Code, p3, "Third", Plans.Find("studio")!, "month", default);
        (await teams.GetAsync(studio!.Id, default))!.BonusSeats.Should().Be(2);
    }

    [Fact]
    public async Task Codes_respect_limits_dates_and_the_switch()
    {
        if (Open() is not var (promos, teams, accounts)) return;
        var (a, ownerA) = await NewTeamAsync(teams, accounts, "solo");
        var (b, ownerB) = await NewTeamAsync(teams, accounts, "solo");

        var once = Code(kind: "free_months", months: 2, max: 1);
        await promos.SaveAsync(null, once, null, default);
        (await promos.RedeemAsync(once.Code, a, ownerA, default)).Should().BeNull();
        (await promos.RedeemAsync(once.Code, b, ownerB, default)).Should().Be("used up");

        var off = Code(active: false);
        await promos.SaveAsync(null, off, null, default);
        (await promos.RedeemAsync(off.Code, b, ownerB, default)).Should().Be("switched off");

        var ended = Code(ends: DateTimeOffset.UtcNow.AddDays(-1));
        await promos.SaveAsync(null, ended, null, default);
        (await promos.RedeemAsync(ended.Code, b, ownerB, default)).Should().Be("ended");

        var later = Code(starts: DateTimeOffset.UtcNow.AddDays(1));
        await promos.SaveAsync(null, later, null, default);
        (await promos.RedeemAsync(later.Code, b, ownerB, default)).Should().Be("not started");

        (await promos.RedeemAsync("NO-SUCH-CODE", b, ownerB, default)).Should().Be("unknown");
        (await promos.RedeemAsync("!!", b, ownerB, default)).Should().Be("malformed");
    }

    [Fact]
    public async Task New_teams_only_means_made_after_the_code()
    {
        if (Open() is not var (promos, teams, accounts)) return;
        var (old, oldOwner) = await NewTeamAsync(teams, accounts, "trio");
        await Task.Delay(20);
        var fresh = Code(newOnly: true);
        await promos.SaveAsync(null, fresh, null, default);
        await Task.Delay(20);
        var (young, youngOwner) = await NewTeamAsync(teams, accounts, "trio");
        (await promos.RedeemAsync(fresh.Code, old, oldOwner, default)).Should().Be("not a new team");
        (await promos.RedeemAsync(fresh.Code, young, youngOwner, default)).Should().BeNull();
    }

    [Fact]
    public async Task Used_codes_can_only_be_switched_off_not_deleted()
    {
        if (Open() is not var (promos, teams, accounts)) return;
        var code = Code();
        var (_, id) = await promos.SaveAsync(null, code, null, default);
        var (team, owner) = await NewTeamAsync(teams, accounts, "team");
        await promos.RedeemAsync(code.Code, team, owner, default);
        (await promos.DeleteUnusedAsync(id, default)).Should().BeFalse();

        var unused = Code();
        var (_, unusedId) = await promos.SaveAsync(null, unused, null, default);
        (await promos.DeleteUnusedAsync(unusedId, default)).Should().BeTrue();
        (await promos.SaveAsync(null, code with { Code = code.Code }, null, default)).Ok.Should().BeFalse("the code text is taken");
    }

    [Theory]
    [InlineData("free_months", 0, 3, "3 months free")]
    [InlineData("percent_off", 50, 6, "50 % off for 6 months")]
    [InlineData("amount_off", 2.5, null, "€2.5 off each payment for as long as you're subscribed")]
    [InlineData("extra_storage", 10, 1, "10 GB extra storage for 1 month")]
    public void Codes_are_described_in_words(string kind, decimal amount, int? months, string text) =>
        PromoStore.Describe(kind, amount, months, null).Should().Be(text);
}
