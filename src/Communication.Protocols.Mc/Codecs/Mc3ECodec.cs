using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Mc.Models;

namespace Communication.Protocols.Mc.Codecs;

/// <summary>Encodes and decodes QnA-compatible MC 3E binary frames.</summary>
public sealed class Mc3ECodec : IProtocolCodec<McRequest, McResponse>
{
    private readonly McClientOptions _options;

    /// <summary>Initializes an MC 3E codec.</summary>
    public Mc3ECodec(McClientOptions? options = null)
    {
        _options = options ?? new McClientOptions();
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Encode(McRequest request) => EncodeRequest(request, _options);

    /// <inheritdoc />
    public ProtocolDecodeResult<McResponse> TryDecode(ReadOnlySequence<byte> buffer) => TryDecodeResponse(buffer);

    /// <summary>Encodes one batch read or write request.</summary>
    public static ReadOnlyMemory<byte> EncodeRequest(McRequest request, McClientOptions? options = null)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        options ??= new McClientOptions();
        ValidateRequest(request);
        byte[] payload = BuildRequestData(request);
        ushort requestLength = checked((ushort)(6 + payload.Length));
        byte[] frame = new byte[9 + requestLength];
        frame[0] = 0x50;
        frame[1] = 0x00;
        frame[2] = options.NetworkNumber;
        frame[3] = options.StationNumber;
        WriteUInt16(frame, 4, options.ModuleIoNumber);
        frame[6] = options.MultidropStationNumber;
        WriteUInt16(frame, 7, requestLength);
        WriteUInt16(frame, 9, request.MonitoringTimer);
        WriteUInt16(frame, 11, request.IsWrite ? (ushort)0x1401 : (ushort)0x0401);
        WriteUInt16(frame, 13, request.Address.IsBitDevice ? (ushort)0x0001 : (ushort)0x0000);
        payload.CopyTo(frame, 15);
        return frame;
    }

    /// <summary>Incrementally decodes one 3E binary response.</summary>
    public static ProtocolDecodeResult<McResponse> TryDecodeResponse(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < 9)
        {
            return ProtocolDecodeResult<McResponse>.NeedMoreData(buffer.Length);
        }

        byte[] header = buffer.Slice(0, 9).ToArray();
        if (header[0] != 0xD0 || header[1] != 0x00)
        {
            return Invalid<McResponse>("The MC 3E response subheader must be D000.", 1, 2);
        }

        ushort length = ReadUInt16(header, 7);
        if (length < 2)
        {
            return Invalid<McResponse>("The MC 3E response length is invalid.", 9, 9);
        }

        int total = 9 + length;
        if (buffer.Length < total)
        {
            return ProtocolDecodeResult<McResponse>.NeedMoreData(buffer.Length);
        }

        byte[] frame = buffer.Slice(0, total).ToArray();
        return ProtocolDecodeResult<McResponse>.Done(new McResponse(
            ReadUInt16(frame, 9),
            frame.AsMemory(11, length - 2).ToArray()), total);
    }

    /// <summary>Incrementally decodes one request for simulator use.</summary>
    public static ProtocolDecodeResult<McRequest> TryDecodeRequest(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < 15)
        {
            return ProtocolDecodeResult<McRequest>.NeedMoreData(buffer.Length);
        }

        byte[] header = buffer.Slice(0, 15).ToArray();
        if (header[0] != 0x50 || header[1] != 0x00)
        {
            return Invalid<McRequest>("The MC 3E request subheader must be 5000.", 1, 2);
        }

        ushort length = ReadUInt16(header, 7);
        int total = 9 + length;
        if (length < 12 || buffer.Length < total)
        {
            return buffer.Length < total
                ? ProtocolDecodeResult<McRequest>.NeedMoreData(buffer.Length)
                : Invalid<McRequest>("The MC 3E request length is invalid.", total, total);
        }

        byte[] frame = buffer.Slice(0, total).ToArray();
        ushort command = ReadUInt16(frame, 11);
        ushort subcommand = ReadUInt16(frame, 13);
        if (command is not (0x0401 or 0x1401) || subcommand is not (0x0000 or 0x0001))
        {
            return Invalid<McRequest>("Only MC batch read/write word or bit subcommands are supported.", total, total);
        }

        int deviceNumber = frame[15] | (frame[16] << 8) | (frame[17] << 16);
        if (!Enum.IsDefined(typeof(McDeviceCode), frame[18]))
        {
            return Invalid<McRequest>("The MC device code is unsupported.", total, total);
        }

        var address = new McAddress((McDeviceCode)frame[18], deviceNumber, string.Empty);
        ushort points = ReadUInt16(frame, 19);
        byte[] data = frame.AsSpan(21).ToArray();
        var request = new McRequest(address, points, command == 0x1401, data, ReadUInt16(frame, 9));
        try
        {
            ValidateRequest(request);
            return ProtocolDecodeResult<McRequest>.Done(request, total);
        }
        catch (Exception exception)
        {
            return Invalid<McRequest>(exception.Message, total, total);
        }
    }

    /// <summary>Encodes one response for simulator use.</summary>
    public static ReadOnlyMemory<byte> EncodeResponse(
        McResponse response,
        McClientOptions? options = null)
    {
        options ??= new McClientOptions();
        ushort length = checked((ushort)(2 + response.Data.Length));
        byte[] frame = new byte[9 + length];
        frame[0] = 0xD0;
        frame[1] = 0x00;
        frame[2] = options.NetworkNumber;
        frame[3] = options.StationNumber;
        WriteUInt16(frame, 4, options.ModuleIoNumber);
        frame[6] = options.MultidropStationNumber;
        WriteUInt16(frame, 7, length);
        WriteUInt16(frame, 9, response.EndCode);
        response.Data.Span.CopyTo(frame.AsSpan(11));
        return frame;
    }

    private static byte[] BuildRequestData(McRequest request)
    {
        byte[] payload = new byte[6 + request.Data.Length];
        payload[0] = (byte)request.Address.DeviceNumber;
        payload[1] = (byte)(request.Address.DeviceNumber >> 8);
        payload[2] = (byte)(request.Address.DeviceNumber >> 16);
        payload[3] = (byte)request.Address.DeviceCode;
        WriteUInt16(payload, 4, request.Points);
        request.Data.Span.CopyTo(payload.AsSpan(6));
        return payload;
    }

    private static void ValidateRequest(McRequest request)
    {
        if (request.Points == 0 || request.Address.DeviceNumber < 0 ||
            (long)request.Address.DeviceNumber + request.Points > 0x1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The MC device range is invalid.");
        }

        int expected = request.IsWrite
            ? request.Address.IsBitDevice ? (request.Points + 1) / 2 : request.Points * 2
            : 0;
        if (request.Data.Length != expected)
        {
            throw new ArgumentException("The MC request data length does not match its point count.", nameof(request));
        }
    }

    internal static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    internal static void WriteUInt16(Span<byte> bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static ProtocolDecodeResult<T> Invalid<T>(string message, long consumed, long examined) =>
        ProtocolDecodeResult<T>.Invalid(
            new CommunicationError(CommunicationErrorCode.ProtocolError, message),
            consumed,
            examined);
}
