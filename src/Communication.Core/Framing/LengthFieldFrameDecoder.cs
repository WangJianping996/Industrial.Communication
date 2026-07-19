using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Framing;

/// <summary>Extracts frames whose total size is encoded in a one-, two-, or four-byte field.</summary>
public sealed class LengthFieldFrameDecoder : IFrameDecoder
{
    private readonly int _lengthFieldEnd;

    /// <summary>Initializes a length-field decoder.</summary>
    /// <param name="lengthFieldOffset">Zero-based offset of the length field.</param>
    /// <param name="lengthFieldLength">Length field size: 1, 2, or 4 bytes.</param>
    /// <param name="lengthAdjustment">Value added to the decoded length to obtain total frame length.</param>
    /// <param name="initialBytesToStrip">Bytes removed from the returned frame, but still consumed.</param>
    /// <param name="littleEndian">Whether the length field uses little-endian byte order.</param>
    /// <param name="maxFrameLength">Maximum accepted total frame length.</param>
    public LengthFieldFrameDecoder(
        int lengthFieldOffset,
        int lengthFieldLength,
        int lengthAdjustment = 0,
        int initialBytesToStrip = 0,
        bool littleEndian = false,
        int maxFrameLength = 1024 * 1024)
    {
        if (lengthFieldOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthFieldOffset));
        }

        if (lengthFieldLength is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(lengthFieldLength));
        }

        if (initialBytesToStrip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBytesToStrip));
        }

        if (maxFrameLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameLength));
        }

        LengthFieldOffset = lengthFieldOffset;
        LengthFieldLength = lengthFieldLength;
        LengthAdjustment = lengthAdjustment;
        InitialBytesToStrip = initialBytesToStrip;
        LittleEndian = littleEndian;
        MaxFrameLength = maxFrameLength;
        _lengthFieldEnd = checked(lengthFieldOffset + lengthFieldLength);
    }

    /// <summary>Gets the length field offset.</summary>
    public int LengthFieldOffset { get; }

    /// <summary>Gets the length field size.</summary>
    public int LengthFieldLength { get; }

    /// <summary>Gets the total-frame length adjustment.</summary>
    public int LengthAdjustment { get; }

    /// <summary>Gets the number of leading bytes removed from returned frames.</summary>
    public int InitialBytesToStrip { get; }

    /// <summary>Gets whether the length field uses little-endian byte order.</summary>
    public bool LittleEndian { get; }

    /// <summary>Gets the maximum accepted total frame length.</summary>
    public int MaxFrameLength { get; }

    /// <inheritdoc />
    public FrameDecodeResult TryDecode(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < _lengthFieldEnd)
        {
            return FrameDecodeResult.NeedMoreData(buffer.Length);
        }

        byte[] bytes = buffer.Slice(LengthFieldOffset, LengthFieldLength).ToArray();
        uint encodedLength = DecodeLength(bytes);
        long frameLength = encodedLength + (long)LengthAdjustment;

        if (frameLength < _lengthFieldEnd || frameLength < InitialBytesToStrip || frameLength > MaxFrameLength)
        {
            CommunicationError error = new(
                CommunicationErrorCode.ProtocolError,
                $"Invalid frame length {frameLength}; expected {_lengthFieldEnd}..{MaxFrameLength}.");
            return FrameDecodeResult.Invalid(error, 1, _lengthFieldEnd);
        }

        if (buffer.Length < frameLength)
        {
            return FrameDecodeResult.NeedMoreData(buffer.Length);
        }

        long payloadLength = frameLength - InitialBytesToStrip;
        ReadOnlyMemory<byte> frame = buffer.Slice(InitialBytesToStrip, payloadLength).ToArray();
        return FrameDecodeResult.Done(frame, frameLength);
    }

    private uint DecodeLength(byte[] bytes)
    {
        if (LittleEndian)
        {
            Array.Reverse(bytes);
        }

        uint value = 0;
        foreach (byte current in bytes)
        {
            value = (value << 8) | current;
        }

        return value;
    }
}
