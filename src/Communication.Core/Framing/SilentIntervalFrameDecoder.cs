using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Framing;

/// <summary>Completes the current frame after the owning transport observes a silent interval.</summary>
public sealed class SilentIntervalFrameDecoder : IFrameDecoder
{
    private int _silenceObserved;

    /// <summary>Initializes a silent-interval decoder.</summary>
    public SilentIntervalFrameDecoder(int maxFrameLength = 1024 * 1024)
    {
        if (maxFrameLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameLength));
        }

        MaxFrameLength = maxFrameLength;
    }

    /// <summary>Gets the maximum accepted frame length.</summary>
    public int MaxFrameLength { get; }

    /// <summary>Signals that the configured silent interval elapsed after the latest byte.</summary>
    public void NotifySilence() => Interlocked.Exchange(ref _silenceObserved, 1);

    /// <inheritdoc />
    public FrameDecodeResult TryDecode(ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            Interlocked.Exchange(ref _silenceObserved, 0);
            return FrameDecodeResult.NeedMoreData(0);
        }

        if (Volatile.Read(ref _silenceObserved) == 0)
        {
            return FrameDecodeResult.NeedMoreData(buffer.Length);
        }

        if (buffer.Length > MaxFrameLength)
        {
            CommunicationError error = new(
                CommunicationErrorCode.ProtocolError,
                $"Silent-interval frame exceeds {MaxFrameLength} bytes.");
            return FrameDecodeResult.Invalid(error, buffer.Length, buffer.Length);
        }

        Interlocked.Exchange(ref _silenceObserved, 0);
        return FrameDecodeResult.Done(buffer.ToArray(), buffer.Length);
    }
}
