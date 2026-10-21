using Dapper;
using FluentAssertions;
using YHDE.Server.Projects;
using YHDE.Server.Teams;

namespace YHDE.Server.Tests.Persistence;

// The summaries the statistics read (018_stats_summaries.sql, ADR 0017) must
// give the same answers as reading the whole operation log.
public sealed class StatsSummaryTests
{
    private static async Task Insert(YHDE.Server.Persistence.Database db, Guid branch, long seq, string type, Guid target, string payload, Guid session, DateTime at)
    {
        await using var conn = await db.OpenAsync(default);
        await conn.ExecuteAsync(
            """
            INSERT INTO operations (op_id, seq, branch_id, type, target_id, payload, actor_id, session_id, client_op_ref, parent_seq, created_at)
            VALUES (@id, @seq, @branch, @type, @target, @payload::jsonb, @actor, @session, @id, @parent, @at)
            """,
            new { id = Guid.NewGuid(), seq, branch, type, target, payload, actor = Guid.NewGuid(), session, parent = seq - 1, at });
    }

    [Fact]
    public async Task Summaries_match_the_log_and_go_with_the_project()
    {
        if (TestDatabase.Open() is not { } db) return;
        var projects = new ProjectStore(db);
        var p = await projects.CreateAsync("Summaries " + Guid.NewGuid().ToString("N")[..6], default);
        var branch = p.MainBranchId;
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (s1, s2) = (Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;
        var hash = new string('a', 64);

        await Insert(db, branch, 1, "RegisterAsset", a, $$"""{"s":"res://a.png","h":"{{hash}}","n":100}""", s1, now.AddHours(-30));
        await Insert(db, branch, 2, "RegisterAsset", b, $$"""{"s":"res://b.png","h":"{{hash}}","n":50}""", s1, now.AddHours(-30));
        await Insert(db, branch, 3, "UpdateAsset", a, $$"""{"s":"res://a.png","h":"{{hash}}","n":120}""", s2, now.AddHours(-2));
        await Insert(db, branch, 4, "MoveAsset", b, $$"""{"s":"res://c/b.png","f":"res://b.png","h":"{{hash}}","n":50}""", s2, now.AddMinutes(-5));
        await Insert(db, branch, 5, "DeleteAsset", b, """{"s":"res://c/b.png"}""", s2, now.AddMinutes(-4));
        // A size that is not a number must not stop the operation from being stored.
        await Insert(db, branch, 6, "RegisterAsset", c, """{"s":"res://c.txt","n":"lots"}""", s2, now.AddMinutes(-3));
        await Insert(db, branch, 7, "SetProperty", Guid.NewGuid(), """{"p":"x"}""", s2, now.AddMinutes(-2));

        var stats = (await projects.StatsAsync(default)).Single(s => s.ProjectId == p.ProjectId);
        stats.Operations.Should().Be(7);
        stats.Files.Should().Be(2);
        stats.FileBytes.Should().Be(120);
        stats.LastActivity.Should().BeCloseTo(new DateTimeOffset(now.AddMinutes(-2)), TimeSpan.FromSeconds(1));

        // The old query over the whole log, for comparison.
        await using (var conn = await db.OpenAsync(default))
        {
            var old = await conn.QuerySingleAsync<(long files, long bytes)>(
                """
                WITH latest AS (
                    SELECT DISTINCT ON (o.branch_id, o.target_id) o.type, o.payload
                    FROM operations o WHERE o.branch_id = @branch
                      AND o.type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset', 'DeleteAsset')
                    ORDER BY o.branch_id, o.target_id, o.seq DESC
                )
                SELECT COUNT(*) AS files, COALESCE(SUM(CASE WHEN jsonb_typeof(payload->'n') = 'number' THEN (payload->>'n')::bigint ELSE 0 END), 0)::bigint AS bytes
                FROM latest WHERE type <> 'DeleteAsset'
                """, new { branch });
            (old.files, old.bytes).Should().Be((stats.Files, stats.FileBytes));
        }

        var teams = new TeamStore(db);
        (await teams.StatsAsync([p.ProjectId], default)).Should().ContainSingle()
            .Which.Should().Be(new TeamProjectStats(p.ProjectId, 7, 2, 120));
        var daily = await teams.DailyAsync([p.ProjectId], 30, default);
        daily.Sum(d => d.Count).Should().Be(7);

        // Deleting the project removes its summaries too.
        await projects.SetArchivedAsync(p.ProjectId, true, default);
        (await projects.DeleteArchivedAsync(p.ProjectId, default)).Should().NotBeNull();
        await using (var conn = await db.OpenAsync(default))
        {
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM op_hourly WHERE project_id = @id", new { id = p.ProjectId })).Should().Be(0);
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM asset_heads WHERE project_id = @id", new { id = p.ProjectId })).Should().Be(0);
        }
    }

    [Fact]
    public async Task Every_project_summary_matches_its_log()
    {
        if (TestDatabase.Open() is not { } db) return;
        await using var conn = await db.OpenAsync(default);
        var countsOff = await conn.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM (
                SELECT b.project_id, COUNT(*) AS n FROM operations o JOIN branches b ON b.branch_id = o.branch_id GROUP BY 1
            ) log FULL JOIN (
                SELECT project_id, SUM(n) AS n FROM op_hourly GROUP BY 1
            ) sum ON sum.project_id = log.project_id
            WHERE log.n IS DISTINCT FROM sum.n
            """);
        countsOff.Should().Be(0);
        var headsOff = await conn.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM (
                SELECT DISTINCT ON (o.branch_id, o.target_id) o.branch_id, o.target_id, o.seq
                FROM operations o WHERE o.type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset', 'DeleteAsset')
                ORDER BY o.branch_id, o.target_id, o.seq DESC
            ) log FULL JOIN asset_heads h ON h.branch_id = log.branch_id AND h.target_id = log.target_id
            WHERE log.seq IS DISTINCT FROM h.seq
            """);
        headsOff.Should().Be(0);
    }
}
