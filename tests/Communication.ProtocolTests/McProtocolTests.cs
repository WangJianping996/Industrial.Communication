using System.Buffers;
using Communication.Abstractions.Models;
using Communication.Protocols.Mc;
using Communication.Protocols.Mc.Codecs;
using Communication.Protocols.Mc.Models;
using Communication.Protocols.Mc.Simulator;

namespace Communication.ProtocolTests;

public sealed class McProtocolTests
{
    [Theory]
    [InlineData("X10", McDeviceCode.X, 0x10)]
    [InlineData("Y1A", McDeviceCode.Y, 0x1A)]
    [InlineData("M100", McDeviceCode.M, 100)]
    [InlineData("D200", McDeviceCode.D, 200)]
    [InlineData("W20", McDeviceCode.W, 0x20)]
    public void Parses_device_specific_number_radix(string text, McDeviceCode code, int number)
    {
        CommunicationResult<McAddress> result = new McAddressParser().ParseMc(text);

        Assert.True(result.IsSuccess);
        Assert.Equal(code, result.Value!.DeviceCode);
        Assert.Equal(number, result.Value.DeviceNumber);
    }

    [Fact]
    public void Batch_read_matches_melsec_3e_binary_golden_frame()
    {
        var request = new McRequest(
            new McAddress(McDeviceCode.D, 100, "D100"),
            2,
            false,
            ReadOnlyMemory<byte>.Empty);

        ReadOnlyMemory<byte> frame = Mc3ECodec.EncodeRequest(request);

        Assert.Equal(
            "500000FFFF03000C00100001040000640000A80200",
            Convert.ToHexString(frame.Span));
    }

    [Fact]
    public void Response_decoder_supports_fragmented_and_sticky_frames()
    {
        byte[] first = Mc3ECodec.EncodeResponse(new McResponse(0, new byte[] { 0x34, 0x12 })).ToArray();
        byte[] second = Mc3ECodec.EncodeResponse(
            new McResponse(0xC051, ReadOnlyMemory<byte>.Empty)).ToArray();
        byte[] sticky = [.. first, .. second];

        var partial = Mc3ECodec.TryDecodeResponse(new ReadOnlySequence<byte>(first.AsMemory(0, 8)));
        var decodedFirst = Mc3ECodec.TryDecodeResponse(new ReadOnlySequence<byte>(sticky));
        var decodedSecond = Mc3ECodec.TryDecodeResponse(
            new ReadOnlySequence<byte>(sticky.AsMemory((int)decodedFirst.Consumed)));

        Assert.Equal(DecodeStatus.NeedMoreData, partial.Status);
        Assert.Equal("3412", Convert.ToHexString(decodedFirst.Value!.Data.Span));
        Assert.Equal((ushort)0xC051, decodedSecond.Value!.EndCode);
    }

    [Fact]
    public async Task Unified_client_round_trips_bit_word_real_and_string()
    {
        await using var memory = new McMemoryDataAccess();
        await using var client = new McPlcClient(memory);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        VariableDefinition[] definitions =
        [
            new("Ready", "M100", PlcDataType.Boolean),
            new("Count", "D100", PlcDataType.Int16),
            new("Speed", "D101", PlcDataType.Float32),
            new("Label", "D103", PlcDataType.String, Length: 8),
        ];
        object[] values = [true, (short)1234, 12.5f, "Line-A"];
        for (int index = 0; index < definitions.Length; index++)
        {
            Assert.True((await client.WriteAsync(new PlcWriteRequest(definitions[index], values[index]))).IsSuccess);
        }

        IReadOnlyList<CommunicationResult<VariableValue>> read = await client.ReadAsync(definitions);

        Assert.All(read, item => Assert.True(item.IsSuccess, item.Error?.Message));
        Assert.True(Assert.IsType<bool>(read[0].Value!.Value));
        Assert.Equal((short)1234, read[1].Value!.Value);
        Assert.Equal(12.5f, read[2].Value!.Value);
        Assert.Equal("Line-A", read[3].Value!.Value);
    }

    [Fact]
    public async Task Batch_read_keeps_invalid_variable_failure_isolated()
    {
        await using var memory = new McMemoryDataAccess();
        await using var client = new McPlcClient(memory);
        await client.ConnectAsync();
        VariableDefinition[] variables =
        [
            new("GoodA", "D0", PlcDataType.Int16),
            new("Bad", "M0", PlcDataType.Int16),
            new("GoodB", "D1", PlcDataType.Int16),
        ];

        IReadOnlyList<CommunicationResult<VariableValue>> results = await client.ReadAsync(variables);

        Assert.True(results[0].IsSuccess);
        Assert.False(results[1].IsSuccess);
        Assert.True(results[2].IsSuccess);
    }

    [Fact]
    public async Task Tcp_data_access_decodes_fragmented_wire_response_and_normalizes_word_order()
    {
        await using var channel = new ScriptedTransportChannel(_ =>
        {
            byte[] response = Mc3ECodec.EncodeResponse(
                new McResponse(0, new byte[] { 0x34, 0x12 })).ToArray();
            return new ReadOnlyMemory<byte>[]
            {
                response.AsMemory(0, 5),
                response.AsMemory(5),
            };
        });
        await using var access = new Mc3ETcpDataAccess(channel);
        await access.ConnectAsync();

        CommunicationResult<ReadOnlyMemory<byte>> result = await access.ReadAsync(
            new McAddress(McDeviceCode.D, 100, "D100"), 1);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("1234", Convert.ToHexString(result.Value!.Span));
    }
}
