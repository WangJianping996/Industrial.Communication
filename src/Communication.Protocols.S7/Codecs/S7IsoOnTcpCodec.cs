using Communication.Abstractions.Models;
using Communication.Protocols.S7.Models;

namespace Communication.Protocols.S7.Codecs;

/// <summary>Builds and validates the ISO-on-TCP/COTP/S7comm subset used for absolute byte access.</summary>
public static class S7IsoOnTcpCodec
{
    private const int S7Start = 7;

    /// <summary>Builds a COTP connection request for one rack and slot.</summary>
    public static ReadOnlyMemory<byte> EncodeConnectionRequest(byte rack, byte slot)
    {
        if (rack > 7 || slot > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(rack), "S7 rack must be 0..7 and slot must be 0..31.");
        }

        byte remoteTsap = checked((byte)((rack * 0x20) + slot));
        return new byte[]
        {
            0x03, 0x00, 0x00, 0x16,
            0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00,
            0xC1, 0x02, 0x01, 0x00,
            0xC2, 0x02, 0x01, remoteTsap,
            0xC0, 0x01, 0x0A,
        };
    }

    /// <summary>Builds an S7 Setup Communication request.</summary>
    public static ReadOnlyMemory<byte> EncodeSetupCommunication(ushort pduReference, ushort requestedPduLength = 480)
    {
        byte[] frame = CreateJobFrame(25, pduReference, 8, 0);
        frame[17] = 0xF0;
        frame[18] = 0x00;
        WriteUInt16(frame, 19, 1);
        WriteUInt16(frame, 21, 1);
        WriteUInt16(frame, 23, requestedPduLength);
        return frame;
    }

    /// <summary>Builds an S7 Read Var request for one contiguous byte range.</summary>
    public static ReadOnlyMemory<byte> EncodeReadRequest(S7Address address, ushort byteCount, ushort pduReference)
    {
        if (byteCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        byte[] frame = CreateJobFrame(31, pduReference, 14, 0);
        frame[17] = 0x04;
        frame[18] = 0x01;
        WriteVariableSpecification(frame, 19, address, byteCount);
        return frame;
    }

    /// <summary>Builds an S7 Write Var request for one contiguous byte range.</summary>
    public static ReadOnlyMemory<byte> EncodeWriteRequest(
        S7Address address,
        ReadOnlySpan<byte> data,
        ushort pduReference)
    {
        if (data.Length is 0 or > ushort.MaxValue / 8)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        int padding = data.Length % 2;
        int dataLength = 4 + data.Length + padding;
        byte[] frame = CreateJobFrame(31 + dataLength, pduReference, 14, dataLength);
        frame[17] = 0x05;
        frame[18] = 0x01;
        WriteVariableSpecification(frame, 19, address, checked((ushort)data.Length));
        frame[31] = 0x00;
        frame[32] = 0x04;
        WriteUInt16(frame, 33, checked((ushort)(data.Length * 8)));
        data.CopyTo(frame.AsSpan(35));
        return frame;
    }

    /// <summary>Validates a COTP connection confirmation.</summary>
    public static CommunicationResult ValidateConnectionConfirmation(ReadOnlySpan<byte> frame)
    {
        CommunicationError? tpkt = ValidateTpkt(frame);
        if (tpkt is not null)
        {
            return CommunicationResult.Failure(tpkt);
        }

        return frame.Length >= 7 && frame[5] == 0xD0
            ? CommunicationResult.Success()
            : ProtocolFailure("The peer did not return a COTP connection confirmation.");
    }

    /// <summary>Validates an S7 Setup Communication acknowledgement.</summary>
    public static CommunicationResult ValidateSetupResponse(ReadOnlySpan<byte> frame)
    {
        CommunicationError? error = ValidateS7Ack(frame, 0xF0, out _, out _);
        return error is null ? CommunicationResult.Success() : CommunicationResult.Failure(error);
    }

    /// <summary>Extracts data from one S7 Read Var acknowledgement.</summary>
    public static CommunicationResult<ReadOnlyMemory<byte>> DecodeReadResponse(
        ReadOnlySpan<byte> frame,
        int expectedBytes)
    {
        CommunicationError? error = ValidateS7Ack(frame, 0x04, out int parameterStart, out int dataStart);
        if (error is not null)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Failure(error);
        }

        if (frame.Length < dataStart + 4 || frame[parameterStart + 1] != 1)
        {
            return ProtocolFailure<ReadOnlyMemory<byte>>("The S7 Read Var response is truncated or has an invalid item count.");
        }

        byte returnCode = frame[dataStart];
        if (returnCode != 0xFF)
        {
            return ItemFailure<ReadOnlyMemory<byte>>(returnCode);
        }

        int bitLength = ReadUInt16(frame, dataStart + 2);
        int byteLength = (bitLength + 7) / 8;
        if (byteLength != expectedBytes || frame.Length < dataStart + 4 + byteLength)
        {
            return ProtocolFailure<ReadOnlyMemory<byte>>(
                $"The S7 Read Var response contains {byteLength} bytes; {expectedBytes} were expected.");
        }

        return CommunicationResult<ReadOnlyMemory<byte>>.Success(
            frame.Slice(dataStart + 4, byteLength).ToArray());
    }

    /// <summary>Validates one S7 Write Var acknowledgement.</summary>
    public static CommunicationResult ValidateWriteResponse(ReadOnlySpan<byte> frame)
    {
        CommunicationError? error = ValidateS7Ack(frame, 0x05, out int parameterStart, out int dataStart);
        if (error is not null)
        {
            return CommunicationResult.Failure(error);
        }

        if (frame.Length <= dataStart || frame[parameterStart + 1] != 1)
        {
            return ProtocolFailure("The S7 Write Var response is truncated or has an invalid item count.");
        }

        return frame[dataStart] == 0xFF
            ? CommunicationResult.Success()
            : CommunicationResult.Failure(ItemError(frame[dataStart]));
    }

    private static byte[] CreateJobFrame(int length, ushort pduReference, ushort parameterLength, int dataLength)
    {
        byte[] frame = new byte[length];
        frame[0] = 0x03;
        frame[1] = 0x00;
        WriteUInt16(frame, 2, checked((ushort)length));
        frame[4] = 0x02;
        frame[5] = 0xF0;
        frame[6] = 0x80;
        frame[7] = 0x32;
        frame[8] = 0x01;
        WriteUInt16(frame, 11, pduReference);
        WriteUInt16(frame, 13, parameterLength);
        WriteUInt16(frame, 15, checked((ushort)dataLength));
        return frame;
    }

    private static void WriteVariableSpecification(
        Span<byte> frame,
        int offset,
        S7Address address,
        ushort byteCount)
    {
        if (address.ByteOffset < 0 || address.BitAddress > 0xFF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }

        frame[offset] = 0x12;
        frame[offset + 1] = 0x0A;
        frame[offset + 2] = 0x10;
        frame[offset + 3] = 0x02;
        WriteUInt16(frame, offset + 4, byteCount);
        WriteUInt16(frame, offset + 6, address.Area == S7MemoryArea.DataBlock ? address.DbNumber : (ushort)0);
        frame[offset + 8] = (byte)address.Area;
        int bitAddress = address.ByteOffset * 8;
        frame[offset + 9] = (byte)(bitAddress >> 16);
        frame[offset + 10] = (byte)(bitAddress >> 8);
        frame[offset + 11] = (byte)bitAddress;
    }

    private static CommunicationError? ValidateS7Ack(
        ReadOnlySpan<byte> frame,
        byte expectedFunction,
        out int parameterStart,
        out int dataStart)
    {
        parameterStart = 0;
        dataStart = 0;
        CommunicationError? tpkt = ValidateTpkt(frame);
        if (tpkt is not null)
        {
            return tpkt;
        }

        if (frame.Length < 19 || frame[4] != 0x02 || frame[5] != 0xF0 || frame[6] != 0x80 ||
            frame[S7Start] != 0x32 || frame[S7Start + 1] != 0x03)
        {
            return new CommunicationError(CommunicationErrorCode.ProtocolError, "The S7 acknowledgement header is invalid.");
        }

        if (frame[S7Start + 10] != 0 || frame[S7Start + 11] != 0)
        {
            return new CommunicationError(
                CommunicationErrorCode.DeviceError,
                $"The S7 CPU returned error class 0x{frame[S7Start + 10]:X2}, code 0x{frame[S7Start + 11]:X2}.");
        }

        int parameterLength = ReadUInt16(frame, S7Start + 6);
        int dataLength = ReadUInt16(frame, S7Start + 8);
        parameterStart = S7Start + 12;
        dataStart = parameterStart + parameterLength;
        if (parameterLength == 0 || frame.Length < dataStart + dataLength || frame[parameterStart] != expectedFunction)
        {
            return new CommunicationError(CommunicationErrorCode.ProtocolError, "The S7 acknowledgement payload is invalid.");
        }

        return null;
    }

    private static CommunicationError? ValidateTpkt(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4 || frame[0] != 0x03 || frame[1] != 0x00)
        {
            return new CommunicationError(CommunicationErrorCode.ProtocolError, "The ISO-on-TCP TPKT header is invalid.");
        }

        int declared = ReadUInt16(frame, 2);
        return declared == frame.Length
            ? null
            : new CommunicationError(
                CommunicationErrorCode.ProtocolError,
                $"The TPKT declares {declared} bytes but the frame contains {frame.Length}.");
    }

    private static CommunicationResult ProtocolFailure(string message) =>
        CommunicationResult.Failure(new CommunicationError(CommunicationErrorCode.ProtocolError, message));

    private static CommunicationResult<T> ProtocolFailure<T>(string message) =>
        CommunicationResult<T>.Failure(new CommunicationError(CommunicationErrorCode.ProtocolError, message));

    private static CommunicationResult<T> ItemFailure<T>(byte returnCode) =>
        CommunicationResult<T>.Failure(ItemError(returnCode));

    private static CommunicationError ItemError(byte returnCode) => new(
        CommunicationErrorCode.DeviceError,
        $"The S7 variable item returned code 0x{returnCode:X2}.",
        $"0x{returnCode:X2}");

    private static int ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        (bytes[offset] << 8) | bytes[offset + 1];

    private static void WriteUInt16(Span<byte> bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }
}
