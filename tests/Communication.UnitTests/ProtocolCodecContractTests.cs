using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.UnitTests;

public sealed class ProtocolCodecContractTests
{
    [Fact]
    public void Codec_encodes_without_a_transport()
    {
        LengthPrefixedTextCodec codec = new();

        ReadOnlyMemory<byte> encoded = codec.Encode("OK");

        Assert.Equal(new byte[] { 2, (byte)'O', (byte)'K' }, encoded.ToArray());
    }

    [Fact]
    public void Codec_requests_more_data_for_a_fragmented_response()
    {
        LengthPrefixedTextCodec codec = new();
        ReadOnlySequence<byte> fragment = new(new byte[] { 3, (byte)'A' });

        ProtocolDecodeResult<string> result = codec.TryDecode(fragment);

        Assert.Equal(DecodeStatus.NeedMoreData, result.Status);
        Assert.Equal(0, result.Consumed);
        Assert.Equal(2, result.Examined);
    }

    [Fact]
    public void Codec_consumes_only_one_response_from_a_sticky_buffer()
    {
        LengthPrefixedTextCodec codec = new();
        ReadOnlySequence<byte> stickyBuffer = new(
            new byte[] { 2, (byte)'O', (byte)'K', 1, (byte)'!' });

        ProtocolDecodeResult<string> result = codec.TryDecode(stickyBuffer);

        Assert.Equal(DecodeStatus.Done, result.Status);
        Assert.Equal("OK", result.Value);
        Assert.Equal(3, result.Consumed);
        Assert.Equal(3, result.Examined);
    }

    private sealed class LengthPrefixedTextCodec : IProtocolCodec<string, string>
    {
        public ReadOnlyMemory<byte> Encode(string request)
        {
            byte[] text = System.Text.Encoding.ASCII.GetBytes(request);
            if (text.Length > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(request));
            }

            byte[] result = new byte[text.Length + 1];
            result[0] = (byte)text.Length;
            text.CopyTo(result, 1);
            return result;
        }

        public ProtocolDecodeResult<string> TryDecode(ReadOnlySequence<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return ProtocolDecodeResult<string>.NeedMoreData(0);
            }

            byte length = buffer.FirstSpan[0];
            long frameLength = length + 1L;
            if (buffer.Length < frameLength)
            {
                return ProtocolDecodeResult<string>.NeedMoreData(buffer.Length);
            }

            byte[] frame = buffer.Slice(1, length).ToArray();
            return ProtocolDecodeResult<string>.Done(
                System.Text.Encoding.ASCII.GetString(frame),
                frameLength);
        }
    }
}
