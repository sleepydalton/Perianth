namespace Perianth.Formats.Diagnostics;

/// <summary>
/// Why an input could not be exported. Three kinds, closed, from porting
/// specification section 13. Callers branch on this, so the distinction between
/// them is behaviour and not commentary.
/// </summary>
public enum RefusalKind
{
    /// <summary>
    /// The bytes contradict a recognized grammar: truncation, an invalid count
    /// or index, a cycle, a non-finite value, an inconsistent declared length.
    /// The file itself is at fault.
    /// </summary>
    Malformed,

    /// <summary>
    /// A coherent but unimplemented mode, layout, code, family, association or
    /// output representation — <em>or a request the data cannot satisfy</em>.
    /// A pose time past the end of a clip is Unsupported, not Malformed: the
    /// file is intact and another time works. Telling someone their asset is
    /// broken because a number was typed too large is a defect.
    /// </summary>
    Unsupported,

    /// <summary>
    /// Something the export needs is not available: allocation, memory or disk
    /// capacity, or a required file that is absent, unreadable, or not a
    /// regular file. Nothing is wrong with either the bytes or the request.
    /// Specification section 13 lists absence and capacity as separate rows and
    /// section 12.1 gives them separate identifiers, but they are one kind:
    /// callers branch on whose fault it is, and neither is anybody's.
    /// </summary>
    Resource,
}
