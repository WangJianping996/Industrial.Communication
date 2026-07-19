using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Export;
using Communication.Core.Monitoring;
using Communication.Core.Replay;
using Communication.Core.Storage;

namespace Communication.UnitTests;

public sealed class ObservabilityTests
{
    [Fact]
    public async Task Monitor_suppresses_raw_payload_by_default()
    {
        await using MessageMonitor monitor = new();
        await using IAsyncEnumerator<MessageEnvelope> subscriber = monitor
            .SubscribeAsync()
            .GetAsyncEnumerator();
        Task<bool> received = subscriber.MoveNextAsync().AsTask();

        await monitor.PublishAsync(CreateMessage(payload: [1, 2, 3]));

        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Empty(subscriber.Current.Payload.ToArray());
        Assert.True(subscriber.Current.IsRedacted);
    }

    [Fact]
    public async Task Rule_based_redaction_matches_protocol_direction_address_and_byte_range()
    {
        RuleBasedMessageRedactor redactor = new(
            [
                new MessageRedactionRule
                {
                    Protocol = "modbus",
                    Direction = MessageDirection.Outbound,
                    AddressFrom = 100,
                    AddressTo = 200,
                    ByteRanges = [new ByteRedactionRange(1, 2)],
                    MetadataFields = ["credential"],
                },
            ],
            fallback: new PassThroughMessageRedactor(),
            mask: 0xEE);
        MessageEnvelope source = CreateMessage(
            payload: [0x01, 0x02, 0x03, 0x04],
            protocol: "Modbus",
            metadata: new Dictionary<string, string>
            {
                ["address"] = "120",
                ["credential"] = "secret",
            });

        CommunicationResult<MessageEnvelope> result = await redactor.RedactAsync(source);

        Assert.True(result.IsSuccess);
        Assert.Equal([0x01, 0xEE, 0xEE, 0x04], result.Value!.Payload.ToArray());
        Assert.True(result.Value.IsRedacted);
        Assert.Equal("***", result.Value.Metadata!["credential"]);
        Assert.Equal("120", result.Value.Metadata["address"]);
    }

    [Fact]
    public async Task Monitor_filter_only_emits_matching_messages()
    {
        await using MessageMonitor monitor = new(new PassThroughMessageRedactor());
        await using IAsyncEnumerator<MessageEnvelope> subscriber = monitor.SubscribeAsync(
            new MessageFilter
            {
                Direction = MessageDirection.Inbound,
                ChannelId = "line-2",
                Protocol = "mc",
            }).GetAsyncEnumerator();
        Task<bool> received = subscriber.MoveNextAsync().AsTask();

        await monitor.PublishAsync(CreateMessage(channelId: "line-1", protocol: "mc"));
        await monitor.PublishAsync(CreateMessage(
            direction: MessageDirection.Inbound,
            channelId: "line-2",
            protocol: "MC"));

        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("line-2", subscriber.Current.ChannelId);
    }

    [Fact]
    public async Task File_store_rolls_files_and_queries_filtered_history()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await using FileMessageStore store = new(new FileMessageStoreOptions
            {
                DirectoryPath = directory,
                RollFileSizeBytes = 100,
                MaxTotalSizeBytes = 10_000,
                RetentionPeriod = TimeSpan.FromDays(1),
            });
            Assert.True((await store.AppendAsync(CreateMessage(
                direction: MessageDirection.Outbound,
                channelId: "line-1",
                payload: Enumerable.Repeat((byte)1, 128).ToArray()))).IsSuccess);
            Assert.True((await store.AppendAsync(CreateMessage(
                direction: MessageDirection.Inbound,
                channelId: "line-2",
                payload: Enumerable.Repeat((byte)2, 128).ToArray()))).IsSuccess);

            List<MessageEnvelope> messages = await ReadAllAsync(store.QueryAsync(new MessageFilter
            {
                Direction = MessageDirection.Inbound,
                ChannelId = "line-2",
            }));

            Assert.Equal(2, Directory.GetFiles(directory, "messages-*.jsonl").Length);
            MessageEnvelope message = Assert.Single(messages);
            Assert.Equal(Enumerable.Repeat((byte)2, 128), message.Payload.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Background_store_failure_is_reported_without_failing_monitor_publish()
    {
        await using MessageMonitor monitor = new(new PassThroughMessageRedactor());
        FailingMessageStore store = new();
        await using MessageStoreWriter writer = new(monitor, store);
        TaskCompletionSource<MessagePersistenceFailedEventArgs> failed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        writer.PersistenceFailed += (_, args) => failed.TrySetResult(args);
        writer.Start();

        Stopwatch elapsed = Stopwatch.StartNew();
        await monitor.PublishAsync(CreateMessage());
        elapsed.Stop();
        MessagePersistenceFailedEventArgs result = await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(100));
        Assert.Equal(CommunicationErrorCode.StorageFailure, result.Error.Code);
    }

    [Fact]
    public async Task Json_export_excludes_payload_unless_explicitly_enabled()
    {
        MessageExporter exporter = new();
        using MemoryStream destination = new();

        CommunicationResult<long> result = await exporter.ExportAsync(
            AsAsyncEnumerable([CreateMessage(payload: [0xDE, 0xAD])]),
            destination,
            new MessageExportOptions("json"));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        destination.Position = 0;
        using JsonDocument json = await JsonDocument.ParseAsync(destination);
        Assert.False(json.RootElement[0].TryGetProperty("payload", out _));
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task Csv_export_includes_payload_and_escapes_text_when_requested()
    {
        MessageExporter exporter = new();
        using MemoryStream destination = new();
        MessageEnvelope message = CreateMessage(payload: [1, 2, 3]) with { Summary = "value, \"quoted\"" };

        CommunicationResult<long> result = await exporter.ExportAsync(
            AsAsyncEnumerable([message]),
            destination,
            new MessageExportOptions("CSV", IncludePayload: true));
        string csv = Encoding.UTF8.GetString(destination.ToArray());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Contains("AQID", csv);
        Assert.Contains("\"value, \"\"quoted\"\"\"", csv);
    }

    [Fact]
    public async Task Replay_filters_direction_and_applies_fixed_intervals()
    {
        MessageReplayService replay = new();
        MessageEnvelope[] source =
        [
            CreateMessage(direction: MessageDirection.Outbound),
            CreateMessage(direction: MessageDirection.Inbound),
            CreateMessage(direction: MessageDirection.Outbound),
        ];
        Stopwatch elapsed = Stopwatch.StartNew();

        List<MessageEnvelope> messages = await ReadAllAsync(replay.ReplayAsync(
            AsAsyncEnumerable(source),
            new MessageReplayOptions
            {
                TimingMode = ReplayTimingMode.FixedInterval,
                FixedInterval = TimeSpan.FromMilliseconds(25),
                Direction = MessageDirection.Outbound,
            }));
        elapsed.Stop();

        Assert.Equal(2, messages.Count);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(15));
    }

    [Fact]
    public async Task Replay_original_intervals_support_speed_multiplier_and_cancellation()
    {
        MessageReplayService replay = new();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        MessageEnvelope[] source =
        [
            CreateMessage() with { Timestamp = timestamp },
            CreateMessage() with { Timestamp = timestamp.AddSeconds(10) },
        ];
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (MessageEnvelope _ in replay.ReplayAsync(
                AsAsyncEnumerable(source),
                new MessageReplayOptions { Speed = 2 },
                cancellation.Token))
            {
            }
        });
    }

    private static MessageEnvelope CreateMessage(
        MessageDirection direction = MessageDirection.Outbound,
        string channelId = "line-1",
        byte[]? payload = null,
        string? protocol = "modbus",
        IReadOnlyDictionary<string, string>? metadata = null) => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            direction,
            channelId,
            payload ?? [0x01],
            SessionId: "session-1",
            Protocol: protocol,
            Summary: "test",
            Duration: TimeSpan.FromMilliseconds(12),
            Metadata: metadata);

    private static async IAsyncEnumerable<MessageEnvelope> AsAsyncEnumerable(
        IEnumerable<MessageEnvelope> messages)
    {
        foreach (MessageEnvelope message in messages)
        {
            await Task.Yield();
            yield return message;
        }
    }

    private static async Task<List<MessageEnvelope>> ReadAllAsync(
        IAsyncEnumerable<MessageEnvelope> messages)
    {
        List<MessageEnvelope> result = [];
        await foreach (MessageEnvelope message in messages)
        {
            result.Add(message);
        }

        return result;
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "IndustrialCommunicationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FailingMessageStore : IMessageStore
    {
        public async ValueTask<CommunicationResult> AppendAsync(
            MessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(25, cancellationToken);
            return CommunicationResult.Failure(new CommunicationError(
                CommunicationErrorCode.StorageFailure,
                "Test storage failure."));
        }

        public async IAsyncEnumerable<MessageEnvelope> QueryAsync(
            MessageFilter filter,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
