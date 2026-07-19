using Communication.Abstractions.Models;
using Communication.Adapters;
using Communication.Protocols.OpcUa;
using Communication.Protocols.OpcUa.Models;
using Communication.Protocols.OpcUa.Simulator;

var options = new OpcUaConnectionOptions
{
    EndpointUrl = "opc.tcp://localhost:4840",
    SecurityMode = OpcUaMessageSecurityMode.SignAndEncrypt,
    SecurityPolicy = OpcUaSecurityPolicy.Basic256Sha256,
    Certificates = new OpcUaCertificateOptions
    {
        // The default is false. Only enable temporarily and always observe SecurityWarning.
        AllowUntrustedCertificates = false,
    },
};
var temperature = new VariableDefinition(
    "Temperature", "ns=2;s=Temperature", PlcDataType.Float64, Scale: 0.1);

await using var session = new MemoryOpcUaSession(options, new Dictionary<string, object?>
{
    [temperature.Address] = 215d,
});
var client = new OpcUaClient(session);
client.SecurityWarning += (_, warning) => Console.Error.WriteLine($"SECURITY: {warning.Message}");
await client.ConnectAsync();

CommunicationResult<OpcUaVariableSubscription> created = await client.SubscribeAsync(
    new[] { temperature });
await using OpcUaVariableSubscription subscription = created.GetValueOrThrow();
using var sampleCancellation = new CancellationTokenSource();
Task observer = Task.Run(async () =>
{
    int count = 0;
    await foreach (VariableValue value in subscription.WatchAsync(sampleCancellation.Token))
    {
        Console.WriteLine($"{value.Definition.Name}: {value.Value}, {value.Quality}, {value.Timestamp:O}");
        if (++count == 2)
        {
            break;
        }
    }
});

await session.SetValueAsync(temperature.Address, 223d, VariableQuality.Good);
await session.SimulateReconnectAsync();
while (subscription.RestoreCount == 0)
{
    await Task.Delay(10);
}

await session.SetValueAsync(temperature.Address, 227d, VariableQuality.Uncertain);
await observer;
Console.WriteLine($"Subscription restore count: {subscription.RestoreCount}");

bool input = false;
await using var digitalIo = new DelegateDigitalIoAdapter(
    "private-io",
    _ => ValueTask.FromResult(CommunicationResult<DigitalIoSnapshot>.Success(
        new DigitalIoSnapshot(new[] { input }, new[] { false }, DateTimeOffset.UtcNow))),
    (index, value, _) =>
    {
        Console.WriteLine($"Explicit output write: DO{index}={value}");
        return ValueTask.FromResult(CommunicationResult.Success());
    });
digitalIo.InputChanged += (_, edge) =>
    Console.WriteLine($"Input edge: DI{edge.Index} {edge.PreviousValue}->{edge.CurrentValue}");
await digitalIo.StartAsync();
await digitalIo.ReadStatusAsync();
input = true;
await digitalIo.ReadStatusAsync();
await digitalIo.SetOutputAsync(0, true);

// For a real server, replace MemoryOpcUaSession with this production implementation:
// await using var realSession = new OpcFoundationSession(options);
