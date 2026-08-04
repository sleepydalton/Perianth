using System;
using System.Numerics;

namespace Perianth.Formats.Cameldata;

/// <summary>
/// One mode-2 constant record, at stride <c>136 + (flags ? 8 : 0)</c>.
/// </summary>
/// <param name="SurfaceOrigin">The surface origin, used to project UV0.</param>
/// <param name="SurfaceU">The surface U axis.</param>
/// <param name="SurfaceV">The surface V axis.</param>
/// <param name="DataIndices">Sixteen bytes at +48, kept and not interpreted.</param>
/// <param name="InverseLocal">The inverse-local matrix.</param>
/// <param name="PositionXScale">The position-X scale at +128.</param>
/// <param name="InverseUnitScale">The inverse unit scale at +132.</param>
/// <param name="OptionalTail">The eight-byte tail present only when the header flag is set.</param>
public readonly record struct Mode2Constant(
    Vector4 SurfaceOrigin,
    Vector4 SurfaceU,
    Vector4 SurfaceV,
    ReadOnlyMemory<byte> DataIndices,
    SerializedMatrix InverseLocal,
    float PositionXScale,
    float InverseUnitScale,
    ReadOnlyMemory<byte> OptionalTail);

/// <summary>
/// One mode-3 constant record, at stride <c>152 + (flags ? 8 : 0)</c>.
/// </summary>
/// <param name="SurfaceOrigin">The surface origin.</param>
/// <param name="SurfaceU">The surface U axis.</param>
/// <param name="SurfaceV">The surface V axis.</param>
/// <param name="DataIndices">Sixteen bytes at +48, kept and not interpreted.</param>
/// <param name="XyBase">Base index into the XY array.</param>
/// <param name="ZBase">Base index into the Z array.</param>
/// <param name="Uv0Base">Base index into the UV0 array.</param>
/// <param name="PackedFlags">Unified-UV0 selector, UV scale index and Z bit width.</param>
/// <param name="InverseLocal">The inverse-local matrix.</param>
/// <param name="PositionXScale">The position-X scale at +144.</param>
/// <param name="InverseUnitScale">The inverse unit scale at +148.</param>
/// <param name="OptionalTail">The eight-byte tail present only when the header flag is set.</param>
public readonly record struct Mode3Constant(
    Vector4 SurfaceOrigin,
    Vector4 SurfaceU,
    Vector4 SurfaceV,
    ReadOnlyMemory<byte> DataIndices,
    uint XyBase,
    uint ZBase,
    uint Uv0Base,
    uint PackedFlags,
    SerializedMatrix InverseLocal,
    float PositionXScale,
    float InverseUnitScale,
    ReadOnlyMemory<byte> OptionalTail)
{
    /// <summary>Bit 0: whether UV0 comes from the unified packed array.</summary>
    public bool UsesUnifiedUv0 => (PackedFlags & 1) != 0;

    /// <summary>Bits 1 and 2: which entry of the UV scale table applies.</summary>
    /// <remarks>Index 3 has no scale and refuses where it is used.</remarks>
    public int Uv0ScaleIndex => (int)((PackedFlags >> 1) & 3);

    /// <summary>Bits 3 to 7, plus one: the width of a packed Z index, from 1 to 32.</summary>
    public int ZBitWidth => (int)((PackedFlags >> 3) & 0x1F) + 1;
}
