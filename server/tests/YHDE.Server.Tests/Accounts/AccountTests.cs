using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using YHDE.Server.Accounts;

namespace YHDE.Server.Tests.Accounts;

public sealed class PasswordTests
{
    [Fact]
    public async Task Hashes_verify_only_the_same_password_and_are_salted()
    {
        var a = await Passwords.HashAsync("correct horse battery");
        var b = await Passwords.HashAsync("correct horse battery");
        a.Should().StartWith("$argon2id$v=19$m=19456,t=2,p=1$");
        a.Should().NotBe(b, "every password gets its own salt");
        (await Passwords.VerifyAsync("correct horse battery", a)).Should().BeTrue();
        (await Passwords.VerifyAsync("correct horse batterY", a)).Should().BeFalse();
        (await Passwords.VerifyAsync("", a)).Should().BeFalse();
        (await Passwords.VerifyAsync("anything", null)).Should().BeFalse();
        (await Passwords.VerifyAsync("anything", "$argon2id$v=19$m=1,t=1,p=1$xx$yy")).Should().BeFalse();
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("aaaaaaaaaaaa", false)]
    [InlineData("password123", false)]
    [InlineData("ada@example.com", false)]
    [InlineData("my jump feels floaty", true)]
    public void New_passwords_are_checked(string password, bool ok) =>
        (Passwords.Problem(password, "ada@example.com") is null).Should().Be(ok);

    [Fact]
    public void Tokens_are_long_random_and_url_safe()
    {
        var t = Secrets.NewToken();
        t.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        Secrets.NewToken().Should().NotBe(t);
        Secrets.PkceChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk").Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }
}

// A mailbox for the tests.
public sealed class TestMail : IEmailSender
{
    public List<(string To, string Subject, string Text)> Sent { get; } = [];
    public Task SendAsync(string to, string subject, string text, CancellationToken ct)
    {
        lock (Sent) Sent.Add((to, subject, text));
        return Task.CompletedTask;
    }
    public string LinkToken(string to, string path) =>
        Regex.Match(Sent.Last(m => m.To == to).Text, Regex.Escape(path) + @"\?token=([A-Za-z0-9_-]+)").Groups[1].Value;
}

[Collection("integration")]
public sealed class AccountServiceTests
{
    private const string Base = "https://yhde.example";

    private static (AccountService, AccountStore, TestMail)? Open()
    {
        if (TestDatabase.Open() is not { } db) return null;
        var store = new AccountStore(db);
        var mail = new TestMail();
        return (new AccountService(store, mail, NullLogger<AccountService>.Instance), store, mail);
    }

    private static string NewEmail() => $"t{Guid.NewGuid():N}@example.com";
    private static string Ip() => "198.51.100." + Random.Shared.Next(1, 250);

    [Fact]
    public async Task Sign_up_verify_and_sign_in()
    {
        if (Open() is not var (accounts, store, mail)) return;
        var email = NewEmail();
        await accounts.RegisterAsync(email.ToUpperInvariant(), "my jump feels floaty", "Ada", Ip(), Base, default);

        var (user, hash) = (await store.ByEmailAsync(email, default))!.Value;
        user.EmailVerified.Should().BeFalse();
        user.Name.Should().Be("Ada");
        hash.Should().StartWith("$argon2id$").And.NotContain("floaty");

        var token = mail.LinkToken(email, "/app#/verify");
        token.Should().NotBeEmpty();
        (await accounts.VerifyAsync(token, default)).Should().BeTrue();
        (await accounts.VerifyAsync(token, default)).Should().BeFalse("links work once");
        (await store.ByIdAsync(user.Id, default))!.EmailVerified.Should().BeTrue();

        (await accounts.LoginAsync(email, "my jump feels floaty", Ip(), default)).Id.Should().Be(user.Id);
        await FluentActions.Awaiting(() => accounts.LoginAsync(email, "my jump feels floatY", Ip(), default))
            .Should().ThrowAsync<AccountService.Refused>().Where(r => r.Status == 401);
    }

    [Fact]
    public async Task Signing_up_twice_gives_no_hint_and_warns_the_owner()
    {
        if (Open() is not var (accounts, store, mail)) return;
        var email = NewEmail();
        await accounts.RegisterAsync(email, "my jump feels floaty", "Ada", Ip(), Base, default);
        var first = (await store.ByEmailAsync(email, default))!.Value;
        await accounts.RegisterAsync(email, "someone elses pass", "Eve", Ip(), Base, default);
        var after = (await store.ByEmailAsync(email, default))!.Value;
        after.PasswordHash.Should().Be(first.PasswordHash, "the second sign-up changes nothing");
        mail.Sent.Last(m => m.To == email).Subject.Should().Contain("tried to sign up");
    }

    [Fact]
    public async Task Unknown_emails_and_wrong_passwords_get_the_same_answer_and_are_limited()
    {
        if (Open() is not var (accounts, _, _)) return;
        var email = NewEmail();
        await accounts.RegisterAsync(email, "my jump feels floaty", "Ada", Ip(), Base, default);
        var unknown = await Record.ExceptionAsync(() => accounts.LoginAsync(NewEmail(), "whatever pass", Ip(), default));
        var wrong = await Record.ExceptionAsync(() => accounts.LoginAsync(email, "whatever pass", Ip(), default));
        unknown!.Message.Should().Be(wrong!.Message);

        var ip = Ip();
        var statuses = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            var e = await Record.ExceptionAsync(() => accounts.LoginAsync(email, "wrong wrong wrong", ip, default));
            statuses.Add(((AccountService.Refused)e!).Status);
        }
        statuses.Should().Contain(429, "guessing one account is limited");
        await FluentActions.Awaiting(() => accounts.LoginAsync(email, "my jump feels floaty", Ip(), default))
            .Should().ThrowAsync<AccountService.Refused>().Where(r => r.Status == 429, "even the right password waits out the limit");
    }

    [Fact]
    public async Task Reset_sets_a_new_password_once_and_ends_every_session()
    {
        if (Open() is not var (accounts, store, mail)) return;
        var email = NewEmail();
        await accounts.RegisterAsync(email, "my jump feels floaty", "Ada", Ip(), Base, default);
        var user = (await store.ByEmailAsync(email, default))!.Value.User;
        var session = await store.CreateSessionAsync(user.Id, "web", "test", "203.0.113.1", TimeSpan.FromDays(1), default);
        (await store.SessionAsync(session, "web", default)).Should().NotBeNull();

        await accounts.ForgotAsync(email, Ip(), Base, default);
        await accounts.ForgotAsync(NewEmail(), Ip(), Base, default); // unknown address: silently nothing
        var token = mail.LinkToken(email, "/app#/reset");

        await FluentActions.Awaiting(() => accounts.ResetAsync(token, "short", default)).Should().ThrowAsync<AccountService.Refused>();
        (await accounts.ResetAsync(token, "a brand new sentence", default)).EmailVerified.Should().BeTrue();
        await FluentActions.Awaiting(() => accounts.ResetAsync(token, "another new sentence", default))
            .Should().ThrowAsync<AccountService.Refused>("a reset link works once");

        (await store.SessionAsync(session, "web", default)).Should().BeNull("a reset signs out everywhere");
        (await accounts.LoginAsync(email, "a brand new sentence", Ip(), default)).Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task Sessions_can_be_listed_and_ended()
    {
        if (Open() is not var (accounts, store, _)) return;
        var email = NewEmail();
        await accounts.RegisterAsync(email, "my jump feels floaty", "Ada", Ip(), Base, default);
        var user = (await store.ByEmailAsync(email, default))!.Value.User;
        var a = await store.CreateSessionAsync(user.Id, "web", "Firefox", "203.0.113.1", TimeSpan.FromDays(1), default);
        var b = await store.CreateSessionAsync(user.Id, "web", "Chrome", "203.0.113.2", TimeSpan.FromDays(1), default);
        var list = await store.SessionsAsync(user.Id, default);
        list.Select(s => s.Device).Should().BeEquivalentTo("Firefox", "Chrome");
        (await store.SessionAsync(a, "editor", default)).Should().BeNull("a web session is not an editor token");

        var chrome = list.Single(s => s.Device == "Chrome");
        (await store.RevokeSessionAsync(Guid.NewGuid(), chrome.Id, default)).Should().BeFalse("only its owner can end it");
        (await store.RevokeSessionAsync(user.Id, chrome.Id, default)).Should().BeTrue();
        (await store.SessionAsync(b, "web", default)).Should().BeNull();
        (await store.SessionAsync(a, "web", default)).Should().NotBeNull();
        (await store.SessionAsync("not-a-token", "web", default)).Should().BeNull();
    }

    [Fact]
    public async Task Providers_join_accounts_only_through_verified_emails()
    {
        if (Open() is not var (accounts, store, mail)) return;

        // A new person: a new, verified account.
        var fresh = NewEmail();
        var created = await accounts.CompleteProviderAsync(new ProviderProfile("github", "gh-" + Guid.NewGuid(), fresh, "Ada L"), null, default);
        created.EmailVerified.Should().BeTrue();
        created.HasPassword.Should().BeFalse();
        (await accounts.CompleteProviderAsync(new ProviderProfile("github", (await store.ProvidersAsync(created.Id, default))[0].Provider == "github" ? (await LinkedId(store, created.Id)) : "", fresh, "Ada L"), null, default))
            .Id.Should().Be(created.Id, "the same GitHub account signs in to the same YHDE account");

        // An unverified password account with the same email is not taken over.
        var victim = NewEmail();
        await accounts.RegisterAsync(victim, "my jump feels floaty", "Victim", Ip(), Base, default);
        await FluentActions.Awaiting(() => accounts.CompleteProviderAsync(new ProviderProfile("google", "g-" + Guid.NewGuid(), victim, "Attacker"), null, default))
            .Should().ThrowAsync<AccountService.Refused>();

        // Once the owner verified it, the same email links.
        await accounts.VerifyAsync(mail.LinkToken(victim, "/app#/verify"), default);
        var linked = await accounts.CompleteProviderAsync(new ProviderProfile("google", "g-" + Guid.NewGuid(), victim, "Victim"), null, default);
        linked.Email.Should().Be(victim);

        // No verified email from the provider: no account.
        await FluentActions.Awaiting(() => accounts.CompleteProviderAsync(new ProviderProfile("github", "gh-" + Guid.NewGuid(), null, "Nobody"), null, default))
            .Should().ThrowAsync<AccountService.Refused>();
    }

    private static async Task<string> LinkedId(AccountStore store, Guid userId)
    {
        await using var conn = await TestDatabase.Open()!.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT provider_user_id FROM oauth_links WHERE user_id = @u", conn);
        cmd.Parameters.AddWithValue("u", userId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
