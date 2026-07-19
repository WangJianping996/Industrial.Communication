using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Reliability;
using Communication.Protocols.Mc.Codecs;
using Communication.Protocols.Mc.Models;

namespace Communication.Protocols.Mc;

/// <summary>Executes MC 3E Binary batch operations over a replaceable transport channel.</summary>
public sealed class Mc3ETcpDataAccess : IMcDataAccess
{
    private readonly ICommunicationClient<McRequest, McResponse> _client;

    /// <summary>Initializes MC 3E Binary data access.</summary>
    public Mc3ETcpDataAccess(
        ITransportChannel channel,
        McClientOptions? options = null,
        CommunicationClientOptions? clientOptions = null)
    {
        if (channel is null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        _client = new ReliableCommunicationClient<McRequest, McResponse>(
            channel,
            new Mc3ECodec(options),
            new SingleRequestCorrelator<McRequest, McResponse>(),
            clientOptions ?? new CommunicationClientOptions { ProtocolName = "MC 3E Binary" });
    }

    /// <inheritdoc />
    public ConnectionState State => _client.State;

    /// <inheritdoc />
    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default) =>
        _client.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default) =>
        _client.DisconnectAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<ReadOnlyMemory<byte>>> ReadAsync(
        McAddress address,
        ushort points,
        CancellationToken cancellationToken = default)
    {
        CommunicationResult<McResponse> result = await _client.ExecuteAsync(
            new McRequest(address, points, false, ReadOnlyMemory<byte>.Empty),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Failure(result.Error!);
        }

        McResponse response = result.Value!;
        if (!response.IsSuccess)
        {
            return DeviceFailure<ReadOnlyMemory<byte>>(response.EndCode);
        }

        int expected = address.IsBitDevice ? (points + 1) / 2 : points * 2;
        if (response.Data.Length != expected)
        {
            return ProtocolFailure<ReadOnlyMemory<byte>>(
                $"The MC response contains {response.Data.Length} bytes; {expected} were expected.");
        }

        byte[] normalized = address.IsBitDevice
            ? UnpackBits(response.Data.Span, points)
            : SwapWordBytes(response.Data.Span);
        return CommunicationResult<ReadOnlyMemory<byte>>.Success(normalized);
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> WriteAsync(
        McAddress address,
        ushort points,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        int expected = address.IsBitDevice ? points : points * 2;
        if (data.Length != expected)
        {
            return CommunicationResult.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                $"The normalized MC write contains {data.Length} bytes; {expected} were expected."));
        }

        byte[] wireData = address.IsBitDevice ? PackBits(data.Span) : SwapWordBytes(data.Span);
        CommunicationResult<McResponse> result = await _client.ExecuteAsync(
            new McRequest(address, points, true, wireData),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return CommunicationResult.Failure(result.Error!);
        }

        return result.Value!.IsSuccess
            ? CommunicationResult.Success()
            : CommunicationResult.Failure(DeviceError(result.Value.EndCode));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();

    internal static byte[] PackBits(ReadOnlySpan<byte> bits)
    {
        byte[] packed = new byte[(bits.Length + 1) / 2];
        for (int index = 0; index < bits.Length; index++)
        {
            if (bits[index] == 0)
            {
                continue;
            }

            packed[index / 2] |= index % 2 == 0 ? (byte)0x10 : (byte)0x01;
        }

        return packed;
    }

    internal static byte[] UnpackBits(ReadOnlySpan<byte> packed, int points)
    {
        byte[] bits = new byte[points];
        for (int index = 0; index < points; index++)
        {
            int value = index % 2 == 0 ? packed[index / 2] >> 4 : packed[index / 2];
            bits[index] = (byte)((value & 0x01) != 0 ? 1 : 0);
        }

        return bits;
    }

    internal static byte[] SwapWordBytes(ReadOnlySpan<byte> data)
    {
        byte[] swapped = data.ToArray();
        for (int index = 0; index < swapped.Length; index += 2)
        {
            (swapped[index], swapped[index + 1]) = (swapped[index + 1], swapped[index]);
        }

        return swapped;
    }

    private static CommunicationResult<T> DeviceFailure<T>(ushort endCode) =>
        CommunicationResult<T>.Failure(DeviceError(endCode));

    private static CommunicationError DeviceError(ushort endCode) => new(
        CommunicationErrorCode.DeviceError,
        $"The MELSEC device returned MC end code 0x{endCode:X4}.",
        $"0x{endCode:X4}");

    private static CommunicationResult<T> ProtocolFailure<T>(string message) =>
        CommunicationResult<T>.Failure(new CommunicationError(CommunicationErrorCode.ProtocolError, message));
}
