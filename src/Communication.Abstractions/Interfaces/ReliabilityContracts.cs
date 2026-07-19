using Communication.Abstractions.Models;

namespace Communication.Abstractions.Interfaces;

/// <summary>Determines whether a failed operation may be attempted again.</summary>
public interface IRetryPolicy
{
    /// <summary>Gets the decision for the next attempt.</summary>
    ValueTask<RetryDecision> GetDecisionAsync(
        RetryContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Determines whether and when an interrupted connection should be restored.</summary>
public interface IReconnectPolicy
{
    /// <summary>Gets the decision for the next reconnection attempt.</summary>
    ValueTask<RetryDecision> GetDecisionAsync(
        ReconnectContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates and validates protocol-specific heartbeat messages.</summary>
public interface IHeartbeatStrategy
{
    /// <summary>Gets the interval between heartbeat attempts.</summary>
    TimeSpan Interval { get; }

    /// <summary>Creates the next heartbeat request payload.</summary>
    ValueTask<ReadOnlyMemory<byte>> CreateRequestAsync(CancellationToken cancellationToken = default);

    /// <summary>Determines whether a received payload acknowledges a heartbeat.</summary>
    ValueTask<bool> IsResponseValidAsync(
        ReadOnlyMemory<byte> response,
        CancellationToken cancellationToken = default);
}
