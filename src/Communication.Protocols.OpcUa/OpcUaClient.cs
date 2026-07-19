using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.OpcUa.Models;

namespace Communication.Protocols.OpcUa;

/// <summary>Provides unified OPC UA reads, writes and recoverable data-change subscriptions.</summary>
public sealed class OpcUaClient : IPlcClient
{
    private readonly IOpcUaSession _session;

    /// <summary>Initializes a client over a replaceable OPC UA session.</summary>
    public OpcUaClient(IOpcUaSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public ConnectionState State => _session.State;

    /// <summary>Raised when insecure certificate acceptance is explicitly exercised.</summary>
    public event EventHandler<OpcUaSecurityWarningEventArgs>? SecurityWarning
    {
        add => _session.SecurityWarning += value;
        remove => _session.SecurityWarning -= value;
    }

    /// <summary>Discovers the configured server endpoints.</summary>
    public ValueTask<CommunicationResult<IReadOnlyList<OpcUaEndpointDescription>>> DiscoverAsync(
        CancellationToken cancellationToken = default) => _session.DiscoverAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default) =>
        _session.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default) =>
        _session.DisconnectAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<VariableValue>> ReadAsync(
        VariableDefinition variable,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CommunicationResult<VariableValue>> results = await ReadAsync(
            new[] { variable }, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CommunicationResult<VariableValue>>> ReadAsync(
        IReadOnlyList<VariableDefinition> variables,
        CancellationToken cancellationToken = default)
    {
        if (variables is null)
        {
            throw new ArgumentNullException(nameof(variables));
        }

        var output = new CommunicationResult<VariableValue>?[variables.Count];
        var nodeIds = new List<string>();
        var indexes = new List<int>();
        for (int index = 0; index < variables.Count; index++)
        {
            CommunicationError? error = Validate(variables[index], VariableAccess.Read);
            if (error is null)
            {
                nodeIds.Add(variables[index].Address);
                indexes.Add(index);
            }
            else
            {
                output[index] = CommunicationResult<VariableValue>.Failure(error);
            }
        }

        IReadOnlyList<CommunicationResult<OpcUaNodeValue>> raw = await _session.ReadAsync(
            nodeIds, cancellationToken).ConfigureAwait(false);
        for (int item = 0; item < indexes.Count; item++)
        {
            int index = indexes[item];
            output[index] = item >= raw.Count
                ? Failure<VariableValue>(CommunicationErrorCode.ProtocolError,
                    "The OPC UA read result count did not match the request count.")
                : ConvertNodeValue(variables[index], raw[item]);
        }

        return output.Select((result, index) => result ?? Failure<VariableValue>(
            CommunicationErrorCode.ProtocolError,
            $"No OPC UA result was produced for '{variables[index].Name}'.")).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> WriteAsync(
        PlcWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CommunicationResult> results = await WriteAsync(
            new[] { request }, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CommunicationResult>> WriteAsync(
        IReadOnlyList<PlcWriteRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests is null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        var output = new CommunicationResult?[requests.Count];
        var writes = new List<OpcUaNodeWrite>();
        var indexes = new List<int>();
        for (int index = 0; index < requests.Count; index++)
        {
            VariableDefinition definition = requests[index].Definition;
            CommunicationError? error = Validate(definition, VariableAccess.Write);
            if (error is null)
            {
                try
                {
                    writes.Add(new OpcUaNodeWrite(definition.Address,
                        ConvertWriteValue(requests[index].Value, definition)));
                    indexes.Add(index);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    output[index] = CommunicationResult.Failure(new CommunicationError(
                        CommunicationErrorCode.InvalidValue,
                        $"Unable to convert OPC UA write '{definition.Name}'.",
                        exception.Message,
                        exception));
                }
            }
            else
            {
                output[index] = CommunicationResult.Failure(error);
            }
        }

        IReadOnlyList<CommunicationResult> raw = await _session.WriteAsync(writes, cancellationToken)
            .ConfigureAwait(false);
        for (int item = 0; item < indexes.Count; item++)
        {
            output[indexes[item]] = item < raw.Count
                ? raw[item]
                : CommunicationResult.Failure(new CommunicationError(
                    CommunicationErrorCode.ProtocolError,
                    "The OPC UA write result count did not match the request count."));
        }

        return output.Select(result => result ?? CommunicationResult.Failure(new CommunicationError(
            CommunicationErrorCode.ProtocolError,
            "No OPC UA write result was produced."))).ToArray();
    }

    /// <summary>Creates a variable subscription which automatically recreates monitored items after reconnect.</summary>
    public async ValueTask<CommunicationResult<OpcUaVariableSubscription>> SubscribeAsync(
        IReadOnlyList<VariableDefinition> variables,
        OpcUaSubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (variables is null)
        {
            throw new ArgumentNullException(nameof(variables));
        }

        options ??= new OpcUaSubscriptionOptions();
        if (variables.Count == 0 || options.PublishingInterval <= TimeSpan.Zero ||
            options.SamplingInterval < TimeSpan.Zero || options.MonitoredItemQueueSize == 0 ||
            options.UpdateQueueCapacity <= 0)
        {
            return CommunicationResult<OpcUaVariableSubscription>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "A subscription requires variables, positive intervals and positive queue sizes."));
        }

        foreach (VariableDefinition variable in variables)
        {
            CommunicationError? error = Validate(variable, VariableAccess.Read);
            if (error is not null)
            {
                return CommunicationResult<OpcUaVariableSubscription>.Failure(error);
            }
        }

        var subscription = new OpcUaVariableSubscription(_session, variables, options);
        CommunicationResult initialized = await subscription.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.IsSuccess)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            return CommunicationResult<OpcUaVariableSubscription>.Failure(initialized.Error!);
        }

        return CommunicationResult<OpcUaVariableSubscription>.Success(subscription);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _session.DisposeAsync();

    internal static CommunicationResult<VariableValue> ConvertNodeValue(
        VariableDefinition definition,
        CommunicationResult<OpcUaNodeValue> raw)
    {
        if (!raw.IsSuccess)
        {
            return CommunicationResult<VariableValue>.Failure(raw.Error!);
        }

        OpcUaNodeValue value = raw.Value!;
        try
        {
            object? converted = value.Quality == VariableQuality.Bad
                ? value.Value
                : ConvertReadValue(value.Value, definition);
            return CommunicationResult<VariableValue>.Success(new VariableValue(
                definition,
                converted,
                value.Quality,
                value.SourceTimestamp,
                value.Error));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure<VariableValue>(
                CommunicationErrorCode.InvalidValue,
                $"Unable to convert OPC UA node '{definition.Address}': {exception.Message}");
        }
    }

    private static object? ConvertReadValue(object? value, VariableDefinition definition)
    {
        if (value is null)
        {
            return null;
        }

        if (definition.DataType == PlcDataType.Bytes)
        {
            return value is byte[] bytes
                ? bytes.ToArray()
                : throw new InvalidCastException("A byte array is required.");
        }

        if (definition.Length > 1 && value is System.Collections.IEnumerable sequence && value is not string)
        {
            object?[] items = sequence.Cast<object?>().ToArray();
            if (items.Length != definition.Length)
            {
                throw new InvalidCastException($"Expected {definition.Length} array elements, received {items.Length}.");
            }

            return items.Select(item => ConvertScalar(item, definition, applyScale: true)).ToArray();
        }

        return ConvertScalar(value, definition, applyScale: true);
    }

    private static object? ConvertWriteValue(object? value, VariableDefinition definition)
    {
        if (definition.DataType == PlcDataType.Bytes)
        {
            return value is byte[] bytes
                ? bytes.ToArray()
                : throw new InvalidCastException("A byte array is required.");
        }

        if (definition.Length > 1 && value is System.Collections.IEnumerable sequence && value is not string)
        {
            object?[] items = sequence.Cast<object?>().ToArray();
            if (items.Length != definition.Length)
            {
                throw new InvalidCastException($"Expected {definition.Length} array elements, received {items.Length}.");
            }

            return items.Select(item => ConvertScalar(item, definition, applyScale: false)).ToArray();
        }

        return ConvertScalar(value, definition, applyScale: false);
    }

    private static object? ConvertScalar(object? value, VariableDefinition definition, bool applyScale)
    {
        if (value is null)
        {
            return null;
        }

        double scale = applyScale ? definition.Scale : 1d / definition.Scale;
        return definition.DataType switch
        {
            PlcDataType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            PlcDataType.Byte => applyScale
                ? definition.Scale == 1
                    ? Convert.ToByte(value, CultureInfo.InvariantCulture)
                    : Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale
                : Convert.ToByte(Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale,
                    CultureInfo.InvariantCulture),
            PlcDataType.Int16 => applyScale && definition.Scale == 1
                ? Convert.ToInt16(value, CultureInfo.InvariantCulture)
                : applyScale ? Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale
                    : Convert.ToInt16(Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale,
                        CultureInfo.InvariantCulture),
            PlcDataType.UInt16 => applyScale && definition.Scale == 1
                ? Convert.ToUInt16(value, CultureInfo.InvariantCulture)
                : applyScale ? Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale
                    : Convert.ToUInt16(Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale,
                        CultureInfo.InvariantCulture),
            PlcDataType.Int32 => applyScale && definition.Scale == 1
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : applyScale ? Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale
                    : Convert.ToInt32(Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale,
                        CultureInfo.InvariantCulture),
            PlcDataType.UInt32 => applyScale && definition.Scale == 1
                ? Convert.ToUInt32(value, CultureInfo.InvariantCulture)
                : applyScale ? Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale
                    : Convert.ToUInt32(Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale,
                        CultureInfo.InvariantCulture),
            PlcDataType.Float32 => applyScale && definition.Scale == 1
                ? Convert.ToSingle(value, CultureInfo.InvariantCulture)
                : applyScale ? Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale
                    : Convert.ToSingle(Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale,
                        CultureInfo.InvariantCulture),
            PlcDataType.Float64 => Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale,
            PlcDataType.String => Convert.ToString(value, CultureInfo.InvariantCulture),
            PlcDataType.Bytes => value is byte[] bytes ? bytes.ToArray() : throw new InvalidCastException("A byte array is required."),
            _ => throw new InvalidCastException("The OPC UA value type is unsupported."),
        };
    }

    private static CommunicationError? Validate(VariableDefinition definition, VariableAccess required)
    {
        if (definition is null || string.IsNullOrWhiteSpace(definition.Name) ||
            string.IsNullOrWhiteSpace(definition.Address) || definition.Length <= 0 ||
            definition.Scale == 0 || double.IsNaN(definition.Scale) || double.IsInfinity(definition.Scale))
        {
            return new CommunicationError(CommunicationErrorCode.InvalidValue, "The OPC UA variable definition is invalid.");
        }

        return (definition.Access & required) == 0
            ? new CommunicationError(CommunicationErrorCode.InvalidState,
                $"Variable '{definition.Name}' does not allow {required} access.")
            : null;
    }

    private static CommunicationResult<T> Failure<T>(CommunicationErrorCode code, string message) =>
        CommunicationResult<T>.Failure(new CommunicationError(code, message));
}

/// <summary>Streams unified OPC UA values and recreates monitored items after session recovery.</summary>
public sealed class OpcUaVariableSubscription : IAsyncDisposable
{
    private readonly IOpcUaSession _session;
    private readonly VariableDefinition[] _variables;
    private readonly OpcUaSubscriptionOptions _options;
    private readonly Channel<VariableValue> _updates;
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private IOpcUaSessionSubscription? _inner;
    private int _disposed;

    internal OpcUaVariableSubscription(
        IOpcUaSession session,
        IReadOnlyList<VariableDefinition> variables,
        OpcUaSubscriptionOptions options)
    {
        _session = session;
        _variables = variables.ToArray();
        _options = options;
        _updates = Channel.CreateBounded<VariableValue>(new BoundedChannelOptions(options.UpdateQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _session.Reconnected += OnReconnected;
    }

    /// <summary>Gets how many times monitored items were recreated after reconnect.</summary>
    public int RestoreCount { get; private set; }

    /// <summary>Streams data changes.</summary>
    public async IAsyncEnumerable<VariableValue> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (VariableValue update in _updates.Reader.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
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

        _session.Reconnected -= OnReconnected;
        await _restoreGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_inner is not null)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
                _inner = null;
            }
        }
        finally
        {
            _restoreGate.Release();
            _restoreGate.Dispose();
            _updates.Writer.TryComplete();
        }
    }

    internal ValueTask<CommunicationResult> InitializeAsync(CancellationToken cancellationToken) =>
        RestoreAsync(initial: true, cancellationToken);

    private void OnReconnected(object? sender, EventArgs args) => _ = RestoreAfterReconnectAsync();

    private async Task RestoreAfterReconnectAsync()
    {
        CommunicationResult result = await RestoreAsync(initial: false, CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            foreach (VariableDefinition variable in _variables)
            {
                _updates.Writer.TryWrite(new VariableValue(
                    variable, null, VariableQuality.Bad, DateTimeOffset.UtcNow, result.Error));
            }
        }
    }

    private async ValueTask<CommunicationResult> RestoreAsync(bool initial, CancellationToken cancellationToken)
    {
        await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return CommunicationResult.Failure(new CommunicationError(
                    CommunicationErrorCode.InvalidState,
                    "The OPC UA subscription is disposed."));
            }

            IOpcUaSessionSubscription? previous = _inner;
            CommunicationResult<IOpcUaSessionSubscription> created = await _session.SubscribeAsync(
                _variables.Select(variable => new OpcUaMonitoredNode(
                    variable.Address,
                    _options.SamplingInterval.TotalMilliseconds,
                    _options.MonitoredItemQueueSize)).ToArray(),
                _options.PublishingInterval,
                PublishAsync,
                cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                return CommunicationResult.Failure(created.Error!);
            }

            _inner = created.Value!;
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            if (!initial)
            {
                RestoreCount++;
            }

            return CommunicationResult.Success();
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    private ValueTask PublishAsync(OpcUaNodeValue raw)
    {
        foreach (VariableDefinition definition in _variables.Where(variable =>
                     string.Equals(variable.Address, raw.NodeId, StringComparison.Ordinal)))
        {
            CommunicationResult<VariableValue> converted = OpcUaClient.ConvertNodeValue(
                definition,
                CommunicationResult<OpcUaNodeValue>.Success(raw));
            VariableValue update = converted.IsSuccess
                ? converted.Value!
                : new VariableValue(definition, null, VariableQuality.Bad, DateTimeOffset.UtcNow, converted.Error);
            _updates.Writer.TryWrite(update);
        }

        return default;
    }
}
