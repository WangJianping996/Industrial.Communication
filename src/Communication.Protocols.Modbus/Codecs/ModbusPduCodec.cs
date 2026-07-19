using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Models;

namespace Communication.Protocols.Modbus.Codecs;

/// <summary>Encodes and decodes transport-independent Modbus protocol data units.</summary>
public static class ModbusPduCodec
{
    /// <summary>Encodes and validates one request PDU.</summary>
    public static CommunicationResult<ReadOnlyMemory<byte>> EncodeRequest(ModbusRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        CommunicationError? validation = ValidateRequest(request);
        if (validation is not null)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Failure(validation);
        }

        byte function = (byte)request.FunctionCode;
        if (IsRead(request.FunctionCode))
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Success(
                new byte[]
                {
                    function,
                    (byte)(request.Address >> 8),
                    (byte)request.Address,
                    (byte)(request.Quantity >> 8),
                    (byte)request.Quantity,
                });
        }

        if (request.FunctionCode is ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteSingleRegister)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Success(
                new byte[]
                {
                    function,
                    (byte)(request.Address >> 8),
                    (byte)request.Address,
                    request.Data.Span[0],
                    request.Data.Span[1],
                });
        }

        byte[] pdu = new byte[6 + request.Data.Length];
        pdu[0] = function;
        WriteUInt16(pdu, 1, request.Address);
        WriteUInt16(pdu, 3, request.Quantity);
        pdu[5] = checked((byte)request.Data.Length);
        request.Data.Span.CopyTo(pdu.AsSpan(6));
        return CommunicationResult<ReadOnlyMemory<byte>>.Success(pdu);
    }

    /// <summary>Decodes and validates one complete request PDU.</summary>
    public static CommunicationResult<ModbusRequest> DecodeRequest(
        ReadOnlySpan<byte> pdu,
        ushort transactionId,
        byte unitId)
    {
        if (pdu.Length < 5 || !TryGetFunction(pdu[0], out ModbusFunctionCode function))
        {
            return ProtocolFailure<ModbusRequest>("The Modbus request function or length is invalid.");
        }

        ushort address = ReadUInt16(pdu, 1);
        ModbusRequest request;
        if (IsRead(function))
        {
            if (pdu.Length != 5)
            {
                return ProtocolFailure<ModbusRequest>("A Modbus read request PDU must contain five bytes.");
            }

            request = new ModbusRequest(transactionId, unitId, function, address, ReadUInt16(pdu, 3), ReadOnlyMemory<byte>.Empty);
        }
        else if (function is ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteSingleRegister)
        {
            if (pdu.Length != 5)
            {
                return ProtocolFailure<ModbusRequest>("A Modbus single-write request PDU must contain five bytes.");
            }

            request = new ModbusRequest(transactionId, unitId, function, address, 1, pdu.Slice(3, 2).ToArray());
        }
        else
        {
            if (pdu.Length < 6 || pdu[5] != pdu.Length - 6)
            {
                return ProtocolFailure<ModbusRequest>("The Modbus multiple-write byte count is invalid.");
            }

            request = new ModbusRequest(
                transactionId,
                unitId,
                function,
                address,
                ReadUInt16(pdu, 3),
                pdu.Slice(6).ToArray());
        }

        CommunicationError? validation = ValidateRequest(request);
        return validation is null
            ? CommunicationResult<ModbusRequest>.Success(request)
            : CommunicationResult<ModbusRequest>.Failure(validation);
    }

    /// <summary>Encodes one response PDU.</summary>
    public static CommunicationResult<ReadOnlyMemory<byte>> EncodeResponse(ModbusResponse response)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        if (response.ExceptionCode.HasValue)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Success(new byte[]
            {
                (byte)((byte)response.FunctionCode | 0x80),
                (byte)response.ExceptionCode.Value,
            });
        }

        if (IsRead(response.FunctionCode))
        {
            if (response.Data.Length > 250)
            {
                return ProtocolFailure<ReadOnlyMemory<byte>>("The Modbus read response data is too long.");
            }

            byte[] pdu = new byte[2 + response.Data.Length];
            pdu[0] = (byte)response.FunctionCode;
            pdu[1] = checked((byte)response.Data.Length);
            response.Data.Span.CopyTo(pdu.AsSpan(2));
            return CommunicationResult<ReadOnlyMemory<byte>>.Success(pdu);
        }

        if (response.Data.Length != 4)
        {
            return ProtocolFailure<ReadOnlyMemory<byte>>("A Modbus write response must contain a four-byte echo.");
        }

        byte[] writePdu = new byte[5];
        writePdu[0] = (byte)response.FunctionCode;
        response.Data.Span.CopyTo(writePdu.AsSpan(1));
        return CommunicationResult<ReadOnlyMemory<byte>>.Success(writePdu);
    }

    /// <summary>Decodes one complete response PDU.</summary>
    public static CommunicationResult<ModbusResponse> DecodeResponse(
        ReadOnlySpan<byte> pdu,
        ushort transactionId,
        byte unitId)
    {
        if (pdu.Length < 2)
        {
            return ProtocolFailure<ModbusResponse>("The Modbus response PDU is incomplete.");
        }

        bool isException = (pdu[0] & 0x80) != 0;
        byte rawFunction = (byte)(pdu[0] & 0x7F);
        if (!TryGetFunction(rawFunction, out ModbusFunctionCode function))
        {
            return ProtocolFailure<ModbusResponse>($"Unsupported Modbus function 0x{rawFunction:X2}.");
        }

        if (isException)
        {
            if (pdu.Length != 2 || !Enum.IsDefined(typeof(ModbusExceptionCode), pdu[1]))
            {
                return ProtocolFailure<ModbusResponse>("The Modbus exception response is invalid.");
            }

            return CommunicationResult<ModbusResponse>.Success(new ModbusResponse(
                transactionId,
                unitId,
                function,
                ReadOnlyMemory<byte>.Empty,
                (ModbusExceptionCode)pdu[1]));
        }

        if (IsRead(function))
        {
            if (pdu[1] != pdu.Length - 2)
            {
                return ProtocolFailure<ModbusResponse>("The Modbus read response byte count is invalid.");
            }

            return CommunicationResult<ModbusResponse>.Success(new ModbusResponse(
                transactionId,
                unitId,
                function,
                pdu.Slice(2).ToArray()));
        }

        if (pdu.Length != 5)
        {
            return ProtocolFailure<ModbusResponse>("A Modbus write response PDU must contain five bytes.");
        }

        return CommunicationResult<ModbusResponse>.Success(new ModbusResponse(
            transactionId,
            unitId,
            function,
            pdu.Slice(1, 4).ToArray()));
    }

    internal static CommunicationError? ValidateRequest(ModbusRequest request)
    {
        ushort maximum = request.FunctionCode switch
        {
            ModbusFunctionCode.ReadCoils or ModbusFunctionCode.ReadDiscreteInputs => ModbusLimits.MaxReadBits,
            ModbusFunctionCode.ReadHoldingRegisters or ModbusFunctionCode.ReadInputRegisters => ModbusLimits.MaxReadRegisters,
            ModbusFunctionCode.WriteMultipleCoils => ModbusLimits.MaxWriteCoils,
            ModbusFunctionCode.WriteMultipleRegisters => ModbusLimits.MaxWriteRegisters,
            ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteSingleRegister => 1,
            _ => 0,
        };
        if (maximum == 0)
        {
            return new CommunicationError(CommunicationErrorCode.InvalidValue, "The Modbus function is unsupported.");
        }

        if (request.Quantity == 0 || request.Quantity > maximum ||
            (long)request.Address + request.Quantity > 65_536)
        {
            return new CommunicationError(
                CommunicationErrorCode.InvalidAddress,
                "The Modbus zero-based address range or quantity is invalid.");
        }

        int expectedDataLength = request.FunctionCode switch
        {
            ModbusFunctionCode.ReadCoils or
            ModbusFunctionCode.ReadDiscreteInputs or
            ModbusFunctionCode.ReadHoldingRegisters or
            ModbusFunctionCode.ReadInputRegisters => 0,
            ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteSingleRegister => 2,
            ModbusFunctionCode.WriteMultipleCoils => (request.Quantity + 7) / 8,
            ModbusFunctionCode.WriteMultipleRegisters => request.Quantity * 2,
            _ => -1,
        };
        if (request.Data.Length != expectedDataLength)
        {
            return new CommunicationError(CommunicationErrorCode.InvalidValue, "The Modbus request data length is invalid.");
        }

        if (request.FunctionCode == ModbusFunctionCode.WriteSingleCoil &&
            !(request.Data.Span.SequenceEqual(new byte[] { 0xFF, 0x00 }) ||
              request.Data.Span.SequenceEqual(new byte[] { 0x00, 0x00 })))
        {
            return new CommunicationError(CommunicationErrorCode.InvalidValue, "A single-coil value must be FF00 or 0000.");
        }

        return null;
    }

    internal static bool IsRead(ModbusFunctionCode function) => function is
        ModbusFunctionCode.ReadCoils or
        ModbusFunctionCode.ReadDiscreteInputs or
        ModbusFunctionCode.ReadHoldingRegisters or
        ModbusFunctionCode.ReadInputRegisters;

    internal static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    internal static void WriteUInt16(Span<byte> bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    private static bool TryGetFunction(byte value, out ModbusFunctionCode function)
    {
        function = (ModbusFunctionCode)value;
        return Enum.IsDefined(typeof(ModbusFunctionCode), function);
    }

    private static CommunicationResult<T> ProtocolFailure<T>(string message) =>
        CommunicationResult<T>.Failure(new CommunicationError(CommunicationErrorCode.ProtocolError, message));
}
