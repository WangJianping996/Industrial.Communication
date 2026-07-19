using Communication.Abstractions;
using Communication.Abstractions.Models;

namespace Communication.UnitTests;

public sealed class ConnectionStateMachineTests
{
    [Fact]
    public void New_machine_is_disconnected()
    {
        ConnectionStateMachine machine = new();

        Assert.Equal(ConnectionState.Disconnected, machine.State);
    }

    [Fact]
    public void Valid_lifecycle_commits_each_transition_and_notifies_after_commit()
    {
        ConnectionStateMachine machine = new();
        List<(ConnectionState Previous, ConnectionState Current)> transitions = [];

        machine.StateChanged += (_, args) =>
        {
            Assert.Equal(args.CurrentState, machine.State);
            transitions.Add((args.PreviousState, args.CurrentState));
        };

        machine.TransitionTo(ConnectionState.Connecting);
        machine.TransitionTo(ConnectionState.Connected);
        machine.TransitionTo(ConnectionState.Disconnecting);
        machine.TransitionTo(ConnectionState.Disconnected);

        Assert.Equal(
            [
                (ConnectionState.Disconnected, ConnectionState.Connecting),
                (ConnectionState.Connecting, ConnectionState.Connected),
                (ConnectionState.Connected, ConnectionState.Disconnecting),
                (ConnectionState.Disconnecting, ConnectionState.Disconnected),
            ],
            transitions);
    }

    [Theory]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Reconnecting)]
    [InlineData(ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Faulted)]
    public void Disconnected_machine_rejects_skipped_states(ConnectionState invalidState)
    {
        ConnectionStateMachine machine = new();

        Assert.False(machine.TryTransition(invalidState));
        Assert.Equal(ConnectionState.Disconnected, machine.State);
    }

    [Fact]
    public void TransitionTo_throws_for_an_invalid_transition()
    {
        ConnectionStateMachine machine = new();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => machine.TransitionTo(ConnectionState.Connected));

        Assert.Contains("Disconnected", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Disconnected, machine.State);
    }

    [Fact]
    public void Fault_transition_preserves_structured_error_in_event()
    {
        ConnectionStateMachine machine = new();
        machine.TransitionTo(ConnectionState.Connecting);
        CommunicationError expected = new(
            CommunicationErrorCode.ConnectionFailure,
            "Connection failed.");
        ConnectionStateChangedEventArgs? observed = null;
        machine.StateChanged += (_, args) => observed = args;

        machine.TransitionTo(ConnectionState.Faulted, expected);

        Assert.NotNull(observed);
        Assert.Same(expected, observed.Error);
    }
}
