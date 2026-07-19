using System.Diagnostics;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Reliability;

/// <summary>Coordinates automatic channel reconnection and post-reconnect recovery hooks.</summary>
public sealed class AutomaticReconnectCoordinator : IAsyncDisposable
{
    private readonly ITransportChannel _channel;
    private readonly IReconnectPolicy _policy;
    private readonly IReadOnlyList<IConnectionRecoveryHandler> _recoveryHandlers;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _reconnectRunning;
    private int _userDisconnect;
    private int _disposed;

    /// <summary>Initializes and starts a reconnection coordinator.</summary>
    public AutomaticReconnectCoordinator(
        ITransportChannel channel,
        IReconnectPolicy policy,
        IEnumerable<IConnectionRecoveryHandler>? recoveryHandlers = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _recoveryHandlers = recoveryHandlers?.ToArray() ?? [];
        _channel.StateChanged += OnChannelStateChanged;
    }

    /// <summary>Raised after the channel and all recovery hooks have completed.</summary>
    public event EventHandler? Reconnected;

    /// <summary>Connects explicitly and enables future automatic reconnects.</summary>
    public async ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _userDisconnect, 0);
        return await _channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disconnects explicitly and suppresses automatic reconnection.</summary>
    public async ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _userDisconnect, 1);
        return await _channel.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.StateChanged -= OnChannelStateChanged;
        _lifetimeCancellation.Cancel();
        Interlocked.Exchange(ref _userDisconnect, 1);
        await _channel.DisconnectAsync().ConfigureAwait(false);
        _lifetimeCancellation.Dispose();
    }

    private void OnChannelStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        if (args.CurrentState == ConnectionState.Faulted && Volatile.Read(ref _userDisconnect) == 0)
        {
            _ = RunReconnectLoopAsync(args.Error ?? new CommunicationError(
                CommunicationErrorCode.ConnectionFailure,
                "The channel entered a faulted state."));
        }
    }

    private async Task RunReconnectLoopAsync(CommunicationError initialError)
    {
        if (Interlocked.CompareExchange(ref _reconnectRunning, 1, 0) != 0)
        {
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        CommunicationError error = initialError;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                CancellationToken cancellationToken = _lifetimeCancellation.Token;
                if (Volatile.Read(ref _userDisconnect) != 0 || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                RetryDecision decision = await _policy.GetDecisionAsync(
                    new ReconnectContext(attempt, stopwatch.Elapsed, error, false),
                    cancellationToken).ConfigureAwait(false);
                if (!decision.ShouldRetry)
                {
                    return;
                }

                await Task.Delay(decision.Delay, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _userDisconnect) != 0)
                {
                    return;
                }

                CommunicationResult result = await _channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    error = result.Error!;
                    continue;
                }

                bool recoveryFailed = false;
                foreach (IConnectionRecoveryHandler handler in _recoveryHandlers)
                {
                    try
                    {
                        await handler.OnReconnectedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        error = new CommunicationError(
                            CommunicationErrorCode.ConnectionFailure,
                            "Post-reconnect recovery failed.",
                            exception.Message,
                            exception);
                        await _channel.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                        recoveryFailed = true;
                        break;
                    }
                }

                if (recoveryFailed)
                {
                    continue;
                }

                Reconnected?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectRunning, 0);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AutomaticReconnectCoordinator));
        }
    }
}
