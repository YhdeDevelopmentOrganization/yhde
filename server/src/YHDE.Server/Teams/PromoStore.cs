using System.Globalization;
using Dapper;
using YHDE.Server.Persistence;

namespace YHDE.Server.Teams;

public sealed record PromoCode(
    Guid Id,
    string Code,
    string Note,
    bool Active,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string[]? Plans,
    bool NewTeamsOnly,
    string Kind,
    decimal Amount,
    int? Months,
    int? MaxRedemptions,
    DateTimeOffset Created,
    int Redemptions,
    bool UnlocksTeams = false);

// What a team got from a code, shown on its Billing page (never the code).
public sealed record TeamPromo(string Text, DateTimeOffset RedeemedAt, DateTimeOffset? Until);

// Promo codes (009_staff_and_promos.sql): drafted on the admin page, typed by
// a team owner on the Billing page. During the beta a code that unlocks teams
// (011_team_access_codes.sql) is also what lets someone make a team at all.
// Nothing is charged yet, so a code's benefit is recorded now and applied when
// payments start; extra seats and storage count at once.
public sealed class PromoStore(Database db)
{
    public static readonly string[] Kinds = ["nothing", "free_months", "percent_off", "amount_off", "extra_seats", "extra_storage"];

    // Codes are compared in capitals without spaces: " spring-25 " = "SPRING-25".
    public static string Normalize(string? code) =>
        new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    public static bool LooksLikeCode(string code) =>
        code.Length is >= 3 and <= 40 && code.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    public static string Describe(string kind, decimal amount, int? months, string[]? plans)
    {
        var n = amount.ToString("0.##", CultureInfo.InvariantCulture);
        var span = months is { } m ? (m == 1 ? "for 1 month" : $"for {m} months") : "for as long as you're subscribed";
        var text = kind switch
        {
            "nothing" => "Early access",
            "free_months" => months is { } fm ? (fm == 1 ? "1 month free" : $"{fm} months free") : "Free",
            "percent_off" => $"{n} % off {span}",
            "amount_off" => $"€{n} off each payment {span}",
            "extra_seats" => $"{n} extra {(amount == 1 ? "seat" : "seats")} {span}",
            "extra_storage" => $"{n} GB extra storage {span}",
            _ => kind,
        };
        if (plans is { Length: > 0 } && kind != "nothing")
            text += " on " + string.Join(" or ", plans.Select(p => Teams.Plans.Find(p)?.Name ?? p));
        return text;
    }

    public async Task<IReadOnlyList<PromoCode>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            Row.Select + " ORDER BY c.created_at DESC", cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<PromoCode?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Row.Select + " WHERE c.code_id = @id", new { id }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<PromoCode?> ByCodeAsync(string code, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Row.Select + " WHERE c.code = @code", new { code = Normalize(code) }, cancellationToken: ct));
        return row?.ToModel();
    }

    // Saves a new code (id null) or changes one. False: the code text is taken.
    public async Task<(bool Ok, Guid Id)> SaveAsync(Guid? id, PromoCode c, Guid? by, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var taken = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT code_id FROM promo_codes WHERE code = @code", new { code = c.Code }, cancellationToken: ct));
        if (taken is { } t && t != id) return (false, t);
        var args = new
        {
            id = id ?? Guid.NewGuid(),
            code = c.Code,
            note = c.Note,
            active = c.Active,
            starts = c.StartsAt,
            ends = c.EndsAt,
            plans = c.Plans is { Length: > 0 } p ? p : null,
            newOnly = c.NewTeamsOnly,
            kind = c.Kind,
            amount = c.Amount,
            months = c.Months,
            max = c.MaxRedemptions,
            by,
            unlocks = c.UnlocksTeams,
        };
        await conn.ExecuteAsync(new CommandDefinition(id is null
            ? """
              INSERT INTO promo_codes (code_id, code, note, active, starts_at, ends_at, plans, new_teams_only, kind, amount, months, max_redemptions, created_by, unlocks_teams)
              VALUES (@id, @code, @note, @active, @starts, @ends, @plans, @newOnly, @kind, @amount, @months, @max, @by, @unlocks)
              """
            : """
              UPDATE promo_codes SET code = @code, note = @note, active = @active, starts_at = @starts, ends_at = @ends, plans = @plans,
                     new_teams_only = @newOnly, kind = @kind, amount = @amount, months = @months, max_redemptions = @max,
                     unlocks_teams = @unlocks
              WHERE code_id = @id
              """, args, cancellationToken: ct));
        return (true, args.id);
    }

    // Only a code nobody has used can be deleted; used ones are switched off.
    public async Task<bool> DeleteUnusedAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM promo_codes WHERE code_id = @id AND NOT EXISTS (SELECT 1 FROM promo_redemptions WHERE code_id = @id)",
            new { id }, cancellationToken: ct)) > 0;
    }

    // Uses a code for a team. Null when it worked, else why (for the admin
    // log; the owner is only told the code doesn't work, so codes can't be
    // probed for which exist).
    public async Task<string?> RedeemAsync(string input, Team team, Guid by, CancellationToken ct)
    {
        var code = Normalize(input);
        if (!LooksLikeCode(code)) return "malformed";
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var why = await UseAsync(conn, tx, code, team.Id, team.PlanInfo.Id, team.Created, by, needsUnlock: false, ct);
        if (why is null) await tx.CommitAsync(ct);
        return why;
    }

    // Makes a team with a code that unlocks teams, in one transaction: the
    // team exists only if the code worked, and a code limited to 10 uses
    // makes at most 10 teams. Why is null when it worked (reasons as above).
    public async Task<(Team? Team, string? Why)> CreateTeamAsync(string input, Guid ownerId, string name, Plan plan, string period, CancellationToken ct)
    {
        var code = Normalize(input);
        if (!LooksLikeCode(code)) return (null, "malformed");
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var team = await TeamStore.InsertAsync(conn, tx, ownerId, name, plan, period, ct);
        var why = await UseAsync(conn, tx, code, team.Id, plan.Id, team.Created, ownerId, needsUnlock: true, ct);
        if (why is not null) return (null, why);
        await tx.CommitAsync(ct);
        return (team, null);
    }

    private static async Task<string?> UseAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx, string code,
        Guid teamId, string planId, DateTimeOffset teamCreated, Guid by, bool needsUnlock, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Row.Select + " WHERE c.code = @code", new { code }, tx, cancellationToken: ct));
        if (row is null) return "unknown";
        var c = row.ToModel();
        var now = DateTimeOffset.UtcNow;
        if (needsUnlock && !c.UnlocksTeams) return "doesn't unlock teams";
        if (!c.Active) return "switched off";
        if (c.StartsAt is { } s && now < s) return "not started";
        if (c.EndsAt is { } e && now >= e) return "ended";
        // Beta teams take any code: every team is on that plan until 1.0.
        if (c.Plans is { Length: > 0 } plans && planId != "beta" && !plans.Contains(planId)) return "other plan";
        if (c.NewTeamsOnly && teamCreated < c.Created) return "not a new team";
        if (c.MaxRedemptions is { } max && c.Redemptions >= max) return "used up";
        var until = c.Months is { } m ? now.AddMonths(m) : (DateTimeOffset?)null;
        var added = await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO promo_redemptions (code_id, team_id, redeemed_by, until) VALUES (@id, @team, @by, @until) ON CONFLICT DO NOTHING",
            new { id = c.Id, team = teamId, by, until }, tx, cancellationToken: ct));
        return added == 0 ? "already used by this team" : null;
    }

    public async Task<IReadOnlyList<TeamPromo>> ForTeamAsync(Guid teamId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<TeamRow>(new CommandDefinition(
            """
            SELECT c.kind, c.amount, c.months, c.plans, r.redeemed_at, r.until
            FROM promo_redemptions r JOIN promo_codes c ON c.code_id = r.code_id
            WHERE r.team_id = @teamId ORDER BY r.redeemed_at DESC
            """, new { teamId }, cancellationToken: ct));
        return rows.Select(r => new TeamPromo(Describe(r.kind, r.amount, r.months, r.plans), Utc(r.redeemed_at), r.until is { } u ? Utc(u) : null)).ToList();
    }

    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

#pragma warning disable IDE1006 // column names
    private sealed class Row
    {
        public const string Select =
            """
            SELECT c.*, (SELECT COUNT(*) FROM promo_redemptions r WHERE r.code_id = c.code_id)::int AS redemptions
            FROM promo_codes c
            """;
        public Guid code_id { get; init; }
        public string code { get; init; } = "";
        public string note { get; init; } = "";
        public bool active { get; init; }
        public DateTime? starts_at { get; init; }
        public DateTime? ends_at { get; init; }
        public string[]? plans { get; init; }
        public bool new_teams_only { get; init; }
        public string kind { get; init; } = "";
        public decimal amount { get; init; }
        public int? months { get; init; }
        public int? max_redemptions { get; init; }
        public Guid? created_by { get; init; }
        public DateTime created_at { get; init; }
        public int redemptions { get; init; }
        public bool unlocks_teams { get; init; }
        public PromoCode ToModel() => new(code_id, code, note, active, starts_at is { } s ? Utc(s) : null, ends_at is { } e ? Utc(e) : null,
            plans, new_teams_only, kind, amount, months, max_redemptions, Utc(created_at), redemptions, unlocks_teams);
    }

    private sealed class TeamRow
    {
        public string kind { get; init; } = "";
        public decimal amount { get; init; }
        public int? months { get; init; }
        public string[]? plans { get; init; }
        public DateTime redeemed_at { get; init; }
        public DateTime? until { get; init; }
    }
#pragma warning restore IDE1006
}
