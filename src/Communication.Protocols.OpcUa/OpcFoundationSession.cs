using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Communication.Abstractions.Models;
using Communication.Protocols.OpcUa.Models;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using UaMessageSecurityMode = Opc.Ua.MessageSecurityMode;

namespace Communication.Protocols.OpcUa;

/// <summary>Implements an OPC UA session with the official OPC Foundation .NET Standard SDK.</summary>
public sealed class OpcFoundationSession : IOpcUaSession
{
    private readonly OpcUaConnectionOptions _options;
    private readonly ITelemetryContext _telemetry;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private ApplicationConfiguration? _configuration;
    private ISession? _session;
    private ConnectionState _state = ConnectionState.Disconnected;
    private int _reconnectStarted;
    private bool _disposed;

    /// <summary>Initializes an official OPC Foundation SDK session.</summary>
    public OpcFoundationSession(OpcUaConnectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.EndpointUrl))
        {
            throw new ArgumentException("An OPC UA endpoint URL is required.", nameof(options));
        }

        if (options.SessionTimeout <= TimeSpan.Zero || options.ReconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException("Positive OPC UA session and reconnect timeouts are required.", nameof(options));
        }

        _telemetry = DefaultTelemetry.Create(_ => { });
    }

    /// <inheritdoc />
    public ConnectionState State => _state;

    /// <inheritdoc />
    public event EventHandler? Reconnected;

    /// <inheritdoc />
    public event EventHandler<OpcUaSecurityWarningEventArgs>? SecurityWarning;

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<IReadOnlyList<OpcUaEndpointDescription>>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplicationConfiguration configuration = await GetConfigurationAsync(cancellationToken)
                .ConfigureAwait(false);
            EndpointDescriptionCollection endpoints = await DiscoverEndpointsAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<OpcUaEndpointDescription> descriptions = endpoints.Select(endpoint =>
                new OpcUaEndpointDescription(
                    endpoint.EndpointUrl,
                    FromSdkSecurityMode(endpoint.SecurityMode),
                    endpoint.SecurityPolicyUri,
                    endpoint.SecurityLevel,
                    endpoint.UserIdentityTokens
                        .Select(token => FromSdkIdentity(token.TokenType))
                        .Distinct()
                        .ToArray())).ToArray();
            return CommunicationResult<IReadOnlyList<OpcUaEndpointDescription>>.Success(descriptions);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommunicationResult<IReadOnlyList<OpcUaEndpointDescription>>.Failure(ToError(
                "OPC UA endpoint discovery failed.", exception));
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_session?.Connected == true)
            {
                return CommunicationResult.Success();
            }

            _state = ConnectionState.Connecting;
            if (_options.Certificates.AllowUntrustedCertificates)
            {
                SecurityWarning?.Invoke(this, new OpcUaSecurityWarningEventArgs(
                    "Untrusted server certificate acceptance is explicitly enabled. Do not use this setting in production."));
            }

            ApplicationConfiguration configuration = await GetConfigurationAsync(cancellationToken)
                .ConfigureAwait(false);
            EndpointDescriptionCollection endpoints = await DiscoverEndpointsAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
            EndpointDescription? selected = endpoints
                .Where(endpoint => endpoint.SecurityMode == ToSdkSecurityMode(_options.SecurityMode) &&
                                   string.Equals(endpoint.SecurityPolicyUri,
                                       ToSecurityPolicyUri(_options.SecurityPolicy),
                                       StringComparison.Ordinal))
                .OrderByDescending(endpoint => endpoint.SecurityLevel)
                .FirstOrDefault();
            if (selected is null)
            {
                _state = ConnectionState.Faulted;
                return CommunicationResult.Failure(new CommunicationError(
                    CommunicationErrorCode.ConnectionFailure,
                    "The server did not expose an endpoint matching the requested OPC UA security mode and policy."));
            }

            var endpointConfiguration = EndpointConfiguration.Create(configuration);
            var configuredEndpoint = new ConfiguredEndpoint(null, selected, endpointConfiguration);
            IUserIdentity identity = CreateIdentity();
            ISession session = await new DefaultSessionFactory(_telemetry).CreateAsync(
                configuration,
                configuredEndpoint,
                false,
                _options.Certificates.ValidateCertificateHost,
                _options.ApplicationName,
                checked((uint)ToMilliseconds(_options.SessionTimeout)),
                identity,
                null,
                cancellationToken).ConfigureAwait(false);
            session.KeepAlive += OnKeepAlive;
            _session = session;
            _state = ConnectionState.Connected;
            return CommunicationResult.Success();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _state = ConnectionState.Faulted;
            return CommunicationResult.Failure(ToError("The OPC UA session could not be opened.", exception));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ISession? session = _session;
            if (session is null)
            {
                _state = ConnectionState.Disconnected;
                return CommunicationResult.Success();
            }

            _state = ConnectionState.Disconnecting;
            _session = null;
            session.KeepAlive -= OnKeepAlive;
            await session.CloseAsync(5000, true, cancellationToken).ConfigureAwait(false);
            session.Dispose();
            _state = ConnectionState.Disconnected;
            return CommunicationResult.Success();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _state = ConnectionState.Faulted;
            return CommunicationResult.Failure(ToError("The OPC UA session could not be closed.", exception));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CommunicationResult<OpcUaNodeValue>>> ReadAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (nodeIds is null)
        {
            throw new ArgumentNullException(nameof(nodeIds));
        }

        ISession? session = _session;
        if (session?.Connected != true)
        {
            return FailureResults<OpcUaNodeValue>(nodeIds.Count, CommunicationErrorCode.InvalidState,
                "The OPC UA session is not connected.");
        }

        try
        {
            var nodes = new ReadValueIdCollection(nodeIds.Select(nodeId => new ReadValueId
            {
                NodeId = NodeId.Parse(nodeId),
                AttributeId = Attributes.Value,
            }));
            ReadResponse response = await session.ReadAsync(
                null, 0, TimestampsToReturn.Both, nodes, cancellationToken).ConfigureAwait(false);
            return nodeIds.Select((nodeId, index) => index < response.Results.Count
                ? ConvertDataValue(nodeId, response.Results[index])
                : CommunicationResult<OpcUaNodeValue>.Failure(new CommunicationError(
                    CommunicationErrorCode.ProtocolError,
                    "The OPC UA server returned fewer read results than requested."))).ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return FailureResults<OpcUaNodeValue>(nodeIds.Count, ToError("The OPC UA read failed.", exception));
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CommunicationResult>> WriteAsync(
        IReadOnlyList<OpcUaNodeWrite> writes,
        CancellationToken cancellationToken = default)
    {
        if (writes is null)
        {
            throw new ArgumentNullException(nameof(writes));
        }

        ISession? session = _session;
        if (session?.Connected != true)
        {
            return FailureResults(writes.Count, CommunicationErrorCode.InvalidState,
                "The OPC UA session is not connected.");
        }

        try
        {
            var values = new WriteValueCollection(writes.Select(write => new WriteValue
            {
                NodeId = NodeId.Parse(write.NodeId),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(write.Value)),
            }));
            WriteResponse response = await session.WriteAsync(null, values, cancellationToken)
                .ConfigureAwait(false);
            return writes.Select((_, index) => index >= response.Results.Count
                ? CommunicationResult.Failure(new CommunicationError(
                    CommunicationErrorCode.ProtocolError,
                    "The OPC UA server returned fewer write results than requested."))
                : StatusCode.IsGood(response.Results[index])
                    ? CommunicationResult.Success()
                    : CommunicationResult.Failure(StatusError(
                        "The OPC UA server rejected a write.", response.Results[index]))).ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return FailureResults(writes.Count, ToError("The OPC UA write failed.", exception));
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<IOpcUaSessionSubscription>> SubscribeAsync(
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

        ISession? session = _session;
        if (session?.Connected != true)
        {
            return CommunicationResult<IOpcUaSessionSubscription>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidState,
                "The OPC UA session is not connected."));
        }

        try
        {
            var subscription = new Subscription(_telemetry, new SubscriptionOptions
            {
                DisplayName = "Industrial.Communication",
                PublishingInterval = ToMilliseconds(publishingInterval),
                KeepAliveCount = 10,
                LifetimeCount = 100,
                PublishingEnabled = true,
                TimestampsToReturn = TimestampsToReturn.Both,
            });
            if (!session.AddSubscription(subscription))
            {
                return CommunicationResult<IOpcUaSessionSubscription>.Failure(new CommunicationError(
                    CommunicationErrorCode.ProtocolError,
                    "The OPC UA SDK rejected the subscription."));
            }

            var wrapper = new OpcFoundationSubscription(subscription, onValue);
            foreach (OpcUaMonitoredNode node in nodes)
            {
                wrapper.Add(node, _telemetry);
            }

            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
            return CommunicationResult<IOpcUaSessionSubscription>.Success(wrapper);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommunicationResult<IOpcUaSessionSubscription>.Failure(ToError(
                "The OPC UA subscription could not be created.", exception));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycleGate.Dispose();
        if (_telemetry is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async ValueTask<ApplicationConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_configuration is not null)
        {
            return _configuration;
        }

        string host = Dns.GetHostName();
        OpcUaCertificateOptions certificates = _options.Certificates;
        var configuration = new ApplicationConfiguration(_telemetry)
        {
            ApplicationName = _options.ApplicationName,
            ApplicationUri = _options.ApplicationUri ?? $"urn:{host}:{_options.ApplicationName}",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = certificates.ApplicationCertificateStorePath,
                    SubjectName = $"CN={_options.ApplicationName}, DC={host}",
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = certificates.TrustedPeerStorePath,
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = certificates.TrustedIssuerStorePath,
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = certificates.RejectedCertificateStorePath,
                },
                AutoAcceptUntrustedCertificates = false,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048,
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 15000,
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = ToMilliseconds(_options.SessionTimeout),
            },
        };
        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken).ConfigureAwait(false);
        if (certificates.AutoCreateApplicationCertificate)
        {
            var application = new ApplicationInstance(configuration, _telemetry);
            bool valid = await application.CheckApplicationInstanceCertificatesAsync(
                false, 2048, cancellationToken).ConfigureAwait(false);
            if (!valid)
            {
                throw new ServiceResultException(StatusCodes.BadCertificateInvalid,
                    "A valid OPC UA application certificate could not be created or loaded.");
            }
        }

        configuration.CertificateValidator.CertificateValidation += OnCertificateValidation;
        _configuration = configuration;
        return configuration;
    }

    private async ValueTask<EndpointDescriptionCollection> DiscoverEndpointsAsync(
        ApplicationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using DiscoveryClient client = await DiscoveryClient.CreateAsync(
            new Uri(_options.EndpointUrl),
            EndpointConfiguration.Create(configuration),
            _telemetry,
            DiagnosticsMasks.None,
            cancellationToken).ConfigureAwait(false);
        return await client.GetEndpointsAsync(null, cancellationToken).ConfigureAwait(false);
    }

    private IUserIdentity CreateIdentity() => _options.Identity.Kind switch
    {
        OpcUaIdentityKind.Anonymous => new UserIdentity(),
        OpcUaIdentityKind.UsernamePassword when !string.IsNullOrWhiteSpace(_options.Identity.Username) =>
            new UserIdentity(_options.Identity.Username!, Encoding.UTF8.GetBytes(_options.Identity.Password ?? string.Empty)),
        OpcUaIdentityKind.Certificate when !string.IsNullOrWhiteSpace(_options.Identity.CertificatePath) =>
            new UserIdentity(new X509Certificate2(
                _options.Identity.CertificatePath!,
                _options.Identity.CertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet)),
        _ => throw new InvalidOperationException("The configured OPC UA user identity is incomplete."),
    };

    private void OnCertificateValidation(CertificateValidator sender, CertificateValidationEventArgs args)
    {
        if (!_options.Certificates.AllowUntrustedCertificates ||
            args.Error.StatusCode != StatusCodes.BadCertificateUntrusted)
        {
            return;
        }

        args.Accept = true;
        string thumbprint = args.Certificate.Thumbprint ?? "unknown";
        SecurityWarning?.Invoke(this, new OpcUaSecurityWarningEventArgs(
            $"Explicitly accepted an untrusted OPC UA server certificate (thumbprint: {thumbprint})."));
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs args)
    {
        if (ServiceResult.IsGood(args.Status) || Interlocked.CompareExchange(ref _reconnectStarted, 1, 0) != 0)
        {
            return;
        }

        _state = ConnectionState.Reconnecting;
        _ = ReconnectAsync(session);
    }

    private async Task ReconnectAsync(ISession session)
    {
        while (!_disposed && ReferenceEquals(_session, session) &&
               _state is not ConnectionState.Disconnecting and not ConnectionState.Disconnected)
        {
            try
            {
                await Task.Delay(_options.ReconnectDelay).ConfigureAwait(false);
                await session.ReconnectAsync(CancellationToken.None).ConfigureAwait(false);
                _state = ConnectionState.Connected;
                Reconnected?.Invoke(this, EventArgs.Empty);
                Interlocked.Exchange(ref _reconnectStarted, 0);
                return;
            }
            catch (Exception) when (!_disposed)
            {
                _state = ConnectionState.Reconnecting;
            }
        }

        Interlocked.Exchange(ref _reconnectStarted, 0);
    }

    private static CommunicationResult<OpcUaNodeValue> ConvertDataValue(string nodeId, DataValue value)
    {
        if (StatusCode.IsBad(value.StatusCode))
        {
            return CommunicationResult<OpcUaNodeValue>.Failure(StatusError(
                $"The OPC UA server could not read node '{nodeId}'.", value.StatusCode));
        }

        DateTime source = value.SourceTimestamp == DateTime.MinValue
            ? DateTime.UtcNow
            : value.SourceTimestamp;
        DateTime server = value.ServerTimestamp == DateTime.MinValue ? source : value.ServerTimestamp;
        VariableQuality quality = StatusCode.IsGood(value.StatusCode)
            ? VariableQuality.Good
            : VariableQuality.Uncertain;
        return CommunicationResult<OpcUaNodeValue>.Success(new OpcUaNodeValue(
            nodeId,
            value.WrappedValue.Value,
            quality,
            new DateTimeOffset(DateTime.SpecifyKind(source, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(server, DateTimeKind.Utc))));
    }

    private static CommunicationError StatusError(string message, StatusCode statusCode) =>
        new(CommunicationErrorCode.DeviceError, message, statusCode.ToString());

    private static CommunicationError ToError(string message, Exception exception) => exception switch
    {
        OperationCanceledException => new CommunicationError(
            CommunicationErrorCode.Canceled, message, exception.Message, exception),
        TimeoutException => new CommunicationError(
            CommunicationErrorCode.Timeout, message, exception.Message, exception),
        ServiceResultException service => new CommunicationError(
            CommunicationErrorCode.ProtocolError, message, service.StatusCode.ToString(), service),
        _ => new CommunicationError(
            CommunicationErrorCode.ConnectionFailure, message, exception.Message, exception),
    };

    private static IReadOnlyList<CommunicationResult<T>> FailureResults<T>(
        int count,
        CommunicationErrorCode code,
        string message) => FailureResults<T>(count, new CommunicationError(code, message));

    private static IReadOnlyList<CommunicationResult<T>> FailureResults<T>(int count, CommunicationError error) =>
        Enumerable.Range(0, count).Select(_ => CommunicationResult<T>.Failure(error)).ToArray();

    private static IReadOnlyList<CommunicationResult> FailureResults(
        int count,
        CommunicationErrorCode code,
        string message) => FailureResults(count, new CommunicationError(code, message));

    private static IReadOnlyList<CommunicationResult> FailureResults(int count, CommunicationError error) =>
        Enumerable.Range(0, count).Select(_ => CommunicationResult.Failure(error)).ToArray();

    private static int ToMilliseconds(TimeSpan value) => checked((int)Math.Clamp(
        value.TotalMilliseconds, 1, int.MaxValue));

    private static string ToSecurityPolicyUri(OpcUaSecurityPolicy policy) => policy switch
    {
        OpcUaSecurityPolicy.None => SecurityPolicies.None,
        OpcUaSecurityPolicy.Basic256Sha256 => SecurityPolicies.Basic256Sha256,
        OpcUaSecurityPolicy.Aes128Sha256RsaOaep => SecurityPolicies.Aes128_Sha256_RsaOaep,
        OpcUaSecurityPolicy.Aes256Sha256RsaPss => SecurityPolicies.Aes256_Sha256_RsaPss,
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    private static UaMessageSecurityMode ToSdkSecurityMode(OpcUaMessageSecurityMode mode) => mode switch
    {
        OpcUaMessageSecurityMode.None => UaMessageSecurityMode.None,
        OpcUaMessageSecurityMode.Sign => UaMessageSecurityMode.Sign,
        OpcUaMessageSecurityMode.SignAndEncrypt => UaMessageSecurityMode.SignAndEncrypt,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static OpcUaMessageSecurityMode FromSdkSecurityMode(UaMessageSecurityMode mode) => mode switch
    {
        UaMessageSecurityMode.None => OpcUaMessageSecurityMode.None,
        UaMessageSecurityMode.Sign => OpcUaMessageSecurityMode.Sign,
        UaMessageSecurityMode.SignAndEncrypt => OpcUaMessageSecurityMode.SignAndEncrypt,
        _ => OpcUaMessageSecurityMode.None,
    };

    private static OpcUaIdentityKind FromSdkIdentity(UserTokenType tokenType) => tokenType switch
    {
        UserTokenType.UserName => OpcUaIdentityKind.UsernamePassword,
        UserTokenType.Certificate => OpcUaIdentityKind.Certificate,
        _ => OpcUaIdentityKind.Anonymous,
    };

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OpcFoundationSession));
        }
    }

    private sealed class OpcFoundationSubscription : IOpcUaSessionSubscription
    {
        private readonly Subscription _subscription;
        private readonly Func<OpcUaNodeValue, ValueTask> _onValue;
        private readonly List<MonitoredItem> _items = new();
        private int _disposed;

        public OpcFoundationSubscription(
            Subscription subscription,
            Func<OpcUaNodeValue, ValueTask> onValue)
        {
            _subscription = subscription;
            _onValue = onValue;
        }

        public void Add(OpcUaMonitoredNode node, ITelemetryContext telemetry)
        {
            var item = new MonitoredItem(telemetry, new MonitoredItemOptions
            {
                DisplayName = node.NodeId,
                StartNodeId = NodeId.Parse(node.NodeId),
                AttributeId = Attributes.Value,
                SamplingInterval = checked((int)Math.Clamp(
                    node.SamplingIntervalMilliseconds, 0, int.MaxValue)),
                QueueSize = node.QueueSize,
                DiscardOldest = true,
                MonitoringMode = MonitoringMode.Reporting,
            });
            item.Notification += OnNotification;
            _items.Add(item);
            _subscription.AddItem(item);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (MonitoredItem item in _items)
            {
                item.Notification -= OnNotification;
            }

            if (_subscription.Created)
            {
                await _subscription.DeleteAsync(true, CancellationToken.None).ConfigureAwait(false);
            }

            _subscription.Dispose();
        }

        private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs args)
        {
            foreach (DataValue value in item.DequeueValues())
            {
                CommunicationResult<OpcUaNodeValue> converted = ConvertDataValue(
                    item.StartNodeId.ToString(), value);
                if (converted.IsSuccess)
                {
                    _ = _onValue(converted.Value!);
                }
                else
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    _ = _onValue(new OpcUaNodeValue(
                        item.StartNodeId.ToString(), null, VariableQuality.Bad, now, now, converted.Error));
                }
            }
        }
    }
}
