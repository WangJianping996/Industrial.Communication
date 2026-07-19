using Communication.Abstractions.Models;

namespace Communication.Protocols.Modbus.Models;

/// <summary>Identifies the supported Modbus application functions.</summary>
public enum ModbusFunctionCode : byte
{
    /// <summary>Reads coils.</summary>
    ReadCoils = 0x01,
    /// <summary>Reads discrete inputs.</summary>
    ReadDiscreteInputs = 0x02,
    /// <summary>Reads holding registers.</summary>
    ReadHoldingRegisters = 0x03,
    /// <summary>Reads input registers.</summary>
    ReadInputRegisters = 0x04,
    /// <summary>Writes one coil.</summary>
    WriteSingleCoil = 0x05,
    /// <summary>Writes one holding register.</summary>
    WriteSingleRegister = 0x06,
    /// <summary>Writes multiple coils.</summary>
    WriteMultipleCoils = 0x0F,
    /// <summary>Writes multiple holding registers.</summary>
    WriteMultipleRegisters = 0x10,
}

/// <summary>Identifies a Modbus exception response.</summary>
public enum ModbusExceptionCode : byte
{
    /// <summary>The function is not supported.</summary>
    IllegalFunction = 0x01,
    /// <summary>The requested address range is not available.</summary>
    IllegalDataAddress = 0x02,
    /// <summary>A request field or quantity is invalid.</summary>
    IllegalDataValue = 0x03,
    /// <summary>The server failed while processing the request.</summary>
    ServerDeviceFailure = 0x04,
    /// <summary>The server accepted a long-running operation.</summary>
    Acknowledge = 0x05,
    /// <summary>The server is busy.</summary>
    ServerDeviceBusy = 0x06,
    /// <summary>A gateway path is unavailable.</summary>
    GatewayPathUnavailable = 0x0A,
    /// <summary>A gateway target did not respond.</summary>
    GatewayTargetFailedToRespond = 0x0B,
}

/// <summary>Selects the Modbus application data unit framing.</summary>
public enum ModbusTransportMode
{
    /// <summary>MBAP framing over a stream transport.</summary>
    Tcp,
    /// <summary>Station address and CRC16 framing over a serial transport.</summary>
    Rtu,
}

/// <summary>Identifies one of the four Modbus data tables.</summary>
public enum ModbusDataArea
{
    /// <summary>Read/write single-bit coils.</summary>
    Coils,
    /// <summary>Read-only single-bit discrete inputs.</summary>
    DiscreteInputs,
    /// <summary>Read/write 16-bit holding registers.</summary>
    HoldingRegisters,
    /// <summary>Read-only 16-bit input registers.</summary>
    InputRegisters,
}

/// <summary>Represents a transport-independent Modbus request PDU plus routing fields.</summary>
/// <param name="TransactionId">The TCP transaction identifier; ignored by RTU.</param>
/// <param name="UnitId">The TCP unit identifier or RTU station address.</param>
/// <param name="FunctionCode">The requested function.</param>
/// <param name="Address">The zero-based protocol address.</param>
/// <param name="Quantity">The item count. Single writes always use one.</param>
/// <param name="Data">Packed coil bytes or big-endian register/value bytes for writes.</param>
public sealed record ModbusRequest(
    ushort TransactionId,
    byte UnitId,
    ModbusFunctionCode FunctionCode,
    ushort Address,
    ushort Quantity,
    ReadOnlyMemory<byte> Data)
{
    /// <summary>Creates a read request using a zero-based protocol address.</summary>
    public static ModbusRequest Read(
        byte unitId,
        ModbusFunctionCode functionCode,
        ushort address,
        ushort quantity) => new(0, unitId, functionCode, address, quantity, ReadOnlyMemory<byte>.Empty);

    /// <summary>Creates a single-coil write request.</summary>
    public static ModbusRequest WriteCoil(byte unitId, ushort address, bool value) => new(
        0,
        unitId,
        ModbusFunctionCode.WriteSingleCoil,
        address,
        1,
        value ? new byte[] { 0xFF, 0x00 } : new byte[] { 0x00, 0x00 });

    /// <summary>Creates a single-register write request.</summary>
    public static ModbusRequest WriteRegister(byte unitId, ushort address, ushort value) => new(
        0,
        unitId,
        ModbusFunctionCode.WriteSingleRegister,
        address,
        1,
        new byte[] { (byte)(value >> 8), (byte)value });

    /// <summary>Creates a multiple-coil write request.</summary>
    public static ModbusRequest WriteCoils(byte unitId, ushort address, IReadOnlyList<bool> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        byte[] packed = new byte[(values.Count + 7) / 8];
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index])
            {
                packed[index / 8] |= (byte)(1 << (index % 8));
            }
        }

        return new ModbusRequest(
            0,
            unitId,
            ModbusFunctionCode.WriteMultipleCoils,
            address,
            checked((ushort)values.Count),
            packed);
    }

    /// <summary>Creates a multiple-register write request.</summary>
    public static ModbusRequest WriteRegisters(byte unitId, ushort address, IReadOnlyList<ushort> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        byte[] data = new byte[checked(values.Count * 2)];
        for (int index = 0; index < values.Count; index++)
        {
            data[index * 2] = (byte)(values[index] >> 8);
            data[(index * 2) + 1] = (byte)values[index];
        }

        return new ModbusRequest(
            0,
            unitId,
            ModbusFunctionCode.WriteMultipleRegisters,
            address,
            checked((ushort)values.Count),
            data);
    }
}

/// <summary>Represents a decoded Modbus response.</summary>
/// <param name="TransactionId">The TCP transaction identifier, or zero for RTU.</param>
/// <param name="UnitId">The responding unit or station.</param>
/// <param name="FunctionCode">The original function code without the exception bit.</param>
/// <param name="Data">Read data or the four-byte write echo.</param>
/// <param name="ExceptionCode">The device exception, when present.</param>
public sealed record ModbusResponse(
    ushort TransactionId,
    byte UnitId,
    ModbusFunctionCode FunctionCode,
    ReadOnlyMemory<byte> Data,
    ModbusExceptionCode? ExceptionCode = null)
{
    /// <summary>Gets whether this is a Modbus exception response.</summary>
    public bool IsException => ExceptionCode.HasValue;

    /// <summary>Converts a device exception into the common error model.</summary>
    public CommunicationError ToCommunicationError() => new(
        CommunicationErrorCode.DeviceError,
        $"Modbus unit {UnitId} returned exception {ExceptionCode}.",
        ExceptionCode.HasValue ? $"0x{(byte)ExceptionCode.Value:X2}" : null);
}

/// <summary>Defines Modbus protocol quantity and frame limits.</summary>
public static class ModbusLimits
{
    /// <summary>Maximum PDU length in bytes.</summary>
    public const int MaxPduLength = 253;
    /// <summary>Maximum number of coils or discrete inputs per read.</summary>
    public const ushort MaxReadBits = 2000;
    /// <summary>Maximum number of registers per read.</summary>
    public const ushort MaxReadRegisters = 125;
    /// <summary>Maximum number of coils per multiple write.</summary>
    public const ushort MaxWriteCoils = 1968;
    /// <summary>Maximum number of registers per multiple write.</summary>
    public const ushort MaxWriteRegisters = 123;
}
