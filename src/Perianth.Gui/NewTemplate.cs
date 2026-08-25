namespace Perianth.Gui;

/// <summary>What a new thing can be based on.</summary>
/// <remarks>
/// One shape for all three kinds, because the pane asks one question of each:
/// which shipped thing is yours like? What differs is only where the list comes
/// from and what the second line says, so a type per kind would be three ways of
/// holding a name and a path.
/// </remarks>
/// <param name="Name">What to show — the game's own name for it.</param>
/// <param name="Detail">Where it is or what slot it fills, shown underneath.</param>
/// <param name="Path">The archive path of the file to copy.</param>
/// <param name="Entity">
/// For a prop, which entity in that layer. Empty for the other kinds, which
/// have one declaration per file.
/// </param>
/// <param name="X">Where the original stands, so a copy can start beside it.</param>
/// <param name="Y">The same.</param>
/// <param name="Z">The same.</param>
public sealed record NewTemplate(
    string Name, string Detail, string Path, string Entity = "",
    double X = 0, double Y = 0, double Z = 0)
{
    /// <summary>Whether a search word matches this.</summary>
    public bool Matches(string word) =>
        Name.Contains(word, System.StringComparison.OrdinalIgnoreCase)
        || Detail.Contains(word, System.StringComparison.OrdinalIgnoreCase);
}
