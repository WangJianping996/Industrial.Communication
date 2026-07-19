using Communication.Abstractions.Models;
using Communication.Protocols.OpcUa.Models;

namespace Communication.Protocols.OpcUa.Simulator;

/// <summary>An in-memory OPC UA session for deterministic tests and examples.</summary>
public sealed class MemoryOpcUaSession : IOpcUaSession
{
    private readonly object _sync = new();
    private readonly OpcUaConnectionOptions _options;
    private readonly Dictionary<string, OpcUaNodeValue> _values = new(StringComparer.Ordinal);
    private readonly HashSet<MemorySubscription> _subscriptions = new();
    private ConnectionState _state = ConnectionState.Disconnected;
    private int _createdSubscriptionCount;
    private bool _disposed;

    /// <summary>Initializes an in-memory session with optional node values.</summary>
    public MemoryOpcUaSession(
        OpcUaConnectionOptions options,
        IReadOnlyDictionary<string, object?>? initialValues = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (initialValues is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<string, object?> item in initialValues)
        {
            _values[item.Key] = new OpcUaNodeValue(
                item.Key, item.Value, VariableQuality.Good, now, now);
        }
    }

    /// <inheritdoc />
    public ConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets the number of subscriptions created during this session's lifetime.</summary>
    public int CreatedSubscriptionCount => Volatile.Read(ref _createdSubscriptionCount);

    /// <summary>Gets the number of active simulated subscriptions.</summary>
    public int ActiveSubscriptionCount
    {
        get
        {
            lock (_sync)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? Reconnected;

    /// <inheritdoc />
    public event EventHandler<OpcUaSecurityWarningEventArgs>? SecurityWarning;

    /// <inheritdoc />
    public ValueTask<CommunicationResult<IReadOnlyList<OpcUaEndpointDescription>>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<OpcUaEndpointDescription> endpoints = new[]
        {
            new OpcUaEndpointDescription(
                _options.EndpointUrl,
                _options.SecurityMode,
                ToSecurityPolicyUri(_options.SecurityPolicy),
                _options.SecurityMode == OpcUaMessageSecurityMode.None ? (byte)0 : (byte)128,
                new[]
                {
                    OpcUaIdentityKind.Anonymous,
                    OpcUaIdentityKind.UsernamePassword,
                    OpcUaIdentityKind.Certificate,
                }),
        };
        return new ValueTask<CommunicationResult<IReadOnlyList<OpcUaEndpointDescription>>>(
            CommunicationResult<IReadOnlyList<OpcUaEndpointDescription>>.Success(endpoints));
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            _state = ConnectionState.Connected;
        }

        if (_options.Certificates.AllowUntrustedCertificates)
        {
            SecurityWarning?.Invoke(this, new OpcUaSecurityWarningEventArgs(
                "Untrusted server certificates are explicitly allowed. Do not use this setting in production."));
        }

        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MemorySubscription[] subscriptions;
        lock (_sync)
        {
            if (_disposed)
            {
                return new ValueTask<CommunicationResult>(CommunicationResult.Success());
            }

            _state = ConnectionState.Disconnected;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (MemorySubscription subscription in subscriptions)
        {
            subscription.MarkDetached();
        }

        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CommunicationResult<OpcUaNodeValue>>> ReadAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (nodeIds is null)
        {
            throw new ArgumentNullException(nameof(nodeIds));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            CommunicationError? stateError = GetStateError();
            IReadOnlyList<CommunicationResult<OpcUaNodeValue>> results = nodeIds.Select(nodeId =>
            {
                if (stateError is not null)
                {
                    return CommunicationResult<OpcUaNodeValue>.Failure(stateError);
                }

                return _values.TryGetValue(nodeId, out OpcUaNodeValue? value)
                    ? CommunicationResult<OpcUaNodeValue>.Success(value)
                    : CommunicationResult<OpcUaNodeValue>.Failure(new CommunicationError(
                        CommunicationErrorCode.InvalidAddress,
                        $"OPC UA node '{nodeId}' does not exist."));
            }).ToArray();
            return new ValueTask<IReadOnlyList<CommunicationResult<OpcUaNodeValue>>>(results);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CommunicationResult>> WriteAsync(
        IReadOnlyList<OpcUaNodeWrite> writes,
        CancellationToken cancellationToken = default)
    {
        if (writes is null)
        {
            throw new ArgumentNullException(nameof(writes));
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<(OpcUaNodeValue Value, MemorySubscription[] Targets)> notifications = new();
        CommunicationResult[] results;
        lock (_sync)
        {
            CommunicationError? stateError = GetStateError();
            results = new CommunicationResult[writes.Count];
            for (int index = 0; index < writes.Count; index++)
            {
                OpcUaNodeWrite write = writes[index];
                if (stateError is not null)
                {
                    results[index] = CommunicationResult.Failure(stateError);
                    continue;
                }

                if (!_values.ContainsKey(write.NodeId))
                {
                    results[index] = CommunicationResult.Failure(new CommunicationError(
                        CommunicationErrorCode.InvalidAddress,
                        $"OPC UA node '{write.NodeId}' does not exist."));
                    continue;
                }

                OpcUaNodeValue value = CreateNodeValue(write.NodeId, write.Value, VariableQuality.Good, null);
                _values[write.NodeId] = value;
                results[index] = CommunicationResult.Success();
                notifications.Add((value, FindSubscribers(write.NodeId)));
            }
        }

        foreach ((OpcUaNodeValue value, MemorySubscription[] targets) in notifications)
        {
            foreach (MemorySubscription subscription in targets)
            {
                _ = subscription.PublishAsync(value);
            }
        }

        return new ValueTask<IReadOnlyList<CommunicationResult>>(results);
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult<IOpcUaSessionSubscription>> SubscribeAsync(
        IReadOnlyList<OpcUaMonitoredNode> nodes,
        TimeSpan publishingInterval,
        Func<OpcUaNodeValue, ValueTask> onValue,
        CancellationToken cancellationToken = default)
    {
        if (nodes is null)
        {
            throw new ArgumentNullException(nameof(nodes));
        }

        if (onValue is null)
        {
            throw new ArgumentNullException(nameof(onValue));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            CommunicationError? stateError = GetStateError();
            if (stateError is not null)
            {
                return new ValueTask<CommunicationResult<IOpcUaSessionSubscription>>(
                    CommunicationResult<IOpcUaSessionSubscription>.Failure(stateError));
            }

            if (nodes.Count == 0 || publishingInterval <= TimeSpan.Zero ||
                nodes.Any(node => string.IsNullOrWhiteSpace(node.NodeId) ||
                                  node.SamplingIntervalMilliseconds < 0 || node.QueueSize == 0))
            {
                return new ValueTask<CommunicationResult<IOpcUaSessionSubscription>>(
                    CommunicationResult<IOpcUaSessionSubscription>.Failure(new CommunicationError(
                        CommunicationErrorCode.InvalidValue,
                        "The OPC UA subscription settings are invalid.")));
            }

            var subscription = new MemorySubscription(this, nodes.Select(node => node.NodeId), onValue);
            _subscriptions.Add(subscription);
            Interlocked.Increment(ref _createdSubscriptionCount);
            return new ValueTask<CommunicationResult<IOpcUaSessionSubscription>>(
                CommunicationResult<IOpcUaSessionSubscription>.Success(subscription));
        }
    }

    /// <summary>Sets a simulated value and sends a data-change notification.</summary>
    public async ValueTask SetValueAsync(
        string nodeId,
        object? value,
        VariableQuality quality = VariableQuality.Good,
        CommunicationError? error = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new ArgumentException("A node identifier is required.", nameof(nodeId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        OpcUaNodeValue sample;
        MemorySubscription[] targets;
        lock (_sync)
        {
            ThrowIfDisposed();
            sample = CreateNodeValue(nodeId, value, quality, error);
            _values[nodeId] = sample;
            targets = FindSubscribers(nodeId);
        }

        foreach (MemorySubscription target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await target.PublishAsync(sample).ConfigureAwait(false);
        }
    }

    /// <summary>Simulates a recovered secure channel; server-side subscriptions are discarded first.</summary>
    public ValueTask SimulateReconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MemorySubscription[] previous;
        lock (_sync)
        {
            ThrowIfDisposed();
            _state = ConnectionState.Reconnecting;
            previous = _subscriptions.ToArray();
            _subscriptions.Clear();
            _state = ConnectionState.Connected;
        }

        foreach (MemorySubscription subscription in previous)
        {
            subscription.MarkDetached();
        }

        Reconnected?.Invoke(this, EventArgs.Empty);
        return default;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private static OpcUaNodeValue CreateNodeValue(
        string nodeId,
        object? value,
        VariableQuality quality,
        CommunicationError? error)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new OpcUaNodeValue(nodeId, value, quality, now, now, error);
    }

    private MemorySubscription[] FindSubscribers(string nodeId) =>
        _subscriptions.Where(subscription => subscription.Contains(nodeId)).ToArray();

    private CommunicationError? GetStateError()
    {
        ThrowIfDisposed();
        return _state == ConnectionState.Connected
            ? null
            : new CommunicationError(CommunicationErrorCode.InvalidState,
                "The OPC UA session is not connected.");
    }

    private void Remove(MemorySubscription subscription)
    {
        lock (_sync)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MemoryOpcUaSession));
        }
    }

    private static string ToSecurityPolicyUri(OpcUaSecurityPolicy policy) => policy switch
    {
        OpcUaSecurityPolicy.None => "http://opcfoundation.org/UA/SecurityPolicy#None",
        OpcUaSecurityPolicy.Basic256Sha256 => "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256",
        OpcUaSecurityPolicy.Aes128Sha256RsaOaep => "http://opcfoundation.org/UA/SecurityPolicy#Aes128_Sha256_RsaOaep",
        OpcUaSecurityPolicy.Aes256Sha256RsaPss => "http://opcfoundation.org/UA/SecurityPolicy#Aes256_Sha256_RsaPss",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    private sealed class MemorySubscription : IOpcUaSessionSubscription
    {
        private readonly MemoryOpcUaSession _owner;
        private readonly HashSet<string> _nodeIds;
        private readonly Func<OpcUaNodeValue, ValueTask> _onValue;
        private int _detached;

        public MemorySubscription(
            MemoryOpcUaSession owner,
            IEnumerable<string> nodeIds,
            Func<OpcUaNodeValue, ValueTask> onValue)
        {
            _owner = owner;
            _nodeIds = new HashSet<string>(nodeIds, StringComparer.Ordinal);
            _onValue = onValue;
        }

        public bool Contains(string nodeId) =>
            Volatile.Read(ref _detached) == 0 && _nodeIds.Contains(nodeId);

        public ValueTask PublishAsync(OpcUaNodeValue value) =>
            Volatile.Read(ref _detached) == 0 ? _onValue(value) : default;

        public void MarkDetached() => Interlocked.Exchange(ref _detached, 1);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _detached, 1) == 0)
            {
                _owner.Remove(this);
            }

            return default;
        }
    }
}
