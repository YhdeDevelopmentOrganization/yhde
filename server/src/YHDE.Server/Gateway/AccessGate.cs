using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using YHDE.Server.Projects;

namespace YHDE.Server.Gateway;

// What a connection may reach: every project (the operator's server key, or
// an open development server), exactly one project (an invite code), or the
// projects of a signed-in person's team (an editor sign-in, with who it is).
public sealed record AccessGrant(Guid? ProjectId)
{
    public static readonly AccessGrant AllProjects = new((Guid?)null);

    public IReadOnlySet<Guid>? Projects { get; init; }
    // Projects this person may only look at (their access is "view").
    public IReadOnlySet<Guid>? ReadOnly { get; init; }
    public Guid? UserId { get; init; }
    public string? UserName { get; init; }

    public bool CanEdit(Guid projectId) => ReadOnly is null || !ReadOnly.Contains(projectId);

    public bool Allows(Guid projectId) =>
        ProjectId is { } one ? one == projectId : Projects is { } team ? team.Contains(projectId) : true;
}

// Who may connect (network_protocol.md, projects.md). Three kinds of secret
// come as `Authorization: Bearer <secret>` on the WebSocket and file routes:
// - an editor sign-in token: the team's projects the person may open;
// - a project invite code (YHDE-XXXX-...): one project;
// - the server access key (Yhde:AccessKey): every project, for the operator.
//
// Without Yhde:AccessKey the server refuses to start unless
// Yhde:AllowAnonymous is set, so a server is never open by accident.
public sealed class AccessGate
{
    // Application close code (RFC 6455 section 7.4.2, 4000-4999) for a refused key.
    // Clients stop reconnecting when they receive it.
    public const int UnauthorizedCloseCode = 4401;
    public const int MinimumKeyLength = 16;

    private static readonly TimeSpan InviteCacheTime = TimeSpan.FromSeconds(30);

    private readonly byte[]? _keyHash;
    private readonly IProjectStore? _projects;
    private readonly ConcurrentDictionary<string, (Guid? Project, bool ViewOnly, DateTimeOffset Until)> _inviteCache = new();

    private readonly Accounts.EditorTokens? _editors;

    public AccessGate(IConfiguration configuration, ILogger<AccessGate> logger, IProjectStore? projects = null, Accounts.EditorTokens? editors = null)
    {
        _projects = projects;
        _editors = editors;
        var key = configuration["Yhde:AccessKey"]?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            if (!configuration.GetValue("Yhde:AllowAnonymous", false))
            {
                throw new InvalidOperationException(
                    "Yhde:AccessKey is not set. Set it (at least 16 characters) or set " +
                    "Yhde:AllowAnonymous=true to run an open server deliberately.");
            }
            logger.LogWarning("No access key configured: anyone who can reach this server can edit its projects");
            return;
        }
        if (key.Length < MinimumKeyLength)
        {
            throw new InvalidOperationException(
                $"Yhde:AccessKey must be at least {MinimumKeyLength} characters.");
        }
        _keyHash = Hash(key);
    }

    public bool Required => _keyHash is not null;

    // Constant-time check of an Authorization header value. Both sides are
    // hashed first so the comparison does not leak the key's length either.
    public bool Admits(string? authorizationHeader)
    {
        if (_keyHash is null) return true;
        if (string.IsNullOrEmpty(authorizationHeader)) return false;

        const string scheme = "Bearer ";
        if (!authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var presented = Hash(authorizationHeader[scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, _keyHash);
    }

    // The full check: an editor sign-in, then the server key, then an invite
    // code. Null = refused. Invite lookups are cached briefly (file transfers
    // call this per chunk); a revoked code stops working within that time.
    public async Task<AccessGrant?> AdmitAsync(string? authorizationHeader, CancellationToken ct)
    {
        const string scheme = "Bearer ";
        var code = authorizationHeader is { } h && h.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? h[scheme.Length..].Trim() : null;
        // A signed-in editor first, always: the account decides who this is
        // and what they may open or change, even on a server without a key.
        if (_editors is not null && code is not null && Accounts.EditorTokens.LooksLikeToken(code)
            && await _editors.ResolveAsync(code, ct) is { } editor)
            return new AccessGrant((Guid?)null) { Projects = editor.Projects, ReadOnly = editor.ReadOnly, UserId = editor.UserId, UserName = editor.Name };
        if (Admits(authorizationHeader)) return AccessGrant.AllProjects;
        if (_projects is null || code is null) return null;
        if (!InviteCode.LooksLikeCode(code)) return null;

        var key = Convert.ToHexString(InviteCode.Hash(code));
        var now = DateTimeOffset.UtcNow;
        if (_inviteCache.TryGetValue(key, out var cached) && cached.Until > now)
            return cached.Project is { } p ? Grant(p, cached.ViewOnly) : null;
        var project = await _projects.ProjectForCodeAsync(code, ct);
        // A view link's code watches: it never changes the project.
        var viewOnly = project is not null && await _projects.CodeIsViewOnlyAsync(code, ct);
        if (_inviteCache.Count > 10_000) _inviteCache.Clear();
        _inviteCache[key] = (project, viewOnly, now + InviteCacheTime);
        return project is { } id ? Grant(id, viewOnly) : null;
    }

    private static AccessGrant Grant(Guid project, bool viewOnly) =>
        viewOnly ? new AccessGrant(project) { ReadOnly = new HashSet<Guid> { project } } : new AccessGrant(project);

    // Forget cached codes at once (after a revoke on the admin page).
    public void ForgetInvites()
    {
        _inviteCache.Clear();
        _editors?.Forget();
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
