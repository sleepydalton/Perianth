using System;
using System.Diagnostics.CodeAnalysis;

namespace Perianth.Formats.Diagnostics;

/// <summary>
/// Constructs results. The factories live here rather than on
/// <see cref="Result{T}"/> so that a decoder writes <c>Result.Ok(model)</c> and
/// lets inference name the type.
/// </summary>
public static class Result
{
    /// <summary>A successful result carrying <paramref name="value"/>.</summary>
    public static Result<T> Ok<T>(T value) => new(isSuccess: true, value, refusal: null);

    /// <summary>
    /// A refused result. Rarely needed explicitly: returning the refusal itself
    /// converts to this.
    /// </summary>
    public static Result<T> Refused<T>(Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return new Result<T>(isSuccess: false, default, refusal);
    }
}

/// <summary>
/// Either a decoded value or a typed <see cref="Refusal"/>. Every stage that
/// can decline an input returns one of these, which keeps the refusing paths
/// visible in signatures and keeps absence distinct from fault.
/// </summary>
/// <typeparam name="T">The decoded value.</typeparam>
public readonly struct Result<T>
{
    // Success is tracked explicitly rather than inferred from a null refusal,
    // so that default(Result<T>) is neither outcome and says so when used
    // instead of impersonating a success carrying null.
    private readonly bool _isSuccess;
    private readonly T? _value;
    private readonly Refusal? _refusal;

    internal Result(bool isSuccess, T? value, Refusal? refusal)
    {
        _isSuccess = isSuccess;
        _value = value;
        _refusal = refusal;
    }

    /// <summary>Whether a value is present.</summary>
    public bool IsSuccess => _isSuccess;

    /// <summary>Whether a refusal is present.</summary>
    public bool IsRefused => _refusal is not null;

    /// <summary>Lets a decoder write <c>return Refusal.Malformed(...);</c> directly.</summary>
    public static implicit operator Result<T>(Refusal refusal) => Result.Refused<T>(refusal);

    /// <summary>
    /// The decoded value. Reading this on a refused result is a bug in the
    /// caller rather than a property of the input, so it throws.
    /// </summary>
    public T Value => _isSuccess
        ? _value!
        : throw new InvalidOperationException(IsRefused
            ? "This result is a refusal; read Refusal instead. " + _refusal!.Message
            : "This result was never given an outcome.");

    /// <summary>
    /// The refusal. Reading this on a successful result is likewise a caller bug.
    /// </summary>
    public Refusal Refusal => _refusal
        ?? throw new InvalidOperationException(_isSuccess
            ? "This result is a success; read Value instead."
            : "This result was never given an outcome.");

    /// <summary>
    /// The branch-free form: true hands back the value, false hands back the
    /// refusal, and exactly one of the two is non-null either way.
    /// </summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value, [NotNullWhen(false)] out Refusal? refusal)
    {
        if (_isSuccess)
        {
            value = _value!;
            refusal = null;
            return true;
        }

        value = default;
        refusal = _refusal ?? throw new InvalidOperationException("This result was never given an outcome.");
        return false;
    }
}
