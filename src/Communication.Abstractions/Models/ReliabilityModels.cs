namespace Communication.Abstractions.Models;

/// <summary>Provides input to a retry policy.</summary>
/// <param name="OperationName">The logical operation name.</param>
/// <param name="Attempt">The one-based failed attempt number.</param>
/// <param name="Elapsed">Time elapsed since the first attempt.</param>
/// <param name="Error">The most recent error.</param>
public sealed record RetryContext(
    string OperationName,
    int Attempt,
    TimeSpan Elapsed,
    CommunicationError Error);

/// <summary>Describes whether and when another attempt should occur.</summary>
/// <param name="ShouldRetry">Whether another attempt is permitted.</param>
/// <param name="Delay">The delay before that attempt.</param>
public readonly record struct RetryDecision(bool ShouldRetry, TimeSpan Delay)
{
    /// <summary>Gets a decision that stops retrying.</summary>
    public static RetryDecision Stop => new(false, TimeSpan.Zero);
}

/// <summary>Provides input to an automatic reconnection policy.</summary>
/// <param name="Attempt">The one-based reconnection attempt number.</param>
/// <param name="Elapsed">Time elapsed since the connection was lost.</param>
/// <param name="Error">The error that caused the disconnect.</param>
/// <param name="WasUserInitiated">Whether the user explicitly requested disconnection.</param>
public sealed record ReconnectContext(
    int Attempt,
    TimeSpan Elapsed,
    CommunicationError Error,
    bool WasUserInitiated);

/// <summary>Controls one client operation.</summary>
public sealed record CommunicationRequestOptions
{
    /// <summary>Gets the operation timeout. A null value uses the client default.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets whether the configured retry policy may be used.</summary>
    public bool EnableRetry { get; init; } = true;
}
