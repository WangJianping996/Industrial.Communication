using System.Globalization;
using System.Text.RegularExpressions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Models;

namespace Communication.Protocols.Modbus.Client;

/// <summary>Parses zero-based C/DI/HR/IR Modbus variable addresses.</summary>
public sealed class ModbusPlcAddressParser : IAddressParser
{
    private static readonly Regex Pattern = new(
        @"^(?<area>C|DI|HR|IR)(?<offset>[0-9]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public CommunicationResult<PlcAddress> Parse(string address)
    {
        CommunicationResult<ModbusPlcAddress> parsed = ParseModbus(address);
        return parsed.IsSuccess
            ? CommunicationResult<PlcAddress>.Success(new PlcAddress(
                parsed.Value!.Area.ToString(), parsed.Value.Offset, null, parsed.Value.Original))
            : CommunicationResult<PlcAddress>.Failure(parsed.Error!);
    }

    /// <summary>Parses a strongly typed Modbus address.</summary>
    public CommunicationResult<ModbusPlcAddress> ParseModbus(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return Invalid("A Modbus variable address is required.");
        }

        string normalized = address.Trim().ToUpperInvariant();
        Match match = Pattern.Match(normalized);
        if (!match.Success || !ushort.TryParse(
                match.Groups["offset"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ushort offset))
        {
            return Invalid("Use a zero-based C, DI, HR, or IR address, for example C0 or HR100.");
        }

        ModbusDataArea area = match.Groups["area"].Value switch
        {
            "C" => ModbusDataArea.Coils,
            "DI" => ModbusDataArea.DiscreteInputs,
            "HR" => ModbusDataArea.HoldingRegisters,
            "IR" => ModbusDataArea.InputRegisters,
            _ => throw new InvalidOperationException(),
        };
        return CommunicationResult<ModbusPlcAddress>.Success(new ModbusPlcAddress(area, offset, normalized));
    }

    private static CommunicationResult<ModbusPlcAddress> Invalid(string message) =>
        CommunicationResult<ModbusPlcAddress>.Failure(new CommunicationError(
            CommunicationErrorCode.InvalidAddress,
            message));
}
