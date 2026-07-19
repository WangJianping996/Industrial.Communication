using Communication.Abstractions.Models;
using Communication.Core.Queues;

namespace Communication.UnitTests;

public sealed class BoundedMessageQueueTests
{
    [Fact]
    public async Task Wait_strategy_applies_backpressure_until_a_reader_advances()
    {
        await using BoundedMessageQueue<int> queue = new(1);
        await queue.WriteAsync(1);
        Task<QueueWriteResult> blockedWrite = queue.WriteAsync(2).AsTask();

        await Task.Delay(25);
        Assert.False(blockedWrite.IsCompleted);

        await using IAsyncEnumerator<int> reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, reader.Current);
        QueueWriteResult result = await blockedWrite;

        Assert.True(result.Accepted);
        Assert.False(result.Dropped);
    }

    [Fact]
    public async Task Reject_strategy_reports_a_full_queue_without_dropping_data()
    {
        await using BoundedMessageQueue<int> queue = new(1, QueueBackpressureStrategy.Reject);
        await queue.WriteAsync(1);

        QueueWriteResult result = await queue.WriteAsync(2);
        queue.Complete();
        List<int> values = await ReadAllAsync(queue);

        Assert.False(result.Accepted);
        Assert.False(result.Dropped);
        Assert.Equal([1], values);
    }

    [Fact]
    public async Task Drop_oldest_strategy_retains_the_newest_items()
    {
        await using BoundedMessageQueue<int> queue = new(2, QueueBackpressureStrategy.DropOldest);
        await queue.WriteAsync(1);
        await queue.WriteAsync(2);

        QueueWriteResult result = await queue.WriteAsync(3);
        queue.Complete();
        List<int> values = await ReadAllAsync(queue);

        Assert.True(result.Accepted);
        Assert.True(result.Dropped);
        Assert.Equal([2, 3], values);
    }

    [Fact]
    public async Task Drop_newest_strategy_discards_the_incoming_item()
    {
        await using BoundedMessageQueue<int> queue = new(1, QueueBackpressureStrategy.DropNewest);
        await queue.WriteAsync(1);

        QueueWriteResult result = await queue.WriteAsync(2);

        Assert.False(result.Accepted);
        Assert.True(result.Dropped);
    }

    [Fact]
    public async Task Concurrent_producers_do_not_lose_items_with_wait_backpressure()
    {
        await using BoundedMessageQueue<int> queue = new(128);
        Task<List<int>> consumer = ReadAllAsync(queue);
        Task[] producers = Enumerable.Range(0, 8)
            .Select(producer => Task.Run(async () =>
            {
                for (int item = 0; item < 100; item++)
                {
                    await queue.WriteAsync((producer * 100) + item);
                }
            }))
            .ToArray();

        await Task.WhenAll(producers);
        queue.Complete();
        List<int> values = await consumer;

        Assert.Equal(800, values.Count);
        Assert.Equal(800, values.Distinct().Count());
    }

    [Fact]
    public async Task Canceling_a_blocked_writer_exits_promptly_and_preserves_the_queued_item()
    {
        await using BoundedMessageQueue<int> queue = new(1);
        await queue.WriteAsync(1);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.WriteAsync(2, cancellation.Token).AsTask());
        queue.Complete();

        Assert.Equal([1], await ReadAllAsync(queue));
    }

    private static async Task<List<T>> ReadAllAsync<T>(BoundedMessageQueue<T> queue)
    {
        List<T> values = [];
        await foreach (T value in queue.ReadAllAsync())
        {
            values.Add(value);
        }

        return values;
    }
}
