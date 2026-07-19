namespace Communication.Abstractions.Models;

/// <summary>Describes one raw simulator request for reusable response scripts.</summary>
/// <param name="Protocol">The protocol name.</param>
/// <param name="ChannelId">The simulator channel identifier.</param>
/// <param name="SessionId">An optional server session identifier.</param>
/// <param name="Sequence">The one-based request sequence number.</param>
/// <param name="Payload">A stable copy of the raw request.</param>
public sealed record SimulationRequest(
    string Protocol,
    string ChannelId,
    string? SessionId,
    long Sequence,
    ReadOnlyMemory<byte> Payload);

/// <summary>Controls one simulated response without depending on a specific protocol.</summary>
public sealed record SimulationResponseDirective
{
    /// <summary>Gets an optional complete raw response replacement.</summary>
    public ReadOnlyMemory<byte>? ResponseOverride { get; init; }

    /// <summary>Gets the additional delay before responding.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Gets whether the response should be silently dropped.</summary>
    public bool DropResponse { get; init; }

    /// <summary>Gets whether the simulated connection should be interrupted.</summary>
    public bool Disconnect { get; init; }

    /// <summary>Gets whether the protocol integrity field should be corrupted.</summary>
    public bool CorruptResponse { get; init; }
}
