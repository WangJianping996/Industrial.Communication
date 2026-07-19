using Communication.Abstractions.Models;

namespace Communication.Adapters;

/// <summary>Adapts lifecycle and health delegates for private ASCII, binary or vendor SDK devices.</summary>
public sealed class DelegateDeviceAdapter : DeviceAdapterBase
{
    private readonly Func<CancellationToken, ValueTask<CommunicationResult>> _start;
    private readonly Func<CancellationToken, ValueTask<CommunicationResult>> _stop;
    private readonly Func<CancellationToken, ValueTask<DeviceHealth>>? _health;

    /// <summary>Initializes a delegate-backed adapter.</summary>
    public DelegateDeviceAdapter(
        string deviceId,
        Func<CancellationToken, ValueTask<CommunicationResult>> start,
        Func<CancellationToken, ValueTask<CommunicationResult>> stop,
        Func<CancellationToken, ValueTask<DeviceHealth>>? health = null)
        : base(deviceId)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        _health = health;
    }

    /// <inheritdoc />
    public override async ValueTask<DeviceHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (_health is null)
        {
            return await base.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        }

        Health = await _health(cancellationToken).ConfigureAwait(false);
        return Health;
    }

    /// <inheritdoc />
    protected override ValueTask<CommunicationResult> OnStartAsync(CancellationToken cancellationToken) =>
        _start(cancellationToken);

    /// <inheritdoc />
    protected override ValueTask<CommunicationResult> OnStopAsync(CancellationToken cancellationToken) =>
        _stop(cancellationToken);
}
