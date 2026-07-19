using Communication.Abstractions.Models;

namespace Communication.Protocols.S7.Models;

/// <summary>Identifies the S7 absolute memory areas supported by the first release.</summary>
public enum S7MemoryArea : byte
{
    /// <summary>Process inputs.</summary>
    Inputs = 0x81,
    /// <summary>Process outputs.</summary>
    Outputs = 0x82,
    /// <summary>Marker memory.</summary>
    Markers = 0x83,
    /// <summary>Non-optimized data block memory.</summary>
    DataBlock = 0x84,
}

/// <summary>Represents one parsed absolute S7 address.</summary>
/// <param name="Area">The memory area.</param>
/// <param name="DbNumber">The DB number, or zero outside DB memory.</param>
/// <param name="ByteOffset">The zero-based byte offset.</param>
/// <param name="BitOffset">An optional bit offset from 0 through 7.</param>
/// <param name="Original">The original address text.</param>
public sealed record S7Address(
    S7MemoryArea Area,
    ushort DbNumber,
    int ByteOffset,
    int? BitOffset,
    string Original)
{
    /// <summary>Gets the S7 three-byte bit address.</summary>
    public int BitAddress => checked((ByteOffset * 8) + (BitOffset ?? 0));

    /// <summary>Converts to the vendor-neutral parsed address model.</summary>
    public PlcAddress ToPlcAddress() => new(
        Area == S7MemoryArea.DataBlock ? $"DB{DbNumber}" : Area.ToString(),
        ByteOffset,
        BitOffset,
        Original);
}

/// <summary>Configures ISO-on-TCP connection and S7 PDU limits.</summary>
public sealed record S7ClientOptions
{
    /// <summary>Gets the CPU rack number.</summary>
    public byte Rack { get; init; }

    /// <summary>Gets the CPU slot number; S7-1200/1500 commonly use one.</summary>
    public byte Slot { get; init; } = 1;

    /// <summary>Gets the maximum contiguous data bytes per grouped request.</summary>
    public int MaxBatchBytes { get; init; } = 200;

    /// <summary>Gets the timeout for one ISO-on-TCP exchange.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
