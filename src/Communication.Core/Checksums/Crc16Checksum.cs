using Communication.Abstractions.Interfaces;

namespace Communication.Core.Checksums;

/// <summary>Computes a configurable reflected CRC-16 checksum.</summary>
public sealed class Crc16Checksum : IChecksum
{
    /// <summary>Initializes CRC-16/Modbus defaults.</summary>
    public Crc16Checksum(
        ushort polynomial = 0xA001,
        ushort initialValue = 0xFFFF,
        ushort finalXor = 0x0000,
        bool littleEndianOutput = true)
    {
        Polynomial = polynomial;
        InitialValue = initialValue;
        FinalXor = finalXor;
        LittleEndianOutput = littleEndianOutput;
    }

    /// <inheritdoc />
    public int Size => 2;

    /// <summary>Gets the reflected polynomial.</summary>
    public ushort Polynomial { get; }

    /// <summary>Gets the initial register value.</summary>
    public ushort InitialValue { get; }

    /// <summary>Gets the value XORed with the final register.</summary>
    public ushort FinalXor { get; }

    /// <summary>Gets whether checksum bytes are emitted least-significant byte first.</summary>
    public bool LittleEndianOutput { get; }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Compute(ReadOnlySpan<byte> payload)
    {
        ushort crc = InitialValue;
        foreach (byte current in payload)
        {
            crc ^= current;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1);
            }
        }

        crc ^= FinalXor;
        byte high = (byte)(crc >> 8);
        byte low = (byte)crc;
        return LittleEndianOutput ? new byte[] { low, high } : new byte[] { high, low };
    }

    /// <inheritdoc />
    public bool Validate(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> expectedChecksum)
    {
        if (expectedChecksum.Length != Size)
        {
            return false;
        }

        ReadOnlySpan<byte> actual = Compute(payload).Span;
        int difference = 0;
        for (int index = 0; index < Size; index++)
        {
            difference |= actual[index] ^ expectedChecksum[index];
        }

        return difference == 0;
    }
}
