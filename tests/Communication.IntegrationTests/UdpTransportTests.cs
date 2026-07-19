using System.Net;
using Communication.Abstractions.Models;
using Communication.Transports.Options;
using Communication.Transports.Udp;

namespace Communication.IntegrationTests;

public sealed class UdpTransportTests
{
    [Fact]
    public async Task Connected_udp_sender_delivers_a_datagram_to_a_bound_receiver()
    {
        await using UdpTransportChannel receiver = new(new UdpTransportOptions
        {
            LocalAddress = IPAddress.Loopback.ToString(),
            LocalPort = 0,
        });
        Assert.True((await receiver.ConnectAsync()).IsSuccess);

        await using UdpTransportChannel sender = new(new UdpTransportOptions
        {
            LocalAddress = IPAddress.Loopback.ToString(),
            LocalPort = 0,
            RemoteHost = IPAddress.Loopback.ToString(),
            RemotePort = receiver.BoundPort,
        });
        Assert.True((await sender.ConnectAsync()).IsSuccess);
        byte[] payload = [0x10, 0x20, 0x30, 0x40];

        Assert.Equal(payload.Length, (await sender.SendAsync(payload)).GetValueOrThrow());
        ReadOnlyMemory<byte> received = await ReadOneDatagramAsync(receiver)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload, received.ToArray());
    }

    [Fact]
    public async Task Udp_send_without_a_remote_endpoint_returns_an_invalid_state_error()
    {
        await using UdpTransportChannel channel = new(new UdpTransportOptions
        {
            LocalAddress = IPAddress.Loopback.ToString(),
            LocalPort = 0,
        });
        await channel.ConnectAsync();

        CommunicationResult<int> result = await channel.SendAsync(new byte[] { 1 });

        Assert.False(result.IsSuccess);
        Assert.Equal(CommunicationErrorCode.InvalidState, result.Error?.Code);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadOneDatagramAsync(UdpTransportChannel channel)
    {
        await foreach (ReadOnlyMemory<byte> datagram in channel.ReceiveAsync())
        {
            return datagram;
        }

        throw new InvalidOperationException("UDP receive stream ended.");
    }
}
