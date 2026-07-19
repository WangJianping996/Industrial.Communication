using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Codecs;
using Communication.Protocols.Modbus.Models;
using Communication.Protocols.Modbus.Simulator;

namespace Communication.ProtocolTests;

public sealed class ModbusCodecTests
{
    [Fact]
    public void Rtu_timing_uses_character_time_at_low_baud_and_fixed_time_at_high_baud()
    {
        TimeSpan at9600 = ModbusRtuTiming.GetInterFrameDelay(9600);
        TimeSpan at115200 = ModbusRtuTiming.GetInterFrameDelay(115200);

        Assert.InRange(at9600.TotalMilliseconds, 3.64, 3.65);
        Assert.Equal(1.75, at115200.TotalMilliseconds, 3);
    }

    [Fact]
    public void Tcp_read_holding_registers_matches_mbap_golden_frame()
    {
        ModbusRequest request = ModbusRequest.Read(
            0x11,
            ModbusFunctionCode.ReadHoldingRegisters,
            0x006B,
            3) with { TransactionId = 1 };

        byte[] frame = ModbusTcpFrameCodec.EncodeRequest(request).ToArray();

        Assert.Equal(
            new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x11, 0x03, 0x00, 0x6B, 0x00, 0x03 },
            frame);
    }

    [Fact]
    public void Rtu_read_holding_registers_matches_crc_golden_frame()
    {
        ModbusRequest request = ModbusRequest.Read(
            1,
            ModbusFunctionCode.ReadHoldingRegisters,
            0,
            10);

        byte[] frame = ModbusRtuFrameCodec.EncodeRequest(request).ToArray();

        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD }, frame);
    }

    [Fact]
    public void Tcp_decoder_handles_fragmented_and_sticky_frames()
    {
        byte[] response =
        [
            0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x11,
            0x03, 0x06, 0xAE, 0x41, 0x56, 0x52, 0x43, 0x40,
        ];
        ProtocolDecodeResult<ModbusResponse> partial = ModbusTcpFrameCodec.TryDecodeResponse(
            new ReadOnlySequence<byte>(response.AsMemory(0, 8)));
        byte[] sticky = response.Concat(response).ToArray();
        ProtocolDecodeResult<ModbusResponse> complete = ModbusTcpFrameCodec.TryDecodeResponse(
            new ReadOnlySequence<byte>(sticky));

        Assert.Equal(DecodeStatus.NeedMoreData, partial.Status);
        Assert.Equal(DecodeStatus.Done, complete.Status);
        Assert.Equal(response.Length, complete.Consumed);
        Assert.Equal(new byte[] { 0xAE, 0x41, 0x56, 0x52, 0x43, 0x40 }, complete.Value!.Data.ToArray());
    }

    [Fact]
    public void Exception_response_preserves_function_and_exception_code()
    {
        byte[] frame = { 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x11, 0x83, 0x02 };

        ProtocolDecodeResult<ModbusResponse> result = ModbusTcpFrameCodec.TryDecodeResponse(
            new ReadOnlySequence<byte>(frame));

        Assert.Equal(DecodeStatus.Done, result.Status);
        Assert.Equal(ModbusFunctionCode.ReadHoldingRegisters, result.Value!.FunctionCode);
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, result.Value.ExceptionCode);
    }

    [Fact]
    public void Rtu_decoder_rejects_a_corrupt_crc()
    {
        ModbusResponse response = new(
            0,
            1,
            ModbusFunctionCode.ReadHoldingRegisters,
            new byte[] { 0x00, 0x2A });
        byte[] frame = ModbusRtuFrameCodec.EncodeResponse(response).ToArray();
        frame[frame.Length - 1] ^= 0xFF;

        ProtocolDecodeResult<ModbusResponse> result = ModbusRtuFrameCodec.TryDecodeResponse(
            new ReadOnlySequence<byte>(frame));

        Assert.Equal(DecodeStatus.InvalidData, result.Status);
        Assert.Equal(CommunicationErrorCode.ChecksumFailure, result.Error!.Code);
    }

    [Theory]
    [InlineData(ModbusFunctionCode.ReadCoils, 2001)]
    [InlineData(ModbusFunctionCode.ReadDiscreteInputs, 2001)]
    [InlineData(ModbusFunctionCode.ReadHoldingRegisters, 126)]
    [InlineData(ModbusFunctionCode.ReadInputRegisters, 126)]
    public void Read_quantity_above_the_protocol_limit_is_rejected(
        ModbusFunctionCode function,
        ushort quantity)
    {
        CommunicationResult<ReadOnlyMemory<byte>> result = ModbusPduCodec.EncodeRequest(
            ModbusRequest.Read(1, function, 0, quantity));

        Assert.False(result.IsSuccess);
        Assert.Equal(CommunicationErrorCode.InvalidAddress, result.Error!.Code);
    }

    [Fact]
    public void Address_range_must_fit_the_zero_based_16_bit_protocol_space()
    {
        CommunicationResult<ReadOnlyMemory<byte>> valid = ModbusPduCodec.EncodeRequest(
            ModbusRequest.Read(1, ModbusFunctionCode.ReadHoldingRegisters, 65_535, 1));
        CommunicationResult<ReadOnlyMemory<byte>> invalid = ModbusPduCodec.EncodeRequest(
            ModbusRequest.Read(1, ModbusFunctionCode.ReadHoldingRegisters, 65_535, 2));

        Assert.True(valid.IsSuccess);
        Assert.False(invalid.IsSuccess);
        Assert.Equal(CommunicationErrorCode.InvalidAddress, invalid.Error!.Code);
    }

    [Fact]
    public void Slave_executes_all_supported_function_codes_against_shared_tables()
    {
        ModbusDataStore store = new();
        Assert.True(store.SetBits(ModbusDataArea.DiscreteInputs, 10, [true, false, true]).IsSuccess);
        Assert.True(store.SetRegisters(ModbusDataArea.InputRegisters, 20, [11, 12]).IsSuccess);
        ModbusSlave slave = new(1, store);

        Assert.False(slave.ProcessRequest(ModbusRequest.Read(1, ModbusFunctionCode.ReadCoils, 0, 1)).IsException);
        Assert.Equal(0x05, slave.ProcessRequest(
            ModbusRequest.Read(1, ModbusFunctionCode.ReadDiscreteInputs, 10, 3)).Data.Span[0]);
        Assert.False(slave.ProcessRequest(ModbusRequest.Read(1, ModbusFunctionCode.ReadHoldingRegisters, 0, 1)).IsException);
        Assert.Equal(new byte[] { 0x00, 0x0B, 0x00, 0x0C }, slave.ProcessRequest(
            ModbusRequest.Read(1, ModbusFunctionCode.ReadInputRegisters, 20, 2)).Data.ToArray());
        Assert.False(slave.ProcessRequest(ModbusRequest.WriteCoil(1, 30, true)).IsException);
        Assert.False(slave.ProcessRequest(ModbusRequest.WriteRegister(1, 31, 1234)).IsException);
        Assert.False(slave.ProcessRequest(ModbusRequest.WriteCoils(1, 32, [true, true, false])).IsException);
        Assert.False(slave.ProcessRequest(ModbusRequest.WriteRegisters(1, 40, [100, 200])).IsException);

        Assert.Equal([true], store.ReadBits(ModbusDataArea.Coils, 30, 1).Value);
        Assert.Equal([true, true, false], store.ReadBits(ModbusDataArea.Coils, 32, 3).Value);
        Assert.Equal([1234], store.ReadRegisters(ModbusDataArea.HoldingRegisters, 31, 1).Value);
        Assert.Equal([100, 200], store.ReadRegisters(ModbusDataArea.HoldingRegisters, 40, 2).Value);
    }
}
