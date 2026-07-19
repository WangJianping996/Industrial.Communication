using Communication.Abstractions.Interfaces;
using Communication.DependencyInjection;
using Communication.Protocols.Mc;
using Communication.Protocols.Modbus.Client;
using Communication.Protocols.Modbus.Models;
using Communication.Protocols.OpcUa;
using Communication.Protocols.OpcUa.Models;
using Communication.Protocols.S7;
using Microsoft.Extensions.DependencyInjection;

namespace Communication.ProtocolTests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task All_protocol_registrations_resolve_as_unified_clients_and_are_container_owned()
    {
        var services = new ServiceCollection();
        services.AddModbusPlcClient(_ => new ScriptedTransportChannel(_ => []), ModbusTransportMode.Tcp);
        services.AddS7PlcClient(_ => new ScriptedTransportChannel(_ => []));
        services.AddMc3EPlcClient(_ => new ScriptedTransportChannel(_ => []));
        services.AddOpcUaPlcClient(_ => new OpcUaConnectionOptions
        {
            EndpointUrl = "opc.tcp://localhost:4840",
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        IPlcClient[] clients = provider.GetServices<IPlcClient>().ToArray();

        Assert.Collection(
            clients,
            client => Assert.IsType<ModbusPlcClient>(client),
            client => Assert.IsType<S7PlcClient>(client),
            client => Assert.IsType<McPlcClient>(client),
            client => Assert.IsType<OpcUaClient>(client));
        Assert.Same(provider.GetRequiredService<OpcUaClient>(), clients[3]);
    }
}
