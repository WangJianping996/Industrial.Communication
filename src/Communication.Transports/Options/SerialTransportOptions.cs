using System.IO.Ports;

namespace Communication.Transports.Options;

/// <summary>Configures a serial port transport.</summary>
public sealed record SerialTransportOptions
{
    /// <summary>Gets the operating-system port name.</summary>
    public required string PortName { get; init; }

    /// <summary>Gets the baud rate.</summary>
    public int BaudRate { get; init; } = 9600;

    /// <summary>Gets the number of data bits.</summary>
    public int DataBits { get; init; } = 8;

    /// <summary>Gets the parity mode.</summary>
    public Parity Parity { get; init; } = Parity.None;

    /// <summary>Gets the stop-bit mode.</summary>
    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>Gets the handshake mode.</summary>
    public Handshake Handshake { get; init; } = Handshake.None;

    /// <summary>Gets the open timeout used by the wrapper.</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the serial driver read timeout.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the serial driver write timeout.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the asynchronous read buffer size.</summary>
    public int ReceiveBufferSize { get; init; } = 4096;
}
