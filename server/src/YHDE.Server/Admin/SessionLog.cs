using Dapper;
using YHDE.Server.Gateway;
using YHDE.Server.Persistence;

namespace YHDE.Server.Admin;

// Who was connected, to which project and for how long (admin statistics).
// Best effort: a failure here is logged and never affects the connection.
public sealed class SessionLog(Database db, ILogger<SessionLog> logger)
{
    public async Task StartAsync(SessionState s)
    {
        await Try(async conn => await conn.ExecuteAsync(
            """
            INSERT INTO session_log (session_id, member_id, member_name, project_id, client_version, via)
            VALUES (@SessionId, @member, @name, @project, @version, @via)
            ON CONFLICT (session_id) DO UPDATE
              SET project_id = EXCLUDED.project_id, member_id = EXCLUDED.member_id,
                  member_name = EXCLUDED.member_name, last_seen_at = NOW()
            """,
            new
            {
                s.SessionId,
                member = s.MemberId,
                name = s.MemberName ?? "",
                project = s.SubscribedProjectId,
                version = s.ClientVersion ?? "",
                via = s.Grant.UserId is not null ? "account" : s.Grant.ProjectId is null ? "server key" : "invite code",
            }));
    }

    public Task EndAsync(Guid sessionId) => Try(async conn => await conn.ExecuteAsync(
        "UPDATE session_log SET ended_at = NOW(), last_seen_at = NOW() WHERE session_id = @sessionId AND ended_at IS NULL",
        new { sessionId }));

    public Task TouchAsync(Guid[] sessionIds) => sessionIds.Length == 0 ? Task.CompletedTask : Try(async conn => await conn.ExecuteAsync(
        "UPDATE session_log SET last_seen_at = NOW() WHERE session_id = ANY(@sessionIds) AND ended_at IS NULL",
        new { sessionIds }));

    // After a restart nothing is connected: close what the last run left open
    // at the time it was last seen.
    public Task CloseLeftoversAsync() => Try(async conn => await conn.ExecuteAsync(
        "UPDATE session_log SET ended_at = last_seen_at WHERE ended_at IS NULL"));

    private async Task Try(Func<Npgsql.NpgsqlConnection, Task> work)
    {
        try
        {
            await using var conn = await db.OpenAsync();
            await work(conn);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Connection history not updated");
        }
    }
}
