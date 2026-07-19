using System.Net;
using Communication.Abstractions.Models;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

namespace Communication.IntegrationTests;

public sealed class TcpTransportTests
{
    [Fact]
    public async Task Client_and_server_complete_an_echo_round_trip()
    {
        await using TcpCommunicationServer server = CreateServer();
        Assert.True((await server.StartAsync()).IsSuccess);
        await using TcpTransportChannel client = CreateClient(server.BoundPort);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        Task<ServerRequestContext> requestTask = ReadOneRequestAsync(server);
        byte[] payload = "industrial-echo"u8.ToArray();
        Assert.Equal(payload.Length, (await client.SendAsync(payload)).GetValueOrThrow());
        ServerRequestContext request = await requestTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload, request.Message.Payload.ToArray());
        Assert.Equal(payload.Length, (await server.SendAsync(
            request.Session.SessionId,
            request.Message.Payload)).GetValueOrThrow());
        ReadOnlyMemory<byte> response = await ReadOneChunkAsync(client).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload, response.ToArray());
        Assert.True((await client.DisconnectAsync()).IsSuccess);
        Assert.True((await server.StopAsync()).IsSuccess);
    }

    [Fact]
    public async Task Multiple_clients_have_distinct_server_sessions()
    {
        await using TcpCommunicationServer server = CreateServer();
        await server.StartAsync();
        await using TcpTransportChannel first = CreateClient(server.BoundPort);
        await using TcpTransportChannel second = CreateClient(server.BoundPort);
        await first.ConnectAsync();
        await second.ConnectAsync();

        await first.SendAsync(new byte[] { 1 });
        await second.SendAsync(new byte[] { 2 });
        ServerRequestContext firstRequest = await ReadOneRequestAsync(server).WaitAsync(TimeSpan.FromSeconds(5));
        ServerRequestContext secondRequest = await ReadOneRequestAsync(server).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(firstRequest.Session.SessionId, secondRequest.Session.SessionId);
        Assert.Equal(2, server.Sessions.Count);
    }

    [Fact]
    public async Task Connect_and_disconnect_are_idempotent()
    {
        await using TcpCommunicationServer server = CreateServer();
        await server.StartAsync();
        await using TcpTransportChannel client = CreateClient(server.BoundPort);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.True((await client.DisconnectAsync()).IsSuccess);
        Assert.True((await client.DisconnectAsync()).IsSuccess);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Canceling_a_tcp_receive_exits_promptly_without_closing_the_channel()
    {
        await using TcpCommunicationServer server = CreateServer();
        await server.StartAsync();
        await using TcpTransportChannel client = CreateClient(server.BoundPort);
        await client.ConnectAsync();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadOneChunkAsync(client, cancellation.Token));

        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task Concurrent_sends_are_serialized_without_byte_interleaving()
    {
        await using TcpCommunicationServer server = CreateServer();
        await server.StartAsync();
        await using TcpTransportChannel client = CreateClient(server.BoundPort);
        await client.ConnectAsync();
        const int sendCount = 20;
        Task<byte[]> receivedTask = ReadBytesAsync(server, sendCount * 4);

        Task[] sends = Enumerable.Range(0, sendCount)
            .Select(value => client.SendAsync(Enumerable.Repeat((byte)value, 4).ToArray()).AsTask())
            .ToArray();
        await Task.WhenAll(sends);
        byte[] received = await receivedTask.WaitAsync(TimeSpan.FromSeconds(5));

        byte[][] groups = received.Chunk(4).ToArray();
        Assert.All(groups, group => Assert.All(group, value => Assert.Equal(group[0], value)));
        Assert.Equal(
            Enumerable.Range(0, sendCount).Select(value => (byte)value).Order(),
            groups.Select(group => group[0]).Order());
    }

    [Fact]
    public async Task Remote_close_is_detected_as_a_faulted_half_open_connection()
    {
        await using TcpCommunicationServer server = CreateServer();
        await server.StartAsync();
        await using TcpTransportChannel client = CreateClient(server.BoundPort);
        await client.ConnectAsync();
        await client.SendAsync(new byte[] { 1 });
        await ReadOneRequestAsync(server).WaitAsync(TimeSpan.FromSeconds(5));

        await server.StopAsync();
        await using IAsyncEnumerator<ReadOnlyMemory<byte>> reader = client.ReceiveAsync().GetAsyncEnumerator();
        bool hasData = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(hasData);
        Assert.Equal(ConnectionState.Faulted, client.State);
    }

    private static TcpCommunicationServer CreateServer() => new(new TcpServerOptions
    {
        ListenAddress = IPAddress.Loopback.ToString(),
        Port = 0,
        MaxConnections = 8,
        RequestQueueCapacity = 32,
    });

    private static TcpTransportChannel CreateClient(int port) => new(new TcpTransportOptions
    {
        Host = IPAddress.Loopback.ToString(),
        Port = port,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    });

    private static async Task<ServerRequestContext> ReadOneRequestAsync(TcpCommunicationServer server)
    {
        await foreach (ServerRequestContext request in server.ReadRequestsAsync())
        {
            return request;
        }

        throw new InvalidOperationException("Server request stream ended.");
    }

    private static async Task<ReadOnlyMemory<byte>> ReadOneChunkAsync(
        TcpTransportChannel client,
        CancellationToken cancellationToken = default)
    {
        await foreach (ReadOnlyMemory<byte> chunk in client.ReceiveAsync(cancellationToken))
        {
            return chunk;
        }

        throw new InvalidOperationException("Client receive stream ended.");
    }

    private static async Task<byte[]> ReadBytesAsync(TcpCommunicationServer server, int expectedLength)
    {
        List<byte> bytes = [];
        await foreach (ServerRequestContext request in server.ReadRequestsAsync())
        {
            bytes.AddRange(request.Message.Payload.ToArray());
            if (bytes.Count >= expectedLength)
            {
                return bytes.ToArray();
            }
        }

        throw new InvalidOperationException("Server request stream ended.");
    }
}
