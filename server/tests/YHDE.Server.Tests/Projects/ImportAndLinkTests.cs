using YHDE.Server.Admin;
using YHDE.Server.Projects;

namespace YHDE.Server.Tests.Projects;

public sealed class ImportAndLinkTests
{
    [Theory]
    // Reference values: Python's uuid.uuid5(UUID("5f0e6c1a-9a55-4f3e-8b0b-79d2a1c3e4f5"), "file:" + path),
    // the same RFC 4122 v5 the editor's Uuid::from_name computes.
    [InlineData("res://player.gd", "3909d0a1-bbe6-557c-80bd-4b35f7cf05ac")]
    public void Imported_files_get_the_ids_editors_give_them(string path, string expected)
    {
        Assert.Equal(Guid.Parse(expected), ProjectImporter.FileId(path));
    }

    [Fact]
    public void Link_tokens_are_short_and_never_look_like_invite_codes()
    {
        for (var i = 0; i < 200; i++)
        {
            var token = LinkToken.New();
            Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$", token);
            Assert.True(LinkToken.LooksLikeToken(token));
            Assert.True(LinkToken.LooksLikeToken(token.ToLowerInvariant()));
            Assert.False(InviteCode.LooksLikeCode(token));
        }
        Assert.False(LinkToken.LooksLikeToken(InviteCode.New()));
        Assert.False(LinkToken.LooksLikeToken("../etc"));
    }

    [Fact]
    public void A_link_token_and_an_invite_code_never_share_a_hash()
    {
        var token = LinkToken.New();
        Assert.NotEqual(LinkToken.Hash(token), InviteCode.Hash(token));
    }

    [Theory]
    [InlineData("res://levels/main.tscn", "Scenes")]
    [InlineData("res://player.gd", "Scripts")]
    [InlineData("res://art/hero.PNG", "Images")]
    [InlineData("res://models/ship.glb", "3D models")]
    [InlineData("res://art/hero.png.import", "Import settings")]
    [InlineData("res://project.godot", "Text and config")]
    [InlineData("res://thing.xyz", "Other")]
    public void Files_are_grouped_by_kind_for_the_storage_chart(string path, string kind)
    {
        Assert.Equal(kind, AdminStats.Kind(path));
    }
}
