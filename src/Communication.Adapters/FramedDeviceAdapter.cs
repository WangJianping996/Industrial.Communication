using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Adapters;

/// <summary>Combines a channel, incremental frame decoder and device-specific frame parser.</summary>
/// <typeparam name="TReading">The parsed reading type.</typeparam>
public abstract class FramedDeviceAdapter<TReading> : DeviceAdapterBase
{
    private readonly ITransportChannel _channel;
    private readonly IFrameDecoder _decoder;
    private readonly int _maxBufferedBytes;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;

    /// <summary>Initializes a framed adapter.</summary>
    protected FramedDeviceAdapter(
        string deviceId,
        ITransportChannel channel,
        IFrameDecoder decoder,
        int maxBufferedBytes = 1024 * 1024)
        : base(deviceId)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _maxBufferedBytes = maxBufferedBytes > 0
            ? maxBufferedBytes
            : throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
    }

    /// <summary>Sends an explicit raw device command.</summary>
    protected async ValueTask<CommunicationResult> SendCommandAsync(
        ReadOnlyMemory<byte> command,
        CancellationToken cancellationToken)
    {
        CommunicationResult state = EnsureConnected();
        if (!state.IsSuccess)
        {
            return state;
        }

        CommunicationResult<int> sent = await _channel.SendAsync(command, cancellationToken).ConfigureAwait(false);
        return sent.IsSuccess ? CommunicationResult.Success() : CommunicationResult.Failure(sent.Error!);
    }

    /// <summary>Parses one complete device frame.</summary>
    protected abstract CommunicationResult<TReading> ParseFrame(ReadOnlyMemory<byte> frame);

    /// <summary>Receives one parsed reading.</summary>
    protected abstract void OnReading(TReading reading, ReadOnlyMemory<byte> rawFrame);

    /// <inheritdoc />
    protected override async ValueTask<CommunicationResult> OnStartAsync(CancellationToken cancellationToken)
    {
        CommunicationResult connected = await _channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!connected.IsSuccess)
        {
            return connected;
        }

        _receiveCancellation?.Dispose();
        _receiveCancellation = new CancellationTokenSource();
        _receiveTask = ReceiveLoopAsync(_receiveCancellation.Token);
        return CommunicationResult.Success();
    }

    /// <inheritdoc />
    protected override async ValueTask<CommunicationResult> OnStopAsync(CancellationToken cancellationToken)
    {
        _receiveCancellation?.Cancel();
        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_receiveCancellation?.IsCancellationRequested == true)
            {
            }
        }

        _receiveTask = null;
        _receiveCancellation?.Dispose();
        _receiveCancellation = null;
        return await _channel.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync() => _channel.DisposeAsync();

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        List<byte> buffered = [];
        await foreach (ReadOnlyMemory<byte> chunk in _channel.ReceiveAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            buffered.AddRange(chunk.ToArray());
            if (buffered.Count > _maxBufferedBytes)
            {
                buffered.Clear();
                Health = new DeviceHealth(
                    DeviceHealthStatus.Degraded,
                    DateTimeOffset.UtcNow,
                    "The framed device receive buffer overflowed.");
                continue;
            }

            while (buffered.Count > 0)
            {
                FrameDecodeResult decoded = _decoder.TryDecode(new ReadOnlySequence<byte>(buffered.ToArray()));
                if (decoded.Status == DecodeStatus.NeedMoreData)
                {
                    break;
                }

                int consumed = checked((int)decoded.Consumed);
                buffered.RemoveRange(0, Math.Clamp(consumed, 1, buffered.Count));
                if (decoded.Status != DecodeStatus.Done)
                {
                    Health = new DeviceHealth(DeviceHealthStatus.Degraded, DateTimeOffset.UtcNow,
                        decoded.Error?.Message, decoded.Error);
                    continue;
                }

                CommunicationResult<TReading> parsed = ParseFrame(decoded.Frame);
                if (parsed.IsSuccess)
                {
                    OnReading(parsed.Value!, decoded.Frame);
                }
                else
                {
                    Health = new DeviceHealth(DeviceHealthStatus.Degraded, DateTimeOffset.UtcNow,
                        parsed.Error!.Message, parsed.Error);
                }
            }
        }
    }
}
