using System.Buffers;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Framing;

namespace Communication.UnitTests;

public sealed class FrameDecoderTests
{
    [Fact]
    public void Fixed_length_decoder_leaves_sticky_bytes_unconsumed()
    {
        FixedLengthFrameDecoder decoder = new(3);

        FrameDecodeResult result = decoder.TryDecode(new ReadOnlySequence<byte>([1, 2, 3, 4, 5]));

        Assert.Equal(DecodeStatus.Done, result.Status);
        Assert.Equal(3, result.Consumed);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Frame.ToArray());
    }

    [Fact]
    public void Length_field_decoder_handles_every_random_fragmentation()
    {
        LengthFieldFrameDecoder decoder = new(lengthFieldOffset: 0, lengthFieldLength: 1);
        byte[] stream = [4, 1, 2, 3, 3, 8, 9];

        for (int seed = 0; seed < 100; seed++)
        {
            IReadOnlyList<byte[]> frames = DecodeFragmented(decoder, stream, seed);

            Assert.Equal(2, frames.Count);
            Assert.Equal(new byte[] { 4, 1, 2, 3 }, frames[0]);
            Assert.Equal(new byte[] { 3, 8, 9 }, frames[1]);
        }
    }

    [Fact]
    public void Length_field_decoder_can_strip_a_header_and_use_little_endian_length()
    {
        LengthFieldFrameDecoder decoder = new(
            lengthFieldOffset: 1,
            lengthFieldLength: 2,
            initialBytesToStrip: 3,
            littleEndian: true);

        FrameDecodeResult result = decoder.TryDecode(
            new ReadOnlySequence<byte>([0xAA, 0x05, 0x00, 0x11, 0x22]));

        Assert.Equal(DecodeStatus.Done, result.Status);
        Assert.Equal(5, result.Consumed);
        Assert.Equal(new byte[] { 0x11, 0x22 }, result.Frame.ToArray());
    }

    [Fact]
    public void Invalid_length_is_reported_and_consumes_one_byte_for_progress()
    {
        LengthFieldFrameDecoder decoder = new(0, 1, maxFrameLength: 8);

        FrameDecodeResult result = decoder.TryDecode(new ReadOnlySequence<byte>([100]));

        Assert.Equal(DecodeStatus.InvalidData, result.Status);
        Assert.Equal(1, result.Consumed);
        Assert.Equal(CommunicationErrorCode.ProtocolError, result.Error?.Code);
    }

    [Fact]
    public void Delimiter_decoder_extracts_multiple_frames_from_one_buffer()
    {
        DelimiterFrameDecoder decoder = new("\r\n"u8);
        byte[] stream = "ONE\r\nTWO\r\n"u8.ToArray();

        IReadOnlyList<byte[]> frames = DecodeFragmented(decoder, stream, seed: 42);

        Assert.Equal(2, frames.Count);
        Assert.Equal("ONE"u8.ToArray(), frames[0]);
        Assert.Equal("TWO"u8.ToArray(), frames[1]);
    }

    [Fact]
    public void Delimiter_decoder_rejects_an_unbounded_frame()
    {
        DelimiterFrameDecoder decoder = new([0], maxFrameLength: 3);

        FrameDecodeResult result = decoder.TryDecode(new ReadOnlySequence<byte>([1, 2, 3, 4]));

        Assert.Equal(DecodeStatus.InvalidData, result.Status);
    }

    [Fact]
    public void Silent_interval_decoder_completes_only_after_notification()
    {
        SilentIntervalFrameDecoder decoder = new();
        ReadOnlySequence<byte> buffer = new([1, 2, 3]);

        Assert.Equal(DecodeStatus.NeedMoreData, decoder.TryDecode(buffer).Status);

        decoder.NotifySilence();
        FrameDecodeResult result = decoder.TryDecode(buffer);

        Assert.Equal(DecodeStatus.Done, result.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Frame.ToArray());
    }

    [Fact]
    public void Delegating_decoder_uses_custom_rules()
    {
        DelegatingFrameDecoder decoder = new(buffer =>
            buffer.Length >= 1
                ? FrameDecodeResult.Done(buffer.Slice(0, 1).ToArray(), 1)
                : FrameDecodeResult.NeedMoreData(0));

        FrameDecodeResult result = decoder.TryDecode(new ReadOnlySequence<byte>([0x7E]));

        Assert.Equal(new byte[] { 0x7E }, result.Frame.ToArray());
    }

    private static IReadOnlyList<byte[]> DecodeFragmented(IFrameDecoder decoder, byte[] stream, int seed)
    {
        Random random = new(seed);
        List<byte> buffered = [];
        List<byte[]> frames = [];
        int offset = 0;
        while (offset < stream.Length)
        {
            int chunkLength = Math.Min(random.Next(1, 5), stream.Length - offset);
            buffered.AddRange(stream.AsSpan(offset, chunkLength).ToArray());
            offset += chunkLength;

            while (buffered.Count > 0)
            {
                FrameDecodeResult result = decoder.TryDecode(new ReadOnlySequence<byte>(buffered.ToArray()));
                if (result.Status == DecodeStatus.NeedMoreData)
                {
                    break;
                }

                Assert.Equal(DecodeStatus.Done, result.Status);
                frames.Add(result.Frame.ToArray());
                buffered.RemoveRange(0, checked((int)result.Consumed));
            }
        }

        Assert.Empty(buffered);
        return frames;
    }
}
