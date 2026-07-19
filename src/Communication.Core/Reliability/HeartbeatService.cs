using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Reliability;

/// <summary>Runs protocol-specific heartbeat exchanges without owning the transport receive loop.</summary>
public sealed class HeartbeatService : IAsyncDisposable
{
    private readonly IHeartbeatStrategy _strategy;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<CommunicationResult<ReadOnlyMemory<byte>>>> _exchange;
    private readonly int _maxConsecutiveFailures;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private int _disposed;

    /// <summary>Initializes a heartbeat service.</summary>
    public HeartbeatService(
        IHeartbeatStrategy strategy,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<CommunicationResult<ReadOnlyMemory<byte>>>> exchange,
        int maxConsecutiveFailures = 3)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
        if (strategy.Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(strategy));
        }

        if (maxConsecutiveFailures <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConsecutiveFailures));
        }

        _maxConsecutiveFailures = maxConsecutiveFailures;
    }

    /// <summary>Raised when consecutive failures reach the configured threshold.</summary>
    public event EventHandler<HeartbeatFailureEventArgs>? Failed;

    /// <summary>Gets whether the heartbeat loop is active.</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Starts the heartbeat loop. Repeated calls are safe.</summary>
    public void Start()
    {
        ThrowIfDisposed();
        if (IsRunning)
        {
            return;
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _loop = RunAsync(_cancellation.Token);
    }

    /// <summary>Stops the heartbeat loop.</summary>
    public async ValueTask StopAsync()
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? loop = _loop;
        if (cancellation is null || loop is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        cancellation.Dispose();
        _cancellation = null;
        _loop = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        while (true)
        {
            await Task.Delay(_strategy.Interval, cancellationToken).ConfigureAwait(false);
            CommunicationResult<ReadOnlyMemory<byte>> response;
            bool valid;
            try
            {
                ReadOnlyMemory<byte> request = await _strategy.CreateRequestAsync(cancellationToken)
                    .ConfigureAwait(false);
                response = await _exchange(request, cancellationToken).ConfigureAwait(false);
                valid = response.IsSuccess && await _strategy
                    .IsResponseValidAsync(response.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                response = CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                    CommunicationErrorCode.ConnectionFailure,
                    "The heartbeat exchange failed.",
                    exception.Message,
                    exception));
                valid = false;
            }

            if (valid)
            {
                consecutiveFailures = 0;
                continue;
            }

            consecutiveFailures++;
            if (consecutiveFailures < _maxConsecutiveFailures)
            {
                continue;
            }

            CommunicationError error = response.Error ?? new CommunicationError(
                CommunicationErrorCode.ProtocolError,
                "Heartbeat response validation failed.");
            Failed?.Invoke(this, new HeartbeatFailureEventArgs(consecutiveFailures, error));
            consecutiveFailures = 0;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(HeartbeatService));
        }
    }
}

/// <summary>Provides data for a heartbeat failure threshold event.</summary>
public sealed class HeartbeatFailureEventArgs : EventArgs
{
    /// <summary>Initializes a heartbeat failure event.</summary>
    public HeartbeatFailureEventArgs(int consecutiveFailures, CommunicationError error)
    {
        ConsecutiveFailures = consecutiveFailures;
        Error = error;
    }

    /// <summary>Gets the number of consecutive failed exchanges.</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>Gets the latest heartbeat error.</summary>
    public CommunicationError Error { get; }
}

/// <summary>Adapts delegates to a protocol-specific heartbeat strategy.</summary>
public sealed class DelegatingHeartbeatStrategy : IHeartbeatStrategy
{
    private readonly Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>> _createRequest;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> _validateResponse;

    /// <summary>Initializes a heartbeat strategy.</summary>
    public DelegatingHeartbeatStrategy(
        TimeSpan interval,
        Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>> createRequest,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> validateResponse)
    {
        Interval = interval;
        _createRequest = createRequest ?? throw new ArgumentNullException(nameof(createRequest));
        _validateResponse = validateResponse ?? throw new ArgumentNullException(nameof(validateResponse));
    }

    /// <inheritdoc />
    public TimeSpan Interval { get; }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> CreateRequestAsync(CancellationToken cancellationToken = default) =>
        _createRequest(cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> IsResponseValidAsync(
        ReadOnlyMemory<byte> response,
        CancellationToken cancellationToken = default) =>
        _validateResponse(response, cancellationToken);
}
