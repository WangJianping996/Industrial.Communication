using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Mc.Models;

namespace Communication.Protocols.Mc;

/// <summary>Provides replaceable MC device-point access for TCP and simulator implementations.</summary>
public interface IMcDataAccess : IAsyncDisposable
{
    /// <summary>Gets the current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Connects the underlying channel.</summary>
    ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnects the underlying channel.</summary>
    ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads normalized points. Bits use one byte per point; words use big-endian byte pairs.</summary>
    ValueTask<CommunicationResult<ReadOnlyMemory<byte>>> ReadAsync(
        McAddress address,
        ushort points,
        CancellationToken cancellationToken = default);

    /// <summary>Writes normalized points. Bits use one byte per point; words use big-endian byte pairs.</summary>
    ValueTask<CommunicationResult> WriteAsync(
        McAddress address,
        ushort points,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);
}
