using Communication.Abstractions.Models;
using Communication.Protocols.OpcUa.Models;

namespace Communication.Protocols.OpcUa;

/// <summary>Isolates OPC UA SDK session types from the public variable client.</summary>
public interface IOpcUaSession : IAsyncDisposable
{
    /// <summary>Gets the session state.</summary>
    ConnectionState State { get; }

    /// <summary>Raised after the session and secure channel have recovered.</summary>
    event EventHandler? Reconnected;

    /// <summary>Raised when explicitly insecure certificate handling is used.</summary>
    event EventHandler<OpcUaSecurityWarningEventArgs>? SecurityWarning;

    /// <summary>Discovers endpoints without opening a session.</summary>
    ValueTask<CommunicationResult<IReadOnlyList<OpcUaEndpointDescription>>> DiscoverAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Opens the configured session.</summary>
    ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the session.</summary>
    ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads node values with per-node results.</summary>
    ValueTask<IReadOnlyList<CommunicationResult<OpcUaNodeValue>>> ReadAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default);

    /// <summary>Writes node values with per-node results.</summary>
    ValueTask<IReadOnlyList<CommunicationResult>> WriteAsync(
        IReadOnlyList<OpcUaNodeWrite> writes,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a raw data-change subscription.</summary>
    ValueTask<CommunicationResult<IOpcUaSessionSubscription>> SubscribeAsync(
        IReadOnlyList<OpcUaMonitoredNode> nodes,
        TimeSpan publishingInterval,
        Func<OpcUaNodeValue, ValueTask> onValue,
        CancellationToken cancellationToken = default);
}

/// <summary>Represents one replaceable SDK subscription.</summary>
public interface IOpcUaSessionSubscription : IAsyncDisposable
{
}
