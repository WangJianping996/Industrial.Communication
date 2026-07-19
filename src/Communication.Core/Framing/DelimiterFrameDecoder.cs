using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Framing;

/// <summary>Extracts frames terminated by a byte delimiter.</summary>
public sealed class DelimiterFrameDecoder : IFrameDecoder
{
    private readonly byte[] _delimiter;

    /// <summary>Initializes a delimiter decoder.</summary>
    public DelimiterFrameDecoder(
        ReadOnlySpan<byte> delimiter,
        bool includeDelimiter = false,
        int maxFrameLength = 1024 * 1024)
    {
        if (delimiter.IsEmpty)
        {
            throw new ArgumentException("A delimiter is required.", nameof(delimiter));
        }

        if (maxFrameLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameLength));
        }

        _delimiter = delimiter.ToArray();
        IncludeDelimiter = includeDelimiter;
        MaxFrameLength = maxFrameLength;
    }

    /// <summary>Gets whether returned frames include their delimiter.</summary>
    public bool IncludeDelimiter { get; }

    /// <summary>Gets the maximum accepted frame length including its delimiter.</summary>
    public int MaxFrameLength { get; }

    /// <inheritdoc />
    public FrameDecodeResult TryDecode(ReadOnlySequence<byte> buffer)
    {
        byte[] bytes = buffer.ToArray();
        int delimiterIndex = IndexOf(bytes, _delimiter);
        if (delimiterIndex < 0)
        {
            if (buffer.Length <= MaxFrameLength)
            {
                return FrameDecodeResult.NeedMoreData(buffer.Length);
            }

            CommunicationError error = new(
                CommunicationErrorCode.ProtocolError,
                $"Delimiter was not found within {MaxFrameLength} bytes.");
            return FrameDecodeResult.Invalid(error, 1, buffer.Length);
        }

        int consumed = checked(delimiterIndex + _delimiter.Length);
        if (consumed > MaxFrameLength)
        {
            CommunicationError error = new(
                CommunicationErrorCode.ProtocolError,
                $"Delimited frame length {consumed} exceeds {MaxFrameLength} bytes.");
            return FrameDecodeResult.Invalid(error, consumed, consumed);
        }

        int returnedLength = IncludeDelimiter ? consumed : delimiterIndex;
        return FrameDecodeResult.Done(bytes.AsMemory(0, returnedLength).ToArray(), consumed);
    }

    private static int IndexOf(byte[] buffer, byte[] delimiter)
    {
        int lastStart = buffer.Length - delimiter.Length;
        for (int index = 0; index <= lastStart; index++)
        {
            bool matched = true;
            for (int delimiterIndex = 0; delimiterIndex < delimiter.Length; delimiterIndex++)
            {
                if (buffer[index + delimiterIndex] != delimiter[delimiterIndex])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }
}
