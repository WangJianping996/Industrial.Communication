namespace Communication.Transports.Options;

/// <summary>Configures a UDP transport.</summary>
public sealed record UdpTransportOptions
{
    /// <summary>Gets the local bind address.</summary>
    public string LocalAddress { get; init; } = "0.0.0.0";

    /// <summary>Gets the local bind port. Zero requests an ephemeral port.</summary>
    public int LocalPort { get; init; }

    /// <summary>Gets an optional default remote host.</summary>
    public string? RemoteHost { get; init; }

    /// <summary>Gets an optional default remote port.</summary>
    public int? RemotePort { get; init; }

    /// <summary>Gets the maximum accepted datagram size.</summary>
    public int ReceiveBufferSize { get; init; } = 65_507;

    /// <summary>Gets whether socket broadcast is enabled.</summary>
    public bool EnableBroadcast { get; init; }
}
