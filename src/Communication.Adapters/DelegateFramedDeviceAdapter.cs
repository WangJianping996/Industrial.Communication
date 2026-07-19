using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Adapters;

/// <summary>Adapts private ASCII or binary devices with caller-supplied frame parsing delegates.</summary>
/// <typeparam name="TReading">The private device reading type.</typeparam>
public sealed class DelegateFramedDeviceAdapter<TReading> : FramedDeviceAdapter<TReading>
{
    private readonly Func<ReadOnlyMemory<byte>, CommunicationResult<TReading>> _parser;
    private readonly Action<TReading, ReadOnlyMemory<byte>> _onReading;

    /// <summary>Initializes a delegate-backed framed ASCII or binary adapter.</summary>
    public DelegateFramedDeviceAdapter(
        string deviceId,
        ITransportChannel channel,
        IFrameDecoder decoder,
        Func<ReadOnlyMemory<byte>, CommunicationResult<TReading>> parser,
        Action<TReading, ReadOnlyMemory<byte>> onReading,
        int maxBufferedBytes = 1024 * 1024)
        : base(deviceId, channel, decoder, maxBufferedBytes)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _onReading = onReading ?? throw new ArgumentNullException(nameof(onReading));
    }

    /// <inheritdoc />
    protected override CommunicationResult<TReading> ParseFrame(ReadOnlyMemory<byte> frame) => _parser(frame);

    /// <inheritdoc />
    protected override void OnReading(TReading reading, ReadOnlyMemory<byte> rawFrame) =>
        _onReading(reading, rawFrame);
}
