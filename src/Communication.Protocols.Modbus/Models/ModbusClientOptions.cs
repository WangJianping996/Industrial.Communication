namespace Communication.Protocols.Modbus.Models;

/// <summary>Configures a Modbus TCP or RTU client.</summary>
public sealed record ModbusClientOptions
{
    /// <summary>Gets the default response timeout.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the maximum concurrent Modbus TCP transactions.</summary>
    public int MaxTcpInFlight { get; init; } = 64;

    /// <summary>Gets the maximum undecoded receive bytes.</summary>
    public int MaxBufferedBytes { get; init; } = 260 * 4;

    /// <summary>Gets the minimum silent interval enforced between RTU exchanges.</summary>
    public TimeSpan RtuInterFrameDelay { get; init; } = TimeSpan.FromMilliseconds(4);
}
