using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Adapters;

/// <summary>Parses continuous weighing frames and exposes explicit tare/zero commands.</summary>
public sealed class WeighingDeviceAdapter : FramedDeviceAdapter<WeightReading>, IWeighingDevice
{
    private readonly Func<ReadOnlyMemory<byte>, CommunicationResult<WeightReading>> _parser;
    private readonly ReadOnlyMemory<byte> _tareCommand;
    private readonly ReadOnlyMemory<byte> _zeroCommand;
    private WeightReading? _latest;

    /// <summary>Initializes a framed weighing device.</summary>
    public WeighingDeviceAdapter(
        string deviceId,
        ITransportChannel channel,
        IFrameDecoder decoder,
        Func<ReadOnlyMemory<byte>, CommunicationResult<WeightReading>> parser,
        ReadOnlyMemory<byte> tareCommand,
        ReadOnlyMemory<byte> zeroCommand)
        : base(deviceId, channel, decoder)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _tareCommand = tareCommand;
        _zeroCommand = zeroCommand;
    }

    /// <inheritdoc />
    public event EventHandler<WeightReportedEventArgs>? WeightReported;

    /// <inheritdoc />
    public ValueTask<CommunicationResult<WeightReading>> ReadWeightAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommunicationResult state = EnsureConnected();
        CommunicationResult<WeightReading> result = !state.IsSuccess
            ? CommunicationResult<WeightReading>.Failure(state.Error!)
            : _latest is null
                ? CommunicationResult<WeightReading>.Failure(new CommunicationError(
                    CommunicationErrorCode.InvalidState,
                    "No complete weight frame has been received."))
                : CommunicationResult<WeightReading>.Success(_latest);
        return new ValueTask<CommunicationResult<WeightReading>>(result);
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult> TareAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(_tareCommand, cancellationToken);

    /// <inheritdoc />
    public ValueTask<CommunicationResult> ZeroAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(_zeroCommand, cancellationToken);

    /// <inheritdoc />
    protected override CommunicationResult<WeightReading> ParseFrame(ReadOnlyMemory<byte> frame) => _parser(frame);

    /// <inheritdoc />
    protected override void OnReading(WeightReading reading, ReadOnlyMemory<byte> rawFrame)
    {
        _latest = reading;
        WeightReported?.Invoke(this, new WeightReportedEventArgs(reading));
    }
}
