using Communication.Abstractions.Models;

namespace Communication.Abstractions.Interfaces;

/// <summary>Defines an asynchronous, protocol-neutral byte transport.</summary>
public interface ITransportChannel : IAsyncDisposable
{
    /// <summary>Gets the logical channel identifier used for diagnostics.</summary>
    string ChannelId { get; }

    /// <summary>Gets the current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Raised after the connection state changes.</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Connects the channel.</summary>
    ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnects the channel. Repeated calls must be safe.</summary>
    ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends one byte sequence and reports the number of accepted bytes.</summary>
    ValueTask<CommunicationResult<int>> SendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams received byte chunks. A consumer must copy a chunk before advancing when it needs
    /// to retain the bytes beyond the current iteration.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
}
