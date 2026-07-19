using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Codecs;
using Communication.Protocols.Modbus.Models;

namespace Communication.Protocols.Modbus.Simulator;

/// <summary>Processes the supported Modbus functions against an in-memory data store.</summary>
public sealed class ModbusSlave
{
    /// <summary>Initializes a Modbus slave.</summary>
    public ModbusSlave(byte unitId = 1, ModbusDataStore? dataStore = null)
    {
        UnitId = unitId;
        DataStore = dataStore ?? new ModbusDataStore();
    }

    /// <summary>Gets the configured unit identifier.</summary>
    public byte UnitId { get; }

    /// <summary>Gets the backing data store.</summary>
    public ModbusDataStore DataStore { get; }

    /// <summary>Processes one validated request into a normal or exception response.</summary>
    public ModbusResponse ProcessRequest(ModbusRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        CommunicationError? validation = ModbusPduCodec.ValidateRequest(request);
        if (validation is not null)
        {
            return Exception(request, validation.Code == CommunicationErrorCode.InvalidAddress
                ? ModbusExceptionCode.IllegalDataAddress
                : ModbusExceptionCode.IllegalDataValue);
        }

        try
        {
            return request.FunctionCode switch
            {
                ModbusFunctionCode.ReadCoils => ReadBits(request, ModbusDataArea.Coils),
                ModbusFunctionCode.ReadDiscreteInputs => ReadBits(request, ModbusDataArea.DiscreteInputs),
                ModbusFunctionCode.ReadHoldingRegisters => ReadRegisters(request, ModbusDataArea.HoldingRegisters),
                ModbusFunctionCode.ReadInputRegisters => ReadRegisters(request, ModbusDataArea.InputRegisters),
                ModbusFunctionCode.WriteSingleCoil => WriteSingleCoil(request),
                ModbusFunctionCode.WriteSingleRegister => WriteSingleRegister(request),
                ModbusFunctionCode.WriteMultipleCoils => WriteMultipleCoils(request),
                ModbusFunctionCode.WriteMultipleRegisters => WriteMultipleRegisters(request),
                _ => Exception(request, ModbusExceptionCode.IllegalFunction),
            };
        }
        catch (Exception)
        {
            return Exception(request, ModbusExceptionCode.ServerDeviceFailure);
        }
    }

    private ModbusResponse ReadBits(ModbusRequest request, ModbusDataArea area)
    {
        CommunicationResult<IReadOnlyList<bool>> result = DataStore.ReadBits(area, request.Address, request.Quantity);
        if (!result.IsSuccess)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataAddress);
        }

        byte[] packed = new byte[(request.Quantity + 7) / 8];
        for (int index = 0; index < request.Quantity; index++)
        {
            if (result.Value![index])
            {
                packed[index / 8] |= (byte)(1 << (index % 8));
            }
        }

        return Response(request, packed);
    }

    private ModbusResponse ReadRegisters(ModbusRequest request, ModbusDataArea area)
    {
        CommunicationResult<IReadOnlyList<ushort>> result = DataStore.ReadRegisters(
            area,
            request.Address,
            request.Quantity);
        if (!result.IsSuccess)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataAddress);
        }

        byte[] data = new byte[request.Quantity * 2];
        for (int index = 0; index < request.Quantity; index++)
        {
            ModbusPduCodec.WriteUInt16(data, index * 2, result.Value![index]);
        }

        return Response(request, data);
    }

    private ModbusResponse WriteSingleCoil(ModbusRequest request)
    {
        CommunicationResult result = DataStore.SetBits(
            ModbusDataArea.Coils,
            request.Address,
            [request.Data.Span[0] == 0xFF]);
        return result.IsSuccess
            ? WriteEcho(request, ModbusPduCodec.ReadUInt16(request.Data.Span, 0))
            : Exception(request, ModbusExceptionCode.IllegalDataAddress);
    }

    private ModbusResponse WriteSingleRegister(ModbusRequest request)
    {
        ushort value = ModbusPduCodec.ReadUInt16(request.Data.Span, 0);
        CommunicationResult result = DataStore.SetRegisters(
            ModbusDataArea.HoldingRegisters,
            request.Address,
            [value]);
        return result.IsSuccess
            ? WriteEcho(request, value)
            : Exception(request, ModbusExceptionCode.IllegalDataAddress);
    }

    private ModbusResponse WriteMultipleCoils(ModbusRequest request)
    {
        bool[] values = new bool[request.Quantity];
        for (int index = 0; index < request.Quantity; index++)
        {
            values[index] = (request.Data.Span[index / 8] & (1 << (index % 8))) != 0;
        }

        CommunicationResult result = DataStore.SetBits(ModbusDataArea.Coils, request.Address, values);
        return result.IsSuccess
            ? WriteEcho(request, request.Quantity)
            : Exception(request, ModbusExceptionCode.IllegalDataAddress);
    }

    private ModbusResponse WriteMultipleRegisters(ModbusRequest request)
    {
        ushort[] values = new ushort[request.Quantity];
        for (int index = 0; index < request.Quantity; index++)
        {
            values[index] = ModbusPduCodec.ReadUInt16(request.Data.Span, index * 2);
        }

        CommunicationResult result = DataStore.SetRegisters(
            ModbusDataArea.HoldingRegisters,
            request.Address,
            values);
        return result.IsSuccess
            ? WriteEcho(request, request.Quantity)
            : Exception(request, ModbusExceptionCode.IllegalDataAddress);
    }

    private static ModbusResponse WriteEcho(ModbusRequest request, ushort value)
    {
        byte[] echo = new byte[4];
        ModbusPduCodec.WriteUInt16(echo, 0, request.Address);
        ModbusPduCodec.WriteUInt16(echo, 2, value);
        return Response(request, echo);
    }

    private static ModbusResponse Response(ModbusRequest request, ReadOnlyMemory<byte> data) => new(
        request.TransactionId,
        request.UnitId,
        request.FunctionCode,
        data);

    private static ModbusResponse Exception(ModbusRequest request, ModbusExceptionCode exception) => new(
        request.TransactionId,
        request.UnitId,
        request.FunctionCode,
        ReadOnlyMemory<byte>.Empty,
        exception);
}
