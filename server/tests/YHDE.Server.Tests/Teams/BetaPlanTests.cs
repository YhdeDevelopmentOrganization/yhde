using FluentAssertions;
using YHDE.Server.Accounts;
using YHDE.Server.Projects;
using YHDE.Server.Teams;

namespace YHDE.Server.Tests.Teams;

[Collection("integration")]
public sealed class BetaPlanTests
{
    [Fact]
    public void Before_1_0_every_new_team_gets_the_beta_plan()
    {
        Plans.Open.Should().BeFalse("the server is still 0.x");
        Plans.Pickable("studio").Should().Be(Plans.Beta);
        Plans.Pickable(null).Should().Be(Plans.Beta);
        Plans.Find("beta").Should().Be(Plans.Beta);
    }

    [Fact]
    public void A_beta_tester_has_three_projects_of_four_people_in_2_GB()
    {
        var team = new Team(Guid.NewGuid(), "Beta", Guid.NewGuid(), "beta", "month", 0, 0, DateTimeOffset.UtcNow);
        team.Seats.Should().Be(4);
        team.StorageBytes.Should().Be(2L * 1024 * 1024 * 1024);
        Plans.Beta.MaxExtraSeats.Should().Be(0);
        team.PeoplePerProject.Should().Be(3, "three invited people in each project");
        team.MaxProjects.Should().Be(3);
        (team with { FreeSeats = 2 }).PeoplePerProject.Should().Be(5, "free seats from staff add room in every project");
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g', (byte)'>' }, null)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8' }, null)]
    [InlineData(new byte[0], null)]
    public void Project_images_are_told_apart_by_their_first_bytes(byte[] data, string? type) =>
        TeamStore.ImageType(data).Should().Be(type);

    [Fact]
    public async Task A_project_image_is_stored_replaced_and_removed_with_the_project()
    {
        if (TestDatabase.Open() is not { } db) return;
        var teams = new TeamStore(db);
        var projects = new ProjectStore(db);
        var accounts = new AccountStore(db);
        var owner = (await accounts.CreateAsync($"t{Guid.NewGuid():N}@example.com", "Ada", null, verified: true, default))!;
        var team = await teams.CreateAsync(owner.Id, "Pictures " + Guid.NewGuid().ToString("N")[..6], Plans.Beta, "month", default);
        var p = await projects.CreateAsync("With image", default);
        await teams.AttachProjectAsync(p.ProjectId, team.Id, default);

        (await teams.ImageAsync(p.ProjectId, default)).Should().BeNull();
        (await teams.ImageStampsAsync([p.ProjectId], default)).Should().BeEmpty();

        await teams.SetImageAsync(p.ProjectId, "image/png", [1, 2, 3], default);
        await teams.SetImageAsync(p.ProjectId, "image/jpeg", [4, 5], default);
        var image = (await teams.ImageAsync(p.ProjectId, default))!.Value;
        image.ContentType.Should().Be("image/jpeg");
        image.Data.Should().Equal(4, 5);
        (await teams.ImageStampsAsync([p.ProjectId], default)).Should().ContainKey(p.ProjectId);

        await teams.RemoveImageAsync(p.ProjectId, default);
        (await teams.ImageAsync(p.ProjectId, default)).Should().BeNull();

        await teams.SetImageAsync(p.ProjectId, "image/png", [1], default);
        await projects.SetArchivedAsync(p.ProjectId, true, default);
        (await projects.DeleteArchivedAsync(p.ProjectId, default)).Should().NotBeNull("the image doesn't keep the project from being deleted");
        (await teams.ImageAsync(p.ProjectId, default)).Should().BeNull();
    }
}
