namespace YHDE.Server.Domain;

// The operation types the server accepts (operation_system.md). Values never
// change meaning; new types are only added.
public static class OperationType
{
    // Hierarchy
    public const string CreateNode = "CreateNode";
    public const string DeleteNode = "DeleteNode";
    public const string MoveNode = "MoveNode";
    public const string RenameNode = "RenameNode";
    public const string ReorderNode = "ReorderNode";
    public const string ChangeNodeType = "ChangeNodeType"; // same node, another class (Change Type)
    public const string SetSceneRoot = "SetSceneRoot";     // another node becomes the scene root

    // Property (includes transforms: a transform is a ChangeProperty on the transform prop)
    public const string ChangeProperty = "ChangeProperty";

    // Resource
    public const string AddResource = "AddResource";
    public const string RemoveResource = "RemoveResource";
    public const string ChangeResourceProperty = "ChangeResourceProperty";

    // Asset files (assets.md): the bytes live in the blob store, the log says
    // which bytes each project path holds.
    public const string RegisterAsset = "RegisterAsset";
    public const string UpdateAsset = "UpdateAsset";
    public const string MoveAsset = "MoveAsset";
    public const string DeleteAsset = "DeleteAsset";

    // Live text files (text_editing.md): one edit of a shared text file,
    // transformed by the server against concurrent edits.
    public const string EditText = "EditText";

    private static readonly HashSet<string> _coreTypes = new(StringComparer.Ordinal)
    {
        CreateNode, DeleteNode, MoveNode, RenameNode, ReorderNode, ChangeNodeType, SetSceneRoot,
        ChangeProperty,
        AddResource, RemoveResource, ChangeResourceProperty,
        RegisterAsset, UpdateAsset, MoveAsset, DeleteAsset,
        EditText,
    };

    public static bool IsKnown(string type) => _coreTypes.Contains(type);
}
