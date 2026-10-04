using System.Collections.Concurrent;
using YHDE.Server.Teams;

namespace YHDE.Server.Accounts;

// Who an editor's sign-in token belongs to, and which projects it may open
// (their own and those they were invited to). The Godot editor gets the token by signing in through the
// website (DeviceEndpoints) and sends it as `Authorization: Bearer <token>`
// on the WebSocket and file routes, like an invite code.
//
// Lookups are cached for a short time (file transfers ask on every chunk);
// signing out, being removed from a project or a new project take effect
// within that time, and at once for new connections after Forget.
public sealed class EditorTokens(AccountStore accounts, TeamStore teams)
{
    public sealed record Editor(Guid UserId, Guid SessionId, string Name, Guid? TeamId, IReadOnlySet<Guid> Projects, IReadOnlySet<Guid> ReadOnly);

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (Editor? Editor, DateTimeOffset Until)> _cache = new(StringComparer.Ordinal);

    // Tokens are 32 random bytes in URL-safe base64: 43 characters.
    public static bool LooksLikeToken(string value) =>
        value.Length == 43 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public async Task<Editor?> ResolveAsync(string token, CancellationToken ct)
    {
        if (!LooksLikeToken(token)) return null;
        var key = Convert.ToHexString(Secrets.Hash(token));
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(key, out var hit) && hit.Until > now) return hit.Editor;

        Editor? editor = null;
        if (await accounts.SessionAsync(token, "editor", ct) is { } found)
        {
            var mine = await teams.ProjectsForAsync(found.User.Id, ct);
            editor = new Editor(found.User.Id, found.Session.Id, found.User.Name, (await teams.OwnedAsync(found.User.Id, ct))?.Id,
                mine.Select(m => m.ProjectId).ToHashSet(),
                mine.Where(m => m.Access == "view").Select(m => m.ProjectId).ToHashSet());
        }
        if (_cache.Count > 20_000) _cache.Clear();
        _cache[key] = (editor, now + CacheFor);
        return editor;
    }

    public void Forget() => _cache.Clear();

    // Closes a person's open editor connections (removed from a project):
    // "refused", so the editor stops reconnecting and re-checks its projects.
    public static async Task DisconnectAsync(Gateway.ISessionManager sessions, Guid userId)
    {
        foreach (var s in sessions.All.Where(s => s.Grant.UserId == userId).ToList())
        {
            // One writer at a time on a socket (as every send does).
            await s.WriteLock.WaitAsync();
            try
            {
                await s.Socket.CloseOutputAsync((System.Net.WebSockets.WebSocketCloseStatus)Gateway.AccessGate.UnauthorizedCloseCode,
                    "Access changed", CancellationToken.None);
            }
            catch (Exception)
            {
                // Already closing: nothing to do.
            }
            finally
            {
                s.WriteLock.Release();
            }
        }
    }
}
