using System.Buffers;
using Communication.Abstractions.Models;

namespace Communication.Abstractions.Interfaces;

/// <summary>Defines stateless request encoding and incremental response decoding.</summary>
/// <typeparam name="TRequest">The protocol request model.</typeparam>
/// <typeparam name="TResponse">The protocol response model.</typeparam>
public interface IProtocolCodec<in TRequest, TResponse>
{
    /// <summary>Encodes one request without performing I/O.</summary>
    ReadOnlyMemory<byte> Encode(TRequest request);

    /// <summary>Attempts to decode one response from the supplied buffered bytes.</summary>
    ProtocolDecodeResult<TResponse> TryDecode(ReadOnlySequence<byte> buffer);
}

/// <summary>Defines an incremental frame boundary decoder for sticky and fragmented streams.</summary>
public interface IFrameDecoder
{
    /// <summary>Attempts to extract one complete frame from the supplied buffered bytes.</summary>
    FrameDecodeResult TryDecode(ReadOnlySequence<byte> buffer);
}

/// <summary>Defines a message integrity algorithm such as CRC16 or LRC.</summary>
public interface IChecksum
{
    /// <summary>Gets the checksum size in bytes.</summary>
    int Size { get; }

    /// <summary>Computes the checksum bytes for a payload.</summary>
    ReadOnlyMemory<byte> Compute(ReadOnlySpan<byte> payload);

    /// <summary>Validates a payload against expected checksum bytes.</summary>
    bool Validate(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> expectedChecksum);
}

/// <summary>Represents an incremental protocol decoding outcome.</summary>
/// <typeparam name="T">The decoded response type.</typeparam>
public sealed record ProtocolDecodeResult<T>
{
    private ProtocolDecodeResult(
        DecodeStatus status,
        T? value,
        long consumed,
        long examined,
        CommunicationError? error)
    {
        Status = status;
        Value = value;
        Consumed = consumed;
        Examined = examined;
        Error = error;
    }

    /// <summary>Gets the decode status.</summary>
    public DecodeStatus Status { get; }

    /// <summary>Gets the decoded value when <see cref="Status"/> is <see cref="DecodeStatus.Done"/>.</summary>
    public T? Value { get; }

    /// <summary>Gets the number of bytes that may be removed from the buffer.</summary>
    public long Consumed { get; }

    /// <summary>Gets the number of bytes inspected by the decoder.</summary>
    public long Examined { get; }

    /// <summary>Gets structured details when decoding failed.</summary>
    public CommunicationError? Error { get; }

    /// <summary>Creates an incomplete result.</summary>
    public static ProtocolDecodeResult<T> NeedMoreData(long examined) =>
        new(DecodeStatus.NeedMoreData, default, 0, examined, null);

    /// <summary>Creates a completed result.</summary>
    public static ProtocolDecodeResult<T> Done(T value, long consumed) =>
        new(DecodeStatus.Done, value, consumed, consumed, null);

    /// <summary>Creates an invalid-data result.</summary>
    public static ProtocolDecodeResult<T> Invalid(
        CommunicationError error,
        long consumed,
        long examined) => new(DecodeStatus.InvalidData, default, consumed, examined, error);
}

/// <summary>Represents an incremental frame decoding outcome.</summary>
public sealed record FrameDecodeResult
{
    private FrameDecodeResult(
        DecodeStatus status,
        ReadOnlyMemory<byte> frame,
        long consumed,
        long examined,
        CommunicationError? error)
    {
        Status = status;
        Frame = frame;
        Consumed = consumed;
        Examined = examined;
        Error = error;
    }

    /// <summary>Gets the decode status.</summary>
    public DecodeStatus Status { get; }

    /// <summary>Gets the complete frame when available.</summary>
    public ReadOnlyMemory<byte> Frame { get; }

    /// <summary>Gets the number of bytes that may be removed from the buffer.</summary>
    public long Consumed { get; }

    /// <summary>Gets the number of bytes inspected by the decoder.</summary>
    public long Examined { get; }

    /// <summary>Gets structured details when decoding failed.</summary>
    public CommunicationError? Error { get; }

    /// <summary>Creates an incomplete result.</summary>
    public static FrameDecodeResult NeedMoreData(long examined) =>
        new(DecodeStatus.NeedMoreData, ReadOnlyMemory<byte>.Empty, 0, examined, null);

    /// <summary>Creates a completed result.</summary>
    public static FrameDecodeResult Done(ReadOnlyMemory<byte> frame, long consumed) =>
        new(DecodeStatus.Done, frame, consumed, consumed, null);

    /// <summary>Creates an invalid-data result.</summary>
    public static FrameDecodeResult Invalid(
        CommunicationError error,
        long consumed,
        long examined) =>
        new(DecodeStatus.InvalidData, ReadOnlyMemory<byte>.Empty, consumed, examined, error);
}
