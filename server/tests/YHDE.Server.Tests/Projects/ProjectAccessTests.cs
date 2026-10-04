using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using YHDE.Server.Admin;
using YHDE.Server.Gateway;
using YHDE.Server.Persistence;
using YHDE.Server.Persistence.Migrations;
using YHDE.Server.Projects;

namespace YHDE.Server.Tests.Projects;

// Many game projects per server, invite codes and the admin sign-in
// (projects.md, admin.md).
public sealed class ProjectAccessTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Invite_codes_are_readable_and_forgiving_to_type()
    {
        var code = InviteCode.New();
        code.Should().MatchRegex("^YHDE(-[0-9A-HJKMNP-TV-Z]{4}){4}$");
        InviteCode.New().Should().NotBe(code);
        InviteCode.LooksLikeCode(code).Should().BeTrue();
        InviteCode.Hash(code.ToLowerInvariant().Replace("-", " ")).Should().Equal(InviteCode.Hash(code));
        InviteCode.Hash("YHDE-0000-1111-2222-3333").Should().Equal(InviteCode.Hash("yhde-oooo-iiii-2222-3333"));
        InviteCode.LooksLikeCode("0123456789abcdef").Should().BeFalse();
    }

    [Fact]
    public async Task The_server_key_opens_every_project_and_an_invite_opens_one()
    {
        var project = Guid.NewGuid();
        var store = Substitute.For<IProjectStore>();
        const string code = "YHDE-ABCD-EFGH-JKMN-PQRS";
        store.ProjectForCodeAsync(Arg.Is<string>(c => InviteCode.Normalize(c) == InviteCode.Normalize(code)), Arg.Any<CancellationToken>())
            .Returns(project);
        var gate = new AccessGate(Config(("Yhde:AccessKey", Key)), NullLogger<AccessGate>.Instance, store);

        (await gate.AdmitAsync("Bearer " + Key, default))!.ProjectId.Should().BeNull();
        var invited = await gate.AdmitAsync("Bearer " + code.ToLowerInvariant(), default);
        invited!.ProjectId.Should().Be(project);
        invited.Allows(project).Should().BeTrue();
        invited.Allows(Guid.NewGuid()).Should().BeFalse();
        (await gate.AdmitAsync("Bearer YHDE-0000-0000-0000-0000", default)).Should().BeNull();
        (await gate.AdmitAsync(null, default)).Should().BeNull();
        (await gate.AdmitAsync("Basic " + code, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_revoked_code_stops_working_once_forgotten()
    {
        var store = Substitute.For<IProjectStore>();
        const string code = "YHDE-ABCD-EFGH-JKMN-PQRS";
        store.ProjectForCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var gate = new AccessGate(Config(("Yhde:AccessKey", Key)), NullLogger<AccessGate>.Instance, store);
        (await gate.AdmitAsync("Bearer " + code, default)).Should().NotBeNull();

        store.ProjectForCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Guid?)null);
        gate.ForgetInvites();
        (await gate.AdmitAsync("Bearer " + code, default)).Should().BeNull();
    }

    [Fact]
    public void Server_password_is_off_without_one_and_locks_out_guessing()
    {
        var off = new AdminAuth(Config(), TimeProvider.System, NullLogger<AdminAuth>.Instance);
        off.PasswordEnabled.Should().BeFalse();
        off.Login("anything", null).Result.Should().Be(AdminAuth.LoginResult.Disabled);

        var weak = () => new AdminAuth(Config(("Yhde:AdminPassword", "short")), TimeProvider.System, NullLogger<AdminAuth>.Instance);
        weak.Should().Throw<InvalidOperationException>();

        var auth = new AdminAuth(Config(("Yhde:AdminPassword", "correct horse battery")), TimeProvider.System, NullLogger<AdminAuth>.Instance);
        var ip = System.Net.IPAddress.Parse("203.0.113.9");
        var (ok, token) = auth.Login("correct horse battery", ip);
        ok.Should().Be(AdminAuth.LoginResult.Ok);
        auth.IsValid(token).Should().BeTrue();
        auth.IsValid("forged").Should().BeFalse();
        auth.Logout(token);
        auth.IsValid(token).Should().BeFalse();

        for (var i = 0; i < AdminAuth.MaxFailures; i++) auth.Login("wrong", ip).Result.Should().Be(AdminAuth.LoginResult.Wrong);
        auth.Login("correct horse battery", ip).Result.Should().Be(AdminAuth.LoginResult.LockedOut);
        auth.Login("correct horse battery", System.Net.IPAddress.Parse("203.0.113.10")).Result.Should().Be(AdminAuth.LoginResult.Ok);
    }

    // Against a real PostgreSQL when YHDE_TEST_DATABASE is set.
    [Fact]
    public async Task Projects_get_a_main_branch_and_codes_follow_revoke_and_archive()
    {
        if (TestDatabase.Open() is not { } db) return;
        var store = new ProjectStore(db);

        var project = await store.CreateAsync("Space Game " + Guid.NewGuid().ToString("N")[..6], default);
        project.MainBranchId.Should().NotBe(Guid.Empty);
        (await store.ListAsync(default)).Should().Contain(p => p.ProjectId == project.ProjectId);

        var (invite, code) = await store.CreateInviteAsync(project.ProjectId, "Maya", default);
        (await store.ProjectForCodeAsync(code, default)).Should().Be(project.ProjectId);
        (await store.ListInvitesAsync(project.ProjectId, default)).Should().ContainSingle(i => i.Label == "Maya");

        await store.SetArchivedAsync(project.ProjectId, true, default);
        (await store.ProjectForCodeAsync(code, default)).Should().BeNull();
        await store.SetArchivedAsync(project.ProjectId, false, default);
        (await store.ProjectForCodeAsync(code, default)).Should().Be(project.ProjectId);

        await store.RevokeInviteAsync(invite.InviteId, default);
        (await store.ProjectForCodeAsync(code, default)).Should().BeNull();

        await store.RenameAsync(project.ProjectId, "Renamed", default);
        (await store.GetAsync(project.ProjectId, default))!.Name.Should().Be("Renamed");
        (await store.StatsAsync(default)).Should().Contain(s => s.ProjectId == project.ProjectId);
    }
}
