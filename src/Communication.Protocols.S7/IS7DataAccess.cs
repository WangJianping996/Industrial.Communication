using Communication.Abstractions.Models;
using Communication.Protocols.S7.Models;

namespace Communication.Protocols.S7;

/// <summary>Abstracts replaceable S7 byte access without exposing a third-party SDK.</summary>
public interface IS7DataAccess : IAsyncDisposable
{
    /// <summary>Gets the connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Connects the S7 endpoint.</summary>
    ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnects the S7 endpoint.</summary>
    ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads contiguous bytes from one area.</summary>
    ValueTask<CommunicationResult<ReadOnlyMemory<byte>>> ReadBytesAsync(
        S7Address address,
        int byteCount,
        CancellationToken cancellationToken = default);

    /// <summary>Writes contiguous bytes to one area.</summary>
    ValueTask<CommunicationResult> WriteBytesAsync(
        S7Address address,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);
}
