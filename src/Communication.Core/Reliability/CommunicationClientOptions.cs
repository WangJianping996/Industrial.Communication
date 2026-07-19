namespace Communication.Core.Reliability;

/// <summary>Configures a reliable request/response communication client.</summary>
public sealed record CommunicationClientOptions
{
    /// <summary>Gets the default response timeout.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the maximum undecoded receive buffer size.</summary>
    public int MaxBufferedBytes { get; init; } = 1024 * 1024;

    /// <summary>Gets the protocol name attached to monitored messages.</summary>
    public string? ProtocolName { get; init; }
}
