using System.Runtime.CompilerServices;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Plc;

namespace Communication.UnitTests;

public sealed class VariableMonitorTests
{
    [Fact]
    public async Task Publishes_changes_and_isolates_one_variable_failure()
    {
        VariableDefinition good = new("Good", "D0", PlcDataType.Int16);
        VariableDefinition bad = new("Bad", "invalid", PlcDataType.Int16);
        await using var monitor = new VariableMonitor(new SequencePlcClient());
        CommunicationResult started = await monitor.StartAsync(
            [good, bad],
            new VariableMonitorOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        List<VariableValue> updates = [];
        await foreach (VariableValue update in monitor.WatchAsync(timeout.Token))
        {
            updates.Add(update);
            if (updates.Count >= 3)
            {
                break;
            }
        }

        await monitor.StopAsync();
        Assert.True(started.IsSuccess);
        Assert.Contains(updates, value => value.Definition.Name == "Bad" && value.Quality == VariableQuality.Bad);
        Assert.Equal([1, 2], updates
            .Where(value => value.Definition.Name == "Good")
            .Select(value => Assert.IsType<int>(value.Value))
            .ToArray());
    }

    private sealed class SequencePlcClient : IPlcClient
    {
        private int _cycle;

        public ConnectionState State => ConnectionState.Connected;

        public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default) =>
            new(CommunicationResult.Success());

        public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default) =>
            new(CommunicationResult.Success());

        public ValueTask<CommunicationResult<VariableValue>> ReadAsync(
            VariableDefinition variable,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<CommunicationResult<VariableValue>>> ReadAsync(
            IReadOnlyList<VariableDefinition> variables,
            CancellationToken cancellationToken = default)
        {
            int value = Interlocked.Increment(ref _cycle) == 1 ? 1 : 2;
            IReadOnlyList<CommunicationResult<VariableValue>> results =
            [
                CommunicationResult<VariableValue>.Success(new VariableValue(
                    variables[0], value, VariableQuality.Good, DateTimeOffset.UtcNow)),
                CommunicationResult<VariableValue>.Failure(new CommunicationError(
                    CommunicationErrorCode.InvalidAddress, "bad address")),
            ];
            return new ValueTask<IReadOnlyList<CommunicationResult<VariableValue>>>(results);
        }

        public ValueTask<CommunicationResult> WriteAsync(
            PlcWriteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<CommunicationResult>> WriteAsync(
            IReadOnlyList<PlcWriteRequest> requests,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
