namespace Communication.Abstractions.Models;

/// <summary>Represents a message observed on a communication channel.</summary>
/// <param name="Id">A unique message identifier.</param>
/// <param name="Timestamp">The observation timestamp.</param>
/// <param name="Direction">The message direction.</param>
/// <param name="ChannelId">The logical channel identifier.</param>
/// <param name="Payload">The raw or redacted bytes.</param>
/// <param name="SessionId">An optional server session identifier.</param>
/// <param name="Protocol">An optional protocol name.</param>
/// <param name="Summary">An optional safe parsed summary.</param>
/// <param name="Duration">An optional request duration.</param>
/// <param name="IsRedacted">Whether any message content was masked.</param>
/// <param name="Metadata">Optional parsed fields such as address or function code.</param>
public sealed record MessageEnvelope(
    Guid Id,
    DateTimeOffset Timestamp,
    MessageDirection Direction,
    string ChannelId,
    ReadOnlyMemory<byte> Payload,
    string? SessionId = null,
    string? Protocol = null,
    string? Summary = null,
    TimeSpan? Duration = null,
    bool IsRedacted = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Filters monitored or stored messages.</summary>
public sealed record MessageFilter
{
    /// <summary>Gets an optional inclusive start time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Gets an optional exclusive end time.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Gets an optional message direction.</summary>
    public MessageDirection? Direction { get; init; }

    /// <summary>Gets an optional channel identifier.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Gets an optional session identifier.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets an optional protocol name.</summary>
    public string? Protocol { get; init; }
}

/// <summary>Controls message replay timing and filtering.</summary>
public sealed record MessageReplayOptions
{
    /// <summary>Gets the replay timing mode.</summary>
    public ReplayTimingMode TimingMode { get; init; } = ReplayTimingMode.OriginalIntervals;

    /// <summary>Gets the speed multiplier for original intervals.</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>Gets the interval used by fixed-interval replay.</summary>
    public TimeSpan FixedInterval { get; init; } = TimeSpan.Zero;

    /// <summary>Gets an optional direction to replay.</summary>
    public MessageDirection? Direction { get; init; }
}

/// <summary>Describes the format of a message export.</summary>
public sealed record MessageExportOptions(string Format, bool IncludePayload = false);

/// <summary>Describes the outcome of a queue write.</summary>
/// <param name="Accepted">Whether the supplied item was accepted.</param>
/// <param name="Dropped">Whether an item was discarded.</param>
public readonly record struct QueueWriteResult(bool Accepted, bool Dropped);
