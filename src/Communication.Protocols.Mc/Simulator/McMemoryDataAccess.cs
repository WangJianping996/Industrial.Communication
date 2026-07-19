using Communication.Abstractions;
using Communication.Abstractions.Models;
using Communication.Protocols.Mc.Models;

namespace Communication.Protocols.Mc.Simulator;

/// <summary>Provides deterministic in-memory MC X/Y/M/D/W storage for tests and local simulation.</summary>
public sealed class McMemoryDataAccess : IMcDataAccess
{
    private readonly ConnectionStateMachine _state = new();
    private readonly Dictionary<(McDeviceCode Code, int Number), ushort> _values = [];
    private readonly object _sync = new();
    private int _disposed;

    /// <inheritdoc />
    public ConnectionState State => _state.State;

    /// <inheritdoc />
    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (State == ConnectionState.Disconnected)
        {
            _state.TransitionTo(ConnectionState.Connecting);
            _state.TransitionTo(ConnectionState.Connected);
        }

        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != ConnectionState.Disconnected)
        {
            _state.TryTransition(ConnectionState.Disconnecting);
            _state.TryTransition(ConnectionState.Disconnected);
        }

        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult<ReadOnlyMemory<byte>>> ReadAsync(
        McAddress address,
        ushort points,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        CommunicationError? validation = Validate(address, points);
        if (validation is not null)
        {
            return new ValueTask<CommunicationResult<ReadOnlyMemory<byte>>>(
                CommunicationResult<ReadOnlyMemory<byte>>.Failure(validation));
        }

        byte[] data = new byte[address.IsBitDevice ? points : points * 2];
        lock (_sync)
        {
            for (int index = 0; index < points; index++)
            {
                _values.TryGetValue((address.DeviceCode, address.DeviceNumber + index), out ushort value);
                if (address.IsBitDevice)
                {
                    data[index] = (byte)(value == 0 ? 0 : 1);
                }
                else
                {
                    data[index * 2] = (byte)(value >> 8);
                    data[(index * 2) + 1] = (byte)value;
                }
            }
        }

        return new ValueTask<CommunicationResult<ReadOnlyMemory<byte>>>(
            CommunicationResult<ReadOnlyMemory<byte>>.Success(data));
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult> WriteAsync(
        McAddress address,
        ushort points,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        CommunicationError? validation = Validate(address, points);
        if (validation is not null)
        {
            return new ValueTask<CommunicationResult>(CommunicationResult.Failure(validation));
        }

        int expected = address.IsBitDevice ? points : points * 2;
        if (data.Length != expected)
        {
            return new ValueTask<CommunicationResult>(CommunicationResult.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                $"The MC simulator write contains {data.Length} bytes; {expected} were expected.")));
        }

        lock (_sync)
        {
            for (int index = 0; index < points; index++)
            {
                ushort value = address.IsBitDevice
                    ? (ushort)(data.Span[index] == 0 ? 0 : 1)
                    : (ushort)((data.Span[index * 2] << 8) | data.Span[(index * 2) + 1]);
                _values[(address.DeviceCode, address.DeviceNumber + index)] = value;
            }
        }

        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    private CommunicationError? Validate(McAddress address, ushort points)
    {
        if (State != ConnectionState.Connected)
        {
            return new CommunicationError(CommunicationErrorCode.InvalidState, "The MC simulator is not connected.");
        }

        return points == 0 || address.DeviceNumber < 0 ||
               (long)address.DeviceNumber + points > 0x1_000_000
            ? new CommunicationError(CommunicationErrorCode.InvalidAddress, "The MC device range is invalid.")
            : null;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(McMemoryDataAccess));
        }
    }
}
