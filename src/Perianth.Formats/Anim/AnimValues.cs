namespace Perianth.Formats.Anim;

/// <summary>Which of a node's three local transform channels a value belongs to.</summary>
/// <remarks>
/// The names mirror the file's selector-stream tags: <c>TRAI</c>, <c>ROTI</c>
/// and <c>SCAI</c> index the translation, rotation and scale channels. The scale
/// channel doubles as the visibility channel.
/// </remarks>
public enum AnimChannel
{
    /// <summary>Local translation, from the <c>TRAI</c>/<c>DTRA</c>/<c>TRAD</c> chunks.</summary>
    Translation,

    /// <summary>Local rotation, from the <c>ROTI</c>/<c>DROT</c>/<c>ROTD</c> chunks.</summary>
    Rotation,

    /// <summary>Local scale, from the <c>SCAI</c>/<c>DSCA</c>/<c>SCAD</c> chunks.</summary>
    Scale,
}

/// <summary>A decoded translation or scale, in binary64.</summary>
public readonly record struct AnimVec3(double X, double Y, double Z)
{
    /// <summary>The translation identity, <c>(0, 0, 0)</c>.</summary>
    public static AnimVec3 Zero => new(0.0, 0.0, 0.0);

    /// <summary>The scale identity, <c>(1, 1, 1)</c>.</summary>
    public static AnimVec3 One => new(1.0, 1.0, 1.0);
}

/// <summary>A decoded unit quaternion in <c>(x, y, z, w)</c> order, binary64.</summary>
public readonly record struct AnimQuat(double X, double Y, double Z, double W)
{
    /// <summary>The rotation identity, <c>(0, 0, 0, 1)</c>.</summary>
    public static AnimQuat Identity => new(0.0, 0.0, 0.0, 1.0);
}
