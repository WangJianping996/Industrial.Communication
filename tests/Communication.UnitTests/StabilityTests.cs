using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Queues;
using Communication.Core.Reliability;

namespace Communication.UnitTests;

public sealed class StabilityTests
{
    [Fact]
    public async Task Bounded_queue_survives_sustained_concurrent_producers_without_loss_or_leak()
    {
        const int producerCount = 8;
        const int itemsPerProducer = 500;
        await using var queue = new BoundedMessageQueue<int>(37, QueueBackpressureStrategy.Wait);
        var received = new ConcurrentDictionary<int, byte>();
        Task consumer = Task.Run(async () =>
        {
            await foreach (int value in queue.ReadAllAsync())
            {
                Assert.True(received.TryAdd(value, 0), $"Duplicate queue value {value}.");
            }
        });

        Task[] producers = Enumerable.Range(0, producerCount).Select(producer => Task.Run(async () =>
        {
            for (int index = 0; index < itemsPerProducer; index++)
            {
                QueueWriteResult result = await queue.WriteAsync((producer * itemsPerProducer) + index);
                Assert.True(result.Accepted);
                Assert.False(result.Dropped);
            }
        })).ToArray();
        await Task.WhenAll(producers);
        queue.Complete();
        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(producerCount * itemsPerProducer, received.Count);
    }

    [Fact]
    public async Task Reconnect_storm_is_coalesced_and_dispose_removes_the_event_subscription()
    {
        await using var channel = new StormTransportChannel();
        var coordinator = new AutomaticReconnectCoordinator(
            channel,
            new ExponentialBackoffReconnectPolicy(
                maxAttempts: 2,
                initialDelay: TimeSpan.Zero,
                maxDelay: TimeSpan.Zero,
                jitterRatio: 0));
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Reconnected += (_, _) => reconnected.TrySetResult();
        await coordinator.ConnectAsync();

        channel.RaiseFaultStorm(100);
        await channel.ReconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, channel.ConnectCount);
        channel.AllowReconnect.TrySetResult();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, channel.ConnectCount);

        await coordinator.DisposeAsync();
        int before = channel.ConnectCount;
        channel.RaiseFaultStorm(10);
        await Task.Delay(50);
        Assert.Equal(before, channel.ConnectCount);
    }

    private sealed class StormTransportChannel : ITransportChannel
    {
        private ConnectionState _state;
        private int _connectCount;

        public string ChannelId => "storm";

        public ConnectionState State => _state;

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public TaskCompletionSource ReconnectEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowReconnect { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

        public async ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
        {
            int attempt = Interlocked.Increment(ref _connectCount);
            if (attempt > 1)
            {
                ReconnectEntered.TrySetResult();
                await AllowReconnect.Task.WaitAsync(cancellationToken);
            }

            _state = ConnectionState.Connected;
            return CommunicationResult.Success();
        }

        public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = ConnectionState.Disconnected;
            return new ValueTask<CommunicationResult>(CommunicationResult.Success());
        }

        public ValueTask<CommunicationResult<int>> SendAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) =>
            new(CommunicationResult<int>.Success(payload.Length));

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => default;

        public void RaiseFaultStorm(int count)
        {
            _state = ConnectionState.Faulted;
            var error = new CommunicationError(
                CommunicationErrorCode.ConnectionFailure,
                "Injected reconnect storm.");
            for (int index = 0; index < count; index++)
            {
                StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(
                    ConnectionState.Connected,
                    ConnectionState.Faulted,
                    DateTimeOffset.UtcNow,
                    error));
            }
        }
    }
}
