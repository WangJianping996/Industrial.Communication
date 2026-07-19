namespace Communication.Abstractions.Models;

/// <summary>Defines one protocol-independent PLC variable.</summary>
/// <param name="Name">The user-facing variable name.</param>
/// <param name="Address">The protocol address.</param>
/// <param name="DataType">The expected value type.</param>
/// <param name="Length">The element or string length.</param>
/// <param name="Access">Allowed operations.</param>
/// <param name="Scale">A multiplier applied to raw numeric values.</param>
/// <param name="Description">An optional description.</param>
/// <param name="ByteOrder">The byte/word ordering transform.</param>
/// <param name="StringEncoding">The encoding used for string variables.</param>
public sealed record VariableDefinition(
    string Name,
    string Address,
    PlcDataType DataType,
    int Length = 1,
    VariableAccess Access = VariableAccess.ReadWrite,
    double Scale = 1.0,
    string? Description = null,
    PlcByteOrder ByteOrder = PlcByteOrder.BigEndian,
    PlcStringEncoding StringEncoding = PlcStringEncoding.Ascii);

/// <summary>Represents one sampled PLC value and its quality.</summary>
/// <param name="Definition">The sampled variable.</param>
/// <param name="Value">The converted value, if available.</param>
/// <param name="Quality">The sample quality.</param>
/// <param name="Timestamp">The source or observation timestamp.</param>
/// <param name="Error">An optional per-variable error.</param>
public sealed record VariableValue(
    VariableDefinition Definition,
    object? Value,
    VariableQuality Quality,
    DateTimeOffset Timestamp,
    CommunicationError? Error = null);

/// <summary>Contains a parsed, protocol-specific address without exposing vendor SDK types.</summary>
/// <param name="Area">The normalized memory or device area.</param>
/// <param name="Offset">The zero-based byte, word or element offset.</param>
/// <param name="Bit">An optional bit index.</param>
/// <param name="Original">The original address text.</param>
public sealed record PlcAddress(string Area, int Offset, int? Bit, string Original);

/// <summary>Associates a variable definition with a value to write.</summary>
public sealed record PlcWriteRequest(VariableDefinition Definition, object? Value);

/// <summary>Configures periodic variable observation.</summary>
public sealed record VariableMonitorOptions
{
    /// <summary>Gets the interval between polling cycles.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets whether unchanged good values should also be emitted.</summary>
    public bool PublishUnchangedValues { get; init; }

    /// <summary>Gets the bounded number of pending observations.</summary>
    public int QueueCapacity { get; init; } = 1024;
}
