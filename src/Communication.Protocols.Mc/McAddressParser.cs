using System.Globalization;
using System.Text.RegularExpressions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Mc.Models;

namespace Communication.Protocols.Mc;

/// <summary>Parses X/Y/M/D/W MC device addresses.</summary>
public sealed class McAddressParser : IAddressParser
{
    private static readonly Regex Pattern = new(
        @"^(?<device>X|Y|M|D|W)(?<number>[0-9A-F]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public CommunicationResult<PlcAddress> Parse(string address)
    {
        CommunicationResult<McAddress> parsed = ParseMc(address);
        return parsed.IsSuccess
            ? CommunicationResult<PlcAddress>.Success(new PlcAddress(
                parsed.Value!.DeviceCode.ToString(),
                parsed.Value.DeviceNumber,
                null,
                parsed.Value.Original))
            : CommunicationResult<PlcAddress>.Failure(parsed.Error!);
    }

    /// <summary>Parses one strongly typed MC address.</summary>
    public CommunicationResult<McAddress> ParseMc(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return Invalid("An MC device address is required.");
        }

        string normalized = address.Trim().ToUpperInvariant();
        Match match = Pattern.Match(normalized);
        if (!match.Success || !Enum.TryParse(match.Groups["device"].Value, out McDeviceCode code))
        {
            return Invalid("Use X/Y/M/D/W followed by a device number, for example X10, M100, D200, or W1A.");
        }

        int numberBase = code is McDeviceCode.X or McDeviceCode.Y or McDeviceCode.W ? 16 : 10;
        if (!int.TryParse(
                match.Groups["number"].Value,
                numberBase == 16 ? NumberStyles.HexNumber : NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int number) ||
            number is < 0 or > 0xFF_FFFF)
        {
            return Invalid("The MC device number is outside the 24-bit 3E frame range.");
        }

        return CommunicationResult<McAddress>.Success(new McAddress(code, number, normalized));
    }

    private static CommunicationResult<McAddress> Invalid(string message) =>
        CommunicationResult<McAddress>.Failure(new CommunicationError(CommunicationErrorCode.InvalidAddress, message));
}
