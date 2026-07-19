using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Queues;

/// <summary>Implements a bounded, asynchronous message queue with explicit backpressure.</summary>
/// <typeparam name="T">The queued item type.</typeparam>
public sealed class BoundedMessageQueue<T> : IMessageQueue<T>
{
    private readonly Channel<T> _channel;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    /// <summary>Initializes a bounded queue.</summary>
    public BoundedMessageQueue(
        int capacity,
        QueueBackpressureStrategy backpressureStrategy = QueueBackpressureStrategy.Wait,
        bool singleReader = false)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        Capacity = capacity;
        BackpressureStrategy = backpressureStrategy;
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = singleReader,
            SingleWriter = false,
        });
    }

    /// <inheritdoc />
    public int Capacity { get; }

    /// <inheritdoc />
    public QueueBackpressureStrategy BackpressureStrategy { get; }

    /// <inheritdoc />
    public async ValueTask<QueueWriteResult> WriteAsync(
        T item,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (BackpressureStrategy == QueueBackpressureStrategy.Wait)
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return new QueueWriteResult(true, false);
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_channel.Writer.TryWrite(item))
            {
                return new QueueWriteResult(true, false);
            }

            if (BackpressureStrategy == QueueBackpressureStrategy.Reject)
            {
                return new QueueWriteResult(false, false);
            }

            if (BackpressureStrategy == QueueBackpressureStrategy.DropNewest)
            {
                return new QueueWriteResult(false, true);
            }

            bool dropped = _channel.Reader.TryRead(out _);
            if (!_channel.Writer.TryWrite(item))
            {
                return new QueueWriteResult(false, dropped);
            }

            return new QueueWriteResult(true, dropped);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposedForRead();
        await foreach (T item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <inheritdoc />
    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Writer.TryComplete();
            _writeGate.Dispose();
        }

        return default;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BoundedMessageQueue<T>));
        }
    }

    private void ThrowIfDisposedForRead()
    {
        if (Volatile.Read(ref _disposed) != 0 && !_channel.Reader.TryPeek(out _))
        {
            throw new ObjectDisposedException(nameof(BoundedMessageQueue<T>));
        }
    }
}
