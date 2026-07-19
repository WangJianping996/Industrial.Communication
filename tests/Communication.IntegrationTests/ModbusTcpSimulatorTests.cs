using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Client;
using Communication.Protocols.Modbus.Codecs;
using Communication.Protocols.Modbus.Models;
using Communication.Protocols.Modbus.Simulator;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

namespace Communication.IntegrationTests;

public sealed class ModbusTcpSimulatorTests
{
    [Fact]
    public async Task Real_tcp_client_reads_and_writes_the_simulator_data_store()
    {
        ModbusSlave slave = new();
        slave.DataStore.SetRegisters(ModbusDataArea.InputRegisters, 10, [100, 200]);
        await using ModbusTcpSimulatorServer server = CreateServer(slave);
        Assert.True((await server.StartAsync()).IsSuccess);
        await using TcpTransportChannel transport = CreateTransport(server.BoundPort);
        await using ModbusClient client = new(transport, ModbusTransportMode.Tcp);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        Assert.True((await client.WriteMultipleRegistersAsync(1, 0, [10, 20, 30])).IsSuccess);
        Assert.Equal([10, 20, 30], (await client.ReadHoldingRegistersAsync(1, 0, 3)).Value);
        Assert.Equal([100, 200], (await client.ReadInputRegistersAsync(1, 10, 2)).Value);
    }

    [Fact]
    public async Task Simulator_reassembles_a_fragmented_mbap_request()
    {
        ModbusSlave slave = new();
        slave.DataStore.SetRegisters(ModbusDataArea.HoldingRegisters, 0, [0x1234]);
        await using ModbusTcpSimulatorServer server = CreateServer(slave);
        await server.StartAsync();
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        NetworkStream stream = client.GetStream();
        byte[] request = ModbusTcpFrameCodec.EncodeRequest(ModbusRequest.Read(
            1,
            ModbusFunctionCode.ReadHoldingRegisters,
            0,
            1) with { TransactionId = 7 }).ToArray();

        await stream.WriteAsync(request.AsMemory(0, 5));
        await Task.Delay(20);
        await stream.WriteAsync(request.AsMemory(5));
        byte[] response = await ReadOneMbapFrameAsync(stream);
        ProtocolDecodeResult<ModbusResponse> decoded = ModbusTcpFrameCodec.TryDecodeResponse(
            new ReadOnlySequence<byte>(response));

        Assert.Equal(DecodeStatus.Done, decoded.Status);
        Assert.Equal((ushort)7, decoded.Value!.TransactionId);
        Assert.Equal(new byte[] { 0x12, 0x34 }, decoded.Value.Data.ToArray());
    }

    [Fact]
    public async Task Simulator_extracts_multiple_requests_from_one_sticky_tcp_write()
    {
        ModbusSlave slave = new();
        slave.DataStore.SetRegisters(ModbusDataArea.HoldingRegisters, 0, [11, 22]);
        await using ModbusTcpSimulatorServer server = CreateServer(slave);
        await server.StartAsync();
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        NetworkStream stream = client.GetStream();
        byte[] first = ModbusTcpFrameCodec.EncodeRequest(ModbusRequest.Read(
            1,
            ModbusFunctionCode.ReadHoldingRegisters,
            0,
            1) with { TransactionId = 1 }).ToArray();
        byte[] second = ModbusTcpFrameCodec.EncodeRequest(ModbusRequest.Read(
            1,
            ModbusFunctionCode.ReadHoldingRegisters,
            1,
            1) with { TransactionId = 2 }).ToArray();

        await stream.WriteAsync(first.Concat(second).ToArray());
        byte[] response1 = await ReadOneMbapFrameAsync(stream);
        byte[] response2 = await ReadOneMbapFrameAsync(stream);
        ModbusResponse[] responses =
        [
            ModbusTcpFrameCodec.TryDecodeResponse(new ReadOnlySequence<byte>(response1)).Value!,
            ModbusTcpFrameCodec.TryDecodeResponse(new ReadOnlySequence<byte>(response2)).Value!,
        ];

        Assert.Equal([1, 2], responses.Select(response => (int)response.TransactionId).Order());
        Assert.Equal(
            [11, 22],
            responses.Select(response => (int)((response.Data.Span[0] << 8) | response.Data.Span[1])).Order());
    }

    private static ModbusTcpSimulatorServer CreateServer(ModbusSlave slave) => new(
        new TcpServerOptions
        {
            ListenAddress = IPAddress.Loopback.ToString(),
            Port = 0,
            MaxConnections = 8,
            RequestQueueCapacity = 32,
        },
        slave);

    private static TcpTransportChannel CreateTransport(int port) => new(new TcpTransportOptions
    {
        Host = IPAddress.Loopback.ToString(),
        Port = port,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    });

    private static async Task<byte[]> ReadOneMbapFrameAsync(NetworkStream stream)
    {
        byte[] header = await ReadExactAsync(stream, 7);
        int remaining = ((header[4] << 8) | header[5]) - 1;
        byte[] pdu = await ReadExactAsync(stream, remaining);
        return header.Concat(pdu).ToArray();
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] bytes = new byte[count];
        int offset = 0;
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(3));
        while (offset < count)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(offset), cancellation.Token);
            if (read == 0)
            {
                throw new IOException("The Modbus simulator closed the connection.");
            }

            offset += read;
        }

        return bytes;
    }
}
