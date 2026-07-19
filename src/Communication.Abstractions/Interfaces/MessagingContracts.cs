using Communication.Abstractions.Models;

namespace Communication.Abstractions.Interfaces;

/// <summary>Defines a bounded asynchronous queue with explicit close semantics.</summary>
/// <typeparam name="T">The queued item type.</typeparam>
public interface IMessageQueue<T> : IAsyncDisposable
{
    /// <summary>Gets the configured maximum number of buffered items.</summary>
    int Capacity { get; }

    /// <summary>Gets the behavior used when the queue is full.</summary>
    QueueBackpressureStrategy BackpressureStrategy { get; }

    /// <summary>Writes one item according to the configured backpressure strategy.</summary>
    ValueTask<QueueWriteResult> WriteAsync(T item, CancellationToken cancellationToken = default);

    /// <summary>Streams queued items until the queue is completed.</summary>
    IAsyncEnumerable<T> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Prevents further writes and lets readers drain accepted items.</summary>
    void Complete(Exception? error = null);
}

/// <summary>Publishes and subscribes to live message observations.</summary>
public interface IMessageMonitor
{
    /// <summary>Publishes one message without blocking the primary I/O path indefinitely.</summary>
    ValueTask PublishAsync(MessageEnvelope message, CancellationToken cancellationToken = default);

    /// <summary>Streams matching messages observed after subscription.</summary>
    IAsyncEnumerable<MessageEnvelope> SubscribeAsync(
        MessageFilter? filter = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Persists and queries message history.</summary>
public interface IMessageStore
{
    /// <summary>Appends one message to history.</summary>
    ValueTask<CommunicationResult> AppendAsync(
        MessageEnvelope message,
        CancellationToken cancellationToken = default);

    /// <summary>Streams matching messages in timestamp order.</summary>
    IAsyncEnumerable<MessageEnvelope> QueryAsync(
        MessageFilter filter,
        CancellationToken cancellationToken = default);
}

/// <summary>Exports monitored or stored messages.</summary>
public interface IMessageExporter
{
    /// <summary>Writes messages to a caller-owned destination stream.</summary>
    ValueTask<CommunicationResult<long>> ExportAsync(
        IAsyncEnumerable<MessageEnvelope> messages,
        Stream destination,
        MessageExportOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Replays recorded messages according to a timing policy.</summary>
public interface IMessageReplayService
{
    /// <summary>Streams messages at their configured replay times.</summary>
    IAsyncEnumerable<MessageEnvelope> ReplayAsync(
        IAsyncEnumerable<MessageEnvelope> messages,
        MessageReplayOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Masks sensitive message content before monitoring or persistence.</summary>
public interface IMessageRedactor
{
    /// <summary>Returns a safe message or a failure when recording must be suppressed.</summary>
    ValueTask<CommunicationResult<MessageEnvelope>> RedactAsync(
        MessageEnvelope message,
        CancellationToken cancellationToken = default);
}
