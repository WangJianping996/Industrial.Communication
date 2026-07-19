using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Communication.Abstractions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Reliability;

namespace Communication.UnitTests;

public sealed class ReliabilityTests
{
    [Fact]
    public async Task Transaction_correlator_matches_out_of_order_concurrent_responses()
    {
        await using FakeTransportChannel transport = new(async (payload, _, writer) =>
        {
            await Task.Delay((10 - payload[1]) * 4);
            await writer.WriteAsync(payload);
        });
        await using ReliableCommunicationClient<TestRequest, TestResponse> client = CreateClient(
            transport,
            new DelegatingResponseCorrelator<TestRequest, TestResponse>(
                request => request.Id.ToString(),
                response => response.Id.ToString(),
                maxInFlight: 16));
        Assert.True((await client.ConnectAsync()).IsSuccess);

        Task<CommunicationResult<TestResponse>>[] operations = Enumerable.Range(1, 8)
            .Select(id => client.ExecuteAsync(new TestRequest((byte)id, (byte)(id + 20))).AsTask())
            .ToArray();
        CommunicationResult<TestResponse>[] results = await Task.WhenAll(operations);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(
            Enumerable.Range(1, 8),
            results.Select(result => (int)result.Value!.Id));
        Assert.True(transport.MaximumScheduledResponses > 1);
    }

    [Fact]
    public async Task Single_request_correlator_serializes_requests_without_transaction_ids()
    {
        await using FakeTransportChannel transport = new(async (payload, _, writer) =>
        {
            await Task.Delay(25);
            await writer.WriteAsync(payload);
        });
        await using ReliableCommunicationClient<TestRequest, TestResponse> client = CreateClient(
            transport,
            new SingleRequestCorrelator<TestRequest, TestResponse>());
        await client.ConnectAsync();

        CommunicationResult<TestResponse>[] results = await Task.WhenAll(
            client.ExecuteAsync(new TestRequest(1, 10)).AsTask(),
            client.ExecuteAsync(new TestRequest(2, 20)).AsTask(),
            client.ExecuteAsync(new TestRequest(3, 30)).AsTask());

        Assert.All(results, result => Assert.True(result.IsSuccess));
        long[] sendTimestamps = transport.SendTimestamps.ToArray();
        Assert.Equal(3, sendTimestamps.Length);
        Assert.All(
            sendTimestamps.Zip(sendTimestamps.Skip(1)),
            pair => Assert.True(Stopwatch.GetElapsedTime(pair.First, pair.Second) >= TimeSpan.FromMilliseconds(15)));
    }

    [Fact]
    public async Task Timeout_is_structured_and_retry_can_recover_on_a_later_attempt()
    {
        await using FakeTransportChannel transport = new(async (payload, attempt, writer) =>
        {
            if (attempt == 2)
            {
                await writer.WriteAsync(payload);
            }
        });
        ExponentialBackoffRetryPolicy retry = new(
            maxAttempts: 2,
            initialDelay: TimeSpan.Zero,
            maxDelay: TimeSpan.Zero,
            jitterRatio: 0);
        await using ReliableCommunicationClient<TestRequest, TestResponse> client = CreateClient(
            transport,
            new SingleRequestCorrelator<TestRequest, TestResponse>(),
            retry);
        await client.ConnectAsync();

        CommunicationResult<TestResponse> result = await client.ExecuteAsync(
            new TestRequest(7, 42),
            new CommunicationRequestOptions { Timeout = TimeSpan.FromMilliseconds(30), EnableRetry = true });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, transport.SendCount);
        Assert.Equal((byte)42, result.Value!.Value);
    }

    [Fact]
    public async Task Request_cancellation_interrupts_a_pending_response_wait()
    {
        await using FakeTransportChannel transport = new(async (payload, attempt, writer) =>
        {
            if (attempt == 2)
            {
                await writer.WriteAsync(payload);
            }
        });
        await using ReliableCommunicationClient<TestRequest, TestResponse> client = CreateClient(
            transport,
            new SingleRequestCorrelator<TestRequest, TestResponse>());
        await client.ConnectAsync();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(
            new TestRequest(1, 2),
            new CommunicationRequestOptions { Timeout = TimeSpan.FromSeconds(2) },
            cancellation.Token).AsTask());

        CommunicationResult<TestResponse> next = await client.ExecuteAsync(new TestRequest(2, 3));
        Assert.True(next.IsSuccess);
        Assert.Equal((byte)2, next.Value!.Id);
    }

    [Fact]
    public async Task Automatic_reconnect_runs_recovery_hooks_after_connection_is_restored()
    {
        await using FakeTransportChannel transport = new();
        TaskCompletionSource<bool> recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DelegatingConnectionRecoveryHandler handler = new(_ =>
        {
            recovered.TrySetResult(true);
            return default;
        });
        await using AutomaticReconnectCoordinator coordinator = new(
            transport,
            new ExponentialBackoffReconnectPolicy(
                maxAttempts: 2,
                initialDelay: TimeSpan.Zero,
                maxDelay: TimeSpan.Zero,
                jitterRatio: 0),
            [handler]);
        await coordinator.ConnectAsync();

        transport.Fault();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ConnectionState.Connected, transport.State);
        Assert.Equal(2, transport.ConnectCount);
    }

    [Fact]
    public async Task Explicit_disconnect_suppresses_a_reconnect_already_waiting_in_backoff()
    {
        await using FakeTransportChannel transport = new();
        await using AutomaticReconnectCoordinator coordinator = new(
            transport,
            new ExponentialBackoffReconnectPolicy(
                maxAttempts: 2,
                initialDelay: TimeSpan.FromMilliseconds(150),
                maxDelay: TimeSpan.FromMilliseconds(150),
                jitterRatio: 0));
        await coordinator.ConnectAsync();

        transport.Fault();
        await coordinator.DisconnectAsync();
        await Task.Delay(225);

        Assert.Equal(ConnectionState.Disconnected, transport.State);
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task Heartbeat_reports_the_configured_consecutive_failure_threshold()
    {
        TaskCompletionSource<HeartbeatFailureEventArgs> failed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DelegatingHeartbeatStrategy strategy = new(
            TimeSpan.FromMilliseconds(10),
            _ => new ValueTask<ReadOnlyMemory<byte>>(new byte[] { 0x01 }),
            (_, _) => new ValueTask<bool>(true));
        await using HeartbeatService heartbeat = new(
            strategy,
            (_, _) => new ValueTask<CommunicationResult<ReadOnlyMemory<byte>>>(
                CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                    CommunicationErrorCode.Timeout,
                    "No heartbeat response."))),
            maxConsecutiveFailures: 2);
        heartbeat.Failed += (_, args) => failed.TrySetResult(args);

        heartbeat.Start();
        HeartbeatFailureEventArgs result = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await heartbeat.StopAsync();

        Assert.Equal(2, result.ConsecutiveFailures);
        Assert.Equal(CommunicationErrorCode.Timeout, result.Error.Code);
        Assert.False(heartbeat.IsRunning);
    }

    private static ReliableCommunicationClient<TestRequest, TestResponse> CreateClient(
        FakeTransportChannel transport,
        IResponseCorrelator<TestRequest, TestResponse> correlator,
        IRetryPolicy? retry = null) => new(
            transport,
            new TestCodec(),
            correlator,
            new CommunicationClientOptions { DefaultTimeout = TimeSpan.FromMilliseconds(500) },
            retry);

    private sealed record TestRequest(byte Id, byte Value);

    private sealed record TestResponse(byte Id, byte Value);

    private sealed class TestCodec : IProtocolCodec<TestRequest, TestResponse>
    {
        public ReadOnlyMemory<byte> Encode(TestRequest request) => new byte[] { 3, request.Id, request.Value };

        public ProtocolDecodeResult<TestResponse> TryDecode(ReadOnlySequence<byte> buffer)
        {
            if (buffer.Length < 1)
            {
                return ProtocolDecodeResult<TestResponse>.NeedMoreData(buffer.Length);
            }

            byte[] bytes = buffer.ToArray();
            int length = bytes[0];
            return buffer.Length < length
                ? ProtocolDecodeResult<TestResponse>.NeedMoreData(buffer.Length)
                : ProtocolDecodeResult<TestResponse>.Done(
                    new TestResponse(bytes[1], bytes[2]),
                    length);
        }
    }

    private sealed class FakeTransportChannel : ITransportChannel
    {
        private readonly ConnectionStateMachine _stateMachine = new();
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Func<byte[], int, ChannelWriter<ReadOnlyMemory<byte>>, Task>? _onSend;
        private readonly ConcurrentQueue<long> _sendTimestamps = new();
        private int _scheduledResponses;
        private int _maximumScheduledResponses;

        public FakeTransportChannel(
            Func<byte[], int, ChannelWriter<ReadOnlyMemory<byte>>, Task>? onSend = null)
        {
            _onSend = onSend;
            _stateMachine.StateChanged += (_, args) => StateChanged?.Invoke(this, args);
        }

        public string ChannelId => "fake";

        public ConnectionState State => _stateMachine.State;

        public int ConnectCount { get; private set; }

        public int SendCount { get; private set; }

        public int MaximumScheduledResponses => Volatile.Read(ref _maximumScheduledResponses);

        public IReadOnlyCollection<long> SendTimestamps => _sendTimestamps.ToArray();

        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

        public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            _stateMachine.TransitionTo(ConnectionState.Connecting);
            _stateMachine.TransitionTo(ConnectionState.Connected);
            return new ValueTask<CommunicationResult>(CommunicationResult.Success());
        }

        public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State is ConnectionState.Connected or ConnectionState.Faulted or ConnectionState.Connecting)
            {
                _stateMachine.TransitionTo(ConnectionState.Disconnecting);
                _stateMachine.TransitionTo(ConnectionState.Disconnected);
            }

            return new ValueTask<CommunicationResult>(CommunicationResult.Success());
        }

        public ValueTask<CommunicationResult<int>> SendAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int attempt = ++SendCount;
            _sendTimestamps.Enqueue(Stopwatch.GetTimestamp());
            if (_onSend is not null)
            {
                byte[] copy = payload.ToArray();
                _ = RunResponseAsync(copy, attempt);
            }

            return new ValueTask<CommunicationResult<int>>(
                CommunicationResult<int>.Success(payload.Length));
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (ReadOnlyMemory<byte> chunk in _incoming.Reader.ReadAllAsync(cancellationToken))
            {
                yield return chunk;
            }
        }

        public void Fault()
        {
            _stateMachine.TransitionTo(
                ConnectionState.Faulted,
                new CommunicationError(CommunicationErrorCode.ConnectionFailure, "Test fault."));
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return default;
        }

        private async Task RunResponseAsync(byte[] payload, int attempt)
        {
            int active = Interlocked.Increment(ref _scheduledResponses);
            int observed;
            while (active > (observed = Volatile.Read(ref _maximumScheduledResponses)))
            {
                if (Interlocked.CompareExchange(ref _maximumScheduledResponses, active, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await _onSend!(payload, attempt, _incoming.Writer);
            }
            finally
            {
                Interlocked.Decrement(ref _scheduledResponses);
            }
        }
    }
}
