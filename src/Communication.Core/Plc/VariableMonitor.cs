using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Plc;

/// <summary>Polls mapped PLC variables and publishes changed values with isolated failures.</summary>
public sealed class VariableMonitor : IVariableMonitor
{
    private readonly IPlcClient _client;
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cancellation;
    private Channel<VariableValue>? _updates;
    private Task? _loop;
    private int _disposed;

    /// <summary>Initializes a variable monitor without taking ownership of the PLC client.</summary>
    public VariableMonitor(IPlcClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <inheritdoc />
    public ValueTask<CommunicationResult> StartAsync(
        IReadOnlyList<VariableDefinition> variables,
        VariableMonitorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (variables is null)
        {
            throw new ArgumentNullException(nameof(variables));
        }

        options ??= new VariableMonitorOptions();
        if (variables.Count == 0 || options.PollInterval <= TimeSpan.Zero || options.QueueCapacity <= 0)
        {
            return new ValueTask<CommunicationResult>(CommunicationResult.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "At least one variable, a positive poll interval, and a positive queue capacity are required.")));
        }

        lock (_syncRoot)
        {
            if (IsRunning)
            {
                return new ValueTask<CommunicationResult>(CommunicationResult.Failure(new CommunicationError(
                    CommunicationErrorCode.InvalidState,
                    "The variable monitor is already running.")));
            }

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _updates = Channel.CreateBounded<VariableValue>(new BoundedChannelOptions(options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
            _loop = RunAsync(variables.ToArray(), options, _updates, _cancellation.Token);
        }

        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loop;
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            loop = _loop;
            cancellation = _cancellation;
            _loop = null;
            _cancellation = null;
        }

        if (loop is null || cancellation is null)
        {
            return CommunicationResult.Success();
        }

        cancellation.Cancel();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        cancellation.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        return CommunicationResult.Success();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<VariableValue> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<VariableValue> updates = _updates ??
            throw new InvalidOperationException("The variable monitor has not been started.");
        await foreach (VariableValue update in updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _updates?.Writer.TryComplete();
    }

    private async Task RunAsync(
        VariableDefinition[] variables,
        VariableMonitorOptions options,
        Channel<VariableValue> updates,
        CancellationToken cancellationToken)
    {
        Dictionary<string, VariableValue> previous = new(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<CommunicationResult<VariableValue>> results;
                try
                {
                    results = await _client.ReadAsync(variables, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    CommunicationError error = new(
                        CommunicationErrorCode.Unknown,
                        "The PLC batch read threw an exception.",
                        exception.Message,
                        exception);
                    results = variables.Select(variable =>
                        CommunicationResult<VariableValue>.Failure(error)).ToArray();
                }

                for (int index = 0; index < variables.Length; index++)
                {
                    VariableDefinition variable = variables[index];
                    CommunicationResult<VariableValue>? result = index < results.Count ? results[index] : null;
                    VariableValue current = result is { IsSuccess: true }
                        ? result.Value!
                        : new VariableValue(
                            variable,
                            null,
                            VariableQuality.Bad,
                            DateTimeOffset.UtcNow,
                            result?.Error ?? new CommunicationError(
                                CommunicationErrorCode.ProtocolError,
                                "The PLC batch result count did not match the request count."));
                    bool changed = !previous.TryGetValue(variable.Name, out VariableValue? prior) ||
                                   !AreEqual(prior, current);
                    previous[variable.Name] = current;
                    if (changed || options.PublishUnchangedValues)
                    {
                        updates.Writer.TryWrite(current);
                    }
                }

                await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            updates.Writer.TryComplete();
        }
    }

    private static bool AreEqual(VariableValue left, VariableValue right)
    {
        if (left.Quality != right.Quality || left.Error?.Code != right.Error?.Code)
        {
            return false;
        }

        if (left.Value is Array leftArray && right.Value is Array rightArray)
        {
            return leftArray.Cast<object?>().SequenceEqual(rightArray.Cast<object?>());
        }

        return Equals(left.Value, right.Value);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(VariableMonitor));
        }
    }
}
