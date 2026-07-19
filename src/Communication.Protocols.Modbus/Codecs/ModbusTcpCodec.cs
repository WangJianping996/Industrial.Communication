using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Models;

namespace Communication.Protocols.Modbus.Codecs;

/// <summary>Provides MBAP frame operations for Modbus TCP requests and responses.</summary>
public static class ModbusTcpFrameCodec
{
    private const int HeaderLength = 7;

    /// <summary>Encodes one Modbus TCP request ADU.</summary>
    public static ReadOnlyMemory<byte> EncodeRequest(ModbusRequest request) =>
        Encode(request.TransactionId, request.UnitId, ModbusPduCodec.EncodeRequest(request).GetValueOrThrow());

    /// <summary>Encodes one Modbus TCP response ADU.</summary>
    public static ReadOnlyMemory<byte> EncodeResponse(ModbusResponse response) =>
        Encode(response.TransactionId, response.UnitId, ModbusPduCodec.EncodeResponse(response).GetValueOrThrow());

    /// <summary>Incrementally decodes one Modbus TCP request ADU.</summary>
    public static ProtocolDecodeResult<ModbusRequest> TryDecodeRequest(ReadOnlySequence<byte> buffer)
    {
        FrameState frame = ReadFrame(buffer);
        if (frame.Status != DecodeStatus.Done)
        {
            return frame.Status == DecodeStatus.NeedMoreData
                ? ProtocolDecodeResult<ModbusRequest>.NeedMoreData(frame.Examined)
                : ProtocolDecodeResult<ModbusRequest>.Invalid(frame.Error!, frame.Consumed, frame.Examined);
        }

        CommunicationResult<ModbusRequest> decoded = ModbusPduCodec.DecodeRequest(
            frame.Bytes.AsSpan(HeaderLength),
            frame.TransactionId,
            frame.UnitId);
        return decoded.IsSuccess
            ? ProtocolDecodeResult<ModbusRequest>.Done(decoded.Value!, frame.Consumed)
            : ProtocolDecodeResult<ModbusRequest>.Invalid(decoded.Error!, frame.Consumed, frame.Consumed);
    }

    /// <summary>Incrementally decodes one Modbus TCP response ADU.</summary>
    public static ProtocolDecodeResult<ModbusResponse> TryDecodeResponse(ReadOnlySequence<byte> buffer)
    {
        FrameState frame = ReadFrame(buffer);
        if (frame.Status != DecodeStatus.Done)
        {
            return frame.Status == DecodeStatus.NeedMoreData
                ? ProtocolDecodeResult<ModbusResponse>.NeedMoreData(frame.Examined)
                : ProtocolDecodeResult<ModbusResponse>.Invalid(frame.Error!, frame.Consumed, frame.Examined);
        }

        CommunicationResult<ModbusResponse> decoded = ModbusPduCodec.DecodeResponse(
            frame.Bytes.AsSpan(HeaderLength),
            frame.TransactionId,
            frame.UnitId);
        return decoded.IsSuccess
            ? ProtocolDecodeResult<ModbusResponse>.Done(decoded.Value!, frame.Consumed)
            : ProtocolDecodeResult<ModbusResponse>.Invalid(decoded.Error!, frame.Consumed, frame.Consumed);
    }

    private static ReadOnlyMemory<byte> Encode(
        ushort transactionId,
        byte unitId,
        ReadOnlyMemory<byte> pdu)
    {
        if (pdu.Length is < 2 or > ModbusLimits.MaxPduLength)
        {
            throw new ArgumentOutOfRangeException(nameof(pdu));
        }

        ushort length = checked((ushort)(pdu.Length + 1));
        byte[] frame = new byte[HeaderLength + pdu.Length];
        ModbusPduCodec.WriteUInt16(frame, 0, transactionId);
        ModbusPduCodec.WriteUInt16(frame, 2, 0);
        ModbusPduCodec.WriteUInt16(frame, 4, length);
        frame[6] = unitId;
        pdu.Span.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    private static FrameState ReadFrame(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < HeaderLength)
        {
            return FrameState.NeedMore(buffer.Length);
        }

        byte[] header = buffer.Slice(0, HeaderLength).ToArray();
        ushort transactionId = ModbusPduCodec.ReadUInt16(header, 0);
        ushort protocolId = ModbusPduCodec.ReadUInt16(header, 2);
        ushort length = ModbusPduCodec.ReadUInt16(header, 4);
        if (protocolId != 0)
        {
            return FrameState.Invalid("The MBAP protocol identifier must be zero.", HeaderLength, HeaderLength);
        }

        if (length is < 3 or > ModbusLimits.MaxPduLength + 1)
        {
            return FrameState.Invalid("The MBAP length field is outside the Modbus limit.", HeaderLength, HeaderLength);
        }

        int totalLength = 6 + length;
        if (buffer.Length < totalLength)
        {
            return FrameState.NeedMore(buffer.Length);
        }

        return FrameState.Done(
            buffer.Slice(0, totalLength).ToArray(),
            transactionId,
            header[6],
            totalLength);
    }

    private sealed record FrameState(
        DecodeStatus Status,
        byte[] Bytes,
        ushort TransactionId,
        byte UnitId,
        long Consumed,
        long Examined,
        CommunicationError? Error)
    {
        public static FrameState NeedMore(long examined) => new(
            DecodeStatus.NeedMoreData, [], 0, 0, 0, examined, null);

        public static FrameState Invalid(string message, long consumed, long examined) => new(
            DecodeStatus.InvalidData,
            [],
            0,
            0,
            consumed,
            examined,
            new CommunicationError(CommunicationErrorCode.ProtocolError, message));

        public static FrameState Done(byte[] bytes, ushort transactionId, byte unitId, int consumed) => new(
            DecodeStatus.Done, bytes, transactionId, unitId, consumed, consumed, null);
    }
}

/// <summary>Adapts Modbus TCP response framing to the common protocol codec contract.</summary>
public sealed class ModbusTcpCodec : IProtocolCodec<ModbusRequest, ModbusResponse>
{
    /// <inheritdoc />
    public ReadOnlyMemory<byte> Encode(ModbusRequest request) => ModbusTcpFrameCodec.EncodeRequest(request);

    /// <inheritdoc />
    public ProtocolDecodeResult<ModbusResponse> TryDecode(ReadOnlySequence<byte> buffer) =>
        ModbusTcpFrameCodec.TryDecodeResponse(buffer);
}
