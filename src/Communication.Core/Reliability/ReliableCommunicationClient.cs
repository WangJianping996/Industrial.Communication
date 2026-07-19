using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Communication.Abstractions.Exceptions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Reliability;

/// <summary>
/// Coordinates a transport, incremental protocol codec, response correlator, timeout, cancellation,
/// retry, monitoring, and optional automatic reconnection.
/// </summary>
/// <typeparam name="TRequest">The protocol request model.</typeparam>
/// <typeparam name="TResponse">The protocol response model.</typeparam>
public sealed class ReliableCommunicationClient<TRequest, TResponse> :
    ICommunicationClient<TRequest, TResponse>
{
    private readonly ITransportChannel _channel;
    private readonly IProtocolCodec<TRequest, TResponse> _codec;
    private readonly IResponseCorrelator<TRequest, TResponse> _correlator;
    private readonly IRetryPolicy? _retryPolicy;
    private readonly IMessageMonitor? _monitor;
    private readonly CommunicationClientOptions _options;
    private readonly SemaphoreSlim _inFlightGate;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _receiveLock = new();
    private readonly AutomaticReconnectCoordinator? _reconnectCoordinator;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private int _disposed;

    /// <summary>Initializes a reliable request/response client.</summary>
    public ReliableCommunicationClient(
        ITransportChannel channel,
        IProtocolCodec<TRequest, TResponse> codec,
        IResponseCorrelator<TRequest, TResponse> correlator,
        CommunicationClientOptions? options = null,
        IRetryPolicy? retryPolicy = null,
        IReconnectPolicy? reconnectPolicy = null,
        IMessageMonitor? monitor = null,
        IEnumerable<IConnectionRecoveryHandler>? recoveryHandlers = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _correlator = correlator ?? throw new ArgumentNullException(nameof(correlator));
        _options = options ?? new CommunicationClientOptions();
        ValidateOptions(_options, correlator);
        _retryPolicy = retryPolicy;
        _monitor = monitor;
        _inFlightGate = new SemaphoreSlim(correlator.MaxInFlight, correlator.MaxInFlight);
        _channel.StateChanged += ForwardStateChanged;

        if (reconnectPolicy is not null)
        {
            List<IConnectionRecoveryHandler> handlers =
            [new DelegatingConnectionRecoveryHandler(OnReconnectedAsync)];
            if (recoveryHandlers is not null)
            {
                handlers.AddRange(recoveryHandlers);
            }

            _reconnectCoordinator = new AutomaticReconnectCoordinator(channel, reconnectPolicy, handlers);
        }
    }

    /// <inheritdoc />
    public ConnectionState State => _channel.State;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Raised after automatic reconnection and all recovery hooks complete.</summary>
    public event EventHandler? Reconnected
    {
        add
        {
            if (_reconnectCoordinator is not null)
            {
                _reconnectCoordinator.Reconnected += value;
            }
        }
        remove
        {
            if (_reconnectCoordinator is not null)
            {
                _reconnectCoordinator.Reconnected -= value;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CommunicationResult result = _reconnectCoordinator is null
            ? await _channel.ConnectAsync(cancellationToken).ConfigureAwait(false)
            : await _reconnectCoordinator.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            StartReceiveLoop(forceRestart: false);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelReceiveLoop();
        FailAllPending(new CommunicationError(
            CommunicationErrorCode.Canceled,
            "The communication client was disconnected."));
        return _reconnectCoordinator is null
            ? await _channel.DisconnectAsync(cancellationToken).ConfigureAwait(false)
            : await _reconnectCoordinator.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<TResponse>> ExecuteAsync(
        TRequest request,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State != ConnectionState.Connected)
        {
            return CommunicationResult<TResponse>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidState,
                "The communication client is not connected."));
        }

        options ??= new CommunicationRequestOptions();
        TimeSpan timeout = options.Timeout ?? _options.DefaultTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive.");
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        for (int attempt = 1; ; attempt++)
        {
            CommunicationResult<TResponse> result = await ExecuteOnceAsync(request, timeout, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess || !options.EnableRetry || _retryPolicy is null)
            {
                return result;
            }

            RetryDecision decision = await _retryPolicy.GetDecisionAsync(
                new RetryContext("Execute", attempt, elapsed.Elapsed, result.Error!),
                cancellationToken).ConfigureAwait(false);
            if (!decision.ShouldRetry)
            {
                return result;
            }

            await Task.Delay(decision.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.StateChanged -= ForwardStateChanged;
        _lifetimeCancellation.Cancel();
        CancelReceiveLoop();
        FailAllPending(new CommunicationError(
            CommunicationErrorCode.Canceled,
            "The communication client was disposed."));
        if (_reconnectCoordinator is not null)
        {
            await _reconnectCoordinator.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await _channel.DisconnectAsync().ConfigureAwait(false);
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
        _receiveCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
        _inFlightGate.Dispose();
    }

    private async ValueTask<CommunicationResult<TResponse>> ExecuteOnceAsync(
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await _inFlightGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? key = null;
        PendingRequest? pending = null;
        try
        {
            key = _correlator.GetRequestKey(request);
            pending = new PendingRequest();
            if (!_pending.TryAdd(key, pending))
            {
                return CommunicationResult<TResponse>.Failure(new CommunicationError(
                    CommunicationErrorCode.InvalidState,
                    $"A request with correlation key '{key}' is already pending."));
            }

            ReadOnlyMemory<byte> payload;
            try
            {
                payload = _codec.Encode(request);
            }
            catch (Exception exception)
            {
                return CommunicationResult<TResponse>.Failure(new CommunicationError(
                    CommunicationErrorCode.ProtocolError,
                    exception.Message,
                    Exception: exception));
            }

            await PublishAsync(new MessageEnvelope(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                MessageDirection.Outbound,
                _channel.ChannelId,
                payload,
                Protocol: _options.ProtocolName,
                Summary: $"Request {key}")).ConfigureAwait(false);

            CommunicationResult<int> sent = await _channel.SendAsync(payload, cancellationToken)
                .ConfigureAwait(false);
            if (!sent.IsSuccess)
            {
                return CommunicationResult<TResponse>.Failure(sent.Error!);
            }

            using CancellationTokenSource timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task timeoutTask = Task.Delay(timeout, timeoutCancellation.Token);
            Task completed = await Task.WhenAny(pending.Completion.Task, timeoutTask).ConfigureAwait(false);
            if (completed == pending.Completion.Task)
            {
                timeoutCancellation.Cancel();
                return await pending.Completion.Task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CommunicationResult<TResponse>.Failure(new CommunicationError(
                CommunicationErrorCode.Timeout,
                $"No response was received within {timeout}."));
        }
        finally
        {
            if (key is not null)
            {
                _pending.TryRemove(key, out _);
            }

            _inFlightGate.Release();
        }
    }

    private void StartReceiveLoop(bool forceRestart)
    {
        lock (_receiveLock)
        {
            if (!forceRestart && _receiveTask is { IsCompleted: false })
            {
                return;
            }

            _receiveCancellation?.Cancel();
            _receiveCancellation?.Dispose();
            _receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _receiveTask = ReceiveLoopAsync(_receiveCancellation.Token);
        }
    }

    private void CancelReceiveLoop()
    {
        lock (_receiveLock)
        {
            _receiveCancellation?.Cancel();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        List<byte> buffered = [];
        try
        {
            await foreach (ReadOnlyMemory<byte> chunk in _channel.ReceiveAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                buffered.AddRange(chunk.ToArray());
                if (buffered.Count > _options.MaxBufferedBytes)
                {
                    CommunicationError overflow = new(
                        CommunicationErrorCode.ProtocolError,
                        $"Receive buffer exceeded {_options.MaxBufferedBytes} bytes.");
                    FailAllPending(overflow);
                    buffered.Clear();
                    continue;
                }

                while (buffered.Count > 0)
                {
                    ProtocolDecodeResult<TResponse> decoded = _codec.TryDecode(
                        new ReadOnlySequence<byte>(buffered.ToArray()));
                    if (decoded.Status == DecodeStatus.NeedMoreData)
                    {
                        break;
                    }

                    int consumed = checked((int)decoded.Consumed);
                    if (consumed <= 0 || consumed > buffered.Count)
                    {
                        consumed = 1;
                    }

                    byte[] rawFrame = buffered.Take(consumed).ToArray();
                    buffered.RemoveRange(0, consumed);
                    if (decoded.Status == DecodeStatus.InvalidData || decoded.Value is null)
                    {
                        CommunicationError error = decoded.Error ?? new CommunicationError(
                            CommunicationErrorCode.ProtocolError,
                            "The protocol decoder rejected a response.");
                        FailAllPending(error);
                        await PublishAsync(new MessageEnvelope(
                            Guid.NewGuid(),
                            DateTimeOffset.UtcNow,
                            MessageDirection.Inbound,
                            _channel.ChannelId,
                            rawFrame,
                            Protocol: _options.ProtocolName,
                            Summary: error.Message)).ConfigureAwait(false);
                        continue;
                    }

                    string key = _correlator.GetResponseKey(decoded.Value);
                    _pending.TryRemove(key, out PendingRequest? request);
                    TimeSpan? duration = request is null
                        ? null
                        : TimeSpan.FromSeconds(
                            (Stopwatch.GetTimestamp() - request.StartTimestamp) /
                            (double)Stopwatch.Frequency);
                    await PublishAsync(new MessageEnvelope(
                        Guid.NewGuid(),
                        DateTimeOffset.UtcNow,
                        MessageDirection.Inbound,
                        _channel.ChannelId,
                        rawFrame,
                        Protocol: _options.ProtocolName,
                        Summary: request is null ? $"Unmatched response {key}" : $"Response {key}",
                        Duration: duration)).ConfigureAwait(false);
                    request?.Completion.TrySetResult(CommunicationResult<TResponse>.Success(decoded.Value));
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                FailAllPending(new CommunicationError(
                    CommunicationErrorCode.ConnectionFailure,
                    "The receive stream ended."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailAllPending(CommunicationError.FromException(
                exception is CommunicationException
                    ? exception
                    : new ConnectionException(exception.Message, exception)));
        }
    }

    private ValueTask OnReconnectedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartReceiveLoop(forceRestart: true);
        return default;
    }

    private async ValueTask PublishAsync(MessageEnvelope message)
    {
        if (_monitor is null)
        {
            return;
        }

        try
        {
            await _monitor.PublishAsync(message, _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!_lifetimeCancellation.IsCancellationRequested)
        {
            // Observability failures must never break the primary communication path.
        }
    }

    private void FailAllPending(CommunicationError error)
    {
        foreach (KeyValuePair<string, PendingRequest> entry in _pending.ToArray())
        {
            if (_pending.TryRemove(entry.Key, out PendingRequest? request))
            {
                request.Completion.TrySetResult(CommunicationResult<TResponse>.Failure(error));
            }
        }
    }

    private void ForwardStateChanged(object? sender, ConnectionStateChangedEventArgs args) =>
        StateChanged?.Invoke(this, args);

    private static void ValidateOptions(
        CommunicationClientOptions options,
        IResponseCorrelator<TRequest, TResponse> correlator)
    {
        if (options.DefaultTimeout <= TimeSpan.Zero ||
            options.MaxBufferedBytes <= 0 ||
            correlator.MaxInFlight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ReliableCommunicationClient<TRequest, TResponse>));
        }
    }

    private sealed class PendingRequest
    {
        public PendingRequest()
        {
            StartTimestamp = Stopwatch.GetTimestamp();
            Completion = new TaskCompletionSource<CommunicationResult<TResponse>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public long StartTimestamp { get; }

        public TaskCompletionSource<CommunicationResult<TResponse>> Completion { get; }
    }
}
