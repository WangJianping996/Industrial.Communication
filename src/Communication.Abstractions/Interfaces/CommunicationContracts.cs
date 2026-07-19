using Communication.Abstractions.Models;

namespace Communication.Abstractions.Interfaces;

/// <summary>Coordinates a transport and protocol codec for request/response communication.</summary>
/// <typeparam name="TRequest">The request model.</typeparam>
/// <typeparam name="TResponse">The response model.</typeparam>
public interface ICommunicationClient<in TRequest, TResponse> : IAsyncDisposable
{
    /// <summary>Gets the current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Raised after the connection state changes.</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Connects the underlying channel.</summary>
    ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnects the underlying channel.</summary>
    ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a request and awaits its correlated response.</summary>
    ValueTask<CommunicationResult<TResponse>> ExecuteAsync(
        TRequest request,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Defines an asynchronous multi-session communication server.</summary>
public interface ICommunicationServer : IAsyncDisposable
{
    /// <summary>Gets the current server state.</summary>
    ConnectionState State { get; }

    /// <summary>Gets a snapshot of active sessions.</summary>
    IReadOnlyCollection<CommunicationSession> Sessions { get; }

    /// <summary>Raised after the server state changes.</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Starts listening for clients.</summary>
    ValueTask<CommunicationResult> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops listening and closes active sessions gracefully.</summary>
    ValueTask<CommunicationResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Streams requests received from all active sessions.</summary>
    IAsyncEnumerable<ServerRequestContext> ReadRequestsAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a response to one active session.</summary>
    ValueTask<CommunicationResult<int>> SendAsync(
        string sessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
}

/// <summary>Maps requests and responses to stable correlation keys.</summary>
/// <typeparam name="TRequest">The request model.</typeparam>
/// <typeparam name="TResponse">The response model.</typeparam>
public interface IResponseCorrelator<in TRequest, in TResponse>
{
    /// <summary>Gets the maximum number of requests that may await responses concurrently.</summary>
    int MaxInFlight { get; }

    /// <summary>Gets the correlation key assigned to a request.</summary>
    string GetRequestKey(TRequest request);

    /// <summary>Gets the correlation key carried by a response.</summary>
    string GetResponseKey(TResponse response);
}

/// <summary>Restores subscriptions, monitors, or other state after automatic reconnection.</summary>
public interface IConnectionRecoveryHandler
{
    /// <summary>Runs after a connection has been restored.</summary>
    ValueTask OnReconnectedAsync(CancellationToken cancellationToken = default);
}
