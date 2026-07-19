using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.S7.Codecs;
using Communication.Protocols.S7.Models;

namespace Communication.Protocols.S7;

/// <summary>Provides direct S7comm absolute byte access over ISO-on-TCP port 102.</summary>
public sealed class S7IsoTcpDataAccess : IS7DataAccess
{
    private readonly ITransportChannel _channel;
    private readonly S7ClientOptions _options;
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private readonly List<byte> _buffer = [];
    private IAsyncEnumerator<ReadOnlyMemory<byte>>? _receiver;
    private CancellationTokenSource? _receiveCancellation;
    private ushort _pduReference;
    private int _disposed;

    /// <summary>Initializes ISO-on-TCP data access over a replaceable byte channel.</summary>
    public S7IsoTcpDataAccess(ITransportChannel channel, S7ClientOptions? options = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _options = options ?? new S7ClientOptions();
        if (_options.Rack > 7 || _options.Slot > 31 || _options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <inheritdoc />
    public ConnectionState State => _channel.State;

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommunicationResult connected = await _channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!connected.IsSuccess)
            {
                return connected;
            }

            _buffer.Clear();
            if (_receiver is not null)
            {
                await _receiver.DisposeAsync().ConfigureAwait(false);
            }

            _receiveCancellation?.Cancel();
            _receiveCancellation?.Dispose();
            _receiveCancellation = new CancellationTokenSource();
            _receiver = _channel.ReceiveAsync(_receiveCancellation.Token)
                .GetAsyncEnumerator(_receiveCancellation.Token);
            CommunicationResult<ReadOnlyMemory<byte>> cotp = await ExchangeAsync(
                S7IsoOnTcpCodec.EncodeConnectionRequest(_options.Rack, _options.Slot),
                cancellationToken).ConfigureAwait(false);
            CommunicationResult handshake = cotp.IsSuccess
                ? S7IsoOnTcpCodec.ValidateConnectionConfirmation(cotp.Value!.Span)
                : CommunicationResult.Failure(cotp.Error!);
            if (!handshake.IsSuccess)
            {
                await _channel.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                return handshake;
            }

            CommunicationResult<ReadOnlyMemory<byte>> setup = await ExchangeAsync(
                S7IsoOnTcpCodec.EncodeSetupCommunication(NextReference()),
                cancellationToken).ConfigureAwait(false);
            CommunicationResult result = setup.IsSuccess
                ? S7IsoOnTcpCodec.ValidateSetupResponse(setup.Value!.Span)
                : CommunicationResult.Failure(setup.Error!);
            if (!result.IsSuccess)
            {
                await _channel.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_receiver is not null)
            {
                _receiveCancellation?.Cancel();
                await _receiver.DisposeAsync().ConfigureAwait(false);
                _receiver = null;
            }

            _receiveCancellation?.Dispose();
            _receiveCancellation = null;
            _buffer.Clear();
            return await _channel.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<ReadOnlyMemory<byte>>> ReadBytesAsync(
        S7Address address,
        int byteCount,
        CancellationToken cancellationToken = default)
    {
        if (byteCount is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommunicationResult<ReadOnlyMemory<byte>> response = await ExchangeAsync(
                S7IsoOnTcpCodec.EncodeReadRequest(
                    address with { BitOffset = null },
                    checked((ushort)byteCount),
                    NextReference()),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccess
                ? S7IsoOnTcpCodec.DecodeReadResponse(response.Value!.Span, byteCount)
                : response;
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> WriteBytesAsync(
        S7Address address,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommunicationResult<ReadOnlyMemory<byte>> response = await ExchangeAsync(
                S7IsoOnTcpCodec.EncodeWriteRequest(
                    address with { BitOffset = null },
                    bytes.Span,
                    NextReference()),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccess
                ? S7IsoOnTcpCodec.ValidateWriteResponse(response.Value!.Span)
                : CommunicationResult.Failure(response.Error!);
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        _receiveCancellation?.Dispose();
        _exchangeGate.Dispose();
    }

    private async ValueTask<CommunicationResult<ReadOnlyMemory<byte>>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        if (State != ConnectionState.Connected || _receiver is null)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidState,
                "The S7 ISO-on-TCP channel is not connected."));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            CommunicationResult<int> sent = await _channel.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!sent.IsSuccess)
            {
                return CommunicationResult<ReadOnlyMemory<byte>>.Failure(sent.Error!);
            }

            return await ReadTpktAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                CommunicationErrorCode.Timeout,
                $"The S7 endpoint did not respond within {_options.RequestTimeout}."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                CommunicationErrorCode.ConnectionFailure,
                exception.Message,
                Exception: exception));
        }
    }

    private async ValueTask<CommunicationResult<ReadOnlyMemory<byte>>> ReadTpktAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_buffer.Count >= 4)
            {
                if (_buffer[0] != 0x03 || _buffer[1] != 0x00)
                {
                    _buffer.RemoveAt(0);
                    return CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                        CommunicationErrorCode.ProtocolError,
                        "The ISO-on-TCP stream contains an invalid TPKT header."));
                }

                int length = (_buffer[2] << 8) | _buffer[3];
                if (length < 7)
                {
                    _buffer.Clear();
                    return CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                        CommunicationErrorCode.ProtocolError,
                        "The ISO-on-TCP stream declares an invalid TPKT length."));
                }

                if (_buffer.Count >= length)
                {
                    byte[] frame = _buffer.Take(length).ToArray();
                    _buffer.RemoveRange(0, length);
                    return CommunicationResult<ReadOnlyMemory<byte>>.Success(frame);
                }
            }

            IAsyncEnumerator<ReadOnlyMemory<byte>>? receiver = _receiver;
            if (receiver is null)
            {
                return CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                    CommunicationErrorCode.ConnectionFailure,
                    "The S7 endpoint closed the ISO-on-TCP stream."));
            }

            Task<bool> moveTask = receiver.MoveNextAsync().AsTask();
            Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Task completed = await Task.WhenAny(moveTask, cancellationTask).ConfigureAwait(false);
            if (completed != moveTask)
            {
                _receiveCancellation?.Cancel();
                await cancellationTask.ConfigureAwait(false);
            }

            if (!await moveTask.ConfigureAwait(false))
            {
                return CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                    CommunicationErrorCode.ConnectionFailure,
                    "The S7 endpoint closed the ISO-on-TCP stream."));
            }

            _buffer.AddRange(receiver.Current.ToArray());
        }
    }

    private ushort NextReference() => unchecked(++_pduReference);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(S7IsoTcpDataAccess));
        }
    }
}
