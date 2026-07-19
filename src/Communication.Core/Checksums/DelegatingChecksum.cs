using Communication.Abstractions.Interfaces;

namespace Communication.Core.Checksums;

/// <summary>Adapts caller-supplied checksum functions to <see cref="IChecksum"/>.</summary>
public sealed class DelegatingChecksum : IChecksum
{
    private readonly Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> _compute;

    /// <summary>Initializes a custom checksum.</summary>
    public DelegatingChecksum(int size, Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> compute)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        Size = size;
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));
    }

    /// <inheritdoc />
    public int Size { get; }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Compute(ReadOnlySpan<byte> payload)
    {
        ReadOnlyMemory<byte> checksum = _compute(payload.ToArray());
        if (checksum.Length != Size)
        {
            throw new InvalidOperationException($"Custom checksum returned {checksum.Length} bytes; expected {Size}.");
        }

        return checksum;
    }

    /// <inheritdoc />
    public bool Validate(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> expectedChecksum) =>
        expectedChecksum.SequenceEqual(Compute(payload).Span);
}
