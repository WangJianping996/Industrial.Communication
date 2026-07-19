using System.Text.RegularExpressions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.S7.Models;

namespace Communication.Protocols.S7;

/// <summary>Parses S7 DB/I/Q/M absolute addresses without performing I/O.</summary>
public sealed class S7AddressParser : IAddressParser
{
    private static readonly Regex DbPattern = new(
        @"^DB(?<db>\d+)\.(?<kind>DBX|DBB|DBW|DBD)(?<offset>\d+)(?:\.(?<bit>[0-7]))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AreaPattern = new(
        @"^(?<area>I|Q|M)(?<kind>B|W|D)?(?<offset>\d+)(?:\.(?<bit>[0-7]))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public CommunicationResult<PlcAddress> Parse(string address)
    {
        CommunicationResult<S7Address> result = ParseS7(address);
        return result.IsSuccess
            ? CommunicationResult<PlcAddress>.Success(result.Value!.ToPlcAddress())
            : CommunicationResult<PlcAddress>.Failure(result.Error!);
    }

    /// <summary>Parses one strongly typed S7 address.</summary>
    public CommunicationResult<S7Address> ParseS7(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return Invalid("An S7 address is required.");
        }

        string normalized = address.Trim();
        Match db = DbPattern.Match(normalized);
        if (db.Success)
        {
            if (!ushort.TryParse(db.Groups["db"].Value, out ushort dbNumber) || dbNumber == 0 ||
                !int.TryParse(db.Groups["offset"].Value, out int offset))
            {
                return Invalid("The S7 DB number or byte offset is outside the supported range.");
            }

            string kind = db.Groups["kind"].Value.ToUpperInvariant();
            int? bit = db.Groups["bit"].Success ? int.Parse(db.Groups["bit"].Value) : null;
            if ((kind == "DBX") != bit.HasValue)
            {
                return Invalid("DBX requires a .0 through .7 bit suffix; DBB/DBW/DBD do not accept one.");
            }

            return CommunicationResult<S7Address>.Success(new S7Address(
                S7MemoryArea.DataBlock,
                dbNumber,
                offset,
                bit,
                normalized));
        }

        Match area = AreaPattern.Match(normalized);
        if (!area.Success || !int.TryParse(area.Groups["offset"].Value, out int areaOffset))
        {
            return Invalid("Use DB1.DBX0.0, DB1.DBB0, DB1.DBW0, DB1.DBD0, I0.0, IB0, IW0, ID0, Q, or M forms.");
        }

        string areaName = area.Groups["area"].Value.ToUpperInvariant();
        string kindName = area.Groups["kind"].Value.ToUpperInvariant();
        int? areaBit = area.Groups["bit"].Success ? int.Parse(area.Groups["bit"].Value) : null;
        if (string.IsNullOrEmpty(kindName) != areaBit.HasValue)
        {
            return Invalid("Bit addresses require a .0 through .7 suffix; byte/word/dword forms do not accept one.");
        }

        S7MemoryArea memoryArea = areaName switch
        {
            "I" => S7MemoryArea.Inputs,
            "Q" => S7MemoryArea.Outputs,
            _ => S7MemoryArea.Markers,
        };
        return CommunicationResult<S7Address>.Success(new S7Address(
            memoryArea,
            0,
            areaOffset,
            areaBit,
            normalized));
    }

    private static CommunicationResult<S7Address> Invalid(string message) =>
        CommunicationResult<S7Address>.Failure(new CommunicationError(CommunicationErrorCode.InvalidAddress, message));
}
