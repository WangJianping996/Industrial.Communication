using System.Net;
using Communication.Abstractions.Models;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

namespace Communication.IntegrationTests;

public sealed class Day7TransportStabilityTests
{
    [Fact]
    public async Task Concurrent_connect_disconnect_race_is_serialized_and_releases_server_sessions()
    {
        await using var server = new TcpCommunicationServer(new TcpServerOptions
        {
            ListenAddress = IPAddress.Loopback.ToString(),
            Port = 0,
            MaxConnections = 8,
            RequestQueueCapacity = 32,
        });
        await server.StartAsync();
        await using var client = CreateClient(server.BoundPort);

        Task<CommunicationResult>[] operations = Enumerable.Range(0, 100)
            .Select(index => (index & 1) == 0
                ? client.ConnectAsync().AsTask()
                : client.DisconnectAsync().AsTask())
            .ToArray();
        CommunicationResult[] results = await Task.WhenAll(operations);
        await client.DisconnectAsync();
        await WaitUntilAsync(() => server.Sessions.Count == 0);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(ConnectionState.Disconnected, client.State);
        Assert.Empty(server.Sessions);
    }

    [Fact]
    public async Task Accelerated_soak_completes_500_tcp_round_trips_on_one_connection()
    {
        await using var server = new TcpCommunicationServer(new TcpServerOptions
        {
            ListenAddress = IPAddress.Loopback.ToString(),
            Port = 0,
            MaxConnections = 2,
            RequestQueueCapacity = 32,
        });
        await server.StartAsync();
        await using var client = CreateClient(server.BoundPort);
        await client.ConnectAsync();
        Task serverLoop = Task.Run(async () =>
        {
            int count = 0;
            await foreach (ServerRequestContext request in server.ReadRequestsAsync())
            {
                Assert.True((await server.SendAsync(
                    request.Session.SessionId,
                    request.Message.Payload)).IsSuccess);
                if (++count == 500)
                {
                    break;
                }
            }
        });
        await using IAsyncEnumerator<ReadOnlyMemory<byte>> responses =
            client.ReceiveAsync().GetAsyncEnumerator();

        for (int index = 0; index < 500; index++)
        {
            byte[] payload = BitConverter.GetBytes(index);
            Assert.True((await client.SendAsync(payload)).IsSuccess);
            Assert.True(await responses.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(payload, responses.Current.ToArray());
        }

        await serverLoop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    private static TcpTransportChannel CreateClient(int port) => new(new TcpTransportOptions
    {
        Host = IPAddress.Loopback.ToString(),
        Port = port,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
