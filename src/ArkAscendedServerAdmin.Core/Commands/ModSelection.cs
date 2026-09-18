using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Commands;

/// <summary>
/// One entry of an ordered mod list as the editors save it (issue #24). A disabled entry keeps its place
/// in the list and is left out of <c>-mods</c>. A bare id converts to an enabled entry so callers that
/// only order ids stay as they are.
/// </summary>
public sealed record ModSelection(int ModId, bool Enabled = true)
{
    public static implicit operator ModSelection(int modId) => new(modId);
}

/// <summary>One entry of an ordered mod list as the pages read it: the library entry plus its enabled flag.</summary>
public sealed record ModListItem(ModLibraryEntry Mod, bool Enabled);
