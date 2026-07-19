using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Monitoring;

/// <summary>Publishes redacted message observations to bounded, non-blocking subscribers.</summary>
public sealed class MessageMonitor : IMessageMonitor, IAsyncDisposable
{
    private readonly IMessageRedactor _redactor;
    private readonly int _subscriberCapacity;
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private int _disposed;

    /// <summary>Initializes a message monitor that suppresses payloads by default.</summary>
    public MessageMonitor(IMessageRedactor? redactor = null, int subscriberCapacity = 1024)
    {
        if (subscriberCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subscriberCapacity));
        }

        _redactor = redactor ?? new SuppressPayloadMessageRedactor();
        _subscriberCapacity = subscriberCapacity;
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(
        MessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CommunicationResult<MessageEnvelope> redacted = await _redactor
            .RedactAsync(message, cancellationToken)
            .ConfigureAwait(false);
        if (!redacted.IsSuccess || redacted.Value is null)
        {
            return;
        }

        foreach (Subscription subscription in _subscriptions.Values)
        {
            if (!Matches(redacted.Value, subscription.Filter))
            {
                continue;
            }

            if (!subscription.Channel.Writer.TryWrite(redacted.Value))
            {
                subscription.Channel.Reader.TryRead(out _);
                subscription.Channel.Writer.TryWrite(redacted.Value);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MessageEnvelope> SubscribeAsync(
        MessageFilter? filter = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Guid id = Guid.NewGuid();
        Channel<MessageEnvelope> channel = Channel.CreateBounded<MessageEnvelope>(
            new BoundedChannelOptions(_subscriberCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        Subscription subscription = new(channel, filter);
        if (!_subscriptions.TryAdd(id, subscription))
        {
            throw new InvalidOperationException("Unable to create a monitor subscription.");
        }

        try
        {
            await foreach (MessageEnvelope message in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            _subscriptions.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            foreach (Subscription subscription in _subscriptions.Values)
            {
                subscription.Channel.Writer.TryComplete();
            }

            _subscriptions.Clear();
        }

        return default;
    }

    internal static bool Matches(MessageEnvelope message, MessageFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        return (!filter.From.HasValue || message.Timestamp >= filter.From.Value) &&
               (!filter.To.HasValue || message.Timestamp < filter.To.Value) &&
               (!filter.Direction.HasValue || message.Direction == filter.Direction.Value) &&
               (filter.ChannelId is null || string.Equals(
                   message.ChannelId,
                   filter.ChannelId,
                   StringComparison.Ordinal)) &&
               (filter.SessionId is null || string.Equals(
                   message.SessionId,
                   filter.SessionId,
                   StringComparison.Ordinal)) &&
               (filter.Protocol is null || string.Equals(
                   message.Protocol,
                   filter.Protocol,
                   StringComparison.OrdinalIgnoreCase));
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MessageMonitor));
        }
    }

    private sealed record Subscription(Channel<MessageEnvelope> Channel, MessageFilter? Filter);
}
