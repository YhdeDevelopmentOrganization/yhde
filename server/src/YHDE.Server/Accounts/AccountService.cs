using YHDE.Server.Site;

namespace YHDE.Server.Accounts;

// What signing up, signing in and account recovery do (ADR 0015). HTTP and
// cookies are in AuthEndpoints; this class decides.
public sealed class AccountService(AccountStore store, IEmailSender email, ILogger<AccountService> logger)
{
    public static readonly TimeSpan VerifyLinkLifetime = TimeSpan.FromDays(2);
    public static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromHours(1);

    // Guessing limits: per address (IP) and per account.
    private readonly Limiter _loginByIp = new(30, TimeSpan.FromMinutes(10));
    private readonly Limiter _loginByEmail = new(8, TimeSpan.FromMinutes(15));
    private readonly Limiter _signupByIp = new(6, TimeSpan.FromHours(1));
    private readonly Limiter _recoverByIp = new(6, TimeSpan.FromHours(1));
    private readonly Limiter _recoverByEmail = new(3, TimeSpan.FromHours(1));

    public sealed class Refused(string message, int status = 400) : Exception(message)
    {
        public int Status { get; } = status;
    }

    public static string CleanName(string? name) => NameRules.Clean(name, NameRules.Kind.Person);

    // A person's name, cleaned and checked (NameRules), or a refusal to show.
    public static string CheckedName(string? name)
    {
        var clean = CleanName(name);
        if (clean.Length == 0) throw new Refused("Enter your name. Your teammates see it next to your cursor.");
        return NameRules.Problem(clean, NameRules.Kind.Person) is { } problem ? throw new Refused(problem) : clean;
    }

    // Answers the same whether or not the address already has an account (no
    // hint who is signed up): the person is told to check their email.
    public async Task RegisterAsync(string? emailInput, string? password, string? name, string ip, string baseUrl, CancellationToken ct)
    {
        if (!_signupByIp.Try(ip)) throw new Refused("Too many new accounts from here. Try again later.", 429);
        var address = SiteStore.NormalizeEmail(emailInput) ?? throw new Refused("That email address doesn't look right.");
        if (Passwords.Problem(password, address) is { } problem) throw new Refused(problem);
        var display = CheckedName(name);

        var hash = await Passwords.HashAsync(password!);
        var user = await store.CreateAsync(address, display, hash, verified: false, ct);
        if (user is null)
        {
            // The address has an account already. Say the same as for a new
            // one (no hint who has an account) and tell the owner by email.
            await SendAsync(address, "Someone tried to sign up with your email",
                $"Someone tried to make a YHDE account with this address, but you already have one.\n\nSign in: {baseUrl}/app#/sign-in\nForgot your password? {baseUrl}/app#/forgot\n\nIf this wasn't you, you can ignore this email.", ct);
            return;
        }
        await SendVerificationAsync(user, baseUrl, ct);
    }

    public async Task<User> LoginAsync(string? emailInput, string? password, string ip, CancellationToken ct)
    {
        var address = SiteStore.NormalizeEmail(emailInput) ?? "";
        if (!_loginByIp.Try(ip) || (address.Length > 0 && !_loginByEmail.Try(address)))
            throw new Refused("Too many tries. Wait 15 minutes, or reset your password.", 429);
        var found = address.Length > 0 ? await store.ByEmailAsync(address, ct) : null;
        // Always verify something, so a missing account takes as long as a wrong password.
        var ok = await Passwords.VerifyAsync(password ?? "", found?.PasswordHash);
        if (!ok || found is null || found.Value.User.Disabled)
            throw new Refused("Wrong email or password. Signed up with GitHub or Google? Use that button.", 401);
        _loginByEmail.Forget(address);
        return found.Value.User;
    }

    public async Task SendVerificationAsync(User user, string baseUrl, CancellationToken ct)
    {
        if (user.EmailVerified) return;
        if (!_recoverByEmail.Try("verify:" + user.Email)) throw new Refused("We just sent one. Check your inbox (and spam) before asking again.", 429);
        var token = await store.CreateEmailTokenAsync(user.Id, "verify", VerifyLinkLifetime, ct);
        await SendAsync(user.Email, "Confirm your email for YHDE",
            $"Hi {user.Name},\n\nConfirm your email address to finish setting up your YHDE account:\n{baseUrl}/app#/verify?token={token}\n\nThe link works for 2 days. If you didn't sign up, ignore this email.", ct);
    }

    public async Task<bool> VerifyAsync(string? token, CancellationToken ct)
    {
        var userId = await store.UseEmailTokenAsync(token ?? "", "verify", ct);
        if (userId is null) return false;
        await store.MarkVerifiedAsync(userId.Value, ct);
        return true;
    }

    // Always answers the same, whether or not the address has an account.
    public async Task ForgotAsync(string? emailInput, string ip, string baseUrl, CancellationToken ct)
    {
        if (!_recoverByIp.Try(ip)) throw new Refused("Too many tries. Try again in an hour.", 429);
        var address = SiteStore.NormalizeEmail(emailInput);
        if (address is null || !_recoverByEmail.Try("reset:" + address)) return;
        var found = await store.ByEmailAsync(address, ct);
        if (found is null || found.Value.User.Disabled) return;
        var token = await store.CreateEmailTokenAsync(found.Value.User.Id, "reset", ResetLinkLifetime, ct);
        await SendAsync(address, "Reset your YHDE password",
            $"Someone (hopefully you) asked to reset the password of your YHDE account.\n\nChoose a new password:\n{baseUrl}/app#/reset?token={token}\n\nThe link works for 1 hour and only once. If you didn't ask for this, ignore this email; your password stays the same.", ct);
    }

    public async Task<User> ResetAsync(string? token, string? password, CancellationToken ct)
    {
        // Check the password before using up the link.
        if (Passwords.Problem(password) is { } problem) throw new Refused(problem);
        var userId = await store.UseEmailTokenAsync(token ?? "", "reset", ct) ?? throw new Refused("This reset link has expired or was already used. Ask for a new one.");
        var user = await store.ByIdAsync(userId, ct) ?? throw new Refused("That account no longer exists.");
        if (Passwords.Problem(password, user.Email) is { } p2) throw new Refused(p2);
        await store.SetPasswordAsync(userId, await Passwords.HashAsync(password!), ct);
        // The link proves the inbox, so the address is verified too.
        await store.MarkVerifiedAsync(userId, ct);
        await store.RevokeOtherSessionsAsync(userId, null, ct);
        return (await store.ByIdAsync(userId, ct))!;
    }

    public async Task ChangePasswordAsync(User user, Guid currentSession, string? current, string? next, CancellationToken ct)
    {
        var stored = await store.PasswordHashAsync(user.Id, ct);
        if (stored is not null && !await Passwords.VerifyAsync(current ?? "", stored)) throw new Refused("Your current password is not right.", 401);
        if (Passwords.Problem(next, user.Email) is { } problem) throw new Refused(problem);
        await store.SetPasswordAsync(user.Id, await Passwords.HashAsync(next!), ct);
        await store.RevokeOtherSessionsAsync(user.Id, currentSession, ct);
    }

    // A finished provider sign-in: the account it belongs to (made if new).
    // A provider identity joins an existing account only through a verified
    // email on both sides, or when a signed-in person links it themselves.
    public async Task<User> CompleteProviderAsync(ProviderProfile p, Guid? linkTo, CancellationToken ct)
    {
        var existing = await store.UserForProviderAsync(p.Provider, p.Id, ct);
        if (linkTo is { } me)
        {
            if (existing is { } other && other != me) throw new Refused($"That {Title(p.Provider)} account is already used by another YHDE account.");
            if (existing is null) await store.LinkProviderAsync(me, p.Provider, p.Id, p.VerifiedEmail ?? "", ct);
            return await store.ByIdAsync(me, ct) ?? throw new Refused("Your account no longer exists.");
        }
        if (existing is { } id)
        {
            var linked = await store.ByIdAsync(id, ct);
            if (linked is null || linked.Disabled) throw new Refused("This account can't sign in.", 403);
            return linked;
        }
        if (p.VerifiedEmail is null)
            throw new Refused($"Your {Title(p.Provider)} account has no verified email. Verify one there, or sign up with email and password.");
        var byEmail = await store.ByEmailAsync(p.VerifiedEmail, ct);
        if (byEmail is { } found)
        {
            if (!found.User.EmailVerified)
                throw new Refused($"There is already a YHDE account for {p.VerifiedEmail}. Sign in with its password first, then connect {Title(p.Provider)} under Account.");
            await store.LinkProviderAsync(found.User.Id, p.Provider, p.Id, p.VerifiedEmail, ct);
            return found.User;
        }
        var name = new[] { CleanName(p.Name), CleanName(p.VerifiedEmail.Split('@')[0]) }
            .FirstOrDefault(n => NameRules.Problem(n, NameRules.Kind.Person) is null) ?? "New member";
        var user = await store.CreateAsync(p.VerifiedEmail, name, passwordHash: null, verified: true, ct)
            ?? throw new Refused("Could not make the account. Try again.");
        await store.LinkProviderAsync(user.Id, p.Provider, p.Id, p.VerifiedEmail, ct);
        return user;
    }

    public static string Title(string provider) => provider == "github" ? "GitHub" : provider == "google" ? "Google" : provider;

    private async Task SendAsync(string to, string subject, string text, CancellationToken ct)
    {
        // Test accounts (.test addresses) have no inbox.
        if (!Teams.TeamEndpoints.Mailable(to)) return;
        try
        {
            await email.SendAsync(to, subject, text, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Account email not sent");
            throw new Refused("We couldn't send the email right now. Try again in a few minutes.", 503);
        }
    }
}
