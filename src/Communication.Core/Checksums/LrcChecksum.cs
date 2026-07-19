using Communication.Abstractions.Interfaces;

namespace Communication.Core.Checksums;

/// <summary>Computes the Modbus ASCII longitudinal redundancy check.</summary>
public sealed class LrcChecksum : IChecksum
{
    /// <inheritdoc />
    public int Size => 1;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Compute(ReadOnlySpan<byte> payload)
    {
        byte sum = 0;
        foreach (byte current in payload)
        {
            sum = unchecked((byte)(sum + current));
        }

        return new byte[] { unchecked((byte)-sum) };
    }

    /// <inheritdoc />
    public bool Validate(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> expectedChecksum) =>
        expectedChecksum.Length == Size && Compute(payload).Span[0] == expectedChecksum[0];
}
