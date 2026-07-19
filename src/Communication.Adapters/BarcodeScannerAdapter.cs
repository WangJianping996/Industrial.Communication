using System.Text;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Adapters;

/// <summary>Parses delimiter- or length-framed barcode results with duplicate filtering.</summary>
public sealed class BarcodeScannerAdapter : FramedDeviceAdapter<string>, IBarcodeScanner
{
    private readonly Encoding _encoding;
    private readonly ReadOnlyMemory<byte> _triggerCommand;
    private readonly TimeSpan _duplicateWindow;
    private string? _lastValue;
    private DateTimeOffset _lastTimestamp;

    /// <summary>Initializes a framed barcode scanner.</summary>
    public BarcodeScannerAdapter(
        string deviceId,
        ITransportChannel channel,
        IFrameDecoder decoder,
        ReadOnlyMemory<byte> triggerCommand,
        TimeSpan? duplicateWindow = null,
        Encoding? encoding = null)
        : base(deviceId, channel, decoder)
    {
        _triggerCommand = triggerCommand;
        _duplicateWindow = duplicateWindow ?? TimeSpan.FromMilliseconds(500);
        if (_duplicateWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duplicateWindow));
        }

        _encoding = encoding ?? Encoding.ASCII;
    }

    /// <inheritdoc />
    public event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    /// <inheritdoc />
    public ValueTask<CommunicationResult> TriggerAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(_triggerCommand, cancellationToken);

    /// <inheritdoc />
    protected override CommunicationResult<string> ParseFrame(ReadOnlyMemory<byte> frame)
    {
        string value = _encoding.GetString(frame.ToArray()).Trim();
        return value.Length == 0
            ? CommunicationResult<string>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "The scanner returned an empty barcode."))
            : CommunicationResult<string>.Success(value);
    }

    /// <inheritdoc />
    protected override void OnReading(string reading, ReadOnlyMemory<byte> rawFrame)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.Equals(reading, _lastValue, StringComparison.Ordinal) &&
            now - _lastTimestamp <= _duplicateWindow)
        {
            return;
        }

        _lastValue = reading;
        _lastTimestamp = now;
        BarcodeScanned?.Invoke(this, new BarcodeScannedEventArgs(reading, now, rawFrame.ToArray()));
    }
}
