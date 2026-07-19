using System.Buffers;
using Communication.Abstractions.Interfaces;

namespace Communication.Core.Framing;

/// <summary>Adapts a caller-supplied frame decoding function.</summary>
public sealed class DelegatingFrameDecoder : IFrameDecoder
{
    private readonly Func<ReadOnlySequence<byte>, FrameDecodeResult> _decoder;

    /// <summary>Initializes a custom frame decoder.</summary>
    public DelegatingFrameDecoder(Func<ReadOnlySequence<byte>, FrameDecodeResult> decoder)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
    }

    /// <inheritdoc />
    public FrameDecodeResult TryDecode(ReadOnlySequence<byte> buffer) => _decoder(buffer);
}
