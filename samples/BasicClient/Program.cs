using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Client;
using Communication.Protocols.Modbus.Models;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

bool modbusMode = args.Length > 0 && string.Equals(args[0], "modbus", StringComparison.OrdinalIgnoreCase);
int offset = modbusMode ? 1 : 0;
string host = args.Length > offset ? args[offset] : "127.0.0.1";
int port = args.Length > offset + 1 && int.TryParse(args[offset + 1], out int configuredPort)
    ? configuredPort
    : 5020;
using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

TcpTransportChannel channel = new(new TcpTransportOptions
{
    Host = host,
    Port = port,
    ConnectTimeout = TimeSpan.FromSeconds(5),
});

if (modbusMode)
{
    await using ModbusClient modbus = new(channel, ModbusTransportMode.Tcp);
    CommunicationResult connected = await modbus.ConnectAsync(timeout.Token);
    if (!connected.IsSuccess)
    {
        Console.Error.WriteLine($"Connection failed: {connected.Error?.Message}");
        return;
    }

    CommunicationResult<IReadOnlyList<ushort>> read = await modbus.ReadHoldingRegistersAsync(
        unitId: 1,
        address: 0,
        quantity: 3,
        cancellationToken: timeout.Token);
    Console.WriteLine(read.IsSuccess
        ? $"Holding registers 0..2: {string.Join(", ", read.Value!)}"
        : $"Modbus read failed: {read.Error?.Message}");
    return;
}

await using (channel)
{
    string text = args.Length > 2 ? args[2] : "Hello industrial communication";

    CommunicationResult connected = await channel.ConnectAsync(timeout.Token);
    if (!connected.IsSuccess)
    {
        Console.Error.WriteLine($"Connection failed: {connected.Error?.Message}");
        return;
    }

    byte[] payload = System.Text.Encoding.UTF8.GetBytes(text);
    CommunicationResult<int> sent = await channel.SendAsync(payload, timeout.Token);
    if (!sent.IsSuccess)
    {
        Console.Error.WriteLine($"Send failed: {sent.Error?.Message}");
        return;
    }

    await foreach (ReadOnlyMemory<byte> response in channel.ReceiveAsync(timeout.Token))
    {
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Span));
        break;
    }

    await channel.DisconnectAsync();
}
