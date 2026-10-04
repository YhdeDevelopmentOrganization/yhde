using FluentAssertions;
using YHDE.Server.Assets;
using YHDE.Server.Domain;
using YHDE.Server.Operations;

namespace YHDE.Server.Tests.Assets;

// Asset operations enter the log only when their bytes are stored and their
// path is a safe project path (assets.md).
public sealed class AssetRulesTests : IDisposable
{
    private readonly TempBlobStore _temp = new();

    public void Dispose() => _temp.Dispose();

    private static OperationSubmission Op(string type, string payload) =>
        new(Guid.NewGuid(), type, Guid.NewGuid(), payload, Guid.NewGuid(), 0);

    [Fact]
    public async Task Accepts_a_registration_whose_bytes_are_stored()
    {
        var hash = await _temp.PutAsync([1, 2, 3]);

        AssetRules.Validate(Op(OperationType.RegisterAsset, $$"""{"s":"res://art/hero.png","h":"{{hash}}","n":3}"""), _temp.Store)
            .Should().BeNull();
    }

    [Fact]
    public void Refuses_a_registration_before_the_upload()
    {
        var hash = TempBlobStore.HashOf([9, 9]);

        AssetRules.Validate(Op(OperationType.UpdateAsset, $$"""{"s":"res://a.png","h":"{{hash}}","n":2}"""), _temp.Store)!
            .Value.code.Should().Be(RejectionCode.AssetMissing);
    }

    [Fact]
    public async Task Refuses_a_size_that_does_not_match_the_bytes()
    {
        var hash = await _temp.PutAsync([1, 2, 3]);

        AssetRules.Validate(Op(OperationType.RegisterAsset, $$"""{"s":"res://a.png","h":"{{hash}}","n":4}"""), _temp.Store)
            .Should().NotBeNull();
    }

    [Fact]
    public void Moves_need_a_valid_different_source_and_deletes_need_no_bytes()
    {
        AssetRules.Validate(Op(OperationType.DeleteAsset, """{"s":"res://old.png"}"""), _temp.Store).Should().BeNull();
        AssetRules.Validate(Op(OperationType.MoveAsset, """{"s":"res://a.png","f":"res://a.png","h":"x","n":1}"""), _temp.Store)
            .Should().NotBeNull();
        AssetRules.Validate(Op(OperationType.MoveAsset, """{"s":"res://a.png","f":"res://../a.png"}"""), _temp.Store)
            .Should().NotBeNull();
    }

    [Fact]
    public void Other_operations_are_not_its_concern() =>
        AssetRules.Validate(Op(OperationType.ChangeProperty, """{"s":"res://../x"}"""), _temp.Store).Should().BeNull();

    [Theory]
    [InlineData("res://icon.svg", true)]
    [InlineData("res://art/hero.png", true)]
    [InlineData("res://art/.gdignore", true)]
    [InlineData("res://.yhdeignore", true)]
    [InlineData("res://", false)]
    [InlineData("res://../outside.png", false)]
    [InlineData("res://a/../../b.png", false)]
    [InlineData("res://.godot/imported/monkey.blend-58e6f4f5c66939b6820792b8c88265cc.scn", true)]
    [InlineData("res://.godot/imported/monkey.blend-58e6f4f5c66939b6820792b8c88265cc.md5", true)]
    [InlineData("res://.godot/imported/sub/x.ctex", false)]
    [InlineData("res://.godot/imported/.hidden", false)]
    [InlineData("res://.godot/uid_cache.bin", false)]
    [InlineData("res://.godot/editor/state.cfg", false)]
    [InlineData("res://Addons/YHDE/plugin.gd", false)]
    [InlineData("res://.git/hooks/pre-commit", false)]
    [InlineData("res://addons/yhde/bin/libyhde.so", false)]
    [InlineData("res://a\\b.png", false)]
    [InlineData("res://a//b.png", false)]
    [InlineData("res://scene.tscn::1", false)]
    [InlineData("user://save.dat", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("res://trailing./x.png", false)]
    public void Paths_must_stay_inside_the_shared_project(string path, bool valid) =>
        AssetRules.IsValidPath(path).Should().Be(valid);

    [Fact]
    public void Project_godot_can_be_changed_but_never_deleted_or_moved_away()
    {
        AssetRules.Validate(Op(OperationType.DeleteAsset, """{"s":"res://project.godot"}"""), _temp.Store)
            .Should().NotBeNull();
        AssetRules.Validate(Op(OperationType.MoveAsset, """{"s":"res://old.godot","f":"res://project.godot","h":"x","n":1}"""), _temp.Store)
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Dependencies_must_be_project_paths()
    {
        var hash = await _temp.PutAsync([1, 2, 3]);

        AssetRules.Validate(Op(OperationType.RegisterAsset,
            $$"""{"s":"res://hero.tscn","h":"{{hash}}","n":3,"d":["res://art/hero.png","res://hero.fbx"]}"""), _temp.Store)
            .Should().BeNull();
        AssetRules.Validate(Op(OperationType.RegisterAsset,
            $$"""{"s":"res://hero.tscn","h":"{{hash}}","n":3,"d":["res://../x.png"]}"""), _temp.Store)
            .Should().NotBeNull();
        AssetRules.Validate(Op(OperationType.RegisterAsset,
            $$"""{"s":"res://hero.tscn","h":"{{hash}}","n":3,"d":"res://x.png"}"""), _temp.Store)
            .Should().NotBeNull();
    }
}
