using System.Buffers;
using Communication.Abstractions.Interfaces;

namespace Communication.ProtocolTests;

public sealed class ProtocolBoundaryTests
{
    [Fact]
    public void Codec_contract_uses_an_incremental_buffer_and_no_transport_parameter()
    {
        Type codec = typeof(IProtocolCodec<,>);
        Type decodeParameter = codec.GetMethod(nameof(IProtocolCodec<object, object>.TryDecode))!
            .GetParameters()
            .Single()
            .ParameterType;

        Assert.Equal(typeof(ReadOnlySequence<byte>), decodeParameter);
        Assert.DoesNotContain(
            codec.GetMethods().SelectMany(method => method.GetParameters()),
            parameter => typeof(ITransportChannel).IsAssignableFrom(parameter.ParameterType));
    }
}
