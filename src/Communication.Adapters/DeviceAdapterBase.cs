using Communication.Abstractions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Adapters;

/// <summary>Provides serialized lifecycle, connection state and health handling for device adapters.</summary>
public abstract class DeviceAdapterBase : IDeviceAdapter
{
    private readonly ConnectionStateMachine _state = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _disposed;

    /// <summary>Initializes a device adapter.</summary>
    protected DeviceAdapterBase(string deviceId)
    {
        DeviceId = string.IsNullOrWhiteSpace(deviceId)
            ? throw new ArgumentException("A device identifier is required.", nameof(deviceId))
            : deviceId;
        Health = new DeviceHealth(DeviceHealthStatus.Unknown, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public string DeviceId { get; }

    /// <inheritdoc />
    public ConnectionState State => _state.State;

    /// <inheritdoc />
    public DeviceHealth Health { get; protected set; }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                return CommunicationResult.Success();
            }

            _state.TransitionTo(State == ConnectionState.Faulted
                ? ConnectionState.Reconnecting
                : ConnectionState.Connecting);
            CommunicationResult result = await OnStartAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _state.TransitionTo(ConnectionState.Connected);
                Health = new DeviceHealth(DeviceHealthStatus.Healthy, DateTimeOffset.UtcNow);
            }
            else
            {
                _state.TransitionTo(ConnectionState.Faulted, result.Error);
                Health = new DeviceHealth(DeviceHealthStatus.Unhealthy, DateTimeOffset.UtcNow,
                    result.Error!.Message, result.Error);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _state.TryTransition(ConnectionState.Disconnected);
            throw;
        }
        catch (Exception exception)
        {
            CommunicationError error = CommunicationError.FromException(exception);
            _state.TryTransition(ConnectionState.Faulted, error);
            Health = new DeviceHealth(DeviceHealthStatus.Unhealthy, DateTimeOffset.UtcNow,
                error.Message, error);
            return CommunicationResult.Failure(error);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Disconnected)
            {
                return CommunicationResult.Success();
            }

            _state.TryTransition(ConnectionState.Disconnecting);
            CommunicationResult result = await OnStopAsync(cancellationToken).ConfigureAwait(false);
            _state.TryTransition(result.IsSuccess ? ConnectionState.Disconnected : ConnectionState.Faulted,
                result.Error);
            Health = new DeviceHealth(
                result.IsSuccess ? DeviceHealthStatus.Unknown : DeviceHealthStatus.Unhealthy,
                DateTimeOffset.UtcNow,
                result.Error?.Message,
                result.Error);
            return result;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public virtual ValueTask<DeviceHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Health = new DeviceHealth(
            State == ConnectionState.Connected ? DeviceHealthStatus.Healthy : DeviceHealthStatus.Unhealthy,
            DateTimeOffset.UtcNow,
            State.ToString());
        return new ValueTask<DeviceHealth>(Health);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        await OnDisposeAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    /// <summary>Starts device-specific resources.</summary>
    protected abstract ValueTask<CommunicationResult> OnStartAsync(CancellationToken cancellationToken);

    /// <summary>Stops device-specific resources.</summary>
    protected abstract ValueTask<CommunicationResult> OnStopAsync(CancellationToken cancellationToken);

    /// <summary>Disposes additional resources after the adapter has stopped.</summary>
    protected virtual ValueTask OnDisposeAsync() => default;

    /// <summary>Returns a consistent invalid-state error when the device is not connected.</summary>
    protected CommunicationResult EnsureConnected() => State == ConnectionState.Connected
        ? CommunicationResult.Success()
        : CommunicationResult.Failure(new CommunicationError(
            CommunicationErrorCode.InvalidState,
            $"Device '{DeviceId}' is not connected."));

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }
}
