using Communication.Abstractions;
using Communication.Abstractions.Models;
using Communication.Protocols.S7.Models;

namespace Communication.Protocols.S7.Simulator;

/// <summary>Provides an in-memory S7 byte-access endpoint for CI and mapping tests.</summary>
public sealed class S7MemoryDataAccess : IS7DataAccess
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<(S7MemoryArea Area, ushort Db), byte[]> _areas = new();
    private readonly ConnectionStateMachine _state = new();
    private int _disposed;

    /// <inheritdoc />
    public ConnectionState State => _state.State;

    /// <inheritdoc />
    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (State == ConnectionState.Connected)
        {
            return new ValueTask<CommunicationResult>(CommunicationResult.Success());
        }

        _state.TransitionTo(State == ConnectionState.Faulted ? ConnectionState.Reconnecting : ConnectionState.Connecting);
        _state.TransitionTo(ConnectionState.Connected);
        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != ConnectionState.Disconnected)
        {
            _state.TransitionTo(ConnectionState.Disconnecting);
            _state.TransitionTo(ConnectionState.Disconnected);
        }

        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <summary>Configures bytes without requiring a connection.</summary>
    public CommunicationResult SetBytes(S7Address address, ReadOnlySpan<byte> bytes)
    {
        CommunicationError? error = ValidateRange(address, bytes.Length);
        if (error is not null)
        {
            return CommunicationResult.Failure(error);
        }

        lock (_syncRoot)
        {
            byte[] area = GetArea(address);
            bytes.CopyTo(area.AsSpan(address.ByteOffset));
        }

        return CommunicationResult.Success();
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult<ReadOnlyMemory<byte>>> ReadBytesAsync(
        S7Address address,
        int byteCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != ConnectionState.Connected)
        {
            return new ValueTask<CommunicationResult<ReadOnlyMemory<byte>>>(StateFailure<ReadOnlyMemory<byte>>());
        }

        CommunicationError? error = ValidateRange(address, byteCount);
        if (error is not null)
        {
            return new ValueTask<CommunicationResult<ReadOnlyMemory<byte>>>(
                CommunicationResult<ReadOnlyMemory<byte>>.Failure(error));
        }

        byte[] result = new byte[byteCount];
        lock (_syncRoot)
        {
            Array.Copy(GetArea(address), address.ByteOffset, result, 0, byteCount);
        }

        return new ValueTask<CommunicationResult<ReadOnlyMemory<byte>>>(
            CommunicationResult<ReadOnlyMemory<byte>>.Success(result));
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult> WriteBytesAsync(
        S7Address address,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != ConnectionState.Connected)
        {
            return new ValueTask<CommunicationResult>(CommunicationResult.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidState,
                "The S7 simulator is not connected.")));
        }

        return new ValueTask<CommunicationResult>(SetBytes(address, bytes.Span));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    private byte[] GetArea(S7Address address)
    {
        var key = (address.Area, address.Area == S7MemoryArea.DataBlock ? address.DbNumber : (ushort)0);
        if (!_areas.TryGetValue(key, out byte[]? bytes))
        {
            bytes = new byte[65_536];
            _areas.Add(key, bytes);
        }

        return bytes;
    }

    private static CommunicationError? ValidateRange(S7Address address, int byteCount) =>
        address.ByteOffset < 0 || byteCount <= 0 || (long)address.ByteOffset + byteCount > 65_536
            ? new CommunicationError(CommunicationErrorCode.InvalidAddress, "The S7 byte range is invalid.")
            : null;

    private static CommunicationResult<T> StateFailure<T>() => CommunicationResult<T>.Failure(new CommunicationError(
        CommunicationErrorCode.InvalidState,
        "The S7 simulator is not connected."));

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(S7MemoryDataAccess));
        }
    }
}
