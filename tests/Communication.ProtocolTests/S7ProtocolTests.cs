using Communication.Abstractions.Models;
using Communication.Protocols.S7;
using Communication.Protocols.S7.Codecs;
using Communication.Protocols.S7.Models;
using Communication.Protocols.S7.Simulator;

namespace Communication.ProtocolTests;

public sealed class S7ProtocolTests
{
    [Theory]
    [InlineData("DB1.DBX10.3", S7MemoryArea.DataBlock, 1, 10, 3)]
    [InlineData("DB20.DBW4", S7MemoryArea.DataBlock, 20, 4, null)]
    [InlineData("I0.7", S7MemoryArea.Inputs, 0, 0, 7)]
    [InlineData("QD12", S7MemoryArea.Outputs, 0, 12, null)]
    [InlineData("MB100", S7MemoryArea.Markers, 0, 100, null)]
    public void Parses_supported_absolute_addresses(
        string text,
        S7MemoryArea area,
        ushort db,
        int offset,
        int? bit)
    {
        CommunicationResult<S7Address> result = new S7AddressParser().ParseS7(text);

        Assert.True(result.IsSuccess);
        Assert.Equal(area, result.Value!.Area);
        Assert.Equal(db, result.Value.DbNumber);
        Assert.Equal(offset, result.Value.ByteOffset);
        Assert.Equal(bit, result.Value.BitOffset);
    }

    [Fact]
    public void Read_request_matches_known_iso_on_tcp_frame()
    {
        var address = new S7Address(S7MemoryArea.DataBlock, 1, 0, null, "DB1.DBB0");

        ReadOnlyMemory<byte> frame = S7IsoOnTcpCodec.EncodeReadRequest(address, 4, 1);

        Assert.Equal(
            "0300001F02F080320100000001000E00000401120A10020004000184000000",
            Convert.ToHexString(frame.Span));
    }

    [Fact]
    public void Read_response_extracts_payload_and_rejects_item_error()
    {
        byte[] success = Convert.FromHexString(
            "0300001B02F0803203000000010002000600000401FF0400101234");
        byte[] failure = success.ToArray();
        failure[21] = 0x05;

        CommunicationResult<ReadOnlyMemory<byte>> decoded =
            S7IsoOnTcpCodec.DecodeReadResponse(success, 2);
        CommunicationResult<ReadOnlyMemory<byte>> rejected =
            S7IsoOnTcpCodec.DecodeReadResponse(failure, 2);

        Assert.Equal("1234", Convert.ToHexString(decoded.Value!.Span));
        Assert.False(rejected.IsSuccess);
        Assert.Equal(CommunicationErrorCode.DeviceError, rejected.Error!.Code);
    }

    [Fact]
    public async Task Unified_client_round_trips_bit_integer_real_and_string()
    {
        await using var memory = new S7MemoryDataAccess();
        await using var client = new S7PlcClient(memory);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        VariableDefinition[] definitions =
        [
            new("Ready", "DB1.DBX0.3", PlcDataType.Boolean),
            new("Count", "DB1.DBW2", PlcDataType.Int16),
            new("Speed", "DB1.DBD4", PlcDataType.Float32),
            new("Label", "DB1.DBB8", PlcDataType.String, Length: 8),
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
        await using var memory = new S7MemoryDataAccess();
        await using var client = new S7PlcClient(memory);
        await client.ConnectAsync();
        VariableDefinition[] variables =
        [
            new("GoodA", "MW0", PlcDataType.Int16),
            new("Bad", "DBX", PlcDataType.Int16),
            new("GoodB", "MW2", PlcDataType.Int16),
        ];

        IReadOnlyList<CommunicationResult<VariableValue>> results = await client.ReadAsync(variables);

        Assert.True(results[0].IsSuccess);
        Assert.False(results[1].IsSuccess);
        Assert.True(results[2].IsSuccess);
    }

    [Fact]
    public async Task Iso_on_tcp_data_access_completes_handshake_and_fragmented_read()
    {
        byte[] cotp = Convert.FromHexString("0300000702D000");
        byte[] setup = Convert.FromHexString(
            "0300001B02F080320300000001000800000000F0000001000101E0");
        byte[] read = Convert.FromHexString(
            "0300001B02F0803203000000020002000600000401FF0400101234");
        await using var channel = new ScriptedTransportChannel(request =>
        {
            byte[] response = request.Span[5] == 0xE0
                ? cotp
                : request.Span[17] == 0xF0 ? setup : read;
            int split = response.Length / 2;
            return new ReadOnlyMemory<byte>[]
            {
                response.AsMemory(0, split),
                response.AsMemory(split),
            };
        });
        await using var access = new S7IsoTcpDataAccess(channel);

        CommunicationResult connected = await access.ConnectAsync();
        CommunicationResult<ReadOnlyMemory<byte>> result = await access.ReadBytesAsync(
            new S7Address(S7MemoryArea.DataBlock, 1, 0, null, "DB1.DBB0"), 2);

        Assert.True(connected.IsSuccess, connected.Error?.Message);
        Assert.Equal("1234", Convert.ToHexString(result.Value!.Span));
    }
}
