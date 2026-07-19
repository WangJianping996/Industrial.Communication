using System.Buffers;
using Communication.Abstractions.Interfaces;

namespace Communication.Core.Framing;

/// <summary>Extracts frames with one fixed byte length.</summary>
public sealed class FixedLengthFrameDecoder : IFrameDecoder
{
    /// <summary>Initializes a fixed-length decoder.</summary>
    public FixedLengthFrameDecoder(int frameLength)
    {
        if (frameLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameLength));
        }

        FrameLength = frameLength;
    }

    /// <summary>Gets the required frame length.</summary>
    public int FrameLength { get; }

    /// <inheritdoc />
    public FrameDecodeResult TryDecode(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < FrameLength)
        {
            return FrameDecodeResult.NeedMoreData(buffer.Length);
        }

        return FrameDecodeResult.Done(buffer.Slice(0, FrameLength).ToArray(), FrameLength);
    }
}
