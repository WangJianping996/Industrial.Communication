using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Models;

namespace Communication.Protocols.Modbus.Simulator;

/// <summary>Provides thread-safe zero-based storage for all four Modbus data tables.</summary>
public sealed class ModbusDataStore
{
    private readonly object _syncRoot = new();
    private readonly bool[] _coils = new bool[65_536];
    private readonly bool[] _discreteInputs = new bool[65_536];
    private readonly ushort[] _holdingRegisters = new ushort[65_536];
    private readonly ushort[] _inputRegisters = new ushort[65_536];

    /// <summary>Configures contiguous bit values in coils or discrete inputs.</summary>
    public CommunicationResult SetBits(ModbusDataArea area, ushort address, IReadOnlyList<bool> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        bool[]? table = area switch
        {
            ModbusDataArea.Coils => _coils,
            ModbusDataArea.DiscreteInputs => _discreteInputs,
            _ => null,
        };
        if (table is null)
        {
            return InvalidArea("The selected Modbus area does not contain bits.");
        }

        if (!IsValidRange(address, values.Count))
        {
            return InvalidAddress();
        }

        lock (_syncRoot)
        {
            for (int index = 0; index < values.Count; index++)
            {
                table[address + index] = values[index];
            }
        }

        return CommunicationResult.Success();
    }

    /// <summary>Configures contiguous register values in holding or input registers.</summary>
    public CommunicationResult SetRegisters(ModbusDataArea area, ushort address, IReadOnlyList<ushort> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        ushort[]? table = area switch
        {
            ModbusDataArea.HoldingRegisters => _holdingRegisters,
            ModbusDataArea.InputRegisters => _inputRegisters,
            _ => null,
        };
        if (table is null)
        {
            return InvalidArea("The selected Modbus area does not contain registers.");
        }

        if (!IsValidRange(address, values.Count))
        {
            return InvalidAddress();
        }

        lock (_syncRoot)
        {
            for (int index = 0; index < values.Count; index++)
            {
                table[address + index] = values[index];
            }
        }

        return CommunicationResult.Success();
    }

    /// <summary>Reads a stable copy of contiguous bit values.</summary>
    public CommunicationResult<IReadOnlyList<bool>> ReadBits(ModbusDataArea area, ushort address, ushort quantity)
    {
        bool[]? table = area switch
        {
            ModbusDataArea.Coils => _coils,
            ModbusDataArea.DiscreteInputs => _discreteInputs,
            _ => null,
        };
        if (table is null)
        {
            return CommunicationResult<IReadOnlyList<bool>>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidAddress,
                "The selected Modbus area does not contain bits."));
        }

        if (!IsValidRange(address, quantity))
        {
            return CommunicationResult<IReadOnlyList<bool>>.Failure(InvalidAddressError());
        }

        bool[] values = new bool[quantity];
        lock (_syncRoot)
        {
            Array.Copy(table, address, values, 0, quantity);
        }

        return CommunicationResult<IReadOnlyList<bool>>.Success(values);
    }

    /// <summary>Reads a stable copy of contiguous register values.</summary>
    public CommunicationResult<IReadOnlyList<ushort>> ReadRegisters(
        ModbusDataArea area,
        ushort address,
        ushort quantity)
    {
        ushort[]? table = area switch
        {
            ModbusDataArea.HoldingRegisters => _holdingRegisters,
            ModbusDataArea.InputRegisters => _inputRegisters,
            _ => null,
        };
        if (table is null)
        {
            return CommunicationResult<IReadOnlyList<ushort>>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidAddress,
                "The selected Modbus area does not contain registers."));
        }

        if (!IsValidRange(address, quantity))
        {
            return CommunicationResult<IReadOnlyList<ushort>>.Failure(InvalidAddressError());
        }

        ushort[] values = new ushort[quantity];
        lock (_syncRoot)
        {
            Array.Copy(table, address, values, 0, quantity);
        }

        return CommunicationResult<IReadOnlyList<ushort>>.Success(values);
    }

    private static bool IsValidRange(ushort address, int quantity) =>
        quantity > 0 && (long)address + quantity <= 65_536;

    private static CommunicationResult InvalidArea(string message) =>
        CommunicationResult.Failure(new CommunicationError(CommunicationErrorCode.InvalidAddress, message));

    private static CommunicationResult InvalidAddress() => CommunicationResult.Failure(InvalidAddressError());

    private static CommunicationError InvalidAddressError() => new(
        CommunicationErrorCode.InvalidAddress,
        "The Modbus zero-based data-store range is invalid.");
}
