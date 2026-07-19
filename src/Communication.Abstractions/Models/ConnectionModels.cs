namespace Communication.Abstractions.Models;

/// <summary>Provides data for a connection state transition.</summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>Initializes a state transition notification.</summary>
    public ConnectionStateChangedEventArgs(
        ConnectionState previousState,
        ConnectionState currentState,
        DateTimeOffset timestamp,
        CommunicationError? error = null)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Timestamp = timestamp;
        Error = error;
    }

    /// <summary>Gets the state before the transition.</summary>
    public ConnectionState PreviousState { get; }

    /// <summary>Gets the state after the transition.</summary>
    public ConnectionState CurrentState { get; }

    /// <summary>Gets the UTC timestamp of the transition.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Gets an optional failure that caused the transition.</summary>
    public CommunicationError? Error { get; }
}

/// <summary>Describes one active server-side client session.</summary>
/// <param name="SessionId">The stable session identifier.</param>
/// <param name="ConnectedAt">The time at which the session was accepted.</param>
/// <param name="RemoteEndpoint">A display-only remote endpoint without transport-specific types.</param>
public sealed record CommunicationSession(
    string SessionId,
    DateTimeOffset ConnectedAt,
    string? RemoteEndpoint = null);

/// <summary>Associates an inbound server message with its client session.</summary>
/// <param name="Session">The source session.</param>
/// <param name="Message">The received message.</param>
public sealed record ServerRequestContext(CommunicationSession Session, MessageEnvelope Message);
