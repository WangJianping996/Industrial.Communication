using Communication.Transports.Options;
using Communication.Transports.Serial;

namespace Communication.IntegrationTests;

public sealed class SerialTransportOptionTests
{
    [Fact]
    public void Serial_channel_rejects_an_empty_port_name_before_touching_hardware()
    {
        SerialTransportOptions options = new() { PortName = string.Empty };

        Assert.Throws<ArgumentException>(() => new SerialTransportChannel(options));
    }

    [Fact]
    public void Serial_channel_exposes_a_stable_diagnostic_identifier()
    {
        SerialTransportOptions options = new() { PortName = "COM_TEST", BaudRate = 115200 };

        SerialTransportChannel channel = new(options);

        Assert.Equal("serial://COM_TEST", channel.ChannelId);
    }
}
