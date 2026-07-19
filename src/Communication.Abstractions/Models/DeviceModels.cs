namespace Communication.Abstractions.Models;

/// <summary>Represents the current health of a device adapter.</summary>
/// <param name="Status">The high-level health status.</param>
/// <param name="Timestamp">The time at which health was evaluated.</param>
/// <param name="Description">An optional safe description.</param>
/// <param name="Error">An optional structured failure.</param>
public sealed record DeviceHealth(
    DeviceHealthStatus Status,
    DateTimeOffset Timestamp,
    string? Description = null,
    CommunicationError? Error = null);

/// <summary>Configures a protocol-independent communication simulator.</summary>
public sealed record CommunicationSimulatorOptions
{
    /// <summary>Gets the default response delay.</summary>
    public TimeSpan ResponseDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Gets an optional deterministic random seed.</summary>
    public int? RandomSeed { get; init; }
}

/// <summary>Describes one fault to inject into a simulator.</summary>
/// <param name="Kind">The fault kind.</param>
/// <param name="Count">The number of times to apply it.</param>
/// <param name="Delay">An optional delay used by delay faults.</param>
/// <param name="Detail">Optional device error detail.</param>
public sealed record SimulatorFault(
    SimulatorFaultKind Kind,
    int Count = 1,
    TimeSpan? Delay = null,
    string? Detail = null);

/// <summary>Contains a stable digital I/O snapshot.</summary>
public sealed record DigitalIoSnapshot(
    IReadOnlyList<bool> Inputs,
    IReadOnlyList<bool> Outputs,
    DateTimeOffset Timestamp);

/// <summary>Describes one input edge.</summary>
public sealed class DigitalEdgeChangedEventArgs : EventArgs
{
    /// <summary>Initializes an edge event.</summary>
    public DigitalEdgeChangedEventArgs(
        int index,
        bool previousValue,
        bool currentValue,
        DateTimeOffset timestamp)
    {
        Index = index;
        PreviousValue = previousValue;
        CurrentValue = currentValue;
        Timestamp = timestamp;
    }

    /// <summary>Gets the zero-based input index.</summary>
    public int Index { get; }

    /// <summary>Gets the previous state.</summary>
    public bool PreviousValue { get; }

    /// <summary>Gets the current state.</summary>
    public bool CurrentValue { get; }

    /// <summary>Gets the observation timestamp.</summary>
    public DateTimeOffset Timestamp { get; }
}

/// <summary>Describes the current state of one motion axis.</summary>
public sealed record AxisState(
    int Axis,
    bool Enabled,
    bool Homed,
    bool Moving,
    bool Alarmed,
    double Position,
    DateTimeOffset Timestamp,
    string? Alarm = null);

/// <summary>Configures speed, acceleration and deceleration for an explicit movement.</summary>
public sealed record MotionProfile(double Velocity, double Acceleration, double Deceleration);

/// <summary>Controls how an explicit axis stop is performed.</summary>
public enum MotionStopMode
{
    /// <summary>Stops using the configured deceleration.</summary>
    Decelerated,
    /// <summary>Requests an immediate emergency stop.</summary>
    Emergency,
}

/// <summary>Contains one accepted barcode.</summary>
public sealed class BarcodeScannedEventArgs : EventArgs
{
    /// <summary>Initializes a barcode event.</summary>
    public BarcodeScannedEventArgs(string value, DateTimeOffset timestamp, ReadOnlyMemory<byte> rawFrame)
    {
        Value = value;
        Timestamp = timestamp;
        RawFrame = rawFrame;
    }

    /// <summary>Gets the decoded barcode.</summary>
    public string Value { get; }

    /// <summary>Gets the observation timestamp.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Gets a stable copy of the complete frame.</summary>
    public ReadOnlyMemory<byte> RawFrame { get; }
}

/// <summary>Identifies the unit of a weight reading.</summary>
public enum WeightUnit
{
    /// <summary>Grams.</summary>
    Gram,
    /// <summary>Kilograms.</summary>
    Kilogram,
    /// <summary>Pounds.</summary>
    Pound,
    /// <summary>Tonnes.</summary>
    Tonne,
    /// <summary>A device-specific or unknown unit.</summary>
    Unknown,
}

/// <summary>Contains one parsed weight sample.</summary>
public sealed record WeightReading(
    double Gross,
    double Net,
    WeightUnit Unit,
    bool IsStable,
    DateTimeOffset Timestamp);

/// <summary>Wraps a continuous weight sample event.</summary>
public sealed class WeightReportedEventArgs : EventArgs
{
    /// <summary>Initializes a weight event.</summary>
    public WeightReportedEventArgs(WeightReading reading)
    {
        Reading = reading;
    }

    /// <summary>Gets the parsed reading.</summary>
    public WeightReading Reading { get; }
}
