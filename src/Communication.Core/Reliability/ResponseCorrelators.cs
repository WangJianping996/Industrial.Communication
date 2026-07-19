using Communication.Abstractions.Interfaces;

namespace Communication.Core.Reliability;

/// <summary>Serializes requests for protocols that do not carry transaction identifiers.</summary>
/// <typeparam name="TRequest">The request model.</typeparam>
/// <typeparam name="TResponse">The response model.</typeparam>
public sealed class SingleRequestCorrelator<TRequest, TResponse> : IResponseCorrelator<TRequest, TResponse>
{
    private const string CorrelationKey = "single";

    /// <inheritdoc />
    public int MaxInFlight => 1;

    /// <inheritdoc />
    public string GetRequestKey(TRequest request) => CorrelationKey;

    /// <inheritdoc />
    public string GetResponseKey(TResponse response) => CorrelationKey;
}

/// <summary>Uses caller-supplied transaction ID selectors for concurrent request correlation.</summary>
/// <typeparam name="TRequest">The request model.</typeparam>
/// <typeparam name="TResponse">The response model.</typeparam>
public sealed class DelegatingResponseCorrelator<TRequest, TResponse> : IResponseCorrelator<TRequest, TResponse>
{
    private readonly Func<TRequest, string> _requestKeySelector;
    private readonly Func<TResponse, string> _responseKeySelector;

    /// <summary>Initializes a transaction-aware correlator.</summary>
    public DelegatingResponseCorrelator(
        Func<TRequest, string> requestKeySelector,
        Func<TResponse, string> responseKeySelector,
        int maxInFlight = 256)
    {
        if (maxInFlight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInFlight));
        }

        _requestKeySelector = requestKeySelector ?? throw new ArgumentNullException(nameof(requestKeySelector));
        _responseKeySelector = responseKeySelector ?? throw new ArgumentNullException(nameof(responseKeySelector));
        MaxInFlight = maxInFlight;
    }

    /// <inheritdoc />
    public int MaxInFlight { get; }

    /// <inheritdoc />
    public string GetRequestKey(TRequest request) => ValidateKey(_requestKeySelector(request));

    /// <inheritdoc />
    public string GetResponseKey(TResponse response) => ValidateKey(_responseKeySelector(response));

    private static string ValidateKey(string key) =>
        string.IsNullOrWhiteSpace(key)
            ? throw new InvalidOperationException("A response correlation key cannot be empty.")
            : key;
}

/// <summary>Adapts a delegate to a reconnection recovery hook.</summary>
public sealed class DelegatingConnectionRecoveryHandler : IConnectionRecoveryHandler
{
    private readonly Func<CancellationToken, ValueTask> _handler;

    /// <summary>Initializes a recovery handler.</summary>
    public DelegatingConnectionRecoveryHandler(Func<CancellationToken, ValueTask> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <inheritdoc />
    public ValueTask OnReconnectedAsync(CancellationToken cancellationToken = default) =>
        _handler(cancellationToken);
}
