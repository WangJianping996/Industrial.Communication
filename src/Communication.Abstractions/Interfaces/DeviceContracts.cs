using Communication.Abstractions.Models;

namespace Communication.Abstractions.Interfaces;

/// <summary>Defines the lifecycle and health contract for an industrial device adapter.</summary>
public interface IDeviceAdapter : IAsyncDisposable
{
    /// <summary>Gets the adapter identifier.</summary>
    string DeviceId { get; }

    /// <summary>Gets the current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Gets the most recently evaluated health.</summary>
    DeviceHealth Health { get; }

    /// <summary>Starts the adapter and its underlying communication resources.</summary>
    ValueTask<CommunicationResult> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the adapter safely.</summary>
    ValueTask<CommunicationResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Evaluates device health without changing device state.</summary>
    ValueTask<DeviceHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>Defines a reusable communication simulator with deterministic fault injection.</summary>
public interface ICommunicationSimulator : IAsyncDisposable
{
    /// <summary>Gets whether the simulator is running.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the simulator.</summary>
    ValueTask<CommunicationResult> StartAsync(
        CommunicationSimulatorOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stops the simulator.</summary>
    ValueTask<CommunicationResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Queues a deterministic fault for future requests.</summary>
    ValueTask<CommunicationResult> InjectFaultAsync(
        SimulatorFault fault,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides digital input/output access and edge notifications.</summary>
public interface IDigitalIoDevice : IDeviceAdapter
{
    /// <summary>Raised when an observed input changes state.</summary>
    event EventHandler<DigitalEdgeChangedEventArgs>? InputChanged;

    /// <summary>Reads all configured digital inputs and outputs.</summary>
    ValueTask<CommunicationResult<DigitalIoSnapshot>> ReadStatusAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Sets one digital output explicitly.</summary>
    ValueTask<CommunicationResult> SetOutputAsync(
        int index,
        bool value,
        CancellationToken cancellationToken = default);

    /// <summary>Sets multiple outputs in one explicit operation.</summary>
    ValueTask<IReadOnlyList<CommunicationResult>> SetOutputsAsync(
        IReadOnlyDictionary<int, bool> values,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides explicit motion-control operations without automatic command replay.</summary>
public interface IMotionController : IDeviceAdapter
{
    /// <summary>Reads one axis state.</summary>
    ValueTask<CommunicationResult<AxisState>> GetAxisStateAsync(
        int axis,
        CancellationToken cancellationToken = default);

    /// <summary>Explicitly enables or disables one axis.</summary>
    ValueTask<CommunicationResult> SetEnabledAsync(
        int axis,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Explicitly starts homing.</summary>
    ValueTask<CommunicationResult> HomeAsync(
        int axis,
        MotionProfile? profile = null,
        CancellationToken cancellationToken = default);

    /// <summary>Explicitly starts an absolute movement.</summary>
    ValueTask<CommunicationResult> MoveAbsoluteAsync(
        int axis,
        double position,
        MotionProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>Explicitly starts a relative movement.</summary>
    ValueTask<CommunicationResult> MoveRelativeAsync(
        int axis,
        double distance,
        MotionProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>Explicitly stops one axis.</summary>
    ValueTask<CommunicationResult> StopAxisAsync(
        int axis,
        MotionStopMode mode = MotionStopMode.Decelerated,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides framed barcode results and an explicit trigger command.</summary>
public interface IBarcodeScanner : IDeviceAdapter
{
    /// <summary>Raised for accepted, non-duplicate scans.</summary>
    event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    /// <summary>Sends the configured scanner trigger command.</summary>
    ValueTask<CommunicationResult> TriggerAsync(CancellationToken cancellationToken = default);
}

/// <summary>Provides continuous weight readings and explicit tare/zero operations.</summary>
public interface IWeighingDevice : IDeviceAdapter
{
    /// <summary>Raised when a complete weight frame is parsed.</summary>
    event EventHandler<WeightReportedEventArgs>? WeightReported;

    /// <summary>Gets the latest parsed reading.</summary>
    ValueTask<CommunicationResult<WeightReading>> ReadWeightAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Sends an explicit tare command.</summary>
    ValueTask<CommunicationResult> TareAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends an explicit zero command.</summary>
    ValueTask<CommunicationResult> ZeroAsync(CancellationToken cancellationToken = default);
}
