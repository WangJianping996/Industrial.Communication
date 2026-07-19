using Communication.Abstractions.Models;

namespace Communication.Abstractions;

/// <summary>
/// Provides a thread-safe connection lifecycle state machine shared by transports and clients.
/// </summary>
public sealed class ConnectionStateMachine
{
    private readonly object _syncRoot = new();
    private int _state = (int)ConnectionState.Disconnected;

    /// <summary>Raised after a valid state transition has committed.</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Gets the current state.</summary>
    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    /// <summary>Returns whether the current state can transition to <paramref name="nextState"/>.</summary>
    public bool CanTransitionTo(ConnectionState nextState) => IsAllowed(State, nextState);

    /// <summary>Attempts a state transition and returns whether it succeeded.</summary>
    public bool TryTransition(ConnectionState nextState, CommunicationError? error = null)
    {
        ConnectionState previousState;

        lock (_syncRoot)
        {
            previousState = (ConnectionState)_state;
            if (!IsAllowed(previousState, nextState))
            {
                return false;
            }

            Volatile.Write(ref _state, (int)nextState);
        }

        StateChanged?.Invoke(
            this,
            new ConnectionStateChangedEventArgs(previousState, nextState, DateTimeOffset.UtcNow, error));
        return true;
    }

    /// <summary>Performs a valid state transition or throws when the transition is not allowed.</summary>
    public void TransitionTo(ConnectionState nextState, CommunicationError? error = null)
    {
        if (!TryTransition(nextState, error))
        {
            throw new InvalidOperationException($"Transition from {State} to {nextState} is not allowed.");
        }
    }

    private static bool IsAllowed(ConnectionState currentState, ConnectionState nextState) =>
        currentState switch
        {
            ConnectionState.Disconnected => nextState is ConnectionState.Connecting,
            ConnectionState.Connecting => nextState is
                ConnectionState.Connected or
                ConnectionState.Disconnecting or
                ConnectionState.Disconnected or
                ConnectionState.Faulted,
            ConnectionState.Connected => nextState is
                ConnectionState.Reconnecting or
                ConnectionState.Disconnecting or
                ConnectionState.Faulted,
            ConnectionState.Reconnecting => nextState is
                ConnectionState.Connected or
                ConnectionState.Disconnecting or
                ConnectionState.Disconnected or
                ConnectionState.Faulted,
            ConnectionState.Disconnecting => nextState is
                ConnectionState.Disconnected or
                ConnectionState.Faulted,
            ConnectionState.Faulted => nextState is
                ConnectionState.Connecting or
                ConnectionState.Reconnecting or
                ConnectionState.Disconnecting or
                ConnectionState.Disconnected,
            _ => false,
        };
}
