namespace Communication.Protocols.Modbus.Models;

/// <summary>Represents a parsed zero-based Modbus variable address.</summary>
public sealed record ModbusPlcAddress(ModbusDataArea Area, ushort Offset, string Original);

/// <summary>Configures the unified Modbus PLC-variable adapter.</summary>
public sealed record ModbusPlcClientOptions
{
    /// <summary>Gets the TCP unit identifier or RTU station address.</summary>
    public byte UnitId { get; init; } = 1;

    /// <summary>Gets the maximum registers in one grouped read.</summary>
    public int MaxReadRegisters { get; init; } = 125;

    /// <summary>Gets the maximum bits in one grouped read.</summary>
    public int MaxReadBits { get; init; } = 2000;
}
