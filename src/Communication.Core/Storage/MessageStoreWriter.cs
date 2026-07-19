using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Storage;

/// <summary>Persists monitored messages on a background path isolated from primary communication.</summary>
public sealed class MessageStoreWriter : IAsyncDisposable
{
    private readonly IMessageMonitor _monitor;
    private readonly IMessageStore _store;
    private readonly MessageFilter? _filter;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private int _disposed;

    /// <summary>Initializes a background history writer.</summary>
    public MessageStoreWriter(IMessageMonitor monitor, IMessageStore store, MessageFilter? filter = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _filter = filter;
    }

    /// <summary>Raised when persistence fails. The monitor subscription remains active.</summary>
    public event EventHandler<MessagePersistenceFailedEventArgs>? PersistenceFailed;

    /// <summary>Starts background persistence. Repeated calls are safe.</summary>
    public void Start()
    {
        ThrowIfDisposed();
        _loop ??= RunAsync(_cancellation.Token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        _cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (MessageEnvelope message in _monitor
            .SubscribeAsync(_filter, cancellationToken)
            .ConfigureAwait(false))
        {
            CommunicationResult result;
            try
            {
                result = await _store.AppendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                result = CommunicationResult.Failure(new CommunicationError(
                    CommunicationErrorCode.StorageFailure,
                    "The message store threw while appending history.",
                    exception.Message,
                    exception));
            }

            if (!result.IsSuccess)
            {
                PersistenceFailed?.Invoke(
                    this,
                    new MessagePersistenceFailedEventArgs(message, result.Error!));
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MessageStoreWriter));
        }
    }
}

/// <summary>Provides data for an isolated message persistence failure.</summary>
public sealed class MessagePersistenceFailedEventArgs : EventArgs
{
    /// <summary>Initializes a persistence failure event.</summary>
    public MessagePersistenceFailedEventArgs(MessageEnvelope message, CommunicationError error)
    {
        Message = message;
        Error = error;
    }

    /// <summary>Gets the message that could not be persisted.</summary>
    public MessageEnvelope Message { get; }

    /// <summary>Gets the storage error.</summary>
    public CommunicationError Error { get; }
}
