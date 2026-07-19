using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Checksums;
using Communication.Protocols.Modbus.Models;

namespace Communication.Protocols.Modbus.Codecs;

/// <summary>Provides station-address and CRC16 frame operations for Modbus RTU.</summary>
public static class ModbusRtuFrameCodec
{
    private static readonly Crc16Checksum Checksum = new();

    /// <summary>Encodes one Modbus RTU request ADU.</summary>
    public static ReadOnlyMemory<byte> EncodeRequest(ModbusRequest request)
    {
        ValidateUnitId(request.UnitId);
        return Encode(request.UnitId, ModbusPduCodec.EncodeRequest(request).GetValueOrThrow());
    }

    /// <summary>Encodes one Modbus RTU response ADU.</summary>
    public static ReadOnlyMemory<byte> EncodeResponse(ModbusResponse response)
    {
        ValidateUnitId(response.UnitId);
        return Encode(response.UnitId, ModbusPduCodec.EncodeResponse(response).GetValueOrThrow());
    }

    /// <summary>Incrementally decodes one Modbus RTU request ADU.</summary>
    public static ProtocolDecodeResult<ModbusRequest> TryDecodeRequest(ReadOnlySequence<byte> buffer)
    {
        int expectedLength = GetRequestLength(buffer);
        if (expectedLength == 0)
        {
            return ProtocolDecodeResult<ModbusRequest>.NeedMoreData(buffer.Length);
        }

        if (expectedLength < 0)
        {
            return Invalid<ModbusRequest>("The Modbus RTU request function or length is invalid.", 1, buffer.Length);
        }

        if (buffer.Length < expectedLength)
        {
            return ProtocolDecodeResult<ModbusRequest>.NeedMoreData(buffer.Length);
        }

        byte[] frame = buffer.Slice(0, expectedLength).ToArray();
        CommunicationError? integrity = ValidateFrame(frame);
        if (integrity is not null)
        {
            return ProtocolDecodeResult<ModbusRequest>.Invalid(integrity, expectedLength, expectedLength);
        }

        CommunicationResult<ModbusRequest> decoded = ModbusPduCodec.DecodeRequest(
            frame.AsSpan(1, frame.Length - 3),
            0,
            frame[0]);
        return decoded.IsSuccess
            ? ProtocolDecodeResult<ModbusRequest>.Done(decoded.Value!, expectedLength)
            : ProtocolDecodeResult<ModbusRequest>.Invalid(decoded.Error!, expectedLength, expectedLength);
    }

    /// <summary>Incrementally decodes one Modbus RTU response ADU.</summary>
    public static ProtocolDecodeResult<ModbusResponse> TryDecodeResponse(ReadOnlySequence<byte> buffer)
    {
        int expectedLength = GetResponseLength(buffer);
        if (expectedLength == 0)
        {
            return ProtocolDecodeResult<ModbusResponse>.NeedMoreData(buffer.Length);
        }

        if (expectedLength < 0)
        {
            return Invalid<ModbusResponse>("The Modbus RTU response function or length is invalid.", 1, buffer.Length);
        }

        if (buffer.Length < expectedLength)
        {
            return ProtocolDecodeResult<ModbusResponse>.NeedMoreData(buffer.Length);
        }

        byte[] frame = buffer.Slice(0, expectedLength).ToArray();
        CommunicationError? integrity = ValidateFrame(frame);
        if (integrity is not null)
        {
            return ProtocolDecodeResult<ModbusResponse>.Invalid(integrity, expectedLength, expectedLength);
        }

        CommunicationResult<ModbusResponse> decoded = ModbusPduCodec.DecodeResponse(
            frame.AsSpan(1, frame.Length - 3),
            0,
            frame[0]);
        return decoded.IsSuccess
            ? ProtocolDecodeResult<ModbusResponse>.Done(decoded.Value!, expectedLength)
            : ProtocolDecodeResult<ModbusResponse>.Invalid(decoded.Error!, expectedLength, expectedLength);
    }

    private static ReadOnlyMemory<byte> Encode(byte unitId, ReadOnlyMemory<byte> pdu)
    {
        if (pdu.Length is < 2 or > ModbusLimits.MaxPduLength)
        {
            throw new ArgumentOutOfRangeException(nameof(pdu));
        }

        byte[] frame = new byte[1 + pdu.Length + 2];
        frame[0] = unitId;
        pdu.Span.CopyTo(frame.AsSpan(1));
        Checksum.Compute(frame.AsSpan(0, frame.Length - 2)).Span.CopyTo(frame.AsSpan(frame.Length - 2));
        return frame;
    }

    private static int GetRequestLength(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < 2)
        {
            return 0;
        }

        byte[] prefix = buffer.Slice(0, Math.Min(buffer.Length, 7)).ToArray();
        byte function = prefix[1];
        if (function is >= 0x01 and <= 0x06)
        {
            return Enum.IsDefined(typeof(ModbusFunctionCode), function) ? 8 : -1;
        }

        if (function is 0x0F or 0x10)
        {
            return prefix.Length < 7 ? 0 : 9 + prefix[6];
        }

        return -1;
    }

    private static int GetResponseLength(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < 2)
        {
            return 0;
        }

        byte[] prefix = buffer.Slice(0, Math.Min(buffer.Length, 3)).ToArray();
        byte function = prefix[1];
        if ((function & 0x80) != 0)
        {
            return 5;
        }

        if (function is >= 0x01 and <= 0x04)
        {
            return prefix.Length < 3 ? 0 : 5 + prefix[2];
        }

        return function is 0x05 or 0x06 or 0x0F or 0x10 ? 8 : -1;
    }

    private static CommunicationError? ValidateFrame(byte[] frame)
    {
        if (frame[0] > 247)
        {
            return new CommunicationError(CommunicationErrorCode.InvalidAddress, "A Modbus RTU station must be in range 0..247.");
        }

        return Checksum.Validate(frame.AsSpan(0, frame.Length - 2), frame.AsSpan(frame.Length - 2))
            ? null
            : new CommunicationError(CommunicationErrorCode.ChecksumFailure, "The Modbus RTU CRC16 is invalid.");
    }

    private static ProtocolDecodeResult<T> Invalid<T>(string message, long consumed, long examined) =>
        ProtocolDecodeResult<T>.Invalid(
            new CommunicationError(CommunicationErrorCode.ProtocolError, message),
            consumed,
            examined);

    private static void ValidateUnitId(byte unitId)
    {
        if (unitId > 247)
        {
            throw new ArgumentOutOfRangeException(nameof(unitId), "A Modbus RTU station must be in range 0..247.");
        }
    }
}

/// <summary>Adapts Modbus RTU response framing to the common protocol codec contract.</summary>
public sealed class ModbusRtuCodec : IProtocolCodec<ModbusRequest, ModbusResponse>
{
    /// <inheritdoc />
    public ReadOnlyMemory<byte> Encode(ModbusRequest request) => ModbusRtuFrameCodec.EncodeRequest(request);

    /// <inheritdoc />
    public ProtocolDecodeResult<ModbusResponse> TryDecode(ReadOnlySequence<byte> buffer) =>
        ModbusRtuFrameCodec.TryDecodeResponse(buffer);
}
