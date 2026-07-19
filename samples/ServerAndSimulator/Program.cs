using System.Net;
using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Models;
using Communication.Protocols.Modbus.Simulator;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

bool modbusMode = args.Length > 0 && string.Equals(args[0], "modbus", StringComparison.OrdinalIgnoreCase);
int portIndex = modbusMode ? 1 : 0;
int port = args.Length > portIndex && int.TryParse(args[portIndex], out int configuredPort)
    ? configuredPort
    : 5020;
using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

if (modbusMode)
{
    ModbusSlave slave = new();
    slave.DataStore.SetRegisters(ModbusDataArea.HoldingRegisters, 0, [100, 200, 300]);
    await using ModbusTcpSimulatorServer simulator = new(new TcpServerOptions
    {
        ListenAddress = IPAddress.Any.ToString(),
        Port = port,
        MaxConnections = 100,
        RequestQueueCapacity = 1024,
    }, slave);
    CommunicationResult started = await simulator.StartAsync(shutdown.Token);
    if (!started.IsSuccess)
    {
        Console.Error.WriteLine($"Modbus simulator failed to start: {started.Error?.Message}");
        return;
    }

    Console.WriteLine(
        $"Modbus TCP simulator listening on 0.0.0.0:{simulator.BoundPort}; unit=1, holding[0..2]=100,200,300.");
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }

    return;
}

await using TcpCommunicationServer server = new(new TcpServerOptions
{
    ListenAddress = IPAddress.Any.ToString(),
    Port = port,
    MaxConnections = 100,
    RequestQueueCapacity = 1024,
});

CommunicationResult start = await server.StartAsync(shutdown.Token);
if (!start.IsSuccess)
{
    Console.Error.WriteLine($"Server failed to start: {start.Error?.Message}");
    return;
}

Console.WriteLine($"TCP echo server listening on 0.0.0.0:{server.BoundPort}. Press Ctrl+C to stop.");
try
{
    await foreach (ServerRequestContext request in server.ReadRequestsAsync(shutdown.Token))
    {
        CommunicationResult<int> sent = await server.SendAsync(
            request.Session.SessionId,
            request.Message.Payload,
            shutdown.Token);
        Console.WriteLine(
            $"{request.Session.RemoteEndpoint ?? request.Session.SessionId}: " +
            $"received {request.Message.Payload.Length} bytes, echoed={sent.IsSuccess}");
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}
finally
{
    await server.StopAsync();
}
