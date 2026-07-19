namespace Communication.Transports.Options;

/// <summary>Configures a TCP client transport.</summary>
public sealed record TcpTransportOptions
{
    /// <summary>Gets the remote DNS name or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>Gets the remote TCP port.</summary>
    public required int Port { get; init; }

    /// <summary>Gets the connection timeout.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the socket receive buffer size.</summary>
    public int ReceiveBufferSize { get; init; } = 8192;

    /// <summary>Gets the socket send buffer size.</summary>
    public int SendBufferSize { get; init; } = 8192;

    /// <summary>Gets whether Nagle's algorithm is disabled.</summary>
    public bool NoDelay { get; init; } = true;
}

/// <summary>Configures a multi-session TCP communication server.</summary>
public sealed record TcpServerOptions
{
    /// <summary>Gets the listen IP address. Use <c>0.0.0.0</c> or <c>::</c> for any interface.</summary>
    public string ListenAddress { get; init; } = "0.0.0.0";

    /// <summary>Gets the listen port. Zero requests an ephemeral port.</summary>
    public int Port { get; init; }

    /// <summary>Gets the maximum number of simultaneous client sessions.</summary>
    public int MaxConnections { get; init; } = 100;

    /// <summary>Gets the per-session receive buffer size.</summary>
    public int ReceiveBufferSize { get; init; } = 8192;

    /// <summary>Gets the bounded request queue capacity.</summary>
    public int RequestQueueCapacity { get; init; } = 1024;

    /// <summary>Gets whether Nagle's algorithm is disabled for accepted clients.</summary>
    public bool NoDelay { get; init; } = true;
}
