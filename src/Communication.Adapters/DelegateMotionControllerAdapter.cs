using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Adapters;

/// <summary>Adapts vendor motion delegates while keeping every dangerous action explicit.</summary>
public sealed class DelegateMotionControllerAdapter : DeviceAdapterBase, IMotionController
{
    private readonly Func<int, CancellationToken, ValueTask<CommunicationResult<AxisState>>> _read;
    private readonly Func<int, bool, CancellationToken, ValueTask<CommunicationResult>> _enable;
    private readonly Func<int, MotionProfile?, CancellationToken, ValueTask<CommunicationResult>> _home;
    private readonly Func<int, double, MotionProfile, bool, CancellationToken, ValueTask<CommunicationResult>> _move;
    private readonly Func<int, MotionStopMode, CancellationToken, ValueTask<CommunicationResult>> _stopAxis;

    /// <summary>Initializes a delegate-backed motion controller.</summary>
    public DelegateMotionControllerAdapter(
        string deviceId,
        Func<int, CancellationToken, ValueTask<CommunicationResult<AxisState>>> read,
        Func<int, bool, CancellationToken, ValueTask<CommunicationResult>> enable,
        Func<int, MotionProfile?, CancellationToken, ValueTask<CommunicationResult>> home,
        Func<int, double, MotionProfile, bool, CancellationToken, ValueTask<CommunicationResult>> move,
        Func<int, MotionStopMode, CancellationToken, ValueTask<CommunicationResult>> stopAxis)
        : base(deviceId)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _enable = enable ?? throw new ArgumentNullException(nameof(enable));
        _home = home ?? throw new ArgumentNullException(nameof(home));
        _move = move ?? throw new ArgumentNullException(nameof(move));
        _stopAxis = stopAxis ?? throw new ArgumentNullException(nameof(stopAxis));
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult<AxisState>> GetAxisStateAsync(
        int axis,
        CancellationToken cancellationToken = default) =>
        ExecuteReadAsync(axis, cancellationToken);

    /// <inheritdoc />
    public ValueTask<CommunicationResult> SetEnabledAsync(
        int axis,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _enable(axis, enabled, cancellationToken));

    /// <inheritdoc />
    public ValueTask<CommunicationResult> HomeAsync(
        int axis,
        MotionProfile? profile = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _home(axis, profile, cancellationToken));

    /// <inheritdoc />
    public ValueTask<CommunicationResult> MoveAbsoluteAsync(
        int axis,
        double position,
        MotionProfile profile,
        CancellationToken cancellationToken = default) =>
        ValidateAndMoveAsync(axis, position, profile, relative: false, cancellationToken);

    /// <inheritdoc />
    public ValueTask<CommunicationResult> MoveRelativeAsync(
        int axis,
        double distance,
        MotionProfile profile,
        CancellationToken cancellationToken = default) =>
        ValidateAndMoveAsync(axis, distance, profile, relative: true, cancellationToken);

    /// <inheritdoc />
    public ValueTask<CommunicationResult> StopAxisAsync(
        int axis,
        MotionStopMode mode = MotionStopMode.Decelerated,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _stopAxis(axis, mode, cancellationToken));

    /// <inheritdoc />
    protected override ValueTask<CommunicationResult> OnStartAsync(CancellationToken cancellationToken) =>
        new(CommunicationResult.Success());

    /// <inheritdoc />
    protected override ValueTask<CommunicationResult> OnStopAsync(CancellationToken cancellationToken) =>
        new(CommunicationResult.Success());

    private async ValueTask<CommunicationResult<AxisState>> ExecuteReadAsync(
        int axis,
        CancellationToken cancellationToken)
    {
        CommunicationResult validation = ValidateAxis(axis);
        return validation.IsSuccess
            ? await _read(axis, cancellationToken).ConfigureAwait(false)
            : CommunicationResult<AxisState>.Failure(validation.Error!);
    }

    private ValueTask<CommunicationResult> ValidateAndMoveAsync(
        int axis,
        double target,
        MotionProfile profile,
        bool relative,
        CancellationToken cancellationToken)
    {
        CommunicationResult validation = ValidateAxis(axis);
        if (!validation.IsSuccess || profile is null ||
            profile.Velocity <= 0 || profile.Acceleration <= 0 || profile.Deceleration <= 0 ||
            double.IsNaN(target) || double.IsInfinity(target))
        {
            return new ValueTask<CommunicationResult>(validation.IsSuccess
                ? CommunicationResult.Failure(new CommunicationError(
                    CommunicationErrorCode.InvalidValue,
                    "A finite target and positive motion profile are required."))
                : validation);
        }

        return ExecuteAsync(() => _move(axis, target, profile, relative, cancellationToken));
    }

    private ValueTask<CommunicationResult> ExecuteAsync(Func<ValueTask<CommunicationResult>> operation)
    {
        CommunicationResult state = EnsureConnected();
        return state.IsSuccess ? operation() : new ValueTask<CommunicationResult>(state);
    }

    private CommunicationResult ValidateAxis(int axis)
    {
        CommunicationResult state = EnsureConnected();
        return !state.IsSuccess || axis >= 0
            ? state
            : CommunicationResult.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidAddress,
                "An axis index cannot be negative."));
    }
}
