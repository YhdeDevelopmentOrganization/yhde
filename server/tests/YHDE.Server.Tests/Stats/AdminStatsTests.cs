using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YHDE.Server.Admin;
using YHDE.Server.Gateway;
using YHDE.Server.Projects;
using YHDE.Server.Tests.Assets;

namespace YHDE.Server.Tests.Stats;

// The admin page's statistics (admin.md), read from the summaries (ADR 0017).
public sealed class AdminStatsTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Activity_counts_editor_changes_apart_from_imports_and_every_part_answers()
    {
        if (TestDatabase.Open() is not { } db) return;
        using var temp = new TempBlobStore();
        var sessions = Substitute.For<ISessionManager>();
        var health = new HealthMonitor(sessions, new SessionLog(db, NullLogger<SessionLog>.Instance), NullLogger<HealthMonitor>.Instance);
        var stats = new AdminStats(db, temp.Store, new YHDE.Server.Assets.BlobStoreOptions(temp.Root, 1 << 20), health, sessions, NullLogger<AdminStats>.Instance);

        var project = await new ProjectStore(db).CreateAsync("Stats " + Guid.NewGuid().ToString("N")[..6], default);
        var (editor, import) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = await db.OpenAsync(default))
        {
            await conn.ExecuteAsync(
                "INSERT INTO session_log (session_id, member_id, member_name, project_id, via) VALUES (@editor, @member, 'Maya', @project, 'invite code')",
                new { editor, member = Guid.NewGuid(), project = project.ProjectId });
            var seq = 0;
            foreach (var (session, count) in new[] { (editor, 3), (import, 5) })
                for (var i = 0; i < count; i++)
                {
                    seq++;
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO operations (op_id, seq, branch_id, type, target_id, payload, actor_id, session_id, client_op_ref, parent_seq)
                        VALUES (@id, @seq, @branch, 'RegisterAsset', @id, @payload::jsonb, @id, @session, @id, @parent)
                        """,
                        new { id = Guid.NewGuid(), seq, branch = project.MainBranchId, session, parent = seq - 1, payload = $$"""{"s":"res://f{{seq}}.png","h":"{{seq:x64}}","n":10}""" });
                }
        }

        var activity = Json(await stats.ActivityAsync(default));
        var mine = activity.GetProperty("daily").EnumerateArray().Where(d => d.GetProperty("project").GetGuid() == project.ProjectId).ToList();
        mine.Sum(d => d.GetProperty("n").GetInt64()).Should().Be(3, "only changes made in the editor are charted");
        activity.GetProperty("totals").GetProperty("today").GetInt64().Should().BeGreaterThanOrEqualTo(3);
        activity.GetProperty("totals").GetProperty("importedToday").GetInt64().Should().BeGreaterThanOrEqualTo(5);
        activity.GetProperty("people").GetInt64().Should().BeGreaterThanOrEqualTo(1);

        var storage = Json(await stats.StorageAsync(default));
        storage.GetProperty("byProject").EnumerateArray()
            .Single(p => p.GetProperty("project").GetGuid() == project.ProjectId)
            .GetProperty("bytes").GetInt64().Should().Be(80);
        storage.GetProperty("byKind").EnumerateArray().Should().Contain(k => k.GetProperty("kind").GetString() == "Images");

        Json(stats.Health()).GetProperty("statsMs").GetProperty("activity").GetInt64().Should().BeGreaterThanOrEqualTo(0);

        foreach (var range in AdminStats.Ranges)
        {
            var people = Json(await stats.PeopleAsync(range, default));
            people.GetProperty("buckets").GetArrayLength().Should().BeGreaterThan(0);
            if (range is "day" or "week" or "month")
                people.GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("changes").GetInt64()).Should().BeGreaterThanOrEqualTo(3);
        }
    }
}
