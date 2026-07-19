namespace Communication.Protocols.Mc.Models;

/// <summary>Identifies the supported MELSEC device codes.</summary>
public enum McDeviceCode : byte
{
    /// <summary>Input relay, hexadecimal device number.</summary>
    X = 0x9C,
    /// <summary>Output relay, hexadecimal device number.</summary>
    Y = 0x9D,
    /// <summary>Internal relay, decimal device number.</summary>
    M = 0x90,
    /// <summary>Data register, decimal device number.</summary>
    D = 0xA8,
    /// <summary>Link register, hexadecimal device number.</summary>
    W = 0xB4,
}

/// <summary>Represents one parsed MC device address.</summary>
/// <param name="DeviceCode">The binary device code.</param>
/// <param name="DeviceNumber">The zero-based device number.</param>
/// <param name="Original">The original address text.</param>
public sealed record McAddress(McDeviceCode DeviceCode, int DeviceNumber, string Original)
{
    /// <summary>Gets whether this device is addressed in bit units.</summary>
    public bool IsBitDevice => DeviceCode is McDeviceCode.X or McDeviceCode.Y or McDeviceCode.M;
}

/// <summary>Represents one MC 3E batch request.</summary>
public sealed record McRequest(
    McAddress Address,
    ushort Points,
    bool IsWrite,
    ReadOnlyMemory<byte> Data,
    ushort MonitoringTimer = 0x0010);

/// <summary>Represents one MC 3E response.</summary>
public sealed record McResponse(ushort EndCode, ReadOnlyMemory<byte> Data)
{
    /// <summary>Gets whether the PLC reported successful completion.</summary>
    public bool IsSuccess => EndCode == 0;
}

/// <summary>Configures MC batch grouping and frame routing fields.</summary>
public sealed record McClientOptions
{
    /// <summary>Gets the destination network number.</summary>
    public byte NetworkNumber { get; init; }

    /// <summary>Gets the destination station number.</summary>
    public byte StationNumber { get; init; } = 0xFF;

    /// <summary>Gets the destination module I/O number.</summary>
    public ushort ModuleIoNumber { get; init; } = 0x03FF;

    /// <summary>Gets the destination multidrop station.</summary>
    public byte MultidropStationNumber { get; init; }

    /// <summary>Gets the maximum word points in one grouped read.</summary>
    public int MaxWordPoints { get; init; } = 960;

    /// <summary>Gets the maximum bit points in one grouped read.</summary>
    public int MaxBitPoints { get; init; } = 7168;
}
