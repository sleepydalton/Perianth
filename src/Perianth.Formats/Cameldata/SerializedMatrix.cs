using System.Numerics;

namespace Perianth.Formats.Cameldata;

/// <summary>
/// The sixteen floats of an inverse-local matrix, in the four groups they were
/// serialized as.
/// </summary>
/// <remarks>
/// Deliberately not <c>System.Numerics.Matrix4x4</c>. That type carries its own
/// row and column conventions, and adopting them here would silently assert an
/// interpretation this reader has no evidence for. Sections 5.2 and 5.3 describe
/// only "serialized row groups"; section 5.4 then asks for "column 0" and
/// "column 1" of the result, which is element 0 and element 1 of each group.
/// Keeping the groups as they arrived leaves that mapping to the code that
/// actually needs it, where it is one visible step rather than an assumption.
/// </remarks>
/// <param name="Group0">The first four serialized floats.</param>
/// <param name="Group1">The second four.</param>
/// <param name="Group2">The third four.</param>
/// <param name="Group3">The fourth four.</param>
public readonly record struct SerializedMatrix(
    Vector4 Group0,
    Vector4 Group1,
    Vector4 Group2,
    Vector4 Group3)
{
    /// <summary>
    /// Element <paramref name="index"/> of each group, which is what section 5.4
    /// calls a column of the inverse-local matrix.
    /// </summary>
    public Vector4 Column(int index) => new(
        Group0[index],
        Group1[index],
        Group2[index],
        Group3[index]);
}
