using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Anim;

/// <summary>
/// A decoded ANIM container: its node table and hierarchy, its per-channel
/// selector streams, and the machinery to reconstruct a node's local transform
/// at an integer sample.
/// </summary>
/// <remarks>
/// <para>
/// This is the format layer. It resolves and decodes values — static, flat, and
/// change-key compressed — and enforces the codec's own refusals: an unknown
/// rotation code, a non-unit quaternion, a reserved bit, a malformed channel
/// table. What it does not do is compose the hierarchy, interpolate between
/// samples, or choose between a clip and its setup; those are the caller's, and
/// keep this a reader.
/// </para>
/// <para>
/// It is a class, not a record: it holds the whole source buffer so a channel
/// can be resolved lazily, and value equality over that buffer is a comparison
/// nobody wants.
/// </para>
/// </remarks>
public sealed class AnimFile
{
    // The header word at +0x1C selects the packed rotation layout. Constants
    // read from the runtime as binary32; the quantised scale is corpus-derived,
    // which is what the norm check guards.
    private static readonly double SmallestThreeScale = 4.3161006e-05f;
    private static readonly double SmallestThreeBias = 0.70710677f;
    private const double QuantisedScale = 1.0 / 16384.0;
    private const double QuantisedNormTolerance = 1.0e-3;

    internal const int Root = -1;

    // The transform tags searched in order from +0x3C, bounding one another.
    internal static readonly string[] OrderedTags = ["DTRA", "DROT", "DSCA", "TRAI", "ROTI", "SCAI", "NAME"];

    private readonly ReadOnlyMemory<byte> _source;
    private readonly Dictionary<string, int> _offsets;
    private readonly Dictionary<AnimChannel, ImmutableArray<ushort>> _streams;
    private readonly Dictionary<string, int> _nameToIndex;

    internal AnimFile(
        ReadOnlyMemory<byte> source,
        ImmutableArray<string> names,
        Dictionary<string, int> nameToIndex,
        ImmutableArray<int> parents,
        Dictionary<AnimChannel, ImmutableArray<ushort>> streams,
        Dictionary<string, int> offsets,
        float fps,
        int sampleCount,
        int rotationStride)
    {
        _source = source;
        Names = names;
        _nameToIndex = nameToIndex;
        Parents = parents;
        _streams = streams;
        _offsets = offsets;
        Fps = fps;
        SampleCount = sampleCount;
        RotationStride = rotationStride;

        // One pass rather than a scan per sample: visibility is asked for at every
        // sample of every clip.
        SelectsVisibility = false;
        foreach (ushort selector in streams[AnimChannel.Scale])
        {
            if (selector != 0xFFFF)
            {
                SelectsVisibility = true;
                break;
            }
        }
    }

    /// <summary>The animation's frame rate, in samples per second.</summary>
    public float Fps { get; }

    /// <summary>The number of samples the animation declares.</summary>
    public int SampleCount { get; }

    /// <summary>
    /// The samples that are a pose. The declared count includes one more, and it is
    /// not one: a channel keyed on that final sample carries the node's rest value
    /// rather than the end of its motion, so playing it throws whatever it names
    /// back to where the setup parks it. Measured over two characters' clips, every
    /// channel that snaps on the last sample — 33 of 33 — snaps to exactly the rest
    /// pose and to nothing else, while the rest simply hold. Since a hand parks far
    /// from the body, the visible cost was a hand across the room on the last frame
    /// of a clip, held for every frame after it in a queued export.
    /// </summary>
    public int PlayableSamples => SampleCount > 1 ? SampleCount - 1 : SampleCount;

    /// <summary>
    /// Whether the scale stream states anything at all. Every selector being
    /// <c>0xFFFF</c> is the empty stream, not a request to show everything: the
    /// file records no scale state for any node, so it selects nothing.
    /// </summary>
    /// <remarks>
    /// It matters because a node a clip names takes that clip's selector, and
    /// <c>0xFFFF</c> reveals. The files with an empty stream are overlays — they
    /// pose one joint, or a handful, and are meant to be layered over a base
    /// animation — yet they name much of the rig, so playing one alone turns on
    /// every alternate part at once. Censused over 1,481 clips from nine
    /// characters, 26 have an empty scale stream, and deferring to the setup for
    /// those changes the visible set of 15, each to exactly the count the setup
    /// itself shows. The other 1,455 are untouched.
    /// </remarks>
    public bool SelectsVisibility { get; }

    /// <summary>Bytes per packed rotation entry, chosen by the header codec selector.</summary>
    public int RotationStride { get; }

    /// <summary>The node names, in file order.</summary>
    public ImmutableArray<string> Names { get; }

    /// <summary>Each node's parent index, or <see cref="Root"/> for a root.</summary>
    public ImmutableArray<int> Parents { get; }

    /// <summary>The number of nodes.</summary>
    public int NodeCount => Names.Length;

    /// <summary>Looks up a node by its exact name.</summary>
    public bool TryGetNode(string name, out int index) => _nameToIndex.TryGetValue(name, out index);

    /// <summary>The raw selector for a node's channel: a sentinel, a static index, or a dense channel.</summary>
    public ushort Selector(AnimChannel channel, int node) => _streams[channel][node];

    /// <summary>The decoded local translation of <paramref name="node"/> at an integer sample.</summary>
    public Result<AnimVec3> DecodeTranslation(int node, int sample) => DecodeVec3(AnimChannel.Translation, node, sample, AnimVec3.Zero);

    /// <summary>The decoded local scale of <paramref name="node"/> at an integer sample.</summary>
    public Result<AnimVec3> DecodeScale(int node, int sample) => DecodeVec3(AnimChannel.Scale, node, sample, AnimVec3.One);

    /// <summary>The decoded local rotation of <paramref name="node"/> at an integer sample.</summary>
    public Result<AnimQuat> DecodeRotation(int node, int sample)
    {
        Result<byte[]?> bytes = ChannelBytes(AnimChannel.Rotation, node, sample);
        if (!bytes.TryGetValue(out byte[]? raw, out Refusal? refusal))
        {
            return refusal;
        }

        return raw is null ? Result.Ok(AnimQuat.Identity) : DecodeRotation(raw);
    }

    /// <summary>
    /// Converts a time in seconds to a sample position, bounded to the last
    /// sample, refusing a request the file cannot satisfy.
    /// </summary>
    /// <remarks>
    /// The finite, non-negative and past-the-end checks describe the request, not
    /// the file, so they are <see cref="RefusalKind.Unsupported"/>: the ANIM is
    /// intact and a different time works. Only an invalid sampling rate is a fault
    /// of the file itself.
    /// </remarks>
    public Result<double> SamplePosition(double seconds) => SamplePosition(seconds, clamp: false);

    /// <summary>
    /// The same, but holding the last sample instead of refusing past the end.
    /// </summary>
    /// <remarks>
    /// For a <em>companion</em> track only — one being read alongside the file
    /// whose timeline is being walked, rather than one the user named a time in.
    /// A setup ANIM is a rest pose and is routinely far shorter than the clip
    /// played against it — three samples, an eighth of a second, against clips
    /// of four seconds and more. Refusing there rejects the export of most
    /// animations for a reason that has nothing to do with what was asked.
    /// <para>
    /// Holding the last sample is the answer because a rest pose has no further
    /// motion to give; it is not a guess about missing data. The distinction
    /// that must survive is the other case — a static <c>--time</c> past the end
    /// of the file the user actually named — which stays a refusal, because
    /// there the request is for a moment that does not exist.
    /// </para>
    /// </remarks>
    public Result<double> ClampedSamplePosition(double seconds) => SamplePosition(seconds, clamp: true);

    private Result<double> SamplePosition(double seconds, bool clamp)
    {
        if (!double.IsFinite(seconds) || seconds < 0.0)
        {
            return Refusal.Unsupported("The pose time must be finite and nonnegative.");
        }

        if (SampleCount == 0)
        {
            if (seconds != 0.0)
            {
                return Refusal.Unsupported("The ANIM contains no samples, so only time 0 can be requested of it.");
            }

            return Result.Ok(0.0);
        }

        if (!(double.IsFinite(Fps) && Fps > 0.0))
        {
            return Refusal.Malformed("The ANIM sampling rate is invalid.");
        }

        double position = seconds * Fps;
        int lastSample = SampleCount - 1;
        if (!clamp && position > lastSample + 1.0e-9)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The pose time {seconds:g9}s is past the end of this ANIM, which has {SampleCount} samples at {Fps:g9} fps."));
        }

        return Result.Ok(Math.Min(position, lastSample));
    }

    /// <summary>The local translation at a fractional sample position, linearly interpolated.</summary>
    public Result<AnimVec3> TranslationAt(int node, double position) => Vec3At(AnimChannel.Translation, node, position, AnimVec3.Zero);

    /// <summary>The local scale at a fractional sample position, linearly interpolated.</summary>
    public Result<AnimVec3> ScaleAt(int node, double position) => Vec3At(AnimChannel.Scale, node, position, AnimVec3.One);

    /// <summary>The local rotation at a fractional sample position, interpolated by shortest-path slerp.</summary>
    public Result<AnimQuat> RotationAt(int node, double position)
    {
        int low = (int)Math.Floor(position);
        int high = (int)Math.Ceiling(position);

        Result<byte[]?> lowBytes = ChannelBytes(AnimChannel.Rotation, node, low);
        if (!lowBytes.TryGetValue(out byte[]? lowRaw, out Refusal? lowRefusal))
        {
            return lowRefusal;
        }

        if (lowRaw is null)
        {
            return Result.Ok(AnimQuat.Identity);
        }

        Result<AnimQuat> first = DecodeRotation(lowRaw);
        if (low == high || !first.IsSuccess)
        {
            return first;
        }

        Result<byte[]?> highBytes = ChannelBytes(AnimChannel.Rotation, node, high);
        if (!highBytes.TryGetValue(out byte[]? highRaw, out Refusal? highRefusal))
        {
            return highRefusal;
        }

        Result<AnimQuat> second = DecodeRotation(highRaw!);
        if (!second.IsSuccess)
        {
            return second;
        }

        return Result.Ok(Slerp(first.Value, second.Value, position - low));
    }

    private Result<AnimVec3> Vec3At(AnimChannel channel, int node, double position, AnimVec3 identity)
    {
        int low = (int)Math.Floor(position);
        int high = (int)Math.Ceiling(position);

        Result<byte[]?> lowBytes = ChannelBytes(channel, node, low);
        if (!lowBytes.TryGetValue(out byte[]? lowRaw, out Refusal? lowRefusal))
        {
            return lowRefusal;
        }

        if (lowRaw is null)
        {
            return Result.Ok(identity);
        }

        Result<AnimVec3> first = DecodeFixed3(lowRaw);
        if (low == high || !first.IsSuccess)
        {
            return first;
        }

        Result<byte[]?> highBytes = ChannelBytes(channel, node, high);
        if (!highBytes.TryGetValue(out byte[]? highRaw, out Refusal? highRefusal))
        {
            return highRefusal;
        }

        Result<AnimVec3> second = DecodeFixed3(highRaw!);
        if (!second.IsSuccess)
        {
            return second;
        }

        double amount = position - low;
        AnimVec3 a = first.Value;
        AnimVec3 b = second.Value;
        return Result.Ok(new AnimVec3(
            a.X + ((b.X - a.X) * amount),
            a.Y + ((b.Y - a.Y) * amount),
            a.Z + ((b.Z - a.Z) * amount)));
    }

    private static AnimQuat Slerp(AnimQuat first, AnimQuat second, double amount)
    {
        double dot = (first.X * second.X) + (first.Y * second.Y) + (first.Z * second.Z) + (first.W * second.W);
        if (dot < 0.0)
        {
            second = new AnimQuat(-second.X, -second.Y, -second.Z, -second.W);
            dot = -dot;
        }

        dot = Math.Clamp(dot, -1.0, 1.0);

        if (dot > 0.9995)
        {
            AnimQuat blended = new(
                first.X + ((second.X - first.X) * amount),
                first.Y + ((second.Y - first.Y) * amount),
                first.Z + ((second.Z - first.Z) * amount),
                first.W + ((second.W - first.W) * amount));
            double length = Math.Sqrt((blended.X * blended.X) + (blended.Y * blended.Y) + (blended.Z * blended.Z) + (blended.W * blended.W));
            return new AnimQuat(blended.X / length, blended.Y / length, blended.Z / length, blended.W / length);
        }

        double angle = Math.Acos(dot);
        double sine = Math.Sin(angle);
        double firstWeight = Math.Sin((1.0 - amount) * angle) / sine;
        double secondWeight = Math.Sin(amount * angle) / sine;
        return new AnimQuat(
            (firstWeight * first.X) + (secondWeight * second.X),
            (firstWeight * first.Y) + (secondWeight * second.Y),
            (firstWeight * first.Z) + (secondWeight * second.Z),
            (firstWeight * first.W) + (secondWeight * second.W));
    }

    private Result<AnimVec3> DecodeVec3(AnimChannel channel, int node, int sample, AnimVec3 identity)
    {
        Result<byte[]?> bytes = ChannelBytes(channel, node, sample);
        if (!bytes.TryGetValue(out byte[]? raw, out Refusal? refusal))
        {
            return refusal;
        }

        return raw is null ? Result.Ok(identity) : DecodeFixed3(raw);
    }

    /// <summary>
    /// The stride bytes for a node's channel at a sample, or null for a sentinel
    /// (which the caller reads as the channel's identity).
    /// </summary>
    private Result<byte[]?> ChannelBytes(AnimChannel channel, int node, int sample)
    {
        ushort selector = _streams[channel][node];
        if (selector is 0xFFFE or 0xFFFF)
        {
            return Result.Ok<byte[]?>(null);
        }

        Result<byte[]> resolved = selector >= 0x8000
            ? StaticRaw(channel, selector)
            : AnimatedRaw(channel, selector, sample);

        if (!resolved.TryGetValue(out byte[]? raw, out Refusal? refusal))
        {
            return refusal;
        }

        return Result.Ok<byte[]?>(raw);
    }

    private static (string Stream, string Blob, string Sub) Tags(AnimChannel channel) => channel switch
    {
        AnimChannel.Translation => ("TRAI", "DTRA", "TRAD"),
        AnimChannel.Rotation => ("ROTI", "DROT", "ROTD"),
        _ => ("SCAI", "DSCA", "SCAD"),
    };

    private int Stride(AnimChannel channel) => channel == AnimChannel.Rotation ? RotationStride : 8;

    /// <summary>Resolves a static value, whose index is the selector minus 0x8000.</summary>
    private Result<byte[]> StaticRaw(AnimChannel channel, int selector)
    {
        (string streamTag, string blobTag, _) = Tags(channel);
        int stride = Stride(channel);
        int index = selector - 0x8000;

        // The blob runs from its own tag to the next ordered tag that is present,
        // so a chunk never reads into the one after it.
        int blobOrder = Array.IndexOf(OrderedTags, blobTag);
        int nextOffset = -1;
        for (int i = blobOrder + 1; i < OrderedTags.Length; i++)
        {
            if (_offsets.TryGetValue(OrderedTags[i], out int offset) && (nextOffset < 0 || offset < nextOffset))
            {
                nextOffset = offset;
            }
        }

        if (!_offsets.TryGetValue(blobTag, out int blobOffset) || nextOffset < blobOffset + 4)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} static data layout is invalid."));
        }

        int payloadStart = blobOffset + 4;
        int payloadLength = nextOffset - payloadStart;
        int start = index * stride;
        if (start > payloadLength || stride > payloadLength - start)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} static entry {index} is out of range."));
        }

        return Result.Ok(_source.Span.Slice(payloadStart + start, stride).ToArray());
    }

    /// <summary>Resolves an animated value, flat or change-key compressed, at an integer sample.</summary>
    private Result<byte[]> AnimatedRaw(AnimChannel channel, int selector, int sample)
    {
        (string streamTag, _, string subTag) = Tags(channel);
        int stride = Stride(channel);
        ReadOnlySpan<byte> data = _source.Span;

        int animatedCount = 0;
        foreach (ushort value in _streams[channel])
        {
            if (value < 0x8000)
            {
                animatedCount++;
            }
        }

        if (selector >= animatedCount)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} animated selector {selector} is out of range."));
        }

        if (!_offsets.TryGetValue(streamTag, out int streamOffset))
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} selector stream is missing."));
        }

        int selectorEnd = streamOffset + 4 + (NodeCount * 2);

        // The section ends at the next selector stream, name or hierarchy chunk;
        // the data tag must sit inside it.
        int sectionEnd = data.Length;
        foreach ((string stream, int offset) in _offsets)
        {
            if (offset > selectorEnd && stream is "TRAI" or "ROTI" or "SCAI" or "NAME" or "PRNT" or "PART" && offset < sectionEnd)
            {
                sectionEnd = offset;
            }
        }

        int subOffset = Find(data, subTag, selectorEnd);
        if (subOffset < selectorEnd || subOffset >= sectionEnd)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} animated data is missing."));
        }

        int chakOffset = Find(data, "CHAK", subOffset + 4, sectionEnd);
        int caksOffset = Find(data, "CAKS", subOffset + 4, sectionEnd);

        if (chakOffset >= 0 && caksOffset > chakOffset)
        {
            return CompressedRaw(
                channel, selector, sample, stride, streamTag,
                subOffset, chakOffset, caksOffset, sectionEnd, animatedCount);
        }

        int payloadEnd = sectionEnd;
        if (chakOffset >= 0 && chakOffset < payloadEnd)
        {
            payloadEnd = chakOffset;
        }

        if (caksOffset >= 0 && caksOffset < payloadEnd)
        {
            payloadEnd = caksOffset;
        }

        int flatStart = subOffset + 4;
        int flatLength = payloadEnd - flatStart;
        int unit = animatedCount * stride;
        if (unit == 0 || flatLength % unit != 0)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} flat channel layout is invalid."));
        }

        int samplesPerChannel = flatLength / unit;
        if (sample < 0 || sample >= samplesPerChannel)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} sample {sample} is out of range."));
        }

        // Sample-major: every channel at frame 0, then every channel at frame 1.
        // The reference reads this channel-major — every frame of channel 0,
        // then every frame of channel 1 — and this build did too, faithfully,
        // because it was ported rather than re-derived.
        //
        // Nothing in the file says which it is, so it was settled by what the
        // two readings produce. Animation is smooth, and a wrong reading
        // scrambles it, so the orderings can be told apart without any ground
        // truth: decode the same bytes both ways and measure how far each
        // channel travels. Over 9,610 ANIM files across two corpora, every one
        // of the 30 flat payloads — rotation, translation and scale alike — is
        // smoother read sample-major, none channel-major, by a median factor of
        // 8.6x for rotation and 93x for translation.
        //
        // On the case this was reported against it is the difference between
        // 44,934 degrees of joint travel and 2,436: a character flailing rather
        // than moving. See Roadmap §10.5.
        int start = ((sample * animatedCount) + selector) * stride;
        return Result.Ok(data.Slice(flatStart + start, stride).ToArray());
    }

    private Result<byte[]> CompressedRaw(
        AnimChannel channel,
        int selector,
        int sample,
        int stride,
        string streamTag,
        int subOffset,
        int chakOffset,
        int caksOffset,
        int sectionEnd,
        int animatedCount)
    {
        ReadOnlySpan<byte> data = _source.Span;
        int payloadStart = subOffset + 4;
        int payloadLength = chakOffset - payloadStart;

        if (!TryReadU16Array(data, chakOffset + 4, caksOffset, out ushort[] changes) ||
            !TryReadU16Array(data, caksOffset + 4, sectionEnd, out ushort[] offsets))
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} change-key table has an odd byte length."));
        }

        int valueCount = Math.DivRem(payloadLength, stride, out int remainder);
        if (remainder != 0 ||
            offsets.Length != animatedCount + 1 ||
            valueCount != animatedCount + changes.Length)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} compressed channel layout is invalid."));
        }

        int changeStart = offsets[selector];
        int changeEnd = offsets[selector + 1];
        if (changeStart > changeEnd || changeEnd > changes.Length)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {streamTag} change-key range is invalid."));
        }

        int valueIndex = selector + changeStart;
        for (int i = changeStart; i < changeEnd; i++)
        {
            if (sample >= changes[i])
            {
                valueIndex = selector + changeStart + 1 + (i - changeStart);
            }
        }

        int start = valueIndex * stride;
        int available = Math.Max(0, payloadLength - start);
        return Result.Ok(data.Slice(payloadStart + start, Math.Min(stride, available)).ToArray());
    }

    private static bool TryReadU16Array(ReadOnlySpan<byte> data, int start, int end, out ushort[] values)
    {
        int length = end - start;
        if (length < 0 || (length & 1) != 0)
        {
            values = [];
            return false;
        }

        values = new ushort[length / 2];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (ushort)(data[start + (i * 2)] | (data[start + (i * 2) + 1] << 8));
        }

        return true;
    }

    private static int Find(ReadOnlySpan<byte> data, string tag, int start)
    {
        if (start < 0)
        {
            start = 0;
        }

        if (start > data.Length)
        {
            return -1;
        }

        int relative = data[start..].IndexOf(System.Text.Encoding.ASCII.GetBytes(tag));
        return relative < 0 ? -1 : start + relative;
    }

    private static int Find(ReadOnlySpan<byte> data, string tag, int start, int end)
    {
        if (start < 0)
        {
            start = 0;
        }

        if (end > data.Length)
        {
            end = data.Length;
        }

        if (start >= end)
        {
            return -1;
        }

        int relative = data[start..end].IndexOf(System.Text.Encoding.ASCII.GetBytes(tag));
        return relative < 0 ? -1 : start + relative;
    }

    private static Result<AnimVec3> DecodeFixed3(byte[] raw)
    {
        if (raw.Length != 8)
        {
            return Refusal.Malformed("ANIM packed float3 entry is truncated.");
        }

        short highX = (short)(raw[0] | (raw[1] << 8));
        short highY = (short)(raw[2] | (raw[3] << 8));
        short highZ = (short)(raw[4] | (raw[5] << 8));
        int packed = raw[6] | (raw[7] << 8);
        int exponent = packed & 0xF;
        double multiplier = Math.ScaleB(1.0, exponent - 19);

        return Result.Ok(new AnimVec3(
            ((highX << 4) | ((packed >> 12) & 0xF)) * multiplier,
            ((highY << 4) | ((packed >> 8) & 0xF)) * multiplier,
            ((highZ << 4) | ((packed >> 4) & 0xF)) * multiplier));
    }

    private static Result<AnimQuat> DecodeRotation(byte[] raw) => raw.Length switch
    {
        8 => DecodeQuantised(raw),
        6 => DecodeSmallestThree(raw),
        _ => DecodeQuantisedComponent(raw),
    };

    /// <summary>Selector 5: a quantised component plus its derived companion.</summary>
    private static Result<AnimQuat> DecodeQuantisedComponent(byte[] raw)
    {
        if (raw.Length != 3)
        {
            return Refusal.Malformed("ANIM packed rotation entry is truncated.");
        }

        int fixedValue = (((short)(raw[0] | (raw[1] << 8))) << 5) | (raw[2] & 0x1F);
        double stored = Math.Clamp(fixedValue / 524288.0, -1.0, 1.0);
        double companion = Math.Sqrt(Math.Max(0.0, 1.0 - (stored * stored)));
        int code = raw[2] >> 5;

        (double X, double Y, double Z, double W)? values = code switch
        {
            1 => (stored, 0.0, 0.0, companion),
            2 => (0.0, stored, 0.0, companion),
            3 => (stored, companion, 0.0, 0.0),
            4 => (0.0, 0.0, stored, companion),
            5 => (stored, 0.0, companion, 0.0),
            6 => (0.0, stored, companion, 0.0),
            _ => null,
        };

        if (values is not { } q)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"ANIM uses unsupported packed rotation code {code}."));
        }

        double length = Math.Sqrt((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W));
        return Result.Ok(new AnimQuat(q.X / length, q.Y / length, q.Z / length, q.W / length));
    }

    /// <summary>Selector 2: the smallest-three scheme, the omitted component recovered.</summary>
    private static Result<AnimQuat> DecodeSmallestThree(byte[] raw)
    {
        if (raw.Length != 6)
        {
            return Refusal.Malformed("ANIM packed rotation entry is truncated.");
        }

        ushort first = (ushort)(raw[0] | (raw[1] << 8));
        ushort second = (ushort)(raw[2] | (raw[3] << 8));
        ushort third = (ushort)(raw[4] | (raw[5] << 8));

        if ((third & 0x8000) != 0)
        {
            return Refusal.Malformed(
                "ANIM packed rotation entry sets the reserved high bit of its third component.");
        }

        double a = ((first & 0x7FFF) * SmallestThreeScale) - SmallestThreeBias;
        double b = ((second & 0x7FFF) * SmallestThreeScale) - SmallestThreeBias;
        double c = ((short)third * SmallestThreeScale) - SmallestThreeBias;
        double radicand = 1.0 - (a * a) - (b * b) - (c * c);
        if (radicand < 0.0)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"ANIM packed rotation entry is not a unit quaternion (1 - a^2 - b^2 - c^2 = {radicand:g6})."));
        }

        double omitted = Math.Sqrt(radicand);
        int index = ((first >> 15) & 1) | (((second >> 15) & 1) << 1);
        return Result.Ok(index switch
        {
            0 => new AnimQuat(omitted, a, b, c),
            1 => new AnimQuat(a, omitted, b, c),
            2 => new AnimQuat(a, b, omitted, c),
            _ => new AnimQuat(a, b, c, omitted),
        });
    }

    /// <summary>Selector 0: four signed 16-bit components, checked against unit length.</summary>
    private static Result<AnimQuat> DecodeQuantised(byte[] raw)
    {
        if (raw.Length != 8)
        {
            return Refusal.Malformed("ANIM packed rotation entry is truncated.");
        }

        double x = (short)(raw[0] | (raw[1] << 8)) * QuantisedScale;
        double y = (short)(raw[2] | (raw[3] << 8)) * QuantisedScale;
        double z = (short)(raw[4] | (raw[5] << 8)) * QuantisedScale;
        double w = (short)(raw[6] | (raw[7] << 8)) * QuantisedScale;

        double length = Math.Sqrt((x * x) + (y * y) + (z * z) + (w * w));
        if (Math.Abs(length - 1.0) > QuantisedNormTolerance)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM packed rotation entry is not a unit quaternion (norm {length:g6})."));
        }

        return Result.Ok(new AnimQuat(x / length, y / length, z / length, w / length));
    }
}
