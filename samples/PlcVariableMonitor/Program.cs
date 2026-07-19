using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Plc;
using Communication.Protocols.Mc;
using Communication.Protocols.Mc.Simulator;
using Communication.Protocols.Modbus.Client;
using Communication.Protocols.Modbus.Models;
using Communication.Protocols.Modbus.Simulator;
using Communication.Protocols.S7;
using Communication.Protocols.S7.Simulator;

await RunAsync(
    "Modbus",
    new ModbusPlcClient(new ModbusClient(
        new ModbusSimulatorChannel(ModbusTransportMode.Tcp),
        ModbusTransportMode.Tcp)),
    [
        (new VariableDefinition("Ready", "C0", PlcDataType.Boolean), (object)true),
        (new VariableDefinition("Count", "HR10", PlcDataType.Int16), (object)(short)120),
    ]);

await RunAsync(
    "Siemens S7",
    new S7PlcClient(new S7MemoryDataAccess()),
    [
        (new VariableDefinition("Ready", "DB1.DBX0.0", PlcDataType.Boolean), (object)true),
        (new VariableDefinition("Speed", "DB1.DBD2", PlcDataType.Float32), (object)12.5f),
    ]);

await RunAsync(
    "Mitsubishi MC",
    new McPlcClient(new McMemoryDataAccess()),
    [
        (new VariableDefinition("Ready", "M0", PlcDataType.Boolean), (object)true),
        (new VariableDefinition("Count", "D10", PlcDataType.Int16), (object)(short)320),
    ]);

static async Task RunAsync(
    string protocol,
    IPlcClient client,
    IReadOnlyList<(VariableDefinition Definition, object Value)> mappings)
{
    await using (client)
    {
        CommunicationResult connected = await client.ConnectAsync();
        if (!connected.IsSuccess)
        {
            Console.WriteLine($"{protocol}: connect failed: {connected.Error!.Message}");
            return;
        }

        foreach ((VariableDefinition definition, object value) in mappings)
        {
            CommunicationResult written = await client.WriteAsync(new PlcWriteRequest(definition, value));
            if (!written.IsSuccess)
            {
                Console.WriteLine($"{protocol}/{definition.Name}: write failed: {written.Error!.Message}");
            }
        }

        await using var monitor = new VariableMonitor(client);
        await monitor.StartAsync(
            mappings.Select(mapping => mapping.Definition).ToArray(),
            new VariableMonitorOptions { PollInterval = TimeSpan.FromMilliseconds(100) });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int received = 0;
        await foreach (VariableValue update in monitor.WatchAsync(timeout.Token))
        {
            Console.WriteLine(
                $"{protocol}/{update.Definition.Name} = {update.Value}; " +
                $"quality={update.Quality}; timestamp={update.Timestamp:O}");
            if (++received == mappings.Count)
            {
                break;
            }
        }

        await monitor.StopAsync();
    }
}
