using System.Text.Json;
using FluentAssertions;
using YHDE.Server.Domain;
using YHDE.Server.Persistence.Repositories;
using YHDE.Server.Projects;

namespace YHDE.Server.Tests.Assets;

// The server and the add-on share and refuse the same paths, and
// two files that differ only in capitals cannot both exist on a branch.
public sealed class PathRulesTests
{
    public static TheoryData<string, bool> SharedTable()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "path_rules.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var data = new TheoryData<string, bool>();
        foreach (var row in doc.RootElement.GetProperty("paths").EnumerateObject()) data.Add(row.Name, row.Value.GetBoolean());
        return data;
    }

    [Theory]
    [MemberData(nameof(SharedTable))]
    public void Server_agrees_with_the_shared_path_table(string path, bool shared) =>
        YHDE.Server.Assets.AssetRules.IsValidPath(path).Should().Be(shared);

    [Fact]
    public async Task A_name_that_differs_only_in_capitals_is_found_while_the_file_lives()
    {
        if (TestDatabase.Open() is not { } db) return;
        var project = await new ProjectStore(db).CreateAsync("Case", default);
        var ops = new OperationRepository(db);
        var branch = project.MainBranchId;
        async Task Op(string type, string json) => await ops.CommitAsync(
            new OperationSubmission(Guid.NewGuid(), type, ProjectImporter.FileId(JsonDocument.Parse(json).RootElement.GetProperty("s").GetString()!), json, Guid.NewGuid(), 0),
            branch, Guid.Empty, Guid.Empty, default);
        var hash = new string('a', 64);

        await Op(OperationType.RegisterAsset, $$"""{"s":"res://Player.gd","h":"{{hash}}","n":1}""");
        (await ops.LivePathsDifferingInCaseAsync(branch, "res://player.gd", default)).Should().Equal("res://Player.gd");
        (await ops.LivePathsDifferingInCaseAsync(branch, "res://Player.gd", default)).Should().BeEmpty("the same name is no collision");

        await Op(OperationType.MoveAsset, $$"""{"s":"res://hero.gd","f":"res://Player.gd","h":"{{hash}}","n":1}""");
        (await ops.LivePathsDifferingInCaseAsync(branch, "res://player.gd", default)).Should().BeEmpty("it was moved away");
        (await ops.LivePathsDifferingInCaseAsync(branch, "res://HERO.gd", default)).Should().Equal("res://hero.gd");

        await Op(OperationType.DeleteAsset, """{"s":"res://hero.gd"}""");
        (await ops.LivePathsDifferingInCaseAsync(branch, "res://HERO.gd", default)).Should().BeEmpty("it was deleted");
    }
}
