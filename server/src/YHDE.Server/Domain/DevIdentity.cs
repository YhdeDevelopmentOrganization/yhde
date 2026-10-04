namespace YHDE.Server.Domain;

// The actor for changes nobody signed in made: connections with an invite
// code or the server key, and changes the server makes itself (a project
// imported from a zip). Signed-in editors act as their own account.
public static class DevIdentity
{
    public static readonly Guid ActorId = new("00000000-0000-0000-0000-000000000001");
    public const string DisplayName = "dev";

    public static Guid ResolveActorId(string? _authHeader) => ActorId;
}
