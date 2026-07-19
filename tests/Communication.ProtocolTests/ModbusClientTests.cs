using Communication.Abstractions.Models;
using Communication.Core.Simulation;
using Communication.Protocols.Modbus.Client;
using Communication.Protocols.Modbus.Models;
using Communication.Protocols.Modbus.Simulator;

namespace Communication.ProtocolTests;

public sealed class ModbusClientTests
{
    [Fact]
    public async Task Simulator_failure_injection_returns_structured_timeout_and_disconnect_state()
    {
        await using var droppedChannel = new ModbusSimulatorChannel(
            ModbusTransportMode.Tcp,
            options: new ModbusSimulatorOptions { DropResponses = true });
        await using var droppedClient = new ModbusClient(
            droppedChannel,
            ModbusTransportMode.Tcp,
            new ModbusClientOptions { DefaultTimeout = TimeSpan.FromMilliseconds(40) });
        await droppedClient.ConnectAsync();

        CommunicationResult<IReadOnlyList<ushort>> timeout =
            await droppedClient.ReadHoldingRegistersAsync(1, 0, 1);
        Assert.False(timeout.IsSuccess);
        Assert.Equal(CommunicationErrorCode.Timeout, timeout.Error!.Code);

        await using var disconnectedChannel = new ModbusSimulatorChannel(
            ModbusTransportMode.Tcp,
            options: new ModbusSimulatorOptions { DisconnectOnRequest = true });
        await using var disconnectedClient = new ModbusClient(
            disconnectedChannel,
            ModbusTransportMode.Tcp,
            new ModbusClientOptions { DefaultTimeout = TimeSpan.FromMilliseconds(40) });
        await disconnectedClient.ConnectAsync();
        await disconnectedClient.ReadHoldingRegistersAsync(1, 0, 1);

        Assert.Equal(ConnectionState.Faulted, disconnectedChannel.State);
    }

    [Fact]
    public async Task Unified_plc_adapter_round_trips_variables_and_isolates_invalid_mapping()
    {
        ModbusDataStore store = new();
        await using ModbusSimulatorChannel channel = new(
            ModbusTransportMode.Tcp,
            new ModbusSlave(1, store));
        var rawClient = new ModbusClient(channel, ModbusTransportMode.Tcp);
        await using var client = new ModbusPlcClient(rawClient);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        VariableDefinition ready = new("Ready", "C0", PlcDataType.Boolean);
        VariableDefinition count = new("Count", "HR10", PlcDataType.Int16);
        Assert.True((await client.WriteAsync(new PlcWriteRequest(ready, true))).IsSuccess);
        Assert.True((await client.WriteAsync(new PlcWriteRequest(count, (short)1234))).IsSuccess);

        IReadOnlyList<CommunicationResult<VariableValue>> results = await client.ReadAsync(
        [
            ready,
            new VariableDefinition("Invalid", "DI0", PlcDataType.Int16),
            count,
        ]);

        Assert.True(Assert.IsType<bool>(results[0].Value!.Value));
        Assert.False(results[1].IsSuccess);
        Assert.Equal((short)1234, results[2].Value!.Value);
    }

    [Theory]
    [InlineData(ModbusTransportMode.Tcp)]
    [InlineData(ModbusTransportMode.Rtu)]
    public async Task Tcp_and_rtu_clients_share_typed_read_write_behavior(ModbusTransportMode mode)
    {
        ModbusDataStore store = new();
        store.SetBits(ModbusDataArea.DiscreteInputs, 10, [true, false, true]);
        store.SetRegisters(ModbusDataArea.InputRegisters, 20, [101, 202]);
        ModbusSlave slave = new(1, store);
        await using ModbusSimulatorChannel channel = new(mode, slave);
        await using ModbusClient client = new(channel, mode);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        Assert.True((await client.WriteSingleCoilAsync(1, 0, true)).IsSuccess);
        Assert.True((await client.WriteMultipleCoilsAsync(1, 1, [true, false, true])).IsSuccess);
        Assert.Equal([true, true, false, true], (await client.ReadCoilsAsync(1, 0, 4)).Value);
        Assert.Equal([true, false, true], (await client.ReadDiscreteInputsAsync(1, 10, 3)).Value);

        Assert.True((await client.WriteSingleRegisterAsync(1, 0, 123)).IsSuccess);
        Assert.True((await client.WriteMultipleRegistersAsync(1, 1, [456, 789])).IsSuccess);
        Assert.Equal([123, 456, 789], (await client.ReadHoldingRegistersAsync(1, 0, 3)).Value);
        Assert.Equal([101, 202], (await client.ReadInputRegistersAsync(1, 20, 2)).Value);
    }

    [Fact]
    public async Task Forced_exception_is_returned_as_a_structured_device_error()
    {
        await using ModbusSimulatorChannel channel = new(
            ModbusTransportMode.Tcp,
            options: new ModbusSimulatorOptions
            {
                ForcedException = ModbusExceptionCode.IllegalDataAddress,
            });
        await using ModbusClient client = new(channel, ModbusTransportMode.Tcp);
        await client.ConnectAsync();

        CommunicationResult<IReadOnlyList<ushort>> result = await client.ReadHoldingRegistersAsync(1, 0, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommunicationErrorCode.DeviceError, result.Error!.Code);
        Assert.Equal("0x02", result.Error.Detail);
    }

    [Fact]
    public async Task Dropped_response_produces_a_timeout_without_blocking_later_requests()
    {
        DelegatingSimulationResponseScript script = new((request, _) =>
            new ValueTask<SimulationResponseDirective>(new SimulationResponseDirective
            {
                DropResponse = request.Sequence == 1,
            }));
        await using ModbusSimulatorChannel channel = new(
            ModbusTransportMode.Tcp,
            options: new ModbusSimulatorOptions { Script = script });
        await using ModbusClient client = new(channel, ModbusTransportMode.Tcp);
        await client.ConnectAsync();

        CommunicationResult<IReadOnlyList<ushort>> timeout = await client.ReadHoldingRegistersAsync(
            1,
            0,
            1,
            new CommunicationRequestOptions { Timeout = TimeSpan.FromMilliseconds(30), EnableRetry = false });
        CommunicationResult<IReadOnlyList<ushort>> next = await client.ReadHoldingRegistersAsync(1, 0, 1);

        Assert.False(timeout.IsSuccess);
        Assert.Equal(CommunicationErrorCode.Timeout, timeout.Error!.Code);
        Assert.True(next.IsSuccess);
    }

    [Fact]
    public async Task Corrupt_rtu_crc_is_reported_as_checksum_failure()
    {
        await using ModbusSimulatorChannel channel = new(
            ModbusTransportMode.Rtu,
            options: new ModbusSimulatorOptions { CorruptRtuCrc = true });
        await using ModbusClient client = new(channel, ModbusTransportMode.Rtu);
        await client.ConnectAsync();

        CommunicationResult<IReadOnlyList<ushort>> result = await client.ReadHoldingRegistersAsync(1, 0, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommunicationErrorCode.ChecksumFailure, result.Error!.Code);
    }

    [Fact]
    public async Task Rtu_station_zero_is_rejected_because_client_calls_require_a_response()
    {
        await using ModbusSimulatorChannel channel = new(ModbusTransportMode.Rtu);
        await using ModbusClient client = new(channel, ModbusTransportMode.Rtu);
        await client.ConnectAsync();

        CommunicationResult result = await client.WriteSingleRegisterAsync(0, 1, 2);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommunicationErrorCode.InvalidAddress, result.Error!.Code);
    }

    [Fact]
    public async Task Configured_response_delay_can_deterministically_trigger_timeout()
    {
        await using ModbusSimulatorChannel channel = new(
            ModbusTransportMode.Tcp,
            options: new ModbusSimulatorOptions { ResponseDelay = TimeSpan.FromMilliseconds(100) });
        await using ModbusClient client = new(channel, ModbusTransportMode.Tcp);
        await client.ConnectAsync();

        CommunicationResult<IReadOnlyList<ushort>> result = await client.ReadHoldingRegistersAsync(
            1,
            0,
            1,
            new CommunicationRequestOptions { Timeout = TimeSpan.FromMilliseconds(25), EnableRetry = false });

        Assert.False(result.IsSuccess);
        Assert.Equal(CommunicationErrorCode.Timeout, result.Error!.Code);
    }

    [Fact]
    public async Task Scripted_disconnect_moves_the_simulated_transport_to_faulted()
    {
        await using ModbusSimulatorChannel channel = new(
            ModbusTransportMode.Tcp,
            options: new ModbusSimulatorOptions { DisconnectOnRequest = true });
        await using ModbusClient client = new(channel, ModbusTransportMode.Tcp);
        await client.ConnectAsync();

        CommunicationResult<IReadOnlyList<ushort>> result = await client.ReadHoldingRegistersAsync(
            1,
            0,
            1,
            new CommunicationRequestOptions { Timeout = TimeSpan.FromMilliseconds(25), EnableRetry = false });

        Assert.False(result.IsSuccess);
        Assert.Equal(ConnectionState.Faulted, channel.State);
    }
}
